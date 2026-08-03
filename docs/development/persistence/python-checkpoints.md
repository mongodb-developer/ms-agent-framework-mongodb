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

The inherited list methods return the first configured bounded page. The
additional `list_checkpoint_page(..., cursor=None, limit=None)` API returns
`MongoDBCheckpointPage(checkpoints, next_cursor)`. The default page size is 100,
the configurable hard maximum defaults to 1000, and invalid or unknown-version
cursors fail closed. Ordering is `(sequence, checkpoint_id)`, not timestamp or
`iteration_count`.

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
for the complete scope and ID. An identical retry returns the same ID. Reusing
the ID with different public state raises `MongoDBConcurrencyError`.
`schema_version`, `framework_version`, and the checkpoint's public `version`
are independent compatibility gates. Unknown values raise
`MongoDBMappingError` with migration guidance rather than best-effort loading.
Python/.NET physical checkpoint interoperability is not claimed.

## Sequence allocation, lineage, and retention

A separate, scoped counter document uses atomic `$inc` with upsert. Concurrent
saves therefore receive unique, positive, monotonic sequences. Retries and
failed inserts may leave sequence gaps; ordering never assumes contiguity.
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

## Explicit regular indexes

Construction, save, load, and workflow hooks never mutate indexes.
`ensure_indexes()` is the explicit provisioning operation and
`validate_indexes()` is read-only.

| Name | Keys after scoped prefix | Options |
| --- | --- | --- |
| `checkpoint_scope_identity` | `checkpoint_id` | unique, simple collation |
| `checkpoint_scope_sequence` | `sequence` | unique, simple collation |
| `checkpoint_scope_lineage` | `parent_checkpoint_id` | simple collation |
| `checkpoint_expiration` | `expires_at` | `expireAfterSeconds: 0` |

The scoped prefix is `scope_discriminator`, `workflow_name`, and `session_id`.
All indexes have a partial filter for checkpoint records so the internal
sequence counter cannot collide with checkpoint uniqueness.

Runtime privileges are find, insert, atomic update/upsert for the sequence
counter, and targeted delete on the checkpoint collection. Provisioning also
requires `createIndex`; validation requires index-list access.

## Errors, cancellation, ownership, and logs

Missing authorized records raise `MongoDBCheckpointNotFoundError`, which is both
an integration retrieval error and the framework's
`WorkflowCheckpointException`. Configuration, mapping, concurrency,
authorization, transient retrieval, transient persistence, and other MongoDB
failures use the package's stable categories while preserving driver exceptions
as `__cause__`. `asyncio.CancelledError` is never caught.

Injected clients and collections remain caller-owned. A storage created from a
connection string owns its PyMongo `AsyncMongoClient`; `close()` and the async
context manager close it once. Ownership never changes after an error.

Completion logs contain only feature, operation, outcome, bounded result count,
duration, and stable error category. They exclude IDs, scopes, payloads,
collection/database names, filters, driver messages, hosts, and credentials.

## Verification

Public serialization, actual workflow pause/resume, idempotency, conflict,
lineage, concurrent sequence, pagination, latest, scope, TTL-gap, compatibility,
index, cancellation, error, and ownership tests are in
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
