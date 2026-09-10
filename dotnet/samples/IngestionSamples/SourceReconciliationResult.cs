namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// The outcome of one <see cref="SourceManifestReconciler.ReconcileAsync"/> call: which previously stored source
/// IDs are no longer present in the caller's manifest of currently known sources ("disappeared"), and how many
/// stored records those disappeared sources' tombstoning deleted.
/// </summary>
public sealed record SourceReconciliationResult(
    IReadOnlyList<string> DisappearedSourceIds,
    int RecordsDeleted);
