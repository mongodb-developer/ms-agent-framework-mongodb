# Python Workflow Checkpoint Store implementation

This document describes implementation-map slice 17. Normative requirements are
[persistence](../../spec/features/persistence.md),
[architecture](../../spec/architecture/system.md),
[interfaces](../../spec/interfaces.md), [resilience](../../spec/resilience.md),
[security](../../spec/observability-security.md),
[testing](../../spec/testing.md), [packages](../../spec/packages.md), and
[samples](../../spec/samples.md). ADRs
[0012](../../decisions/0012-include-session-and-checkpoint-stores.md),
[0018](../../decisions/0018-version-gate-persistence-contracts.md), and
[0009](../../decisions/0009-enforce-behavioral-not-physical-parity.md) record
rationale; their proposed status does not override the specifications.

## Public contract and scope

`agent_framework_mongodb.MongoDBCheckpointStorage` explicitly derives from the
public Agent Framework Core 1.13 `CheckpointStorage` protocol and implements its
exact asynchronous seam:

- `save(checkpoint) -> CheckpointID`
- `load(checkpoint_id) -> WorkflowCheckpoint`
- `list_checkpoints(*, workflow_name) -> list[WorkflowCheckpoint]`
- `delete(checkpoint_id) -> bool`
- `get_latest(*, workflow_name) -> WorkflowCheckpoint | None`
- `list_checkpoint_ids(*, workflow_name) -> list[CheckpointID]`

Agent Framework's protocol has no tenant or run parameters. Therefore
`MongoDBCheckpointStorageOptions` immutably binds a required `tenant_id`,
`workflow_name`, and `session_id` at construction. `application_id` is optional.
Every read, write, sort, limit, and delete includes the discriminator and all raw
scope fields. `workflow_name` arguments and checkpoint payloads must equal the
bound value. A checkpoint ID alone is never an authorization filter.

The inherited list methods enumerate the complete authorized run in deterministic
order by repeatedly fetching configured bounded pages. Cancellation propagates
from every page request. The additional
`list_checkpoint_page(..., cursor=None, limit=None)` API lets callers consume
one bounded `MongoDBCheckpointPage(checkpoints, next_cursor)` at a time. The
default page size is 100, the configurable hard maximum defaults to 1000, and
invalid or unknown-version cursors fail closed. Ordering is
`(sequence, checkpoint_id)`, not timestamp or `iteration_count`.

## Serialization and immutable records

The implementation calls only the public `WorkflowCheckpoint.to_dict()` and
`WorkflowCheckpoint.from_dict()` serialization methods. It does not import or
reflect over framework internals. The public dictionary is encoded as BSON
binary with Python pickle so public framework message/event objects and
application executor state remain lossless. Loading uses a restricted unpickler:
safe built-ins and concrete `agent_framework` types are permitted, while
application types must be explicitly listed as `module:qualname` values in
`allowed_checkpoint_types`.

Pickle is appropriate only for application-owned, access-controlled checkpoint
storage. It is not a boundary against an attacker who can modify the collection.
Never load checkpoint documents from untrusted input. Use TLS, MongoDB access
controls, encryption at rest, and deployment-owned client-side field-level
encryption where required.

Each immutable checkpoint document is:

```json
{
  "_id": "<deterministic scoped sha-256>",
  "_kind": "workflow_checkpoint",
  "schema_version": 1,
  "framework_version": "agent-framework-core/1:WorkflowCheckpoint.to_dict/v1",
  "payload_version": "1.0",
  "idempotency_hash_version": 2,
  "scope_discriminator": "<scope sha-256>",
  "tenant_id": "tenant-1",
  "application_id": "application-1",
  "workflow_name": "approval-workflow",
  "session_id": "run-1",
  "checkpoint_id": "framework checkpoint ID",
  "parent_checkpoint_id": "optional exact lineage edge",
  "sequence": 12,
  "created_at": "<UTC BSON datetime>",
  "expires_at": "<optional UTC BSON datetime>",
  "checkpoint": "<BSON binary of the public dictionary>",
  "payload_hash": "<sha-256>"
}
```

The framework checkpoint ID is preserved exactly, while `_id` is deterministic
for the complete scope and ID. Idempotency hashes use a versioned canonical
logical representation of the public checkpoint dictionary rather than pickle
bytes. Exact `dict` values are explicitly order-insensitive because their public
checkpoint meaning is key/value state. `OrderedDict` carries its fully qualified
type and entry sequence, so a reversed order conflicts; allowlisted/framework
mapping subclasses also carry concrete type and sequence. `OrderedDict` and
allowlisted mapping instances are accepted only when their lossless pickle
reduction contains no instance state beyond entries. Attributes, assigned slots,
constructor state such as a `defaultdict` factory, list state, or a custom state
setter raise `MongoDBSerializationError` before sequence allocation, with
migration guidance to a plain `dict` or stateless `OrderedDict`. Other mapping
subclasses fail with stable guidance rather than being silently flattened. Sets
are stably ordered, scalar and collection types carry explicit
tags, and framework/application dataclasses or public `to_dict` values carry
stable type identities. The same logical checkpoint therefore hashes
identically across processes and `PYTHONHASHSEED` values without discarding
lossless concrete-type or order semantics. Cycles, non-finite floats, and
unsupported objects fail before sequence allocation with a stable
`MongoDBMappingError`. Pickle remains only the lossless storage encoding and is
not part of identity. Hash version 2 rejects version 1 records with migration
guidance. An identical retry returns the same ID. Reusing the ID with different
public state raises `MongoDBConcurrencyError`.
`schema_version`, `framework_version`, the checkpoint's public `version`, and
`idempotency_hash_version` are independent compatibility gates. Unknown values
raise `MongoDBMappingError` with migration guidance rather than best-effort
loading. Python/.NET physical checkpoint interoperability is not claimed.

