namespace MongoDB.AgentFramework;

/// <summary>Raised when a required MongoDB Search index is absent, mismatched, or not ready.</summary>
public class MongoDBIndexException : MongoDBIntegrationException
{
    /// <summary>Initializes an exception with an actionable message.</summary>
    /// <param name="message">The error message.</param>
    public MongoDBIndexException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes an exception while preserving its underlying cause.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying error.</param>
    public MongoDBIndexException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
