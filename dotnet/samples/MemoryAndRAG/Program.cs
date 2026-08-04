#pragma warning disable MAAI001 // AIContextProvider is an evaluation-purposes-only API in this package version.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using MongoDB.AgentFramework;
using MongoDB.Bson;
using MongoDB.Driver;

MemoryAndRagOptions command = MemoryAndRagOptions.Parse(args);
MemoryAndRagSettings settings = MemoryAndRagSettings.Load();
var embeddingGenerator = new SampleEmbeddingGenerator();
var retrievalScope = new MongoDBMemoryScope(
    applicationId: "memory-rag-sample",
    userId: settings.MemoryUserId);
MongoDBMemoryScope storageScope = retrievalScope.WithSession(settings.MemorySessionId);

await using var memoryProvider = new MongoDBMemoryProvider(
    settings.ConnectionString,
    settings.DatabaseName,
    settings.MemoryCollectionName,
    embeddingGenerator,
    vectorDimensions: 3,
    _ => new MongoDBMemoryProvider.State(retrievalScope, storageScope),
    options: new MongoDBMemoryProviderOptions
    {
        MaxResults = 3,
        NumCandidates = 30,
        PersistenceFailFast = true,
    });

var ragOptions = new MongoDBRAGProviderOptions
{
    SearchMode = MongoDBSearchMode.VectorAnn,
    VectorIndexName = settings.RagVectorIndexName,
    TopK = 3,
    MandatoryFilter = MongoDBRAGFilter.Equal("tenant_id", settings.RagTenantId),
};

await using var ragProvider = new MongoDBRAGProvider(
    settings.ConnectionString,
    settings.DatabaseName,
    settings.RagCollectionName,
    embeddingGenerator,
    vectorDimensions: 3,
    ragOptions);

using var client = new MongoClient(settings.ConnectionString);
IMongoCollection<BsonDocument> ragCollection = client
    .GetDatabase(settings.DatabaseName)
    .GetCollection<BsonDocument>(settings.RagCollectionName);
var ragVectorDefinition = new MongoDBVectorSearchIndexDefinition(
    settings.RagVectorIndexName,
    vectorFieldName: "embedding",
    vectorDimensions: 3,
    similarity: "cosine",
    filterFieldPaths: ["tenant_id"]);
await using var ragIndexManager = new MongoDBRAGIndexManager(ragCollection, ragVectorDefinition);

var ragContextProvider = new MongoDBRAGContextProvider(ragProvider);
var agent = new FixtureAgent(memoryProvider, ragContextProvider);

if (command.ValidateOnly)
{
    Console.WriteLine("Validated Memory plus RAG configuration.");
    return;
}

long cleaned = 0;
try
{
    await memoryProvider.ValidateVectorSearchIndexAsync();
    await ragIndexManager.ValidateVectorSearchIndexAsync();

    int seeded = await memoryProvider.StoreAsync(
        [
            new ChatMessage(
                ChatRole.User,
                "A prior conversation established that approvals require tenant-scoped access controls.")
        ],
        storageScope);
    Console.WriteLine($"Seeded {seeded} scoped memory message(s).");

    AgentResponse response = await agent.RunAsync(
        "What do prior context and authoritative sources say about access?",
        session: null,
        options: null,
        cancellationToken: default);

    Console.WriteLine(response.Text);
}
finally
{
    if (!command.KeepMemory)
    {
        cleaned = await memoryProvider.ClearSessionAsync(settings.MemorySessionId, retrievalScope);
        Console.WriteLine($"Cleared {cleaned} memory record(s) from session '{settings.MemorySessionId}'.");
    }
}

internal sealed record MemoryAndRagOptions(bool ValidateOnly, bool KeepMemory)
{
    public static MemoryAndRagOptions Parse(string[] args)
    {
        bool validateOnly = false;
        bool keepMemory = false;
        foreach (string arg in args)
        {
            switch (arg)
            {
                case "--validate-only":
                    validateOnly = true;
                    break;
                case "--keep":
                    keepMemory = true;
                    break;
                default:
                    throw new ArgumentException("Usage: dotnet run --project ... -- [--validate-only] [--keep]");
            }
        }

        return new(validateOnly, keepMemory);
    }
}

