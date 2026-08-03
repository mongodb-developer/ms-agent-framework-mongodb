using MongoDB.AgentFramework.Internal;
using MongoDB.AgentFramework.Internal.IndexManagement;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Search;

namespace MongoDB.AgentFramework;

/// <summary>
/// An explicit, feature-specific facade over RAG's Vector Search and/or Search indexes (docs/spec/features/
/// index-management.md's index-management interface, kept in the runtime package per ADR 0016), independently
/// constructible from a collection and one or both index definitions without requiring a full
/// <see cref="MongoDBRAGProvider"/> or an embedding generator -- demonstrating the "provisioner" role a
/// least-privilege deployment keeps separate from the "runtime" role <see cref="MongoDBRAGProvider"/> plays (ADR
/// 0006). At least one of <see cref="VectorDefinition"/>/<see cref="SearchDefinition"/> must be configured;
/// <see cref="MongoDBSearchMode.HybridRrf"/>'s operations additionally require both. Every retrieval method
/// (<c>Get*</c>/<c>List*</c>/<c>Validate*</c>) never mutates MongoDB; only <c>Ensure*</c>/<c>Update*</c>/<c>Drop*</c>
/// do, and only when explicitly called.
/// </summary>
public sealed class MongoDBRAGIndexManager : IAsyncDisposable
{
    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly OwnedResource<IMongoClient>? _client;

    /// <summary>Creates a manager over an injected database, which remains caller-owned.</summary>
    public MongoDBRAGIndexManager(
        IMongoDatabase database,
        string collectionName,
        MongoDBVectorSearchIndexDefinition? vectorDefinition = null,
        MongoDBSearchIndexDefinition? searchDefinition = null)
        : this(
            (database ?? throw new ArgumentNullException(nameof(database)))
                .GetCollection<BsonDocument>(RequireText(collectionName, nameof(collectionName))),
            vectorDefinition,
            searchDefinition)
    {
    }

    /// <summary>Creates a manager over an injected collection, which remains caller-owned.</summary>
    public MongoDBRAGIndexManager(
        IMongoCollection<BsonDocument> collection,
        MongoDBVectorSearchIndexDefinition? vectorDefinition = null,
        MongoDBSearchIndexDefinition? searchDefinition = null)
    {
        if (vectorDefinition is null && searchDefinition is null)
        {
            throw new MongoDBConfigurationException(
                $"At least one of {nameof(vectorDefinition)} or {nameof(searchDefinition)} must be configured.");
        }

        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        VectorDefinition = vectorDefinition;
        SearchDefinition = searchDefinition;
    }

    /// <summary>Creates a manager over an injected client, which remains caller-owned.</summary>
    public MongoDBRAGIndexManager(
        IMongoClient client,
        string databaseName,
        string collectionName,
        MongoDBVectorSearchIndexDefinition? vectorDefinition = null,
        MongoDBSearchIndexDefinition? searchDefinition = null)
        : this(
            (client ?? throw new ArgumentNullException(nameof(client)))
                .GetDatabase(RequireText(databaseName, nameof(databaseName))),
            collectionName,
            vectorDefinition,
            searchDefinition)
    {
    }

    /// <summary>
    /// Creates a manager-owned client from a connection string, for standalone provisioning tooling that runs
    /// under a distinct, more privileged identity than the runtime <see cref="MongoDBRAGProvider"/> connects with
    /// (docs/spec/features/index-management.md's least-privilege table).
    /// </summary>
    public MongoDBRAGIndexManager(
        string connectionString,
        string databaseName,
        string collectionName,
        MongoDBVectorSearchIndexDefinition? vectorDefinition = null,
        MongoDBSearchIndexDefinition? searchDefinition = null)
        : this(
            ConnectClient(connectionString, databaseName, collectionName),
            vectorDefinition,
            searchDefinition)
    {
    }

    private MongoDBRAGIndexManager(
        (OwnedResource<IMongoClient> Client, IMongoCollection<BsonDocument> Collection) connected,
        MongoDBVectorSearchIndexDefinition? vectorDefinition,
        MongoDBSearchIndexDefinition? searchDefinition)
        : this(connected.Collection, vectorDefinition, searchDefinition)
    {
        _client = connected.Client;
    }

    /// <summary>Gets whether the manager owns its MongoDB client.</summary>
    public bool OwnsClient => _client?.OwnsValue is true;

    /// <summary>Gets the expected Vector Search index definition, or <see langword="null"/> if not configured.</summary>
    public MongoDBVectorSearchIndexDefinition? VectorDefinition { get; }

