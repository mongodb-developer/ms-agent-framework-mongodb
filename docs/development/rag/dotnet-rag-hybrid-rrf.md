# .NET HybridRrf RAG direct search

This document describes the .NET portion of implementation-map
[slice 12](../../spec/implementation-map.md), governed by the
[RAG specification](../../spec/features/rag.md), the
[interface contract](../../spec/interfaces.md), and ADR rationale
[0002](../../decisions/0002-separate-memory-history-rag-and-persistence.md),
[0007](../../decisions/0007-use-typed-filters-and-native-search-pipelines.md), and
[0011](../../decisions/0011-release-features-through-staged-quality-gates.md). It builds directly on the public
contracts and typed filter AST from [slice 6](dotnet-rag.md) and the `SearchAsync`/`MongoDBRAGContextProvider` seams
introduced in [slice 8](dotnet-rag-vector-search.md) and [slice 10](dotnet-rag-full-text-search.md), reusing both
slices' result mapping, cancellation, timeout, and citation formatting entirely unchanged.

This slice adds live `MongoDBSearchMode.HybridRrf` retrieval through the existing
`MongoDBRAGProvider.SearchAsync` seam, using MongoDB's native `$rankFusion` aggregation stage (rag.md 196-260).
Search/Vector Search index **provisioning** remains out of scope (implementation-map slice 13).

## Hybrid rank-fusion pipeline

`SearchCoreAsync` now branches three ways on `_options.SearchMode`: `FullText`, `HybridRrf`, and the vector family
(`VectorAnn`/`VectorEnn`, unchanged). For `HybridRrf`, a new `BuildHybridSearchStagesAsync` method:

1. Embeds the query text via the existing `EmbedAsync` (Hybrid is only reachable through the vector-family
   constructors, which always require an `IEmbeddingGenerator`/`vectorDimensions`, so this is never null on this
   path).
2. Computes `vectorNumCandidates` (the existing `DefaultNumCandidates` heuristic, shared with `VectorAnn`),
   `vectorCandidateLimit` (`_options.VectorCandidateLimit` or the same default), and `textCandidateLimit`
   (`_options.TextCandidateLimit` or the same default) — Hybrid's two independent candidate-set sizes upstream of
   fusion, per rag.md's "input candidate limits SHOULD exceed final topK" guidance.
3. Translates the mandatory filter **independently** for each branch: `RAGFilterTranslator.TranslateVectorFilter`
   for the vector input, `RAGFilterTranslator.TranslateSearchFilter` for the text input — the same translators
   `VectorAnn`/`VectorEnn` and `FullText` already use, simply invoked twice. There is no shared "translate once,
   reuse" shortcut, because the two branches have structurally different filter placement (a single BSON `filter`
   document under `$vectorSearch` vs. an array under `$search.compound.filter`).
4. Calls the new `Internal.RAGPipelineBuilder.BuildHybridRankFusionPipeline`, which builds:

```javascript
[
  {
    $rankFusion: {
      input: {
        pipelines: {
          vector: [ { $vectorSearch: { index, path, queryVector, numCandidates, limit, filter } } ],
          text: [
            { $search: { index, compound: { must: [...], filter: [...] } } },
            { $limit: textCandidateLimit }
          ]
        }
      },
      combination: { weights: { vector: VectorWeight, text: TextWeight } },
      scoreDetails: IncludeScoreDetails // omitted entirely (not rendered as `false`) when unset
    }
  },
  { $limit: topK },
  { $set: { _ragScore: { $meta: "score" } } },
  { $set: { _ragScoreDetails: { $meta: "scoreDetails" } } } // only present when IncludeScoreDetails is true
]
```

Both input pipelines run against the same collection (a `$rankFusion` requirement), and — matching the vector and
FullText pipelines' established rule — the mandatory filter is placed **inside** each input stage (`$vectorSearch`'s
`filter`, `$search.compound.filter`), never applied after fusion; `$rankFusion` itself performs de-duplication
across the two same-collection candidate sets, so no separate application-side de-dup step exists or is needed.
There is intentionally no `$project` stage, matching the vector/FullText pipelines, so the complete original
document survives to `MongoDBRAGProvider.MapResult` unmodified.

