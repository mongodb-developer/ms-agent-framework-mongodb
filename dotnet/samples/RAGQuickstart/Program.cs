#pragma warning disable MAAI001 // AIContextProvider is an evaluation-purposes-only API in this package version.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using MongoDB.AgentFramework;
using MongoDB.Bson;
using MongoDB.Driver;

// This slice does not implement Vector Search index provisioning (see
// docs/development/rag/dotnet-rag-vector-search.md), so the target collection and index must already exist.
// Set MONGODB_RAG_VECTOR_INDEX to a Vector Search index (3-dimension, cosine) defined over the "embedding"
// field of the target collection before running this sample.
string uri = Environment.GetEnvironmentVariable("MONGODB_URI")
    ?? throw new InvalidOperationException("Set MONGODB_URI.");
string databaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE")
    ?? throw new InvalidOperationException("Set MONGODB_DATABASE.");
string collectionName = Environment.GetEnvironmentVariable("MONGODB_RAG_COLLECTION")
    ?? "agent_framework_rag_chunks";
string vectorIndexName = Environment.GetEnvironmentVariable("MONGODB_RAG_VECTOR_INDEX")
    ?? "agent_framework_rag_vector";

using var client = new MongoClient(uri);
IMongoCollection<BsonDocument> collection = client
    .GetDatabase(databaseName)
    .GetCollection<BsonDocument>(collectionName);
IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator = new SampleEmbeddingGenerator();

await SeedKnowledgeAsync(collection);

var options = new MongoDBRAGProviderOptions
{
    SearchMode = MongoDBSearchMode.VectorAnn,
    VectorIndexName = vectorIndexName,
    TopK = 3,
    MandatoryFilter = MongoDBRAGFilter.Equal("tenant_id", "quickstart"),
};

await using var ragProvider = new MongoDBRAGProvider(
    client,
    databaseName,
    collectionName,
    embeddingGenerator,
    vectorDimensions: 3,
    options);

Console.WriteLine("Direct SearchAsync results:");
IReadOnlyList<MongoDBRAGResult> results = await ragProvider.SearchAsync("What color do widgets ship in?");
foreach (MongoDBRAGResult result in results)
{
    Console.WriteLine($"  [{result.Score:F3}] {result.Text} (source: {result.SourceName ?? "n/a"})");
}

Console.WriteLine();
Console.WriteLine("MongoDBRAGContextProvider before-invoke context:");
var contextProvider = new MongoDBRAGContextProvider(ragProvider);
AIContext context = await contextProvider.InvokingAsync(
    new AIContextProvider.InvokingContext(
        new SampleAgent(),
        null,
        new AIContext
        {
            Messages = [new ChatMessage(ChatRole.User, "What color do widgets ship in?")],
        }),
    default);
Console.WriteLine($"  Instructions: {context.Instructions}");
foreach (ChatMessage message in context.Messages ?? [])
{
    if (message.AdditionalProperties?.ContainsKey("_rag_id") is true)
    {
        Console.WriteLine($"  [{message.Role}] {message.Text}");
    }
}

static async Task SeedKnowledgeAsync(IMongoCollection<BsonDocument> collection)
{
    var documents = new[]
    {
        new BsonDocument
        {
            { "_id", "quickstart-chunk-1" },
            { "text", "Widgets ship in blue by default." },
            { "embedding", new BsonArray([1.0, 0.0, 0.0]) },
            { "tenant_id", "quickstart" },
            { "source", new BsonDocument { { "name", "Catalog" }, { "url", "https://example.test/catalog" } } },
        },
        new BsonDocument
        {
            { "_id", "quickstart-chunk-2" },
            { "text", "Gadgets ship in red by default." },
            { "embedding", new BsonArray([0.0, 1.0, 0.0]) },
            { "tenant_id", "quickstart" },
            { "source", new BsonDocument { { "name", "Catalog" }, { "url", "https://example.test/catalog" } } },
        },
    };
    foreach (BsonDocument document in documents)
    {
        await collection.ReplaceOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", document["_id"]),
            document,
            new ReplaceOptions { IsUpsert = true });
    }
}

sealed class SampleEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
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

sealed class SampleAgent : AIAgent
{
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedSession,
        JsonSerializerOptions? jsonSerializerOptions,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }
}
