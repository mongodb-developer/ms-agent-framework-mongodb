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
    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddingGenerator;
    private readonly MongoDBRAGProviderOptions _options;
    private readonly int _vectorDimensions;
    private readonly OwnedResource<IMongoClient>? _client;
    private readonly ILogger<MongoDBRAGProvider> _logger;

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
            options,
            logger)
    {
    }

    private MongoDBRAGProvider(
        (OwnedResource<IMongoClient> Client, IMongoCollection<BsonDocument> Collection) connected,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int vectorDimensions,
        MongoDBRAGProviderOptions options,
        ILogger<MongoDBRAGProvider>? logger)
        : this(connected.Collection, embeddingGenerator, vectorDimensions, options, logger)
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
            options,
            logger)
    {
    }

    private MongoDBRAGProvider(
        (OwnedResource<IMongoClient> Client, IMongoCollection<BsonDocument> Collection) connected,
        MongoDBRAGProviderOptions options,
        ILogger<MongoDBRAGProvider>? logger)
        : this(connected.Collection, options, logger)
    {
        _client = connected.Client;
    }

    /// <summary>
    /// Validates every argument that does not require a MongoDB client first — including calling
    /// <see cref="MongoDBRAGProviderOptions.Validate"/> directly, since the chained collection constructor only
    /// validates <paramref name="options"/> indirectly through <c>Copy()</c>, which would otherwise run after a
    /// client already exists — so a validation failure never leaves an owned client that nothing will ever
    /// dispose. Only after that validation succeeds does this create the client and resolve the
    /// database/collection; if that later step throws, the client is disposed here before rethrowing, since no
    /// <see cref="MongoDBRAGProvider"/> instance will ever exist to do it.
    /// </summary>
    private static (OwnedResource<IMongoClient> Client, IMongoCollection<BsonDocument> Collection) Connect(
        string connectionString,
        string databaseName,
        string collectionName,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        int vectorDimensions,
        MongoDBRAGProviderOptions options,
        Func<string, IMongoClient>? clientFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        EmbeddingValidator.ValidateDimensions(vectorDimensions);
        ArgumentNullException.ThrowIfNull(embeddingGenerator);
        return ConnectClient(connectionString, databaseName, collectionName, clientFactory);
    }

    /// <summary>
    /// The <see cref="MongoDBSearchMode.FullText"/>-only analogue of <see cref="Connect"/>: it validates
    /// <paramref name="options"/> and requires <see cref="MongoDBSearchMode.FullText"/> — since this family accepts
    /// no embedding generator to validate — before creating a client, with the same client-disposal-on-later-
    /// failure guarantee.
    /// </summary>
    private static (OwnedResource<IMongoClient> Client, IMongoCollection<BsonDocument> Collection) ConnectFullTextOnly(
        string connectionString,
        string databaseName,
        string collectionName,
        MongoDBRAGProviderOptions options,
        Func<string, IMongoClient>? clientFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        RequireFullTextOnlyConstructionMode(options.SearchMode);
        return ConnectClient(connectionString, databaseName, collectionName, clientFactory);
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
