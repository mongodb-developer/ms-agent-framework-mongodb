namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// A sample-local manifest-reconciliation flow layered over <see cref="IChunkStore"/>: given a tenant's currently
/// known set of source IDs (its "manifest"), tombstones (fully deletes) every previously stored source that is no
/// longer present in that manifest. This is the complement to <see cref="IncrementalIngestionPipeline"/>'s
/// per-source stale-chunk cleanup, which only ever removes chunks within a source that is still being actively
/// re-ingested; a source that disappears from the corpus entirely (its <see cref="SourceDocument"/> is simply no
/// longer produced) would otherwise never be revisited by that pipeline and its records would linger forever.
/// </summary>
public sealed class SourceManifestReconciler
{
    private readonly IChunkStore _chunkStore;

    /// <summary>Initializes a reconciler over an injected, caller-owned <see cref="IChunkStore"/>.</summary>
    public SourceManifestReconciler(IChunkStore chunkStore)
    {
        _chunkStore = chunkStore ?? throw new ArgumentNullException(nameof(chunkStore));
    }

    /// <summary>
    /// Compares <paramref name="currentSourceIds"/> (the tenant's full, currently known set of source IDs) against
    /// every source ID currently stored for <paramref name="tenantId"/>, and fully deletes (tombstones) every
    /// stored source absent from <paramref name="currentSourceIds"/>. An empty <paramref name="currentSourceIds"/>
    /// is a deliberate, valid "no sources currently observed" input -- unlike <see cref="IChunkStore.DeleteAsync"/>,
    /// which no-ops on an empty ID list to prevent an unintended unbounded delete, this method's caller is
    /// explicitly opting into reconciliation and an empty manifest legitimately means every stored source for this
    /// tenant has disappeared. Deletion is always scoped to <paramref name="tenantId"/>; other tenants' sources,
    /// even ones sharing the same source ID, are never affected. Checks <paramref name="cancellationToken"/> before
    /// deleting each disappeared source, so cancellation halts before any further deletion executes.
    /// </summary>
    public async Task<SourceReconciliationResult> ReconcileAsync(
        string tenantId,
        IReadOnlyCollection<string> currentSourceIds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            throw new IngestionValidationException($"{nameof(tenantId)} must not be empty.");
        }

        ArgumentNullException.ThrowIfNull(currentSourceIds);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<string> storedSourceIds = await _chunkStore
            .ListSourceIdsAsync(tenantId, cancellationToken)
            .ConfigureAwait(false);
        var currentSet = new HashSet<string>(currentSourceIds, StringComparer.Ordinal);
        List<string> disappeared = [.. storedSourceIds.Where(sourceId => !currentSet.Contains(sourceId))];

        int recordsDeleted = 0;
        foreach (string sourceId in disappeared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            recordsDeleted += await _chunkStore
                .DeleteSourceAsync(tenantId, sourceId, cancellationToken)
                .ConfigureAwait(false);
        }

        return new SourceReconciliationResult(disappeared, recordsDeleted);
    }
}
