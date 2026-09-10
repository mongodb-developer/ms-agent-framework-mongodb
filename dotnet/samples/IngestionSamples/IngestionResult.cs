namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// A summary of one <see cref="IncrementalIngestionPipeline.IngestAsync"/> or
/// <see cref="ParentDocumentIngestionPipeline.IngestAsync"/> call: how many of the source's chunks were unchanged
/// and skipped, how many were new or changed and written, and how many previously stored stale chunks were deleted.
/// </summary>
public sealed record IngestionResult(
    string SourceId,
    int ChunksUnchanged,
    int ChunksUpserted,
    int ChunksDeleted);
