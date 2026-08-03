# .NET Index Management implementation

This document describes implementation-map
[slice 13](../../spec/implementation-map.md) (.NET only), governed by the
[index-management specification](../../spec/features/index-management.md)
and ADR rationale
[0006](../../decisions/0006-make-index-provisioning-explicit.md) and
[0016](../../decisions/0016-keep-index-facades-in-runtime-packages.md). The
ADRs remain proposed and do not override the specification.

## Public boundary and ownership

Two explicit, feature-specific facades live in the runtime package:

- `MongoDBMemoryIndexManager` in
  `dotnet/src/MongoDB.AgentFramework/Memory/MongoDBMemoryIndexManager.cs`
  manages Memory's single Vector Search index, built from a
  `MongoDBVectorSearchIndexDefinition`.
- `MongoDBRAGIndexManager` in
  `dotnet/src/MongoDB.AgentFramework/RAG/MongoDBRAGIndexManager.cs` manages
  RAG's Vector Search index (`MongoDBVectorSearchIndexDefinition`), Search
  index (`MongoDBSearchIndexDefinition`), or both together for
  `MongoDBSearchMode.HybridRrf`. At least one definition is required; hybrid
  operations (`EnsureHybridAsync`/`ValidateHybridAsync`) require both.

Both facades are independently constructible from a database, collection, or
client (all caller-owned) or from a connection string (the facade then owns
and disposes the client it creates) -- the same database/collection/client/
connection-string constructor family already used by `MongoDBMemoryProvider`
and `MongoDBRAGProvider`, so a facade never requires a full provider or an
embedding generator. `OwnsClient` reports which case applies. `DisposeAsync`
only ever closes a client the facade itself created; an injected client is
never disposed.

This independent constructibility is what lets a facade instance play the
"provisioner" role from ADR 0006/0016: a deployment-time principal, distinct
from and more privileged than the "runtime" identity `MongoDBMemoryProvider`/
`MongoDBRAGProvider` connect with, can construct a facade purely to
create/update/drop indexes without ever constructing a provider or an
embedding generator.

## Operations

Every facade exposes the same eight-operation shape from
docs/spec/features/index-management.md, once per managed index:

| Operation | Mutates? | Notes |
| --- | --- | --- |
| `ListIndexesAsync` | No | Every Search/Vector Search index on the collection. |
| `GetIndexAsync` / `GetVectorSearchIndexAsync` / `GetSearchIndexAsync` | No | `null` if the named index does not exist. |
| `ValidateIndexAsync` / `ValidateVectorSearchIndexAsync` / `ValidateSearchIndexAsync` / `ValidateHybridAsync` | No | Read-only comparison; see below. |
| `EnsureIndexAsync` / `EnsureVectorSearchIndexAsync` / `EnsureSearchIndexAsync` / `EnsureHybridAsync` | Yes, explicit | Create-if-missing plus optional bounded polling. |
| `UpdateIndexAsync` / `UpdateVectorSearchIndexAsync` / `UpdateSearchIndexAsync` | Yes, explicit | Replaces an *existing* index's definition; a missing index is an error, never silently created. |
| `WaitUntilReadyAsync` / `WaitUntilVectorSearchIndexReadyAsync` / `WaitUntilSearchIndexReadyAsync` | No | Polls only; never creates. |
| `DropIndexAsync` / `DropVectorSearchIndexAsync` / `DropSearchIndexAsync` | Yes, explicit | Already-absent is a successful no-op. |

No constructor and no `Get*`/`List*`/`Validate*` method ever mutates MongoDB.
Only `Ensure*`/`Update*`/`Drop*` mutate, and only when the caller explicitly
invokes them -- never from a constructor, a framework lifecycle hook, or a
provider's direct retrieval/storage path.

## Shared internal mechanics (no duplication)

Both facades, and the pre-existing `MongoDBMemoryProvider.EnsureVectorSearchIndexAsync`/
`ValidateVectorSearchIndexAsync` and `MongoDBRAGProvider.ValidateSearchIndexAsync`/
`ValidateHybridSearchCapabilityAsync` (kept for API compatibility, now
delegating rather than duplicating), share one implementation under
`dotnet/src/MongoDB.AgentFramework/Internal/IndexManagement/`:

