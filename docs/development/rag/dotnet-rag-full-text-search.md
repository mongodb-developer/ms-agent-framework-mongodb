# .NET FullText RAG direct search

This document describes the .NET portion of implementation-map
[slice 10](../../spec/implementation-map.md), governed by the
[RAG specification](../../spec/features/rag.md), the
[interface contract](../../spec/interfaces.md), and ADR rationale
[0002](../../decisions/0002-separate-memory-history-rag-and-persistence.md),
[0007](../../decisions/0007-use-typed-filters-and-native-search-pipelines.md), and
[0011](../../decisions/0011-release-features-through-staged-quality-gates.md). It builds directly on the public
contracts and typed filter AST from [slice 6](dotnet-rag.md) and the `SearchAsync`/`MongoDBRAGContextProvider` seams
introduced in [slice 8](dotnet-rag-vector-search.md), reusing that slice's result mapping, cancellation, timeout,
and citation formatting entirely unchanged.

This slice adds live `MongoDBSearchMode.FullText` retrieval through the existing `MongoDBRAGProvider.SearchAsync`
seam. It intentionally does **not** implement `HybridRrf`, Search index provisioning, or on-demand retrieval tools.
Those remain later implementation-map slices (12, 13).

## FullText-only constructors

`FullText` never embeds a query, so requiring every caller to supply an
`IEmbeddingGenerator<string, Embedding<float>>` it would never use — as the existing vector-family constructors do —
would be an unnecessary and misleading dependency. Rather than making the vector-family constructors' embedding
parameters optional (a source-breaking parameter-order/meaning change for existing callers), this slice adds an
entirely new, parallel constructor family that mirrors the vector family's four public overloads exactly (injected
`IMongoDatabase`, injected `IMongoCollection<BsonDocument>`, injected `IMongoClient`, and a connection-string
constructor) but accepts no `embeddingGenerator`/`vectorDimensions` parameters at all:

```csharp
public MongoDBRAGProvider(IMongoDatabase database, string collectionName, MongoDBRAGProviderOptions options, ILogger<MongoDBRAGProvider>? logger = null);
public MongoDBRAGProvider(IMongoCollection<BsonDocument> collection, MongoDBRAGProviderOptions options, ILogger<MongoDBRAGProvider>? logger = null);
public MongoDBRAGProvider(IMongoClient client, string databaseName, string collectionName, MongoDBRAGProviderOptions options, ILogger<MongoDBRAGProvider>? logger = null);
public MongoDBRAGProvider(string connectionString, string databaseName, string collectionName, MongoDBRAGProviderOptions options, ILogger<MongoDBRAGProvider>? logger = null);
```

Every overload calls `RequireFullTextOnlyConstructionMode(options.SearchMode)` immediately after `options.Copy()`
succeeds, throwing `MongoDBConfigurationException` if `options.SearchMode` is anything other than `FullText` —
otherwise a caller could construct a provider that can never actually search (no embedding generator to reach
`VectorAnn`/`VectorEnn`, and this family cannot be reconfigured after construction since `MongoDBRAGProviderOptions`
is copied immutably). The existing vector-family constructors, `SearchAsync`, and every other public member are
completely unchanged in signature and behavior.

