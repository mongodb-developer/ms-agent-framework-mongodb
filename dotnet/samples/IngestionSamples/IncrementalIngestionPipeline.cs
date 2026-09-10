namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// A sample-only, deterministic incremental ingestion pipeline over a flat chunk schema (docs/spec/samples.md's
/// <c>IncrementalIngestion</c> sample). One <see cref="SourceDocument"/> is chunked, hashed, diffed against what is
/// already stored for its tenant+source scope, and reconciled: unchanged chunks are skipped, new/changed chunks are
/// embedded and upserted, and chunks no longer produced by the current content are deleted -- but only within the
/// same tenant+source scope, never a bare ID. Cancellation is propagated through every read, embed, write, and
/// cleanup step.
/// </summary>
public sealed class IncrementalIngestionPipeline
{
    private readonly IChunkStore _store;
    private readonly BatchEmbedder _embedder;
    private readonly ChunkingOptions _chunkingOptions;

    /// <summary>Initializes a pipeline over an injected, caller-owned store and embedder.</summary>
    public IncrementalIngestionPipeline(
        IChunkStore store,
        BatchEmbedder embedder,
        ChunkingOptions? chunkingOptions = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _chunkingOptions = chunkingOptions ?? new ChunkingOptions();
    }

    /// <summary>
    /// Ingests one source document: chunks its content, embeds only new/changed chunks, upserts them, and deletes
    /// any previously stored chunk for this tenant+source that the current content no longer produces.
    /// </summary>
    public async Task<IngestionResult> IngestAsync(
        SourceDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<string> chunkTexts = DocumentChunker.Chunk(document.Content, _chunkingOptions);
        var desired = new List<ChunkCandidate>(chunkTexts.Count);
        for (int index = 0; index < chunkTexts.Count; index++)
        {
            string id = DeterministicId.ForChunk(document.TenantId, document.SourceId, index);
            // The tracked hash covers every persisted, per-record field that matters -- chunk text as well as the
            // Title/Url attribution stamped onto every ChunkRecord (see below) -- not just the chunk text, so a
            // title-only or URL-only edit (no chunk text change) is still detected as a change on the next run and
            // the stale attribution metadata gets corrected rather than left stale forever. ContentHash.ComputeFramed
            // uses canonical length-prefixed framing (CanonicalFraming), not delimiter-joined concatenation, so a
            // Title/Url/text boundary shift can never be silently mistaken for unchanged content.
            string hash = ContentHash.ComputeFramed(chunkTexts[index], document.Title, document.Url);
            desired.Add(new ChunkCandidate(id, ParentId: null, ChunkRecord.FlatChunkRecordType, chunkTexts[index], hash, NeedsEmbedding: true));
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyDictionary<string, string> existing = await _store
            .GetExistingHashesAsync(document.TenantId, document.SourceId, cancellationToken)
            .ConfigureAwait(false);

        (IReadOnlyList<ChunkCandidate> toWrite, int unchanged, IReadOnlyList<string> staleIds) =
            IngestionDiffing.Diff(desired, existing);

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ReadOnlyMemory<float>> embeddings = toWrite.Count == 0
            ? []
            : await _embedder
                .EmbedAsync([.. toWrite.Select(static candidate => candidate.Text)], cancellationToken)
                .ConfigureAwait(false);

        var records = new List<ChunkRecord>(toWrite.Count);
        for (int index = 0; index < toWrite.Count; index++)
        {
            ChunkCandidate candidate = toWrite[index];
            records.Add(new ChunkRecord(
                candidate.Id,
                document.TenantId,
                document.SourceId,
                candidate.ParentId,
                candidate.RecordType,
                candidate.Text,
                candidate.Hash,
                embeddings[index],
                document.Title,
                document.Url));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (records.Count > 0)
        {
            await _store.UpsertAsync(records, cancellationToken).ConfigureAwait(false);
        }

        int deleted = 0;
        if (staleIds.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            deleted = await _store
                .DeleteAsync(document.TenantId, document.SourceId, staleIds, cancellationToken)
                .ConfigureAwait(false);
        }

        return new IngestionResult(document.SourceId, unchanged, records.Count, deleted);
    }
}
