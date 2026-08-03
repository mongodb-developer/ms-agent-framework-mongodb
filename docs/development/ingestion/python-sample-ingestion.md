# Python sample ingestion

This document describes implementation-map slice 14, Python only. The normative
requirements are [Knowledge ingestion](../../spec/features/ingestion.md),
[Samples](../../spec/samples.md), [RAG](../../spec/features/rag.md), and
[security](../../spec/observability-security.md). ADR
[0002](../../decisions/0002-separate-memory-history-rag-and-persistence.md)
keeps this writer outside all runtime providers; ADR
[0007](../../decisions/0007-use-typed-filters-and-native-search-pipelines.md)
requires structured MongoDB operations and validated field paths.

## Boundary and control flow

`python/samples/ingestion_helpers.py` is a sample namespace and is not included in
the `agent-framework-mongodb` wheel. Its public sample seams are:

- `IngestionDocument`: ingestion-neutral source record.
- `MongoDBDocumentLoader.load()`: async, ascending source-ID keyset pagination.
- `IncrementalIngestor.ingest()`: bounded hash comparison, embedding, and writes.
- `IncrementalIngestor.cleanup()`: target deletion constrained to the configured
  sample/test prefix.
- `IngestionResult`: scanned, upserted, unchanged, and deleted counts.

The runnable `python/samples/incremental_ingestion.py` waits up to ten minutes for
the existing Vector Search index through the public read-only
`MongoDBRAGProvider` before ingestion. It never calls an index creation/update API.

```text
sample source -> bounded loader -> neutral documents -> hash lookup
             -> changed-only embedding -> structured bulk replace/upsert -> RAG collection
```

Cancellation is not caught at cursor, embedding, index-validation, or write
awaits. Driver failures and mapping/configuration errors fail directly to the
caller. This demonstration adds no retry or durable checkpoint behavior.

## Collection compatibility contract

The default target shape is:

```json
{
  "_id": "sample-run-<sha256 of UTF-8 source ID>",
  "source_id": "sample-source-1",
  "content": "UTF-8 Python string",
  "embedding": [0.1, 0.2, 0.3],
  "embedding_model": "caller-model-id",
  "content_hash": "<sha256 hex>",
  "title": "Source title",
  "url": "https://example.invalid/source",
  "metadata": {"section": 1},
  "tenant_id": "sample-tenant"
}
```

Target field paths can be configured on `IncrementalIngestor`; the runnable
sample exposes text and vector field overrides. Paths reject empty segments,
operator-prefixed segments, null bytes, positional syntax, and overlapping
targets. Documents and write filters are built as mappings and PyMongo write
models, never strings or model-produced BSON.

The deterministic ID is the run prefix followed by SHA-256 of the UTF-8 source
ID. The content hash is canonical JSON over content, title, URL, metadata,
tenant, embedding model identifier, and dimensions. Sorted keys and compact
separators make reruns deterministic. An unchanged hash causes no embedding or
write. Changed records use explicit whole-document replacement with upsert.
Changing the model identifier or dimensions changes the hash and refreshes the
vector. A source tombstone deletes only its derived deterministic ID.

The Vector Search index must map the configured vector field with exactly
`MONGODB_RAG_VECTOR_DIMENSIONS` and include any RAG authorization filter fields,
such as `tenant_id`. Query-time embedding generation must use a compatible model.
The sample does not claim cross-language physical schema compatibility.

## Security and operations

Source reads are fixed structured range queries over a required unique
`sample-`/`test-` prefix. The loader projects configured fields and accepts no
caller or model BSON. Page and embedding/write batch sizes are independently
bounded to 1–1000. Duplicate source IDs fail the pass rather than allowing
unordered last-writer behavior.

Use three identities:

1. ingestion: source read plus target sample find/insert/replace/delete and index
   inspection;
2. runtime RAG: index inspection, read/aggregate, and Search query only;
3. provisioner: explicit index management through the separate provisioning
   sample.

The script logs aggregate counts only. It does not log URIs, credentials,
content, embeddings, URLs, tenant values, hashes, or IDs. TLS/network access and
credential rotation remain deployment responsibilities. Cleanup uses a bounded
range on the validated output prefix; choose a unique prefix for every test run.

## Verification

`python/tests/unit/test_ingestion_samples.py` uses source, target, and embedding
boundary fakes. It covers paging/projection, mapping, field validation,
deterministic IDs, changed/unchanged behavior, model refresh, batch dimensions,
bounded batches, tombstones, cleanup isolation, duplicate IDs, cancellation, and
required environment configuration. No credentialed integration test is needed
for this sample-only seam; existing RAG integration suites validate real index
inspection and runtime retrieval.

Run:

```powershell
python -m pytest tests\unit\test_ingestion_samples.py
ruff check samples tests\unit\test_ingestion_samples.py
ruff format --check samples tests\unit\test_ingestion_samples.py
```

See [`python/samples/README.md`](../../../python/samples/README.md) for environment,
execution, expected output, and cleanup instructions.
