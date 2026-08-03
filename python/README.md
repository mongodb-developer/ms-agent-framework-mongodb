# Agent Framework MongoDB for Python

MongoDB integrations for Microsoft Agent Framework.

## Install

The distribution name is `agent-framework-mongodb`; Python imports use
`agent_framework_mongodb`.

```powershell
python -m pip install agent-framework-mongodb
```

This repository has not published the distribution yet. For release-candidate
testing, build from this directory and install the exact wheel:

```powershell
python -m build
python -m twine check dist\*.whl dist\*.tar.gz
python -m pip install dist\agent_framework_mongodb-*.whl
```

Do not install an unverified registry project with this name. The package
requires Python 3.10 or later, Agent Framework Core 1.13 or later (but below
2.0), PyMongo 4.13 or later (but below 5.0), and OpenTelemetry API 1.39 or later
(but below 2.0). Only versions recorded in the
[compatibility evidence](../docs/development/release/python-packaging.md) are
release-tested.

## Choose a feature

| Feature | Preserves | Does not replace | Sample |
| --- | --- | --- | --- |
| Memory | scoped semantic conversation recall | exact replay or authoritative knowledge | [`memory_quickstart.py`](samples/memory_quickstart.py) |
| Chat History | exact ordered supported messages | semantic recall or complete session state | [`history_quickstart.py`](samples/history_quickstart.py) |
| RAG | attributed read-only knowledge results | conversation learning or ingestion | [Vector](samples/rag_vector_quickstart.py), [full text](samples/rag_full_text_quickstart.py), [hybrid](samples/rag_hybrid_quickstart.py) |
| Session Store | complete versioned `AgentSession` snapshots | transcript queries or workflow lineage | [`session_persistence.py`](samples/session_persistence.py) |
| Workflow Checkpoint Store | resumable workflow state and lineage | complete sessions or exact chat replay | [`workflow_checkpoint_resume.py`](samples/workflow_checkpoint_resume.py) |

Applications may combine providers deliberately; provider lifecycles, scopes,
collections, and authorization remain separate.

Provider-agnostic scenario fixtures cover
[parent hydration](samples/rag_parent_document.py),
[on-demand tools](samples/on_demand_retrieval_tool.py),
[workflow retrieval](samples/workflow_retrieval.py),
[Memory with RAG](samples/memory_and_rag.py),
[structured metadata](samples/structured_metadata_retrieval.py), and the
[bounded document loader](samples/document_loader.py). They need no external
model-provider identity; MongoDB-backed execution still requires the documented
deployment and least-privilege credentials.

## Environment and privileges

All samples require `MONGODB_URI` (except ingestion, which uses
`MONGODB_INGESTION_URI`), `MONGODB_DATABASE`, and the feature-specific variables
listed in [`samples/README.md`](samples/README.md). Missing configuration is
reported before network access. Never commit connection strings.

Use separate identities:

- **runtime:** only the feature's required read or scoped persistence operations;
- **provisioner:** explicit index create, update, inspect, and drop operations;
- **sample ingestion:** bounded reads and writes for uniquely prefixed demo data.

Runtime RAG is read-only. Public filters are typed and operator-limited; no
model controls BSON, MongoDB field names, operators, index names, or pipelines.

## Index provisioning

Run `samples\index_provisioning.py` under a dedicated provisioner identity to
explicitly create/update and wait for RAG Vector Search and Search indexes. Set
`MONGODB_URI`, `MONGODB_DATABASE`, `MONGODB_RAG_COLLECTION`,
`MONGODB_RAG_VECTOR_INDEX`, `MONGODB_RAG_SEARCH_INDEX`, and the positive
`MONGODB_RAG_VECTOR_DIMENSIONS`, then pass `--apply` to acknowledge the explicit
index mutation. Runtime providers never call these mutating operations implicitly.

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

## Session Store quickstart

Session Store persists a complete `AgentSession`, including registered
provider-owned state, for stateless hosting. It is not exact Chat History or a
workflow checkpoint ledger.

```python
from datetime import timedelta

from agent_framework_mongodb import MongoDBSessionStore, MongoDBSessionStoreOptions

store = MongoDBSessionStore(
    connection_string=os.environ["MONGODB_URI"],
    database_name=os.environ["MONGODB_DATABASE"],
    collection_name=os.environ["MONGODB_SESSION_COLLECTION"],
    options=MongoDBSessionStoreOptions(
        tenant_id=os.environ["MONGODB_SESSION_TENANT_ID"],
        application_id=os.environ["MONGODB_SESSION_APPLICATION_ID"],
        agent_id=os.environ["MONGODB_SESSION_AGENT_ID"],
        ttl=timedelta(days=7),
    ),
)
await store.ensure_indexes()
version = await store.create("session-123", session)
version = await store.compare_and_set(
    "session-123",
    continued_session,
    expected_version=version,
)
```

The package publicly exports `MongoDBSessionStore`,
`MongoDBSessionStoreOptions`, `MongoDBVersionedSession`, and
`MongoDBConcurrencyError`. Every operation uses the immutable authorization
scope in its MongoDB filter. Index provisioning and authorized deletion are
explicit. See `samples\session_persistence.py` and
[`docs/development/persistence/python-session-store.md`](../docs/development/persistence/python-session-store.md).

## Workflow Checkpoint Store quickstart

Workflow Checkpoint Store persists immutable resumable workflow history,
including pending approvals, executor state, and parent lineage. It is separate
from complete Session Store snapshots and exact Chat History.

