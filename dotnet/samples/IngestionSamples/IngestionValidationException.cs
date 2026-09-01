namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// A sample-local validation error distinct from the runtime package's <c>MongoDBConfigurationException</c>. This
/// library is deliberately sample-only (docs/spec/features/ingestion.md forbids a production ingestion API in the
/// runtime package), so its errors are never mistaken for a runtime contract.
/// </summary>
public sealed class IngestionValidationException : Exception
{
    /// <summary>Initializes a new validation exception with an actionable message.</summary>
    public IngestionValidationException(string message)
        : base(message)
    {
    }
}
