# .NET Workflow Checkpoint Store implementation

This guide describes implementation-map slice 18. The normative requirements
are [Workflow Checkpoint Store](../../spec/features/persistence.md) and
[interfaces](../../spec/interfaces.md). ADRs
[0009](../../decisions/0009-enforce-behavioral-not-physical-parity.md),
[0012](../../decisions/0012-include-session-and-checkpoint-stores.md), and
[0018](../../decisions/0018-version-gate-persistence-contracts.md) record
rationale without overriding those specifications.
[dotnet-checkpoint-contract-research.md](dotnet-checkpoint-contract-research.md)
records the primary-source verification behind the design decisions
summarized here.

## Public surface and ownership

`MongoDBCheckpointStore` in
`dotnet/src/MongoDB.AgentFramework/Persistence/MongoDBCheckpointStore.cs` is a
`sealed class : JsonCheckpointStore, IAsyncDisposable` -- a real, direct
derivation from the public
`Microsoft.Agents.AI.Workflows.Checkpointing.JsonCheckpointStore` extension
point, not a facade over an unrelated type. It implements all three required
abstract hooks (`CreateCheckpointAsync`, `RetrieveCheckpointAsync`,
`RetrieveIndexAsync`) and additionally exposes a richer, cancellable,
explicitly-identified public facade: `SaveCheckpointAsync`,
`LoadCheckpointAsync`, `GetLatestCheckpointAsync`, `ListCheckpointsAsync`,
`DeleteCheckpointAsync`, `EnsureIndexesAsync`, `ValidateIndexesAsync`. Both
surfaces delegate to the same internal storage core, so a checkpoint created
through the raw framework hook and one created through the explicit facade
observe identical idempotency, lineage, and version-gate behavior.
`MongoDBCheckpointStoreOptions` fixes the tenant (optional) and workflow
(required) authorization scope at construction, plus optional default TTL and
retrieval/persistence deadlines.

Injected clients, databases, and collections remain caller-owned. The
connection-string constructor creates one owned `MongoClient`, disposed
exactly once by `DisposeAsync`. Construction validates options and the
resolved framework assembly version entirely *before* creating that owned
client, so a validation failure never creates (and therefore never needs to
dispose) a client; if a later construction step that does require the client
fails, the constructor disposes the already-created client itself before
rethrowing. Construction otherwise neither contacts MongoDB nor creates
indexes. The facade passes `CancellationToken` to the driver; the three raw
framework hooks cannot (see
[dotnet-checkpoint-contract-research.md](dotnet-checkpoint-contract-research.md)),
so they run with `CancellationToken.None`. Optional operation deadlines raise
`MongoDBTimeoutException`; caller cancellation on the facade remains
cancellation. Driver failures preserve their cause in stable retrieval,
persistence, or concurrency errors.

Workflow checkpoints are stored in a **separate collection and document
`doc_type` from Session Store's session documents** -- distinct persistence
concerns per the product-boundary requirement in
[ADR 0012](../../decisions/0012-include-session-and-checkpoint-stores.md);
this store never reads or writes a session document, and
`MongoDBAgentSessionStore` never reads or writes a checkpoint document, even
if a caller points both at the same underlying MongoDB collection (not
recommended, but not actively prevented -- the `doc_type` discriminator
namespaces documents so this is at least self-consistent if it happens).

## Lifecycle and data flow

Checkpoints are **immutable historical records**: once committed, a
checkpoint's payload bytes and parent lineage never change on a retry.
`SaveCheckpointAsync` (and `CreateCheckpointAsync`, which delegates to the
same internal core) first performs a read-check against the identity scope
before allocating a sequence number, so a purely idempotent retry -- the
common case -- never burns a sequence value:

- If no checkpoint with that identifier exists in scope, a new sequence is
  atomically allocated (`FindOneAndUpdateAsync` with `$inc` on a per-session
  counter pseudo-document, upserted) and the document is inserted.
- If a checkpoint with that identifier already exists in scope with
  byte-identical payload and identical parent lineage, the call converges
  and returns the already-stored record unchanged -- it does not extend,
  touch, or re-derive `expires_at`, since checkpoints are immutable and a
  converging retry must never be expected to change a previously committed
  expiry.
