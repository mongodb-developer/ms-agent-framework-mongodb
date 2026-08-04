namespace MongoDB.AgentFramework;

/// <summary>Raised when a MongoDB retrieval operation fails.</summary>
public sealed class MongoDBRetrievalException : MongoDBIntegrationException
{
    /// <summary>Initializes an exception with an actionable message.</summary>
    /// <param name="message">The error message.</param>
    public MongoDBRetrievalException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes an exception while preserving its underlying cause.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying error.</param>
    public MongoDBRetrievalException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
