# MongoDB.AgentFramework

`MongoDB.AgentFramework` provides MongoDB-backed integrations for Microsoft Agent Framework.

## Semantic Memory

`MongoDBMemoryProvider : AIContextProvider` recalls scoped conversation
messages before an invocation and stores selected user, assistant, and system
text afterward. It is semantic recall, not exact chat-history replay or RAG.

```csharp
var memory = new MongoDBMemoryProvider(
    database,
    "memories",
    embeddingGenerator,
    vectorDimensions: 1536,
    _ => new MongoDBMemoryProvider.State(
        new MongoDBMemoryScope(
            applicationId: "my-app",
            userId: "user-123")),
    new MongoDBMemoryProviderOptions
    {
        MaxResults = 3,
        NumCandidates = 30,
    });

await memory.EnsureVectorSearchIndexAsync(waitUntilReady: true);
```

The state factory may return different `SearchScope` and `StorageScope`
instances. Search crosses sessions unless its scope includes `SessionId`.
Mandatory application, agent, user, and optional session fields are placed
inside `$vectorSearch.filter`.

### Direct APIs

- `StoreAsync` batches one embedding call and writes one document per eligible
  message.
- `SearchAsync` supports ANN by default and ENN with `exact: true`.
- `DeleteByIdAsync`, `ClearSessionAsync`, `ClearUserAsync`, and `ListAsync`
  always require a durable authorization scope.
- `EnsureVectorSearchIndexAsync` is the only provisioning path;
  `ValidateVectorSearchIndexAsync` is read-only.

Direct APIs surface stable `MongoDBIntegrationException` categories.
Framework retrieval fails open only for operational retrieval/embedding
failures. Framework persistence fails open by default and can be made
fail-fast with `PersistenceFailFast`. Cancellation always propagates.
Fallback IDs used by framework persistence retries are versioned in
`AgentSession.StateBag` under the provider's advertised `StateKeys`, so they
survive session serialization and provider recreation.

Injected clients, databases, collections, and embedding generators remain
caller-owned. Only a client created by the connection-string constructor is
disposed by the provider.

Run the sample after setting `MONGODB_URI`, `MONGODB_DATABASE`, and optionally
`MONGODB_MEMORY_COLLECTION`:

```powershell
dotnet run --project samples\MemoryQuickstart\MemoryQuickstart.csproj
```

The sample uses a deterministic three-dimensional demonstration embedding
generator; replace it for production. It explicitly creates a Vector Search
index and prints the closest stored message (or `No memory found.`). It leaves
the configured collection intact; remove that collection when finished if it
is dedicated to the sample. The MongoDB principal therefore needs collection
read/write and Search index-management privileges. See the
[.NET Memory developer guide](../docs/development/memory/dotnet-memory.md) and
[implementation specifications](../docs/spec/README.md).

The package is under active development and is not ready for publication.

## Exact Chat History

`MongoDBChatHistoryProvider : ChatHistoryProvider` stores lossless, ordered
`ChatMessage` payloads for one immutable application/agent/session authorization
scope. It uses Agent Framework's public JSON serialization and delegates lifecycle
filtering, merging, and source attribution to the framework base provider.

```csharp
await using var history = new MongoDBChatHistoryProvider(
    collection,
    new MongoDBChatHistoryProviderOptions
    {
        ApplicationId = "my-app",
        AgentId = "assistant",
        SessionId = "session-123",
        MaxMessages = 100,
    });

await history.EnsureIndexesAsync();
await history.SaveMessagesAsync(
    "session-123",
    [new ChatMessage(ChatRole.User, "Hello") { MessageId = "message-1" }]);
IReadOnlyList<ChatMessage> messages =
    await history.GetMessagesAsync("session-123");
```

`EnsureIndexesAsync` is the only mutating provisioning operation. Runtime history
does not use MongoDB Search or the Memory collection. `ClearMessagesAsync` rejects
any session other than the configured authorization scope. Unknown stored versions
fail with migration guidance. History schema version 2 adds canonical scope
discrimination and is a breaking authorization-boundary change; version 1
collections require migration before replay or index provisioning.

Run the sample after setting `MONGODB_URI` and `MONGODB_DATABASE`:

```powershell
dotnet run --project samples\HistoryQuickstart\HistoryQuickstart.csproj
```

Optional variables are `MONGODB_HISTORY_COLLECTION`,
`MONGODB_HISTORY_APPLICATION_ID`, `MONGODB_HISTORY_AGENT_ID`, and
`MONGODB_HISTORY_SESSION_ID`. Set `MONGODB_HISTORY_CLEAR=true` only when the
sample's authorized session should be removed. See the
[.NET Chat History developer guide](../docs/development/history/dotnet-history.md).

