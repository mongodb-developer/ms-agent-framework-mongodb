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
