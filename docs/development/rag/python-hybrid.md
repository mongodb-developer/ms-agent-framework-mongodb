# Python native hybrid reciprocal-rank fusion

This document describes implementation-map
[slice 11](../../spec/implementation-map.md), Python Hybrid RAG. The normative
requirements are the [RAG](../../spec/features/rag.md),
[index](../../spec/features/index-management.md),
[resilience](../../spec/resilience.md),
[security](../../spec/observability-security.md), and
[testing](../../spec/testing.md) specifications. ADRs
[0007](../../decisions/0007-use-typed-filters-and-native-search-pipelines.md),
[0010](../../decisions/0010-fail-open-only-at-agent-adapter-boundaries.md), and
[0011](../../decisions/0011-release-features-through-staged-quality-gates.md)
record the rationale without overriding those specifications.

## Public seams and responsibilities

`MongoDBRAGProvider.search()` is the deterministic direct seam.
`MongoDBRAGContextProvider` delegates to it for Agent Framework context
injection. The implementation is in
`python/src/agent_framework_mongodb/rag/provider.py`; immutable configuration is
in `rag/options.py`, and complete typed-filter translation is in
`rag/_filters.py`.

Set `MongoDBRAGProviderOptions.mode` to
`MongoDBSearchMode.HYBRID_RRF`. Hybrid requires a caller-owned embedding
generator, vector dimensions, independently named Vector Search and Search
indexes, and an existing knowledge collection. Defaults are `top_k=5`,
`num_candidates=50`, and vector/text weights `1.0`. Limits are bounded to 100
results and 10,000 candidates. Candidates must be at least `top_k`; weights are
finite and non-negative with at least one positive value. Per-call options may
lower or raise the bounded limits and add a typed relevance filter, but cannot
replace the immutable provider authorization filter.

## Validation and execution flow

Each search follows this order:

1. validate the query and normalize bounded per-call options
2. completely translate the effective typed filter for both native branches
3. read and validate both named indexes, including every effective filter path
4. capability-probe native `$rankFusion` with public `buildInfo`, `hello`, and
   `explain` commands
5. generate and dimension-check one query embedding
6. execute one read-only `aggregate` and consume its cursor
7. map the fused score, configured fields, metadata, sources, and original raw
   document to `MongoDBRAGResult`

No constructor, hook, capability validation, or search creates or updates an
index. `ensure_vector_search_index()` and `ensure_search_index()` remain
explicit provisioner operations.

The capability gate requires MongoDB 8.0 or later and proves that the deployment
accepts the actual native stage rather than inferring support from version
alone. A confirmed pre-8 version or recognized unsupported `$rankFusion`
response is cached for the bounded `capability_cache_ttl`. Supported probes,
authorization failures, cancellation, and inconclusive operational failures are
not cached. There is no application-memory fusion, `$scoreFusion` substitution,
or fallback to one input mode.

## Native pipeline and authorization boundary

The first and only retrieval stage is native `$rankFusion`. Its `vector` input
starts with `$vectorSearch`; its `text` input starts with `$search` and ends in
the candidate `$limit`. Input documents are not modified. The complete
provider authorization filter conjoined with any per-call typed filter appears
independently in:

- `$vectorSearch.filter`
- `$search.compound.filter`

Both placements precede candidate and result limiting. Partial translation and
post-retrieval authorization are rejected. The vector input uses ANN:
`numCandidates` controls the ANN pool and each branch returns at most the
effective candidate count.

`combination.weights` supplies the documented vector and text weights.
`scoreDetails` is opt-in. After fusion, `_ragScore` captures `{ $meta: "score" }`
and optional `_ragScoreDetails` captures diagnostic metadata in the raw result.
The provider never labels the fused score a probability or compares raw vector
and Search scores.

Native `$rankFusion` de-duplicates identical collection documents. A
post-fusion score sort and group additionally de-duplicates by the configured
`id_field`, preserving the highest-ranked original document and fused score,
then applies the final `top_k` limit. All document modification occurs after
fusion.

## Parent hydration and errors

`MongoDBRAGParentOptions` is legal in hybrid mode. Hydration runs only after
fusion, is bounded by parent count, lookup fan-out, text length, and context
tokens, and de-duplicates parents by configured identity. Its second read
reapplies only the immutable provider authorization filter; a per-call relevance
filter is not incorrectly imposed on parent documents. Same-database collection
selection and typed field paths prevent arbitrary enrichment.

Direct search surfaces configuration, filter, capability, index, embedding,
mapping, timeout, authorization, and retrieval errors with driver exceptions as
causes. Cancellation propagates through index reads, capability commands,
embedding, aggregate execution, cursor consumption, and parent hydration.
Only transient retrieval and deadline errors fail open at
`MongoDBRAGContextProvider.before_run`; capability, security, configuration,
index, mapping, and cancellation errors propagate. Logs contain stable operation
fields, not queries, filters, embeddings, documents, hosts, or credentials.

Runtime hybrid paths call only index inspection, public capability commands, and
`aggregate`. `$out`, `$merge`, inserts, updates, replacements, upserts, and
deletes are absent.

## Operations and sample

Runtime identities need read/aggregate, Search query, and named-index inspection
permissions. Keep create/update/drop Search-index privileges on a separate
provisioner identity. MongoDB 8.0 deployments may require native `$rankFusion`
enablement through MongoDB support; the capability error includes remediation.

`python/samples/rag_hybrid_quickstart.py` shows explicit provisioning followed by
direct search. It requires `MONGODB_URI`, `MONGODB_DATABASE`,
`MONGODB_RAG_COLLECTION`, `MONGODB_RAG_VECTOR_INDEX`,
`MONGODB_RAG_SEARCH_INDEX`, and `MONGODB_RAG_TENANT`. The collection must
already contain `content`, three-dimensional `embedding`, and `tenant_id`
fields. Production applications must replace the demonstration generator with
the same model and dimensions used at ingestion. The sample does not ingest or
delete data.

## Verification

`python/tests/unit/test_rag_hybrid.py` covers both public seams, stage legality,
dual filter placement, options, configured-identity de-duplication, score/raw
preservation, capability caching, pre-embedding index validation, read-only
behavior, parent authorization, adapter policy, and cancellation.
`python/tests/integration_rag_hybrid/test_rag_hybrid_integration.py` uses a
unique `af_rag_hybrid_test_` collection, explicitly provisions both indexes,
checks cross-tenant exclusion, native de-duplication, positive fused scores, and
non-tied weight-sensitive ordering, and drops only that prefixed collection in
`finally`. It skips when credentials or the required deployment capability are
absent.

The package quality gate is run from `python/`:

```text
python -m pytest -q
python -m ruff format --check src tests samples
python -m ruff check src tests samples
python -m mypy
pyright
python -m build --outdir .artifact-dist-rag-hybrid
python -m twine check .artifact-dist-rag-hybrid\*
```
