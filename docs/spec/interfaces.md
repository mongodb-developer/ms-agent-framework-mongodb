# Interfaces and Parity

## Public interfaces

Implementation MUST use language-idiomatic syntax while preserving these public concepts and behaviors.

### Python Memory

```python
memory = MongoDBMemoryContextProvider(
    embedding_generator,
    connection_string=os.environ["MONGODB_URI"],
    database_name=os.environ["MONGODB_DATABASE"],
    collection_name=os.environ["MONGODB_MEMORY_COLLECTION"],
    vector_dimensions=1536,
    user_id="user-123",
    index_name="agent_framework_memory",
    max_results=3,
)

await memory.ensure_vector_search_index(wait_until_ready=True)
```

### Python RAG

```python
rag = MongoDBRAGContextProvider(
    embedding_generator,
    connection_string=os.environ["MONGODB_URI"],
    database_name=os.environ["MONGODB_DATABASE"],
    collection_name=os.environ["MONGODB_RAG_COLLECTION"],
    search_mode="vector",
    vector_index_name="knowledge_vector_index",
    text_field="content",
    vector_field="embedding",
    title_field="source.title",
    url_field="source.url",
    top_k=5,
    filter=EqualFilter(field="tenant_id", value="tenant-123"),
)
```

  `EqualFilter` is an illustrative final name for the required typed filter API; the implementation ADR may select an
  equivalent language-idiomatic name. Raw dictionaries/BSON are not accepted by the public RAG provider.

### Python Chat History

```python
history = MongoDBHistoryProvider(
    collection,
    options=MongoDBHistoryProviderOptions(
        application_id="application-123",
        agent_id="agent-123",
        session_id="session-123",
    ),
)
```

`MongoDBHistoryProvider` MUST derive from the public `HistoryProvider` contract and expose authorized
`clear_messages(...)` behavior in addition to the required framework hooks.

### .NET Memory

```csharp
var memory = new MongoDBMemoryProvider(
    database,
    collectionName,
    embeddingGenerator,
    vectorDimensions,
    _ => new MongoDBMemoryProvider.State(
        new MongoDBMemoryScope { UserId = "user-123" }),
    new MongoDBMemoryProviderOptions { MaxResults = 3 });
```

### .NET RAG

```csharp
var rag = new MongoDBRAGProvider(
    database,
    collectionName,
    embeddingGenerator,
    new MongoDBRAGProviderOptions
    {
        SearchMode = MongoDBSearchMode.Vector,
        VectorIndexName = "knowledge_vector_index",
        TextFieldName = "content",
        VectorFieldName = "embedding",
        SourceNameFieldName = "source.title",
        SourceLinkFieldName = "source.url",
        TopK = 5,
    });
```

### .NET Chat History

```csharp
var history = new MongoDBChatHistoryProvider(
    collection,
    new MongoDBChatHistoryProviderOptions
    {
        ApplicationId = "application-123",
        AgentId = "agent-123",
        SessionId = "session-123",
    });
```

`MongoDBChatHistoryProvider` MUST derive from the public `ChatHistoryProvider` contract and expose authorized
`ClearMessagesAsync(...)` behavior in addition to the required framework hooks.

### Cross-language public parity contract

| Concept | Python | .NET | Required equivalent behavior |
| --- | --- | --- | --- |
| Memory provider | `MongoDBMemoryContextProvider` | `MongoDBMemoryProvider` | Retrieve before run; persist selected messages after run |
| Chat History provider | `MongoDBHistoryProvider` | `MongoDBChatHistoryProvider` | Persist and replay exact scoped messages in deterministic order |
| RAG provider | `MongoDBRAGContextProvider` | `MongoDBRAGProvider` | Read-only retrieval and attributed context |
| Direct search | `search(query, ...)` | `SearchAsync(query, ...)` | Same mode/filter/limit/score semantics |
| Search mode | String enum/`Enum` | `MongoDBSearchMode` enum | ANN, ENN, full text, and hybrid RRF |
| Options | Keyword options/dataclass-like model | Options class | Equivalent validation and defaults |
| Raw result | Mapping/document | `BsonDocument` or generic document | Preserve original retrieved document |
| Cancellation | Task cancellation | `CancellationToken` | Propagate through embedding, MongoDB, polling, and persistence |
| Resource cleanup | `close`/async context manager | `IAsyncDisposable` | Dispose provider-owned resources only |
| Index operations | Async explicit methods | Async explicit methods | Same read-only versus mutating split |
| RAG framework adapter | `SessionContext` injection | Composed `TextSearchProvider` | Equivalent source/citation behavior |
| Session Store | `MongoDBSessionStore(SessionStore)` | `MongoDBAgentSessionStore` | Versioned complete-session persistence through supported public hosting contracts |
| Workflow checkpoints | `MongoDBCheckpointStorage(CheckpointStorage)` | `MongoDBCheckpointStore(JsonCheckpointStore)` | Versioned checkpoint serialization, lineage, ordering, and resumption |

Parity means equivalent observable behavior, not identical constructor syntax, physical BSON casing, serializer
implementation, or package version. Public defaults MUST be listed in one parity document and covered by contract
tests. Any intentional language difference requires a documented rationale.

### Physical schema interoperability

The first release MUST NOT claim that Python and .NET providers can transparently share the same Memory collection.
That claim requires fixtures proving compatible:

- BSON field names and casing
- ID and timestamp types
- role values
- embedding numeric representation
- missing/null behavior
- serializers and discriminator fields
- vector and filter index paths

RAG can naturally query a shared application collection when both language configurations map the same fields. That
is configurable mapping compatibility, not a promise that all raw result types serialize identically.
