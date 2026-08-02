# Python full-text RAG

This document describes implementation-map
[slice 9](../../spec/implementation-map.md). Normative behavior is defined by
the [RAG](../../spec/features/rag.md),
[index management](../../spec/features/index-management.md),
[resilience](../../spec/resilience.md), and
[observability/security](../../spec/observability-security.md) specifications.
ADRs [0007](../../decisions/0007-use-typed-filters-and-native-search-pipelines.md),
[0010](../../decisions/0010-fail-open-only-at-agent-adapter-boundaries.md), and
[0011](../../decisions/0011-release-features-through-staged-quality-gates.md)
record the rationale.

## Public seams and pipeline

`MongoDBRAGProvider.search()` and `MongoDBRAGContextProvider.search()` are the
direct-search seams. Select `MongoDBSearchMode.FULL_TEXT`, configure
`search_index_name`, one or more `text_fields`, and the index
`search_analyzer`. Full-text mode forbids vector dimensions, vector index
options, candidates, and query embeddings.

The provider validates the effective Search index, then emits structured
PyMongo aggregation documents in this order:

```javascript
[
  {
    $search: {
      index: searchIndex,
      compound: {
        must: [{ text: { query: queryText, path: textFields } }],
        filter: translatedProviderAndCallFilters
      }
    }
  },
  { $limit: topK },
  { $set: { _ragScore: { $meta: "searchScore" } } }
]
```

`$search` is always first. The provider-owned authorization filter and optional
per-call relevance filter are conjoined and translated completely into
`compound.filter` before `$limit`; an unsupported AST fails before index or
aggregation I/O. The filter member is omitted when no filter is configured.
No query, field path, index name, operator, or pipeline is model-controlled.
The runtime path calls only index inspection and `aggregate`; it never writes
documents or provisions indexes.

`MongoDBRAGResult` maps configured nested ID, text, source name/URL, and
metadata paths while retaining the original document. `_ragScore` is exposed
unchanged as MongoDB `searchScore`; it is not normalized or described as a
probability. `to_citation()` preserves source attribution for Agent Framework.

## Filter mappings and Search index lifecycle

`SearchIndexManager` in
`python/src/agent_framework_mongodb/_shared/indexes.py` provides the same
missing/building/ready/failed state semantics, explicit ensure behavior,
bounded monotonic polling, cancellation, and stable error categories as the
Vector Search manager.

`validate_search_index()` is read-only. It compares the index name/type,
READY/queryable state, every configured text path, and the configured analyzer.
It also validates effective filter paths and their inferred Search mapping:
strings use `token`, booleans use `boolean`, numbers use `number`, and
timezone-aware datetimes use `date`. Mixed BSON types for one path and null
Search equality values fail before I/O.

`ensure_search_index()` is the only full-text create/update facade. It creates
a Search index with dynamic mappings plus explicit text/analyzer and filter
mappings. Dotted paths become nested `document` mappings. Ensure is never
called by construction, direct search, or Agent Framework hooks. Use a
provisioner identity for ensure; runtime identities need only index inspection,
read/aggregate, and Search query privileges.

## Parent hydration, resilience, and ownership

Optional `MongoDBRAGParentOptions` uses ranked child IDs to perform one bounded
same-database parent aggregation. That `$match` combines the parent IDs with a
complete classic-MongoDB translation of the provider authorization filter.
Per-call relevance filters remain child constraints. Parent ordering, fan-out,
text size, and context size retain the Vector RAG bounds.

Direct search, capability/index validation, mapping, and ensure failures
propagate with stable integration errors and the PyMongo failure as cause.
Only transient retrieval/deadline errors fail open in
`MongoDBRAGContextProvider.before_run`; cancellation, authorization,
configuration, filter, capability, index, and mapping failures propagate.
Warnings contain only low-cardinality operation fields. Queries, filters,
documents, source data, credentials, and driver messages are not logged.

Injected clients and collections remain caller-owned. A URI-created asynchronous
PyMongo client is provider-owned and closes through `close()` or the async
context manager.

## Verification

`python/tests/unit/test_rag_full_text.py` covers pipeline order and filter
placement, score/source/citation mapping, analyzer/index validation, explicit
ensure, parent authorization, transient adapter behavior, cancellation, and
read-only execution. Contract tests retain typed-filter parity.
`python/tests/integration_rag_search/test_rag_search_integration.py` uses a
unique `af_rag_search_test_` collection and proves cross-tenant exclusion on a
Search-capable deployment. It skips cleanly without credentials or capability.

Validated commands are recorded with the owning change; the package quality
gate is `ruff`, strict `mypy`, strict `pyright`, full `pytest`, distribution
build/check, and clean artifact import smoke testing.
