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
per-call options without replacing the application filter, completely translates
the effective filter, validates all effective filter paths against the named
index before embedding, requests exactly one query embedding,
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
allowlisted same-database collection reads all those IDs and reapplies the
complete mandatory filter. Mapping retains each parent's best child score,
sorts by score and original child relevance order, then limits parent count and
bounds text/context. Unordered `$in` results therefore cannot discard a more
relevant parent. Chunk and parent writes remain ingestion concerns.

## Index lifecycle and ownership

`VectorIndexManager` in `_shared/indexes.py` is the internal lifecycle mechanic.
`validate_vector_search_index()` is read-only and compares index type, vector
path, dimensions, similarity, required filter paths, status, and queryability.
`ensure_vector_search_index()` is the only create/update facade and optionally
polls with a monotonic deadline. Search and framework hooks never call ensure.
Missing, building/non-queryable, ready, and failed states are distinct. A
`FAILED` index raises `MongoDBIndexFailedError` immediately with explicit
repair/recreate remediation; readiness polling does not wait to timeout on a
permanent failure.

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

## ENN capability gate

ENN is gated before query embedding and retrieval. The provider records
diagnostic facts from the public `buildInfo` and `hello` commands and the
installed PyMongo version. It then asks MongoDB to explain a controlled,
read-only `$vectorSearch` pipeline containing `exact: true` against the already
validated index. Successful planning is the support signal. A public-command
parse, invalid-option, or unsupported-stage response raises
`MongoDBCapabilityError` with remediation to use ANN or enable exact search.
Authentication/authorization errors and task cancellation propagate unchanged.

No server-version threshold is hard-coded: server and deployment strings are
diagnostic facts, not inferred support claims. Results, including unsupported
results and their driver cause, are cached for 300 seconds by default.
`capability_cache_ttl` changes the bound, and
`validate_capabilities(refresh=True)` explicitly refreshes it. The explain probe
uses a generated finite vector of the configured dimensions; it does not invoke
the embedding generator, retrieve documents, or include query/filter values.

## Effective-filter validation

Provider and per-call filters are conjoined first. Complete translation and all
required index filter-path checks occur before embedding. An unsupported AST or
an effective path absent from the inspected Vector Search index fails closed.

## Verification

`python/tests/unit/test_rag_vector.py` covers ANN/ENN pipeline structure,
security-filter placement, effective-filter index validation, ENN public-command
capability caching, explicit provisioning, index state transitions, mapping,
citation/source attribution, deterministic parent authorization/ordering,
read-only hooks, redacted fail-open behavior, and cancellation.
`python/tests/integration_rag_vector/test_rag_vector_integration.py` uses a
unique `af_rag_vector_test_` collection, explicitly provisions the index, and
checks cross-tenant exclusion separately for ANN and ENN. It skips with a
capability diagnostic when credentials or a supported deployment are absent.

The runnable `python/samples/rag_vector_quickstart.py` documents its environment
and demonstrates explicit provisioning plus direct search. Full-text and hybrid
pipelines are deliberately absent from this slice.
