namespace MongoDB.AgentFramework;

/// <summary>Raised when embedding generation or validation fails.</summary>
public sealed class MongoDBEmbeddingException : MongoDBIntegrationException
{
    /// <summary>Initializes an exception with an actionable message.</summary>
    /// <param name="message">The error message.</param>
    public MongoDBEmbeddingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes an exception while preserving its underlying cause.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying error.</param>
    public MongoDBEmbeddingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
