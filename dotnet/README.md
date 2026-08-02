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