- If a checkpoint with that identifier already exists in scope with a
  *different* payload or a *different* parent, the call throws
  `MongoDBConcurrencyException` -- a real conflict against an immutable
  record is never silently overwritten.
- A genuine race between two concurrent first-writers for the same
  identifier is resolved the same way, via the insert-time duplicate-key
  exception path: the losing writer re-fetches the winner's document and
  applies the same converge-or-conflict comparison.
- If the colliding/raced document carries an incompatible `schema_version`,
  the call throws the migration exception below instead of ever comparing
  content.

Monotonic `sequence` allocation is independent of wall-clock timestamps:
`GetLatestCheckpointAsync` and `RetrieveIndexAsync`'s ordering are always
driven by `sequence`, never by `created_at`, so concurrent saves that commit
in a different order than they were allocated (or whose clocks are skewed)
still produce a stable, correct commit order.

- **`LoadCheckpointAsync`** returns `null` when absent (a non-throwing,
  facade-level not-found convention).
- **`RetrieveCheckpointAsync`** (the raw framework hook) throws
  `KeyNotFoundException` when absent -- a deliberately different convention
  from `LoadCheckpointAsync`, chosen because `ICheckpointManager`'s XML
  documentation for the equivalent lookup explicitly documents
  `KeyNotFoundException`, even though every other MongoDB.AgentFramework
  not-found convention in this repository is a custom typed exception or a
  nullable return. Callers must not conflate the two surfaces' not-found
  behavior.
- **`RetrieveIndexAsync`** internally pages through `ListCheckpointsAsync` in
  bounded batches of 1,000 to build the full, unbounded index the base
  contract requires, applying `withParent` filtering client-side (branch
  lineage) after retrieval, since a full scan across all pages is already
  required for framework contract correctness.
- **`DeleteCheckpointAsync`** without a matching document is an idempotent
  no-op (`false`). Deleting a checkpoint that is another checkpoint's lineage
  parent leaves a lineage gap; this is documented, not prevented -- the store
  does not attempt cascading delete or lineage repair.
- **`ListCheckpointsAsync`** returns metadata-only summaries (no payload) in
  ascending `sequence` order, bounded per call, with an opaque
  scoped/versioned/tamper-rejecting continuation token for the next page.

Authorization scope (`tenant_id` + `workflow_id`, and `session_id` within
that scope) is applied to every query **before** any sort, limit, or delete
is executed -- there is no code path that sorts, limits, or deletes across
scopes and filters authorization afterward.

Every mutation and lookup filter requires an exact match on this build's
`schema_version` constant, not just the identity scope. A scoped document
that exists but was written by an incompatible schema version is always
detected **read-only, before any mutation is attempted** -- never partially
updated or deleted -- and raises `MongoDBMappingException` with a message
that states the expected schema version and links
[dotnet-checkpoint-store-migration.md](dotnet-checkpoint-store-migration.md)
verbatim.

The complete framework-produced checkpoint JSON is stored as the exact UTF-8
JSON bytes (`element.GetRawText()`), wrapped verbatim in a BSON `Binary`
field on write and read back as the identical bytes (`JsonDocument.Parse`
over the stored bytes) -- never re-parsed through `BsonDocument`, so there is
no BSON-type-coercion round trip to lose precision or distinguish integers
from decimals. Unusual numeric literals (values beyond `double` precision,
decimals with trailing zeros) survive a round trip byte-for-byte.

## Schema and indexes

Representative checkpoint document:

```json
{
  "_id": "scoped SHA-256 identity hash",
  "doc_type": "checkpoint",
  "schema_version": 1,
  "tenant_id": null,
  "workflow_id": "workflow-42",
  "session_id": "run-7",
  "checkpoint_id": "3e9d...af1",
  "parent_checkpoint_id": "root-checkpoint-id",
  "sequence": 4,
  "created_at": "UTC BSON date",
  "expires_at": "optional UTC BSON date",
  "checkpoint": "BSON Binary wrapping the exact UTF-8 JSON checkpoint payload bytes, stored verbatim"
}
```

A second, internal document shape backs atomic sequence allocation and is
excluded from every checkpoint query via the `doc_type` discriminator:

```json
{
  "_id": "scoped SHA-256 sequence-counter hash",
  "doc_type": "sequence_counter",
  "tenant_id": null,
  "workflow_id": "workflow-42",
  "session_id": "run-7",
  "sequence_value": 4
}
```

