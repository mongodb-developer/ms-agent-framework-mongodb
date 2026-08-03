using MongoDB.AgentFramework.Internal;
using MongoDB.AgentFramework.Internal.IndexManagement;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Search;

namespace MongoDB.AgentFramework;

/// <summary>
/// An explicit, feature-specific facade over Memory's Vector Search index (docs/spec/features/index-management.md's
/// index-management interface, kept in the runtime package per ADR 0016), independently constructible from a
/// collection and a <see cref="MongoDBVectorSearchIndexDefinition"/> without requiring a full
/// <see cref="MongoDBMemoryProvider"/> or an embedding generator -- demonstrating the "provisioner" role a
/// least-privilege deployment keeps separate from the "runtime" role <see cref="MongoDBMemoryProvider"/> plays (ADR
/// 0006). Every retrieval method (<see cref="GetIndexAsync"/>, <see cref="ListIndexesAsync"/>,
/// <see cref="ValidateIndexAsync"/>) never mutates MongoDB; only <see cref="EnsureIndexAsync"/>,
/// <see cref="UpdateIndexAsync"/>, and <see cref="DropIndexAsync"/> do, and only when explicitly called.
/// </summary>
public sealed class MongoDBMemoryIndexManager : IAsyncDisposable
{
    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly OwnedResource<IMongoClient>? _client;

    /// <summary>Creates a manager over an injected database, which remains caller-owned.</summary>
    public MongoDBMemoryIndexManager(
        IMongoDatabase database,
        string collectionName,
        MongoDBVectorSearchIndexDefinition definition)
        : this(
            (database ?? throw new ArgumentNullException(nameof(database)))
                .GetCollection<BsonDocument>(RequireText(collectionName, nameof(collectionName))),
            definition)
    {
    }

    /// <summary>Creates a manager over an injected collection, which remains caller-owned.</summary>
    public MongoDBMemoryIndexManager(
        IMongoCollection<BsonDocument> collection,
        MongoDBVectorSearchIndexDefinition definition)
    {
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
    }

    /// <summary>Creates a manager over an injected client, which remains caller-owned.</summary>
    public MongoDBMemoryIndexManager(
        IMongoClient client,
        string databaseName,
        string collectionName,
        MongoDBVectorSearchIndexDefinition definition)
        : this(
            (client ?? throw new ArgumentNullException(nameof(client)))
                .GetDatabase(RequireText(databaseName, nameof(databaseName))),
            collectionName,
            definition)
    {
    }

    /// <summary>
    /// Creates a manager-owned client from a connection string, for standalone provisioning tooling (for example
    /// a deployment pipeline step) that runs under a distinct, more privileged identity than the runtime
    /// <see cref="MongoDBMemoryProvider"/> connects with (docs/spec/features/index-management.md's least-privilege
    /// table).
    /// </summary>
    public MongoDBMemoryIndexManager(
        string connectionString,
        string databaseName,
        string collectionName,
        MongoDBVectorSearchIndexDefinition definition)
        : this(connectionString, databaseName, collectionName, definition, clientFactory: null)
    {
    }

    /// <summary>
    /// Test-only seam mirroring <see cref="MongoClientFactory.FromConnectionString"/>'s existing
    /// <c>clientFactory</c> override. It exists solely so tests can substitute the underlying
    /// <see cref="IMongoClient"/> and prove that a construction failure occurring after the owned client is
    /// created (for example resolving the database/collection) still disposes it; it is internal because it is
    /// not part of the public surface.
    /// </summary>
    internal MongoDBMemoryIndexManager(
        string connectionString,
        string databaseName,
        string collectionName,
        MongoDBVectorSearchIndexDefinition definition,
        Func<string, IMongoClient>? clientFactory)
        : this(Connect(connectionString, databaseName, collectionName, definition, clientFactory))
    {
    }

    private MongoDBMemoryIndexManager(
        (OwnedResource<IMongoClient> Client, IMongoCollection<BsonDocument> Collection, MongoDBVectorSearchIndexDefinition Definition) connected)
        : this(connected.Collection, connected.Definition)
    {
        _client = connected.Client;
    }

    /// <summary>Gets whether the manager owns its MongoDB client.</summary>
    public bool OwnsClient => _client?.OwnsValue is true;

    /// <summary>Gets the expected Vector Search index definition this manager validates/ensures against.</summary>
    public MongoDBVectorSearchIndexDefinition Definition { get; }

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

