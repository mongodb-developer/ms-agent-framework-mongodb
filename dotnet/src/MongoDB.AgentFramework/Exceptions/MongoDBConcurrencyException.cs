namespace MongoDB.AgentFramework;

/// <summary>
/// Raised when an optimistic compare-and-swap write or an authorized version-checked deletion cannot proceed
/// because the stored document's version no longer matches the caller's expectation, or the document is absent
/// when a match was required. Callers must reload the current version and retry explicitly; the store never
/// silently overwrites a conflicting write.
/// </summary>
public sealed class MongoDBConcurrencyException : MongoDBIntegrationException
{
    /// <summary>Initializes an exception with an actionable message.</summary>
    /// <param name="message">The error message.</param>
    public MongoDBConcurrencyException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes an exception while preserving its underlying cause.</summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying error.</param>
    public MongoDBConcurrencyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
