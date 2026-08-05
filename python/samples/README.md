# Python samples

These programs are demonstrations, not production ingestion or orchestration APIs.
Runtime RAG remains read-only.

## Setup and safety

Run commands from `python` after installing the package (`python -m pip install
-e .` for development). Every sample imports without credentials and validates
required environment variables before contacting MongoDB. Use unique
sample-prefixed scopes and separate identities for runtime persistence,
read-only retrieval, index provisioning, and sample ingestion.

| Sample | Feature | Writes | Cleanup |
| --- | --- | --- | --- |
| `memory_quickstart.py` | semantic Memory | scoped sample memory and explicit index ensure | clears its sample session unless `--keep`; does not drop collection/index |
| `history_quickstart.py` | exact Chat History | scoped sample transcript and explicit indexes | optional targeted clear with `MONGODB_HISTORY_CLEAR=true` |
| `rag_vector_quickstart.py` | vector ANN RAG | explicit index ensure only | no document cleanup |
| `rag_full_text_quickstart.py` | full-text RAG | explicit index ensure only | no document cleanup |
| `rag_hybrid_quickstart.py` | native hybrid RRF | explicit index ensures only | no document cleanup |
| `rag_parent_document.py` | bounded parent hydration | read-only retrieval | no document cleanup |
| `on_demand_retrieval_tool.py` | query-text-only framework tool | read-only retrieval | no document cleanup |
| `workflow_retrieval.py` | deterministic workflow retrieval step | read-only retrieval | no document cleanup |
| `memory_and_rag.py` | one model-free fixture agent with separate Memory and RAG | scoped Memory persistence; read-only RAG | use a unique Memory user scope; no collection cleanup |
| `structured_metadata_retrieval.py` | typed structured query plan | read-only retrieval | no document cleanup |
| `document_loader.py` | bounded ingestion-neutral source mapping | read-only source access | no cleanup |
| `index_provisioning.py` | provisioner-only indexes | creates/updates Search indexes with `--apply` | explicit administrative cleanup only |
| `session_persistence.py` | complete Session Store | scoped session and indexes | targeted delete unless `--keep` |
| `workflow_checkpoint_resume.py` | resumable checkpoints | scoped checkpoints/counter and indexes | targeted run clear unless `--keep` |
| `incremental_ingestion.py` | sample-only ingestion | sample-prefixed target records with `--apply` | `--apply --cleanup` removes only that prefix |

The RAG quickstarts use deterministic three-dimensional vectors for setup
demonstration. Existing documents and Vector Search indexes must use the same
dimensions. Replace the generator with the production embedding generator
before using production data.

## Memory

Set `MONGODB_URI`, `MONGODB_DATABASE`, and
`MONGODB_MEMORY_COLLECTION`. The runtime identity needs scoped find, insert,
and targeted delete privileges. The call to `ensure_vector_search_index` is an
explicit provisioning operation and also requires index privileges; production
deployments should run provisioning separately.

```powershell
python samples\memory_quickstart.py --keep
```

Expected output is zero or more recalled message texts. The sample stores one
message under fixed demonstration application/user/session scopes. It clears
only that session unless `--keep` is passed, and never drops the collection or
index.

## Exact Chat History

Set `MONGODB_URI`, `MONGODB_DATABASE`, `MONGODB_HISTORY_COLLECTION`,
`MONGODB_HISTORY_APPLICATION_ID`, `MONGODB_HISTORY_AGENT_ID`, and
`MONGODB_HISTORY_SESSION_ID`. Use a unique session ID. The identity needs
find, insert, atomic sequencing, and explicit regular-index privileges.

```powershell
python samples\history_quickstart.py
$env:MONGODB_HISTORY_CLEAR = "true"
python samples\history_quickstart.py
```

Expected output replays user, assistant tool-call, and tool-result messages in
order. Cleanup is disabled by default; when enabled it clears only the complete
constructor-bound authorized scope. It never drops the collection.

## Vector RAG

Set `MONGODB_URI`, `MONGODB_DATABASE`, `MONGODB_RAG_COLLECTION`,
`MONGODB_RAG_VECTOR_INDEX`, and `MONGODB_RAG_TENANT`. The pre-ingested
collection needs `content`, three-dimensional `embedding`, `tenant_id`, and
optional `source.name`/`source.url` fields. The Vector Search index must map the
vector and tenant filter fields with cosine similarity.

