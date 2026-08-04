// Clean, isolated NuGet consumer smoke test (docs/spec/quality-release.md "NuGet install and runtime smoke tests
// from the produced package, not project references"). Restored exclusively from the locally packed .nupkg (see
// nuget.config and ../../scripts/verify-package.ps1), never from a ProjectReference to this repository's source.
//
// Scope: construction only. There is no live, Search-capable MongoDB deployment available in this validation
// environment (docs/spec/quality-release.md: "Real MongoDB integration tests still require suitable credentials
// and Search-capable infrastructure"), so this smoke test proves every public provider/facade type across every
// public feature area -- Memory, exact Chat History, RAG (all four MongoDBSearchMode values), Index Management,
// Session Store, and Workflow Checkpoint Store -- constructs successfully from the packed artifact, with no
// missing type/member/constructor and no unexpected exception. It intentionally never calls a method that would
// perform network I/O (EnsureIndexesAsync, StoreAsync, SearchAsync, CreateAsync, etc.); those remain covered by
// this repository's own skip-cleanly-without-credentials integration tests.
#pragma warning disable MAAI001 // AIContextProvider is an evaluation-purposes-only API in this package version.

using Microsoft.Extensions.AI;
using MongoDB.AgentFramework;
using MongoDB.Bson;
using MongoDB.Driver;

// A local, non-routable-in-practice endpoint: MongoClient/IMongoCollection construction never blocks on, or
// requires, an actual connection (the driver only attempts I/O when an operation is issued), so this smoke test
// never depends on network reachability. Short timeouts additionally bound any background topology probing the
// driver may start.
const string FakeConnectionString =
    "mongodb://127.0.0.1:1/?connectTimeoutMS=200&serverSelectionTimeoutMS=200&heartbeatFrequencyMS=60000";
const string DatabaseName = "package_smoke_test";

var failures = new List<string>();

void Check(string name, Action construct)
{
    try
    {
        construct();
        Console.WriteLine($"[ OK ] {name}");
    }
    catch (Exception ex)
    {
        failures.Add($"{name}: {ex}");
        Console.WriteLine($"[FAIL] {name}: {ex.Message}");
    }
}

using var client = new MongoClient(FakeConnectionString);
IMongoDatabase database = client.GetDatabase(DatabaseName);
IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator = new SmokeTestEmbeddingGenerator();

// ---- Memory ----
MongoDBMemoryProvider? memoryProvider = null;
Check("MongoDBMemoryProvider + MongoDBMemoryProviderOptions + MongoDBMemoryScope", () =>
{
    memoryProvider = new MongoDBMemoryProvider(
        database,
        "memories",
        embeddingGenerator,
        vectorDimensions: 3,
        _ => new MongoDBMemoryProvider.State(
            new MongoDBMemoryScope(applicationId: "smoke-test", userId: "smoke-user")),
        new MongoDBMemoryProviderOptions { MaxResults = 3, NumCandidates = 30 });
});

Check("MongoDBMemoryIndexManager + MongoDBVectorSearchIndexDefinition", () =>
{
    var definition = new MongoDBVectorSearchIndexDefinition(
        indexName: "agent_framework_memory_smoke",
        vectorFieldName: "content_embedding",
        vectorDimensions: 3,
        filterFieldPaths: ["application_id", "agent_id", "user_id", "session_id"]);
    _ = new MongoDBMemoryIndexManager(database, "memories", definition);
});

// ---- Exact Chat History ----
MongoDBChatHistoryProvider? historyProvider = null;
Check("MongoDBChatHistoryProvider + MongoDBChatHistoryProviderOptions", () =>
{
    historyProvider = new MongoDBChatHistoryProvider(
        database,
        "chat_history",
        new MongoDBChatHistoryProviderOptions
        {
            ApplicationId = "smoke-test",
            AgentId = "smoke-agent",
            SessionId = "smoke-session",
            MaxMessages = 100,
        });
});

// ---- RAG: VectorAnn, FullText, HybridRrf (the fourth mode, VectorEnn, shares VectorAnn's constructor family) ----
MongoDBRAGFilter filter = MongoDBRAGFilter.And(
    MongoDBRAGFilter.Equal("tenant_id", "smoke-test"),
    MongoDBRAGFilter.In("category", ["news", "docs"]));

MongoDBRAGProvider? vectorRagProvider = null;
Check("MongoDBRAGProvider (VectorAnn) + MongoDBRAGProviderOptions + MongoDBRAGFilter", () =>
{
    vectorRagProvider = new MongoDBRAGProvider(
        database,
        "rag_chunks",
        embeddingGenerator,
        vectorDimensions: 3,
        new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.VectorAnn,
            VectorIndexName = "agent_framework_rag_vector_smoke",
            TopK = 5,
            MandatoryFilter = filter,
        });
});

