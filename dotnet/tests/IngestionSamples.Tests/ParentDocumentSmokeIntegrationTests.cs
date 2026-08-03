using MongoDB.AgentFramework.Samples.Ingestion;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

/// <summary>
/// An end-to-end smoke test for the parent-document RAG pattern against a live MongoDB deployment: ingests a
/// parent+child document with <see cref="ParentDocumentIngestionPipeline"/>, searches child chunks through
/// <see cref="MongoDBRAGChildChunkSearcher"/> wrapping a real <see cref="MongoDBRAGProvider"/>, and hydrates the
/// bounded, de-duplicated, tenant-scoped parent through <see cref="MongoParentLookup"/> and
/// <see cref="ParentDocumentRetriever"/>. Index provisioning uses the existing public
/// <see cref="MongoDBRAGIndexManager"/> via <see cref="GeneratedIndexProvisioner"/> (ownership recorded before the
/// create attempt) and cleanup runs through <see cref="SampleCleanupOrchestration"/> (exception-safe from before
/// provisioning, every cleanup step always attempted), and every document written carries a unique, test-owned
/// source ID prefix so concurrent runs never collide.
/// </summary>
public sealed class ParentDocumentSmokeIntegrationTests
{
    [MongoIntegrationFact]
    [Trait("Category", "integration-ingestion")]
    public async Task ParentDocumentIngestionAndRetrievalWorkEndToEndAgainstLiveMongo()
    {
        string uri = Environment.GetEnvironmentVariable("MONGODB_URI")!;
        string databaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE")!;
        string collectionName = Environment.GetEnvironmentVariable("MONGODB_INGESTION_COLLECTION") ??
            "af_ingestion_dotnet_integration";
        string vectorIndexName = $"af_ingestion_dotnet_parent_smoke_{Guid.NewGuid():N}";

        using var client = new MongoClient(uri);
        IMongoDatabase database = client.GetDatabase(databaseName);
        IMongoCollection<BsonDocument> collection = database.GetCollection<BsonDocument>(collectionName);
        string tenantId = "tenant-integration-parent";
        string sourceId = $"af_ingestion_dotnet_test_{Guid.NewGuid():N}";

        var vectorDefinition = new MongoDBVectorSearchIndexDefinition(
            vectorIndexName,
            "embedding",
            vectorDimensions: 3,
            similarity: "cosine",
            filterFieldPaths: [ChunkRecord.TenantIdFieldName, ChunkRecord.RecordTypeFieldName]);
        await using var indexManager = new MongoDBRAGIndexManager(collection, vectorDefinition);

        var store = new MongoChunkStore(collection);
        var pipeline = new ParentDocumentIngestionPipeline(
            store,
            new BatchEmbedder(new FixedVectorEmbeddingGenerator(), dimensions: 3),
            new ChunkingOptions { WindowSize = 60, OverlapSize = 10 });

        // GeneratedIndexProvisioner records ownership *before* attempting to create the index (not only after
        // success), so a failure partway through provisioning (e.g. the index is created but the bounded wait
        // for READY times out) still leaves ownership correctly recorded, and the
        // SampleCleanupOrchestration.RunAsync call below still attempts to drop it rather than leaking it.
        var provisioner = new GeneratedIndexProvisioner(
            existsAsync: async ct => await indexManager.GetVectorSearchIndexAsync(ct) is not null,
            ensureAsync: ct => indexManager.EnsureVectorSearchIndexAsync(waitUntilReady: true, timeout: TimeSpan.FromMinutes(3), cancellationToken: ct),
            validateAsync: ct => indexManager.ValidateVectorSearchIndexAsync(cancellationToken: ct));

        // The outer try/finally boundary starts *before* provisioning (via SampleCleanupOrchestration.RunAsync
        // wrapping the body below), not only around ingestion/search: if provisioning itself throws after having
        // created the index, cleanup still runs and still attempts to drop it. Cleanup steps are each attempted
        // independently -- an index-drop failure never prevents the document-delete attempt, and vice versa --
        // and a primary body failure is never silently hidden by a later cleanup failure.
        await SampleCleanupOrchestration.RunAsync(
            body: async () =>
            {
                await provisioner.ProvisionAsync();

                var document = new SourceDocument(
                    tenantId,
                    sourceId,
                    "Widgets ship in blue by default. Gadgets ship in a different color entirely. " +
                        "This parent document links both facts together for attribution.",
                    Title: "Shipping colors reference");
                await pipeline.IngestAsync(document);

                var searchOptions = new MongoDBRAGProviderOptions
                {
                    SearchMode = MongoDBSearchMode.VectorAnn,
                    VectorIndexName = vectorIndexName,
                    TopK = 5,
                    MetadataFieldNames = [ChunkRecord.ParentIdFieldName],
                    MandatoryFilter = MongoDBRAGFilter.And(
                        MongoDBRAGFilter.Equal(ChunkRecord.TenantIdFieldName, tenantId),
                        MongoDBRAGFilter.Equal(ChunkRecord.RecordTypeFieldName, ChunkRecord.ChildRecordType)),
                };
                await using var ragProvider = new MongoDBRAGProvider(
                    client,
                    databaseName,
                    collectionName,
                    new FixedVectorEmbeddingGenerator(),
                    vectorDimensions: 3,
                    searchOptions);
                await using var childSearcher = new MongoDBRAGChildChunkSearcher(ragProvider);
                var parentLookup = new MongoParentLookup(collection);
                var retriever = new ParentDocumentRetriever(childSearcher, parentLookup, tenantId);

                IReadOnlyList<ParentSearchResult> results = await PollUntilNonEmptyAsync(
                    retriever, "What color do widgets ship in?", TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));

                Assert.NotEmpty(results);
                ParentSearchResult hydratedParent = Assert.Single(results);
                Assert.Contains("Widgets ship in blue", hydratedParent.Content, StringComparison.Ordinal);
                Assert.Equal("Shipping colors reference", hydratedParent.SourceName);
            },
            async () =>
            {
                await store.DeleteAsync(
                    tenantId,
                    sourceId,
                    (await store.GetExistingHashesAsync(tenantId, sourceId)).Keys.ToArray());
            },
            async () =>
            {
                // The index is dropped only if this run created it. DropVectorSearchIndexAsync is itself a safe
                // no-op if the index turns out to be absent (for example if creation never actually got far
                // enough to succeed).
                if (provisioner.CreatedByThisRun)
                {
                    await indexManager.DropVectorSearchIndexAsync();
                }
            });
    }

    /// <summary>
    /// Bounded polling that repeatedly invokes <see cref="ParentDocumentRetriever.SearchAsync"/> until it returns a
    /// non-empty result or <paramref name="timeout"/> elapses -- Atlas Vector Search indexes newly written
    /// documents asynchronously, so an immediate query can race the index. Not part of the production retrieval
    /// contract, which never polls on a caller's behalf.
    /// </summary>
    private static async Task<IReadOnlyList<ParentSearchResult>> PollUntilNonEmptyAsync(
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
            return [];
        }
    }

    /// <summary>A fixed-vector embedding generator used only by this integration test.</summary>
    private sealed class FixedVectorEmbeddingGenerator :
        Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>
    {
        public Task<Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            Microsoft.Extensions.AI.EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var generated = new Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>();
            foreach (string value in values)
            {
                float[] vector = value.Contains("widget", StringComparison.OrdinalIgnoreCase)
                    ? [1, 0, 0]
                    : [0, 1, 0];
                generated.Add(new Microsoft.Extensions.AI.Embedding<float>(vector));
            }

            return Task.FromResult(generated);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Skips this fixture's tests unless <c>MONGODB_URI</c>/<c>MONGODB_DATABASE</c> are configured, matching the
    /// repo-wide credential-gating convention. Unlike <c>MongoDBRAGIntegrationTests</c>, this fixture provisions
    /// and tears down its own uniquely-named Vector Search index via <see cref="MongoDBRAGIndexManager"/>, so it
    /// needs no pre-provisioned index -- only Atlas Vector Search index management support on the target cluster.
    /// </summary>
    internal sealed class MongoIntegrationFactAttribute : FactAttribute
    {
        public MongoIntegrationFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONGODB_URI")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONGODB_DATABASE")))
            {
                Skip = "MONGODB_URI and MONGODB_DATABASE are required for integration-ingestion. This fixture " +
                    "provisions and drops its own uniquely-named Atlas Vector Search index and only touches " +
                    "documents under its own unique, test-owned source ID.";
            }
        }
    }
}
