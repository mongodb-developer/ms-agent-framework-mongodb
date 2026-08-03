using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.AgentFramework.Internal;
using MongoDB.AgentFramework.Internal.IndexManagement;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.AgentFramework;

/// <summary>
/// Executes direct MongoDB RAG retrieval (<see cref="MongoDBSearchMode.VectorAnn"/>,
/// <see cref="MongoDBSearchMode.VectorEnn"/>, <see cref="MongoDBSearchMode.FullText"/>, and
/// <see cref="MongoDBSearchMode.HybridRrf"/>) through the public
/// <see cref="SearchAsync(string, CancellationToken)"/> seam. Authorization and multitenancy are expressed entirely
/// through the immutable <see cref="MongoDBRAGProviderOptions.MandatoryFilter"/>, translated into every active
/// retrieval branch, independently for each of <see cref="MongoDBSearchMode.HybridRrf"/>'s two input branches;
/// there is no separate scope/state concept as in <see cref="MongoDBMemoryProvider"/> because RAG retrieval is
/// read-only and stateless per call.
/// </summary>
public sealed class MongoDBRAGProvider : IAsyncDisposable
{
    /// <summary>
    /// How long a successful <see cref="ValidateSearchIndexAsync"/> result is trusted before the next call
    /// re-inspects the index, bounding repeated-call cost (rag.md's cacheable-detection requirement) without
    /// letting a caller-invoked health check ever go permanently stale.
    /// </summary>
    private static readonly TimeSpan SearchIndexValidationCacheDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a successful <see cref="ValidateHybridSearchCapabilityAsync"/> result is trusted before the next
    /// call re-inspects the server version and both indexes, mirroring
    /// <see cref="SearchIndexValidationCacheDuration"/>'s "no forced extra round trip per query" design.
    /// </summary>
    private static readonly TimeSpan HybridCapabilityValidationCacheDuration = TimeSpan.FromSeconds(30);

    /// <summary>The minimum MongoDB server major version that supports the <c>$rankFusion</c> aggregation stage.</summary>
    private const int MinimumHybridServerMajorVersion = 8;

    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddingGenerator;
    private readonly MongoDBRAGProviderOptions _options;
    private readonly int _vectorDimensions;
    private readonly OwnedResource<IMongoClient>? _client;
    private readonly ILogger<MongoDBRAGProvider> _logger;
    private (DateTimeOffset ValidatedAt, bool RequireReady)? _searchIndexValidation;
    private (DateTimeOffset ValidatedAt, bool RequireReady)? _hybridCapabilityValidation;

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
    /// The Vector Search index definition Hybrid's vector branch and <see cref="ValidateHybridSearchCapabilityAsync"/>
    /// validate against -- similarity is intentionally not compared (see <see cref="MongoDBVectorSearchIndexDefinition.Similarity"/>'s
    /// remarks) because <c>$rankFusion</c> combines rank order across branches rather than raw similarity scores.
    /// Not valid to evaluate for a <see cref="MongoDBSearchMode.FullText"/>-only constructed provider (<see cref="_vectorDimensions"/>
    /// is <c>0</c> in that case); every caller must call <see cref="RequireHybridCapabilityMode"/> first, which
    /// rejects that construction mode before this is ever evaluated.
    /// </summary>
    private MongoDBVectorSearchIndexDefinition VectorIndexDefinition =>
        new(
            _options.VectorIndexName,
            _options.VectorFieldName,
            _vectorDimensions,
            similarity: null,
            filterFieldPaths: [.. RAGFilterFieldReferences.Enumerate(_options.MandatoryFilter)
                .Select(static reference => reference.FieldPath)]);

    /// <summary>
    /// The Search index definition <see cref="ValidateSearchIndexAsync"/> and Hybrid's text branch/
    /// <see cref="ValidateHybridSearchCapabilityAsync"/> validate against.
    /// </summary>
    private MongoDBSearchIndexDefinition SearchIndexDefinition =>
        new(_options.SearchIndexName, _options.SearchTextFieldNames, _options.MandatoryFilter);

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

