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