`RAGPipelineBuilder` was refactored (no behavior change to the existing vector/FullText builders) to extract shared
private `BuildVectorSearchStage`/`BuildFullTextSearchStage`/`ScoreAliasStage`/`TextPath` helpers, reused by all three
public pipeline-builder methods, so `BuildHybridRankFusionPipeline` composes the same stage-building logic instead of
duplicating it. The `$rankFusion` stage itself is built with the typed
`MongoDB.Driver.PipelineStageDefinitionBuilder.RankFusion<TInput, TOutput>` builder and `RankFusionOptions<TOutput>`
(per the specification's "typed builders for supported stages" rule), passing a `Dictionary<string,
PipelineDefinition<BsonDocument, BsonDocument>>` for the two named input pipelines and a `Dictionary<string, double>`
for `combination.weights`. Empirically, the driver's typed builder **omits** the `scoreDetails` property entirely
when unset/`false` rather than rendering `scoreDetails: false` — `RAGPipelineBuilderTests` assert on `Contains(...)`
rather than indexing the key directly, to match this.

## Fused score and `ScoreDetails`

`$rankFusion`'s fused rank score is captured through the same `{ $meta: "score" }` mechanism the vector
(`vectorSearchScore`) and FullText (`searchScore`) pipelines already use for their native scores — just with
`"score"` as the meta keyword — via the shared `ScoreAliasStage` helper, aliased to the same reserved
`Internal.FieldPath.ReservedScoreAlias` (`_ragScore`) `MapResult` already reads and strips. `MongoDBRAGResult` gained
a new `ScoreDetails` property (nullable `BsonDocument`, immutable — deep-cloned on construction and on every getter
read, matching `RawDocument`'s existing immutability pattern) exposing `$rankFusion`'s optional raw `scoreDetails`
diagnostic metadata (rag.md: "its internal shape is not a compatibility guarantee") when
`MongoDBRAGProviderOptions.IncludeScoreDetails` is `true`. `MapResult` extracts and strips a second reserved alias,
`Internal.FieldPath.ReservedScoreDetailsAlias` (`_ragScoreDetails`), the same way it already handles `_ragScore`, so
neither ever leaks into `RawDocument`.

## Mode-specific options

`MongoDBRAGProviderOptions` (already present before this slice, see prior hardening) exposes Hybrid-only options,
all validated as unused outside `HybridRrf`:

- `VectorCandidateLimit`/`TextCandidateLimit` (`int?`, bounded by `MaxNumCandidates`) — override the default
  candidate-set-size heuristic per input branch.
- `IncludeScoreDetails` (`bool`, default `false`) — opts into the `scoreDetails` diagnostic stage.
- `VectorWeight`/`TextWeight` (`double`, default `1.0` each) — validated finite, non-negative, and at least one
  strictly positive, matching rag.md's weight rules.

`HybridRrf` requires **both** an embedding generator/dimensions (like the vector family) and search
index/field configuration (like `FullText`); `Validate()`'s mode-specific switch enforces both simultaneously for
`HybridRrf` while continuing to reject vector configuration on `FullText`-only options and search configuration on
vector-only options, so none of the three modes can accidentally require the other's configuration.

## Hybrid capability validation

Per rag.md's capability matrix (server gate "MongoDB 8.0+", indexes "Vector Search + Search"), this slice adds a
read-only, mode-gated `ValidateHybridSearchCapabilityAsync(bool requireReady = true, bool refresh = false,
CancellationToken)` seam, mirroring `ValidateSearchIndexAsync`'s ([slice 10](dotnet-rag-full-text-search.md
#search-index-capability-validation-review-fix)) design exactly:

- Checks the connected server's `buildInfo.version` major component against a minimum of `8`, wrapping any
  `MongoException` from the `buildInfo` command (or an unparsable version string) as an actionable
  `MongoDBCapabilityException` rather than letting `$rankFusion` itself fail an actual query with an opaque command
  error.
- Validates the configured `VectorIndexName` (type `vectorSearch`, the configured `VectorFieldName`'s path and
  dimension) via a new `FindVectorSearchIndexAsync`/`ValidateVectorSearchIndexDefinition`, reusing the same
  `SearchIndexes.ListAsync` mechanism `FindSearchIndexAsync` already uses for FullText's Search index (Atlas's
  `$listSearchIndexes` lists both index types through the same collection-level manager, confirmed against
  `MongoDBMemoryProvider`'s equivalent `FindIndexAsync`). Unlike Memory's analogous check, the vector index's
  `similarity` metric is intentionally **not** validated, because `$rankFusion` combines rank order across branches
  rather than comparing raw similarity scores, so a mismatched similarity metric does not break Hybrid correctness
  the way it would a raw-score-based caller.
- Validates the configured `SearchIndexName` by calling the existing `FindSearchIndexAsync`/
  `ValidateSearchIndexDefinition` unchanged (identical Search-index rules as `FullText`, including the
  dynamic-mapping and multi-type-field-mapping handling already documented in
  [slice 10](dotnet-rag-full-text-search.md#search-index-capability-validation-review-fix)).
- Validates every field referenced by `MandatoryFilter` (extracted immutably via the internal
  `RAGFilterFieldReferences.Enumerate`, covering nested AND/OR) against **both** indexes:
  `ValidateVectorFilterFields` requires each referenced field be declared as a Vector Search `type: "filter"` field
  (Vector Search has no dynamic-filter equivalent, so this check always definitively throws or passes), and
  `ValidateSearchFilterFields` requires each referenced field be mapped to an operator-compatible Search type
  (`Range` needs `number`/`date`/`numberFacet`/`dateFacet`; `Equality`/`Membership` accept
  `token`/`string`/`boolean`/`number`/`date`/`objectId`/`uuid`) when the Search mapping is non-dynamic. A dynamic
  Search mapping cannot be statically verified per field, so it is accepted **without** being treated as verified.
- `requireReady` (default `true`) requires both indexes to report queryable/`READY`.
- `SearchAsync` now calls this method itself before every `HybridRrf` aggregation (first call validates; a
  successful, fully-field-verified result is cached for `HybridCapabilityValidationCacheDuration` (30 seconds), so a
  query does not pay the extra round trips on every call). It remains additionally callable directly as an
  opt-in health-check/startup gate, consistent with `ValidateSearchIndexAsync`. A cached lenient
  (`requireReady: false`) result never silently satisfies a later strict call. Critically, a successful validation is
  **not cached** when the Search-index mapping is dynamic and `MandatoryFilter` references at least one field —
  since that combination cannot be statically verified, every call re-validates rather than risk caching an
  unverified authorization filter as "safe".
- Calling this method against a mode other than `HybridRrf` throws `MongoDBCapabilityException` without any network
  call (`RunCommandCallCount`/`SearchIndexListCallCount` both remain `0`).
- `OperationCanceledException` always propagates unchanged, never wrapped.
- If the aggregation itself still fails with a `MongoCommandException` recognizable as "the deployment does not
  support/allow `$rankFusion`" (an unrecognized-pipeline-stage/command-not-supported server error code, or an error
  message naming `rankFusion`), `SearchAsync` wraps it as `MongoDBCapabilityException` instead of the generic
  `MongoDBRetrievalException` every other mode uses — this is a defense-in-depth safety net for deployments where
  the pre-flight `buildInfo` check reports `8.0+` but `$rankFusion` is still disabled/unavailable; it does not
  replace the mandatory pre-aggregation validation above.

Tests live in `MongoDBRAGHybridCapabilityValidationTests`, using a new `RAGDatabaseProxy` test double (faking
`IMongoDatabase.RunCommandAsync<TResult>` for `buildInfo`, added alongside the existing `RAGCollectionProxy`/
`RAGSearchIndexManagerProxy`) and reusing `FakeTimeProvider`. They cover: server version below 8, exactly 8, an
unparsable version string, a `buildInfo` failure wrapped as `MongoDBCapabilityException`, cancellation propagation,
missing vector index, missing search index, wrong vector index type, mismatched vector dimension, vector index
missing the configured field, not-ready vector/search index rejection (and allowance when `requireReady: false`),
success with both valid indexes, mode gating (no network calls for a non-`HybridRrf` configuration), cache
behavior (TTL reuse, `refresh: true` bypass, TTL expiry, and no stale-serving across a `requireReady` escalation),
mandatory-filter field validation against both indexes (missing/wrong-type Vector Search filter field, unmapped/
incompatible Search field, nested AND/OR coverage, and the no-cache-on-unverified-dynamic-mapping behavior).
`MongoDBRAGProviderSearchTests` additionally covers `SearchAsync` invoking validation before aggregating (and never
aggregating when it fails), reusing the cache across calls, and wrapping a recognized `$rankFusion`-unsupported
command error as `MongoDBCapabilityException` while an unrelated command error still becomes
`MongoDBRetrievalException`.

## Vector candidate relationship validation

`MongoDBRAGProviderOptions.Validate()` now additionally rejects `HybridRrf` options whose effective `NumCandidates`
(the configured value, or `DefaultNumCandidates(TopK)` when unset) is less than the effective
`VectorCandidateLimit` (the configured value, or its own default when unset): `$vectorSearch`'s ANN candidate pool
must be at least as large as the number of vector candidates fed into `$rankFusion`, or the rank fusion input would
be silently truncated. `DefaultNumCandidates` moved from a private `MongoDBRAGProvider` method to
`internal static MongoDBRAGProviderOptions.DefaultNumCandidates(int topK)` so both the provider's pipeline builders
and this validation share one definition. Covered by new `MongoDBRAGProviderOptionsTests` cases across explicit/
default/mixed-null combinations.

## `MongoDBRAGContextProvider`

No changes were required: the adapter composes `SearchAsync` opaquely and is entirely mode-agnostic, so citation
formatting, fail-open behavior, query selection, and `AdditionalProperties` preservation from
[slice 8](dotnet-rag-vector-search.md#mongodbragcontextprovider-before-invoke-adapter) apply unchanged to
`HybridRrf`. A dedicated `HybridSearchWorksTransparentlyThroughTheContextAdapter` test proves the rank-fusion stage,
`_rag_id`/`_rag_score` `AdditionalProperties`, and attributed-message formatting all flow through unchanged.

## Errors, cancellation, and result mapping

Unchanged from [slice 8](dotnet-rag-vector-search.md#errors-and-cancellation) /
[slice 10](dotnet-rag-full-text-search.md#errors-cancellation-and-result-mapping): `MongoException` translation to
`MongoDBRetrievalException`, `OperationCanceledException`/`MongoDBMappingException` propagation, timeout
translation, and the read-only guarantee. Result mapping (`MapId`, `MapScore`, `RawDocument` preservation,
metadata/source resolution) is identical across all four modes, since every pipeline routes through the same
`MapResult`.

## Verification

Tests live under `dotnet/tests/MongoDB.AgentFramework.Tests/RAG/` and were written test-first (red before green):

- `RAGPipelineBuilderTests` — `BuildHybridRankFusionPipeline` stage shape (`$rankFusion`/`$limit`/`$set`), both
  input branches' independent filter placement, candidate limits, weights, and the `scoreDetails` opt-in/omission.
- `MongoDBRAGResultTests` — the new `ScoreDetails` property's default (`null`), content preservation, and
  immutability against both source and getter-snapshot mutation.
- `MongoDBRAGProviderSearchTests` — rank-fusion stage/dual-filter structure, weights/candidate limits, fused score
  and complete raw-document preservation, `scoreDetails` opt-in, and the absence of a `$project` stage. The
  `UnsupportedModesAreRejectedBeforeAnyEmbeddingOrNetworkCall` theory was removed: `HybridRrf` was its last
  remaining case, and no unsupported mode remains once this slice lands.
- `MongoDBRAGContextProviderTests` — `HybridSearchWorksTransparentlyThroughTheContextAdapter` (see above). The
  `CapabilityErrorsPropagateRatherThanFailingOpen` test that previously covered `HybridRrf` as an *unsupported* mode
  was removed when this slice first landed; the mandatory-validation review fix (below) reintroduced a directly
  reachable `MongoDBCapabilityException` trigger through `SearchAsync` itself (missing/misconfigured index, or a
  recognized `$rankFusion`-unsupported command error), so context-adapter fail-open behavior for Hybrid capability
  failures is now covered again via `MongoDBRAGProviderSearchTests`' capability-validation tests.
- `MongoDBRAGContractTests` — a new
  `MandatoryFilterIsCompletelyAndIndependentlyTranslatedIntoBothHybridInputBranches` test asserting a multi-branch
  AND/OR/IN/range `MandatoryFilter` translates completely and independently into both `$vectorSearch.filter` and
  `$search.compound.filter` within the same Hybrid pipeline.
- `MongoDBRAGHybridCapabilityValidationTests` — see
  [Hybrid capability validation](#hybrid-capability-validation) above for full coverage.
- `MongoDBRAGIntegrationTests` — a new credential-gated `integration-rag-hybrid` test,
  `HybridRrfSearchIsolatesTenantsOnPreProvisionedIndexes`. It targets the same fixed, operator-provisioned Vector
  Search and Search indexes the existing Vector/FullText integration tests use (`MONGODB_RAG_VECTOR_INDEX`/
  `MONGODB_RAG_SEARCH_INDEX`, defaulting to `agent_framework_rag_vector`/`agent_framework_rag_search`), rather than
  creating its own indexes, and only ever inserts/deletes documents whose IDs carry a unique, test-owned prefix. It
  independently proves both tenant-A and tenant-B documents are searchable through **each** of Hybrid's two input
  branches (a no-filter `VectorAnn` readiness provider and a no-filter `FullText` readiness provider) before
  asserting the tenant-A-scoped Hybrid provider excludes tenant B — otherwise that exclusion assertion could pass
  vacuously merely because tenant B was never searchable via one or both branches, matching the FullText
  integration test's established readiness-proof pattern.

Run:

```powershell
dotnet test dotnet\MongoDB.AgentFramework.slnx --filter "FullyQualifiedName~RAG"
dotnet test dotnet\MongoDB.AgentFramework.slnx
```

The sample at `dotnet/samples/RAGQuickstart/` now includes a HybridRrf demonstration section, gated on the same
optional `MONGODB_RAG_SEARCH_INDEX` environment variable as the FullText section (both require `MONGODB_RAG_SEARCH_INDEX`;
HybridRrf additionally always has a `MONGODB_RAG_VECTOR_INDEX`, which defaults). It reuses the FullText section's
`PollUntilSearchableAsync` helper so its output is deterministic despite Atlas Search's asynchronous indexing.

## Deferred to later slices

- Search/Vector Search index provisioning for RAG (slice 13).
- On-demand retrieval tool exposure and structured `MetadataQueryPlan` retrieval.
- A validated BSON fallback path for deployments/drivers that support Vector Search and Search individually but not
  the `$rankFusion` stage itself (rag.md's capability matrix lists this as an alternative driver gate; this slice
  implements the `$rankFusion`-only path and surfaces an actionable `MongoDBCapabilityException` rather than
  emulating fusion in application code, per the "no silently downgrade/emulate unsupported capabilities" rule).
