using Microsoft.Extensions.AI;
using MongoDB.AgentFramework;
using MongoDB.AgentFramework.Samples.Ingestion;
using MongoDB.Bson;
using MongoDB.Driver;

// This sample demonstrates the parent-document RAG schema/pattern (docs/spec/features/rag.md's "Parent-document
// retrieval" section and docs/spec/samples.md): only small embedded child chunks are ever searched by Vector
// Search, and after retrieval a second, bounded, de-duplicated, tenant-scoped lookup hydrates each matched chunk's
// full parent document with source attribution. There is no unrestricted pipeline callback anywhere in this flow.
// Provisioning/querying reuse the existing public MongoDBRAGIndexManager/MongoDBRAGProvider; none of this sample's
// ingestion or retrieval code is part of MongoDB.AgentFramework's public runtime API.
string uri = Environment.GetEnvironmentVariable("MONGODB_URI")
    ?? throw new InvalidOperationException("Set MONGODB_URI.");
string databaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE")
    ?? throw new InvalidOperationException("Set MONGODB_DATABASE.");
string collectionName = Environment.GetEnvironmentVariable("MONGODB_INGESTION_COLLECTION")
    ?? "agent_framework_ingestion_chunks";
// The Vector Search index name is never user-supplied: it is always a freshly generated, sample-prefixed, unique
// name for this run, so cleanup can only ever drop an index this run itself created -- never an arbitrary
// pre-existing or user-configured index. (Unlike the index, collectionName remains configurable: MongoDB creates
// collections implicitly on first write, and this sample's cleanup already only ever touches its own tenant+source
// chunks within that collection.)
string vectorIndexName = $"agent_framework_sample_pd_{Guid.NewGuid():N}";
const string TenantId = "quickstart";
const string SourceId = "parent-document-quickstart-doc";

using var client = new MongoClient(uri);
IMongoCollection<BsonDocument> collection = client.GetDatabase(databaseName).GetCollection<BsonDocument>(collectionName);
IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator = new SampleEmbeddingGenerator();

// Provisioning is an explicit, opt-in step through the existing public MongoDBRAGIndexManager -- this sample never
// creates an index implicitly as a side effect of ingestion or search.
var vectorDefinition = new MongoDBVectorSearchIndexDefinition(
    vectorIndexName,
    "embedding",
    vectorDimensions: 3,
    similarity: "cosine",
    filterFieldPaths: [ChunkRecord.TenantIdFieldName, ChunkRecord.RecordTypeFieldName]);
await using var indexManager = new MongoDBRAGIndexManager(collection, vectorDefinition);

// GeneratedIndexProvisioner records ownership *before* attempting to create the index (not only after success),
// so a failure partway through provisioning (e.g. the index is created but the bounded wait for READY times out)
// still leaves ownership correctly recorded, and the SampleCleanupOrchestration.RunAsync call below still
// attempts to drop it rather than leaking it.
var provisioner = new GeneratedIndexProvisioner(
    existsAsync: async ct => await indexManager.GetVectorSearchIndexAsync(ct) is not null,
    ensureAsync: async ct =>
    {
        Console.WriteLine("Creating this run's own Vector Search index (this can take a while on a fresh cluster)...");
        await indexManager.EnsureVectorSearchIndexAsync(waitUntilReady: true, timeout: TimeSpan.FromMinutes(3), cancellationToken: ct);
    },
    validateAsync: async ct =>
    {
        Console.WriteLine("This run's generated index name already exists; validating rather than re-creating it.");
        await indexManager.ValidateVectorSearchIndexAsync(cancellationToken: ct);
    });

var store = new MongoChunkStore(collection);
var pipeline = new ParentDocumentIngestionPipeline(
    store,
    new BatchEmbedder(embeddingGenerator, dimensions: 3),
    new ChunkingOptions { WindowSize = 80, OverlapSize = 15 });

