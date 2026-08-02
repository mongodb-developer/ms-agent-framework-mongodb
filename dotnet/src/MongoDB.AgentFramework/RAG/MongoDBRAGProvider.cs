using System.Globalization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.AgentFramework.Internal;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.AgentFramework;

/// <summary>
/// Executes direct MongoDB RAG retrieval (<see cref="MongoDBSearchMode.VectorAnn"/> and
/// <see cref="MongoDBSearchMode.VectorEnn"/> in this release) through the public
/// <see cref="SearchAsync(string, CancellationToken)"/> seam. Authorization and multitenancy are expressed entirely
/// through the immutable <see cref="MongoDBRAGProviderOptions.MandatoryFilter"/>, translated into every active
/// retrieval branch; there is no separate scope/state concept as in <see cref="MongoDBMemoryProvider"/> because RAG
/// retrieval is read-only and stateless per call.
/// </summary>
public sealed class MongoDBRAGProvider : IAsyncDisposable
{
    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
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
    /// Validates every argument that does not require a MongoDB client first, so a validation failure never
    /// leaves an owned client that nothing will ever dispose. Only after that validation succeeds does this
    /// create the client and resolve the database/collection; if that later step throws, the client is disposed
    /// here before rethrowing, since no <see cref="MongoDBRAGProvider"/> instance will ever exist to do it.
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
        EmbeddingValidator.ValidateDimensions(vectorDimensions);
        ArgumentNullException.ThrowIfNull(embeddingGenerator);
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

    /// <summary>Gets whether the provider owns its MongoDB client.</summary>
    public bool OwnsClient => _client?.OwnsValue is true;

    /// <summary>
    /// Searches with the configured retrieval strategy. The configured
    /// <see cref="MongoDBRAGProviderOptions.MandatoryFilter"/> is always translated and placed inside the active
    /// retrieval stage; this is the sole supported authorization mechanism. Only
    /// <see cref="MongoDBSearchMode.VectorAnn"/> and <see cref="MongoDBSearchMode.VectorEnn"/> are implemented in
    /// this release.
    /// </summary>
    /// <param name="query">The natural-language query, embedded through the caller-provided generator.</param>
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
        MongoDBRAGProviderOptions.RequireText(query, nameof(query));
        RequireVectorMode();

        float[] vector = (await EmbedAsync([query], cancellationToken).ConfigureAwait(false))[0];
        bool exact = _options.SearchMode == MongoDBSearchMode.VectorEnn;
        int? numCandidates = exact
            ? null
            : _options.NumCandidates ?? DefaultNumCandidates(_options.TopK);
        BsonDocument? filter = RAGFilterTranslator.TranslateVectorFilter(_options.MandatoryFilter);
        BsonDocument[] stages = RAGPipelineBuilder.BuildVectorSearchPipeline(
            _options.VectorIndexName,
            _options.VectorFieldName,
            vector,
            _options.TopK,
            exact,
            numCandidates,
            filter);

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

    private void RequireVectorMode()
    {
        if (_options.SearchMode is not (MongoDBSearchMode.VectorAnn or MongoDBSearchMode.VectorEnn))
        {
            throw new MongoDBCapabilityException(
                $"Search mode '{_options.SearchMode}' is not yet implemented in this release; " +
                $"supported modes: {MongoDBSearchMode.VectorAnn}, {MongoDBSearchMode.VectorEnn}.");
        }
    }

    private static int DefaultNumCandidates(int topK) =>
        Math.Min(MongoDBRAGProviderOptions.MaxNumCandidates, Math.Max(topK * 10, 100));

    private async Task<float[][]> EmbedAsync(
        IEnumerable<string> values,
        CancellationToken cancellationToken)
    {
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
