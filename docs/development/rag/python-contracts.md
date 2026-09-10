# Python RAG contracts and typed filters

This document describes implementation-map [slice 6](../../spec/implementation-map.md)
for the Python package. The normative requirements are the complete
[RAG specification](../../spec/features/rag.md), [interfaces](../../spec/interfaces.md),
and [security](../../spec/observability-security.md). ADRs
[0002](../../decisions/0002-separate-memory-history-rag-and-persistence.md),
[0007](../../decisions/0007-use-typed-filters-and-native-search-pipelines.md),
[0009](../../decisions/0009-enforce-behavioral-not-physical-parity.md), and
[0010](../../decisions/0010-fail-open-only-at-agent-adapter-boundaries.md) record
the rationale. They do not override the specifications while proposed.

## Scope and dependencies

`python/src/agent_framework_mongodb/rag/` is an independent feature module. It
depends inward on shared field-path and dimension validation and on the package
error taxonomy; Memory and Chat History do not depend on it, and it does not
call either feature.

This slice establishes contracts only. It intentionally does not contact
MongoDB, generate embeddings, build complete retrieval pipelines, provision
indexes, or install an on-demand tool. `MongoDBRAGProvider.search()` validates
an empty query and otherwise raises `MongoDBCapabilityError` with the missing
mode implementation. Later mode slices replace that boundary with direct,
read-only execution. `MongoDBRAGContextProvider` establishes the public
`ContextProvider` integration seam without claiming context injection behavior
before those slices exist.

## Public surface

All public symbols below are re-exported by `agent_framework_mongodb`:

- `MongoDBSearchMode`: `vector_ann`, `vector_enn`, `full_text`, and
  `hybrid_rrf`.
- `MongoDBFilter` and the leaf filters `EqualFilter`, `NotEqualFilter`,
  `InFilter`, `NotInFilter`, `GreaterThanFilter`,
  `GreaterThanOrEqualFilter`, `LessThanFilter`, and
  `LessThanOrEqualFilter`.
- `AndFilter` and `OrFilter` for bounded composition.
- `MongoDBRAGProviderOptions`, `MongoDBRAGSearchOptions`, and
  `MongoDBRAGParentOptions`.
- `MongoDBRAGResult`, `MongoDBRAGProvider`, and
  `MongoDBRAGContextProvider`.

Constructors normalize explicit list/tuple inputs to tuples and mode strings to
`MongoDBSearchMode`. Scalar strings/bytes and arbitrary iterables are never
treated as sequences. Configuration errors are raised before any future database
access. Raw mappings/BSON are rejected for filter inputs with a `TypeError`;
unsafe paths, values, limits, and incompatible mode options raise
`MongoDBConfigurationError`. Complete-translation failures use
`MongoDBFilterTranslationError`.

## Filter invariants and translation

Filter field paths use the shared
`agent_framework_mongodb._shared.field_paths.validate_field_path` rules:
non-empty dot-delimited segments, no null bytes, `$` segments, positional
segments, empty segments, or `_ragScore` collisions. Equality and membership
accept BSON scalar values only: strings, finite numbers, booleans, timezone-aware
datetimes, and null. Every Python integer must fit BSON int64
(`-2**63` through `2**63 - 1`). Ranges accept finite non-boolean numbers and
timezone-aware datetimes. Membership requires an explicit list or tuple
containing 1-100 values; it rejects scalar strings/bytes rather than splitting
them. Boolean nodes contain 2-20 children, and expression depth is at most eight.

Internal translators in `rag/_filters.py` produce structured values only:

| Mode | Mandatory-filter destination | Translation |
| --- | --- | --- |
| Vector ANN/ENN | `$vectorSearch.filter` | `$eq`, `$ne`, `$in`, `$nin`, range, `$and`, `$or` |
| Full text | `$search.compound.filter` | `equals`, `in`, `range`, and bounded `compound` clauses |
| Hybrid RRF | Both native input stages | Independently produces complete vector and Search forms |

There is no partial translation or application-side filtering. The translators
are internal so BSON structure cannot become a model tool argument. A future
tool may expose query text only; provider options and the mandatory filter
remain application-owned.

## Option normalization

`MongoDBRAGProviderOptions` is immutable. Defaults are `top_k=5`,
`num_candidates=50` for ANN and hybrid, and fusion weights of `1.0`.
`top_k` is bounded to 100 and candidates to 10,000, with candidates at least
`top_k`. ANN requires dimensions and a vector index. ENN requires the same and
forbids candidates. Full text requires a Search index and forbids vector-only
options. Hybrid requires both indexes, uses ANN candidates, requires finite
non-negative weights, and requires at least one positive weight. Names and all
configured result paths are validated at construction.

`text_fields` and `metadata_fields` accept explicit lists or tuples only.
`text_fields` must be non-empty; `metadata_fields` may be empty. Both validate
each complete field path and remove duplicates while preserving first-seen
order. A string, bytes value, generator, or other iterable is rejected so it
cannot be normalized character by character.

`normalize_search_options()` applies per-call bounds and combines an optional
typed relevance filter with the immutable mandatory filter by conjunction; it
never replaces the mandatory filter. Detailed score diagnostics are opt-in and
their eventual MongoDB shape is not part of this contract.

`MongoDBRAGParentOptions` is the only enrichment-related contract in this
slice. It permits a validated same-database collection name or the current
collection and validates parent ID/text fields, parent count, text length,
lookup fan-out, and context-token bounds. Arbitrary enrichment pipelines,
callbacks, cross-database lookup, and write stages are not public inputs.
Parent retrieval is accepted only for vector-capable modes and remains an
execution responsibility of a later mode slice.

## Results, citations, and privacy

`MongoDBRAGResult` preserves the caller-visible ID, required non-empty text,
finite native score, optional source name and URL, immutable normalized
metadata, and the original raw document object. Scores are not normalized or
described as probabilities. `to_citation()` returns the public Agent Framework
`Annotation` citation shape with source title, URL, snippet, document ID, score,
metadata, and the complete result in `raw_representation`.

Raw documents remain an application result and are never accepted as
model-controlled input. This contract layer emits no logs or telemetry. Future
execution must preserve cancellation, redact query/filter/document data, use
only `aggregate`, and fail open only at the context-adapter boundary.

## Cross-language verification

`tests/fixtures/rag/contracts.json` is language-neutral and covers option
normalization and rejection, complete filter translations, normalized result
and citation semantics, authorization placement, cancellation, and read-only
operation expectations. Python consumes it in
`python/tests/contracts/test_rag_contract.py`. Focused behavior is also covered
by `test_rag_filters.py` and `test_rag_contracts.py`. No server integration test
exists in this slice because it implements no search execution mode.

The implementation was verified from `python/` with:

```text
python -m pytest -q
python -m ruff format --check src tests
python -m ruff check src tests
python -m mypy
pyright
python -m build --outdir .artifact-dist-rag
python -m twine check .artifact-dist-rag\*
```

The exact built wheel and sdist were each installed into a new virtual
environment and smoke-imported independently. Those scratch environments and
artifacts are not repository content.
