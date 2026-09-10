namespace MongoDB.AgentFramework;

/// <summary>Base exception for errors raised by MongoDB Agent Framework integrations.</summary>
public class MongoDBIntegrationException : Exception
{
    /// <summary>Initializes an exception with an actionable message.</summary>
    /// <param name="message">The error message.</param>
    public MongoDBIntegrationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes an exception while preserving its underlying cause.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying error.</param>
    public MongoDBIntegrationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
