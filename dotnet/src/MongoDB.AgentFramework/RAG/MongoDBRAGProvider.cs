using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.AgentFramework.Internal;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.AgentFramework;

/// <summary>
/// Executes direct MongoDB RAG retrieval (<see cref="MongoDBSearchMode.VectorAnn"/>,
/// <see cref="MongoDBSearchMode.VectorEnn"/>, and <see cref="MongoDBSearchMode.FullText"/> in this release) through
/// the public <see cref="SearchAsync(string, CancellationToken)"/> seam. Authorization and multitenancy are
/// expressed entirely through the immutable <see cref="MongoDBRAGProviderOptions.MandatoryFilter"/>, translated
/// into every active retrieval branch; there is no separate scope/state concept as in
/// <see cref="MongoDBMemoryProvider"/> because RAG retrieval is read-only and stateless per call.
/// </summary>
public sealed class MongoDBRAGProvider : IAsyncDisposable
{
    /// <summary>
    /// How long a successful <see cref="ValidateSearchIndexAsync"/> result is trusted before the next call
    /// re-inspects the index, bounding repeated-call cost (rag.md's cacheable-detection requirement) without
    /// letting a caller-invoked health check ever go permanently stale.
    /// </summary>
    private static readonly TimeSpan SearchIndexValidationCacheDuration = TimeSpan.FromSeconds(30);

    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddingGenerator;
    private readonly MongoDBRAGProviderOptions _options;
    private readonly int _vectorDimensions;
    private readonly OwnedResource<IMongoClient>? _client;
    private readonly ILogger<MongoDBRAGProvider> _logger;
    private (DateTimeOffset ValidatedAt, bool RequireReady)? _searchIndexValidation;

    /// <summary>
    /// Test-only seam controlling the clock <see cref="ValidateSearchIndexAsync"/> uses for its bounded cache;
    /// defaults to <see cref="TimeProvider.System"/> and is never part of the public construction surface.
    /// </summary>
    internal TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    /// <summary>Creates a provider over an injected database, which remains caller-owned.</summary>
    public MongoDBRAGProvider(
        IMongoDatabase database,
        string collectionName,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int vectorDimensions,
        MongoDBRAGProviderOptions options,
        ILogger<MongoDBRAGProvider>? logger = null)
        : this(
            (database ?? throw new ArgumentNullException(nameof(database)))
                .GetCollection<BsonDocument>(
                    MongoDBRAGProviderOptions.RequireText(collectionName, nameof(collectionName))),
            embeddingGenerator,
            vectorDimensions,
            options,
            logger)
    {
    }

    /// <summary>Creates a provider over an injected collection, which remains caller-owned.</summary>
    public MongoDBRAGProvider(
        IMongoCollection<BsonDocument> collection,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int vectorDimensions,
        MongoDBRAGProviderOptions options,
        ILogger<MongoDBRAGProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Copy();
        EmbeddingValidator.ValidateDimensions(vectorDimensions);

        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        _embeddingGenerator = embeddingGenerator ??
            throw new ArgumentNullException(nameof(embeddingGenerator));
        _vectorDimensions = vectorDimensions;
        _logger = logger ?? NullLogger<MongoDBRAGProvider>.Instance;
    }

    /// <summary>
    /// Wraps an <see cref="MongoDBRAGProviderOptions"/> value already known to be a validated, independent
    /// snapshot (produced by a single <see cref="MongoDBRAGProviderOptions.Copy"/> call). It exists purely to give
    /// the "already validated, do not copy again" core constructors a distinct parameter type from the public
    /// collection constructors, which must still validate and copy caller-supplied options themselves; it carries
    /// no behavior of its own.
    /// </summary>
    private readonly record struct ValidatedOptions(MongoDBRAGProviderOptions Value);

    /// <summary>
    /// Core constructor for the connection-string-owned-client family only: unlike every other constructor,
    /// <paramref name="options"/> here is already a validated, independent snapshot (produced by
    /// <see cref="Connect"/> before the owned client was created), so this does not call
    /// <see cref="MongoDBRAGProviderOptions.Copy"/> again. A second call would re-enumerate any caller-controlled
    /// <see cref="IReadOnlyList{T}"/> option value (for example <see cref="MongoDBRAGProviderOptions.MetadataFieldNames"/>)
    /// after the client already exists; if that second enumeration ever threw, the owned client would leak, since
    /// no <see cref="MongoDBRAGProvider"/> instance would ever exist to dispose it. <see cref="ValidatedOptions"/>
    /// exists purely so this overload cannot be confused with (or accidentally called in place of) the public
    /// collection constructor above, which must copy caller-supplied options itself.
    /// </summary>
    private MongoDBRAGProvider(
        IMongoCollection<BsonDocument> collection,
        ValidatedOptions options,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int vectorDimensions,
        ILogger<MongoDBRAGProvider>? logger)
    {
        _options = options.Value;
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        _embeddingGenerator = embeddingGenerator ??
            throw new ArgumentNullException(nameof(embeddingGenerator));
        _vectorDimensions = vectorDimensions;
        _logger = logger ?? NullLogger<MongoDBRAGProvider>.Instance;
    }

