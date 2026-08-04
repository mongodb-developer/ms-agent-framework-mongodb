# .NET Ingestion samples implementation

This document describes implementation-map
[slice 14](../../spec/implementation-map.md) (.NET only), governed by the
[ingestion specification](../../spec/features/ingestion.md)'s "Knowledge
ingestion and bootstrap boundary" section and the
[RAG specification](../../spec/features/rag.md)'s "Parent-document
retrieval" section.

## Public boundary: sample-only, never a runtime provider

`docs/spec/features/ingestion.md` is explicit that the runtime provider MUST
NOT own crawling, parsing, chunking policy, or embedding-model selection, and
that any bootstrap utility the repository includes MUST be a clearly labeled,
non-production sample. This slice adds **no new public type to
`MongoDB.AgentFramework`** (the packable runtime project) at all. Every type
lives in a separate, non-packable class library,
`dotnet/samples/IngestionSamples/IngestionSamples.csproj`
(`MongoDB.AgentFramework.Samples.Ingestion` namespace,
`IsPackable=false`), referenced only by the two console samples and the test
project -- never by `MongoDB.AgentFramework.csproj` itself. `dotnet pack` on
the runtime project's `.nupkg` contains only `MongoDB.AgentFramework.dll`
(verified for `net8.0`/`net9.0`/`net10.0`); no ingestion sample type is ever
part of the shipped package surface.

The library calls the same public seams the runtime package exposes rather
than duplicating them: `Microsoft.Extensions.AI.IEmbeddingGenerator<string,
Embedding<float>>` for embeddings (the same abstraction
`MongoDBRAGProvider`/`MongoDBMemoryProvider` use), and the existing
`MongoDBRAGProvider`/`MongoDBRAGIndexManager` for querying and index
provisioning in the parent-document pattern -- this slice never re-implements
Vector Search querying or index management.

## Two ingestion patterns, one shared pipeline shape

| Type | Schema | Embedded records | Use case |
| --- | --- | --- | --- |
| `IncrementalIngestionPipeline` | Flat chunk records | Every chunk | docs/spec/samples.md's `IncrementalIngestion` sample |
| `ParentDocumentIngestionPipeline` | One parent + N child chunk records, linked by `ChunkRecord.ParentId` | Only child records | docs/spec/features/rag.md's "Parent-document retrieval" pattern |

Both pipelines share the same `IngestAsync(SourceDocument, CancellationToken)
-> IngestionResult` shape and the same incremental reconciliation semantics
over one `IChunkStore` (implemented by `MongoChunkStore` for MongoDB, and by
an in-memory `FakeChunkStore` for offline tests):

1. **Chunk** `document.Content` via `DocumentChunker.Chunk(content,
   ChunkingOptions)` -- a configurable sliding window (`WindowSize`,
   `OverlapSize`, both eagerly validated so a misconfigured window can never
   loop forever or emit an empty/duplicate chunk).
2. **Derive stable IDs and hashes.** `DeterministicId.ForChunk(tenantId,
   sourceId, index)` / `.ForParent(tenantId, sourceId)` hash the *canonical
   source identity* (tenant, source, positional index) with SHA-256 --
   never a random GUID or timestamp -- so re-ingesting identical content is
   idempotent and produces byte-identical IDs every run. Fields are combined
   via `CanonicalFraming.Frame(params string?[] fields)` -- a presence byte
   plus a 4-byte big-endian UTF-8 byte-length prefix per field, not delimiter
   concatenation -- so no combination of field values (including ones
   containing embedded delimiter/control characters, or a field-boundary
   shift such as `"ab"+"c"` vs. `"a"+"bc"`) can ever collide onto the same
   ID/hash. `ContentHash.Compute(text)` (single-field SHA-256) is the
   per-chunk change detector; `ContentHash.ComputeFramed(fields)` is the
   same canonical framing applied to the parent hash.
   `ParentDocumentIngestionPipeline`'s parent hash covers
   `Title`+`Url`+`Content` (not just `Content`) via `ComputeFramed`, so a
   title/URL-only edit -- even one that would collide under naive delimiter
   concatenation -- is still detected as a parent-record change even when no
   child chunk text changes.
