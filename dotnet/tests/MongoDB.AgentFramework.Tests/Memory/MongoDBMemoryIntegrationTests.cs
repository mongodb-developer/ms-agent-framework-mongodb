using Microsoft.Extensions.AI;
using MongoDB.Driver;

namespace MongoDB.AgentFramework.Tests.Memory;

public sealed class MongoDBMemoryIntegrationTests
{
    [MongoIntegrationFact]
    [Trait("Category", "integration-memory")]
    public async Task StoreSearchAndScopedCleanupOnConfiguredDeployment()
    {
        string? uri = Environment.GetEnvironmentVariable("MONGODB_URI");
        string? databaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE");
        Assert.False(string.IsNullOrWhiteSpace(uri));
        Assert.False(string.IsNullOrWhiteSpace(databaseName));

        string collectionName = $"af_memory_dotnet_test_{Guid.NewGuid():N}";
        using var client = new MongoClient(uri!);
        var provider = new MongoDBMemoryProvider(
            client,
            databaseName!,
            collectionName,
            new RecordingEmbeddingGenerator(),
            3,
            _ => new MongoDBMemoryProvider.State(
                new MongoDBMemoryScope("integration-memory", userId: "user-a")),
            new MongoDBMemoryProviderOptions { NumCandidates = 10 });
        var other = new MongoDBMemoryProvider(
            client,
            databaseName!,
            collectionName,
            new RecordingEmbeddingGenerator(),
            3,
            _ => new MongoDBMemoryProvider.State(
                new MongoDBMemoryScope("integration-memory", userId: "user-b")),
            new MongoDBMemoryProviderOptions { NumCandidates = 10 });
        try
        {
            await provider.StoreAsync(
                [new ChatMessage(ChatRole.User, "Remember that blue is preferred.")],
                new MongoDBMemoryScope(
                    "integration-memory",
                    userId: "user-a",
                    sessionId: "session-a"));
            await other.StoreAsync(
                [new ChatMessage(ChatRole.User, "Cross-tenant blue must not be returned.")],
                new MongoDBMemoryScope(
                    "integration-memory",
                    userId: "user-b",
                    sessionId: "session-b"));
            await provider.EnsureVectorSearchIndexAsync(
                waitUntilReady: true,
                timeout: TimeSpan.FromMinutes(2));

            IReadOnlyList<MongoDBMemorySearchResult> results =
                await provider.SearchAsync(
                    "blue",
                    new MongoDBMemoryScope("integration-memory", userId: "user-a"),
                    maxResults: 10,
                    exact: true);

            Assert.Single(results);
            Assert.Equal(
                "Remember that blue is preferred.",
                results[0].Message.Text);
            Assert.Equal(
                1,
                await provider.ClearSessionAsync(
                    "session-a",
                    new MongoDBMemoryScope("integration-memory", userId: "user-a")));
        }

        finally
        {
            Assert.StartsWith("af_memory_dotnet_test_", collectionName);
            await client.GetDatabase(databaseName!).DropCollectionAsync(collectionName);
            await provider.DisposeAsync();
            await other.DisposeAsync();
        }
    }

    internal sealed class MongoIntegrationFactAttribute : FactAttribute
    {
        public MongoIntegrationFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONGODB_URI")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONGODB_DATABASE")))
            {
                Skip = "MONGODB_URI and MONGODB_DATABASE are required for integration-memory.";
            }
        }
    }
}
