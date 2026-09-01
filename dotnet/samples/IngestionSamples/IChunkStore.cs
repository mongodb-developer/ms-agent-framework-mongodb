namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// A sample-local storage seam behind the ingestion pipelines, isolating MongoDB bulk-write/cleanup mechanics so
/// chunking/hashing/diffing behavior is unit-testable without a live deployment (unit tests must not require
/// network access). <see cref="MongoChunkStore"/> is the production-shaped implementation used by the console
/// samples and the credential-gated integration tests; an in-memory fake is used only by offline tests.
/// </summary>
public interface IChunkStore
{
    /// <summary>
    /// Reads the currently stored content hash for every chunk/parent record scoped to one tenant and source,
    /// bounded and streamed rather than materializing the whole collection.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetExistingHashesAsync(
        string tenantId,
        string sourceId,
        CancellationToken cancellationToken = default);

    /// <summary>Bulk-upserts new or changed records in bounded batches.</summary>
    Task UpsertAsync(
        IReadOnlyList<ChunkRecord> records,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes only the given record IDs, and only when they also match <paramref name="tenantId"/> and
    /// <paramref name="sourceId"/> -- a record ID alone is never a sufficient deletion scope. Returns the number of
    /// records actually deleted. Never issues an unrestricted deletion: an empty <paramref name="ids"/> list is a
    /// deliberate no-op, not translated into a scope-only filter.
    /// </summary>
    Task<int> DeleteAsync(
        string tenantId,
        string sourceId,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every record (parent, child, or flat chunk) scoped to one tenant and source -- used when an entire
    /// source has disappeared from the corpus, as opposed to <see cref="DeleteAsync"/>'s per-ID stale-chunk cleanup
    /// within a source that is still active. Always scoped to both <paramref name="tenantId"/> and
    /// <paramref name="sourceId"/>; never issues a tenant-only or unscoped deletion. Returns the number of records
    /// actually deleted. Implementations must delete in bounded batches rather than one unbounded operation.
    /// </summary>
    Task<int> DeleteSourceAsync(
        string tenantId,
        string sourceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every distinct source ID currently stored for one tenant, bounded and streamed rather than
    /// materializing the whole collection. Used to detect sources that were previously ingested but have since
    /// disappeared from a caller's manifest of currently known sources.
    /// </summary>
    Task<IReadOnlyList<string>> ListSourceIdsAsync(
        string tenantId,
        CancellationToken cancellationToken = default);
}
