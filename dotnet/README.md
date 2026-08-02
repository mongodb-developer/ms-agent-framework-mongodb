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
fail with migration guidance.

Run the sample after setting `MONGODB_URI` and `MONGODB_DATABASE`:

```powershell
dotnet run --project samples\HistoryQuickstart\HistoryQuickstart.csproj
```

Optional variables are `MONGODB_HISTORY_COLLECTION`,
`MONGODB_HISTORY_APPLICATION_ID`, `MONGODB_HISTORY_AGENT_ID`, and
`MONGODB_HISTORY_SESSION_ID`. Set `MONGODB_HISTORY_CLEAR=true` only when the
sample's authorized session should be removed. See the
[.NET Chat History developer guide](../docs/development/history/dotnet-history.md).
