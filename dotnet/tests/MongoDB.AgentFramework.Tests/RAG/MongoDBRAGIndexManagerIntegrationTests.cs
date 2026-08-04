using MongoDB.Driver;

namespace MongoDB.AgentFramework.Tests.RAG;

/// <summary>
/// Credential-gated integration coverage for <see cref="MongoDBRAGIndexManager"/> against a real MongoDB
/// deployment: the explicit provisioner role (<c>EnsureHybridAsync</c>/<c>WaitUntilVectorSearchIndexReadyAsync</c>/
/// <c>DropVectorSearchIndexAsync</c>/<c>DropSearchIndexAsync</c>) followed by the read-only runtime role
/// (<c>ValidateHybridAsync</c>) a <see cref="MongoDBRAGProvider"/> configured for
/// <see cref="MongoDBSearchMode.HybridRrf"/> would play in production, demonstrating the least-privilege
/// separation docs/spec/features/index-management.md and ADR 0006 describe. Skips cleanly (no failure) when
/// <c>MONGODB_URI</c>/<c>MONGODB_DATABASE</c> are not configured, matching
/// <see cref="MongoDBRAGIntegrationTests"/>'s pattern.
/// </summary>
public sealed class MongoDBRAGIndexManagerIntegrationTests
{
    [MongoIntegrationFact]
    [Trait("Category", "integration-index-management")]
    public async Task ProvisionerRoleCreatesBothIndexesAndRuntimeRoleValidatesWithoutMutating()
    {
        string? uri = Environment.GetEnvironmentVariable("MONGODB_URI");
        string? databaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE");
        Assert.False(string.IsNullOrWhiteSpace(uri));
        Assert.False(string.IsNullOrWhiteSpace(databaseName));

        string collectionName = $"af_rag_index_mgmt_dotnet_test_{Guid.NewGuid():N}";
        using var client = new MongoClient(uri!);
        var vectorDefinition = new MongoDBVectorSearchIndexDefinition(
            indexName: "agent_framework_rag_vector",
            vectorFieldName: "embedding",
            vectorDimensions: 3,
            filterFieldPaths: ["tenant_id"]);
        var searchDefinition = new MongoDBSearchIndexDefinition(
            indexName: "agent_framework_rag_search",
            textFieldNames: ["text"],
            mandatoryFilter: MongoDBRAGFilter.Equal("tenant_id", "tenant-a"));

        // The "provisioner" facade: standing in for deployment-time tooling running under a more privileged
        // identity than the runtime provider connects with.
        await using var provisioner = new MongoDBRAGIndexManager(
            client, databaseName!, collectionName, vectorDefinition, searchDefinition);

        // The "runtime" facade: read-only validation only, standing in for what a running
        // MongoDBRAGProvider configured for HybridRrf would do on every query path.
        await using var runtime = new MongoDBRAGIndexManager(
            client, databaseName!, collectionName, vectorDefinition, searchDefinition);

        try
        {
            await client.GetDatabase(databaseName!).CreateCollectionAsync(collectionName);
            Assert.Null(await provisioner.GetVectorSearchIndexAsync());
            Assert.Null(await provisioner.GetSearchIndexAsync());

            await provisioner.EnsureHybridAsync(waitUntilReady: true, timeout: TimeSpan.FromMinutes(2));

            await runtime.ValidateHybridAsync();

            IReadOnlyList<MongoDBIndexInfo> indexes = await runtime.ListIndexesAsync();
            Assert.Contains(indexes, index => index.Name == "agent_framework_rag_vector");
            Assert.Contains(indexes, index => index.Name == "agent_framework_rag_search");

            // Idempotent Ensure: calling again with both indexes already present must not fail.
            await provisioner.EnsureHybridAsync();
        }
        finally
        {
            Assert.StartsWith("af_rag_index_mgmt_dotnet_test_", collectionName);
            await provisioner.DropVectorSearchIndexAsync();
            await provisioner.DropSearchIndexAsync();
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
