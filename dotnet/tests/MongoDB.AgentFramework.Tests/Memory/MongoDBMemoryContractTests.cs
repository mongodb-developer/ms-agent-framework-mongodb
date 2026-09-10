using System.Text.Json;
using MongoDB.Bson;

namespace MongoDB.AgentFramework.Tests.Memory;

public sealed class MongoDBMemoryContractTests
{
    [Fact]
    public async Task LanguageNeutralScopeFiltersAreInsideVectorSearch()
    {
        string fixturePath = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", "..",
                "tests", "fixtures", "memory", "scope-filters.json"));
        using JsonDocument fixture = JsonDocument.Parse(
            await File.ReadAllTextAsync(fixturePath));

        foreach (JsonElement item in fixture.RootElement.GetProperty("cases").EnumerateArray())
        {
            JsonElement providerScope = item.GetProperty("provider_scope");
            string? sessionId = item.GetProperty("session_id").ValueKind == JsonValueKind.Null
                ? null
                : item.GetProperty("session_id").GetString();
            var scope = new MongoDBMemoryScope(
                Property(providerScope, "application_id"),
                Property(providerScope, "agent_id"),
                Property(providerScope, "user_id"),
                sessionId);
            var state = new MemoryCollectionState();
            var provider = new MongoDBMemoryProvider(
                MemoryCollectionProxy.Create(state),
                new RecordingEmbeddingGenerator(),
                3,
                _ => new MongoDBMemoryProvider.State(scope));

            await provider.SearchAsync("contract query", scope);

            BsonDocument actual =
                state.AggregateStages[0]["$vectorSearch"]["filter"].AsBsonDocument;
            BsonDocument expected = BsonDocument.Parse(
                item.GetProperty("expected_filter").GetRawText());
            Assert.Equal(expected, actual);
        }
    }

    private static string? Property(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value)
            ? value.GetString()
            : null;
}
