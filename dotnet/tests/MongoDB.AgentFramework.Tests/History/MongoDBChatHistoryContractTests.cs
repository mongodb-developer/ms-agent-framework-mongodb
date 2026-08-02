using Microsoft.Extensions.AI;
using System.Text.Json;

namespace MongoDB.AgentFramework.Tests.History;

public sealed class MongoDBChatHistoryContractTests
{
    [Fact]
    public async Task MatchesLanguageNeutralLatestAndRetryFixture()
    {
        using JsonDocument fixture = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "Fixtures", "history_contract.json")));
        JsonElement root = fixture.RootElement;
        JsonElement scope = root.GetProperty("scope");
        Assert.Equal(MongoDBChatHistoryProvider.SchemaVersion, root.GetProperty("schema_version").GetInt32());
        Assert.Equal(
            MongoDBChatHistoryProvider.FrameworkSerializationVersion,
            root.GetProperty("framework_version").GetInt32());
        Assert.Equal(JsonValueKind.Null, scope.GetProperty("user_id").ValueKind);
        var state = new HistoryCollectionState();
        var provider = new MongoDBChatHistoryProvider(
            HistoryCollectionProxy.Create(state),
            new MongoDBChatHistoryProviderOptions
            {
                TenantId = scope.GetProperty("tenant_id").GetString(),
                ApplicationId = scope.GetProperty("application_id").GetString()!,
                AgentId = scope.GetProperty("agent_id").GetString()!,
                SessionId = scope.GetProperty("session_id").GetString()!,
                MaxMessages = root.GetProperty("max_messages").GetInt32(),
            });
        ChatMessage[] messages = root.GetProperty("messages").EnumerateArray().Select(item =>
            new ChatMessage(
                new ChatRole(item.GetProperty("role").GetString()!),
                item.GetProperty("text").GetString())
            {
                MessageId = item.GetProperty("message_id").GetString(),
            }).ToArray();

        await provider.SaveMessagesAsync(scope.GetProperty("session_id").GetString()!, messages);
        await provider.SaveMessagesAsync(scope.GetProperty("session_id").GetString()!, messages);
        IReadOnlyList<ChatMessage> restored = await provider.GetMessagesAsync(
            scope.GetProperty("session_id").GetString()!);

        Assert.Equal(
            root.GetProperty("expected_latest_chronological_ids")
                .EnumerateArray()
                .Select(value => value.GetString()),
            restored.Select(message => message.MessageId));
        Assert.Equal(
            root.GetProperty("retry_expected_document_count").GetInt32(),
            state.Documents.Count(document => document["_kind"] == "message"));
        Assert.All(
            state.Documents.Where(document => document["_kind"] == "message"),
            document => Assert.Equal(
                root.GetProperty("expected_scope_discriminator").GetString(),
                document["scope_discriminator"].AsString));
    }
}