3. **Diff against what is stored.** `IChunkStore.GetExistingHashesAsync(
   tenantId, sourceId, ct)` returns only the current tenant+source scope's
   stored hashes; `IngestionDiffing.Diff` classifies every desired record as
   unchanged (hash matches, skipped entirely -- no embed, no write), new/
   changed (hash differs or record is new -- queued for embedding+upsert), or
   stale (`documentId` no longer produced by current content -- queued for
   deletion).
4. **Embed only what changed**, in bounded batches, via `BatchEmbedder`
   (see below). Parent records (`ChunkRecord.ParentRecordType`) are never
   embedded; `NeedsEmbedding: false` on their `ChunkCandidate` skips them.
5. **Upsert then delete**, both scoped to `(document.TenantId,
   document.SourceId)`: `IChunkStore.UpsertAsync(records, ct)` writes only
   the new/changed records; `IChunkStore.DeleteAsync(tenantId, sourceId,
   staleIds, ct)` removes only stale IDs *within that same tenant+source
   scope* -- a bare ID is never sufficient authorization to delete, and an
   empty `staleIds` list is a no-op rather than an issued query.
   `MongoChunkStore.UpsertAsync`'s replace filter matches `_id` together
   with `tenant_id`, `source_id`, and `record_type` -- never `_id` alone --
   so even an accidental or hash-collision match on `_id` cannot silently
   overwrite a record from a different tenant/source/record-type scope: the
   filter simply won't match that other document, and the upsert instead
   fails closed with a MongoDB duplicate-key error.

`cancellationToken.ThrowIfCancellationRequested()` is checked before chunking
and before every store/embed call, so cancellation propagates through read,
embed, write, and delete without ever executing a partial step past the
cancellation point.

### Deleting a whole source and manifest reconciliation

`IncrementalIngestionPipeline`'s stale-chunk cleanup (step 5 above) only ever
removes IDs *within* a source that is still being actively re-ingested. A
source that disappears from the corpus entirely -- its `SourceDocument` is
simply no longer produced by the caller's crawl/read step -- would otherwise
never be revisited by any pipeline, leaving its records to linger forever.
Two additions on `IChunkStore` cover this:

- `DeleteSourceAsync(tenantId, sourceId, ct) -> Task<int>` deletes **every**
  record for one tenant+source scope. `MongoChunkStore`'s implementation
  pages the deletion in bounded batches of at most `MaxBatchSize` (500)
  rather than issuing one unbounded `DeleteManyAsync` -- each round trip
  finds up to `MaxBatchSize` matching IDs (still scoped to `tenant_id` +
  `source_id`), deletes just that page, and continues only while a full page
  was found.
- `ListSourceIdsAsync(tenantId, ct) -> Task<IReadOnlyList<string>>` lists
  every distinct source ID currently stored for one tenant, via the driver's
  `DistinctAsync` cursor.

`SourceManifestReconciler(chunkStore).ReconcileAsync(tenantId,
currentSourceIds, ct)` layers a reconciliation flow over both: it compares a
caller-supplied manifest of currently known source IDs against every source
ID actually stored for that tenant, and fully tombstones (`DeleteSourceAsync`)
every stored source absent from the manifest. An empty `currentSourceIds` is
a deliberate, valid "no sources currently observed" input (unlike
`DeleteAsync`'s no-op guard on an empty ID list, since this method's caller
explicitly opts into reconciliation). Cancellation is checked before each
disappeared source is deleted, so cancellation halts before any further
deletion executes; deletion is always scoped to the given `tenantId`, so
another tenant's same-named source is never touched. The result is a
`SourceReconciliationResult(DisappearedSourceIds, RecordsDeleted)`.
`IncrementalIngestionQuickstart`'s "Run 4" demonstrates this flow end-to-end.

