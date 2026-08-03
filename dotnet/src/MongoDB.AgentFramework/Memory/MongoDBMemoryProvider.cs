using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.AgentFramework.Internal;
using MongoDB.AgentFramework.Internal.IndexManagement;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.AgentFramework;

/// <summary>
/// Stores scoped conversation messages and supplies semantic Memory through the
/// public Agent Framework context-provider lifecycle.
/// </summary>
public sealed class MongoDBMemoryProvider : AIContextProvider, IAsyncDisposable
{
    private static readonly HashSet<string> AllowedRoles =
        new(["user", "assistant", "system"], StringComparer.Ordinal);
    private static readonly IReadOnlyList<string> ProviderStateKeys =
        ["mongodb_memory_pending_batches"];
    private const int RetryStateVersion = 1;

    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly Func<AgentSession?, State> _stateFactory;
    private readonly MongoDBMemoryProviderOptions _options;
    private readonly int _vectorDimensions;
    private readonly MongoDBVectorSearchIndexDefinition _indexDefinition;
    private readonly OwnedResource<IMongoClient>? _client;
    private readonly ILogger<MongoDBMemoryProvider> _logger;
    private readonly object _retryLock = new();
    private readonly RetryState _directRetryState = new();
    private readonly HashSet<string> _activeRetryAttempts = [];

    /// <summary>Defines immutable storage and retrieval scopes for an invocation.</summary>
    public sealed class State
    {
        /// <summary>Creates state. Storage defaults to the retrieval scope.</summary>
        public State(
            MongoDBMemoryScope searchScope,
            MongoDBMemoryScope? storageScope = null)
        {
            SearchScope = searchScope ?? throw new ArgumentNullException(nameof(searchScope));
            StorageScope = storageScope ?? searchScope;
        }

        /// <summary>Gets the retrieval authorization scope.</summary>
        public MongoDBMemoryScope SearchScope { get; }

        /// <summary>Gets the persistence authorization scope.</summary>
        public MongoDBMemoryScope StorageScope { get; }
    }

    /// <summary>Creates a provider over an injected database, which remains caller-owned.</summary>
    public MongoDBMemoryProvider(
        IMongoDatabase database,
        string collectionName,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int vectorDimensions,
        Func<AgentSession?, State> stateFactory,
        MongoDBMemoryProviderOptions? options = null,
        ILogger<MongoDBMemoryProvider>? logger = null)
        : this(
            (database ?? throw new ArgumentNullException(nameof(database)))
                .GetCollection<BsonDocument>(
                    MongoDBMemoryProviderOptions.RequireText(
                        collectionName,
                        nameof(collectionName))),
            embeddingGenerator,
            vectorDimensions,
            stateFactory,
            options,
            logger)
    {
    }

    /// <summary>Creates a provider over an injected collection, which remains caller-owned.</summary>
    public MongoDBMemoryProvider(
        IMongoCollection<BsonDocument> collection,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int vectorDimensions,
        Func<AgentSession?, State> stateFactory,
        MongoDBMemoryProviderOptions? options = null,
        ILogger<MongoDBMemoryProvider>? logger = null)
        : this(
            collection,
            new ValidatedOptions<MongoDBMemoryProviderOptions>(PrepareOptions(options, vectorDimensions)),
            embeddingGenerator,
            vectorDimensions,
            stateFactory,
            logger)
    {
    }

    /// <summary>
    /// Core constructor accepting an already-validated, independent options snapshot (produced exactly once by
    /// <see cref="PrepareOptions"/>), so this never re-copies or re-validates caller-supplied options (or
    /// <paramref name="vectorDimensions"/> again) a second time. This matters for the connection-string-owned-client
    /// family below: if options/vectorDimensions were validated again after the owned client already existed and
    /// that later validation ever threw, the client would leak, since no <see cref="MongoDBMemoryProvider"/>
    /// instance would ever exist to dispose it.
    /// </summary>
    private MongoDBMemoryProvider(
        IMongoCollection<BsonDocument> collection,
        ValidatedOptions<MongoDBMemoryProviderOptions> options,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int vectorDimensions,
        Func<AgentSession?, State> stateFactory,
        ILogger<MongoDBMemoryProvider>? logger)
        : base()
    {
        _options = options.Value;
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        _embeddingGenerator = embeddingGenerator ??
            throw new ArgumentNullException(nameof(embeddingGenerator));
        _stateFactory = stateFactory ?? throw new ArgumentNullException(nameof(stateFactory));
        _vectorDimensions = vectorDimensions;
        _logger = logger ?? NullLogger<MongoDBMemoryProvider>.Instance;
        _indexDefinition = new MongoDBVectorSearchIndexDefinition(
            _options.IndexName,
            _options.VectorFieldName,
            _vectorDimensions,
            _options.Similarity,
            ["application_id", "agent_id", "user_id", "session_id"]);
    }

