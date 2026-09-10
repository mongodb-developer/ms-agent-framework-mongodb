namespace MongoDB.AgentFramework.Samples.Ingestion;

/// <summary>
/// The production <see cref="IChildChunkSearcher"/> implementation: a thin adapter over the runtime
/// <see cref="MongoDBRAGProvider"/>, reusing its public <see cref="MongoDBRAGProvider.SearchAsync"/> seam exactly as
/// direct RAG retrieval does. The wrapped provider must already be configured (via
/// <see cref="MongoDBRAGProviderOptions.MandatoryFilter"/>) to constrain retrieval to authorized child records --
/// this adapter performs no additional filtering of its own.
/// </summary>
public sealed class MongoDBRAGChildChunkSearcher : IChildChunkSearcher, IAsyncDisposable
{
    private readonly MongoDBRAGProvider _provider;
    private readonly bool _ownsProvider;

    /// <summary>
    /// Initializes an adapter over an injected, already-configured provider.
    /// </summary>
    /// <param name="provider">
    /// A provider whose options already constrain <see cref="MongoDBRAGProviderOptions.MandatoryFilter"/> to
    /// authorized child records (for example <c>record_type == "child"</c> AND the caller's tenant).
    /// </param>
    /// <param name="ownsProvider">
    /// Whether this adapter disposes <paramref name="provider"/> when it is disposed. Defaults to
    /// <see langword="false"/> since providers are normally caller-owned.
    /// </param>
    public MongoDBRAGChildChunkSearcher(MongoDBRAGProvider provider, bool ownsProvider = false)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _ownsProvider = ownsProvider;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<MongoDBRAGResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default) =>
        _provider.SearchAsync(query, cancellationToken);

    /// <summary>Disposes the wrapped provider only if this adapter was constructed to own it.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_ownsProvider)
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
        }
    }
}