// The outer try/finally boundary starts *before* provisioning (via SampleCleanupOrchestration.RunAsync wrapping
// the body below), not only around ingestion/search: if provisioning itself throws after having created the
// index (see GeneratedIndexProvisioner above), cleanup still runs and still attempts to drop it. Cleanup steps
// are each attempted independently -- an index-drop failure never prevents the document-delete attempt, and vice
// versa -- and a primary body failure is never silently hidden by a later cleanup failure.
await SampleCleanupOrchestration.RunAsync(
    body: async () =>
    {
        await provisioner.ProvisionAsync();

        Console.WriteLine("Ingesting one parent document plus its embedded child chunks.");
        var document = new SourceDocument(
            TenantId,
            SourceId,
            "Widgets ship in blue by default. Gadgets ship in red by default. This parent document links both facts " +
                "together, along with the shipping policy details a retrieved child chunk alone would not carry.",
            Title: "Shipping colors reference",
            Url: "https://example.test/shipping-colors");
        IngestionResult result = await pipeline.IngestAsync(document);
        Console.WriteLine($"  upserted={result.ChunksUpserted} unchanged={result.ChunksUnchanged} deleted={result.ChunksDeleted}");

        var searchOptions = new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorAnn,
            VectorIndexName = vectorIndexName,
            TopK = 5,
            MetadataFieldNames = [ChunkRecord.ParentIdFieldName],
            // The mandatory filter is the sole authorization boundary here: it constrains Vector Search to this
            // tenant's child records only, applied inside $vectorSearch itself, not as an application-side post-filter.
            MandatoryFilter = MongoDBRAGFilter.And(
                MongoDBRAGFilter.Equal(ChunkRecord.TenantIdFieldName, TenantId),
                MongoDBRAGFilter.Equal(ChunkRecord.RecordTypeFieldName, ChunkRecord.ChildRecordType)),
        };
        await using var ragProvider = new MongoDBRAGProvider(
            client, databaseName, collectionName, embeddingGenerator, vectorDimensions: 3, searchOptions);
        await using var childSearcher = new MongoDBRAGChildChunkSearcher(ragProvider);
        var parentLookup = new MongoParentLookup(collection);
        var retriever = new ParentDocumentRetriever(childSearcher, parentLookup, TenantId, maxParents: 5);

        Console.WriteLine();
        Console.WriteLine("Searching child chunks and hydrating bounded, de-duplicated parents:");
        IReadOnlyList<ParentSearchResult> results = await PollUntilNonEmptyAsync(
            retriever, "What color do widgets ship in?", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
        foreach (ParentSearchResult parent in results)
        {
            Console.WriteLine($"  [{parent.BestChildScore:F3}] {parent.Content} (source: {parent.SourceName ?? "n/a"})");
        }
    },
    async () =>
    {
        Console.WriteLine();
        Console.WriteLine("Cleaning up this quickstart's own chunks.");
        IReadOnlyDictionary<string, string> remainingHashes = await store.GetExistingHashesAsync(TenantId, SourceId);
        int deletedCount = await store.DeleteAsync(TenantId, SourceId, [.. remainingHashes.Keys]);
        Console.WriteLine($"  deleted={deletedCount}");
    },
    async () =>
    {
        // The index is dropped only if this run created it -- never an arbitrary configured or pre-existing
        // index. DropVectorSearchIndexAsync is itself a safe no-op if the index turns out to be absent (for
        // example if creation never actually got far enough to succeed).
        if (provisioner.CreatedByThisRun)
        {
            Console.WriteLine("Cleaning up this run's own Vector Search index.");
            await indexManager.DropVectorSearchIndexAsync();
        }
    });

/// <summary>
/// Bounded polling that repeatedly invokes <see cref="ParentDocumentRetriever.SearchAsync"/> until it returns a
/// non-empty result or <paramref name="timeout"/> elapses -- Atlas Vector Search indexes newly written documents
/// asynchronously, so an immediate query can race the index. Not part of the production retrieval contract.
/// </summary>
static async Task<IReadOnlyList<ParentSearchResult>> PollUntilNonEmptyAsync(
    ParentDocumentRetriever retriever,
    string query,
    TimeSpan timeout,
    TimeSpan pollInterval)
{
    using var cts = new CancellationTokenSource(timeout);
    try
    {
        while (true)
        {
            IReadOnlyList<ParentSearchResult> results = await retriever.SearchAsync(query, cts.Token);
            if (results.Count > 0)
            {
                return results;
            }

            await Task.Delay(pollInterval, cts.Token);
        }
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
        Console.WriteLine("  Timed out waiting for the parent document to become searchable.");
        return [];
    }
}

sealed class SampleEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            values.Select(static value => new Embedding<float>(
                value.Contains("widget", StringComparison.OrdinalIgnoreCase)
                    ? new float[] { 1, 0, 0 }
                    : new float[] { 0, 1, 0 }))));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
