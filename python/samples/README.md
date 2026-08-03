# Python samples

These programs are demonstrations, not production ingestion or orchestration APIs.
Runtime RAG remains read-only.

## Workflow checkpoint resumption

`workflow_checkpoint_resume.py` runs an Agent Framework workflow until a pending
deployment approval is checkpointed, creates a new workflow instance, resumes it
from the latest checkpoint with an approval response, inspects a bounded page,
and deletes only the authorized run's checkpoint IDs unless `--keep` is passed.
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
MongoDB TTL cleanup is eventual and can leave lineage gaps. The default cleanup
deletes only IDs first listed under the constructor-bound tenant/workflow/session
scope and never drops the collection. Expected output reports only status and
bounded counts, not IDs, scope values, or checkpoint state.

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
