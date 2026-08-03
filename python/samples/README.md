# Python samples

These programs are demonstrations, not production ingestion or orchestration APIs.
Runtime RAG remains read-only.

## Incremental ingestion

`incremental_ingestion.py` copies only sample-prefixed records from a bounded
MongoDB source collection into an existing RAG collection. It waits up to ten
minutes for the existing Vector Search index through `MongoDBRAGProvider`, embeds changed content
in batches, and submits structured `ReplaceOne(..., upsert=True)` operations.
Tombstones submit targeted deletes. `--cleanup` deletes only deterministic target
IDs owned by `MONGODB_RAG_SAMPLE_PREFIX`.

Use a dedicated ingestion identity. Do **not** give these write credentials to the
runtime RAG process. The ingestion identity needs read access to the source,
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
The sample has no crawler, scheduler, retry loop, OCR, arbitrary query input, or
index mutation. Use `index_provisioning.py` separately under provisioner
credentials when an index does not yet exist.