    /// <summary>Creates a provider over an injected client, which remains caller-owned.</summary>
    public MongoDBMemoryProvider(
        IMongoClient client,
        string databaseName,
        string collectionName,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int vectorDimensions,
        Func<AgentSession?, State> stateFactory,
        MongoDBMemoryProviderOptions? options = null,
        ILogger<MongoDBMemoryProvider>? logger = null)
        : this(
            (client ?? throw new ArgumentNullException(nameof(client))).GetDatabase(
                MongoDBMemoryProviderOptions.RequireText(databaseName, nameof(databaseName))),
            collectionName,
            embeddingGenerator,
            vectorDimensions,
            stateFactory,
            options,
            logger)
    {
    }

    /// <summary>Creates a provider-owned client from a connection string.</summary>
    public MongoDBMemoryProvider(
        string connectionString,
        string databaseName,
        string collectionName,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int vectorDimensions,
        Func<AgentSession?, State> stateFactory,
        MongoDBMemoryProviderOptions? options = null,
        ILogger<MongoDBMemoryProvider>? logger = null)
        : this(
            connectionString,
            databaseName,
            collectionName,
            embeddingGenerator,
            vectorDimensions,
            stateFactory,
            options,
            logger,
            clientFactory: null)
    {
    }

    /// <summary>
    /// Test-only seam mirroring <see cref="MongoClientFactory.FromConnectionString"/>'s existing
    /// <c>clientFactory</c> override. It exists solely so tests can substitute the underlying
    /// <see cref="IMongoClient"/> and prove that a validation/construction failure occurring after the owned
    /// client is created still disposes it; it is internal because it is not part of the public surface.
    /// </summary>
    internal MongoDBMemoryProvider(
        string connectionString,
        string databaseName,
        string collectionName,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int vectorDimensions,
        Func<AgentSession?, State> stateFactory,
        MongoDBMemoryProviderOptions? options,
        ILogger<MongoDBMemoryProvider>? logger,
        Func<string, IMongoClient>? clientFactory)
        : this(
            Connect(
                connectionString,
                databaseName,
                collectionName,
                embeddingGenerator,
                vectorDimensions,
                stateFactory,
                options,
                clientFactory),
            embeddingGenerator,
            vectorDimensions,
            stateFactory,
            logger)
    {
    }

    private MongoDBMemoryProvider(
        (OwnedResource<IMongoClient> Client,
         IMongoCollection<BsonDocument> Collection,
         MongoDBMemoryProviderOptions Options) connected,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int vectorDimensions,
        Func<AgentSession?, State> stateFactory,
        ILogger<MongoDBMemoryProvider>? logger)
        : this(
            connected.Collection,
            new ValidatedOptions<MongoDBMemoryProviderOptions>(connected.Options),
            embeddingGenerator,
            vectorDimensions,
            stateFactory,
            logger)
    {
        _client = connected.Client;
    }

    /// <summary>
    /// Validates every constructor argument that does not require a MongoDB client -- including
    /// <paramref name="options"/> and <paramref name="vectorDimensions"/> (via <see cref="PrepareOptions"/>),
    /// <paramref name="embeddingGenerator"/>, and <paramref name="stateFactory"/> -- entirely before creating an
    /// owned client, and disposes that client if the subsequent database/collection resolution step fails.
    /// Mirrors <see cref="MongoDBRAGProvider"/>'s equivalent construction-exception-safety design.
    /// </summary>
    private static (OwnedResource<IMongoClient> Client,
        IMongoCollection<BsonDocument> Collection,
        MongoDBMemoryProviderOptions Options) Connect(
        string connectionString,
        string databaseName,
        string collectionName,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int vectorDimensions,
        Func<AgentSession?, State> stateFactory,
        MongoDBMemoryProviderOptions? options,
        Func<string, IMongoClient>? clientFactory)
    {
        MongoDBMemoryProviderOptions validated = PrepareOptions(options, vectorDimensions);
        ArgumentNullException.ThrowIfNull(embeddingGenerator);
        ArgumentNullException.ThrowIfNull(stateFactory);
        string validDatabaseName = MongoDBMemoryProviderOptions.RequireText(databaseName, nameof(databaseName));
        string validCollectionName =
            MongoDBMemoryProviderOptions.RequireText(collectionName, nameof(collectionName));

        OwnedResource<IMongoClient> client =
            MongoClientFactory.FromConnectionString(connectionString, clientFactory);
        try
        {
            IMongoCollection<BsonDocument> collection = client.Value
                .GetDatabase(validDatabaseName)
                .GetCollection<BsonDocument>(validCollectionName);
            return (client, collection, validated);
        }
        catch
        {
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }
    }

