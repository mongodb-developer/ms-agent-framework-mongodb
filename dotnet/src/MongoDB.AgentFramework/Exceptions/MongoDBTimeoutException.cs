namespace MongoDB.AgentFramework;

/// <summary>Raised when a configured provider operation deadline expires.</summary>
public sealed class MongoDBTimeoutException : MongoDBIntegrationException
{
    /// <summary>Initializes a timeout exception while preserving cancellation.</summary>
    public MongoDBTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