- `MongoDBSearchIndexes` -- the only code that calls
  `IMongoSearchIndexManager` (`FindAsync`, `ListAllAsync`, `CreateAsync`,
  `DropAsync`, `UpdateAsync`, `Classify`, `GetDefinition`) plus the
  `IsAlreadyExists`/`IsNotFound`/`IsUnauthorized` server-error classifiers
  (MongoDB command error codes 68/"AlreadyExists", "NotFound", and
  13/"Unauthorized" respectively, each also matched against the error
  message text as a fallback). Every method takes a `mapException` delegate
  so each caller preserves its own established exception type for a given
  failure category (Memory maps an inspection failure to
  `MongoDBRetrievalException`; RAG maps the same failure to
  `MongoDBCapabilityException`, since an unsupported `$listSearchIndexes` is
  itself a deployment-capability gap for RAG). A privilege failure is always
  raised as `MongoDBIndexPrivilegeException`, tightly, regardless of caller.
- `VectorSearchIndexEquivalence` / `SearchIndexEquivalence` -- pure,
  non-throwing semantic `Compare` functions plus a throwing `Validate`
  wrapper and a `BuildDefinition` function that derives the create/update
  BSON shape from the definition record exactly once.
- `BoundedExponentialPolling` -- the one bounded exponential-backoff retry
  loop every readiness-polling path uses.
- `MongoDBIndexPrivilegeException` -- a dedicated integration-exception
  category (`MongoDB.AgentFramework/Exceptions/MongoDBIndexPrivilegeException.cs`)
  distinguishing "the connected identity lacks index-management privileges"
  from a generic deployment/capability error, across every operation.

## Equivalence: semantic, order-insensitive, mismatch vs. compatible difference

`VectorSearchIndexEquivalence.Compare`/`SearchIndexEquivalence.Compare` never
compare raw BSON documents structurally. Instead they:

- resolve the vector field by `path`/`type: "vector"` (not array position),
  and every declared `type: "filter"` field by `path` as an unordered set;
- resolve Search text/filter field mappings by dotted path through nested
  `type: "document"` mappings, tolerating either a single mapping object or
  a multi-type mapping array;
