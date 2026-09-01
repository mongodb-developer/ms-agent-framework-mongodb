using MongoDB.Driver;

namespace MongoDB.AgentFramework.Tests.Memory;

/// <summary>
/// Credential-gated integration coverage for <see cref="MongoDBMemoryIndexManager"/> against a real MongoDB
/// deployment: the explicit provisioner role (<c>EnsureIndexAsync</c>/<c>WaitUntilReadyAsync</c>/<c>DropIndexAsync</c>)
/// followed by the read-only runtime role (<c>ValidateIndexAsync</c>) a <see cref="MongoDBMemoryProvider"/> would
/// play in production, demonstrating the least-privilege separation docs/spec/features/index-management.md and
/// ADR 0006 describe. Skips cleanly (no failure) when <c>MONGODB_URI</c>/<c>MONGODB_DATABASE</c> are not
/// configured, matching <see cref="MongoDBMemoryIntegrationTests"/>'s pattern.
/// </summary>
public sealed class MongoDBMemoryIndexManagerIntegrationTests
{
    [MongoIntegrationFact]
    [Trait("Category", "integration-index-management")]
    public async Task ProvisionerRoleCreatesAndRuntimeRoleValidatesWithoutMutating()
    {
        string? uri = Environment.GetEnvironmentVariable("MONGODB_URI");
        string? databaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE");
        Assert.False(string.IsNullOrWhiteSpace(uri));
        Assert.False(string.IsNullOrWhiteSpace(databaseName));

        string collectionName = $"af_memory_index_mgmt_dotnet_test_{Guid.NewGuid():N}";
        using var client = new MongoClient(uri!);
        var definition = new MongoDBVectorSearchIndexDefinition(
            indexName: "agent_framework_memory",
            vectorFieldName: "content_embedding",
            vectorDimensions: 3,
            filterFieldPaths: ["application_id", "agent_id", "user_id", "session_id"]);

        // The "provisioner" facade: a distinct instance, standing in for tooling that runs under a more
        // privileged, deployment-time identity than the runtime provider connects with.
        await using var provisioner = new MongoDBMemoryIndexManager(client, databaseName!, collectionName, definition);

        // The "runtime" facade: read-only validation only, standing in for what a running MongoDBMemoryProvider
        // would do on every query path -- it must never create, update, or drop the index.
        await using var runtime = new MongoDBMemoryIndexManager(client, databaseName!, collectionName, definition);

        try
        {
            await client.GetDatabase(databaseName!).CreateCollectionAsync(collectionName);
            Assert.Null(await provisioner.GetIndexAsync());

            MongoDBIndexInfo created = await provisioner.EnsureIndexAsync(
                waitUntilReady: true,
                timeout: TimeSpan.FromMinutes(2));
            Assert.Equal(MongoDBIndexStatus.Ready, created.Status);
            Assert.True(created.Queryable);

            MongoDBIndexComparison comparison = await runtime.ValidateIndexAsync();
            Assert.True(comparison.IsCompatible);

            IReadOnlyList<MongoDBIndexInfo> indexes = await runtime.ListIndexesAsync();
            Assert.Contains(indexes, index => index.Name == "agent_framework_memory");

            // Idempotent Ensure: calling again with the index already present must not fail or attempt to
            // recreate it.
            MongoDBIndexInfo reEnsured = await provisioner.EnsureIndexAsync();
            Assert.Equal(MongoDBIndexStatus.Ready, reEnsured.Status);
        }
        finally
        {
            Assert.StartsWith("af_memory_index_mgmt_dotnet_test_", collectionName);
            await provisioner.DropIndexAsync();
            await client.GetDatabase(databaseName!).DropCollectionAsync(collectionName);
        }
    }

    internal sealed class MongoIntegrationFactAttribute : FactAttribute
    {
        public MongoIntegrationFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONGODB_URI")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONGODB_DATABASE")))
            {
                Skip = "MONGODB_URI and MONGODB_DATABASE are required for integration-index-management.";
            }
        }
    }
}
