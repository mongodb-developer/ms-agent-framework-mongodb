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
so they run with `CancellationToken.None` -- `CreateCheckpointAsync` still
applies the configured `PersistenceTimeout` internally (a linked deadline
token constructed from `CancellationToken.None`), so a hung write still fails
with `MongoDBTimeoutException` rather than blocking indefinitely, even though
the base contract gives it no external token to observe. Optional operation
deadlines raise `MongoDBTimeoutException`; caller cancellation on the facade
remains cancellation. Every non-cancellation driver failure from a public
save/load/list/delete/index call is wrapped in a stable
`MongoDBPersistenceException`, `MongoDBRetrievalException`,
`MongoDBCapabilityException`, or `MongoDBConcurrencyException` (never a raw
`MongoException`), always preserving the driver exception as
`InnerException`.

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
before opening a transaction, so a purely idempotent retry -- the common case
-- never opens a transaction or burns a sequence value:

- If no checkpoint with that identifier exists in scope, the store starts a
  MongoDB session (`collection.Database.Client.StartSessionAsync`) and runs
  `IClientSessionHandle.WithTransactionAsync` (majority write concern) so
  sequence allocation and the checkpoint insert commit atomically together: a
  new sequence is allocated (`FindOneAndUpdateAsync` with `$inc` on a
  per-session counter pseudo-document, upserted, inside the transaction) and
  the checkpoint document is inserted inside the same transaction. Two
  concurrent first-writers for the same session genuinely serialize on the
  shared sequence-counter document's write conflict; the driver's own
  `WithTransactionAsync` retry loop (per official MongoDB driver guidance for
  `TransientTransactionError`/`UnknownTransactionCommitResult`, bounded by the
  configured deadline and cancelable) handles the losing side, so no two
  checkpoints ever observe the same sequence and a retried transaction never
  double-allocates.
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
  identifier that the pre-check did not observe is resolved the same way via
  the transaction's insert-time duplicate-key exception: the losing writer
  re-fetches the winner's document and applies the same converge-or-conflict
  comparison, without having burned a sequence value (the whole allocation
  and insert aborted together in one transaction).
- If the colliding/raced document carries an incompatible `schema_version`,
  the call throws the migration exception below instead of ever comparing
  content.
- This design requires a deployment that supports multi-document
  transactions (a replica set, sharded cluster, or `mongos`). A standalone
  `mongod` rejects transaction usage with a recognizable server error (code
  20, "Transaction numbers..."); the store detects this precisely and throws
  `MongoDBCapabilityException` rather than silently claiming an ordering
  guarantee the deployment cannot provide, and no checkpoint is written.

