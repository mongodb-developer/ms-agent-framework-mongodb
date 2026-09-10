namespace MongoDB.AgentFramework.Internal.Observability;

/// <summary>
/// Stable, low-cardinality <c>feature</c> values for the telemetry contract in
/// docs/spec/observability-security.md. Never add a value here without updating that spec first: the whole
/// point of a closed vocabulary is that a telemetry backend's dimension cardinality never grows unbounded.
/// </summary>
internal static class MongoDBTelemetryFeature
{
    public const string Memory = "memory";
    public const string History = "history";
    public const string Rag = "rag";
    public const string SessionStore = "session_store";
    public const string CheckpointStore = "checkpoint_store";
}

/// <summary>Stable, low-cardinality <c>operation</c> values for the telemetry contract. This is a closed set;
/// every instrumented method maps onto one of these, never a bespoke per-method name.</summary>
internal static class MongoDBTelemetryOperation
{
    public const string Retrieve = "retrieve";
    public const string Persist = "persist";
    public const string Delete = "delete";
    public const string ValidateIndex = "validate_index";
    public const string EnsureIndex = "ensure_index";
    public const string Load = "load";
    public const string List = "list";
}

/// <summary>Stable, low-cardinality <c>mode</c> values identifying the MongoDB Search/Vector Search retrieval
/// strategy an operation used. Omitted (null) for operations with no retrieval-mode concept.</summary>
internal static class MongoDBTelemetryMode
{
    /// <summary>Approximate nearest-neighbor Vector Search.</summary>
    public const string Ann = "ann";

    /// <summary>Exact nearest-neighbor Vector Search.</summary>
    public const string Enn = "enn";

    /// <summary>MongoDB Search full-text retrieval.</summary>
    public const string FullText = "full_text";

    /// <summary>Reciprocal-rank-fusion hybrid retrieval combining Vector Search and full-text Search.</summary>
    public const string HybridRrf = "hybrid_rrf";
}

/// <summary>Stable, low-cardinality <c>outcome</c> values. <see cref="Cancelled"/> is always distinct from
/// <see cref="Failed"/>: cancellation is caller-directed control flow, never an error category.</summary>
internal static class MongoDBTelemetryOutcome
{
    public const string Success = "success";
    public const string Empty = "empty";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}