### Batch embedding validation (`BatchEmbedder`)

`BatchEmbedder(generator, dimensions, maxBatchSize = 64)` validates
`dimensions`/`maxBatchSize` are positive at construction, then
`EmbedAsync` sends texts in bounded batches of at most `maxBatchSize`
(never one call per text, never one unbounded call for the whole set),
validating for every returned vector:

- the generator returned exactly as many vectors as texts in the batch;
- each vector's `Length` equals the configured `dimensions`;
- every component is finite (`float.IsFinite`) -- rejecting `NaN`/`Infinity`
  before it ever reaches MongoDB.

Any violation throws `IngestionValidationException` before the batch (or any
later batch) is written.

### Bounded, paged, cancellable local reading (`BoundedFileSystemSourceReader`)

`BoundedFileSystemSourceReader(directoryPath, tenantId, pageSize = 10)` is
the sample-local stand-in for "the application owns parsing" -- it streams
`*.txt` files from one directory via `IAsyncEnumerable<IReadOnlyList<
SourceDocument>> ReadPagesAsync(ct)`, ordered deterministically by file name,
in pages bounded to `pageSize`, checking cancellation before each page and
before each file read. Every produced `SourceDocument` is stamped with the
configured `tenantId`. A missing directory yields zero pages rather than
throwing.

## Parent-document RAG pattern

`ParentDocumentIngestionPipeline` writes one unembedded
`ChunkRecord.ParentRecordType` record holding the full source text/title/URL
plus one embedded `ChunkRecord.ChildRecordType` record per chunk, each with
`ParentId` set to the parent's deterministic ID. Retrieval is a strict two
step, no-callback flow with every bound enforced *before* the parent lookup
query is ever issued:

1. **Child-only search.** `IChildChunkSearcher.SearchAsync(query, ct)` (
   `MongoDBRAGChildChunkSearcher`, wrapping an existing `MongoDBRAGProvider`
   whose `MongoDBRAGProviderOptions.MandatoryFilter` must itself restrict
   results to `record_type == "child"` plus tenant) returns ordinary
   `MongoDBRAGResult`s; retrieval never touches parent records directly, and
   the RAG search boundary/authorization the runtime provider already
   enforces is reused rather than re-implemented.
2. **Bounded, de-duplicated parent hydration.** `ParentDocumentRetriever(
   childSearcher, parentLookup, tenantId, maxParents = 10,
   parentIdMetadataFieldName = "parent_id", contextBounding = null)` reads
   each child result's `parent_id` metadata (populated only if the
   searcher's own `MetadataFieldNames` includes that field path), keeps
   only the first (best-scoring, since child results already arrive
   ordered by score) child per distinct parent ID, stops collecting
   distinct parent IDs at `maxParents`, then issues exactly **one**
   `IParentLookup.FindParentsAsync(parentIds, tenantId, ct)` call
   (`MongoParentLookup`, a plain `$in`/`tenant_id` query against
   `record_type == "parent"`) -- never one lookup per child, never an
   unbounded fan-out, and never a caller-suppliable pipeline callback. A
   parent absent from the tenant-scoped lookup result (deleted, or excluded
   by the lookup's own tenant enforcement) is silently omitted rather than
   surfaced as a partial/unauthorized result; a child missing its parent
   linkage is skipped the same way. Each `ParentSearchResult` carries the
   best child's score/ID alongside the parent's own source attribution
   (falling back to the child's source fields only if the parent record
   does not carry them), so downstream consumers can still cite the origin.
   A final bounding pass then applies `ParentContextBoundingOptions`
   (`MaxCharactersPerParent`, default 2000; `MaxTotalContextCharacters`,
   default 8000) -- a documented, dependency-free character-count proxy for
   a token budget, not an actual tokenizer -- strictly *after* order,
   de-duplication, and source attribution are fully finalized: only each
   result's `Content` may be truncated (via `BoundedTextTruncation.Truncate`,
   which never splits a trailing UTF-16 surrogate pair), and once the
   running total budget is exhausted, remaining lower-ranked parents are
   omitted entirely rather than included empty. Both bounds are validated
   positive eagerly at construction.