    /// <summary>
    /// Validates <paramref name="vectorDimensions"/> and produces a single independent, validated options
    /// snapshot via <see cref="MongoDBMemoryProviderOptions.Copy"/>. Called exactly once per construction path
    /// (whether or not an owned client is created), so a caller-supplied
    /// <see cref="MongoDBMemoryProviderOptions"/> is never copied/validated twice.
    /// </summary>
    private static MongoDBMemoryProviderOptions PrepareOptions(
        MongoDBMemoryProviderOptions? options,
        int vectorDimensions)
    {
        MongoDBMemoryProviderOptions validated = (options ?? new MongoDBMemoryProviderOptions()).Copy();
        if (vectorDimensions <= 0)
        {
            throw new MongoDBConfigurationException(
                "vectorDimensions must be a positive integer.");
        }

        return validated;
    }

    /// <summary>Gets whether the provider owns its MongoDB client.</summary>
    public bool OwnsClient => _client?.OwnsValue is true;

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys => ProviderStateKeys;

    /// <summary>Batch-embeds and stores eligible messages under an explicit scope.</summary>
    public Task<int> StoreAsync(
        IEnumerable<ChatMessage> messages,
        MongoDBMemoryScope scope,
        CancellationToken cancellationToken = default) =>
        WithDeadlineAsync(
            token => StoreCoreAsync(messages, scope, sessionState: null, token),
            _options.PersistenceTimeout,
            "MongoDB Memory persistence deadline exceeded.",
            cancellationToken);

    private Task<int> StoreFrameworkAsync(
        IEnumerable<ChatMessage> messages,
        MongoDBMemoryScope scope,
        AgentSessionStateBag? sessionState,
        CancellationToken cancellationToken) =>
        WithDeadlineAsync(
            token => StoreCoreAsync(messages, scope, sessionState, token),
            _options.PersistenceTimeout,
            "MongoDB Memory persistence deadline exceeded.",
            cancellationToken);