        BsonDocument index = await MongoDBSearchIndexes.FindAsync(
            _collection.SearchIndexes,
            _options.SearchIndexName,
            MapSearchInspectionException,
            cancellationToken).ConfigureAwait(false) ??
            throw new MongoDBIndexMissingException(
                $"Search index '{_options.SearchIndexName}' does not exist; create it explicitly.");

        SearchIndexEquivalence.Validate(index, SearchIndexDefinition, requireReady);
        _searchIndexValidation = (TimeProvider.GetUtcNow(), requireReady);
    }

    /// <summary>
    /// Validates the <see cref="MongoDBSearchMode.HybridRrf"/> capability matrix row: a MongoDB server new enough
    /// to support the <c>$rankFusion</c> aggregation stage (major version 8+), both the Vector Search index used
    /// by Hybrid's vector input branch and the Search index used by its text input branch, and -- when
    /// <see cref="MongoDBRAGProviderOptions.MandatoryFilter"/> references any fields -- that every referenced
    /// field is indexed compatibly with the operator it is used with in both branches. Unlike
    /// <see cref="ValidateSearchIndexAsync"/>, <see cref="SearchAsync(string, CancellationToken)"/> calls this
    /// method before every Hybrid aggregation (never silently downgrading a missing/incapable deployment into a
    /// generic retrieval failure), but a successful result is cached for a bounded interval (see
    /// <see cref="HybridCapabilityValidationCacheDuration"/>) so a query does not pay for the extra round trips on
    /// every call; pass <paramref name="refresh"/><c>: true</c> to force a fresh check regardless of the cache.
    /// A result is only cached when every mandatory-filter field could be statically verified -- a dynamic Search
    /// mapping (see <see cref="SearchIndexComparisonResult.DynamicMappingFieldsUnverified"/>) cannot be checked per
    /// field, so in that case (only when the filter actually references fields) this method re-validates on every
    /// call rather than caching an unverifiable authorization surface.
    /// </summary>
    /// <param name="requireReady">
    /// When <c>true</c> (the default), also requires both indexes to report <c>READY</c>/queryable status. A
    /// cached result only satisfies this when it was itself validated with <paramref name="requireReady"/>
    /// <c>: true</c>, so a prior lenient check can never silently satisfy a later readiness-requiring one.
    /// </param>
    /// <param name="refresh">When <c>true</c>, bypasses the cache and re-inspects the server and both indexes.</param>
    /// <param name="cancellationToken">A token used to cancel the check.</param>
    /// <exception cref="MongoDBCapabilityException">
    /// The configured <see cref="MongoDBRAGProviderOptions.SearchMode"/> is not <see cref="MongoDBSearchMode.HybridRrf"/>,
    /// the connected server reports a major version below 8, or the deployment/driver could not be inspected.
    /// </exception>
    /// <exception cref="MongoDBIndexMissingException">The configured Vector Search or Search index does not exist.</exception>
    /// <exception cref="MongoDBIndexMismatchException">
    /// Either index does not match its required Hybrid definition (wrong type, dimension, or field mapping), or a
    /// <see cref="MongoDBRAGProviderOptions.MandatoryFilter"/> field is not indexed compatibly with its operator
    /// in either branch.
    /// </exception>
    /// <exception cref="MongoDBIndexNotReadyException">
    /// <paramref name="requireReady"/> is <c>true</c> and either index is not queryable.
    /// </exception>
    public async Task ValidateHybridSearchCapabilityAsync(
        bool requireReady = true,
        bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        RequireHybridCapabilityMode();

        if (!refresh &&
            _hybridCapabilityValidation is { } cached &&
            (cached.RequireReady || !requireReady) &&
            TimeProvider.GetUtcNow() - cached.ValidatedAt < HybridCapabilityValidationCacheDuration)
        {
            return;
        }

        await RequireServerVersionAsync(cancellationToken).ConfigureAwait(false);

        BsonDocument vectorIndex = await MongoDBSearchIndexes.FindAsync(
            _collection.SearchIndexes,
            _options.VectorIndexName,
            MapVectorInspectionException,
            cancellationToken).ConfigureAwait(false) ??
            throw new MongoDBIndexMissingException(
                $"Vector Search index '{_options.VectorIndexName}' does not exist; create it explicitly.");
        VectorSearchIndexEquivalence.Validate(vectorIndex, VectorIndexDefinition, requireReady);

        BsonDocument searchIndex = await MongoDBSearchIndexes.FindAsync(
            _collection.SearchIndexes,
            _options.SearchIndexName,
            MapSearchInspectionException,
            cancellationToken).ConfigureAwait(false) ??
            throw new MongoDBIndexMissingException(
                $"Search index '{_options.SearchIndexName}' does not exist; create it explicitly.");
        SearchIndexComparisonResult searchResult = SearchIndexEquivalence.Validate(
            searchIndex, SearchIndexDefinition, requireReady);

        // A dynamic Search mapping cannot be statically checked per referenced field (see
        // SearchIndexEquivalence.Compare), so a result covering unverified mandatory-filter fields is never
        // cached: every call re-validates rather than silently trusting an unverifiable authorization surface. If
        // a prior call had cached success (for example before the Search index's mapping became dynamic), that
        // stale cache entry must be explicitly cleared here rather than left in place, or a later plain
        // (non-refresh) call could still short-circuit on it and skip re-validating an authorization surface
        // that is no longer statically verifiable.
        _hybridCapabilityValidation = searchResult.DynamicMappingFieldsUnverified
            ? null
            : (TimeProvider.GetUtcNow(), requireReady);
    }

    /// <summary>
    /// Searches with the configured retrieval strategy. The configured
    /// <see cref="MongoDBRAGProviderOptions.MandatoryFilter"/> is always translated and placed inside the active
    /// retrieval stage(s) -- independently for each input branch of <see cref="MongoDBSearchMode.HybridRrf"/> --
    /// this is the sole supported authorization mechanism.
    /// </summary>
    /// <param name="query">
    /// The natural-language query. Embedded through the caller-provided generator for
    /// <see cref="MongoDBSearchMode.VectorAnn"/>/<see cref="MongoDBSearchMode.VectorEnn"/>/
    /// <see cref="MongoDBSearchMode.HybridRrf"/>; used as-is as the <c>$search</c> text query for
    /// <see cref="MongoDBSearchMode.FullText"/> (and, in addition to embedding, for <see cref="MongoDBSearchMode.HybridRrf"/>'s
    /// text input), which never invokes an embedding generator on its own.
    /// </param>
    /// <param name="cancellationToken">A token used to cancel the search.</param>
    /// <exception cref="MongoDBConfigurationException"><paramref name="query"/> is empty.</exception>
    /// <exception cref="MongoDBCapabilityException">
    /// The configured <see cref="MongoDBRAGProviderOptions.SearchMode"/> is not implemented; for
    /// <see cref="MongoDBSearchMode.HybridRrf"/>, also the capability-matrix failures described on
    /// <see cref="ValidateHybridSearchCapabilityAsync"/>, or a recognized server response indicating
    /// <c>$rankFusion</c> is unsupported/disabled by the connected deployment.
    /// </exception>
    /// <exception cref="MongoDBEmbeddingException">Embedding generation failed or returned invalid vectors.</exception>
    /// <exception cref="MongoDBIndexMismatchException">
    /// <see cref="MongoDBSearchMode.HybridRrf"/>'s Vector Search or Search index does not match its required
    /// definition, including any configured <see cref="MongoDBRAGProviderOptions.MandatoryFilter"/> field.
    /// </exception>
    /// <exception cref="MongoDBIndexMissingException">
    /// <see cref="MongoDBSearchMode.HybridRrf"/>'s configured Vector Search or Search index does not exist.
    /// </exception>
    /// <exception cref="MongoDBIndexNotReadyException">
    /// <see cref="MongoDBSearchMode.HybridRrf"/>'s Vector Search or Search index is not queryable.
    /// </exception>
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

        // Unlike the analogous FullText ValidateSearchIndexAsync seam (which a caller must invoke explicitly),
        // Hybrid's $rankFusion capability/field validation runs before every aggregation: an unsupported
        // deployment or a mandatory-filter field that is not indexed compatibly must never silently reach
        // MongoDB as a generic retrieval failure. The bounded cache (see ValidateHybridSearchCapabilityAsync)
        // keeps this from costing a network round trip on every call.
        if (_options.SearchMode == MongoDBSearchMode.HybridRrf)
        {
            await ValidateHybridSearchCapabilityAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        BsonDocument[] stages = _options.SearchMode switch
        {
            MongoDBSearchMode.FullText => BuildFullTextSearchStages(validQuery),
            MongoDBSearchMode.HybridRrf =>
                await BuildHybridSearchStagesAsync(validQuery, cancellationToken).ConfigureAwait(false),
            _ => await BuildVectorSearchStagesAsync(validQuery, cancellationToken).ConfigureAwait(false),
        };

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
        catch (MongoCommandException exception)
            when (_options.SearchMode == MongoDBSearchMode.HybridRrf && IsUnsupportedRankFusionError(exception))
        {
            throw new MongoDBCapabilityException(
                "The connected MongoDB deployment rejected the $rankFusion aggregation stage used by " +
                "HybridRrf; it may not support Hybrid search (MongoDB 8.0+ with $rankFusion enabled is " +
                "required). Call ValidateHybridSearchCapabilityAsync for a full capability diagnosis.",
                exception);
        }
        catch (MongoException exception)
        {
            throw new MongoDBRetrievalException("MongoDB RAG retrieval failed.", exception);
        }
    }

    /// <summary>
    /// Recognizes a server command failure indicating the connected deployment does not support (or has
    /// disabled) the <c>$rankFusion</c> aggregation stage: an "unrecognized pipeline stage"/"command not
    /// supported" server error code, or an error message explicitly naming <c>rankFusion</c>. Anything else is a
    /// generic <see cref="MongoDBRetrievalException"/>, matching every other mode.
    /// </summary>
    private static bool IsUnsupportedRankFusionError(MongoCommandException exception) =>
        exception.Code is 40324 or 115 ||
        (exception.ErrorMessage is { } message &&
         message.Contains("rankfusion", StringComparison.OrdinalIgnoreCase));

    private async Task<BsonDocument[]> BuildVectorSearchStagesAsync(string query, CancellationToken cancellationToken)
    {
        float[] vector = (await EmbedAsync([query], cancellationToken).ConfigureAwait(false))[0];
        bool exact = _options.SearchMode == MongoDBSearchMode.VectorEnn;
        int? numCandidates = exact
            ? null
            : _options.NumCandidates ?? MongoDBRAGProviderOptions.DefaultNumCandidates(_options.TopK);
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

    /// <summary>
    /// Builds the <see cref="MongoDBSearchMode.HybridRrf"/> pipeline: the vector input always runs ANN (never ENN)
    /// per rag.md's hybrid pipeline rules, and each input's mandatory filter is translated and placed
    /// independently, matching the ordinary ANN/FullText translation used by their own single-mode pipelines.
    /// </summary>
    private async Task<BsonDocument[]> BuildHybridSearchStagesAsync(string query, CancellationToken cancellationToken)
    {
        float[] vector = (await EmbedAsync([query], cancellationToken).ConfigureAwait(false))[0];
        int vectorNumCandidates = _options.NumCandidates ?? MongoDBRAGProviderOptions.DefaultNumCandidates(_options.TopK);
        int vectorCandidateLimit = _options.VectorCandidateLimit ?? MongoDBRAGProviderOptions.DefaultNumCandidates(_options.TopK);
        int textCandidateLimit = _options.TextCandidateLimit ?? MongoDBRAGProviderOptions.DefaultNumCandidates(_options.TopK);
        BsonDocument? vectorFilter = RAGFilterTranslator.TranslateVectorFilter(_options.MandatoryFilter);
        BsonArray? searchFilter = RAGFilterTranslator.TranslateSearchFilter(_options.MandatoryFilter);
        return RAGPipelineBuilder.BuildHybridRankFusionPipeline(
            _options.VectorIndexName,
            _options.VectorFieldName,
            vector,
            vectorNumCandidates,
            vectorCandidateLimit,
            vectorFilter,
            _options.SearchIndexName,
            _options.SearchTextFieldNames,
            query,
            textCandidateLimit,
            searchFilter,
            _options.VectorWeight,
            _options.TextWeight,
            _options.IncludeScoreDetails,
            _options.TopK);
    }

    private void RequireSupportedMode()
    {
        if (_options.SearchMode is not
            (MongoDBSearchMode.VectorAnn or MongoDBSearchMode.VectorEnn or MongoDBSearchMode.FullText or
             MongoDBSearchMode.HybridRrf))
        {
            throw new MongoDBCapabilityException(
                $"Search mode '{_options.SearchMode}' is not implemented.");
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

    private void RequireHybridCapabilityMode()
    {
        if (_options.SearchMode != MongoDBSearchMode.HybridRrf)
        {
            throw new MongoDBCapabilityException(
                $"{nameof(ValidateHybridSearchCapabilityAsync)} validates the MongoDB 8.0+ / index capability " +
                $"required by '{MongoDBSearchMode.HybridRrf}'; the configured search mode is " +
                $"'{_options.SearchMode}'.");
        }
    }

    /// <summary>
    /// Checks the connected server's <c>buildInfo</c> major version against
    /// <see cref="MinimumHybridServerMajorVersion"/>, the minimum required for the <c>$rankFusion</c> aggregation
    /// stage that <see cref="MongoDBSearchMode.HybridRrf"/> depends on.
    /// </summary>
    private async Task RequireServerVersionAsync(CancellationToken cancellationToken)
    {
        BsonDocument buildInfo;
        try
        {
            buildInfo = await _collection.Database.RunCommandAsync<BsonDocument>(
                new BsonDocument("buildInfo", 1),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (MongoException exception)
        {
            throw new MongoDBCapabilityException(
                "Unable to determine the connected MongoDB server version required by " +
                $"'{MongoDBSearchMode.HybridRrf}''s $rankFusion stage.",
                exception);
        }

        string version = buildInfo.GetValue("version", "").AsString;
        if (ParseMajorVersion(version) is not { } major || major < MinimumHybridServerMajorVersion)
        {
            throw new MongoDBCapabilityException(
                $"'{MongoDBSearchMode.HybridRrf}' requires MongoDB {MinimumHybridServerMajorVersion}.0+ ($rankFusion " +
                $"support); the connected server reports version '{version}'.");
        }
    }

    /// <summary>Parses the leading major-version component of a <c>buildInfo</c> version string, if present.</summary>
    private static int? ParseMajorVersion(string version)
    {
        string majorPart = version.Split('.')[0];
        return int.TryParse(majorPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int major)
            ? major
            : null;
    }

    private Exception MapVectorInspectionException(MongoException exception) =>
        MongoDBSearchIndexes.IsUnauthorized(exception)
            ? new MongoDBIndexPrivilegeException(
                $"Not authorized to inspect Vector Search index '{_options.VectorIndexName}'.", exception)
            : new MongoDBCapabilityException(
                $"Unable to inspect Vector Search index '{_options.VectorIndexName}'; the deployment type or " +
                "driver/server version may not support $listSearchIndexes.",
                exception);

    private Exception MapSearchInspectionException(MongoException exception) =>
        MongoDBSearchIndexes.IsUnauthorized(exception)
            ? new MongoDBIndexPrivilegeException(
                $"Not authorized to inspect Search index '{_options.SearchIndexName}'.", exception)
            : new MongoDBCapabilityException(
                $"Unable to inspect Search index '{_options.SearchIndexName}'; the deployment type or driver/" +
                "server version may not support $listSearchIndexes.",
                exception);

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
        BsonDocument? scoreDetails = null;
        if (document.TryGetValue(FieldPath.ReservedScoreDetailsAlias, out BsonValue? scoreDetailsValue))
        {
            scoreDetails = scoreDetailsValue.AsBsonDocument;
            document.Remove(FieldPath.ReservedScoreDetailsAlias);
        }

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
            document,
            scoreDetails);
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
