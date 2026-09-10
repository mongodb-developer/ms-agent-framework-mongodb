namespace MongoDB.AgentFramework;

/// <summary>Raised when a MongoDB document cannot be mapped safely.</summary>
public sealed class MongoDBMappingException : MongoDBIntegrationException
{
    /// <summary>Initializes an exception with an actionable message.</summary>
    /// <param name="message">The error message.</param>
    public MongoDBMappingException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes an exception while preserving its underlying cause.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying error.</param>
    public MongoDBMappingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
