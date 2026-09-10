namespace MongoDB.AgentFramework;

/// <summary>Raised when a required MongoDB capability is unavailable.</summary>
public sealed class MongoDBCapabilityException : MongoDBIntegrationException
{
    /// <summary>Initializes an exception with an actionable message.</summary>
    /// <param name="message">The error message.</param>
    public MongoDBCapabilityException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes an exception while preserving its underlying cause.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying error.</param>
    public MongoDBCapabilityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
