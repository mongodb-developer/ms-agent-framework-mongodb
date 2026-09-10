using Microsoft.Extensions.AI;
using MongoDB.Driver;

namespace MongoDB.AgentFramework.Tests.History;

public sealed class MongoDBChatHistoryIntegrationTests
{
    [MongoHistoryIntegrationFact]
    [Trait("Category", "integration-history")]
    public async Task ExactReloadRetryIsolationAndAuthorizedCleanup()
    {
        string uri = Environment.GetEnvironmentVariable("MONGODB_URI")!;
        string databaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE")!;
        string collectionName = $"af_history_dotnet_test_{Guid.NewGuid():N}";
        using var client = new MongoClient(uri);
        IMongoCollection<MongoDB.Bson.BsonDocument> collection =
            client.GetDatabase(databaseName).GetCollection<MongoDB.Bson.BsonDocument>(collectionName);

        static MongoDBChatHistoryProviderOptions Options(string tenantId) =>
            new()
            {
                TenantId = tenantId,
                ApplicationId = "integration-history",
                AgentId = "history-agent",
                SessionId = "session-a",
                MaxMessages = 3,
                Retention = TimeSpan.FromDays(1),
            };

        var provider = new MongoDBChatHistoryProvider(collection, Options("tenant-a"));
        var reloaded = new MongoDBChatHistoryProvider(collection, Options("tenant-a"));
        var otherTenant = new MongoDBChatHistoryProvider(collection, Options("tenant-b"));
        try
        {
            await provider.EnsureIndexesAsync();
            await provider.ValidateIndexesAsync();
            ChatMessage[] firstBatch =
            [
                new(
                    ChatRole.User,
                    [
                        new TextContent("weather"),
                        new UriContent("https://example.invalid/radar.png", "image/png"),
                    ])
                {
                    MessageId = "input-1",
                    AdditionalProperties = new AdditionalPropertiesDictionary
                    {
                        ["fixture"] = new Dictionary<string, object?> { ["lossless"] = true },
                    },
                },
                new(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        "weather-1",
                        "weather",
                        new Dictionary<string, object?> { ["city"] = "London" })])
                {
                    MessageId = "call-1",
                },
                new(
                    ChatRole.Tool,
                    [new FunctionResultContent(
                        "weather-1",
                        new Dictionary<string, object?> { ["temperature"] = 19 })])
                {
                    MessageId = "result-1",
                },
            ];
            await provider.SaveMessagesAsync("session-a", firstBatch);
            await provider.SaveMessagesAsync("session-a", firstBatch);
            await otherTenant.SaveMessagesAsync(
                "session-a",
                [new ChatMessage(ChatRole.User, "isolated") { MessageId = "other-1" }]);
            await reloaded.SaveMessagesAsync(
                "session-a",
                [new ChatMessage(ChatRole.Assistant, "It is 19 C.") { MessageId = "answer-1" }]);

            IReadOnlyList<ChatMessage> restored = await reloaded.GetMessagesAsync("session-a");

            Assert.Equal(["call-1", "result-1", "answer-1"], restored.Select(m => m.MessageId));
            Assert.IsType<FunctionCallContent>(restored[0].Contents.Single());
            Assert.IsType<FunctionResultContent>(restored[1].Contents.Single());
            Assert.Equal(4, await reloaded.ClearMessagesAsync("session-a"));
            Assert.Equal(
                ["other-1"],
                (await otherTenant.GetMessagesAsync("session-a")).Select(m => m.MessageId));
            Assert.Equal(1, await otherTenant.ClearMessagesAsync("session-a"));
        }
        finally
        {
            Assert.StartsWith("af_history_dotnet_test_", collectionName);
            await client.GetDatabase(databaseName).DropCollectionAsync(collectionName);
            await provider.DisposeAsync();
            await reloaded.DisposeAsync();
            await otherTenant.DisposeAsync();
        }
    }

    private sealed class MongoHistoryIntegrationFactAttribute : FactAttribute
    {
        public MongoHistoryIntegrationFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONGODB_URI")) ||
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MONGODB_DATABASE")))
            {
                Skip = "MONGODB_URI and MONGODB_DATABASE are required for integration-history.";
            }
        }
    }
}
