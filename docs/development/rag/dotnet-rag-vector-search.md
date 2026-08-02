# .NET Vector RAG (ANN/ENN) direct search and context adapter

This document describes the .NET portion of implementation-map
[slice 8](../../spec/implementation-map.md), governed by the
[RAG specification](../../spec/features/rag.md), the
[interface contract](../../spec/interfaces.md), and ADR rationale
[0002](../../decisions/0002-separate-memory-history-rag-and-persistence.md),
[0007](../../decisions/0007-use-typed-filters-and-native-search-pipelines.md), and
[0011](../../decisions/0011-release-features-through-staged-quality-gates.md). It builds directly on the public
contracts and typed filter AST from [slice 6](dotnet-rag.md). The ADRs remain proposed and do not override the
specification.

This slice adds live `VectorAnn`/`VectorEnn` retrieval through `MongoDBRAGProvider.SearchAsync` and a before-invoke
`MongoDBRAGContextProvider` adapter. It intentionally does **not** implement `FullText` or `HybridRrf` modes, Vector
Search index provisioning, on-demand retrieval tools, or a `TextSearchProvider` composition adapter. Those remain
later implementation-map slices (10, 12, 13).

## Public surface

- `MongoDBRAGProvider` (`dotnet/src/MongoDB.AgentFramework/RAG/MongoDBRAGProvider.cs`) — a sealed,
  `IAsyncDisposable` provider with four constructor overloads mirroring `MongoDBMemoryProvider` exactly: injected
  `IMongoDatabase`, injected `IMongoCollection<BsonDocument>`, injected `IMongoClient`, and a connection-string
  constructor. Injected clients/databases/collections/embedding generators remain caller-owned; only a client
  created by the connection-string constructor is disposed by `DisposeAsync` (`OwnsClient` reports this). Unlike
  Memory, `MongoDBRAGProviderOptions` is a required constructor parameter — RAG has no scope/state concept, and
  `SearchMode` has no sensible default.
- `SearchAsync(string query, CancellationToken cancellationToken = default)` — the sole direct retrieval seam. It
  embeds `query` with the caller-provided `IEmbeddingGenerator<string, Embedding<float>>`, builds and executes a
  `$vectorSearch`-first aggregation pipeline, and returns an immutable `IReadOnlyList<MongoDBRAGResult>`.
- `MongoDBRAGContextProvider` (`dotnet/src/MongoDB.AgentFramework/RAG/MongoDBRAGContextProvider.cs`) — a before-invoke
  `AIContextProvider` that composes a `MongoDBRAGProvider` (composition, not inheritance; the adapter never owns or
  disposes the composed provider) and maps results into attributed `ChatRole.Tool` messages.
- `MongoDBRAGContextProviderOptions` (`Instructions`, `MaxRecentMessages`) — immutable-copy options for the adapter,
  following the same `Validate()`/internal `Copy()` pattern as every other options type in this package.

## `TextSearchProvider` compatibility blocker

Per `docs/spec/features/rag.md`, `MongoDBRAGContextProvider` should compose the framework's `TextSearchProvider` seam
when it is available and proven compatible. The `Microsoft.Agents.AI.Abstractions` version this project's dependency
range actually resolves to is **1.13.0** (confirmed via `project.assets.json`, not a newer type catalog that may be
documented elsewhere), and this version does not expose a `TextSearchProvider` type at all. Per the specification's
documented fallback ("a dedicated adapter must preserve the same information through its own result/context path"),
`MongoDBRAGContextProvider` is built directly on the public `AIContextProvider` seam instead, with the blocker
recorded in the class's XML `<remarks>`. Revisit this composition once a resolved package version exposes
`TextSearchProvider`.

## ANN/ENN pipeline