`EnsureIndexesAsync` explicitly creates three regular/TTL indexes, filtered
to checkpoint documents only via a partial-filter expression so the
sequence-counter pseudo-documents are never indexed by them:

- `checkpoint_identity_lookup`: unique index on
  `tenant_id, workflow_id, session_id, checkpoint_id`.
- `checkpoint_sequence_lookup`: non-unique index on
  `tenant_id, workflow_id, session_id, sequence`, backing
  `GetLatestCheckpointAsync` and paginated `ListCheckpointsAsync`.
- `checkpoint_expiration_ttl`: TTL index on `expires_at`
  (`expireAfter = TimeSpan.Zero`), partial-filtered to documents where
  `expires_at` is a BSON date so undated checkpoints never expire.

`ValidateIndexesAsync` checks exact key order, unique flags, partial
filters, and TTL expiry without mutating MongoDB. Neither index is ever
created implicitly by construction, saves, or retrieval; provisioning is
always an explicit, separate call. Runtime privileges are find, insert, and
scoped delete; provisioning additionally needs index-management privileges.

Continuation tokens are `{version}|{scopeDiscriminator}|{sessionId}|{lastSequence}`,
base64url-encoded and HMAC-SHA256-signed with a key derived from the same
scope discriminator used for document identity. Verification checks the
signature (constant-time comparison), the version tag, the embedded scope,
and the embedded session id; any mismatch -- including a token issued by a
differently scoped `MongoDBCheckpointStore` (different tenant/workflow), or
one that has been altered -- throws `MongoDBConfigurationException` rather
than silently returning wrong-scope or skipped data.

The .NET payload is not claimed physically interoperable with Python;
Workflow Checkpoint Store parity there is tracked separately in the
[implementation map](../../spec/implementation-map.md). Observable behavior
(authorization scoping, idempotency/conflict semantics, lineage, TTL) is the
shared contract, not the on-disk `checkpoint` payload shape, which is
inherently .NET-framework-serializer-defined.

## Verification and operations

Offline public-seam tests under
`dotnet/tests/MongoDB.AgentFramework.Tests/Persistence` cover byte-for-byte
lossless payload round-trips (including unusual numeric literals), idempotent
same-checkpoint-ID-and-payload convergence without a new sequence, conflict on
a different payload or a different parent, tenant/workflow scope isolation,
sequence monotonicity independent of timestamp ordering, `GetLatestCheckpointAsync`
correctness, bounded pagination with stable ordering across pages, tamper and
cross-scope continuation-token rejection, idempotent delete, load-absent
returning `null`, `RetrieveCheckpointAsync` throwing `KeyNotFoundException` on
absent, incompatible-schema-version rejection (read-only, before any
mutation), branched lineage (multiple children of the same parent, retrieved
via `RetrieveIndexAsync(sessionId, withParent:)`), default/explicit/absent
TTL, index provisioning/validation, resolved framework assembly version
gating, owned-client construction exception safety, and cancellation
propagation. A dedicated test builds a **real**
`Microsoft.Agents.AI.Workflows.CheckpointManager` over
`MongoDBCheckpointStore` via the public `CheckpointManager.CreateJson` factory
and exercises `CreateCheckpointAsync`/`RetrieveIndexAsync`/
`RetrieveCheckpointAsync` through the actual framework surface, including a
simulated pending-approval branch point and a resume-at-latest-checkpoint
scenario. The credential-gated `integration-persistence` test uses an
`af_persistence_dotnet_test_` collection and targeted `finally` cleanup,
proving exact round-trip, tenant isolation, retry convergence after a real
elapsed delay, pagination, and lineage against a live MongoDB deployment.

Run:

```powershell
dotnet test dotnet\MongoDB.AgentFramework.slnx
dotnet run --project dotnet\samples\WorkflowCheckpointResumeQuickstart\WorkflowCheckpointResumeQuickstart.csproj
```

The sample requires `MONGODB_URI` and `MONGODB_DATABASE`; optional Workflow
Checkpoint Store variables are documented in `dotnet/README.md`. Logs and
exceptions do not expose checkpoint payload content, connection strings, or
scope values. MongoDB TLS, network controls, encryption at rest, and least
privilege remain deployment responsibilities.