Check("MongoDBRAGContextProvider", () =>
{
    _ = new MongoDBRAGContextProvider(vectorRagProvider!);
});

MongoDBRAGProvider? fullTextRagProvider = null;
Check("MongoDBRAGProvider (FullText, no embedding generator)", () =>
{
    fullTextRagProvider = new MongoDBRAGProvider(
        database,
        "rag_chunks",
        new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.FullText,
            SearchIndexName = "agent_framework_rag_search_smoke",
            SearchTextFieldNames = ["text"],
            TopK = 5,
            MandatoryFilter = filter,
        });
});

MongoDBRAGProvider? hybridRagProvider = null;
Check("MongoDBRAGProvider (HybridRrf)", () =>
{
    hybridRagProvider = new MongoDBRAGProvider(
        database,
        "rag_chunks",
        embeddingGenerator,
        vectorDimensions: 3,
        new MongoDBRAGProviderOptions
        {
            SearchMode = MongoDBSearchMode.HybridRrf,
            VectorIndexName = "agent_framework_rag_vector_smoke",
            SearchIndexName = "agent_framework_rag_search_smoke",
            SearchTextFieldNames = ["text"],
            TopK = 5,
            MandatoryFilter = filter,
        });
});

Check("MongoDBRAGIndexManager + MongoDBSearchIndexDefinition", () =>
{
    var vectorDefinition = new MongoDBVectorSearchIndexDefinition(
        indexName: "agent_framework_rag_vector_smoke",
        vectorFieldName: "embedding",
        vectorDimensions: 3);
    var searchDefinition = new MongoDBSearchIndexDefinition(
        indexName: "agent_framework_rag_search_smoke",
        textFieldNames: ["text"]);
    _ = new MongoDBRAGIndexManager(database, "rag_chunks", vectorDefinition, searchDefinition);
});

// ---- Session Store (compatibility-blocked pre-1.0 facade; see the root/dotnet READMEs) ----
MongoDBAgentSessionStore? sessionStore = null;
Check("MongoDBAgentSessionStore + MongoDBAgentSessionStoreOptions", () =>
{
    sessionStore = new MongoDBAgentSessionStore(
        database,
        "agent_sessions",
        new MongoDBAgentSessionStoreOptions
        {
            ApplicationId = "smoke-test",
            AgentId = "smoke-agent",
            DefaultExpiration = TimeSpan.FromDays(30),
        });
});

// ---- Workflow Checkpoint Store ----
MongoDBCheckpointStore? checkpointStore = null;
Check("MongoDBCheckpointStore + MongoDBCheckpointStoreOptions", () =>
{
    byte[] signingKey = new byte[32];
    System.Security.Cryptography.RandomNumberGenerator.Fill(signingKey);
    checkpointStore = new MongoDBCheckpointStore(
        database,
        "workflow_checkpoints",
        new MongoDBCheckpointStoreOptions
        {
            WorkflowId = "smoke-workflow",
            ContinuationTokenSigningKey = signingKey,
            DefaultExpiration = TimeSpan.FromDays(30),
        });
});

Check("Microsoft.Agents.AI.Workflows.CheckpointManager.CreateJson(MongoDBCheckpointStore)", () =>
{
    _ = Microsoft.Agents.AI.Workflows.CheckpointManager.CreateJson(checkpointStore!);
});

if (memoryProvider is not null)
{
    await memoryProvider.DisposeAsync();
}

if (historyProvider is not null)
{
    await historyProvider.DisposeAsync();
}

foreach (MongoDBRAGProvider? provider in new[] { vectorRagProvider, fullTextRagProvider, hybridRagProvider })
{
    if (provider is not null)
    {
        await provider.DisposeAsync();
    }
}

if (sessionStore is not null)
{
    await sessionStore.DisposeAsync();
}

if (checkpointStore is not null)
{
    await checkpointStore.DisposeAsync();
}

Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine($"Package consumer smoke test FAILED ({failures.Count} construction failure(s)):");
    foreach (string failure in failures)
    {
        Console.WriteLine($"  - {failure}");
    }

    return 1;
}

Console.WriteLine("Package consumer smoke test PASSED: every public feature area constructed successfully.");
return 0;

sealed class SmokeTestEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            values.Select(static _ => new Embedding<float>(new float[] { 1, 0, 0 }))));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