`Internal.RAGPipelineBuilder` (internal, exercised through `InternalsVisibleTo`) builds the shared pipeline for both
vector modes, using the typed `MongoDB.Driver` `PipelineStageDefinitionBuilder.VectorSearch<BsonDocument>` builder
for the `$vectorSearch` stage itself (per the specification's "typed builders for supported stages" rule), and plain
BSON for the two trailing stages the driver has no dedicated builder for:

1. `$vectorSearch` — `index` (`VectorIndexName`), `path` (`VectorFieldName`), `queryVector` (the embedded query),
   `limit` (`TopK`), `filter` (the translated `MandatoryFilter`, entirely omitted when there is no effective filter),
   and either `numCandidates` (ANN) or `exact: true` (ENN, with `numCandidates` always omitted — the two are mutually
   exclusive by construction; `RAGPipelineBuilder.BuildVectorSearchPipeline` throws
   `MongoDBConfigurationException` if both are supplied). The mandatory filter is placed **inside** this stage, not
   applied afterward, so authorization/tenancy narrows the candidate set MongoDB itself searches.
2. `$set` — captures MongoDB's native `{ $meta: "vectorSearchScore" }` under the reserved
   `Internal.FieldPath.ReservedScoreAlias` (`_ragScore`) alias.

The pipeline intentionally does **not** include a trailing `$project` stage: an earlier revision narrowed the result
to the configured field mappings there, which silently discarded any original document field the mapping
configuration did not name, breaking the guarantee that `MongoDBRAGResult.RawDocument` preserves the complete
original document. `MongoDBRAGProvider.MapResult` instead reads and validates the reserved score alias directly from
the unmodified cursor document, then removes that one internal key from a copy before constructing the public
`MongoDBRAGResult` (whose constructor deep-clones its input), so every other original field survives and the
internal alias never leaks into `RawDocument`.

### ANN candidate default

When `NumCandidates` is not explicitly configured for `VectorAnn`, `MongoDBRAGProvider` computes
`Math.Min(MaxNumCandidates, Math.Max(TopK * 10, 100))` — a conventional ANN heuristic (oversample by 10x, with a
100-candidate floor so small `TopK` values still get a reasonable candidate pool) bounded by the same
`MongoDBRAGProviderOptions.MaxNumCandidates` ceiling used for explicit configuration.

## Result mapping

`MongoDBRAGProvider.MapResult` resolves each configured field path against the (unnarrowed) document with
`Internal.FieldPath`:

- **Id** — resolved with the throwing `FieldPath.Resolve` (a missing ID is a mapping defect, not an optional field)
  and converted from its BSON type (`String`, `ObjectId`, `Int32`, `Int64`, `Double`) to a `string`; any other BSON
  type throws `MongoDBMappingException`.
- **Text** — resolved with `FieldPath.Resolve`; a non-string value throws `MongoDBMappingException`.
- **Score** — read from the reserved `Internal.FieldPath.ReservedScoreAlias` (`_ragScore`) field and validated by
  `MapScore`: a missing field, a non-numeric BSON type, or a non-finite (`NaN`/`Infinity`) numeric value all throw
  `MongoDBMappingException` rather than silently defaulting to `0.0` — a fabricated score would corrupt result
  ranking for callers without any visible signal. The alias is then removed from a copy of the document before that
  document becomes `RawDocument`, so the internal alias never leaks into the public result.
- **SourceName/SourceUrl** — resolved with the non-throwing `FieldPath.TryResolve`; a missing path or a non-string
  value both produce `null` rather than throwing, since these are optional per the specification.
- **Metadata** — each configured `MetadataFieldNames` entry resolved with `FieldPath.TryResolve`; absent entries are
  skipped rather than included as `null`.
- **RawDocument** — the complete original document (minus the reserved score alias, stripped as described above) is
  passed to the `MongoDBRAGResult` constructor, which deep-clones it (see [slice 6](dotnet-rag.md)).

## Errors and cancellation

- `RequireVectorMode()` is checked **before** any embedding call or network round-trip, so `FullText`/`HybridRrf`
  configurations fail fast with `MongoDBCapabilityException` rather than partially executing.
- Embedding failures and invalid vectors (dimension mismatch, non-finite values) surface as
  `MongoDBEmbeddingException` through the shared `Internal.EmbeddingValidator`, reused unchanged from Memory.
  `MongoDBEmbeddingException` inherits `MongoDBRetrievalException`, which the fail-open catch list treats uniformly.
- `MongoException` thrown by `AggregateAsync` is translated to `MongoDBRetrievalException`, preserving the driver
  exception as `InnerException`.
- `OperationCanceledException` and `MongoDBMappingException` always propagate unchanged — cancellation and mapping
  defects are never fail-open conditions.
- `SearchAsync` wraps `SearchCoreAsync` in the same `WithDeadlineAsync` helper Memory uses: when
  `MongoDBRAGProviderOptions.RetrievalTimeout` is configured, a linked, timeout-bounded token drives the operation
  and an internally-triggered cancellation (one the caller's own token did not request) is translated to
  `MongoDBTimeoutException`.
- No write operation of any kind is issued by `SearchAsync` or the pipeline it builds — retrieval is entirely
  read-only, verified directly in `MongoDBRAGProviderSearchTests`.

## `MongoDBRAGContextProvider` before-invoke adapter

`ProvideAIContextAsync` builds the search query by joining the non-empty `Text` of `context.AIContext.Messages`
(optionally limited to the most recent `MaxRecentMessages` via `.TakeLast`), calls `SearchAsync`, and maps each
`MongoDBRAGResult` into a `ChatRole.Tool`-tagged `ChatMessage` (**not** `ChatRole.System`/`ChatRole.User` — retrieved
chunks are data, never instructions) carrying `_rag_id`, `_rag_score`, `_rag_source_name`, and `_rag_source_url` in
`AdditionalProperties`. `Instructions` is a fixed, provider-configured framing sentence that never contains chunk
content, so a prompt-injection attempt embedded in a chunk cannot alter the framing instructions themselves — only
the base `AIContextProvider` class decides how the returned `AIContext` is merged with the agent's other context.

Fail-open behavior mirrors ADR 0010/Memory exactly: only `MongoDBRetrievalException`, `MongoDBEmbeddingException`,
and `MongoDBTimeoutException` are caught (logged as a warning, then an empty `AIContext` is returned).
`MongoDBCapabilityException`, `MongoDBConfigurationException`, and `OperationCanceledException` always propagate.
An empty/whitespace-only query short-circuits before calling `SearchAsync` at all (verified by asserting no
aggregate pipeline stage is recorded). `StateKeys` returns `[]` — RAG retrieval is stateless per call, so there is
no persisted fallback-ID concept as in Memory.

> **Test-writer note:** the base `AIContextProvider.InvokingAsync` wrapper merges the original input
> `context.AIContext.Messages` into its returned `AIContext.Messages`, not just what `ProvideAIContextAsync` itself
> returns. Assertions on the merged result must use `Assert.Contains`/`Assert.DoesNotContain` rather than
> `Assert.Single`/`Assert.Null`, matching `MongoDBMemoryBehaviorTests`.

## Verification

Tests live under `dotnet/tests/MongoDB.AgentFramework.Tests/RAG/` and were written test-first (red before green):

- `FieldPathTests` — added `TryResolve` coverage (present/nested value, missing segment, non-document intermediate).
- `RAGPipelineBuilderTests` — exact ANN/ENN `$vectorSearch` stage shape, filter omission, `exact`/`numCandidates`
  mutual exclusivity, stage ordering, and asserts the pipeline has exactly two stages with **no** trailing
  `$project` stage, so the complete document survives to `MapResult`.
- `MongoDBRAGProviderLifecycleTests` — constructor ownership (injected vs. connection-string), vector-dimension
  validation, invalid-options rejection, and null-argument rejection across all four constructors.
- `MongoDBRAGProviderSearchTests` — ANN/ENN filter-in-stage placement, `numCandidates`/`limit`/`exact` wiring,
  capability gating before any embedding/network call, empty-query rejection, embedding dimension/finiteness
  validation, missing-ID/missing-text mapping errors, missing-optional-field-produces-null mapping, missing/
  non-numeric/non-finite `_ragScore` mapping errors, complete raw-document preservation with the reserved score
  alias stripped, `MongoException` translation, cancellation propagation, timeout translation, and a no-write-
  operations guarantee.
- `MongoDBRAGContextProviderTests` — attributed message shape, empty-query short-circuit, empty-results handling,
  fail-open behavior for retrieval/embedding/timeout failures, capability-error and cancellation propagation, and
  recent-message window limiting.
- `MongoDBRAGContractTests` — a language-neutral-style contract test (there is no Python RAG implementation yet to
  share a JSON fixture with) asserting that a multi-branch AND/OR `MandatoryFilter` is completely translated inside
  the `$vectorSearch` stage for both ANN and ENN.
- `MongoDBRAGIntegrationTests` — a credential-gated `integration-rag` test. Because index provisioning is out of
  scope for this slice, it targets a fixed, operator-provisioned collection/index pair
  (`MONGODB_RAG_COLLECTION`/`MONGODB_RAG_VECTOR_INDEX`, both with defaults) rather than creating its own index per
  run, and only ever inserts/deletes documents whose IDs carry a unique, test-owned prefix. It also asserts, against
  a real MongoDB deployment, that `RawDocument` preserves a field the mapping configuration never names
  (`tenant_id`) and never contains the reserved `_ragScore` alias.

Run:

```powershell
dotnet test dotnet\MongoDB.AgentFramework.slnx --filter "FullyQualifiedName~RAG"
dotnet test dotnet\MongoDB.AgentFramework.slnx
```

The sample at `dotnet/samples/RAGQuickstart/` seeds a small two-document knowledge collection, runs
`MongoDBRAGProvider.SearchAsync` directly, and runs `MongoDBRAGContextProvider.InvokingAsync` to show the attributed
before-invoke context. It requires `MONGODB_URI`/`MONGODB_DATABASE` and a pre-provisioned Vector Search index (see
the sample's header comment) since this slice does not provision indexes.

## Deferred to later slices

- `FullText` and `HybridRrf` retrieval modes (slices 10, 12).
- Vector Search index provisioning/`EnsureVectorSearchIndexAsync`-equivalent for RAG (slice 13).
- The `TextSearchProvider` composition/citation adapter, once a resolved package version exposes it.
- On-demand retrieval tool exposure and structured `MetadataQueryPlan` retrieval.
- Cross-language contract fixtures — no Python RAG implementation exists yet.