internal sealed record MemoryAndRagSettings(
    string ConnectionString,
    string DatabaseName,
    string MemoryCollectionName,
    string MemoryUserId,
    string MemorySessionId,
    string RagCollectionName,
    string RagVectorIndexName,
    string RagTenantId)
{
    public static MemoryAndRagSettings Load() =>
        new(
            Required("MONGODB_URI"),
            Required("MONGODB_DATABASE"),
            Required("MONGODB_MEMORY_COLLECTION"),
            Required("MONGODB_MEMORY_USER_ID"),
            Required("MONGODB_MEMORY_SESSION_ID"),
            Required("MONGODB_RAG_COLLECTION"),
            Required("MONGODB_RAG_VECTOR_INDEX"),
            Required("MONGODB_RAG_TENANT"));

    private static string Required(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name)?.Trim();
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Set {name} before running Memory plus RAG.");
    }
}

internal sealed class FixtureSession : AgentSession
{
    public FixtureSession()
    {
    }

    public FixtureSession(AgentSessionStateBag stateBag)
        : base(stateBag)
    {
    }
}

internal sealed class FixtureAgent : AIAgent
{
    private readonly MongoDBMemoryProvider _memoryProvider;
    private readonly MongoDBRAGContextProvider _ragContextProvider;

    public FixtureAgent(
        MongoDBMemoryProvider memoryProvider,
        MongoDBRAGContextProvider ragContextProvider)
    {
        _memoryProvider = memoryProvider;
        _ragContextProvider = ragContextProvider;
    }

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult<AgentSession>(new FixtureSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(session.StateBag.Serialize());

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedSession,
        JsonSerializerOptions? jsonSerializerOptions,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<AgentSession>(new FixtureSession(AgentSessionStateBag.Deserialize(serializedSession)));

    protected override async Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        CancellationToken cancellationToken)
    {
        List<ChatMessage> requestMessages = messages.ToList();
        var invokingContext = new AIContextProvider.InvokingContext(
            this,
            session,
            new AIContext { Messages = requestMessages });

        AIContext memoryContext = await _memoryProvider.InvokingAsync(invokingContext, cancellationToken);
        AIContext ragContext = await _ragContextProvider.InvokingAsync(invokingContext, cancellationToken);
        string memorySummary = FormatMemoryContext(memoryContext.Messages);
        string ragSummary = FormatRagContext(ragContext.Messages);

        if (memorySummary.Contains("no conversational memory", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The Memory provider returned no context for the seeded sample turn.");
        }

        if (ragSummary.Contains("no authoritative RAG context", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The RAG provider returned no context. Preload tenant-scoped vector-search documents before running " +
                "Memory plus RAG.");
        }

        var response = new AgentResponse(
            new ChatMessage(
                ChatRole.Assistant,
                $"Memory context: {memorySummary}{Environment.NewLine}RAG context: {ragSummary}"));

        await _memoryProvider.InvokedAsync(
            new AIContextProvider.InvokedContext(
                this,
                session,
                requestMessages,
                response.Messages),
            cancellationToken);

        return response;
    }

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        AgentResponse response = await RunCoreAsync(messages, session, options, cancellationToken);
        foreach (AgentResponseUpdate update in response.ToAgentResponseUpdates())
        {
            yield return update;
        }
    }

    private static string FormatMemoryContext(IEnumerable<ChatMessage>? messages)
    {
        List<string> recalled = (messages ?? [])
            .Select(static message => message.Text)
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToList();
        return recalled.Count == 0
            ? "no conversational memory"
            : string.Join(" | ", recalled);
    }

    private static string FormatRagContext(IEnumerable<ChatMessage>? messages)
    {
        List<string> citations = (messages ?? [])
            .Select(static message =>
            {
                string source = message.AdditionalProperties?.TryGetValue("_rag_source_name", out object? sourceName) == true &&
                    sourceName is string sourceNameText &&
                    !string.IsNullOrWhiteSpace(sourceNameText)
                        ? sourceNameText
                        : message.AdditionalProperties?.TryGetValue("_rag_id", out object? id) == true
                            ? Convert.ToString(id, System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"
                            : "unknown";
                return $"[{source}] {message.Text}";
            })
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .ToList();
        return citations.Count == 0
            ? "no authoritative RAG context"
            : string.Join(" | ", citations);
    }
}

internal sealed class SampleEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            new GeneratedEmbeddings<Embedding<float>>(
                values.Select(static value => new Embedding<float>(ToVector(value)))));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static float[] ToVector(string value)
    {
        string normalized = value.ToLowerInvariant();
        return normalized.Contains("access", StringComparison.Ordinal) ||
            normalized.Contains("tenant", StringComparison.Ordinal) ||
            normalized.Contains("approval", StringComparison.Ordinal)
            ? [1.0f, 0.0f, 0.0f]
            : normalized.Contains("memory", StringComparison.Ordinal)
                ? [0.0f, 1.0f, 0.0f]
                : [0.0f, 0.0f, 1.0f];
    }
}