```powershell
python samples\rag_vector_quickstart.py
```

Expected output contains score, source/id, and text for authorized documents.
The sample explicitly ensures the index and therefore needs provisioner
privileges in addition to read/aggregate/Search query access. It performs no
document insert, update, or delete.

## Full-text RAG

Set `MONGODB_URI`, `MONGODB_DATABASE`, `MONGODB_RAG_COLLECTION`,
`MONGODB_RAG_SEARCH_INDEX`, and `MONGODB_RAG_TENANT`. The Search index must map
`content` with `lucene.standard` and map `tenant_id` for filtering.

```powershell
python samples\rag_full_text_quickstart.py
```

Expected output contains Search score, source/id, and text. Index ensure is
explicit and requires a provisioner identity; normal retrieval needs only
index inspection, read/aggregate, and Search query privileges. No documents are
written or deleted.

## Hybrid RRF

Set all Vector and full-text variables:
`MONGODB_URI`, `MONGODB_DATABASE`, `MONGODB_RAG_COLLECTION`,
`MONGODB_RAG_VECTOR_INDEX`, `MONGODB_RAG_SEARCH_INDEX`, and
`MONGODB_RAG_TENANT`. The deployment must be MongoDB 8.0 or later with Search,
Vector Search, and native `$rankFusion`. Both indexes must map the authorization
field and the Vector index must use three dimensions.

```powershell
python samples\rag_hybrid_quickstart.py
```

Expected output contains fused score, source/id, and text. The sample explicitly
ensures both indexes and validates native capability; it never falls back to
application-side fusion and performs no document writes or cleanup.

## Explicit index provisioning

Set `MONGODB_URI`, `MONGODB_DATABASE`, `MONGODB_RAG_COLLECTION`,
`MONGODB_RAG_VECTOR_INDEX`, `MONGODB_RAG_SEARCH_INDEX`, and the positive
`MONGODB_RAG_VECTOR_DIMENSIONS`. Optional field variables are
`MONGODB_RAG_VECTOR_FIELD` and `MONGODB_RAG_TEXT_FIELD`.

```powershell
python samples\index_provisioning.py --apply --vector-dimensions 1536
```

Without `--apply` the command exits before mutation. Expected output names each
index and its ready state. Run only with an index-provisioning identity.
Dropping indexes or collections is intentionally not automated.

## Parent-document RAG

`rag_parent_document.py` searches authorized child records and hydrates at most
three parents with bounded fan-out and context tokens. Set `MONGODB_URI`,
`MONGODB_DATABASE`, `MONGODB_RAG_COLLECTION`, `MONGODB_RAG_VECTOR_INDEX`, and
`MONGODB_RAG_TENANT`. The three-dimensional Vector Search index must map
`embedding`, `tenant_id`, and `record_type`; child records use
`record_type="child"` and `parent_id`, while parent records use `_id` and
`content`.

```powershell
python samples\rag_parent_document.py
```

Expected output is parent score, source/id, and hydrated parent text. Validation
and retrieval are read-only. The runtime identity needs index inspection,
read/aggregate, and Vector Search query privileges.

## On-demand retrieval tool

`on_demand_retrieval_tool.py` creates an Agent Framework `FunctionTool` whose
schema contains only the natural-language `query` string. Tenant policy, index,
fields, limits, and typed filters remain application-owned. Set
`MONGODB_URI`, `MONGODB_DATABASE`, `MONGODB_RAG_COLLECTION`,
`MONGODB_RAG_SEARCH_INDEX`, and `MONGODB_RAG_TENANT`.

```powershell
python samples\on_demand_retrieval_tool.py
```

The model-free fixture invokes the tool directly and prints attributed results.
It needs read-only Search privileges and performs no cleanup.

## Workflow retrieval

`workflow_retrieval.py` puts direct full-text retrieval in a deterministic
Agent Framework executor; no model chooses whether or how the database is
queried. Use the same environment and Search index contract as the on-demand
sample.

```powershell
python samples\workflow_retrieval.py
```

Expected output is the authorized, attributed retrieval result emitted by the
workflow. The sample is read-only and has no cleanup.

## Memory and RAG

