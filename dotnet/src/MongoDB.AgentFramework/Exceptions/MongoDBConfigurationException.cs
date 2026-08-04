namespace MongoDB.AgentFramework;

/// <summary>Raised when integration configuration is invalid.</summary>
public sealed class MongoDBConfigurationException : MongoDBIntegrationException
{
    /// <summary>Initializes an exception with an actionable message.</summary>
    /// <param name="message">The error message.</param>
    public MongoDBConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes an exception while preserving its underlying cause.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying error.</param>
    public MongoDBConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
