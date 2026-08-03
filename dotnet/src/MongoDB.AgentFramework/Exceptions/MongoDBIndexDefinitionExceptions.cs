namespace MongoDB.AgentFramework;

/// <summary>Raised when a required named MongoDB Search index is absent.</summary>
public sealed class MongoDBIndexMissingException : MongoDBIndexException
{
    /// <summary>Initializes a missing-index exception.</summary>
    public MongoDBIndexMissingException(string message)
        : base(message)
    {
    }
}

/// <summary>Raised when a MongoDB Search index definition is incompatible.</summary>
public sealed class MongoDBIndexMismatchException : MongoDBIndexException
{
    /// <summary>Initializes an index-definition mismatch exception.</summary>
    public MongoDBIndexMismatchException(string message)
        : base(message)
    {
    }
}

/// <summary>Raised when a MongoDB Search index exists but is not queryable.</summary>
public sealed class MongoDBIndexNotReadyException : MongoDBIndexException
{
    /// <summary>Initializes an index-not-ready exception.</summary>
    public MongoDBIndexNotReadyException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Raised when a MongoDB Search/Vector Search index reports a terminal <c>FAILED</c> build status. This is a
/// non-transient, actionable failure: a failed index build never becomes ready on its own (docs/spec/features/
/// index-management.md's state machine only allows <c>Failed -&gt; Building</c> through an explicit retry or
/// repair), so bounded polling (<see cref="Internal.IndexManagement.BoundedExponentialPolling"/>) must never treat
/// this as transient and retry it until the deadline elapses -- it is surfaced on the very first inspection.
/// </summary>
public sealed class MongoDBIndexFailedException : MongoDBIndexException
{
    /// <summary>Initializes an index-build-failed exception.</summary>
    public MongoDBIndexFailedException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Raised by an explicit, create-only index operation (for example a facade's <c>Create*Async</c>) when the named
/// index already exists. Unlike the idempotent <c>Ensure*Async</c> reconciliation operation -- which treats a
/// concurrent creator reaching the same end state as a successful no-op -- a create-only operation is
/// intentionally not idempotent: docs/spec/features/index-management.md lists <c>create index</c> and
/// <c>ensure expected definition</c> as distinct operations, and a caller that explicitly asked to create must be
/// told when there was already something there instead of silently proceeding.
/// </summary>
public sealed class MongoDBIndexAlreadyExistsException : MongoDBIndexException
{
    /// <summary>Initializes an index-already-exists exception.</summary>
    public MongoDBIndexAlreadyExistsException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes an index-already-exists exception while preserving its underlying cause.</summary>
    public MongoDBIndexAlreadyExistsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