```python
from agent_framework_mongodb import (
    MongoDBCheckpointStorage,
    MongoDBCheckpointStorageOptions,
)

checkpoints = MongoDBCheckpointStorage(
    connection_string=os.environ["MONGODB_URI"],
    database_name=os.environ["MONGODB_DATABASE"],
    collection_name=os.environ["MONGODB_CHECKPOINT_COLLECTION"],
    options=MongoDBCheckpointStorageOptions(
        tenant_id=os.environ["MONGODB_CHECKPOINT_TENANT_ID"],
        workflow_name="approval-workflow",
        session_id="run-123",
        ttl=timedelta(days=7),
    ),
)
await checkpoints.ensure_indexes()
workflow = WorkflowBuilder(
    name="approval-workflow",
    start_executor=approval_executor,
    checkpoint_storage=checkpoints,
).build()
```

The package exports `MongoDBCheckpointStorage`,
`MongoDBCheckpointStorageOptions`, `MongoDBCheckpointPage`,
`MongoDBCheckpointClearResult`, and
`MongoDBCheckpointNotFoundError`. The exact `CheckpointStorage` list methods
traverse bounded pages to enumerate the complete run;
`list_checkpoint_page()` exposes one bounded cursor page. `clear_run()` removes
the exact authorized run's checkpoints and sequence counter with acknowledged
counts. Every operation uses the immutable tenant/workflow/session scope.
See `samples\workflow_checkpoint_resume.py` and
[`docs/development/persistence/python-checkpoints.md`](../docs/development/persistence/python-checkpoints.md).

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
`MongoDBSearchMode`. Vector ANN/ENN, full-text Search, and native hybrid RRF are implemented.
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

Parent-document mode defaults to a provider-controlled
`record_type == "child"` retrieval predicate. Configure
`MongoDBRAGParentOptions.child_record_field` and `child_record_value` only for a
different safe schema. The field path and non-null scalar value are validated,
the discriminator is required in each active search index, and it is applied
before child candidates are limited. Parent hydration reapplies authorization
but not this child-only predicate.

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
        # Only documented built-in analyzers are accepted; custom definitions
        # are intentionally outside this narrow provider option.
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

## Hybrid RAG quickstart

Hybrid RAG combines ANN and full-text input rankings with MongoDB's native
`$rankFusion` reciprocal-rank fusion. Both branches independently apply the
complete typed authorization filter before their candidate limits.

```python
direct = MongoDBRAGProvider(
    MongoDBRAGProviderOptions(
        mode=MongoDBSearchMode.HYBRID_RRF,
        vector_dimensions=1536,
        vector_index_name="knowledge_vector",
        search_index_name="knowledge_search",
        num_candidates=50,
        top_k=5,
        vector_weight=1.0,
        text_weight=1.0,
        filter=EqualFilter("tenant_id", "tenant-123"),
    ),
    embedding_generator=embedding_generator,
    connection_string=os.environ["MONGODB_URI"],
    database_name=os.environ["MONGODB_DATABASE"],
    collection_name=os.environ["MONGODB_RAG_COLLECTION"],
)
await direct.validate_vector_search_index()
await direct.validate_search_index()
await direct.validate_capabilities()
results = await direct.search("tenant isolation")
```

Run `samples\rag_hybrid_quickstart.py` after setting `MONGODB_URI`,
`MONGODB_DATABASE`, `MONGODB_RAG_COLLECTION`, `MONGODB_RAG_VECTOR_INDEX`,
`MONGODB_RAG_SEARCH_INDEX`, and `MONGODB_RAG_TENANT`. Its deterministic
three-dimensional generator is runnable only with pre-ingested matching vectors;
replace it with the generator used to embed production content. The target must
be MongoDB 8.0 or later with Search, Vector Search, and native `$rankFusion`
enabled. Explicit index ensure needs provisioner privileges. Normal retrieval
needs index inspection, read/aggregate, and Search query privileges and performs
no writes.

## Sample-only incremental ingestion

Runtime RAG is read-only. The separately run
[`samples\incremental_ingestion.py`](samples/README.md) demonstration uses a
dedicated write-capable identity to load only uniquely sample-prefixed source
records, skip unchanged hashes, replace changed records, process tombstones, and
perform prefix-targeted cleanup. It waits for an existing Vector Search index but
never creates one. Prefix reads and cleanup force simple binary collation, and a
Unicode-successor upper bound safely includes supplementary IDs. A duplicate
source-ID preflight fails before any target write.

The sample requires explicit connection, collection, index, model, dimensions,
embedding-factory, and unique-prefix environment configuration and refuses to
write without `--apply`. See [`samples\README.md`](samples/README.md) for the
collection contract, least-privilege split, limits, commands, and cleanup.

## Limitations and release status

- Search modes require compatible MongoDB Search deployment capabilities and
  explicitly provisioned indexes; there is no in-memory downgrade.
- The package does not provide production ingestion, arbitrary MongoDB agent
  tools, model-generated pipelines, fact extraction, or graph behavior.
- Python and .NET preserve equivalent observable behavior but do not claim a
  shared physical stored schema without cross-language fixture evidence.
- Credentialed Search and persistence integration evidence, named publishing
  owners, the PyPI trusted-publishing environment, support/security contacts,
  and the organization signing policy remain external release blockers.
See the [developer packaging guide](../docs/development/release/python-packaging.md)
for artifact policy, API compatibility, dependency evidence, and exact
validation commands.
