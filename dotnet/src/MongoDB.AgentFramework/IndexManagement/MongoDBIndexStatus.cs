namespace MongoDB.AgentFramework;

/// <summary>
/// The observable lifecycle status of a MongoDB Search or Vector Search index, distinguishing every state
/// docs/spec/features/index-management.md's polling requirements call out: missing, still building, present but
/// not yet queryable, ready/queryable, and a terminal server-reported build failure. This is a superset of the
/// index state machine's <c>Missing</c>/<c>Building</c>/<c>Ready</c>/<c>Failed</c> states: <see cref="ReadyNotQueryable"/>
/// is the transient window between a build completing (server status <c>READY</c>) and the index actually
/// becoming queryable, which the specification requires callers be able to distinguish from <see cref="Building"/>.
/// </summary>
public enum MongoDBIndexStatus
{
    /// <summary>No index with the requested name exists.</summary>
    Missing,

    /// <summary>The index exists and is still being built asynchronously (not yet <c>READY</c>).</summary>
    Building,

    /// <summary>The server reports <c>READY</c>, but the index is not yet queryable.</summary>
    ReadyNotQueryable,

    /// <summary>The index is <c>READY</c> and queryable.</summary>
    Ready,

    /// <summary>The server reports a terminal build failure. Index managers never retry this automatically.</summary>
    Failed,
}