## RAG contracts, typed filters, Vector Search (ANN/ENN), FullText, and HybridRrf

`MongoDBSearchMode` (`VectorAnn`, `VectorEnn`, `FullText`, `HybridRrf`), the bounded typed `MongoDBRAGFilter` AST,
the immutable `MongoDBRAGResult`, and `MongoDBRAGProviderOptions` are available under
`dotnet/src/MongoDB.AgentFramework/RAG/`. `MongoDBRAGFilter` is created only through static factories
(`Equal`, `NotEqual`, `In`, `NotIn`, `Range`, `And`, `Or`) with bounded nesting depth and value counts, and is
completely translatable into a `$vectorSearch` match filter or a `$search` compound filter through the internal
`RAGFilterTranslator`.

`MongoDBRAGProvider` executes live `VectorAnn`/`VectorEnn`/`FullText`/`HybridRrf` retrieval through `SearchAsync`,
and `MongoDBRAGContextProvider` composes it as a before-invoke `AIContextProvider` that supplies retrieved chunks as
attributed `ChatRole.Tool` context messages. `HybridRrf` uses MongoDB's native `$rankFusion` stage to combine a
Vector Search ANN input and a Search text input (weighted reciprocal-rank fusion) and requires **both** an
embedding generator/dimensions and Search index/field configuration; `ValidateHybridSearchCapabilityAsync` is an
opt-in seam validating MongoDB 8.0+ and both indexes without ever being called implicitly by `SearchAsync`.
`FullText` never requires or invokes an embedding generator: a dedicated constructor overload family
(`MongoDBRAGProvider(database, collectionName, options, ...)`, and the matching collection/client/connection-string
overloads) accepts no `embeddingGenerator`/`vectorDimensions` parameters at all.

```csharp
MongoDBRAGFilter filter = MongoDBRAGFilter.And(
    MongoDBRAGFilter.Equal("tenant_id", "tenant-a"),
    MongoDBRAGFilter.In("category", ["news", "docs"]));

var vectorOptions = new MongoDBRAGProviderOptions
{
    SearchMode = MongoDBSearchMode.VectorAnn,
    VectorIndexName = "knowledge_vector_index",
    VectorFieldName = "embedding",
    TopK = 5,
    MandatoryFilter = filter,
};

await using var rag = new MongoDBRAGProvider(
    database,
    "knowledge_chunks",
    embeddingGenerator,
    vectorDimensions: 1536,
    vectorOptions);

IReadOnlyList<MongoDBRAGResult> results = await rag.SearchAsync("What color do widgets ship in?");

var contextProvider = new MongoDBRAGContextProvider(rag);

// FullText: no embedding generator required.
var fullTextOptions = new MongoDBRAGProviderOptions
{
    SearchMode = MongoDBSearchMode.FullText,
    SearchIndexName = "knowledge_search_index",
    SearchTextFieldNames = ["text"],
    TopK = 5,
    MandatoryFilter = filter,
};

await using var fullTextRag = new MongoDBRAGProvider(database, "knowledge_chunks", fullTextOptions);
IReadOnlyList<MongoDBRAGResult> fullTextResults = await fullTextRag.SearchAsync("What color do widgets ship in?");

// HybridRrf: native $rankFusion over both a Vector Search input and a Search input; requires both an embedding
// generator/dimensions and Search index/field configuration.
var hybridOptions = new MongoDBRAGProviderOptions
{
    SearchMode = MongoDBSearchMode.HybridRrf,
    VectorIndexName = "knowledge_vector_index",
    SearchIndexName = "knowledge_search_index",
    SearchTextFieldNames = ["text"],
    TopK = 5,
    MandatoryFilter = filter,
};

await using var hybridRag = new MongoDBRAGProvider(
    database, "knowledge_chunks", embeddingGenerator, vectorDimensions: 1536, hybridOptions);
IReadOnlyList<MongoDBRAGResult> hybridResults = await hybridRag.SearchAsync("What color do widgets ship in?");
```

