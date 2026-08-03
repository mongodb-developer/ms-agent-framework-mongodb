#pragma warning disable MAAI001 // AIContextProvider is an evaluation-purposes-only API in this package version.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;
using MongoDB.AgentFramework;
using MongoDB.Bson;
using MongoDB.Driver;

// This slice does not implement Vector Search or Search index provisioning (see
// docs/development/rag/dotnet-rag-vector-search.md and docs/development/rag/dotnet-rag-full-text-search.md), so
// the target collection and indexes must already exist. Set MONGODB_RAG_VECTOR_INDEX to a Vector Search index
// (3-dimension, cosine) defined over the "embedding" field of the target collection before running this sample.
// Set MONGODB_RAG_SEARCH_INDEX to a Search index defined over the "text" field to also see the FullText demo;
// that section is skipped when the variable is unset since this sample cannot provision the index itself.
string uri = Environment.GetEnvironmentVariable("MONGODB_URI")
    ?? throw new InvalidOperationException("Set MONGODB_URI.");
string databaseName = Environment.GetEnvironmentVariable("MONGODB_DATABASE")
    ?? throw new InvalidOperationException("Set MONGODB_DATABASE.");
string collectionName = Environment.GetEnvironmentVariable("MONGODB_RAG_COLLECTION")
    ?? "agent_framework_rag_chunks";
string vectorIndexName = Environment.GetEnvironmentVariable("MONGODB_RAG_VECTOR_INDEX")
    ?? "agent_framework_rag_vector";
string? searchIndexName = Environment.GetEnvironmentVariable("MONGODB_RAG_SEARCH_INDEX");

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

if (searchIndexName is not null)
{
    Console.WriteLine();
    Console.WriteLine("FullText SearchAsync results (no embedding generator invoked):");
    var fullTextOptions = new MongoDBRAGProviderOptions
    {
        SearchMode = MongoDBSearchMode.FullText,
        SearchIndexName = searchIndexName,
        SearchTextFieldNames = ["text"],
        TopK = 3,
        MandatoryFilter = MongoDBRAGFilter.Equal("tenant_id", "quickstart"),
    };
    await using var fullTextProvider = new MongoDBRAGProvider(
        client,
        databaseName,
        collectionName,
        fullTextOptions);

    // Atlas Search indexes newly (re-)seeded documents asynchronously, so an immediate query can race the
    // index and miss "quickstart-chunk-1" even though SeedKnowledgeAsync already completed. Poll boundedly
    // until it is searchable so this sample's output is deterministic; production SearchAsync never polls.
    IReadOnlyList<MongoDBRAGResult> fullTextResults;
    try
    {
        fullTextResults = await PollUntilSearchableAsync(
            fullTextProvider,
            "What color do widgets ship in?",
            "quickstart-chunk-1",
            timeout: TimeSpan.FromSeconds(30),
            pollInterval: TimeSpan.FromSeconds(1));
    }
    catch (TimeoutException ex)
    {
        Console.WriteLine($"  {ex.Message}");
        fullTextResults = [];
    }

    foreach (MongoDBRAGResult result in fullTextResults)
    {
        Console.WriteLine($"  [{result.Score:F3}] {result.Text} (source: {result.SourceName ?? "n/a"})");
    }
}
else
{
    Console.WriteLine();
    Console.WriteLine("Skipping FullText demo: set MONGODB_RAG_SEARCH_INDEX to a Search index over " +
        "the \"text\" field to see it.");
}

/// <summary>
/// Bounded polling that repeatedly invokes <see cref="MongoDBRAGProvider.SearchAsync(string, CancellationToken)"/>
/// until <paramref name="expectedId"/> appears in its results or <paramref name="timeout"/> elapses. This exists
/// only to make this sample's FullText output deterministic across an Atlas Search index's asynchronous indexing
/// lag; it is not part of the production <see cref="MongoDBRAGProvider"/> contract, which never polls on a
/// caller's behalf. Cancellation always propagates as a clear <see cref="TimeoutException"/> rather than a bare
/// <see cref="OperationCanceledException"/>.
/// </summary>
static async Task<IReadOnlyList<MongoDBRAGResult>> PollUntilSearchableAsync(
    MongoDBRAGProvider provider,
    string query,
    string expectedId,
    TimeSpan timeout,
    TimeSpan pollInterval)
{
    using var cts = new CancellationTokenSource(timeout);
    try
    {
        while (true)
        {
            IReadOnlyList<MongoDBRAGResult> results = await provider.SearchAsync(query, cts.Token);
            if (results.Any(result => result.Id == expectedId))
            {
                return results;
            }

            await Task.Delay(pollInterval, cts.Token);
        }
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
        throw new TimeoutException(
            $"Timed out after {timeout} waiting for document '{expectedId}' to become searchable for query " +
            $"'{query}'. This indicates Atlas Search indexing lag exceeded the bounded poll window, not a " +
            "MongoDBRAGProvider defect.");
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
                // Correlate on the subject the query and the seeded documents actually share ("widget" vs.
                // "gadget"), not an incidental detail like a color mentioned in the answer but not the question --
                // otherwise a query like "What color do widgets ship in?" would embed to the same vector as the
                // unrelated gadget document and retrieve the wrong chunk.
                value.Contains("widget", StringComparison.OrdinalIgnoreCase)
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