    private async Task<int> StoreCoreAsync(
        IEnumerable<ChatMessage> messages,
        MongoDBMemoryScope scope,
        AgentSessionStateBag? sessionState,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(scope);
        ChatMessage[] eligible = messages.Where(IsEligible).ToArray();
        if (eligible.Length == 0)
        {
            return 0;
        }

        float[][] vectors = await EmbedAsync(
            eligible.Select(static message => message.Text!),
            cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyDictionary<string, string> scopeFields = scope.ToFields();
        string fingerprint = BatchFingerprint(eligible, scopeFields);
        RetryAttempt? retryAttempt = eligible.Any(
            static message => string.IsNullOrWhiteSpace(message.MessageId))
            ? BeginRetryAttempt(fingerprint, sessionState)
            : null;
        Dictionary<string, string> retryIds = retryAttempt?.Ids ?? [];
        var documents = new BsonDocument[eligible.Length];
        for (int index = 0; index < eligible.Length; index++)
        {
            ChatMessage message = eligible[index];
            string id = CreateMemoryId(message, scopeFields, index, retryIds);
            var document = new BsonDocument
            {
                { "_id", id },
                { "role", message.Role.Value },
                { "content", message.Text },
                { "created_at", now.UtcDateTime },
            };
            AddScope(document, scopeFields);
            SetFieldPath(document, _options.VectorFieldName, new BsonArray(vectors[index]));
            if (!string.IsNullOrWhiteSpace(message.MessageId))
            {
                document.Add("message_id", message.MessageId);
            }

            if (!string.IsNullOrWhiteSpace(message.AuthorName))
            {
                document.Add("author_name", message.AuthorName);
            }

            if (_options.Retention is { } retention)
            {
                document.Add("expires_at", now.Add(retention).UtcDateTime);
            }

            documents[index] = document;
        }

        if (retryAttempt is not null)
        {
            PersistRetryAttempt(retryAttempt, sessionState);
        }

        try
        {
            await _collection.InsertManyAsync(
                documents,
                new InsertManyOptions { IsOrdered = false },
                cancellationToken).ConfigureAwait(false);
            FinishRetryAttempt(retryAttempt, sessionState, succeeded: true);
            return documents.Length;
        }
        catch (OperationCanceledException)
        {
            FinishRetryAttempt(retryAttempt, sessionState, succeeded: false);
            throw;
        }
        catch (MongoBulkWriteException<BsonDocument> exception)
            when (exception.WriteErrors.Count > 0 &&
                  exception.WriteErrors.All(static error => error.Code == 11000) &&
                  exception.WriteConcernError is null)
        {
            FinishRetryAttempt(retryAttempt, sessionState, succeeded: true);
            return documents.Length - exception.WriteErrors.Count;
        }
        catch (MongoException exception)
        {
            FinishRetryAttempt(retryAttempt, sessionState, succeeded: false);
            throw new MongoDBPersistenceException(
                "MongoDB Memory persistence failed.",
                exception);
        }
    }

    /// <summary>Searches Memory with mandatory scope filters inside <c>$vectorSearch</c>.</summary>
    public Task<IReadOnlyList<MongoDBMemorySearchResult>> SearchAsync(
        string query,
        MongoDBMemoryScope scope,
        int? maxResults = null,
        bool? exact = null,
        CancellationToken cancellationToken = default) =>
        WithDeadlineAsync(
            token => SearchCoreAsync(query, scope, maxResults, exact, token),
            _options.RetrievalTimeout,
            "MongoDB Memory retrieval deadline exceeded.",
            cancellationToken);

    private async Task<IReadOnlyList<MongoDBMemorySearchResult>> SearchCoreAsync(
        string query,
        MongoDBMemoryScope scope,
        int? maxResults,
        bool? exact,
        CancellationToken cancellationToken)
    {
        MongoDBMemoryProviderOptions.RequireText(query, nameof(query));
        ArgumentNullException.ThrowIfNull(scope);
        int limit = maxResults ?? _options.MaxResults;
        if (limit is < 1 or > 100)
        {
            throw new MongoDBConfigurationException("maxResults must be between 1 and 100.");
        }

        float[] vector = (await EmbedAsync([query], cancellationToken).ConfigureAwait(false))[0];
        bool useExact = exact ?? _options.Exact;
        var vectorSearch = new BsonDocument
        {
            { "index", _options.IndexName },
            { "path", _options.VectorFieldName },
            { "queryVector", new BsonArray(vector) },
            { "limit", limit },
            { "filter", ScopeDocument(scope) },
        };
        if (useExact)
        {
            vectorSearch.Add("exact", true);
        }
        else
        {
            vectorSearch.Add("numCandidates", Math.Max(_options.NumCandidates, limit));
        }

        BsonDocument[] stages =
        [
            new("$vectorSearch", vectorSearch),
            new("$project", new BsonDocument
            {
                { "_id", 1 }, { "role", 1 }, { "message_id", 1 },
                { "author_name", 1 }, { "session_id", 1 }, { "content", 1 },
                { "score", new BsonDocument("$meta", "vectorSearchScore") },
            }),
        ];
        try
        {
            using IAsyncCursor<BsonDocument> cursor = await _collection
                .AggregateAsync<BsonDocument>(stages, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var results = new List<MongoDBMemorySearchResult>();
            while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                results.AddRange(cursor.Current.Select(MapSearchResult));
            }

            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MongoException exception)
        {
            throw new MongoDBRetrievalException(
                "MongoDB Memory retrieval failed.",
                exception);
        }
    }

    /// <summary>Deletes one ID only inside the mandatory authorization scope.</summary>
    public Task<long> DeleteByIdAsync(
        string memoryId,
        MongoDBMemoryScope scope,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(
            Builders<BsonDocument>.Filter.And(
                Builders<BsonDocument>.Filter.Eq("_id",
                    MongoDBMemoryProviderOptions.RequireText(memoryId, nameof(memoryId))),
                ScopeFilter(scope)),
            cancellationToken);

    /// <summary>Clears one session inside the mandatory authorization scope.</summary>
    public Task<long> ClearSessionAsync(
        string sessionId,
        MongoDBMemoryScope scope,
        CancellationToken cancellationToken = default) =>
        DeleteAsync(
            ScopeFilter(scope.WithSession(
                MongoDBMemoryProviderOptions.RequireText(sessionId, nameof(sessionId)))),
            cancellationToken);

    /// <summary>Clears a user while retaining its application or agent authorization boundary.</summary>
    public Task<long> ClearUserAsync(
        MongoDBMemoryScope scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.UserId is null || (scope.ApplicationId is null && scope.AgentId is null))
        {
            throw new MongoDBConfigurationException(
                "ClearUserAsync requires userId and applicationId or agentId.");
        }

        return DeleteAsync(ScopeFilter(scope.WithSession(null)), cancellationToken);
    }

    /// <summary>Lists bounded, content-free metadata using keyset pagination.</summary>
    public async Task<MongoDBMemoryMetadataPage> ListAsync(
        MongoDBMemoryScope scope,
        int pageSize = 50,
        string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        if (pageSize is < 1 or > 100)
        {
            throw new MongoDBConfigurationException("pageSize must be between 1 and 100.");
        }

        FilterDefinition<BsonDocument> filter = ScopeFilter(scope);
        if (cursor is not null)
        {
            filter &= Builders<BsonDocument>.Filter.Gt(
                "_id",
                MongoDBMemoryProviderOptions.RequireText(cursor, nameof(cursor)));
        }

        try
        {
            List<BsonDocument> documents = await _collection.Find(filter)
                .Project(Builders<BsonDocument>.Projection
                    .Include("_id").Include("role").Include("created_at")
                    .Include("application_id").Include("agent_id").Include("user_id")
                    .Include("session_id").Include("expires_at"))
                .Sort(Builders<BsonDocument>.Sort.Ascending("_id"))
                .Limit(pageSize + 1)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            bool hasMore = documents.Count > pageSize;
            List<MongoDBMemoryMetadata> items = documents.Take(pageSize).Select(MapMetadata).ToList();
            return new(items, hasMore ? items[^1].MemoryId : null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MongoException exception)
        {
            throw new MongoDBRetrievalException(
                "MongoDB Memory metadata listing failed.",
                exception);
        }
    }

    /// <summary>Creates the missing Vector Search index, validates it, and optionally waits.</summary>
    public async Task<string> EnsureVectorSearchIndexAsync(
        bool waitUntilReady = false,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        BsonDocument? index = await FindIndexAsync(cancellationToken).ConfigureAwait(false);
        bool created = index is null;
        if (index is null)
        {
            await MongoDBSearchIndexes.CreateAsync(
                _collection.SearchIndexes,
                new CreateSearchIndexModel(
                    _options.IndexName,
                    SearchIndexType.VectorSearch,
                    VectorSearchIndexEquivalence.BuildDefinition(_indexDefinition)),
                MapCreateException,
                cancellationToken).ConfigureAwait(false);
        }

        if (!waitUntilReady)
        {
            index = await FindIndexAsync(cancellationToken).ConfigureAwait(false);
            if (index is not null)
            {
                ValidateIndex(index, requireReady: false);
            }

            return _options.IndexName;
        }

        TimeSpan deadline = timeout ?? TimeSpan.FromSeconds(60);
        TimeSpan delay = pollInterval ?? TimeSpan.FromSeconds(1);

        // Delegates to the shared BoundedExponentialPolling primitive (rather than this method's own previous
        // hand-rolled loop) so this legacy path gets the same per-attempt cancellation-linked deadline as every
        // other index-readiness wait in this package: a hung MongoDB call inside ValidateVectorSearchIndexAsync
        // can no longer keep this loop alive past the deadline, even if that call never itself observes
        // cancellation promptly. initialInterval and maxInterval are both set to the caller's single
        // pollInterval, preserving this method's original fixed (non-doubling) polling cadence exactly.
        return await BoundedExponentialPolling.RunAsync(
            async token =>
            {
                await ValidateVectorSearchIndexAsync(true, token).ConfigureAwait(false);
                return _options.IndexName;
            },
            exception => exception is MongoDBIndexNotReadyException ||
                (created && exception is MongoDBIndexMissingException),
            exception => new MongoDBTimeoutException(
                $"Vector Search index '{_options.IndexName}' was not ready before timeout.",
                exception),
            deadline,
            delay,
            delay,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Validates the Vector Search index without mutating MongoDB.</summary>
    public async Task ValidateVectorSearchIndexAsync(
        bool requireReady = true,
        CancellationToken cancellationToken = default)
    {
        BsonDocument index = await RequireIndexAsync(cancellationToken).ConfigureAwait(false);
        ValidateIndex(index, requireReady);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken)
    {
        State state = _stateFactory(context.Session);
        string query = string.Join(
            " ",
            (context.AIContext.Messages ?? [])
                .Select(static message => message.Text)
                .Where(static text => !string.IsNullOrWhiteSpace(text)));
        if (string.IsNullOrWhiteSpace(query))
        {
            return new AIContext();
        }

        try
        {
            IReadOnlyList<MongoDBMemorySearchResult> results = await SearchAsync(
                query,
                state.SearchScope,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new AIContext
            {
                Instructions = results.Count == 0 ? null : _options.ContextPrompt,
                Messages = results.Select(static result => result.Message),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MongoDBRetrievalException)
        {
            _logger.LogWarning("MongoDB Memory adapter retrieval failed.");
            return new AIContext();
        }
        catch (MongoDBEmbeddingException)
        {
            _logger.LogWarning("MongoDB Memory adapter retrieval failed.");
            return new AIContext();
        }
        catch (MongoDBTimeoutException)
        {
            _logger.LogWarning("MongoDB Memory adapter retrieval failed.");
            return new AIContext();
        }
    }

    /// <inheritdoc />
    protected override async ValueTask StoreAIContextAsync(
        InvokedContext context,
        CancellationToken cancellationToken)
    {
        State state = _stateFactory(context.Session);
        IEnumerable<ChatMessage> messages =
            context.RequestMessages.Concat(context.ResponseMessages ?? []);
        try
        {
            await StoreFrameworkAsync(
                messages,
                state.StorageScope,
                context.Session?.StateBag,
                cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MongoDBPersistenceException)
        {
            if (_options.PersistenceFailFast)
            {
                throw;
            }

            _logger.LogWarning("MongoDB Memory adapter persistence failed.");
        }
        catch (MongoDBEmbeddingException)
        {
            if (_options.PersistenceFailFast)
            {
                throw;
            }

            _logger.LogWarning("MongoDB Memory adapter persistence failed.");
        }
        catch (MongoDBTimeoutException)
        {
            if (_options.PersistenceFailFast)
            {
                throw;
            }

            _logger.LogWarning("MongoDB Memory adapter persistence failed.");
        }
    }

    private async Task<float[][]> EmbedAsync(
        IEnumerable<string> values,
        CancellationToken cancellationToken)
    {
        string[] inputs = values.ToArray();
        try
        {
            GeneratedEmbeddings<Embedding<float>> generated =
                await _embeddingGenerator.GenerateAsync(
                    inputs,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            Embedding<float>[] embeddings = generated.ToArray();
            if (embeddings.Length != inputs.Length)
            {
                throw new MongoDBEmbeddingException(
                    $"Embedding count {embeddings.Length} does not match input count {inputs.Length}.");
            }

            return embeddings.Select(embedding =>
            {
                float[] vector = embedding.Vector.ToArray();
                if (vector.Length != _vectorDimensions ||
                    vector.Any(static value => !float.IsFinite(value)))
                {
                    throw new MongoDBEmbeddingException(
                        $"Each embedding must contain {_vectorDimensions} finite values.");
                }

                return vector;
            }).ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MongoDBEmbeddingException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new MongoDBEmbeddingException("Embedding generation failed.", exception);
        }
    }

    private async Task<long> DeleteAsync(
        FilterDefinition<BsonDocument> filter,
        CancellationToken cancellationToken)
    {
        try
        {
            DeleteResult result = await _collection.DeleteManyAsync(filter, cancellationToken)
                .ConfigureAwait(false);
            if (!result.IsAcknowledged)
            {
                throw new MongoDBPersistenceException(
                    "MongoDB Memory deletion was not acknowledged.");
            }

            return result.DeletedCount;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MongoException exception)
        {
            throw new MongoDBPersistenceException(
                "MongoDB Memory deletion failed.",
                exception);
        }
    }

    private async Task<BsonDocument> RequireIndexAsync(CancellationToken cancellationToken)
    {
        BsonDocument? index = await FindIndexAsync(cancellationToken).ConfigureAwait(false);
        return index ?? throw new MongoDBIndexMissingException(
            $"Vector Search index '{_options.IndexName}' does not exist; create it explicitly.");
    }

    private Task<BsonDocument?> FindIndexAsync(CancellationToken cancellationToken) =>
        MongoDBSearchIndexes.FindAsync(
            _collection.SearchIndexes,
            _options.IndexName,
            MapInspectionException,
            cancellationToken);

    private void ValidateIndex(BsonDocument index, bool requireReady) =>
        VectorSearchIndexEquivalence.Validate(index, _indexDefinition, requireReady);

    private Exception MapInspectionException(MongoException exception) =>
        MongoDBSearchIndexes.IsUnauthorized(exception)
            ? new MongoDBIndexPrivilegeException(
                $"Not authorized to inspect Vector Search index '{_options.IndexName}'.", exception)
            : new MongoDBRetrievalException("MongoDB Memory index inspection failed.", exception);

    private Exception MapCreateException(MongoException exception) =>
        MongoDBSearchIndexes.IsUnauthorized(exception)
            ? new MongoDBIndexPrivilegeException(
                $"Not authorized to create Vector Search index '{_options.IndexName}'.", exception)
            : new MongoDBPersistenceException("MongoDB Memory index creation failed.", exception);

    private static bool IsEligible(ChatMessage message) =>
        message is not null &&
        AllowedRoles.Contains(message.Role.Value) &&
        !string.IsNullOrWhiteSpace(message.Text) &&
        !IsProviderAttributed(message);

    private static bool IsProviderAttributed(ChatMessage message) =>
        message.AdditionalProperties?.ContainsKey("_memory_id") is true ||
        message.AdditionalProperties?.ContainsKey("source_id") is true;

    private static BsonDocument ScopeDocument(MongoDBMemoryScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var document = new BsonDocument();
        AddScope(document, scope.ToFields());
        return document;
    }

    private static FilterDefinition<BsonDocument> ScopeFilter(MongoDBMemoryScope scope)
    {
        BsonDocument document = ScopeDocument(scope);
        return new BsonDocumentFilterDefinition<BsonDocument>(document);
    }

    private static void AddScope(
        BsonDocument document,
        IReadOnlyDictionary<string, string> fields)
    {
        foreach ((string name, string value) in fields)
        {
            document.Add(name, value);
        }
    }

    private static void SetFieldPath(
        BsonDocument document,
        string path,
        BsonValue value)
    {
        string[] segments = path.Split('.');
        BsonDocument current = document;
        for (int index = 0; index < segments.Length - 1; index++)
        {
            var nested = new BsonDocument();
            current.Add(segments[index], nested);
            current = nested;
        }

        current.Add(segments[^1], value);
    }

    private static MongoDBMemorySearchResult MapSearchResult(BsonDocument document)
    {
        string role = document.GetValue("role", "").AsString;
        string content = document.GetValue("content", "").AsString;
        if (!AllowedRoles.Contains(role) || string.IsNullOrWhiteSpace(content))
        {
            throw new MongoDBMappingException(
                "Memory result requires a supported role and text content.");
        }

        string id = document.GetValue("_id", "").ToString() ?? string.Empty;
        string? sessionId = OptionalString(document, "session_id");
        var message = new ChatMessage(new ChatRole(role), content)
        {
            MessageId = OptionalString(document, "message_id"),
            AuthorName = OptionalString(document, "author_name"),
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["_memory_id"] = id,
                ["_memory_session_id"] = sessionId,
            },
        };
        return new(
            id,
            message,
            document.GetValue("score", 0.0).ToDouble(),
            sessionId);
    }

    private static MongoDBMemoryMetadata MapMetadata(BsonDocument document) =>
        new(
            document["_id"].ToString() ?? string.Empty,
            document["role"].AsString,
            new DateTimeOffset(document["created_at"].ToUniversalTime()),
            OptionalString(document, "application_id"),
            OptionalString(document, "agent_id"),
            OptionalString(document, "user_id"),
            OptionalString(document, "session_id"),
            document.TryGetValue("expires_at", out BsonValue? expires) && expires.IsValidDateTime
                ? new DateTimeOffset(expires.ToUniversalTime())
                : null);

    private static string? OptionalString(BsonDocument document, string name) =>
        document.TryGetValue(name, out BsonValue? value) && value.IsString
            ? value.AsString
            : null;

    private static string BatchFingerprint(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyDictionary<string, string> scope)
    {
        var builder = new StringBuilder();
        foreach ((string key, string value) in scope.OrderBy(static pair => pair.Key))
        {
            builder.Append(key).Append('=').Append(value).Append('\n');
        }

        for (int index = 0; index < messages.Count; index++)
        {
            ChatMessage message = messages[index];
            builder.Append(index).Append('|').Append(message.Role.Value).Append('|')
                .Append(message.MessageId).Append('|').Append(message.Text).Append('\n');
        }

        return Hash(builder.ToString());
    }

    private static string CreateMemoryId(
        ChatMessage message,
        IReadOnlyDictionary<string, string> scope,
        int ordinal,
        IDictionary<string, string> retryIds)
    {
        string stableScope = string.Join(
            "|",
            scope.OrderBy(static pair => pair.Key)
                .Select(static pair => $"{pair.Key}={pair.Value}"));
        if (!string.IsNullOrWhiteSpace(message.MessageId))
        {
            return Hash($"{stableScope}|message={message.MessageId}");
        }

        string fingerprint = Hash(
            string.Create(
                CultureInfo.InvariantCulture,
                $"{stableScope}|{message.Role.Value}|{message.Text}|{ordinal}"));
        if (!retryIds.TryGetValue(fingerprint, out string? id))
        {
            id = Guid.NewGuid().ToString();
            retryIds.Add(fingerprint, id);
        }

        return id;
    }

    private RetryAttempt BeginRetryAttempt(
        string fingerprint,
        AgentSessionStateBag? sessionState)
    {
        lock (_retryLock)
        {
            RetryState state = LoadRetryState(sessionState);
            NormalizeRetryState(state);
            if (!state.Batches!.TryGetValue(fingerprint, out RetryBatch? batch))
            {
                batch = new();
                state.Batches.Add(fingerprint, batch);
            }

            Dictionary<string, string> ids = batch.Failed!.Count == 0
                ? []
                : batch.Failed[0];
            if (batch.Failed.Count > 0)
            {
                batch.Failed.RemoveAt(0);
            }

            string attemptId = Guid.NewGuid().ToString();
            batch.InFlight!.Add(attemptId, ids);
            _activeRetryAttempts.Add(attemptId);
            return new(fingerprint, attemptId, ids, state);
        }
    }

    private void PersistRetryAttempt(
        RetryAttempt attempt,
        AgentSessionStateBag? sessionState)
    {
        lock (_retryLock)
        {
            ValidateIdMap(attempt.Ids, "in-flight attempt");
            SaveRetryState(sessionState, attempt.State);
        }
    }

    private void FinishRetryAttempt(
        RetryAttempt? attempt,
        AgentSessionStateBag? sessionState,
        bool succeeded)
    {
        if (attempt is null)
        {
            return;
        }

        lock (_retryLock)
        {
            RetryState state = LoadRetryState(sessionState);
            NormalizeRetryState(state);
            _activeRetryAttempts.Remove(attempt.AttemptId);
            if (!state.Batches!.TryGetValue(attempt.Fingerprint, out RetryBatch? batch))
            {
                return;
            }

            batch.InFlight!.Remove(attempt.AttemptId);
            if (!succeeded)
            {
                batch.Failed!.Add(attempt.Ids);
            }

            if (batch.Failed!.Count == 0 && batch.InFlight.Count == 0)
            {
                state.Batches.Remove(attempt.Fingerprint);
            }

            SaveRetryState(sessionState, state);
        }
    }

    private RetryState LoadRetryState(AgentSessionStateBag? sessionState)
    {
        if (sessionState is null)
        {
            return _directRetryState;
        }

        try
        {
            if (!sessionState.TryGetValue(
                    ProviderStateKeys[0],
                    out RetryState? state))
            {
                return new();
            }

            ValidateRetryState(state);
            return state!;
        }
        catch (MongoDBConfigurationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or NotSupportedException)
        {
            throw InvalidRetryState(
                "the stored value cannot be deserialized",
                exception);
        }
    }

    private void NormalizeRetryState(RetryState state)
    {
        ValidateRetryState(state);
        foreach (RetryBatch batch in state.Batches!.Values)
        {
            foreach ((string attemptId, Dictionary<string, string> ids) in
                     batch.InFlight!.ToArray())
            {
                if (!_activeRetryAttempts.Contains(attemptId))
                {
                    batch.Failed!.Add(ids);
                    batch.InFlight!.Remove(attemptId);
                }
            }
        }
    }

    private static void ValidateRetryState(RetryState? state)
    {
        if (state is null || state.Version != RetryStateVersion || state.Batches is null)
        {
            throw InvalidRetryState(
                "the version is unsupported or required fields are missing");
        }

        foreach ((string fingerprint, RetryBatch? batch) in state.Batches)
        {
            if (string.IsNullOrWhiteSpace(fingerprint) ||
                batch?.Failed is null ||
                batch.InFlight is null)
            {
                throw InvalidRetryState("a batch has an invalid shape");
            }

            foreach (Dictionary<string, string>? ids in batch.Failed)
            {
                ValidateIdMap(ids, "failed attempt");
            }

            foreach ((string attemptId, Dictionary<string, string>? ids) in batch.InFlight)
            {
                if (string.IsNullOrWhiteSpace(attemptId))
                {
                    throw InvalidRetryState("an in-flight attempt ID is empty");
                }

                ValidateIdMap(ids, "in-flight attempt");
            }
        }
    }

    private static void ValidateIdMap(
        Dictionary<string, string>? ids,
        string location)
    {
        if (ids is null ||
            ids.Count == 0 ||
            ids.Any(static pair =>
                string.IsNullOrWhiteSpace(pair.Key) ||
                string.IsNullOrWhiteSpace(pair.Value)))
        {
            throw InvalidRetryState($"{location} contains invalid fallback IDs");
        }
    }

    private static void SaveRetryState(
        AgentSessionStateBag? sessionState,
        RetryState state)
    {
        if (sessionState is null)
        {
            return;
        }

        if (state.Batches!.Count == 0)
        {
            sessionState.TryRemoveValue(ProviderStateKeys[0]);
        }
        else
        {
            sessionState.SetValue(ProviderStateKeys[0], state);
        }
    }

    private static MongoDBConfigurationException InvalidRetryState(
        string detail,
        Exception? innerException = null)
    {
        const string guidance =
            "MongoDB Memory provider session retry state is invalid and cannot be migrated. " +
            "Migration guidance: clear 'mongodb_memory_pending_batches' or restore a supported state version.";
        return innerException is null
            ? new MongoDBConfigurationException($"{guidance} Detail: {detail}.")
            : new MongoDBConfigurationException(
                $"{guidance} Detail: {detail}.",
                innerException);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static async Task<T> WithDeadlineAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan? timeout,
        string message,
        CancellationToken cancellationToken)
    {
        if (timeout is null)
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout.Value);
        try
        {
            return await operation(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MongoDBTimeoutException(message, exception);
        }
    }

    private sealed record RetryAttempt(
        string Fingerprint,
        string AttemptId,
        Dictionary<string, string> Ids,
        RetryState State);

    private sealed class RetryState
    {
        public int Version { get; set; } = RetryStateVersion;

        public Dictionary<string, RetryBatch>? Batches { get; set; } = [];
    }

    private sealed class RetryBatch
    {
        public List<Dictionary<string, string>>? Failed { get; set; } = [];

        public Dictionary<string, Dictionary<string, string>>? InFlight { get; set; } = [];
    }
}