This slice does not provision Vector Search or Search indexes itself; the target index/indexes must already exist
before `MongoDBRAGProvider` connects. See [Index Management](#index-management) below for the separate facade that
provisions them. Injected clients/databases/collections/embedding generators remain caller-owned; only a client
created by the connection-string constructor is disposed by the provider.

Run the sample after setting `MONGODB_URI`, `MONGODB_DATABASE`, and a pre-provisioned Vector Search index
(`MONGODB_RAG_VECTOR_INDEX`, optionally `MONGODB_RAG_COLLECTION`). Additionally set `MONGODB_RAG_SEARCH_INDEX` to a
pre-provisioned Search index to also see the FullText and HybridRrf demonstrations (both skipped otherwise;
HybridRrf additionally requires a MongoDB 8.0+ deployment):

```powershell
dotnet run --project samples\RAGQuickstart\RAGQuickstart.csproj
```

See the [.NET RAG contracts developer guide](../docs/development/rag/dotnet-rag.md), the
[.NET Vector RAG developer guide](../docs/development/rag/dotnet-rag-vector-search.md), the
[.NET FullText RAG developer guide](../docs/development/rag/dotnet-rag-full-text-search.md), and the
[.NET HybridRrf RAG developer guide](../docs/development/rag/dotnet-rag-hybrid-rrf.md) for the full public
surface, pipeline shape, and deferred work.

## Index Management

`MongoDBMemoryIndexManager` and `MongoDBRAGIndexManager` (`dotnet/src/MongoDB.AgentFramework/Memory/` and
`.../RAG/`) are explicit, feature-specific facades over the same shared internal index mechanics
`MongoDBMemoryProvider`/`MongoDBRAGProvider` use, independently constructible from a database, collection, client,
or connection string without requiring a provider or an embedding generator. They exist to keep the "provisioner"
role (creating, updating, waiting for, and dropping indexes) operationally and privilege-separate from the
"runtime" role a running provider plays (ADR
[0006](../docs/decisions/0006-make-index-provisioning-explicit.md)/[0016](../docs/decisions/0016-keep-index-facades-in-runtime-packages.md)):

```csharp
var definition = new MongoDBVectorSearchIndexDefinition(
    indexName: "agent_framework_memory",
    vectorFieldName: "content_embedding",
    vectorDimensions: 1536,
    filterFieldPaths: ["application_id", "agent_id", "user_id", "session_id"]);

// Provisioner: run under a distinct, more privileged identity than the runtime provider connects with.
await using var provisioner = new MongoDBMemoryIndexManager(client, "my_database", "memories", definition);
await provisioner.EnsureIndexAsync(waitUntilReady: true, timeout: TimeSpan.FromMinutes(5));

// Runtime: read-only validation only -- never creates, updates, or drops.
await using var runtime = new MongoDBMemoryIndexManager(client, "my_database", "memories", definition);
MongoDBIndexComparison comparison = await runtime.ValidateIndexAsync();
```

Every `Get*`/`List*`/`Validate*` method never mutates MongoDB; only `Create*`/`Ensure*`/`Update*`/`Drop*` do, and only
when explicitly called -- never from a constructor or a framework lifecycle hook. `Create*` is a strict, non-idempotent
create-only operation (throws `MongoDBIndexAlreadyExistsException` if the index already exists); `Ensure*` is the
idempotent reconciliation operation shown above (creates if missing, updates if mismatched) and is what a deployment
retry loop should call. Both always re-inspect and validate the index's final state after any create/update attempt,
so a rival concurrent creator winning with an incompatible definition is still caught. Comparison is semantic and
order-insensitive, and distinguishes an actionable mismatch (`MongoDBIndexComparison.Mismatches`) from a merely
informational compatible difference (`CompatibleDifferences`). A terminal `Failed` build is never automatically
retried or repaired -- it surfaces immediately as `MongoDBIndexFailedException`, never enters the polling loop, and
`Ensure*` never treats it as something to auto-update. `WaitUntilReadyAsync`/`Ensure*(waitUntilReady: true)` poll
with a bounded, cancellable exponential backoff whose per-attempt deadline bounds even a hung underlying call,
distinguishing the caller's own cancellation from the bounded timeout. A connected identity lacking
index-management privileges raises `MongoDBIndexPrivilegeException` distinctly from a generic deployment error.

**Least privilege:** runtime identities (what `MongoDBMemoryProvider`/`MongoDBRAGProvider` connect with) should only
ever need collection read/write/aggregate plus Search query permissions -- never `createSearchIndexes`/
`updateSearchIndexes`/`dropSearchIndexes`. Reserve those index-management privileges for a separate provisioner
identity that runs the `Ensure*`/`Update*`/`Drop*` calls, typically as a deployment-pipeline step. Exact
built-in/custom MongoDB roles must still be verified against the target deployment before package publication.

Run the sample after setting `MONGODB_URI` and `MONGODB_DATABASE` (optionally `MONGODB_MEMORY_COLLECTION` and
`MONGODB_RAG_COLLECTION`):

```powershell
dotnet run --project samples\IndexManagementQuickstart\IndexManagementQuickstart.csproj
```

The sample constructs separate provisioner and runtime facade instances side by side over both a Memory Vector
Search index and a RAG Hybrid (Vector Search + Search) index pair, then drops all three indexes at the end. See the
[.NET Index Management developer guide](../docs/development/index-management/dotnet-index-management.md).
