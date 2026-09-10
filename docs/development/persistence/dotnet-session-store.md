# .NET Session Store implementation

This guide describes implementation-map slice 16. The normative requirements
are [Session Store](../../spec/features/persistence.md) and
[interfaces](../../spec/interfaces.md). ADRs
[0009](../../decisions/0009-enforce-behavioral-not-physical-parity.md),
[0012](../../decisions/0012-include-session-and-checkpoint-stores.md), and
[0018](../../decisions/0018-version-gate-persistence-contracts.md) record
rationale without overriding those specifications.
[dotnet-contract-research.md](dotnet-contract-research.md) records the
primary-source verification behind the design decision summarized here.

## Compatibility status: blocked, not 1.0-complete

**`MongoDBAgentSessionStore` is compatibility-blocked and is not a complete
implementation of the mapped slice's normative public-type requirement.**
[Session Store](../../spec/features/persistence.md) and
[implementation map slice 16](../../spec/implementation-map.md) require
`MongoDBAgentSessionStore` to implement the supported public Agent Framework
session-hosting contract. `Microsoft.Agents.AI.Abstractions` (resolved and
verified at the pinned floor 1.13.0, and unchanged through the latest
published 1.16.0) does not expose one -- see
[dotnet-contract-research.md](dotnet-contract-research.md) for the full
verification methodology and finding. This is an upstream framework gap, not
a design choice this repository can resolve unilaterally: closing the
specification's requirement needs either a `Microsoft.Agents.AI.Abstractions`
release that publishes a session-hosting contract, or an accepted decision
(not a self-accepted one -- see
[ADR 0018](../../decisions/0018-version-gate-persistence-contracts.md), which
remains `proposed`) to change the requirement itself.

Until then, this implementation ships as an **interim, narrow facade** over
the public `AIAgent.SerializeSessionAsync`/`DeserializeSessionAsync`
serialization surface, documented here as development-doc detail rather than
as a change to the normative specification. It is isolated behind the
internal `IAgentSessionCodec` seam specifically so a real adapter against a
future published contract can replace it without changing the storage schema
or any already-stored documents. Re-run the reflection methodology in
[dotnet-contract-research.md](dotnet-contract-research.md) against any newly
resolved `Microsoft.Agents.AI.Abstractions` version before treating this gap
as closed.

Because there is no framework contract to bind this facade's envelope shape
to, the `Microsoft.Agents.AI.Abstractions` `PackageReference` is itself
narrowed to the verified range `[1.13.0, 1.17.0)` (the next-minor exclusive
upper bound above the last verified 1.16.0), and every constructor
additionally inspects the *resolved* assembly version at runtime
(`typeof(AIAgent).Assembly.GetName().Version`) and throws
`MongoDBConfigurationException` for any version outside that same range --
even if a consuming project's dependency resolution or a transitive
reference somehow loads a version the `PackageReference` range did not
prevent. An internal `Func<Version>` constructor seam lets tests inject an
out-of-range version without loading multiple real assemblies side by side.
If this range is widened after re-verifying against a newer
`Microsoft.Agents.AI.Abstractions` release, both the `PackageReference` range
and the two `MongoDBAgentSessionStore` version constants must be updated
together.

## Public surface and ownership

`MongoDBAgentSessionStore` in
`dotnet/src/MongoDB.AgentFramework/Persistence/MongoDBAgentSessionStore.cs` is
a plain `sealed class : IAsyncDisposable`. Its direct APIs are `GetAsync`,
`CreateAsync`, `SetAsync`, `DeleteAsync`, `ListAsync`, `EnsureIndexesAsync`, and
`ValidateIndexesAsync`. `MongoDBAgentSessionStoreOptions` fixes the tenant
(optional), application (required), agent (required), and user (optional)
scope at construction, plus optional default TTL and retrieval/persistence
deadlines. `GetAsync`, `CreateAsync`, and `SetAsync` require the originating
`AIAgent` instance because `AgentSession`'s JSON shape is agent-defined;
`DeleteAsync` and `ListAsync` do not, because they never deserialize session
content.

Injected clients, databases, and collections remain caller-owned. The
connection-string constructor creates one owned `MongoClient`, disposed
exactly once by `DisposeAsync`. Construction validates options, the resolved
framework assembly version, and required database/collection text entirely
*before* creating that owned client, so a validation failure never creates
(and therefore never needs to dispose) a client; if a later construction step
that does require the client (resolving the database/collection) fails, the
constructor disposes the already-created client itself before rethrowing,
since no `MongoDBAgentSessionStore` instance ever exists to do so.
Construction otherwise neither contacts MongoDB nor creates indexes. All APIs
pass `CancellationToken` to the driver. Optional operation deadlines raise
`MongoDBTimeoutException`; caller cancellation remains cancellation. Driver
failures preserve their cause in stable retrieval, persistence, or
concurrency errors.