The connection-string overload reuses the same exception-safety pattern the vector family established
([slice 8](dotnet-rag-vector-search.md#connection-string-constructor-exception-safety)): a private
`ConnectFullTextOnly` helper validates `options` (including calling `Validate()` directly) and the mode gate
**before** creating a client, and a shared private `ConnectClient` helper (extracted from the vector family's
`Connect` in this slice, as a pure refactor with no behavior change to `Connect` itself) disposes the client if
resolving the database/collection fails afterward. An internal-only overload accepting the same
`Func<string, IMongoClient>? clientFactory` test seam as the vector family exists solely for
`MongoDBRAGProviderLifecycleTests` to substitute a client or prove the factory is never invoked for a validation
failure.

## FullText pipeline

`SearchCoreAsync` branches on `_options.SearchMode`: `FullText` skips `EmbedAsync` entirely (proven by a dedicated
test using the *vector-family* constructor with a recording embedding generator but `SearchMode = FullText`, so the
proof covers the mode-gating itself, not merely the absence of a parameter on the FullText-only constructors) and
calls `RAGFilterTranslator.TranslateSearchFilter` followed by `Internal.RAGPipelineBuilder.BuildFullTextSearchPipeline`,
building a 3-stage pipeline:

1. `$search` — built with the typed `MongoDB.Driver.Search` `PipelineStageDefinitionBuilder.Search<BsonDocument>`
   builder (per the specification's "typed builders for supported stages" rule) wrapping a
   `compound.must` text query against `SearchTextFieldNames` (rendered as a single scalar `path` string for one
   configured field, or a BSON array of paths for more than one) and `compound.filter` (the translated
   `MandatoryFilter` array from `RAGFilterTranslator.TranslateSearchFilter`, entirely omitted when there is no
   effective filter — a top-level `AND` flattens directly into `compound.filter`'s array since that array already
   ANDs its entries, avoiding an unnecessary nested `compound` wrapper for the common mandatory-filter case), and
   `index` (`SearchIndexName`) set via `SearchOptions<BsonDocument>.IndexName`. The typed builder renders `index` and
   `compound` as sibling keys directly under `$search`, matching the specification's pipeline shape. The mandatory
   filter is placed **inside** this stage, not applied afterward, so authorization/tenancy narrows the candidate set
   MongoDB itself searches — identical in spirit to the vector pipeline's in-stage filter placement.
2. `$limit` — `TopK`.
3. `$set` — captures MongoDB's native `{ $meta: "searchScore" }` under the same reserved
   `Internal.FieldPath.ReservedScoreAlias` (`_ragScore`) alias the vector pipeline uses.

Like the vector pipeline, there is intentionally **no** trailing `$project` stage, so the complete original document
survives untouched to `MongoDBRAGProvider.MapResult`, which reads and strips the reserved score alias exactly as
described in [slice 8](dotnet-rag-vector-search.md#result-mapping) — that method, `MapScore`, `MapId`, and
`MongoDBRAGResult` itself required **no** changes for `FullText`, since they only read fields from whatever
`BsonDocument` the pipeline returns and are entirely agnostic to which retrieval mode produced it.

## Mode-specific option validation

`MongoDBRAGProviderOptions.Validate()` already validated `FullText`'s `SearchIndexName`/`SearchTextFieldNames`
requirement and the vector family's `VectorIndexName`/`VectorFieldName` requirement independently per mode (see
[slice 6](dotnet-rag.md)) — this slice required no options-validation changes. `FullText` does not require any
vector configuration, and `VectorAnn`/`VectorEnn` do not require any search configuration; only `HybridRrf` requires
both once implemented.

## Search-index capability validation (review fix)

rag.md's capability matrix (291-314) requires FullText retrieval to make Search-index capability gaps explicit and
cacheable rather than surfacing as opaque `$search` pipeline failures. This slice adds a read-only, mode-gated
`ValidateSearchIndexAsync(bool requireReady = true, bool refresh = false, CancellationToken)` seam that mirrors
Memory's `EnsureVectorSearchIndexAsync`/`ValidateVectorSearchIndexAsync` pattern:

- It lists the configured `SearchIndexName` via `IMongoCollection<BsonDocument>.SearchIndexes.ListAsync` and requires
  the match's `type` to be `"search"`.
- Where a static (non-dynamic) mapping definition is available (`{ mappings: { dynamic: false, fields: { ... } } }`
  — structurally different from Vector Search's flat `fields` array), it resolves each configured
  `SearchTextFieldNames` path (including dotted/nested paths through nested `type: "document"` mappings) and
  requires at least one applicable type definition to be text-compatible (`string`/`autocomplete`/`token`). Atlas
  Search allows mapping a single field to either one definition object or an array of multiple type definitions
  (for example both `"number"` and `"token"` on the same field simultaneously); a field is accepted if *any*
  applicable definition is text-compatible, and rejected only once every definition is confirmed incompatible — an
  unrecognized field-mapping shape (neither an object nor an array of objects, or an array containing a non-object
  entry) throws an actionable `MongoDBIndexMismatchException` rather than crashing. `mappings.dynamic` is likewise
  recognized in either of its two documented shapes — a plain boolean, or an object form (for example selecting a
  named type set) — both meaning "every field is indexed automatically", so `listSearchIndexes` provides no
  per-field enumeration to validate in either case; this is a documented driver/Atlas limitation, not a validation
  gap, and field validation is skipped for either dynamic shape. Any other `mappings.dynamic` shape (for example a
  number) is not a documented form and is rejected with an actionable error rather than being silently coerced by
  `BsonValue.ToBoolean()`'s truthiness rules.
- `requireReady` (default `true`) additionally requires the index to report a queryable/`READY` status.
- `SearchAsync` never calls this method — it is an opt-in health-check/startup gate, not an implicit precondition on
  every query — so normal retrieval never pays for the extra round trip. A successful result is cached in-memory for
  `SearchIndexValidationCacheDuration` (30 seconds) to keep a caller that *does* invoke it repeatedly (for example, a
  periodic health check) from re-inspecting the index on every call; `refresh: true` bypasses the cache. A cached
  lenient (`requireReady: false`) result never silently satisfies a later strict (`requireReady: true`) call, so a
  known-not-ready index can never appear to have become ready purely because the cache had not expired. The clock is
  exposed through an `internal TimeProvider` test-only property (not a constructor parameter, to avoid touching any
  public construction signature), defaulting to `TimeProvider.System`.
- Failures translate to actionable, existing exception types: `MongoDBIndexMissingException` (index absent),
  `MongoDBIndexMismatchException` (wrong type, or a configured field maps to a non-text-compatible type), and
  `MongoDBIndexNotReadyException` (`requireReady: true` and not queryable). Calling this method against a mode other
  than `FullText`, or a `$listSearchIndexes` call that itself fails (deployment/driver does not support it), throws
  `MongoDBCapabilityException` — intentionally diverging from Memory's `MongoDBRetrievalException` for the
  equivalent Vector Search inspection failure, since rag.md's capability matrix treats an uninspectable Search index
  as a capability-detection concern rather than a generic retrieval failure.
- `OperationCanceledException` always propagates unchanged, never wrapped.

Tests live in `MongoDBRAGSearchIndexValidationTests`, using a new `RAGSearchIndexManagerProxy` test double (faking
`SearchIndexes.ListAsync`, mirroring Memory's equivalent proxy) and a settable-clock `FakeTimeProvider`. They cover:
missing index, wrong index type, missing/wrong-type configured text field, nested dotted field paths, dynamic-
mapping field-skip (both the boolean and object `mappings.dynamic` shapes), a malformed `mappings.dynamic` shape, a
multi-type field mapping accepted because any applicable definition is text-compatible, a multi-type field mapping
rejected because none are, a malformed multi-type array entry, an unrecognized field-mapping shape, not-ready
rejection and allowance, mode gating (rejecting non-`FullText` configurations), cancellation propagation,
`MongoDBCapabilityException` wrapping of a `$listSearchIndexes` failure, and cache behavior (TTL reuse without a
second network call, `refresh: true` bypass, TTL expiry, and no stale-serving across a `requireReady` escalation).

## Owned-client options-snapshot fix (review fix)

A review found that the connection-string constructors validated `options` once directly in `Connect`/
`ConnectFullTextOnly` (a single enumeration of any list-typed option) but then, *after* the owned client was already
created, the chained core constructor called `MongoDBRAGProviderOptions.Copy()` again — which itself calls
`Validate()` and rebuilds list-typed properties via a collection-expression spread, each a further enumeration. If a
caller-supplied `IReadOnlyList<string>` (for example `MetadataFieldNames`) threw only on one of those *later*
enumerations, the exception would propagate out of the constructor-initializer chain before the instance ever
assigned `_client`, so nothing would ever exist to dispose the already-created owned `IMongoClient` — a genuine
leak, distinct from the disposal path already covered for a client-creation-adjacent failure.

The fix: `Connect`/`ConnectFullTextOnly` now call `options.Copy()` **exactly once**, producing an immutable
snapshot, entirely **before** creating the owned client. The tuple they return carries that snapshot alongside the
client/collection, and a new private `ValidatedOptions`-parameterized constructor overload (one per family) assigns
`_options` directly from it without ever calling `Copy()`/`Validate()` again. `ValidatedOptions` is a private
`readonly record struct` wrapper whose only purpose is to give this "already validated" constructor a distinct
parameter type from the public collection constructors, which must still copy caller-supplied options themselves —
callers of those constructors are unaffected. The injected-client/database/collection constructor families, which
never own a client, are unchanged.

Regression tests in `MongoDBRAGProviderLifecycleTests` use a new `SingleUseFieldNames` test double — an
`IReadOnlyList<string>` that throws after a configurable number of enumerations — on `MetadataFieldNames` (validated
in every mode) for both families:

- `...NeverEnumeratesOptionsListsAfterCreatingAClient` tolerates exactly one enumeration and asserts the internal
  `clientFactory` test seam is never invoked, proving the single validated snapshot is produced before any client
  exists.
- `...OnlyEnumeratesOptionsListsOnceOverall` tolerates exactly two enumerations (one from `Copy()`'s own `Validate()`
  call, one from its collection-expression rebuild) and asserts full construction success, proving no further
  enumeration occurs.

## Deterministic FullText sample/integration tests (review fix)

Atlas Search indexes newly written or re-seeded documents asynchronously, so a query issued immediately after
`InsertManyAsync`/`ReplaceOneAsync` can race the index and intermittently miss a document that is not yet
searchable. `MongoDBRAGIntegrationTests.FullTextSearchIsolatesTenantsOnAPreProvisionedIndex` and the FullText section
of `RAGQuickstart` now call a test/sample-local `PollUntilSearchableAsync` helper that repeatedly invokes
`SearchAsync` until the expected document ID(s) appear or a bounded timeout (30 seconds, 1-second interval) elapses,
propagating cancellation as a clear `TimeoutException` rather than a bare `OperationCanceledException`. This keeps
both deterministic without introducing any polling in the production `MongoDBRAGProvider.SearchAsync` path itself —
polling exists only in test/sample code, never in the library.

`FullTextSearchIsolatesTenantsOnAPreProvisionedIndex` additionally uses a second, unfiltered `readinessProvider`
(same index/collection, no `MandatoryFilter`) to independently poll until *both* the tenant-A and tenant-B documents
are searchable for the query, before asserting that the tenant-A-scoped `provider` excludes tenant B. Without this,
the exclusion assertion could pass vacuously merely because tenant B was never indexed/searchable at all — for
example due to residual indexing lag beyond the poll window — rather than because `MandatoryFilter` actually
excluded it from the `$search` pipeline. Cleanup (`DeleteManyAsync` of both tenant documents) always runs from the
`finally` block regardless of which poll times out, keeping the bounded-cleanup guarantee intact.

## Errors, cancellation, and result mapping

Unchanged from [slice 8](dotnet-rag-vector-search.md#errors-and-cancellation): `MongoException` translation to
`MongoDBRetrievalException`, `OperationCanceledException`/`MongoDBMappingException` propagation, `RetrievalTimeout`
translation to `MongoDBTimeoutException` through the same `WithDeadlineAsync` wrapper, and the read-only guarantee
(no write operation of any kind). `FullText` never calls `EmbedAsync`, so `MongoDBEmbeddingException` cannot occur
on this path. Result mapping (`MapId`, `MapScore`, `RawDocument` preservation, metadata/source resolution) is
identical to the vector pipeline, since both pipelines route through the same `MapResult`.

## `MongoDBRAGContextProvider`

No changes were required: the adapter composes `SearchAsync` opaquely and is entirely mode-agnostic, so citation
formatting, fail-open behavior, query selection (non-empty User/Assistant messages, excluding provider-generated
context, then `MaxRecentMessages` windowing), and `AdditionalProperties` preservation from
[slice 8](dotnet-rag-vector-search.md#mongodbragcontextprovider-before-invoke-adapter) apply unchanged to `FullText`.

## Verification

Tests live under `dotnet/tests/MongoDB.AgentFramework.Tests/RAG/` and were written test-first (red before green):

- `RAGPipelineBuilderTests` — `BuildFullTextSearchPipeline` scalar-path and multi-field-array `compound.must` shape,
  filter placement inside `compound.filter` (and omission when there is no effective filter), and asserts the
  pipeline has exactly three stages (`$search`, `$limit`, `$set`) with **no** trailing `$project` stage.
- `MongoDBRAGProviderLifecycleTests` — the FullText-only constructor family across all four public overloads:
  no embedding generator required, rejection of any non-`FullText` configured mode (`VectorAnn`/`VectorEnn`/
  `HybridRrf`), null-argument rejection, connection-string client ownership/disposal idempotency, and — mirroring
  the vector family's hardening — argument and **options** validation running before a client is created (proven
  with the internal `clientFactory` test seam) and owned-client disposal when a later step fails.
- `MongoDBRAGProviderSearchTests` — `$search` stage shape with the configured index/text fields, mandatory-filter
  placement inside `compound.filter`, a dedicated proof that `FullText` never invokes an embedding generator even
  when one is configured (using the vector-family constructor with `SearchMode = FullText`), native `searchScore`
  capture and complete raw-document preservation, and the absence of a narrowing `$project` stage. The existing
  `UnsupportedModesAreRejectedBeforeAnyEmbeddingOrNetworkCall` theory now only asserts `HybridRrf` is unsupported,
  since `FullText` is implemented in this slice.
- `MongoDBRAGContextProviderTests` — `CapabilityErrorsPropagateRatherThanFailingOpen` now configures `HybridRrf`
  (the only remaining unsupported mode) to continue exercising capability-error propagation.
- `MongoDBRAGContractTests` — a new `MandatoryFilterIsCompletelyTranslatedInsideTheSearchCompoundFilter` test
  asserting a multi-branch AND/OR `MandatoryFilter` is completely translated inside `$search.compound.filter`,
  alongside the existing vector-mode contract test.
- `MongoDBRAGIntegrationTests` — a new credential-gated `integration-rag-search` test,
  `FullTextSearchIsolatesTenantsOnAPreProvisionedIndex`. Because index provisioning is out of scope for this slice,
  it targets a fixed, operator-provisioned Search index (`MONGODB_RAG_SEARCH_INDEX`, defaulting to
  `agent_framework_rag_search`) over the shared `MONGODB_RAG_COLLECTION` collection, rather than creating its own
  index per run, and only ever inserts/deletes documents whose IDs carry a unique, test-owned prefix. It also
  asserts, against a real MongoDB deployment, that `RawDocument` preserves a field the mapping configuration never
  names (`tenant_id`) and never contains the reserved `_ragScore` alias. It polls via `PollUntilSearchableAsync`
  (see [review fix](#deterministic-fulltext-sampleintegration-tests-review-fix) above) before asserting, so it is
  not flaky against real Atlas Search indexing lag.
- `MongoDBRAGSearchIndexValidationTests` — see the
  [Search-index capability validation review fix](#search-index-capability-validation-review-fix) above for full
  coverage.

Run:

```powershell
dotnet test dotnet\MongoDB.AgentFramework.slnx --filter "FullyQualifiedName~RAG"
dotnet test dotnet\MongoDB.AgentFramework.slnx
```

The sample at `dotnet/samples/RAGQuickstart/` now includes a FullText demonstration section, gated on the optional
`MONGODB_RAG_SEARCH_INDEX` environment variable (skipped with an explanatory console message when unset, since this
sample cannot provision a Search index itself), using the new FullText-only `MongoDBRAGProvider` constructor over
the same seeded documents, and polls via its own `PollUntilSearchableAsync` helper before printing results (see
[review fix](#deterministic-fulltext-sampleintegration-tests-review-fix) above).

## Deferred to later slices

- `HybridRrf` retrieval mode (slice 12).
- Search/Vector Search index provisioning for RAG (slice 13).
- On-demand retrieval tool exposure and structured `MetadataQueryPlan` retrieval.
- Cross-language contract fixtures — no Python RAG implementation exists yet.