    /// <summary>Creates a provider over an injected client, which remains caller-owned.</summary>
    public MongoDBRAGProvider(
        IMongoClient client,
        string databaseName,
        string collectionName,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int vectorDimensions,
        MongoDBRAGProviderOptions options,
        ILogger<MongoDBRAGProvider>? logger = null)
        : this(
            (client ?? throw new ArgumentNullException(nameof(client))).GetDatabase(
                MongoDBRAGProviderOptions.RequireText(databaseName, nameof(databaseName))),
            collectionName,
            embeddingGenerator,
            vectorDimensions,
            options,
            logger)
    {
    }

    /// <summary>Creates a provider-owned client from a connection string.</summary>
    public MongoDBRAGProvider(
        string connectionString,
        string databaseName,
        string collectionName,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int vectorDimensions,
        MongoDBRAGProviderOptions options,
        ILogger<MongoDBRAGProvider>? logger = null)
        : this(
            connectionString,
            databaseName,
            collectionName,
            embeddingGenerator,
            vectorDimensions,
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
    internal MongoDBRAGProvider(
        string connectionString,
        string databaseName,
        string collectionName,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int vectorDimensions,
        MongoDBRAGProviderOptions options,
        ILogger<MongoDBRAGProvider>? logger,
        Func<string, IMongoClient>? clientFactory)
        : this(
            Connect(
                connectionString,
                databaseName,
                collectionName,
                embeddingGenerator,
                vectorDimensions,
                options,
                clientFactory),
            embeddingGenerator,
            vectorDimensions,
            logger)
    {
    }

    private MongoDBRAGProvider(
        (OwnedResource<IMongoClient> Client, IMongoCollection<BsonDocument> Collection, MongoDBRAGProviderOptions Options) connected,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int vectorDimensions,
        ILogger<MongoDBRAGProvider>? logger)
        : this(connected.Collection, new ValidatedOptions(connected.Options), embeddingGenerator, vectorDimensions, logger)
    {
        _client = connected.Client;
    }

    /// <summary>
    /// Creates a <see cref="MongoDBSearchMode.FullText"/>-only provider over an injected database, which remains
    /// caller-owned. This overload accepts no embedding generator or vector dimensions: unlike the vector-family
    /// constructors, it never embeds a query, so a caller that only needs <see cref="MongoDBSearchMode.FullText"/>
    /// retrieval is not required to supply an <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> it would never
    /// use. <paramref name="options"/> must configure <see cref="MongoDBSearchMode.FullText"/>.
    /// </summary>
    /// <exception cref="MongoDBConfigurationException">
    /// <paramref name="options"/> configures a mode other than <see cref="MongoDBSearchMode.FullText"/>.
    /// </exception>
    public MongoDBRAGProvider(
        IMongoDatabase database,
        string collectionName,
        MongoDBRAGProviderOptions options,
        ILogger<MongoDBRAGProvider>? logger = null)
        : this(
            (database ?? throw new ArgumentNullException(nameof(database)))
                .GetCollection<BsonDocument>(
                    MongoDBRAGProviderOptions.RequireText(collectionName, nameof(collectionName))),
            options,
            logger)
    {
    }

    /// <summary>
    /// Creates a <see cref="MongoDBSearchMode.FullText"/>-only provider over an injected collection, which remains
    /// caller-owned. See the database-constructor overload's remarks for why this family accepts no embedding
    /// generator or vector dimensions.
    /// </summary>
    /// <exception cref="MongoDBConfigurationException">
    /// <paramref name="options"/> configures a mode other than <see cref="MongoDBSearchMode.FullText"/>.
    /// </exception>
    public MongoDBRAGProvider(
        IMongoCollection<BsonDocument> collection,
        MongoDBRAGProviderOptions options,
        ILogger<MongoDBRAGProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Copy();
        RequireFullTextOnlyConstructionMode(_options.SearchMode);

        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        _embeddingGenerator = null;
        _vectorDimensions = 0;
        _logger = logger ?? NullLogger<MongoDBRAGProvider>.Instance;
    }

    /// <summary>
    /// The <see cref="MongoDBSearchMode.FullText"/>-only analogue of the vector family's <c>ValidatedOptions</c>
    /// core constructor; see its remarks for why this does not call <see cref="MongoDBRAGProviderOptions.Copy"/>.
    /// </summary>
    private MongoDBRAGProvider(
        IMongoCollection<BsonDocument> collection,
        ValidatedOptions options,
        ILogger<MongoDBRAGProvider>? logger)
    {
        _options = options.Value;
        _collection = collection ?? throw new ArgumentNullException(nameof(collection));
        _embeddingGenerator = null;
        _vectorDimensions = 0;
        _logger = logger ?? NullLogger<MongoDBRAGProvider>.Instance;
    }

    /// <summary>
    /// Creates a <see cref="MongoDBSearchMode.FullText"/>-only provider over an injected client, which remains
    /// caller-owned. See the database-constructor overload's remarks for why this family accepts no embedding
    /// generator or vector dimensions.
    /// </summary>
    /// <exception cref="MongoDBConfigurationException">
    /// <paramref name="options"/> configures a mode other than <see cref="MongoDBSearchMode.FullText"/>.
    /// </exception>
    public MongoDBRAGProvider(
        IMongoClient client,
        string databaseName,
        string collectionName,
        MongoDBRAGProviderOptions options,
        ILogger<MongoDBRAGProvider>? logger = null)
        : this(
            (client ?? throw new ArgumentNullException(nameof(client))).GetDatabase(
                MongoDBRAGProviderOptions.RequireText(databaseName, nameof(databaseName))),
            collectionName,
            options,
            logger)
    {
    }

    /// <summary>
    /// Creates a <see cref="MongoDBSearchMode.FullText"/>-only provider-owned client from a connection string. See
    /// the database-constructor overload's remarks for why this family accepts no embedding generator or vector
    /// dimensions.
    /// </summary>
    /// <exception cref="MongoDBConfigurationException">
    /// <paramref name="options"/> configures a mode other than <see cref="MongoDBSearchMode.FullText"/>.
    /// </exception>
    public MongoDBRAGProvider(
        string connectionString,
        string databaseName,
        string collectionName,
        MongoDBRAGProviderOptions options,
        ILogger<MongoDBRAGProvider>? logger = null)
        : this(connectionString, databaseName, collectionName, options, logger, clientFactory: null)
    {
    }

    /// <summary>
    /// Test-only <see cref="MongoDBSearchMode.FullText"/>-only seam mirroring the vector-family internal
    /// connection-string constructor's <c>clientFactory</c> override; see its remarks for why it exists.
    /// </summary>
    internal MongoDBRAGProvider(
        string connectionString,
        string databaseName,
        string collectionName,
        MongoDBRAGProviderOptions options,
        ILogger<MongoDBRAGProvider>? logger,
        Func<string, IMongoClient>? clientFactory)
        : this(
            ConnectFullTextOnly(connectionString, databaseName, collectionName, options, clientFactory),
            logger)
    {
    }

    private MongoDBRAGProvider(
        (OwnedResource<IMongoClient> Client, IMongoCollection<BsonDocument> Collection, MongoDBRAGProviderOptions Options) connected,
        ILogger<MongoDBRAGProvider>? logger)
        : this(connected.Collection, new ValidatedOptions(connected.Options), logger)
    {
        _client = connected.Client;
    }

    /// <summary>
    /// Validates every argument that does not require a MongoDB client first, producing a single validated,
    /// independent <paramref name="options"/> snapshot via <see cref="MongoDBRAGProviderOptions.Copy"/> — entirely
    /// before this creates a client. The tuple-connected core constructor threads that snapshot through
    /// unmodified and never calls <c>Copy()</c>/<c>Validate()</c> a second time, so a caller-controlled
    /// <see cref="IReadOnlyList{T}"/> option value can never be enumerated again after the client already exists;
    /// if it threw only on a later enumeration, the owned client would otherwise leak, since no
    /// <see cref="MongoDBRAGProvider"/> instance would ever exist to dispose it. If resolving the
    /// database/collection afterward throws, the client is disposed here before rethrowing, since no
    /// <see cref="MongoDBRAGProvider"/> instance will ever exist to do it.
    /// </summary>
    private static (OwnedResource<IMongoClient> Client, IMongoCollection<BsonDocument> Collection, MongoDBRAGProviderOptions Options) Connect(
        string connectionString,
        string databaseName,
        string collectionName,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int vectorDimensions,
        MongoDBRAGProviderOptions options,
        Func<string, IMongoClient>? clientFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        MongoDBRAGProviderOptions snapshot = options.Copy();
        EmbeddingValidator.ValidateDimensions(vectorDimensions);
        ArgumentNullException.ThrowIfNull(embeddingGenerator);
        (OwnedResource<IMongoClient> client, IMongoCollection<BsonDocument> collection) =
            ConnectClient(connectionString, databaseName, collectionName, clientFactory);
        return (client, collection, snapshot);
    }

    /// <summary>
    /// The <see cref="MongoDBSearchMode.FullText"/>-only analogue of <see cref="Connect"/>: it produces a single
    /// validated <paramref name="options"/> snapshot and requires <see cref="MongoDBSearchMode.FullText"/> — since
    /// this family accepts no embedding generator to validate — before creating a client, with the same
    /// single-snapshot and client-disposal-on-later-failure guarantees.
    /// </summary>
    private static (OwnedResource<IMongoClient> Client, IMongoCollection<BsonDocument> Collection, MongoDBRAGProviderOptions Options) ConnectFullTextOnly(
        string connectionString,
        string databaseName,
        string collectionName,
        MongoDBRAGProviderOptions options,
        Func<string, IMongoClient>? clientFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        MongoDBRAGProviderOptions snapshot = options.Copy();
        RequireFullTextOnlyConstructionMode(snapshot.SearchMode);
        (OwnedResource<IMongoClient> client, IMongoCollection<BsonDocument> collection) =
            ConnectClient(connectionString, databaseName, collectionName, clientFactory);
        return (client, collection, snapshot);
    }

    private static (OwnedResource<IMongoClient> Client, IMongoCollection<BsonDocument> Collection) ConnectClient(
        string connectionString,
        string databaseName,
        string collectionName,
        Func<string, IMongoClient>? clientFactory)
    {
        string validDatabaseName = MongoDBRAGProviderOptions.RequireText(databaseName, nameof(databaseName));
        string validCollectionName = MongoDBRAGProviderOptions.RequireText(collectionName, nameof(collectionName));

        OwnedResource<IMongoClient> client = MongoClientFactory.FromConnectionString(
            connectionString,
            clientFactory);
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
    /// Guards the <see cref="MongoDBSearchMode.FullText"/>-only constructor family: since it accepts no embedding
    /// generator or vector dimensions, any other configured mode would be silently unusable at search time, so
    /// this fails fast and actionably at construction instead.
    /// </summary>
    private static void RequireFullTextOnlyConstructionMode(MongoDBSearchMode mode)
    {
        if (mode != MongoDBSearchMode.FullText)
        {
            throw new MongoDBConfigurationException(
                $"This constructor overload does not accept an embedding generator, so it only supports " +
                $"'{MongoDBSearchMode.FullText}'; configured mode was '{mode}'. Use a constructor overload that " +
                "accepts an embedding generator and vector dimensions for modes that require vector search.");
        }
    }


    /// <summary>Gets whether the provider owns its MongoDB client.</summary>
    public bool OwnsClient => _client?.OwnsValue is true;

    /// <summary>
    /// Validates the <see cref="MongoDBSearchMode.FullText"/> Search index (rag.md's capability matrix, 291-314)
    /// without ever mutating MongoDB: existence, index type, configured field mappings where the definition is
    /// available, and (when <paramref name="requireReady"/>) readiness/queryability. <see cref="SearchAsync"/>
    /// never calls this method, so a query never pays for the extra round trip this performs; a caller that wants
    /// a startup or health-check gate should invoke it explicitly instead. A successful result is cached for a
    /// bounded interval (see <see cref="SearchIndexValidationCacheDuration"/>) so repeated caller-side checks
    /// (for example, a periodic health check) do not re-inspect the index on every call; pass
    /// <paramref name="refresh"/><c>: true</c> to force a fresh check regardless of the cache.
    /// </summary>
    /// <param name="requireReady">
    /// When <c>true</c> (the default), also requires the index to report <c>READY</c>/queryable status. A cached
    /// result only satisfies this when it was itself validated with <paramref name="requireReady"/><c>: true</c>,
    /// so a prior lenient check can never silently satisfy a later readiness-requiring one.
    /// </param>
    /// <param name="refresh">When <c>true</c>, bypasses the cache and re-inspects the index.</param>
    /// <param name="cancellationToken">A token used to cancel the check.</param>
    /// <exception cref="MongoDBCapabilityException">
    /// The configured <see cref="MongoDBRAGProviderOptions.SearchMode"/> does not use a Search index, or the
    /// deployment/driver could not be inspected (for example, <c>$listSearchIndexes</c> is unsupported).
    /// </exception>
    /// <exception cref="MongoDBIndexMissingException">The configured Search index does not exist.</exception>
    /// <exception cref="MongoDBIndexMismatchException">
    /// The index is not a Search index, or does not map a configured text field to a text-compatible type.
    /// </exception>
    /// <exception cref="MongoDBIndexNotReadyException">
    /// <paramref name="requireReady"/> is <c>true</c> and the index is not queryable.
    /// </exception>
    public async Task ValidateSearchIndexAsync(
        bool requireReady = true,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        RequireSearchIndexMode();

        if (!refresh &&
            _searchIndexValidation is { } cached &&
            (cached.RequireReady || !requireReady) &&
            TimeProvider.GetUtcNow() - cached.ValidatedAt < SearchIndexValidationCacheDuration)
        {
            return;
        }

        BsonDocument? index = await FindSearchIndexAsync(cancellationToken).ConfigureAwait(false);
        if (index is null)
        {
            throw new MongoDBIndexMissingException(
                $"Search index '{_options.SearchIndexName}' does not exist; create it explicitly.");
        }

        ValidateSearchIndexDefinition(index, requireReady);
        _searchIndexValidation = (TimeProvider.GetUtcNow(), requireReady);
    }

    /// <summary>
    /// Searches with the configured retrieval strategy. The configured
    /// <see cref="MongoDBRAGProviderOptions.MandatoryFilter"/> is always translated and placed inside the active
    /// retrieval stage; this is the sole supported authorization mechanism. Only
    /// <see cref="MongoDBSearchMode.VectorAnn"/>, <see cref="MongoDBSearchMode.VectorEnn"/>, and
    /// <see cref="MongoDBSearchMode.FullText"/> are implemented in this release.
    /// </summary>
    /// <param name="query">
    /// The natural-language query. Embedded through the caller-provided generator for
    /// <see cref="MongoDBSearchMode.VectorAnn"/>/<see cref="MongoDBSearchMode.VectorEnn"/>; used as-is as the
    /// <c>$search</c> text query for <see cref="MongoDBSearchMode.FullText"/>, which never invokes an embedding
    /// generator.
    /// </param>
    /// <param name="cancellationToken">A token used to cancel the search.</param>
    /// <exception cref="MongoDBConfigurationException"><paramref name="query"/> is empty.</exception>
    /// <exception cref="MongoDBCapabilityException">
    /// The configured <see cref="MongoDBRAGProviderOptions.SearchMode"/> is not yet implemented.
    /// </exception>
    /// <exception cref="MongoDBEmbeddingException">Embedding generation failed or returned invalid vectors.</exception>
    /// <exception cref="MongoDBMappingException">A retrieved document could not be mapped to a result.</exception>
    /// <exception cref="MongoDBRetrievalException">The retrieval pipeline failed.</exception>
    /// <exception cref="MongoDBTimeoutException"><see cref="MongoDBRAGProviderOptions.RetrievalTimeout"/> elapsed.</exception>
    public Task<IReadOnlyList<MongoDBRAGResult>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default) =>
        WithDeadlineAsync(
            token => SearchCoreAsync(query, token),
            _options.RetrievalTimeout,
            "MongoDB RAG retrieval deadline exceeded.",
            cancellationToken);

    private async Task<IReadOnlyList<MongoDBRAGResult>> SearchCoreAsync(
        string query,
        CancellationToken cancellationToken)
    {
        string validQuery = MongoDBRAGProviderOptions.RequireText(query, nameof(query));
        RequireSupportedMode();

        BsonDocument[] stages = _options.SearchMode == MongoDBSearchMode.FullText
            ? BuildFullTextSearchStages(validQuery)
            : await BuildVectorSearchStagesAsync(validQuery, cancellationToken).ConfigureAwait(false);

        try
        {
            using IAsyncCursor<BsonDocument> cursor = await _collection
                .AggregateAsync<BsonDocument>(stages, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var results = new List<MongoDBRAGResult>();
            while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                results.AddRange(cursor.Current.Select(MapResult));
            }

            return results;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MongoException exception)
        {
            throw new MongoDBRetrievalException("MongoDB RAG retrieval failed.", exception);
        }
    }

    private async Task<BsonDocument[]> BuildVectorSearchStagesAsync(string query, CancellationToken cancellationToken)
    {
        float[] vector = (await EmbedAsync([query], cancellationToken).ConfigureAwait(false))[0];
        bool exact = _options.SearchMode == MongoDBSearchMode.VectorEnn;
        int? numCandidates = exact
            ? null
            : _options.NumCandidates ?? DefaultNumCandidates(_options.TopK);
        BsonDocument? filter = RAGFilterTranslator.TranslateVectorFilter(_options.MandatoryFilter);
        return RAGPipelineBuilder.BuildVectorSearchPipeline(
            _options.VectorIndexName,
            _options.VectorFieldName,
            vector,
            _options.TopK,
            exact,
            numCandidates,
            filter);
    }

    private BsonDocument[] BuildFullTextSearchStages(string query)
    {
        BsonArray? filter = RAGFilterTranslator.TranslateSearchFilter(_options.MandatoryFilter);
        return RAGPipelineBuilder.BuildFullTextSearchPipeline(
            _options.SearchIndexName,
            _options.SearchTextFieldNames,
            query,
            _options.TopK,
            filter);
    }

    private void RequireSupportedMode()
    {
        if (_options.SearchMode is not
            (MongoDBSearchMode.VectorAnn or MongoDBSearchMode.VectorEnn or MongoDBSearchMode.FullText))
        {
            throw new MongoDBCapabilityException(
                $"Search mode '{_options.SearchMode}' is not yet implemented in this release; " +
                $"supported modes: {MongoDBSearchMode.VectorAnn}, {MongoDBSearchMode.VectorEnn}, " +
                $"{MongoDBSearchMode.FullText}.");
        }
    }

    private void RequireSearchIndexMode()
    {
        if (_options.SearchMode != MongoDBSearchMode.FullText)
        {
            throw new MongoDBCapabilityException(
                $"{nameof(ValidateSearchIndexAsync)} validates the Search index used by " +
                $"'{MongoDBSearchMode.FullText}'; the configured search mode is '{_options.SearchMode}'.");
        }
    }

    private async Task<BsonDocument?> FindSearchIndexAsync(CancellationToken cancellationToken)
    {
        try
        {
            using IAsyncCursor<BsonDocument> cursor = await _collection.SearchIndexes.ListAsync(
                _options.SearchIndexName,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                BsonDocument? match = cursor.Current.FirstOrDefault(
                    index => index.GetValue("name", "").AsString == _options.SearchIndexName);
                if (match is not null)
                {
                    return match;
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MongoException exception)
        {
            // Unlike Memory's analogous Vector Search inspection, a failure here is treated as a capability gap
            // rather than a generic retrieval failure: $listSearchIndexes itself can be unsupported by the
            // deployment type or driver/server version, which is exactly the condition rag.md's capability matrix
            // asks callers to detect explicitly.
            throw new MongoDBCapabilityException(
                $"Unable to inspect Search index '{_options.SearchIndexName}'; the deployment type or driver/" +
                "server version may not support $listSearchIndexes.",
                exception);
        }
    }

    /// <summary>
    /// Validates an Atlas Search index definition. Static (non-dynamic) mappings have shape
    /// <c>{ mappings: { dynamic: false, fields: { name: { type, ... } } } }</c> -- structurally different from
    /// Vector Search's flat <c>fields</c> array -- and a dynamic mapping (<c>mappings.dynamic == true</c>) indexes
    /// every field automatically, so <c>listSearchIndexes</c> provides no per-field enumeration to validate
    /// against in that case; this is a documented limitation, not a validation gap (see
    /// docs/development/rag/dotnet-rag-full-text-search.md).
    /// </summary>
    private void ValidateSearchIndexDefinition(BsonDocument index, bool requireReady)
    {
        if (!string.Equals(index.GetValue("type", "").AsString, "search", StringComparison.OrdinalIgnoreCase))
        {
            throw new MongoDBIndexMismatchException(
                $"Search index '{_options.SearchIndexName}' is not a Search index (found type " +
                $"'{index.GetValue("type", "").AsString}'); FullText requires a Search index, not a Vector " +
                "Search index.");
        }

        BsonDocument definition = index.GetValue(
            "latestDefinition",
            index.GetValue("definition", new BsonDocument())).AsBsonDocument;
        BsonDocument mappings = definition.GetValue("mappings", new BsonDocument()).AsBsonDocument;
        if (!IsDynamicMappingEnabled(mappings))
        {
            BsonDocument fields = mappings.GetValue("fields", new BsonDocument()).AsBsonDocument;
            foreach (string textField in _options.SearchTextFieldNames)
            {
                IReadOnlyList<BsonDocument> definitions = ResolveFieldMappingDefinitions(fields, textField);
                if (definitions.Count == 0)
                {
                    throw new MongoDBIndexMismatchException(
                        $"Search index '{_options.SearchIndexName}' does not map configured field " +
                        $"'{textField}'.");
                }

                if (!definitions.Any(IsTextCompatible))
                {
                    string types = string.Join(
                        ", ", definitions.Select(d => d.GetValue("type", "").AsString));
                    throw new MongoDBIndexMismatchException(
                        $"Search index '{_options.SearchIndexName}' maps field '{textField}' to " +
                        $"'{types}', none of which are text-searchable.");
                }
            }
        }

        if (requireReady &&
            (!string.Equals(index.GetValue("status", "").AsString, "READY", StringComparison.OrdinalIgnoreCase) ||
             !index.GetValue("queryable", false).ToBoolean()))
        {
            throw new MongoDBIndexNotReadyException(
                $"Search index '{_options.SearchIndexName}' is not queryable.");
        }
    }

    /// <summary>
    /// Determines whether <c>mappings.dynamic</c> enables automatic field indexing. Atlas Search accepts either a
    /// plain boolean or an object form (for example selecting a named type set); both mean "every field is indexed
    /// automatically" for the purposes of this validation, so per-field enumeration is skipped for either shape.
    /// Any other shape is not a documented "dynamic" form and is rejected with an actionable error rather than
    /// silently coerced by <see cref="BsonValue.ToBoolean"/> truthiness rules.
    /// </summary>
    private bool IsDynamicMappingEnabled(BsonDocument mappings)
    {
        if (!mappings.TryGetValue("dynamic", out BsonValue? dynamicValue))
        {
            return false;
        }

        return dynamicValue switch
        {
            BsonBoolean boolean => boolean.Value,
            BsonDocument => true,
            _ => throw new MongoDBIndexMismatchException(
                $"Search index '{_options.SearchIndexName}' has an unrecognized 'mappings.dynamic' shape " +
                $"({dynamicValue.BsonType}); expected a boolean or an object."),
        };
    }

    /// <summary>
    /// Resolves a possibly dotted field path through nested <c>type: "document"</c> mappings, returning every
    /// applicable type definition for the terminal field. Atlas Search allows a field to be mapped to a single
    /// definition object or to an array of multiple type definitions (for example both <c>"token"</c> and
    /// <c>"number"</c> for the same field); either shape is supported here. Returns an empty list if the path is
    /// not mapped. Throws <see cref="MongoDBIndexMismatchException"/> for a shape that is neither a mapping object
    /// nor an array of mapping objects, rather than silently treating it as unmapped.
    /// </summary>
    private IReadOnlyList<BsonDocument> ResolveFieldMappingDefinitions(BsonDocument fields, string path)
    {
        string[] segments = path.Split('.');
        BsonDocument currentFields = fields;
        for (int i = 0; i < segments.Length; i++)
        {
            if (!currentFields.TryGetValue(segments[i], out BsonValue? value))
            {
                return [];
            }

            IReadOnlyList<BsonDocument> definitions = ResolveFieldDefinitions(value, segments[i]);
            bool isLastSegment = i == segments.Length - 1;
            if (isLastSegment)
            {
                return definitions;
            }

            BsonDocument? nestedDocument = definitions.FirstOrDefault(
                d => string.Equals(d.GetValue("type", "").AsString, "document", StringComparison.OrdinalIgnoreCase));
            if (nestedDocument is null)
            {
                return [];
            }

            currentFields = nestedDocument.GetValue("fields", new BsonDocument()).AsBsonDocument;
        }

        return [];
    }

    /// <summary>Normalizes a single field-mapping value (a mapping object or an array of mapping objects).</summary>
    private IReadOnlyList<BsonDocument> ResolveFieldDefinitions(BsonValue value, string fieldName) =>
        value switch
        {
            BsonDocument document => [document],
            BsonArray array => [.. array.Select(element => element as BsonDocument ??
                throw new MongoDBIndexMismatchException(
                    $"Search index '{_options.SearchIndexName}' has a multi-type mapping for field " +
                    $"'{fieldName}' containing a non-object entry ({element.BsonType}); expected an array of " +
                    "mapping objects."))],
            _ => throw new MongoDBIndexMismatchException(
                $"Search index '{_options.SearchIndexName}' has an unrecognized mapping shape for field " +
                $"'{fieldName}' ({value.BsonType}); expected a mapping object or an array of mapping objects."),
        };

    /// <summary>
    /// A field is text-searchable if any applicable mapping definition is; only reject a field once every
    /// definition is confirmed non-text-compatible (see <see cref="ResolveFieldMappingDefinitions"/>).
    /// </summary>
    private static bool IsTextCompatible(BsonDocument fieldMapping) =>
        fieldMapping.GetValue("type", "").AsString is "string" or "autocomplete" or "token";

    private static int DefaultNumCandidates(int topK) =>
        Math.Min(MongoDBRAGProviderOptions.MaxNumCandidates, Math.Max(topK * 10, 100));

    private async Task<float[][]> EmbedAsync(
        IEnumerable<string> values,
        CancellationToken cancellationToken)
    {
        if (_embeddingGenerator is null)
        {
            // Structurally unreachable: only the FullText-only constructor family leaves this null, and that
            // family also rejects any mode other than FullText, which SearchCoreAsync never routes through this
            // vector embedding path. Guarded defensively so a future mode-gating regression fails loudly with an
            // actionable message instead of a NullReferenceException.
            throw new MongoDBConfigurationException(
                "An embedding generator is required for this search mode, but none was configured. Use a " +
                "constructor overload that accepts an embedding generator and vector dimensions.");
        }

        string[] inputs = values.ToArray();
        try
        {
            GeneratedEmbeddings<Embedding<float>> generated = await _embeddingGenerator.GenerateAsync(
                inputs,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ReadOnlyMemory<float>> normalized = EmbeddingValidator.Normalize(
                generated.Select(static embedding => embedding.Vector),
                inputs.Length,
                _vectorDimensions);
            return [.. normalized.Select(static vector => vector.ToArray())];
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

    private MongoDBRAGResult MapResult(BsonDocument document)
    {
        double score = MapScore(document);
        // Strip the internal reserved alias from a copy of the document before it becomes the public RawDocument;
        // MongoDBRAGResult deep-clones its input, so mutating this instance here does not affect the cursor.
        document.Remove(FieldPath.ReservedScoreAlias);

        BsonValue idValue = FieldPath.Resolve(document, _options.IdFieldName);
        string id = MapId(idValue);
        BsonValue textValue = FieldPath.Resolve(document, _options.ChunkTextFieldName);
        if (!textValue.IsString)
        {
            throw new MongoDBMappingException(
                $"Field '{_options.ChunkTextFieldName}' must be a string.");
        }

        string? sourceName = OptionalString(document, _options.SourceNameFieldName);
        string? sourceUrl = OptionalString(document, _options.SourceUrlFieldName);
        Dictionary<string, BsonValue>? metadata = null;
        if (_options.MetadataFieldNames is { } metadataFieldNames)
        {
            metadata = [];
            foreach (string field in metadataFieldNames)
            {
                if (FieldPath.TryResolve(document, field, out BsonValue? value))
                {
                    metadata[field] = value!;
                }
            }
        }

        return new MongoDBRAGResult(
            id,
            textValue.AsString,
            score,
            sourceName,
            sourceUrl,
            metadata,
            document);
    }

    /// <summary>
    /// Resolves and validates the reserved <see cref="FieldPath.ReservedScoreAlias"/> field. A missing,
    /// non-numeric, or non-finite score is a mapping defect per the RAG score contract, not a value to default to
    /// <c>0.0</c> for, since a fabricated score would silently corrupt result ranking for callers.
    /// </summary>
    private static double MapScore(BsonDocument document)
    {
        if (!document.TryGetValue(FieldPath.ReservedScoreAlias, out BsonValue? scoreValue))
        {
            throw new MongoDBMappingException(
                $"Required field '{FieldPath.ReservedScoreAlias}' is missing from the result.");
        }

        double score = scoreValue.BsonType switch
        {
            BsonType.Double => scoreValue.AsDouble,
            BsonType.Int32 => scoreValue.AsInt32,
            BsonType.Int64 => scoreValue.AsInt64,
            BsonType.Decimal128 => (double)scoreValue.AsDecimal128,
            _ => throw new MongoDBMappingException(
                $"Field '{FieldPath.ReservedScoreAlias}' must be a numeric value."),
        };

        if (!double.IsFinite(score))
        {
            throw new MongoDBMappingException(
                $"Field '{FieldPath.ReservedScoreAlias}' must be a finite numeric value.");
        }

        return score;
    }

    private static string MapId(BsonValue value) => value.BsonType switch
    {
        BsonType.String => value.AsString,
        BsonType.ObjectId => value.AsObjectId.ToString(),
        BsonType.Int32 => value.AsInt32.ToString(CultureInfo.InvariantCulture),
        BsonType.Int64 => value.AsInt64.ToString(CultureInfo.InvariantCulture),
        BsonType.Double => value.AsDouble.ToString(CultureInfo.InvariantCulture),
        _ => throw new MongoDBMappingException(
            $"Field '{value.BsonType}' cannot be mapped to a result identifier."),
    };

    private static string? OptionalString(BsonDocument document, string? fieldPath)
    {
        if (fieldPath is null || !FieldPath.TryResolve(document, fieldPath, out BsonValue? value))
        {
            return null;
        }

        return value!.IsString ? value.AsString : null;
    }

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

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
