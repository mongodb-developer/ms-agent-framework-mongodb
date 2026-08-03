using MongoDB.Driver;
using System.Text.Json;

namespace MongoDB.AgentFramework.Tests.Persistence;

public sealed class MongoDBCheckpointStoreIntegrationTests
{
    [MongoPersistenceIntegrationFact]
    [Trait("Category", "integration-persistence")]
    public async Task ExactRoundTripLineagePaginationTtlAndAuthorizedCleanup()
    {
        string uri = Environment.GetEnvironmentVariable("MONGODB_URI")!;
        string databaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE")!;
        string collectionName = $"af_persistence_dotnet_test_{Guid.NewGuid():N}";
        using var client = new MongoClient(uri);
        IMongoCollection<MongoDB.Bson.BsonDocument> collection =
            client.GetDatabase(databaseName).GetCollection<MongoDB.Bson.BsonDocument>(collectionName);

        static MongoDBCheckpointStoreOptions Options(string tenantId) =>
            new()
            {
                TenantId = tenantId,
                WorkflowId = "integration-persistence-workflow",
                DefaultExpiration = TimeSpan.FromDays(1),
                ContinuationTokenSigningKey = CheckpointStoreTestSigningKey.Bytes,
            };

        var store = new MongoDBCheckpointStore(collection, Options("tenant-a"));
        var otherTenant = new MongoDBCheckpointStore(collection, Options("tenant-b"));
        try
        {
            await store.EnsureIndexesAsync();
            await store.ValidateIndexesAsync();

            JsonElement payload = JsonDocument.Parse("""{"resume_state":"running","step":1}""").RootElement;
            MongoDBCheckpointRecord root = await store.SaveCheckpointAsync("run-a", "root", payload);
            MongoDBCheckpointRecord child = await store.SaveCheckpointAsync(
                "run-a", "child", payload, parentCheckpointId: "root");

            MongoDBCheckpointRecord? crossTenant = await otherTenant.LoadCheckpointAsync("run-a", "root");
            Assert.Null(crossTenant);

            // Retrying an identical save after real elapsed time should converge (not conflict) on the
            // originally persisted default expiry, without extending it.
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            MongoDBCheckpointRecord rootRetried = await store.SaveCheckpointAsync("run-a", "root", payload);
            Assert.Equal(root.Sequence, rootRetried.Sequence);
            Assert.Equal(root.ExpiresAt, rootRetried.ExpiresAt);

            MongoDBCheckpointRecord? latest = await store.GetLatestCheckpointAsync("run-a");
            Assert.NotNull(latest);
            Assert.Equal(child.CheckpointId, latest!.CheckpointId);

            MongoDBCheckpointPage page = await store.ListCheckpointsAsync("run-a", limit: 1);
            Assert.Single(page.Items);
            Assert.NotNull(page.ContinuationToken);
            MongoDBCheckpointPage secondPage =
                await store.ListCheckpointsAsync("run-a", limit: 1, page.ContinuationToken);
            Assert.Single(secondPage.Items);
            Assert.Null(secondPage.ContinuationToken);

            MongoDBCheckpointRecord? reloaded = await store.LoadCheckpointAsync("run-a", "root");
            Assert.NotNull(reloaded);
            Assert.Equal("running", reloaded!.Payload.GetProperty("resume_state").GetString());
            Assert.NotNull(reloaded.ExpiresAt);

            Assert.True(await store.DeleteCheckpointAsync("run-a", "child"));
            Assert.Null(await store.LoadCheckpointAsync("run-a", "child"));
        }
        finally
        {
            Assert.StartsWith("af_persistence_dotnet_test_", collectionName);
            await client.GetDatabase(databaseName).DropCollectionAsync(collectionName);
            await store.DisposeAsync();
            await otherTenant.DisposeAsync();
        }
    }

    [MongoPersistenceIntegrationFact]
    [Trait("Category", "integration-persistence")]
    public async Task ConcurrentSaveCheckpointAsyncCallsAgainstARealDeploymentAllocateGaplessDistinctSequences()
    {
        string uri = Environment.GetEnvironmentVariable("MONGODB_URI")!;
        string databaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE")!;
        string collectionName = $"af_persistence_dotnet_test_{Guid.NewGuid():N}";
        using var client = new MongoClient(uri);
        IMongoCollection<MongoDB.Bson.BsonDocument> collection =
            client.GetDatabase(databaseName).GetCollection<MongoDB.Bson.BsonDocument>(collectionName);

        var store = new MongoDBCheckpointStore(
            collection,
            new MongoDBCheckpointStoreOptions
            {
                TenantId = "tenant-concurrency",
                WorkflowId = "integration-persistence-workflow",
                ContinuationTokenSigningKey = CheckpointStoreTestSigningKey.Bytes,
            });
        try
        {
            const int writerCount = 10;
            JsonElement payload = JsonDocument.Parse("""{"resume_state":"running"}""").RootElement;

            // A real replica-set/mongos transaction serializes the shared per-session sequence-counter
            // increment; genuinely concurrent writers must still converge on a gapless, duplicate-free
            // sequence assignment, proving the transactional allocation is actually atomic under real
            // cross-process-shaped concurrency, not merely in the deterministic in-memory fake.
            MongoDBCheckpointRecord[] records = await Task.WhenAll(
                Enumerable.Range(0, writerCount)
                    .Select(i => store.SaveCheckpointAsync("run-concurrent", $"writer-{i}", payload)));

            long[] sequences = records.Select(record => record.Sequence).Order().ToArray();
            Assert.Equal(Enumerable.Range(1, writerCount).Select(i => (long)i), sequences);

            MongoDBCheckpointPage page = await store.ListCheckpointsAsync("run-concurrent", limit: writerCount + 1);
            Assert.Equal(writerCount, page.Items.Count);
            Assert.Null(page.ContinuationToken);
            Assert.Equal(sequences, page.Items.Select(item => item.Sequence).ToArray());
        }
        finally
        {
            Assert.StartsWith("af_persistence_dotnet_test_", collectionName);
            await client.GetDatabase(databaseName).DropCollectionAsync(collectionName);
            await store.DisposeAsync();
        }
    }

    private sealed class MongoPersistenceIntegrationFactAttribute : FactAttribute
    {
        public MongoPersistenceIntegrationFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONGODB_URI")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONGODB_DATABASE")))
            {
                Skip = "MONGODB_URI and MONGODB_DATABASE are required for integration-persistence.";
            }
        }
    }
}