    /// <summary>Gets the expected Search index definition, or <see langword="null"/> if not configured.</summary>
    public MongoDBSearchIndexDefinition? SearchDefinition { get; }

    /// <summary>Lists every Search/Vector Search index on the collection, never mutating MongoDB.</summary>
    public async Task<IReadOnlyList<MongoDBIndexInfo>> ListIndexesAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<BsonDocument> indexes = await MongoDBSearchIndexes.ListAllAsync(
            _collection.SearchIndexes,
            MapInspectionException,
            cancellationToken).ConfigureAwait(false);
        return [.. indexes.Select(ToIndexInfo)];
    }

    /// <summary>Inspects the configured Vector Search index, returning <see langword="null"/> if it does not exist.</summary>
    public async Task<MongoDBIndexInfo?> GetVectorSearchIndexAsync(CancellationToken cancellationToken = default)
    {
        BsonDocument? index = await FindAsync(RequireVectorDefinition().IndexName, cancellationToken)
            .ConfigureAwait(false);
        return index is null ? null : ToIndexInfo(index);
    }

    /// <summary>Inspects the configured Search index, returning <see langword="null"/> if it does not exist.</summary>
    public async Task<MongoDBIndexInfo?> GetSearchIndexAsync(CancellationToken cancellationToken = default)
    {
        BsonDocument? index = await FindAsync(RequireSearchDefinition().IndexName, cancellationToken)
            .ConfigureAwait(false);
        return index is null ? null : ToIndexInfo(index);
    }

    /// <summary>
    /// Validates the configured Vector Search index against <see cref="VectorDefinition"/> without ever mutating
    /// MongoDB.
    /// </summary>
    /// <exception cref="MongoDBConfigurationException"><see cref="VectorDefinition"/> is not configured.</exception>
    /// <exception cref="MongoDBIndexMissingException">The configured index does not exist.</exception>
    /// <exception cref="MongoDBIndexMismatchException">The index does not match <see cref="VectorDefinition"/>.</exception>
    /// <exception cref="MongoDBIndexNotReadyException"><paramref name="requireReady"/> is <see langword="true"/> and the index is not queryable.</exception>
    public async Task<MongoDBIndexComparison> ValidateVectorSearchIndexAsync(
        bool requireReady = true,
        CancellationToken cancellationToken = default)
    {
        MongoDBVectorSearchIndexDefinition definition = RequireVectorDefinition();
        BsonDocument index = await RequireIndexAsync(definition.IndexName, cancellationToken).ConfigureAwait(false);
        return ValidateVector(index, definition, requireReady);
    }

    /// <summary>
    /// Validates the configured Search index against <see cref="SearchDefinition"/> without ever mutating
    /// MongoDB. A dynamic Search mapping cannot be checked per mandatory-filter field (docs/spec/features/
    /// index-management.md); this is a documented limitation, not an invented automatic mapping change.
    /// </summary>
    /// <exception cref="MongoDBConfigurationException"><see cref="SearchDefinition"/> is not configured.</exception>
    /// <exception cref="MongoDBIndexMissingException">The configured index does not exist.</exception>
    /// <exception cref="MongoDBIndexMismatchException">The index does not match <see cref="SearchDefinition"/>.</exception>
    /// <exception cref="MongoDBIndexNotReadyException"><paramref name="requireReady"/> is <see langword="true"/> and the index is not queryable.</exception>
    public async Task<MongoDBIndexComparison> ValidateSearchIndexAsync(
        bool requireReady = true,
        CancellationToken cancellationToken = default)
    {
        MongoDBSearchIndexDefinition definition = RequireSearchDefinition();
        BsonDocument index = await RequireIndexAsync(definition.IndexName, cancellationToken).ConfigureAwait(false);
        return ValidateSearch(index, definition, requireReady);
    }

    /// <summary>
    /// Validates that both the configured Vector Search and Search indexes exist and match their definitions --
    /// the combination <see cref="MongoDBSearchMode.HybridRrf"/> requires. Both <see cref="VectorDefinition"/> and
    /// <see cref="SearchDefinition"/> must be configured, or this fails fast with
    /// <see cref="MongoDBConfigurationException"/> rather than silently validating only one branch.
    /// </summary>
    /// <exception cref="MongoDBConfigurationException">Either definition is not configured.</exception>
    /// <exception cref="MongoDBIndexMissingException">Either configured index does not exist.</exception>
    /// <exception cref="MongoDBIndexMismatchException">Either index does not match its definition.</exception>
    /// <exception cref="MongoDBIndexNotReadyException"><paramref name="requireReady"/> is <see langword="true"/> and either index is not queryable.</exception>
    public async Task ValidateHybridAsync(
        bool requireReady = true,
        CancellationToken cancellationToken = default)
    {
        RequireHybridDefinitions();
        await ValidateVectorSearchIndexAsync(requireReady, cancellationToken).ConfigureAwait(false);
        await ValidateSearchIndexAsync(requireReady, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates the configured Vector Search index if missing, and optionally waits until queryable.</summary>
    /// <exception cref="MongoDBConfigurationException"><see cref="VectorDefinition"/> is not configured.</exception>
    /// <exception cref="MongoDBIndexMismatchException">An existing index does not match <see cref="VectorDefinition"/>.</exception>
    /// <exception cref="MongoDBIndexPrivilegeException">The connected identity lacks index-creation privileges.</exception>
    /// <exception cref="MongoDBTimeoutException"><paramref name="waitUntilReady"/> is <see langword="true"/> and the deadline elapsed.</exception>
    public Task<MongoDBIndexInfo> EnsureVectorSearchIndexAsync(
        bool waitUntilReady = false,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        MongoDBVectorSearchIndexDefinition definition = RequireVectorDefinition();
        return EnsureAsync(
            definition.IndexName,
            SearchIndexType.VectorSearch,
            VectorSearchIndexEquivalence.BuildDefinition(definition),
            index => ValidateVector(index, definition, requireReady: false),
            () => WaitUntilVectorSearchIndexReadyAsync(timeout, pollInterval, cancellationToken),
            waitUntilReady,
            cancellationToken);
    }

    /// <summary>Creates the configured Search index if missing, and optionally waits until queryable.</summary>
    /// <exception cref="MongoDBConfigurationException"><see cref="SearchDefinition"/> is not configured.</exception>
    /// <exception cref="MongoDBIndexMismatchException">An existing index does not match <see cref="SearchDefinition"/>.</exception>
    /// <exception cref="MongoDBIndexPrivilegeException">The connected identity lacks index-creation privileges.</exception>
    /// <exception cref="MongoDBTimeoutException"><paramref name="waitUntilReady"/> is <see langword="true"/> and the deadline elapsed.</exception>
    public Task<MongoDBIndexInfo> EnsureSearchIndexAsync(
        bool waitUntilReady = false,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        MongoDBSearchIndexDefinition definition = RequireSearchDefinition();
        return EnsureAsync(
            definition.IndexName,
            SearchIndexType.Search,
            SearchIndexEquivalence.BuildDefinition(definition),
            index => ValidateSearch(index, definition, requireReady: false),
            () => WaitUntilSearchIndexReadyAsync(timeout, pollInterval, cancellationToken),
            waitUntilReady,
            cancellationToken);
    }

    /// <summary>
    /// Creates both the configured Vector Search and Search indexes if missing, and optionally waits until both
    /// are queryable -- the combination <see cref="MongoDBSearchMode.HybridRrf"/> requires. Both
    /// <see cref="VectorDefinition"/> and <see cref="SearchDefinition"/> must be configured.
    /// </summary>
    /// <exception cref="MongoDBConfigurationException">Either definition is not configured.</exception>
    public async Task EnsureHybridAsync(
        bool waitUntilReady = false,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        RequireHybridDefinitions();
        await EnsureVectorSearchIndexAsync(waitUntilReady, timeout, pollInterval, cancellationToken)
            .ConfigureAwait(false);
        await EnsureSearchIndexAsync(waitUntilReady, timeout, pollInterval, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Replaces the configured Vector Search index's definition in place. The index must already exist.</summary>
    /// <exception cref="MongoDBConfigurationException"><see cref="VectorDefinition"/> is not configured.</exception>
    /// <exception cref="MongoDBIndexMissingException">The configured index does not exist.</exception>
    public async Task UpdateVectorSearchIndexAsync(CancellationToken cancellationToken = default)
    {
        MongoDBVectorSearchIndexDefinition definition = RequireVectorDefinition();
        await RequireIndexAsync(definition.IndexName, cancellationToken).ConfigureAwait(false);
        await MongoDBSearchIndexes.UpdateAsync(
            _collection.SearchIndexes,
            definition.IndexName,
            VectorSearchIndexEquivalence.BuildDefinition(definition),
            exception => MapMutationException(exception, definition.IndexName, "update"),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replaces the configured Search index's definition in place. The index must already exist.</summary>
    /// <exception cref="MongoDBConfigurationException"><see cref="SearchDefinition"/> is not configured.</exception>
    /// <exception cref="MongoDBIndexMissingException">The configured index does not exist.</exception>
    public async Task UpdateSearchIndexAsync(CancellationToken cancellationToken = default)
    {
        MongoDBSearchIndexDefinition definition = RequireSearchDefinition();
        await RequireIndexAsync(definition.IndexName, cancellationToken).ConfigureAwait(false);
        await MongoDBSearchIndexes.UpdateAsync(
            _collection.SearchIndexes,
            definition.IndexName,
            SearchIndexEquivalence.BuildDefinition(definition),
            exception => MapMutationException(exception, definition.IndexName, "update"),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls with bounded exponential backoff until the configured Vector Search index reports
    /// <c>READY</c>/queryable, returning its final inspected snapshot.
    /// </summary>
    /// <exception cref="MongoDBConfigurationException"><see cref="VectorDefinition"/> is not configured.</exception>
    /// <exception cref="MongoDBTimeoutException">The deadline elapsed before the index became queryable.</exception>
    public Task<MongoDBIndexInfo> WaitUntilVectorSearchIndexReadyAsync(
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        MongoDBVectorSearchIndexDefinition definition = RequireVectorDefinition();
        return WaitUntilReadyAsync(
            definition.IndexName,
            () => ValidateVectorSearchIndexAsync(true, cancellationToken),
            timeout,
            pollInterval,
            cancellationToken);
    }

    /// <summary>
    /// Polls with bounded exponential backoff until the configured Search index reports <c>READY</c>/queryable,
    /// returning its final inspected snapshot.
    /// </summary>
    /// <exception cref="MongoDBConfigurationException"><see cref="SearchDefinition"/> is not configured.</exception>
    /// <exception cref="MongoDBTimeoutException">The deadline elapsed before the index became queryable.</exception>
    public Task<MongoDBIndexInfo> WaitUntilSearchIndexReadyAsync(
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        MongoDBSearchIndexDefinition definition = RequireSearchDefinition();
        return WaitUntilReadyAsync(
            definition.IndexName,
            () => ValidateSearchIndexAsync(true, cancellationToken),
            timeout,
            pollInterval,
            cancellationToken);
    }

    /// <summary>Drops the configured Vector Search index. Already being absent is a successful no-op.</summary>
    /// <exception cref="MongoDBConfigurationException"><see cref="VectorDefinition"/> is not configured.</exception>
    public Task DropVectorSearchIndexAsync(CancellationToken cancellationToken = default)
    {
        MongoDBVectorSearchIndexDefinition definition = RequireVectorDefinition();
        return MongoDBSearchIndexes.DropAsync(
            _collection.SearchIndexes,
            definition.IndexName,
            exception => MapMutationException(exception, definition.IndexName, "drop"),
            cancellationToken);
    }

    /// <summary>Drops the configured Search index. Already being absent is a successful no-op.</summary>
    /// <exception cref="MongoDBConfigurationException"><see cref="SearchDefinition"/> is not configured.</exception>
    public Task DropSearchIndexAsync(CancellationToken cancellationToken = default)
    {
        MongoDBSearchIndexDefinition definition = RequireSearchDefinition();
        return MongoDBSearchIndexes.DropAsync(
            _collection.SearchIndexes,
            definition.IndexName,
            exception => MapMutationException(exception, definition.IndexName, "drop"),
            cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<MongoDBIndexInfo> EnsureAsync(
        string indexName,
        SearchIndexType type,
        BsonDocument definitionDocument,
        Action<BsonDocument> validateExisting,
        Func<Task<MongoDBIndexInfo>> waitUntilReadyAsync,
        bool waitUntilReady,
        CancellationToken cancellationToken)
    {
        BsonDocument? index = await FindAsync(indexName, cancellationToken).ConfigureAwait(false);
        if (index is null)
        {
            await MongoDBSearchIndexes.CreateAsync(
                _collection.SearchIndexes,
                new CreateSearchIndexModel(indexName, type, definitionDocument),
                exception => MapMutationException(exception, indexName, "create"),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            validateExisting(index);
        }

        if (waitUntilReady)
        {
            return await waitUntilReadyAsync().ConfigureAwait(false);
        }

        BsonDocument? refreshed = await FindAsync(indexName, cancellationToken).ConfigureAwait(false);
        return refreshed is null
            ? throw new MongoDBIndexMissingException(
                $"Index '{indexName}' was created but could not be re-inspected.")
            : ToIndexInfo(refreshed);
    }

    private Task<MongoDBIndexInfo> WaitUntilReadyAsync(
        string indexName,
        Func<Task<MongoDBIndexComparison>> validateReadyAsync,
        TimeSpan? timeout,
        TimeSpan? pollInterval,
        CancellationToken cancellationToken) =>
        BoundedExponentialPolling.RunAsync(
            async token =>
            {
                await validateReadyAsync().ConfigureAwait(false);
                BsonDocument index = await RequireIndexAsync(indexName, token).ConfigureAwait(false);
                return ToIndexInfo(index);
            },
            static exception => exception is MongoDBIndexNotReadyException or MongoDBIndexMissingException,
            exception => new MongoDBTimeoutException(
                $"Index '{indexName}' was not ready before timeout.",
                exception),
            timeout ?? TimeSpan.FromSeconds(60),
            pollInterval ?? TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(30),
            cancellationToken);

    private async Task<BsonDocument> RequireIndexAsync(string indexName, CancellationToken cancellationToken)
    {
        BsonDocument? index = await FindAsync(indexName, cancellationToken).ConfigureAwait(false);
        return index ?? throw new MongoDBIndexMissingException(
            $"Index '{indexName}' does not exist; create it explicitly.");
    }

    private Task<BsonDocument?> FindAsync(string indexName, CancellationToken cancellationToken) =>
        MongoDBSearchIndexes.FindAsync(_collection.SearchIndexes, indexName, MapInspectionException, cancellationToken);

    private static MongoDBIndexComparison ValidateVector(
        BsonDocument index, MongoDBVectorSearchIndexDefinition definition, bool requireReady) =>
        VectorSearchIndexEquivalence.Validate(index, definition, requireReady);

    private static MongoDBIndexComparison ValidateSearch(
        BsonDocument index, MongoDBSearchIndexDefinition definition, bool requireReady) =>
        SearchIndexEquivalence.Validate(index, definition, requireReady).Comparison;

    private static MongoDBIndexInfo ToIndexInfo(BsonDocument index) =>
        new(
            index.GetValue("name", "").AsString,
            index.GetValue("type", "").AsString,
            MongoDBSearchIndexes.Classify(index),
            index.GetValue("queryable", false).ToBoolean(),
            index.GetValue("status", "").AsString,
            MongoDBSearchIndexes.GetDefinition(index));

    private MongoDBVectorSearchIndexDefinition RequireVectorDefinition() =>
        VectorDefinition ?? throw new MongoDBConfigurationException(
            $"{nameof(VectorDefinition)} is not configured on this {nameof(MongoDBRAGIndexManager)}.");

    private MongoDBSearchIndexDefinition RequireSearchDefinition() =>
        SearchDefinition ?? throw new MongoDBConfigurationException(
            $"{nameof(SearchDefinition)} is not configured on this {nameof(MongoDBRAGIndexManager)}.");

    private void RequireHybridDefinitions()
    {
        if (VectorDefinition is null || SearchDefinition is null)
        {
            throw new MongoDBConfigurationException(
                $"{nameof(MongoDBSearchMode.HybridRrf)} requires both {nameof(VectorDefinition)} and " +
                $"{nameof(SearchDefinition)} to be configured on this {nameof(MongoDBRAGIndexManager)}.");
        }
    }

    private Exception MapInspectionException(MongoException exception) =>
        MongoDBSearchIndexes.IsUnauthorized(exception)
            ? new MongoDBIndexPrivilegeException("Not authorized to inspect Search/Vector Search indexes.", exception)
            : new MongoDBCapabilityException(
                "Unable to inspect Search/Vector Search indexes; the deployment type or driver/server version " +
                "may not support $listSearchIndexes.",
                exception);

    private static Exception MapMutationException(MongoException exception, string indexName, string operation) =>
        MongoDBSearchIndexes.IsUnauthorized(exception)
            ? new MongoDBIndexPrivilegeException(
                $"Not authorized to {operation} index '{indexName}'.", exception)
            : new MongoDBPersistenceException($"MongoDB RAG index {operation} failed for '{indexName}'.", exception);

    private static (OwnedResource<IMongoClient> Client, IMongoCollection<BsonDocument> Collection) ConnectClient(
        string connectionString,
        string databaseName,
        string collectionName)
    {
        string validDatabaseName = RequireText(databaseName, nameof(databaseName));
        string validCollectionName = RequireText(collectionName, nameof(collectionName));
        OwnedResource<IMongoClient> client = MongoClientFactory.FromConnectionString(connectionString);
        try
        {
            IMongoCollection<BsonDocument> collection = client.Value
                .GetDatabase(validDatabaseName)
                .GetCollection<BsonDocument>(validCollectionName);
            return (client, collection);
        }
        catch
        {
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    private static string RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MongoDBConfigurationException($"{name} must not be empty.");
        }

        return value;
    }
}
