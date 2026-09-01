using Microsoft.Extensions.AI;
using MongoDB.AgentFramework;

string uri = Environment.GetEnvironmentVariable("MONGODB_URI") ??
    throw new InvalidOperationException("Set MONGODB_URI.");
string database = Environment.GetEnvironmentVariable("MONGODB_DATABASE") ??
    throw new InvalidOperationException("Set MONGODB_DATABASE.");
string collection = Environment.GetEnvironmentVariable("MONGODB_HISTORY_COLLECTION") ??
    "chat_history";
string sessionId = Environment.GetEnvironmentVariable("MONGODB_HISTORY_SESSION_ID") ??
    "history-quickstart-session";

await using var history = new MongoDBChatHistoryProvider(
    uri,
    database,
    collection,
    new MongoDBChatHistoryProviderOptions
    {
        ApplicationId = Environment.GetEnvironmentVariable("MONGODB_HISTORY_APPLICATION_ID") ??
            "history-quickstart",
        AgentId = Environment.GetEnvironmentVariable("MONGODB_HISTORY_AGENT_ID") ??
            "sample-agent",
        SessionId = sessionId,
        MaxMessages = 20,
    });

await history.EnsureIndexesAsync();
await history.SaveMessagesAsync(
    sessionId,
    [
        new ChatMessage(ChatRole.User, "Hello from MongoDB Chat History.")
        {
            MessageId = $"sample-{Guid.NewGuid():N}",
        },
    ]);

foreach (ChatMessage message in await history.GetMessagesAsync(sessionId))
{
    Console.WriteLine($"{message.Role}: {message.Text}");
}

if (string.Equals(
    Environment.GetEnvironmentVariable("MONGODB_HISTORY_CLEAR"),
    "true",
    StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine($"Cleared {await history.ClearMessagesAsync(sessionId)} messages.");
}