Monotonic `sequence` allocation is independent of wall-clock timestamps:
`GetLatestCheckpointAsync` and `RetrieveIndexAsync`'s ordering are always
driven by `sequence`, never by `created_at`, so concurrent saves that commit
in a different order than they were allocated (or whose clocks are skewed)
still produce a stable, correct commit order. Because sequence allocation and
the checkpoint write commit atomically in one transaction, `sequence`
represents genuine committed order under cross-process concurrency, not
merely allocation order.

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
  "_id": "SHA-256 hash of the length-prefixed binary framing of (\"checkpoint\", scope discriminator, session id, checkpoint id)",
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
  "_id": "SHA-256 hash of the length-prefixed binary framing of (\"sequence_counter\", scope discriminator, session id)",
  "doc_type": "sequence_counter",
  "tenant_id": null,
  "workflow_id": "workflow-42",
  "session_id": "run-7",
  "sequence_value": 4
}
```

Every document identifier (`_id` above), the internal scope discriminator,
and the continuation-token payload are built by framing each component as a
big-endian 4-byte UTF-8 length followed by its exact UTF-8 bytes -- never by
joining components with a text delimiter -- before hashing or signing.
Session, checkpoint, and parent-checkpoint identifiers are arbitrary
caller-controlled opaque strings that may contain any character, including
one this store might otherwise have chosen as a delimiter (for example a
literal `|`); length-prefixed binary framing is unambiguous and injective
regardless of component content, so two logically distinct identity tuples
can never collide onto the same document ID, scope discriminator, or signed
payload the way delimiter-joined text could.

`EnsureIndexesAsync` explicitly creates three regular/TTL indexes:

- `checkpoint_identity_lookup`: unique index on
  `tenant_id, workflow_id, session_id, checkpoint_id`, partial-filtered to
  `doc_type: "checkpoint"` so the sequence-counter pseudo-documents are never
  indexed by it.
- `checkpoint_sequence_lookup`: non-unique index on
  `tenant_id, workflow_id, session_id, sequence`, backing
  `GetLatestCheckpointAsync` and paginated `ListCheckpointsAsync`, partial-filtered
  the same way (`doc_type: "checkpoint"`).
- `checkpoint_expiration_ttl`: TTL index on `expires_at`
  (`expireAfter = TimeSpan.Zero`), partial-filtered to documents that satisfy
  **both** `doc_type: "checkpoint"` **and** `expires_at: {$type: "date"}`
  together -- the `doc_type` condition ensures this TTL index can never reap a
  sequence-counter pseudo-document (which has no `expires_at` field at all,
  even if it shared this collection with unrelated document types in the
  future), and the `$type: "date"` condition ensures a checkpoint written
  with no expiration (`expires_at` is `BsonNull`, a valid "never expires"
  sentinel) is never mistaken for an expiration date.

`ValidateIndexesAsync` checks exact key order, unique flags, **and** an exact
`partialFilterExpression` match (both conditions on the TTL index, not just
one), and TTL expiry, without mutating MongoDB -- an index that lacks the
required `doc_type` isolation (for example a hand-created or legacy index)
fails validation with `MongoDBIndexMismatchException` rather than being
silently accepted. Neither index is ever created implicitly by construction,
saves, or retrieval; provisioning is always an explicit, separate call.
Runtime privileges are find, insert, scoped delete, and update/`findAndModify`
(the latter required by `AllocateSequenceAsync`'s `FindOneAndUpdateAsync` with
`$inc` against the per-session sequence-counter document, upserted inside the
same transaction as the checkpoint insert), plus transaction usage (a replica
set, sharded cluster, or `mongos` deployment); provisioning additionally needs
index-management privileges.

Continuation tokens are `Base64Url(payload) + "." + Base64Url(signature)`,
where `payload` is length-prefixed binary (`[1-byte format version]
[length-prefixed scope discriminator][length-prefixed session id][8-byte
big-endian last sequence]`, never delimiter-joined text) and `signature` is
`HMAC-SHA256(key, payload)`. The signing `key` is derived by combining the
store's **required, server-held**
`MongoDBCheckpointStoreOptions.ContinuationTokenSigningKey` (at least 32
cryptographically random bytes, for example
`RandomNumberGenerator.GetBytes(32)`, defensively copied at construction so a
caller mutating its original array afterward cannot change the store's
signing key, and excluded from `MongoDBCheckpointStoreOptions.ToString()` so
it is never accidentally logged) with the same scope discriminator used for
document identity, via HMAC domain separation -- the key is never derived
from, or discoverable from, the token's own contents. Verification checks the
signature (constant-time comparison via `CryptographicOperations.FixedTimeEquals`),
the format version, the embedded scope, and the embedded session id; any
mismatch -- including a token issued by a differently scoped
`MongoDBCheckpointStore` (different tenant/workflow), a token decoded with a
different signing key, or one that has been altered -- throws
`MongoDBConfigurationException` rather than silently returning wrong-scope
or skipped data. Because `ContinuationTokenSigningKey` is a required
constructor-time option (there is no key-less construction path), pagination
either works securely or fails configuration validation at construction; it
can never silently operate with a key derived from token-visible data alone.
This key must be configured once, kept stable, and be identical across every
`MongoDBCheckpointStore` instance that must accept each other's tokens (for
example every replica of a horizontally scaled service); rotating it
invalidates every token issued under the previous key.

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

Additional fake-driver tests specifically prove each hardened behavior:

- **Deterministic concurrent-writer ordering**: two concurrent
  `SaveCheckpointAsync` calls are interleaved via a fake transaction gate that
  signals the exact moment each writer is about to contend for the lock
  (no `Thread.Sleep`/timing-based flakiness); the writer that commits second
  observes the next sequence and is listed after the first, proving
  `sequence` reflects committed order, not call order. A companion test
  asserts `MongoDBCapabilityException` (with the driver's transaction-code-20
  error preserved as `InnerException`, and no document written) when the
  simulated deployment does not support transactions. The credential-gated
  `ConcurrentSaveCheckpointAsyncCallsAgainstARealDeploymentAllocateGaplessDistinctSequences`
  integration test proves 10 real concurrent writers against a live
  deployment allocate a gapless, duplicate-free `{1..10}` sequence set.
- **Delimiter-collision safety**: identifiers constructed so a naive
  delimiter-joined hash would collide (for example a session id containing a
  literal `|` combined with different splits of the same total string) are
  proven to produce distinct document identities and distinct, correctly
  isolated lineage results.
- **TTL/index isolation**: `EnsureIndexesAsync` is asserted to render the
  exact `partialFilterExpression` BSON for all three indexes (including the
  combined `doc_type` + `expires_at` date-type TTL condition), and
  `ValidateIndexesAsync` is proven to reject a simulated legacy index that is
  missing the `doc_type` isolation condition, for both the TTL index and a
  regular index, with `MongoDBIndexMismatchException`.
- **Continuation-token signing key**: tokens are proven to decode
  successfully across independent store instances that share the same
  signing key and scope, and to be rejected with
  `MongoDBConfigurationException` when decoded by a store configured with a
  different key.
- **Exception wrapping**: a generic (non-duplicate-key, non-transaction,
  non-cancellation) simulated driver failure injected into save, load, list,
  get-latest, and delete is proven to surface as the stable
  `MongoDBPersistenceException`/`MongoDBRetrievalException` wrapper with the
  original `MongoException` preserved as `InnerException`, never an
  unwrapped driver exception.
- **`CreateCheckpointAsync` timeout without a caller token**: a simulated
  hung driver call is proven to still be bounded by the configured
  `PersistenceTimeout`/`RetrievalTimeout` and to fail with
  `MongoDBTimeoutException`, even though the raw framework hook receives no
  external `CancellationToken` to observe.

Run:

```powershell
dotnet test dotnet\MongoDB.AgentFramework.slnx
dotnet run --project dotnet\samples\WorkflowCheckpointResumeQuickstart\WorkflowCheckpointResumeQuickstart.csproj
```

The sample requires `MONGODB_URI`, `MONGODB_DATABASE`, and
`MONGODB_CHECKPOINT_SIGNING_KEY` (base64-encoded, at least 32 cryptographically
random bytes); optional Workflow Checkpoint Store variables are documented in
`dotnet/README.md`. Logs and exceptions do not expose checkpoint payload
content, connection strings, scope values, or the continuation-token signing
key. MongoDB TLS, network controls, encryption at rest, and least privilege
remain deployment responsibilities.