## Lifecycle and data flow

Every session is stored as exactly one document: there is no message log or
event stream, only the latest authorized snapshot and its version. The
internal `Internal.Persistence.IAgentSessionCodec` seam isolates
"(de)serialize a session through an `AIAgent`" from the rest of the store; its
only implementation today, `AIAgentSessionCodec`, wraps
`AIAgent.SerializeSessionAsync`/`DeserializeSessionAsync`. A future package
version that publishes a real session-hosting contract can add a new codec
(or a parallel adapter over the same BSON envelope) without changing the
store's public methods, its storage schema, or any already-stored documents.

- **`CreateAsync`** inserts a new document at version `1`. A duplicate-key
  race is resolved by content-equality: the already-stored document's payload
  bytes must always match what this call intended to write. The expiry
  comparison then depends on how this call's effective expiry was derived:
  - If the caller passed an explicit `expiresAt`, it must match the stored
    `expires_at` exactly (millisecond-normalized) -- identical content with a
    *different* explicit intended expiry is a genuine conflict, not a retry,
    and throws.
  - If the caller passed no `expiresAt` and `DefaultExpiration` is configured,
    the effective expiry is computed fresh from "now" on every call, so a
    retry's freshly recomputed default will almost never equal the originally
    persisted timestamp exactly. The call instead converges whenever the
    stored document already has a still-future `expires_at` (consistent with
    default-expiration semantics) *without extending it* -- the retry returns
    the original result and its original expiry unchanged. A stored document
    with no expiry, or one whose expiry has already passed, is not a
    compatible convergence target and throws instead.

  If the colliding document carries an incompatible
  `schema_version`/`framework_version`, the call throws the migration
  exception below instead of ever comparing content. Otherwise a real content
  conflict throws `MongoDBConcurrencyException` -- a real conflict is never
  silently overwritten or silently discarded.
- **`SetAsync`** with `expectedVersion: null` unconditionally creates or
  replaces (an upsert): there is no compare-and-swap, and no prior read is
  required -- every successful call always writes a freshly computed expiry.
  With a non-null `expectedVersion`, it performs an atomic compare-and-swap
  (`FindOneAndUpdateAsync` filtered on the exact stored version *and* the
  current `schema_version`/`framework_version`) that increments the version by
  exactly one on success (and, on that success path, always applies a freshly
  computed expiry, since an intentional content change is not a retry). If the
  filter does not match because a *prior, already-applied* attempt already
  produced that exact version and content, the call converges rather than
  conflicting (retry idempotency without last-write-wins), using the same
  explicit-exact-match versus default-derived-still-future-and-not-extended
  expiry rule as `CreateAsync` above. If the scoped document exists but its
  schema/framework markers are incompatible, the call throws the migration
  exception below instead of a compare-and-swap conflict. If the stored
  document differs in version or content from what this call expected, it
  throws `MongoDBConcurrencyException`.
- **`DeleteAsync`** without `expectedVersion` is an idempotent no-op
  (`false`) when nothing matches. If the scoped document exists but its
  schema/framework markers are incompatible, `DeleteAsync` throws the
  migration exception below -- regardless of whether `expectedVersion` was
  supplied -- rather than deleting a document it cannot safely read first.
  With `expectedVersion` against a compatible document, a version mismatch
  throws `MongoDBConcurrencyException` rather than silently deleting (or
  silently not deleting) the wrong version.
- **`ListAsync`** never deserializes session content; it returns
  metadata-only summaries in ascending `session_id` order with an opaque
  continuation token, bounded to at most 10,000 items per call, and excludes
  any session whose `expires_at` has already passed (logically expired even
  if the TTL index has not yet physically reaped it).

Every `CreateAsync`/`SetAsync`/`DeleteAsync` mutation filter requires an exact
match on this build's `schema_version` and `framework_version` constants, not
just the identity scope. A scoped record that exists but was written by an
incompatible schema/framework version is therefore always detected
**read-only, before any mutation is attempted** -- never partially updated or
deleted -- and raises `MongoDBMappingException` with a message that states the
expected markers, confirms no read/update/delete was attempted, and links
[dotnet-session-store-migration.md](dotnet-session-store-migration.md)
verbatim. This is a distinct failure mode from both "not found" (no scoped
document exists at all) and a compare-and-swap conflict (the document is
readable but its `version` does not match); callers must not conflate any of
the three.

