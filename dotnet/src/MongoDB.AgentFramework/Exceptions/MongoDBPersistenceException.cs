namespace MongoDB.AgentFramework;

/// <summary>Raised when a MongoDB persistence operation fails.</summary>
public sealed class MongoDBPersistenceException : MongoDBIntegrationException
{
    /// <summary>Initializes an exception with an actionable message.</summary>
    /// <param name="message">The error message.</param>
    public MongoDBPersistenceException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes an exception while preserving its underlying cause.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying error.</param>
    public MongoDBPersistenceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