## Tenant isolation

Every store operation (`GetExistingHashesAsync`, `UpsertAsync`'s per-record
`TenantId`, `DeleteAsync`, `DeleteSourceAsync`, `ListSourceIdsAsync`,
`MongoParentLookup.FindParentsAsync`) takes or carries an explicit
`tenantId`/`(tenantId, sourceId)` scope; `MongoChunkStore` and
`MongoParentLookup` place it inside the MongoDB filter alongside
`record_type`, never relying on a bare document ID as an authorization
boundary. `ParentDocumentRetrieverTests`, `IncrementalIngestionPipelineTests`,
`FakeChunkStoreTests`, and `SourceManifestReconcilerTests` all assert
cross-tenant records are never hydrated/deleted/reconciled-away by another
tenant's operations.

## Verification

Offline, deterministic unit tests are under
`dotnet/tests/IngestionSamples.Tests/` (110 tests, no network access):

- `DeterministicIdTests`, `ContentHashTests`, `CanonicalFramingTests` --
  stability, distinctness by index/tenant/source, parent-vs-chunk
  distinctness, empty/negative-argument validation, and collision-tuple
  regressions proving field-boundary shifts (including ones containing
  embedded delimiter/control characters) never produce the same ID/hash.
- `ChunkingOptionsTests`, `DocumentChunkerTests` -- default validity,
  non-positive window/negative overlap/overlap>=window rejection, no
  empty/duplicate chunks, determinism, full-content coverage.
- `BatchEmbedderTests` (using `FakeEmbeddingGenerator`) -- one vector per
  text, bounded batching, dimension-mismatch/non-finite-value rejection,
  cancellation propagation, constructor validation.
- `BoundedFileSystemSourceReaderTests` -- every file read exactly once across
  pages, page-size bound honored, tenant stamped on every document,
  cancellation propagation, missing directory yields zero pages.
- `IncrementalIngestionPipelineTests` (using `FakeChunkStore`) -- first-run
  writes all, rerun skips unchanged, only changed chunks embedded/upserted,
  stale chunks deleted, tenant-scoped deletion isolation, cancellation
  propagates before any store call, invalid document rejected.
- `ParentDocumentIngestionPipelineTests` -- parent unembedded + children
  embedded, parent-only content (title) change detected with no child chunk
  change (including a field-boundary-shift regression that would collide
  under naive delimiter concatenation), only changed children re-embedded
  (not the parent), stale children deleted within scope, cancellation
  propagation.
- `FakeChunkStoreTests` -- the same-scope-safety guard `MongoChunkStore`'s
  upsert filter enforces (rejecting an `_id` collision across a different
  tenant/source/record-type while still allowing same-scope replacement),
  plus `DeleteSourceAsync`/`ListSourceIdsAsync` tenant+source isolation and
  cancellation.
- `SourceManifestReconcilerTests` -- disappeared sources fully tombstoned,
  present sources and other tenants' same-named sources untouched,
  cancellation halts before any deletion, constructor/argument validation.
- `ParentDocumentRetrieverTests` (using `FakeChildChunkSearcher`/
  `FakeParentLookup`) -- ordered hydration by best child score,
  de-duplication of multiple children sharing a parent, fan-out bounded to
  `maxParents` before lookup is issued, orphan children skipped, parents
  absent from the authorized lookup omitted, cross-tenant parent never
  hydrated, no lookup call when no children match, cancellation
  propagation, constructor validation, invalid context-bounding rejection,
  oversized single-parent truncation, multi-parent truncation once the
  total budget is exhausted (order preserved), surrogate-pair-safe
  truncation.