`session_id` is treated as opaque and is never trimmed: `RequireText` still
rejects `null`/empty/whitespace-only values (there is no such thing as a
"session" with no id at all), but any other value -- including one with
leading or trailing whitespace -- is preserved exactly as given and forms a
distinct, independently reachable session identity. (The canonical
tenant/application/agent/user scope dimensions are still trimmed at
construction, per existing behavior; only `session_id` itself is exempt.)

The complete framework-serialized session JSON is stored as the public
serializer's exact UTF-8 JSON bytes (`element.GetRawText()`), wrapped
verbatim in a BSON `Binary` field on write and read back as the identical
bytes (`JsonDocument.Parse` over the stored bytes) -- never re-parsed through
`BsonDocument`, so there is no BSON-type-coercion round trip to lose
precision or distinguish integers from decimals. The store never inspects,
maps, or type-coerces individual fields inside that payload; unknown or
future `AgentSessionStateBag` entries -- including numeric literals beyond
`double` precision and decimals with trailing zeros -- survive a round trip
byte-for-byte. Every envelope carries `schema_version` and
`framework_version` markers; every read and mutation path requires an exact
match against this build's constants (see above), and a mismatch always
throws `MongoDBMappingException` with migration guidance rather than
attempting a lossy or silent migration.

## Schema and indexes

Representative document:

```json
{
  "_id": "scoped SHA-256 identity hash",
  "schema_version": 1,
  "framework_version": 1,
  "scope_discriminator": "canonical SHA-256 discriminator",
  "tenant_id": null,
  "application_id": "app",
  "agent_id": "agent",
  "user_id": null,
  "session_id": "session-42",
  "version": 3,
  "created_at": "UTC BSON date",
  "updated_at": "UTC BSON date",
  "expires_at": "optional UTC BSON date",
  "session": "BSON Binary wrapping the agent-defined AgentSession JSON's exact UTF-8 bytes, stored verbatim"
}
```

`EnsureIndexesAsync` explicitly creates two regular indexes only: a unique
`session_scope_lookup` index on `scope_discriminator` + `session_id`, and a
partial-filtered `session_expiration_ttl` TTL index on `expires_at`
(`expireAfter = TimeSpan.Zero`, filtered to documents where `expires_at` is a
BSON date so undated sessions never expire). `ValidateIndexesAsync` checks
exact key order, unique flags, partial filters, and TTL expiry without
mutating MongoDB. Runtime privileges are find, insert, update, and scoped
delete; provisioning additionally needs index-management privileges.

The .NET payload is not claimed physically interoperable with Python; Session
Store parity there is tracked separately in the
[implementation map](../../spec/implementation-map.md). Observable behavior
(authorization scoping, optimistic concurrency semantics, TTL) is the shared
contract, not the on-disk `session` payload shape, which is inherently
.NET-`AIAgent`-defined.

## Verification and operations

Offline public-seam tests under
`dotnet/tests/MongoDB.AgentFramework.Tests/Persistence` cover byte-for-byte
lossless round-trips (including numeric literals beyond `double` precision
and trailing-zero decimals), tenant/user isolation, create duplicate-key
convergence versus real conflict, unconditional upsert versus
compare-and-swap semantics, CAS retry convergence versus real staleness
(including expiry-aware convergence: an explicit intended expiry must match
exactly, while a default-derived expiry converges on the persisted,
still-future expiration without extending it, proven with an injectable fake
clock across simulated elapsed time so a retry's freshly recomputed default
cannot spuriously conflict; an intentional content change still gets a freshly
computed default expiry), idempotent and version-checked deletion,
default/explicit/absent TTL, list pagination/ordering/expiration filtering,
schema/framework version rejection on both read and every mutation path
(proving no mutation occurs and that the failure is distinguishable from a
not-found result or a CAS conflict), opaque non-trimmed `session_id` handling
(whitespace-only rejection; leading/trailing-space distinctness), resolved
framework assembly version gating (supported/below-floor/at-or-above-ceiling),
owned-client construction exception safety (validation-before-client-creation
and disposal-after-later-failure), cancellation propagation, invalid
version-token rejection, and index provisioning/validation. The
credential-gated `integration-persistence` test uses an
`af_persistence_dotnet_test_` collection and targeted `finally` cleanup, and
additionally proves default-expiration `CreateAsync`/CAS `SetAsync` retry
convergence after a real elapsed delay without extending the persisted
expiry.

Run:

```powershell
dotnet test dotnet\MongoDB.AgentFramework.slnx
dotnet run --project dotnet\samples\SessionPersistenceQuickstart\SessionPersistenceQuickstart.csproj
```

The sample requires `MONGODB_URI` and `MONGODB_DATABASE`; optional Session
Store variables are documented in `dotnet/README.md`. Logs and exceptions do
not expose session content, connection strings, or scope values. MongoDB TLS,
network controls, encryption at rest, and least privilege remain deployment
responsibilities.
