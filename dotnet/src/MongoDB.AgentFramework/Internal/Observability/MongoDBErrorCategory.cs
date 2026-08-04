namespace MongoDB.AgentFramework.Internal.Observability;

/// <summary>
/// Classifies a caught exception into the stable, low-cardinality error-category taxonomy used by telemetry
/// (docs/spec/observability-security.md, docs/spec/resilience.md). The exception's message is never part of
/// the classification result, and never flows into a log field, activity tag, or metric dimension -- only the
/// category name does. <see cref="OperationCanceledException"/> is deliberately not classified here: callers
/// must recognize and record it as the distinct <see cref="MongoDBTelemetryOutcome.Cancelled"/> outcome before
/// ever reaching this classifier.
/// </summary>
internal static class MongoDBErrorCategory
{
    public const string Configuration = "configuration";
    public const string Embedding = "embedding";
    public const string Capability = "capability";
    public const string IndexMissing = "index_missing";
    public const string IndexMismatch = "index_mismatch";
    public const string IndexNotReady = "index_not_ready";
    public const string IndexFailed = "index_failed";
    public const string IndexAlreadyExists = "index_already_exists";
    public const string IndexPrivilege = "index_privilege";
    public const string IndexOther = "index_other";
    public const string Mapping = "mapping";
    public const string Retrieval = "retrieval";
    public const string Persistence = "persistence";
    public const string Timeout = "timeout";
    public const string Concurrency = "concurrency";
    public const string Unknown = "unknown";

    /// <summary>Maps an exception's type onto a stable category name. Order matters: derived exception types
    /// (for example the specific index-definition exceptions) are checked before their common base
    /// <see cref="MongoDBIndexException"/>.</summary>
    public static string Classify(Exception exception) => exception switch
    {
        MongoDBIndexMissingException => IndexMissing,
        MongoDBIndexMismatchException => IndexMismatch,
        MongoDBIndexNotReadyException => IndexNotReady,
        MongoDBIndexFailedException => IndexFailed,
        MongoDBIndexAlreadyExistsException => IndexAlreadyExists,
        MongoDBIndexPrivilegeException => IndexPrivilege,
        MongoDBIndexException => IndexOther,
        MongoDBEmbeddingException => Embedding,
        MongoDBCapabilityException => Capability,
        MongoDBMappingException => Mapping,
        MongoDBRetrievalException => Retrieval,
        MongoDBPersistenceException => Persistence,
        MongoDBTimeoutException => Timeout,
        MongoDBConcurrencyException => Concurrency,
        MongoDBConfigurationException => Configuration,
        ArgumentException => Configuration,
        _ => Unknown,
    };
}
