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

## Contract decision

`Microsoft.Agents.AI.Abstractions` (resolved and verified at the pinned floor
1.13.0, and unchanged through the latest published 1.16.0) does not expose a
public session-hosting persistence contract. `MongoDBAgentSessionStore` is
therefore **not** an implementation of any Agent Framework interface -- there
is none to implement -- and is not a fabricated one either. It is a narrow
facade over the public `AIAgent.SerializeSessionAsync` /
`DeserializeSessionAsync` serialization surface. See
[dotnet-contract-research.md](dotnet-contract-research.md) for the full
verification methodology and finding.

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
exactly once by `DisposeAsync`. Construction neither contacts MongoDB nor
creates indexes. All APIs pass `CancellationToken` to the driver. Optional
operation deadlines raise `MongoDBTimeoutException`; caller cancellation
remains cancellation. Driver failures preserve their cause in stable
retrieval, persistence, or concurrency errors.

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
  race is resolved by content-equality: if the already-stored document's
  `session` payload is byte-identical to the one this call intended to write,
  the call converges and returns the existing record instead of throwing.
  Otherwise it throws `MongoDBConcurrencyException` -- a real conflict is
  never silently overwritten or silently discarded.
- **`SetAsync`** with `expectedVersion: null` unconditionally creates or
  replaces (an upsert): there is no compare-and-swap, and no prior read is
  required. With a non-null `expectedVersion`, it performs an atomic
  compare-and-swap (`FindOneAndUpdateAsync` filtered on the exact stored
  version) that increments the version by exactly one on success. If the
  filter does not match because a *prior, already-applied* attempt already
  produced that exact version and content, the call converges rather than
  conflicting (retry idempotency without last-write-wins). If the stored
  document differs in version or content from what this call expected, it
  throws `MongoDBConcurrencyException`.
- **`DeleteAsync`** without `expectedVersion` is an idempotent no-op
  (`false`) when nothing matches. With `expectedVersion`, a mismatch throws
  `MongoDBConcurrencyException` rather than silently deleting (or silently
  not deleting) the wrong version.
- **`ListAsync`** never deserializes session content; it returns
  metadata-only summaries in ascending `session_id` order with an opaque
  continuation token, bounded to at most 10,000 items per call.

The complete framework-serialized session JSON is stored as a nested BSON
sub-document (`BsonDocument.Parse(element.GetRawText())` on write,
`ToJson(RelaxedExtendedJson)` + `JsonDocument.Parse` on read -- the same
round-trip technique `MongoDBChatHistoryProvider` uses for `ChatMessage`
losslessness). The store never inspects, maps, or type-coerces individual
fields inside that payload; unknown or future `AgentSessionStateBag` entries
survive a round trip unchanged. Every envelope carries `schema_version` and
`framework_version` markers; loading a document whose markers do not match
this build's constants throws `MongoDBMappingException` with migration
guidance rather than attempting a lossy or silent migration.

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
  "session": { "...": "agent-defined AgentSession JSON, stored verbatim" }
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
`dotnet/tests/MongoDB.AgentFramework.Tests/Persistence` cover lossless
round-trips including unknown `AgentSessionStateBag` state, tenant/user
isolation, create duplicate-key convergence versus real conflict,
unconditional upsert versus compare-and-swap semantics, CAS retry
convergence versus real staleness, idempotent and version-checked deletion,
default/explicit/absent TTL, list pagination and ordering, schema/framework
version rejection, cancellation propagation, invalid version-token rejection,
and index provisioning/validation. The credential-gated
`integration-persistence` test uses an `af_persistence_dotnet_test_`
collection and targeted `finally` cleanup.

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
