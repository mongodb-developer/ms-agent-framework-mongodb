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

await memory.EnsureVectorSearchIndexAsync(waitUntilReady: true);
await memory.StoreAsync(
    [new ChatMessage(ChatRole.User, "I prefer blue.")],
    new MongoDBMemoryScope(
        applicationId: "quickstart",
        userId: "user-123",
        sessionId: "session-1"));
IReadOnlyList<MongoDBMemorySearchResult> results = await memory.SearchAsync(
    "What color do I prefer?",
    new MongoDBMemoryScope(applicationId: "quickstart", userId: "user-123"));
Console.WriteLine(results.FirstOrDefault()?.Message.Text ?? "No memory found.");

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