`memory_and_rag.py` constructs one Agent with separate
`MongoDBMemoryContextProvider` and `MongoDBRAGContextProvider` instances. Its
local fixture chat client requires no model-provider account and reports the
provider source attribution it receives. Set `MONGODB_URI`,
`MONGODB_DATABASE`, `MONGODB_MEMORY_COLLECTION`, `MONGODB_MEMORY_USER_ID`,
`MONGODB_RAG_COLLECTION`, `MONGODB_RAG_VECTOR_INDEX`, and
`MONGODB_RAG_TENANT`. Both Memory and RAG Vector Search indexes must use the
sample's three-dimensional vectors.

```powershell
python samples\memory_and_rag.py
```

RAG remains read-only. Memory may persist the fixture turn under application
`memory-rag-sample` and the configured user, so use a unique user value and
remove that scope through an authorized Memory cleanup operation after review.
The sample never drops a collection or index.

## Structured metadata retrieval

`structured_metadata_retrieval.py` translates a closed, typed
`RetrievalPlan` into `EqualFilter` and `InFilter`; it never accepts a BSON
document, operator, field path, index, or pipeline from structured output. Set
`MONGODB_URI`, `MONGODB_DATABASE`, `MONGODB_RAG_COLLECTION`,
`MONGODB_RAG_SEARCH_INDEX`, and `MONGODB_RAG_TENANT`. The Search index must map
`content`, `tenant_id`, `metadata.category`, and `visibility`.

```powershell
python samples\structured_metadata_retrieval.py
```

Expected output is up to three authorized security-category results. Retrieval
is read-only and requires no cleanup.

## Bounded document loader

`document_loader.py` maps sample-prefixed source records into
ingestion-neutral documents with duplicate detection, projection, simple
binary collation, ascending keyset pagination, and bounded output. Set
`MONGODB_URI`, `MONGODB_DATABASE`, `MONGODB_INGESTION_SOURCE_COLLECTION`, and
a unique `MONGODB_RAG_SAMPLE_PREFIX` beginning with `sample-` or `test-`.

```powershell
python samples\document_loader.py --page-size 100 --max-documents 10
```

The source identity needs only aggregate and find access. The command prints
mapping metadata for at most the requested number of records and performs no
writes or cleanup.

## Workflow checkpoint resumption

`workflow_checkpoint_resume.py` runs an Agent Framework workflow until a pending
deployment approval is checkpointed, creates a new workflow instance, resumes it
from the latest checkpoint with an approval response, inspects a bounded page,
and clears only the authorized run's checkpoints and sequence counter unless
`--keep` is passed.
It preserves pending requests, executor state, and lineage through the public
Agent Framework 1.13 checkpoint contract.

Set `MONGODB_URI`, `MONGODB_DATABASE`, `MONGODB_CHECKPOINT_COLLECTION`,
`MONGODB_CHECKPOINT_TENANT_ID`, `MONGODB_CHECKPOINT_WORKFLOW_NAME`, and
`MONGODB_CHECKPOINT_SESSION_ID`. `MONGODB_CHECKPOINT_APPLICATION_ID` is optional
and `MONGODB_CHECKPOINT_TTL_SECONDS` defaults to 3600. Use a unique session ID
for each sample run.

From `python`:

```powershell
python samples\workflow_checkpoint_resume.py
python samples\workflow_checkpoint_resume.py --keep
```

Runtime needs find, insert, atomic update/upsert, and targeted delete privileges.
The sample explicitly creates regular indexes and therefore also needs
index-provisioning privileges; production should provision separately.
MongoDB TTL cleanup is eventual, covers checkpoints and their refreshed scoped
counter, and can leave lineage gaps. The default `clear_run()` cleanup applies
the complete constructor-bound tenant/workflow/session scope and never drops the
collection. Expected output reports only status and bounded acknowledged counts,
not IDs, scope values, or checkpoint state.

## Session persistence

`session_persistence.py` saves a complete public Agent Framework `AgentSession`,
reloads and continues it with compare-and-swap, configures UTC expiration, and
performs an authorized versioned delete. It uses only the immutable
tenant/application/agent scope supplied by the application; the session ID alone
is never authorization.