    /// <summary>Inspects the configured index, returning <see langword="null"/> if it does not exist.</summary>
    public async Task<MongoDBIndexInfo?> GetIndexAsync(CancellationToken cancellationToken = default)
    {
        BsonDocument? index = await FindAsync(cancellationToken).ConfigureAwait(false);
        return index is null ? null : ToIndexInfo(index);
    }

    /// <summary>
    /// Validates the configured index against <see cref="Definition"/> without ever mutating MongoDB. Comparison
    /// is semantic and order-insensitive (docs/spec/features/index-management.md).
    /// </summary>
    /// <param name="requireReady">When <see langword="true"/> (the default), also requires <c>READY</c>/queryable status.</param>
    /// <param name="cancellationToken">A token used to cancel the check.</param>
    /// <exception cref="MongoDBIndexMissingException">The configured index does not exist.</exception>
    /// <exception cref="MongoDBIndexMismatchException">The index does not match <see cref="Definition"/>.</exception>
    /// <exception cref="MongoDBIndexNotReadyException"><paramref name="requireReady"/> is <see langword="true"/> and the index is not queryable.</exception>
    public async Task<MongoDBIndexComparison> ValidateIndexAsync(
        bool requireReady = true,
        CancellationToken cancellationToken = default)
    {
        BsonDocument index = await RequireIndexAsync(cancellationToken).ConfigureAwait(false);
        return Validate(index, requireReady);
    }

    /// <summary>
    /// Creates the configured index. Fails immediately if it already exists (docs/spec/features/index-management.md
    /// lists <c>create index</c> as distinct from <c>ensure expected definition</c>): unlike
    /// <see cref="EnsureIndexAsync"/>, a caller that explicitly asked to create-only is told when there was
    /// already something there instead of silently proceeding. After creation (including if a concurrent creator
    /// won a race to create the same index first), this re-inspects the index and validates it actually matches
    /// <see cref="Definition"/> before returning.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel creation.</param>
    /// <exception cref="MongoDBIndexAlreadyExistsException">The configured index already exists.</exception>
    /// <exception cref="MongoDBIndexMismatchException">The created index does not match <see cref="Definition"/>.</exception>
    /// <exception cref="MongoDBIndexFailedException">The created index reports a terminal build failure.</exception>
    /// <exception cref="MongoDBIndexPrivilegeException">The connected identity lacks index-creation privileges.</exception>
    public async Task<MongoDBIndexInfo> CreateIndexAsync(CancellationToken cancellationToken = default)
    {
        BsonDocument index = await MongoDBSearchIndexes.CreateOnlyAsync(
            _collection.SearchIndexes,
            Definition.IndexName,
            SearchIndexType.VectorSearch,
            VectorSearchIndexEquivalence.BuildDefinition(Definition),
            index => Validate(index, requireReady: false),
            MapAlreadyExistsException,
            MapCreateException,
            MapInspectionException,
            cancellationToken).ConfigureAwait(false);
        return ToIndexInfo(index);
    }

    /// <summary>
    /// Creates the configured index if missing, or updates it if an existing index does not match
    /// <see cref="Definition"/> (docs/spec/features/index-management.md's <c>ensure expected definition</c>
    /// operation: explicit create/update plus optional bounded polling), then optionally waits for it to become
    /// queryable. A concurrent caller's create racing this one to the same end state is a successful no-op. This
    /// never treats a terminal <c>Failed</c> build as something to automatically repair -- see
    /// <see cref="MongoDBIndexFailedException"/> -- and, regardless of <paramref name="waitUntilReady"/>, always
    /// re-inspects and validates the index's final state after any create/update attempt (including a create that
    /// raced a concurrent caller to an "already exists" no-op), so a rival concurrent caller having created an
    /// incompatible definition is still caught rather than silently accepted.
    /// </summary>
    /// <param name="waitUntilReady">When <see langword="true"/>, polls with bounded exponential backoff until queryable.</param>
    /// <param name="timeout">The bounded polling deadline. Defaults to 60 seconds.</param>
    /// <param name="pollInterval">The initial polling interval, doubling up to a 30-second cap. Defaults to 1 second.</param>
    /// <param name="cancellationToken">A token used to cancel creation, update, and polling.</param>
    /// <exception cref="MongoDBIndexMismatchException">The final index still does not match <see cref="Definition"/>.</exception>
    /// <exception cref="MongoDBIndexFailedException">The index reports a terminal build failure.</exception>
    /// <exception cref="MongoDBIndexPrivilegeException">The connected identity lacks index-creation/update privileges.</exception>
    /// <exception cref="MongoDBTimeoutException"><paramref name="waitUntilReady"/> is <see langword="true"/> and the deadline elapsed before the index became queryable.</exception>
    public async Task<MongoDBIndexInfo> EnsureIndexAsync(
        bool waitUntilReady = false,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        BsonDocument index = await MongoDBSearchIndexes.EnsureAsync(
            _collection.SearchIndexes,
            Definition.IndexName,
            SearchIndexType.VectorSearch,
            VectorSearchIndexEquivalence.BuildDefinition(Definition),
            index => VectorSearchIndexEquivalence.Compare(MongoDBSearchIndexes.GetDefinition(index), Definition).IsCompatible,
            index => Validate(index, requireReady: false),
            MapCreateException,
            MapUpdateException,
            MapInspectionException,
            cancellationToken).ConfigureAwait(false);

        return waitUntilReady
            ? await WaitUntilReadyAsync(timeout, pollInterval, cancellationToken).ConfigureAwait(false)
            : ToIndexInfo(index);
    }

