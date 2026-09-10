# .NET RAG contracts and typed filters

This document describes the .NET portion of implementation-map
[slice 6](../../spec/implementation-map.md), governed by the
[RAG specification](../../spec/features/rag.md), the
[interface contract](../../spec/interfaces.md), the
[observability and security specification](../../spec/observability-security.md), and ADR rationale
[0002](../../decisions/0002-separate-memory-history-rag-and-persistence.md),
[0007](../../decisions/0007-use-typed-filters-and-native-search-pipelines.md),
[0009](../../decisions/0009-enforce-behavioral-not-physical-parity.md), and
[0010](../../decisions/0010-fail-open-only-at-agent-adapter-boundaries.md). The ADRs remain proposed and do not
override the specification.

This slice adds the public RAG contracts and the bounded typed filter AST with complete translation to native
MongoDB pipeline fragments. It intentionally does **not** implement live vector, full-text, or hybrid retrieval, a
`MongoDBRAGProvider`/`MongoDBRAGContextProvider` runtime type, index provisioning, or a `TextSearchProvider`
adapter. Those remain later implementation-map slices (7, 8, 10, 12) and are tracked there rather than spread across
this contracts-only slice.

## Public surface

All types live under `dotnet/src/MongoDB.AgentFramework/RAG/`:

- `MongoDBSearchMode` — the four required retrieval capabilities: `VectorAnn`, `VectorEnn`, `FullText`, and
  `HybridRrf`.
- `MongoDBRAGFilter` — a bounded, closed-hierarchy typed filter AST. Instances are created only through static
  factories (`Equal`, `NotEqual`, `In`, `NotIn`, `Range` for `double?` and `DateTimeOffset?` bounds, `And`, `Or`);
  there is no public constructor and no way for a caller to introduce an unrecognized node type. Every factory
  validates eagerly: field paths reuse `Internal.FieldPath.Validate` (rejecting empty, `$`-prefixed, positional,
  null-byte, and `_ragScore`-colliding segments), values are restricted to `string`, `bool`, `int`, `long`,
  `double`, `decimal`, `DateTime`, `DateTimeOffset`, and `ObjectId`, membership lists must contain between 1 and
  `MaxMembershipValues` (200) entries, range filters require at least one bound, and AND/OR require between 2 and
  `MaxLogicalOperands` (50) operands with nesting capped at `MaxNestingDepth` (6). Because validation happens at
  construction, a `MongoDBRAGFilter` instance is always completely translatable.
- `MongoDBRAGResult` — an immutable, normalized result (`Id`, `Text`, `Score`, `SourceName`, `SourceUrl`,
  `Metadata`, `RawDocument`). The constructor deep-clones the supplied `BsonDocument` and defensively copies
  metadata into a read-only dictionary, so neither later mutation of the caller's document/dictionary nor an
  attempt to mutate the exposed collections can change a constructed result. `SourceName`/`SourceUrl` carry the
  source attribution used to build framework citations; a dedicated `TextSearchProvider`/citation adapter is
  deferred to the .NET vector/full-text/hybrid slices, which will place the complete `MongoDBRAGResult` in
  `TextSearchResult.RawRepresentation` per the RAG specification.
- `MongoDBRAGProviderOptions` — mode-specific defaults and validation for the search-mode option contract
  (index names, field mappings, `TopK`, `NumCandidates`, hybrid fusion weights, and the caller-configured
  `MandatoryFilter`). `NumCandidates` must be unset for `VectorEnn` (exact search) and `FullText`, and when set for
  `VectorAnn`/`HybridRrf` it must be within `[1, MaxNumCandidates]` and at least `TopK`. `HybridRrf` requires at
  least one of `VectorWeight`/`TextWeight` to be greater than zero; both weights must always be finite and
  non-negative. `Copy()` validates and returns an independent snapshot with its own defensively copied lists, so a
  caller cannot mutate an options instance (or a list it passed in) after handing it to a future provider.

## Filter translation

`Internal.RAGFilterTranslator` (internal, exercised through `InternalsVisibleTo` from the test project — there is
no way to unit test it without touching MongoDB except through this internal seam) provides the two translators
required by the specification:

- `TranslateVectorFilter(MongoDBRAGFilter?)` returns a `$vectorSearch.filter` match `BsonDocument` built with plain
  MongoDB query operators (`$eq`, `$ne`, `$in`, `$nin`, `$gte`/`$gt`/`$lte`/`$lt`, `$and`, `$or`), or `null` when
  there is no effective filter (the property is then omitted from the stage, per the specification).
- `TranslateSearchFilter(MongoDBRAGFilter?)` returns a `$search` compound `filter` `BsonArray` built with the
  MongoDB Search `equals`, `in`, and `range` operators, negation expressed as a nested
  `{ compound: { mustNot: [...] } }` clause, and disjunction expressed as
  `{ compound: { should: [...], minimumShouldMatch: 1 } }`. A top-level AND flattens directly into multiple
  array entries because `compound.filter` already ANDs its entries, avoiding an unnecessary nested wrapper for the
  common single-level mandatory-filter case.

Both translators are structural, recursive, and total over the closed `MongoDBRAGFilter` hierarchy: every node type
has a translation into both branches, so the translators either return a complete translation or — for a
hypothetical future filter node without a registered case — throw `MongoDBRetrievalException` with an actionable
message. Partial translation (dropping a branch of an AND/OR, or silently ignoring an unsupported node) is not
possible by construction. Pipeline stage assembly (deciding which mode uses which branch, embedding the query,
attaching `numCandidates`/`exact`, projecting scores) is left to the retrieval slices that consume these
translators.

## Verification

Tests live under `dotnet/tests/MongoDB.AgentFramework.Tests/RAG/` and were written test-first (red before green) for
each public seam:

- `MongoDBSearchModeTests` — the four required capabilities are declared.
- `MongoDBRAGFilterTests` — field-path validation reuse, value-type restrictions, bounded membership counts, bounded
  operand counts, bounded nesting depth, and that filters within bounds are constructible.
- `MongoDBRAGFilterTranslatorTests` — exact BSON shape for every node type in both the Vector Search and Search
  branches, `null` omission, and that a multi-branch AND mandatory filter is translated completely (no dropped
  branch) into both outputs.
- `MongoDBRAGResultTests` — immutability of the raw document and metadata against later external mutation, and
  source-attribution round-tripping.
- `MongoDBRAGProviderOptionsTests` — mode-specific defaults, the `NumCandidates`/`exact` exclusivity rules, `TopK`
  and candidate bounds, hybrid weight validation, field-path validation, and `Copy()` snapshot independence.

Run:

```powershell
dotnet test dotnet\MongoDB.AgentFramework.slnx --filter "FullyQualifiedName~RAG"
dotnet test dotnet\MongoDB.AgentFramework.slnx
```

## Deferred to later slices

- `MongoDBRAGProvider` / `MongoDBRAGContextProvider` live `VectorAnn`/`VectorEnn` direct search and before-invoke
  integration is now implemented — see [.NET Vector RAG](dotnet-rag-vector-search.md) (slice 8).
- Live `$search` and `$rankFusion` pipeline execution, capability detection, and index provisioning remain deferred
  (slices 10, 12, 13).
- The `TextSearchProvider` composition/citation adapter (blocked on package availability, see
  [.NET Vector RAG](dotnet-rag-vector-search.md#textsearchprovider-compatibility-blocker)) and `MetadataQueryPlan`
  structured-metadata sample.
- Cross-language contract fixtures — no Python RAG implementation exists yet, so there is nothing to compare
  against; `python/tests/contracts/` currently only covers Memory scope and Chat History.
