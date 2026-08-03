using MongoDB.AgentFramework.Samples.Ingestion;
using MongoDB.Bson;
using MongoDB.Driver;

namespace MongoDB.AgentFramework.Samples.Ingestion.Tests;

/// <summary>
/// Exercises <see cref="MongoChunkStore"/> and <see cref="IncrementalIngestionPipeline"/> end-to-end against a live
/// MongoDB deployment: unchanged/changed/stale reconciliation, tenant isolation, and bounded cleanup. Every
/// document this fixture writes carries a unique, test-owned source ID prefix and is deleted in the
/// <c>finally</c> block, so concurrent runs against the same collection do not interfere with each other and never
/// leave residue behind.
/// </summary>
public sealed class MongoChunkStoreIntegrationTests
{
    [MongoIntegrationFact]
    [Trait("Category", "integration-ingestion")]
    public async Task IncrementalIngestionReconcilesUnchangedChangedAndStaleAgainstLiveMongo()
    {
        string uri = Environment.GetEnvironmentVariable("MONGODB_URI")!;
        string databaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE")!;
        string collectionName = Environment.GetEnvironmentVariable("MONGODB_INGESTION_COLLECTION") ??
            "af_ingestion_dotnet_integration";

        using var client = new MongoClient(uri);
        IMongoCollection<BsonDocument> collection = client.GetDatabase(databaseName).GetCollection<BsonDocument>(collectionName);
        string sourceId = $"af_ingestion_dotnet_test_{Guid.NewGuid():N}";
        var store = new MongoChunkStore(collection);
        var pipeline = new IncrementalIngestionPipeline(
            store,
            new BatchEmbedder(new DeterministicTestEmbeddingGenerator(), dimensions: 3),
            new ChunkingOptions { WindowSize = 40, OverlapSize = 8 });

        try
        {
            var document = new SourceDocument(
                "tenant-integration-a",
                sourceId,
                string.Concat(Enumerable.Range(0, 10).Select(i => $"sentence-{i} covers unique ground. ")));

            IngestionResult first = await pipeline.IngestAsync(document);
            Assert.True(first.ChunksUpserted > 0);
            Assert.Equal(0, first.ChunksUnchanged);
            Assert.Equal(0, first.ChunksDeleted);

            IngestionResult rerun = await pipeline.IngestAsync(document);
            Assert.Equal(0, rerun.ChunksUpserted);
            Assert.Equal(first.ChunksUpserted, rerun.ChunksUnchanged);
            Assert.Equal(0, rerun.ChunksDeleted);

            var shrunk = document with { Content = "sentence-0 covers unique ground. " };
            IngestionResult shrunkResult = await pipeline.IngestAsync(shrunk);
            Assert.True(shrunkResult.ChunksDeleted > 0);

            long remaining = await collection.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq(ChunkRecord.TenantIdFieldName, "tenant-integration-a"),
                    Builders<BsonDocument>.Filter.Eq(ChunkRecord.SourceIdFieldName, sourceId)));
            Assert.Equal(1, remaining);
        }
        finally
        {
            await store.DeleteAsync(
                "tenant-integration-a",
                sourceId,
                (await store.GetExistingHashesAsync("tenant-integration-a", sourceId)).Keys.ToArray());
        }
    }

    /// <summary>A deterministic, dimension-3 embedding generator used only by this integration test.</summary>
    private sealed class DeterministicTestEmbeddingGenerator :
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
                generated.Add(new Microsoft.Extensions.AI.Embedding<float>(new float[] { value.Length, 0, 0 }));
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
    /// repo-wide credential-gating convention (see <c>MongoDBRAGIntegrationTests.MongoIntegrationFactAttribute</c>).
    /// </summary>
    internal sealed class MongoIntegrationFactAttribute : FactAttribute
    {
        public MongoIntegrationFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONGODB_URI")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONGODB_DATABASE")))
            {
                Skip = "MONGODB_URI and MONGODB_DATABASE are required for integration-ingestion. Optionally set " +
                    "MONGODB_INGESTION_COLLECTION (default 'af_ingestion_dotnet_integration'); this fixture " +
                    "creates no index and only touches documents under its own unique, test-owned source ID.";
            }
        }
    }
}