- `BoundedTextTruncationTests`, `ParentContextBoundingOptionsTests` --
  within-bound no-op, oversized truncation, non-positive bound -> empty,
  null-text rejection, surrogate-pair safety on both sides of the cut,
  bounds validation.

Credential-gated integration tests (skip cleanly, not a failure, without
`MONGODB_URI`/`MONGODB_DATABASE`; each uses its own private
`MongoIntegrationFactAttribute`, matching the existing repo convention):

- `MongoChunkStoreIntegrationTests` exercises `IncrementalIngestionPipeline`
  + `MongoChunkStore` end-to-end against live MongoDB: first-run,
  unchanged-rerun, and shrink-with-stale-deletion, verifying remaining
  document counts and cleaning up in a `finally` block; plus a case seeding
  more than one `MaxBatchSize` (500) worth of records for one source and
  asserting `DeleteSourceAsync` deletes all of them while leaving another
  source and another tenant's same-named source untouched.
- `ParentDocumentSmokeIntegrationTests` provisions its own uniquely-named
  Vector Search index via `MongoDBRAGIndexManager.EnsureVectorSearchIndexAsync
  (waitUntilReady: true)`, ingests a parent+child document via
  `ParentDocumentIngestionPipeline`, searches via
  `MongoDBRAGChildChunkSearcher`+`MongoDBRAGProvider`, hydrates via
  `MongoParentLookup`+`ParentDocumentRetriever`, bounded-polls for Atlas
  indexing lag, asserts hydrated parent content/source attribution, and
  tears down both data and the index in a `finally` block.

These integration tests were **not executed against a live MongoDB
deployment** in this change (no `MONGODB_URI`/`MONGODB_DATABASE` were
available in the implementing environment) and remain deferred; they were
verified to compile, run, and skip cleanly.

The runnable samples are `dotnet/samples/IncrementalIngestionQuickstart`
(four sequential runs: new/unchanged/changed+stale-deleted reconciliation for
one source, then a "Run 4" demonstrating `SourceManifestReconciler` tombstoning
a second source once it is omitted from a subsequent "currently known
sources" manifest, then explicit bounded cleanup) and
`dotnet/samples/ParentDocumentRAGQuickstart` (parent-document ingestion,
child-chunk search + parent hydration, then explicit cleanup of both data and
the index). **Neither sample accepts a user-supplied Vector Search index
name**: each always generates its own unique, sample-prefixed name (e.g.
`agent_framework_sample_incr_<guid>` / `agent_framework_sample_pd_<guid>`) at
startup, checks whether that generated name happens to already exist (via
`MongoDBRAGIndexManager.GetVectorSearchIndexAsync`), provisions it via
`EnsureVectorSearchIndexAsync(waitUntilReady: true, timeout: 3 minutes)` only
if absent, and tracks whether *this run* created it. The `finally` block drops
the index only when this run created it -- never an arbitrary, pre-existing,
or externally configured index. (The `MONGODB_INGESTION_COLLECTION` env var
remains configurable, since a collection is not a "created/managed resource"
the same way an index is -- MongoDB implicitly creates collections on first
write, and cleanup already only ever touches the sample's own tenant+source
chunks within it.) Both samples were verified to build in Release for every
target framework and to fail fast with a clear `InvalidOperationException`
guard message when `MONGODB_URI`/`MONGODB_DATABASE` are unset; a live
credentialed run was not performed in this environment and remains deferred.

Validated commands are recorded in the implementing change
(`dotnet build`/`dotnet test`/`dotnet format --verify-no-changes`/
`dotnet pack` against `dotnet/MongoDB.AgentFramework.slnx`). Real MongoDB
Search/Vector Search index or write behavior is not claimed beyond what the
credential-gated tests or samples actually exercised.