## Sequence allocation, lineage, and retention

A separate, scoped counter document uses an atomic aggregation-pipeline upsert.
Concurrent saves therefore receive unique, positive, monotonic sequences. When
allocating, storage first reads the greatest retained checkpoint sequence in the
exact authorized scope. The atomic upsert computes the allocation from the
maximum of that observed value and the current counter before adding the batch
count. Concurrent recovery after a missing counter therefore cannot reset or
collide with retained sequences. When TTL is configured, the same update extends
the counter's `expires_at` to the maximum of its current and requested values;
an out-of-order shorter TTL can never move it backward. A non-expiring
checkpoint atomically changes `retention_mode` to `permanent` and removes
`expires_at`. That mode is dominant, so a later concurrent TTL write cannot
restore expiration. Legacy counters with a sequence but no expiration are
treated as permanent. Counter metadata therefore cannot expire while any
retained permanent checkpoint could still need its sequence. Retries and failed
inserts may leave sequence gaps; ordering never assumes contiguity.
`get_latest()` sorts by descending sequence and checkpoint ID.

`previous_checkpoint_id` is copied unchanged to `parent_checkpoint_id`.
Parents are not required to exist at save or load time. This permits branched
framework lineage and is required because MongoDB TTL removal is asynchronous
and may expire a parent before a child. Restoring a child does not traverse or
rewrite its parent edge.

`ttl` computes an optional UTC BSON-millisecond `expires_at` independently from
Session Store, Chat History, and Memory retention. The TTL monitor provides
eventual deletion; applications must not use expiration timing as workflow
coordination.

`clear_run()` is the explicit authorized lifecycle operation for a completed
run. It applies the complete constructor-bound scope to a checkpoint
`delete_many` followed by the exact deterministic counter `delete_one`, returning
`MongoDBCheckpointClearResult` with acknowledged checkpoint and counter counts.
It is retry-safe best-effort cleanup rather than a cross-deployment transaction;
callers must quiesce writers before clearing and may retry after a partial driver
failure. It never issues an empty or ID-only delete.

## Explicit regular indexes

Construction, save, load, and workflow hooks never mutate indexes.
`ensure_indexes()` is the explicit provisioning operation and
`validate_indexes()` is read-only.

| Name | Keys after scoped prefix | Options |
| --- | --- | --- |
| `checkpoint_scope_identity` | `checkpoint_id` | unique, simple collation |
| `checkpoint_scope_sequence` | `sequence` | unique, simple collation |
| `checkpoint_scope_lineage` | `parent_checkpoint_id` | simple collation |
| `checkpoint_expiration` | `expires_at` | `expireAfterSeconds: 0`, checkpoints |
| `checkpoint_counter_expiration` | `expires_at` | `expireAfterSeconds: 0`, counters |

The scoped prefix is `scope_discriminator`, `workflow_name`, and `session_id`.
Identity, sequence, lineage, and checkpoint TTL indexes have a checkpoint-only
partial filter, so the internal counter cannot collide with checkpoint
uniqueness. The separate counter TTL index has a counter-only partial filter.
MongoDB may process the two TTL indexes in either order; correctness never relies
on the counter outliving checkpoints because allocation recovers from the indexed
retained maximum.

Runtime privileges are find, insert, atomic update/upsert for the sequence
counter, and targeted delete on the checkpoint collection. Provisioning also
requires `createIndex`; validation requires index-list access.

## Errors, cancellation, ownership, and logs

Missing authorized records raise `MongoDBCheckpointNotFoundError`, which is both
an integration retrieval error and the framework's
`WorkflowCheckpointException`. Noncanonical lossless serialization raises
`MongoDBSerializationError`, a mapping-error subtype. Configuration, mapping,
concurrency, authorization, transient retrieval, transient persistence, and
other MongoDB failures use the package's stable categories while preserving
driver exceptions as `__cause__`. `asyncio.CancelledError` is never caught.

Injected clients and collections remain caller-owned. A storage created from a
connection string owns its PyMongo `AsyncMongoClient`; `close()` and the async
context manager close it once. Ownership never changes after an error.

Completion logs contain only feature, operation, outcome, bounded result count,
duration, and stable error category. They exclude IDs, scopes, payloads,
collection/database names, filters, driver messages, hosts, and credentials.

## Verification

Public serialization, actual workflow pause/resume, cross-process canonical
idempotency, stateful-mapping rejection, conflict, lineage, concurrent sequence,
missing-counter recovery, complete inherited listing, bounded pagination, latest,
scope cleanup, counter TTL, TTL-gap, compatibility, index, cancellation, error,
and ownership tests are in
`python/tests/unit/test_checkpoint_storage.py`. Language-neutral outcomes are in
`python/tests/contracts/fixtures/checkpoint_storage_contract.json`.
Credential-gated real-deployment coverage is in
`python/tests/integration_persistence/test_checkpoint_storage_integration.py`
and uses a unique `test-checkpoint-` collection with cleanup in `finally`.

From `python`, run:

```powershell
uv run pytest tests\unit\test_checkpoint_storage.py tests\contracts\test_checkpoint_storage_contract.py
uv run pytest tests\integration_persistence -m integration_persistence
uv run ruff check src tests samples
uv run ruff format --check src tests samples
uv run mypy
uv run pyright
```

The integration command skips cleanly without `MONGODB_URI`.