    /// <summary>
    /// Replaces the configured index's definition in place (the state machine's explicit <c>Ready -&gt; Building</c>
    /// transition). The index must already exist; this never creates one.
    /// </summary>
    /// <exception cref="MongoDBIndexMissingException">The configured index does not exist.</exception>
    /// <exception cref="MongoDBIndexPrivilegeException">The connected identity lacks index-update privileges.</exception>
    public async Task UpdateIndexAsync(CancellationToken cancellationToken = default)
    {
        await RequireIndexAsync(cancellationToken).ConfigureAwait(false);
        await MongoDBSearchIndexes.UpdateAsync(
            _collection.SearchIndexes,
            Definition.IndexName,
            VectorSearchIndexEquivalence.BuildDefinition(Definition),
            MapUpdateException,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Polls with bounded exponential backoff (docs/spec/features/index-management.md's polling requirements)
    /// until the configured index reports <c>READY</c>/queryable, returning its final inspected snapshot.
    /// </summary>
    /// <param name="timeout">The bounded polling deadline. Defaults to 60 seconds.</param>
    /// <param name="pollInterval">The initial polling interval, doubling up to a 30-second cap. Defaults to 1 second.</param>
    /// <param name="cancellationToken">A token checked before every attempt and delay.</param>
    /// <exception cref="MongoDBTimeoutException">The deadline elapsed before the index became queryable.</exception>
    public Task<MongoDBIndexInfo> WaitUntilReadyAsync(
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default) =>
        BoundedExponentialPolling.RunAsync(
            async token =>
            {
                BsonDocument index = await RequireIndexAsync(token).ConfigureAwait(false);
                Validate(index, requireReady: true);
                return ToIndexInfo(index);
            },
            static exception => exception is MongoDBIndexNotReadyException or MongoDBIndexMissingException,
            exception => new MongoDBTimeoutException(
                $"Vector Search index '{Definition.IndexName}' was not ready before timeout.",
                exception),
            timeout ?? TimeSpan.FromSeconds(60),
            pollInterval ?? TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(30),
            cancellationToken);

    /// <summary>
    /// Drops the configured index. Already being absent (never created, or a concurrent drop) is a successful
    /// no-op.
    /// </summary>
    /// <exception cref="MongoDBIndexPrivilegeException">The connected identity lacks index-drop privileges.</exception>
    public Task DropIndexAsync(CancellationToken cancellationToken = default) =>
        MongoDBSearchIndexes.DropAsync(
            _collection.SearchIndexes,
            Definition.IndexName,
            MapDropException,
            cancellationToken);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<BsonDocument> RequireIndexAsync(CancellationToken cancellationToken)
    {
        BsonDocument? index = await FindAsync(cancellationToken).ConfigureAwait(false);
        return index ?? throw new MongoDBIndexMissingException(
            $"Vector Search index '{Definition.IndexName}' does not exist; create it explicitly.");
    }

    private Task<BsonDocument?> FindAsync(CancellationToken cancellationToken) =>
        MongoDBSearchIndexes.FindAsync(
            _collection.SearchIndexes,
            Definition.IndexName,
            MapInspectionException,
            cancellationToken);

    private MongoDBIndexComparison Validate(BsonDocument index, bool requireReady) =>
        VectorSearchIndexEquivalence.Validate(index, Definition, requireReady);

    private MongoDBIndexInfo ToIndexInfo(BsonDocument index) =>
        new(
            index.GetValue("name", Definition.IndexName).AsString,
            index.GetValue("type", "vectorSearch").AsString,
            MongoDBSearchIndexes.Classify(index),
            index.GetValue("queryable", false).ToBoolean(),
            index.GetValue("status", "").AsString,
            MongoDBSearchIndexes.GetDefinition(index));

    private Exception MapInspectionException(MongoException exception) =>
        MongoDBSearchIndexes.IsUnauthorized(exception)
            ? new MongoDBIndexPrivilegeException(
                $"Not authorized to inspect Vector Search index '{Definition.IndexName}'.", exception)
            : new MongoDBRetrievalException("MongoDB Memory index inspection failed.", exception);

    private Exception MapCreateException(MongoException exception) =>
        MongoDBSearchIndexes.IsUnauthorized(exception)
            ? new MongoDBIndexPrivilegeException(
                $"Not authorized to create Vector Search index '{Definition.IndexName}'.", exception)
            : new MongoDBPersistenceException("MongoDB Memory index creation failed.", exception);

    private Exception MapAlreadyExistsException(Exception? raceException) =>
        raceException is null
            ? new MongoDBIndexAlreadyExistsException(
                $"Vector Search index '{Definition.IndexName}' already exists; use UpdateIndexAsync or " +
                "EnsureIndexAsync instead.")
            : new MongoDBIndexAlreadyExistsException(
                $"Vector Search index '{Definition.IndexName}' already exists; use UpdateIndexAsync or " +
                "EnsureIndexAsync instead.",
                raceException);

    private Exception MapUpdateException(MongoException exception) =>
        MongoDBSearchIndexes.IsUnauthorized(exception)
            ? new MongoDBIndexPrivilegeException(
                $"Not authorized to update Vector Search index '{Definition.IndexName}'.", exception)
            : new MongoDBPersistenceException("MongoDB Memory index update failed.", exception);

    private Exception MapDropException(MongoException exception) =>
        MongoDBSearchIndexes.IsUnauthorized(exception)
            ? new MongoDBIndexPrivilegeException(
                $"Not authorized to drop Vector Search index '{Definition.IndexName}'.", exception)
            : new MongoDBPersistenceException("MongoDB Memory index drop failed.", exception);

    private static (OwnedResource<IMongoClient> Client, IMongoCollection<BsonDocument> Collection) ConnectClient(
        string connectionString,
        string databaseName,
        string collectionName,
        Func<string, IMongoClient>? clientFactory)
    {
        string validDatabaseName = RequireText(databaseName, nameof(databaseName));
        string validCollectionName = RequireText(collectionName, nameof(collectionName));
        OwnedResource<IMongoClient> client = MongoClientFactory.FromConnectionString(connectionString, clientFactory);
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

    /// <summary>
    /// Validates every constructor argument that does not require a MongoDB client -- including
    /// <paramref name="definition"/> -- entirely before creating an owned client. If this validated first and a
    /// chained constructor validated <paramref name="definition"/> afterward instead, a null/invalid
    /// <paramref name="definition"/> would throw only after <see cref="MongoClientFactory.FromConnectionString"/>
    /// had already created a client, and since no <see cref="MongoDBMemoryIndexManager"/> instance would ever
    /// exist to dispose it, that client would leak. Resolving the database/collection can still throw after the
    /// client exists (a real network-dependent step); <see cref="ConnectClient"/> disposes the client itself in
    /// that case, since it runs before any instance exists either.
    /// </summary>
    private static (OwnedResource<IMongoClient> Client, IMongoCollection<BsonDocument> Collection, MongoDBVectorSearchIndexDefinition Definition) Connect(
        string connectionString,
        string databaseName,
        string collectionName,
        MongoDBVectorSearchIndexDefinition definition,
        Func<string, IMongoClient>? clientFactory)
    {
        MongoDBVectorSearchIndexDefinition validDefinition =
            definition ?? throw new ArgumentNullException(nameof(definition));
        (OwnedResource<IMongoClient> client, IMongoCollection<BsonDocument> collection) =
            ConnectClient(connectionString, databaseName, collectionName, clientFactory);
        return (client, collection, validDefinition);
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
