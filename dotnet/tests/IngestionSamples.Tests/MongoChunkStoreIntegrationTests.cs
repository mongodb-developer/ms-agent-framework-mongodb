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

    [MongoIntegrationFact]
    [Trait("Category", "integration-ingestion")]
    public async Task DeleteSourceAsyncDeletesMoreThanOneBatchOfRecordsScopedToTenantAndSourceOnlyAgainstLiveMongo()
    {
        string uri = Environment.GetEnvironmentVariable("MONGODB_URI")!;
        string databaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE")!;
        string collectionName = Environment.GetEnvironmentVariable("MONGODB_INGESTION_COLLECTION") ??
            "af_ingestion_dotnet_integration";

        using var client = new MongoClient(uri);
        IMongoCollection<BsonDocument> collection = client.GetDatabase(databaseName).GetCollection<BsonDocument>(collectionName);
        string sourceId = $"af_ingestion_dotnet_test_{Guid.NewGuid():N}";
        string otherSourceId = $"af_ingestion_dotnet_test_{Guid.NewGuid():N}";
        var store = new MongoChunkStore(collection);
        const string tenantId = "tenant-integration-delete-source";

        try
        {
            // Seed more than one MongoChunkStore.MaxBatchSize (500) worth of records for the source under test, so
            // DeleteSourceAsync must page across more than one round trip, plus one record for a second source (and
            // one for another tenant sharing the same source ID) that must both survive untouched.
            ChunkRecord[] records = [.. Enumerable.Range(0, MongoChunkStore.MaxBatchSize + 25).Select(i =>
                new ChunkRecord(
                    $"{sourceId}_chunk_{i}", tenantId, sourceId, ParentId: null, ChunkRecord.FlatChunkRecordType,
                    $"text {i}", $"hash-{i}", Embedding: null, SourceName: null, SourceUrl: null))];
            await store.UpsertAsync(records);
            await store.UpsertAsync(
            [
                new ChunkRecord(
                    $"{otherSourceId}_chunk_0", tenantId, otherSourceId, ParentId: null, ChunkRecord.FlatChunkRecordType,
                    "other source text", "hash-other", Embedding: null, SourceName: null, SourceUrl: null),
                new ChunkRecord(
                    $"{sourceId}_other_tenant_chunk_0", "tenant-integration-delete-source-other", sourceId,
                    ParentId: null, ChunkRecord.FlatChunkRecordType, "other tenant text", "hash-other-tenant",
                    Embedding: null, SourceName: null, SourceUrl: null),
            ]);

            IReadOnlyList<string> sourceIdsBefore = await store.ListSourceIdsAsync(tenantId);
            Assert.Contains(sourceId, sourceIdsBefore);
            Assert.Contains(otherSourceId, sourceIdsBefore);

            int deleted = await store.DeleteSourceAsync(tenantId, sourceId);

            Assert.Equal(records.Length, deleted);
            long remainingForDeletedSource = await collection.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq(ChunkRecord.TenantIdFieldName, tenantId),
                    Builders<BsonDocument>.Filter.Eq(ChunkRecord.SourceIdFieldName, sourceId)));
            Assert.Equal(0, remainingForDeletedSource);

            // The other source (same tenant) and the other tenant's same-named source must both be untouched.
            long remainingOtherSource = await collection.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq(ChunkRecord.TenantIdFieldName, tenantId),
                    Builders<BsonDocument>.Filter.Eq(ChunkRecord.SourceIdFieldName, otherSourceId)));
            Assert.Equal(1, remainingOtherSource);
            long remainingOtherTenant = await collection.CountDocumentsAsync(
                Builders<BsonDocument>.Filter.And(
                    Builders<BsonDocument>.Filter.Eq(ChunkRecord.TenantIdFieldName, "tenant-integration-delete-source-other"),
                    Builders<BsonDocument>.Filter.Eq(ChunkRecord.SourceIdFieldName, sourceId)));
            Assert.Equal(1, remainingOtherTenant);

            IReadOnlyList<string> sourceIdsAfter = await store.ListSourceIdsAsync(tenantId);
            Assert.DoesNotContain(sourceId, sourceIdsAfter);
            Assert.Contains(otherSourceId, sourceIdsAfter);
        }
        finally
        {
            await store.DeleteSourceAsync(tenantId, sourceId);
            await store.DeleteSourceAsync(tenantId, otherSourceId);
            await store.DeleteSourceAsync("tenant-integration-delete-source-other", sourceId);
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
