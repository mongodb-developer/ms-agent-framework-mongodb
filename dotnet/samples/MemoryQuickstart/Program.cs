using Microsoft.Extensions.AI;
using MongoDB.AgentFramework;
using MongoDB.Driver;

string uri = Environment.GetEnvironmentVariable("MONGODB_URI")
    ?? throw new InvalidOperationException("Set MONGODB_URI.");
string databaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE")
    ?? throw new InvalidOperationException("Set MONGODB_DATABASE.");
string collectionName = Environment.GetEnvironmentVariable("MONGODB_MEMORY_COLLECTION")
    ?? "agent_framework_memories";

using var client = new MongoClient(uri);
IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator =
    new SampleEmbeddingGenerator();

await using var memory = new MongoDBMemoryProvider(
    client.GetDatabase(databaseName),
    collectionName,
    embeddingGenerator,
    vectorDimensions: 3,
    _ => new MongoDBMemoryProvider.State(
        new MongoDBMemoryScope(
            applicationId: "quickstart",
            userId: "user-123")));

var storageScope = new MongoDBMemoryScope(
    applicationId: "quickstart",
    userId: "user-123",
    sessionId: "session-1");
var retrievalScope = new MongoDBMemoryScope(applicationId: "quickstart", userId: "user-123");
await memory.StoreAsync(
    [new ChatMessage(ChatRole.User, "I prefer blue.")],
    storageScope);
await memory.EnsureVectorSearchIndexAsync(waitUntilReady: true);
IReadOnlyList<MongoDBMemorySearchResult> results = await PollUntilSearchableAsync(
    memory,
    "blue preference",
    retrievalScope,
    timeout: TimeSpan.FromSeconds(30),
    pollInterval: TimeSpan.FromSeconds(1));
Console.WriteLine(results[0].Message.Text);

static async Task<IReadOnlyList<MongoDBMemorySearchResult>> PollUntilSearchableAsync(
    MongoDBMemoryProvider memory,
    string query,
    MongoDBMemoryScope scope,
    TimeSpan timeout,
    TimeSpan pollInterval)
{
    using var cts = new CancellationTokenSource(timeout);
    try
    {
        while (true)
        {
            IReadOnlyList<MongoDBMemorySearchResult> results = await memory.SearchAsync(
                query,
                scope,
                exact: true,
                cancellationToken: cts.Token);
            if (results.Count > 0)
            {
                return results;
            }

            await Task.Delay(pollInterval, cts.Token);
        }
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
        throw new TimeoutException("The stored memory did not become searchable within the bounded wait.");
    }
}

sealed class SampleEmbeddingGenerator :
    IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            values.Select(static value => new Embedding<float>(
                value.Contains("blue", StringComparison.OrdinalIgnoreCase)
                    ? new float[] { 1, 0, 0 }
                    : new float[] { 0, 1, 0 }))));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
