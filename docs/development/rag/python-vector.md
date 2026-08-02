# Python Vector RAG

This document describes implementation-map
[slice 7](../../spec/implementation-map.md). The normative behavior is defined
by the [RAG](../../spec/features/rag.md),
[interfaces](../../spec/interfaces.md), [index management](../../spec/features/index-management.md),
[resilience](../../spec/resilience.md), and
[observability/security](../../spec/observability-security.md) specifications.
ADRs [0007](../../decisions/0007-use-typed-filters-and-native-search-pipelines.md),
[0010](../../decisions/0010-fail-open-only-at-agent-adapter-boundaries.md), and
[0011](../../decisions/0011-release-features-through-staged-quality-gates.md)
record rationale without weakening those requirements.

## Public seams and control flow

`MongoDBRAGProvider` in
`python/src/agent_framework_mongodb/rag/provider.py` owns deterministic direct
search. Construction validates immutable mappings, bounds, mode options, and
ownership but performs no I/O. `search()` rejects empty queries, resolves
per-call options without replacing the application filter, validates the named
index before embedding by default, requests exactly one query embedding,
validates count/dimensions/finite values, runs an aggregation, and maps
`MongoDBRAGResult`.

ANN emits `numCandidates`; ENN emits `exact: true`. The two options never coexist.
`$vectorSearch` is first, and the complete typed filter is nested in its
`filter` property before `limit`. `_ragScore` captures MongoDB's
`vectorSearchScore`; all original fields remain available as `RawDocument`.
Configured nested text, source title/URL, and metadata paths are resolved without
dynamic code. Missing ID/text/score raises `MongoDBMappingError`; optional source
and metadata fields remain absent.

`MongoDBRAGContextProvider(ContextProvider)` delegates to the same direct search.
`before_run` constructs a query from the bounded recent user/assistant input,
adds an instruction that retrieved text is attributed data rather than trusted
instructions, and injects system messages with framework citation annotations
and provider source attribution. It does not mark knowledge as originating from
another conversation session. `after_run` is intentionally a no-op.

## Parent hydration

When `MongoDBRAGParentOptions` is present, child results provide a bounded,
de-duplicated parent-ID set. A second read-only aggregation against the
allowlisted same-database collection reapplies the complete mandatory filter,
limits parent count, retains the best child score, and bounds each text and the
aggregate context budget. Chunk and parent writes remain ingestion concerns.

## Index lifecycle and ownership

`VectorIndexManager` in `_shared/indexes.py` is the internal lifecycle mechanic.
`validate_vector_search_index()` is read-only and compares index type, vector
path, dimensions, similarity, required filter paths, status, and queryability.
`ensure_vector_search_index()` is the only create/update facade and optionally
polls with a monotonic deadline. Search and framework hooks never call ensure.

Injected clients and collections remain caller-owned. A URI-created PyMongo
`AsyncMongoClient` is provider-owned and is closed once through `close()` or the
async context manager. PyMongo's asynchronous API is used throughout.

Runtime identities need read/aggregate and Search query permissions only.
Provisioner identities additionally need list/create/update Search-index
permissions. Production connections must use appropriate TLS and network access.

## Errors, cancellation, and privacy

Direct search, validation, and ensure surface stable integration errors while
preserving the PyMongo exception as `__cause__`. Only transient retrieval and
deadline errors fail open in `before_run`; authorization, configuration, filter,
capability, index, mapping, embedding, and cancellation failures propagate.
Cancellation is not caught as an operational failure during embedding,
aggregation, cursor consumption, index requests, or polling.

The adapter's warning contains only low-cardinality feature/operation/outcome
fields. Query text, filters, embeddings, documents, source URLs, connection
details, tenant values, and driver messages are not logged.

## Verification

`python/tests/unit/test_rag_vector.py` covers ANN/ENN pipeline structure,
security-filter placement, index-before-embedding validation, explicit
provisioning, mapping, citation/source attribution, parent authorization,
read-only hooks, redacted fail-open behavior, and cancellation.
`python/tests/integration_rag_vector/test_rag_vector_integration.py` uses a
unique `af_rag_vector_test_` collection, explicitly provisions the index, and
checks cross-tenant exclusion separately for ANN and ENN. It skips with a
capability diagnostic when credentials or a supported deployment are absent.

The runnable `python/samples/rag_vector_quickstart.py` documents its environment
and demonstrates explicit provisioning plus direct search. Full-text and hybrid
pipelines are deliberately absent from this slice.
