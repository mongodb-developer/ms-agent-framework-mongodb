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

## Vector RAG quickstart

Vector RAG performs read-only retrieval from a pre-ingested knowledge collection.
Its mandatory typed filter is applied inside `$vectorSearch` before candidates
or results are limited.

```python
from agent_framework_mongodb import (
    AndFilter,
    EqualFilter,
    InFilter,
    MongoDBRAGContextProvider,
    MongoDBRAGProvider,
    MongoDBRAGProviderOptions,
    MongoDBSearchMode,
)

direct = MongoDBRAGProvider(
    MongoDBRAGProviderOptions(
        mode=MongoDBSearchMode.VECTOR_ANN,
        vector_dimensions=1536,
        vector_index_name="knowledge_vector",
        text_fields=("content",),
        vector_field="embedding",
        filter=AndFilter(
            EqualFilter("tenant_id", "tenant-123"),
            InFilter("visibility", ("public", "tenant")),
        ),
    ),
    embedding_generator=embedding_generator,
    connection_string=os.environ["MONGODB_URI"],
    database_name=os.environ["MONGODB_DATABASE"],
    collection_name=os.environ["MONGODB_RAG_COLLECTION"],
)
rag = MongoDBRAGContextProvider(direct)
await direct.validate_vector_search_index()
results = await rag.search("tenant isolation")
```

Public filters are typed and bounded; raw dictionaries, BSON, field names,
operators, and pipelines are not accepted as filter input. The package exports
`MongoDBRAGProvider`, `MongoDBRAGContextProvider`, `MongoDBRAGProviderOptions`,
`MongoDBRAGSearchOptions`, `MongoDBRAGParentOptions`, `MongoDBRAGResult`, and
`MongoDBSearchMode`. Vector ANN/ENN and full-text Search are implemented.
Hybrid RRF remains a separate feature slice and fails clearly rather than
downgrading.
ENN verifies exact-search planning through public MongoDB commands before
embedding and caches the observed capability for a bounded interval; it does
not infer support from an unverified server-version threshold. Only recognized
unsupported syntax/capability responses are cached; operational failures
propagate and are retried by the next capability evaluation.

Membership values and field-path collections must be explicit lists or tuples;
scalar strings and bytes are rejected rather than split into characters.
Integer filter values must fit BSON int64, and range filters do not treat
booleans as numbers. Repeated configured field paths are normalized once in
first-seen order.

Run `samples\rag_vector_quickstart.py` after setting `MONGODB_URI`,
`MONGODB_DATABASE`, `MONGODB_RAG_COLLECTION`, `MONGODB_RAG_VECTOR_INDEX`, and
`MONGODB_RAG_TENANT`. The collection must already contain three-dimensional
vectors produced by the sample generator; production dimensions and embeddings
must match the configured index. Explicit index ensure requires provisioner
privileges. Runtime search needs only read/aggregate and Search query privileges.
The sample does not ingest or delete documents.

## Full-text RAG quickstart

Full-text RAG queries a pre-ingested collection without generating embeddings.
The complete provider authorization filter and optional per-call relevance
filter are translated into `$search.compound.filter` before `$limit`.

```python
direct = MongoDBRAGProvider(
    MongoDBRAGProviderOptions(
        mode=MongoDBSearchMode.FULL_TEXT,
        search_index_name="knowledge_search",
        text_fields=("content",),
        search_analyzer="lucene.standard",
        filter=EqualFilter("tenant_id", "tenant-123"),
    ),
    connection_string=os.environ["MONGODB_URI"],
    database_name=os.environ["MONGODB_DATABASE"],
    collection_name=os.environ["MONGODB_RAG_COLLECTION"],
)
await direct.validate_search_index()
results = await direct.search("tenant isolation")
```

Run `samples\rag_full_text_quickstart.py` after setting `MONGODB_URI`,
`MONGODB_DATABASE`, `MONGODB_RAG_COLLECTION`, `MONGODB_RAG_SEARCH_INDEX`, and
`MONGODB_RAG_TENANT`. The collection must already contain the configured text
and authorization fields. Explicit Search index ensure requires a provisioner
identity; runtime search is read-only and needs only index inspection,
read/aggregate, and Search query permissions. The sample performs no ingestion
or cleanup.