- report an **actionable mismatch** (`MongoDBIndexComparison.Mismatches`,
  which makes `IsCompatible` `false`) only for a difference that changes
  retrieval correctness: a missing/mistyped vector or filter field, a wrong
  dimension/similarity, a field mapped to a non-text-searchable type, or a
  mandatory-filter field mapped to a type incompatible with its
  operator/value category (exact-match string filters require `token`, not
  the full-text-analyzed `string`; range filters require an orderable
  `number`/`date`/facet type matching the value's category);
- report a **compatible difference** (`CompatibleDifferences`, which never
  affects `IsCompatible`) for something that does not change retrieval
  behavior, for example an extra declared Vector Search filter field beyond
  what this definition requires, or a server-added default key.

A Vector Search comparison with `expected.Similarity == null` (used by
Hybrid's vector branch) intentionally skips the similarity check entirely --
a mismatched similarity metric there does not break `$rankFusion`
correctness the way it would for a raw-score-based caller.

### Dynamic Search mappings are a documented limitation, not an inferred change

A Search index with `mappings.dynamic == true` (or an object form of
`dynamic`) indexes every field automatically and `listSearchIndexes` provides
no per-field enumeration to validate against. `SearchIndexEquivalence`
**never** invents an automatic mapping change or silently assumes
compatibility to work around this: it treats a dynamic mapping's *declared*
field coverage as satisfied (there is nothing to statically disprove), but
surfaces `SearchIndexComparisonResult.DynamicMappingFieldsUnverified = true`
whenever `MandatoryFilter` references at least one field, so callers know
per-field operator/value-type compatibility could not be confirmed. Explicit,
non-dynamic application mapping configuration (`BuildDefinition`, used by
`EnsureSearchIndexAsync`/`UpdateSearchIndexAsync`) is required wherever exact
per-field mapping validation matters.

## Index state machine, polling, and errors

`MongoDBIndexStatus` is `Missing` / `Building` / `ReadyNotQueryable` / `Ready`
/ `Failed` -- a strict superset of the specification's state machine, adding
`ReadyNotQueryable` for the transient window between server status `READY`
and `queryable == true` actually becoming true. `MongoDBSearchIndexes.Classify`
is the single place that derives this from an inspected index document (or
`Missing` for a `null` document).

`BoundedExponentialPolling.RunAsync` is the one polling loop every
`WaitUntilReadyAsync`/`Ensure*(waitUntilReady: true)` path uses: a monotonic
`Stopwatch`-based deadline, a delay that doubles from an initial interval up
to a capped maximum (and is never allowed to overshoot the remaining
deadline), and a caller-supplied `isTransient` predicate that decides which
failures should keep polling ("not ready yet") versus fail immediately (a
mismatch, which polling can never resolve). `OperationCanceledException` is
never treated as transient regardless of `isTransient` and always propagates
immediately. A deadline expiry raises `MongoDBTimeoutException` with the
index name, last observed state, and the last exception as its inner
exception.

`EnsureIndexAsync`/`EnsureVectorSearchIndexAsync`/`EnsureSearchIndexAsync`
never retry a definitively wrong (mismatched) existing definition
automatically -- an update is always an explicit, separate `Update*` call.
`Ensure*` is idempotent under concurrent callers: `MongoDBSearchIndexes.CreateAsync`
treats a concurrent creator having already created the identically named
index (server error 68) as a successful no-op rather than surfacing an
"already exists" failure, and `Drop*` treats the index already being absent
as a successful no-op.

## Errors, privileges, and observability

Every facade surfaces the same stable exception categories the rest of the
package uses (`MongoDBConfigurationException`, `MongoDBIndexMissingException`,
`MongoDBIndexMismatchException`, `MongoDBIndexNotReadyException`,
`MongoDBTimeoutException`, `MongoDBIndexPrivilegeException`, and the
Memory/RAG-specific base categories), always preserving the underlying
driver exception as `InnerException`. `OperationCanceledException` is never
caught as an operational failure at any layer. No log statement or exception
message includes secrets, connection strings, embeddings, raw index command
responses, or user-bearing filter values -- only index names, field paths,
and classification outcomes.

## Least-privilege guidance

Per docs/spec/features/index-management.md's required-privileges table:

| Role/workload | Required operation categories | Facade usage |
| --- | --- | --- |
| Memory runtime (`MongoDBMemoryProvider`) | Read/aggregate/insert on the memory collection, plus Search query permissions | Never constructs `MongoDBMemoryIndexManager` for mutation; `ValidateVectorSearchIndexAsync` only. |
| RAG runtime (`MongoDBRAGProvider`) | Read/aggregate on the knowledge collection, plus Search query permissions | Never constructs `MongoDBRAGIndexManager` for mutation; `ValidateSearchIndexAsync`/`ValidateHybridSearchCapabilityAsync` only. |
| Index provisioner (deployment tooling) | List/create/update/drop Search indexes on approved collections | Constructs `MongoDBMemoryIndexManager`/`MongoDBRAGIndexManager` explicitly, typically under a distinct, more privileged connection string than the runtime provider uses. `Ensure*`/`Update*`/`Drop*`. |
| Integration tests | Create/drop test-prefixed collections and indexes in an isolated database | Uses a uniquely prefixed collection name and cleans up in a `finally` block; skips cleanly without `MONGODB_URI`/`MONGODB_DATABASE`. |

Runtime identities should **not** receive index-management privileges
(`createSearchIndexes`/`dropSearchIndexes`/`updateSearchIndexes`); only a
separately authorized provisioner identity should. Exact built-in/custom
MongoDB roles must be verified against the target Atlas/MongoDB deployment
and documented before package publication -- this has not yet been done and
remains deferred.

## Verification

Offline public-seam tests are under
`dotnet/tests/MongoDB.AgentFramework.Tests/Memory/MongoDBMemoryIndexManagerTests.cs`
and `dotnet/tests/MongoDB.AgentFramework.Tests/RAG/MongoDBRAGIndexManagerTests.cs`,
using the same small boundary fakes as the existing Memory/RAG tests
(`MemoryTestDoubles.cs`/`RAGTestDoubles.cs`, extended with `CreateOneAsync`/
`DropOneAsync`/`UpdateAsync` proxy support and exception injection). They
cover every operation's missing/present/mismatch/compatible-difference/
not-ready paths, privilege-vs-capability error distinction, idempotent
concurrent `Ensure`/`Drop` under real `Task.WhenAll` races, `WaitUntilReadyAsync`
timeout and cancellation, and caller-owned-vs-manager-owned client disposal.

The credential-gated integration tests
(`Memory/MongoDBMemoryIndexManagerIntegrationTests.cs`,
`RAG/MongoDBRAGIndexManagerIntegrationTests.cs`) exercise the full
provisioner-then-runtime sequence (`EnsureIndexAsync`/`EnsureHybridAsync` with
`waitUntilReady: true`, then `ValidateIndexAsync`/`ValidateHybridAsync`,
`ListIndexesAsync`, idempotent re-`Ensure`, and `Drop*`) against a real
deployment, using a uniquely prefixed collection name and `finally`-block
cleanup. They skip cleanly (not a failure) unless `MONGODB_URI` and
`MONGODB_DATABASE` are set; this was not validated against a real MongoDB
deployment in this change and remains deferred.

The runnable sample is `dotnet/samples/IndexManagementQuickstart`,
demonstrating separate provisioner (`Ensure*`/`Drop*`) and runtime
(`Validate*` only) facade instances side by side.

Validated commands are recorded in the implementing change. Real MongoDB
Search/Vector Search index behavior is not claimed when the credential-gated
tests or sample skip.