Set `MONGODB_URI`, `MONGODB_DATABASE`, `MONGODB_SESSION_COLLECTION`,
`MONGODB_SESSION_TENANT_ID`, `MONGODB_SESSION_APPLICATION_ID`,
`MONGODB_SESSION_AGENT_ID`, and `MONGODB_SESSION_ID`.
`MONGODB_SESSION_TTL_SECONDS` defaults to 3600. Use a dedicated runtime identity
with find, insert, replace/update, and targeted delete privileges. The sample
also explicitly creates regular indexes, so that run requires index-provisioning
privileges; production deployments should provision them separately.

From `python`:

```powershell
python samples\session_persistence.py
python samples\session_persistence.py --keep
```

The default run deletes only its exact authorized session. `--keep` leaves that
snapshot for MongoDB's asynchronous TTL monitor. The collection is never
dropped. Expected output reports versions and cleanup count without scope or
payload data.

## Incremental ingestion

`incremental_ingestion.py` copies only sample-prefixed records from a bounded
MongoDB source collection into an existing RAG collection. It waits up to ten
minutes for the existing Vector Search index through `MongoDBRAGProvider`, embeds changed content
in batches, and submits structured `ReplaceOne(..., upsert=True)` operations.
Tombstones submit targeted deletes. `--cleanup` deletes only deterministic target
IDs owned by `MONGODB_RAG_SAMPLE_PREFIX`.

Use a dedicated ingestion identity. Do **not** give these write credentials to the
runtime RAG process. The ingestion identity needs read/aggregate access to the source,
find/replace/insert/delete access to the target sample records, and index-inspection
access. Index creation needs a separate provisioner identity. Runtime RAG needs
only index inspection, read/aggregate, and Search query privileges.

Required environment variables:

| Variable | Purpose |
| --- | --- |
| `MONGODB_INGESTION_URI` | Write-capable ingestion connection string; never commit it |
| `MONGODB_DATABASE` | Source and target database |
| `MONGODB_INGESTION_SOURCE_COLLECTION` | Distinct local/demo source collection |
| `MONGODB_RAG_COLLECTION` | Existing RAG target collection |
| `MONGODB_RAG_VECTOR_INDEX` | Existing Vector Search index |
| `MONGODB_RAG_VECTOR_DIMENSIONS` | Positive dimensions produced by the generator |
| `MONGODB_RAG_SAMPLE_PREFIX` | Unique `sample-` or `test-` run prefix |
| `MONGODB_EMBEDDING_MODEL` | Model identifier included in content hashes |
| `MONGODB_EMBEDDING_FACTORY` | Caller module factory in `module:callable` form |

The factory receives `MONGODB_EMBEDDING_MODEL` and must return an Agent Framework
embedding generator. The generator used by runtime queries must be compatible
with the stored model and dimensions.

Optional source field variables are
`MONGODB_INGESTION_SOURCE_ID_FIELD`, `MONGODB_INGESTION_CONTENT_FIELD`,
`MONGODB_INGESTION_TITLE_FIELD`, `MONGODB_INGESTION_URL_FIELD`,
`MONGODB_INGESTION_METADATA_FIELD`, `MONGODB_INGESTION_TENANT_FIELD`, and
`MONGODB_INGESTION_DELETED_FIELD`. Target text/vector fields may be set with
`MONGODB_RAG_TEXT_FIELD` and `MONGODB_RAG_VECTOR_FIELD`. All field paths are
validated and are never model-controlled.

From `python`:

```powershell
python -m samples.incremental_ingestion --apply --page-size 100 --batch-size 100
python -m samples.incremental_ingestion --apply --cleanup
```

Expected output reports only counts, for example:

```text
Scanned 3; upserted 2; unchanged 1; deleted 0.
```

Pages and batches are limited to 1–1000. Cancellation propagates immediately.
Every sample-prefix range uses MongoDB's `simple` binary collation and the
exclusive Unicode successor of the prefix, regardless of the collection default.
This includes IDs containing supplementary characters such as emoji; a fixed
`prefix + U+FFFF` sentinel is not used. Prefixes with invalid Unicode scalar
values fail configuration. Before yielding any record, the loader runs a bounded
duplicate-ID aggregate and raises `IngestionDataError` if uniqueness would be
ambiguous; therefore page boundaries cannot silently select one duplicate or
allow an ingestion write first.
The sample has no crawler, scheduler, retry loop, OCR, arbitrary query input, or
index mutation. Use `index_provisioning.py` separately under provisioner
credentials when an index does not yet exist.
