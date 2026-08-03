namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// A sample-only ingestion pipeline for the parent-document RAG schema pattern (docs/spec/features/rag.md's
/// "Parent-document retrieval" section): one unembedded parent record holding the full source text plus one
/// embedded child record per chunk, linked by <see cref="ChunkRecord.ParentId"/>. Only child records are embedded
/// and ever included in the Vector Search path; the parent record's own content hash is still tracked so parent
/// title/content edits are detected even when no child chunk changes. Shares the same incremental
/// unchanged/changed/stale semantics and tenant+source-scoped cleanup as <see cref="IncrementalIngestionPipeline"/>.
/// </summary>
public sealed class ParentDocumentIngestionPipeline
{
    private readonly IChunkStore _store;
    private readonly BatchEmbedder _embedder;
    private readonly ChunkingOptions _chunkingOptions;

    /// <summary>Initializes a pipeline over an injected, caller-owned store and embedder.</summary>
    public ParentDocumentIngestionPipeline(
        IChunkStore store,
        BatchEmbedder embedder,
        ChunkingOptions? chunkingOptions = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        _chunkingOptions = chunkingOptions ?? new ChunkingOptions();
    }

    /// <summary>
    /// Ingests one source document as a parent record plus its embedded child chunks, embedding only new/changed
    /// children, and deletes any previously stored parent/child record for this tenant+source that the current
    /// content no longer produces.
    /// </summary>
    public async Task<IngestionResult> IngestAsync(
        SourceDocument document,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        document.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        string parentId = DeterministicId.ForParent(document.TenantId, document.SourceId);
        // The parent's tracked hash covers title/URL as well as content -- not just content -- so a title-only or
        // URL-only edit (no child chunk text change) is still detected as a parent-record change on the next run.
        // ContentHash.ComputeFramed uses canonical length-prefixed framing (CanonicalFraming), not delimiter-joined
        // concatenation, so a Title/Url boundary shift (e.g. Title="a\u001fb", Url="c" versus Title="a",
        // Url="b\u001fc") can never be silently mistaken for unchanged content.
        string parentHash = ContentHash.ComputeFramed(document.Title, document.Url, document.Content);
        IReadOnlyList<string> chunkTexts = DocumentChunker.Chunk(document.Content, _chunkingOptions);

        var desired = new List<ChunkCandidate>(chunkTexts.Count + 1)
        {
            new(parentId, ParentId: null, ChunkRecord.ParentRecordType, document.Content, parentHash, NeedsEmbedding: false),
        };
        for (int index = 0; index < chunkTexts.Count; index++)
        {
            string id = DeterministicId.ForChunk(document.TenantId, document.SourceId, index);
            string hash = ContentHash.Compute(chunkTexts[index]);
            desired.Add(new ChunkCandidate(id, parentId, ChunkRecord.ChildRecordType, chunkTexts[index], hash, NeedsEmbedding: true));
        }

        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyDictionary<string, string> existing = await _store
            .GetExistingHashesAsync(document.TenantId, document.SourceId, cancellationToken)
            .ConfigureAwait(false);

        (IReadOnlyList<ChunkCandidate> toWrite, int unchanged, IReadOnlyList<string> staleIds) =
            IngestionDiffing.Diff(desired, existing);

        ChunkCandidate[] toEmbed = [.. toWrite.Where(static candidate => candidate.NeedsEmbedding)];
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<ReadOnlyMemory<float>> embeddings = toEmbed.Length == 0
            ? []
            : await _embedder
                .EmbedAsync([.. toEmbed.Select(static candidate => candidate.Text)], cancellationToken)
                .ConfigureAwait(false);

        var embeddingById = new Dictionary<string, ReadOnlyMemory<float>>(toEmbed.Length, StringComparer.Ordinal);
        for (int index = 0; index < toEmbed.Length; index++)
        {
            embeddingById[toEmbed[index].Id] = embeddings[index];
        }

        var records = new List<ChunkRecord>(toWrite.Count);
        foreach (ChunkCandidate candidate in toWrite)
        {
            embeddingById.TryGetValue(candidate.Id, out ReadOnlyMemory<float> embedding);
            // NOTE: the cast to `ReadOnlyMemory<float>?` is required -- ReadOnlyMemory<T> has an implicit
            // conversion from a null array, so an un-cast `cond ? embedding : null` resolves to the *non-nullable*
            // ReadOnlyMemory<float> type and silently produces an empty-but-non-null memory instead of `null` for
            // parent records, which must have a `null` (not empty) Embedding.
            records.Add(new ChunkRecord(
                candidate.Id,
                document.TenantId,
                document.SourceId,
                candidate.ParentId,
                candidate.RecordType,
                candidate.Text,
                candidate.Hash,
                candidate.NeedsEmbedding ? (ReadOnlyMemory<float>?)embedding : null,
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
