# Agent Framework MongoDB for Python

MongoDB integrations for Microsoft Agent Framework.

## Memory quickstart

The Memory provider performs scoped semantic conversation recall. It does not
replace exact Chat History or authoritative RAG.

```python
memory = MongoDBMemoryContextProvider(
    embedding_generator,
    connection_string=os.environ["MONGODB_URI"],
    database_name=os.environ["MONGODB_DATABASE"],
    collection_name=os.environ["MONGODB_MEMORY_COLLECTION"],
    vector_dimensions=1536,
    application_id="my-app",
    user_id="user-123",
)
await memory.ensure_vector_search_index(wait_until_ready=True)
```

Run `samples\memory_quickstart.py` after setting `MONGODB_URI`,
`MONGODB_DATABASE`, and `MONGODB_MEMORY_COLLECTION`. Replace its deterministic
demonstration generator with a production embedding generator whose dimensions
match the configured index. Runtime operations never provision indexes
implicitly. The sample deletes only its scoped fixture messages; collection
cleanup remains an administrator decision.

## Chat History quickstart

Chat History preserves the exact ordered transcript for one authorized session. It
does not perform semantic recall or store complete Agent Framework session state.

```python
history = MongoDBHistoryProvider(
    collection,
    options=MongoDBHistoryProviderOptions(
        application_id="my-app",
        agent_id="my-agent",
        session_id="session-123",
    ),
)
await history.ensure_indexes()
```

Run `samples\history_quickstart.py` after setting `MONGODB_URI`,
`MONGODB_DATABASE`, `MONGODB_HISTORY_COLLECTION`,
`MONGODB_HISTORY_APPLICATION_ID`, `MONGODB_HISTORY_AGENT_ID`, and
`MONGODB_HISTORY_SESSION_ID`. Index creation and session clearing are explicit.

## RAG contracts

The package exports the shared, read-only RAG contracts before any search
execution mode is enabled:

```python
from agent_framework_mongodb import (
    AndFilter,
    EqualFilter,
    InFilter,
    MongoDBRAGProviderOptions,
    MongoDBSearchMode,
)

options = MongoDBRAGProviderOptions(
    mode=MongoDBSearchMode.VECTOR_ANN,
    vector_dimensions=1536,
    vector_index_name="knowledge_vector",
    text_fields=("content",),
    vector_field="embedding",
    filter=AndFilter(
        EqualFilter("tenant_id", "tenant-123"),
        InFilter("visibility", ("public", "tenant")),
    ),
)
```

Public filters are typed and bounded; raw dictionaries, BSON, field names,
operators, and pipelines are not accepted as filter input. The package exports
`MongoDBRAGProvider`, `MongoDBRAGContextProvider`, `MongoDBRAGProviderOptions`,
`MongoDBRAGSearchOptions`, `MongoDBRAGParentOptions`, `MongoDBRAGResult`, and
`MongoDBSearchMode`. Direct `search` currently reports that the selected mode
implementation is not installed. Vector ANN, vector ENN, full-text, and hybrid
RRF execution are delivered by later independently tested feature slices.
