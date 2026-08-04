namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// A sample-local seam over child chunk retrieval, isolating <c>MongoDBRAGProvider.SearchAsync</c> so
/// <see cref="ParentDocumentRetriever"/>'s bounding/de-duplication/attribution logic is unit-testable offline. The
/// production implementation is <see cref="MongoDBRAGChildChunkSearcher"/>, which wraps the runtime provider so
/// this pattern reuses the same public querying surface as direct RAG retrieval (docs/spec/features/ingestion.md's
/// "use the same ... index manager" requirement extends naturally to reusing the same query provider). This is not
/// an unrestricted pipeline callback: it is a fixed one-method contract with no caller-supplied pipeline stages.
/// </summary>
public interface IChildChunkSearcher
{
    /// <summary>
    /// Searches for child chunks matching <paramref name="query"/>. Any tenant/child-record-type authorization
    /// scoping is the responsibility of the searcher's own configuration (for example
    /// <c>MongoDBRAGProviderOptions.MandatoryFilter</c>), never of this method's caller.
    /// </summary>
    Task<IReadOnlyList<MongoDBRAGResult>> SearchAsync(string query, CancellationToken cancellationToken = default);
}
