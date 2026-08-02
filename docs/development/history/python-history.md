# Python Chat History implementation

This document describes implementation-map slice 4. The normative requirements are
[Chat History](../../spec/features/chat-history.md), [interfaces](../../spec/interfaces.md),
and [system architecture](../../spec/architecture/system.md). ADRs
[0002](../../decisions/0002-separate-memory-history-rag-and-persistence.md),
[0008](../../decisions/0008-store-versioned-exact-history-with-atomic-ordering.md), and
[0009](../../decisions/0009-enforce-behavioral-not-physical-parity.md) record rationale;
their proposed status does not override the specifications.

## Public surface and lifecycle

`agent_framework_mongodb.history.MongoDBHistoryProvider` derives from the public
`agent_framework.HistoryProvider`. `before_run` and `after_run` delegate loading,
source attribution, and input/context/output selection to that base provider.
`MongoDBHistoryProviderOptions` is frozen: tenant/application/agent authorization,
the one permitted session, limits, retention, timeouts, and framework filter choices
cannot change after construction. A session ID alone is not accepted as authorization.
Service-managed AI history is rejected before replay to prevent duplicate ownership.

The constructor accepts an injected async PyMongo collection or client. Both remain
caller-owned. With connection settings, the provider creates an `AsyncMongoClient`;
`close()` and the async context manager close that client exactly once. Construction
does not contact MongoDB or provision indexes.

## Stored schema, ordering, and replay

Every authoritative message document has `_kind: "message"`, `schema_version: 1`,
`framework_version: 1`, all configured scope fields, `session_id`, monotonic
`sequence`, `message_id`, role, UTC `created_at`, optional `expires_at`, and the
public `Message.to_json()` payload parsed as structured BSON-safe data under
`message`. Replay uses `Message.from_dict()`. Raw service representations excluded
by Agent Framework public serialization are intentionally not persisted.

An internal `_kind: "sequence"` document identifies the same complete scope.
`find_one_and_update($inc, upsert=True, return_document=AFTER)` atomically assigns
sequence numbers. Stable scoped document IDs and the message uniqueness index make
retries idempotent; duplicate stored data is accepted only when its payload and
versions agree. Messages without framework IDs receive IDs before persistence.
Latest-N reads filter the complete scope in MongoDB, sort descending, limit, then
reverse the bounded result. Optional `max_age` adds a server-side `created_at`
predicate. Tool calls and results remain separate ordered messages.

Unknown schema or framework serialization versions raise `MongoDBMappingError` with
migration guidance. MongoDB failures retain the driver exception as `__cause__` and
map to authorization, retrieval, persistence, transient, or timeout categories.
Cancellation is never translated. Completion/failure logs contain only operation,
duration, count/outcome, and error category—never scope, payload, collection, host,
or driver text.

## Indexes and administration

`ensure_indexes()` is the only provisioning path. It creates regular MongoDB indexes,
not Search indexes:

1. unique tenant/application/agent/session/message identity;
2. unique tenant/application/agent/session/sequence ordering;
3. optional `expires_at` TTL with `expireAfterSeconds: 0`.

`validate_indexes()` is read-only. `clear_messages()` requires the configured
authorization and exact session, deletes only that partition, resets its allocator,
and returns the acknowledged message-document count. Applications must not clear a
session concurrently with writes.

Runtime privileges require find, insert, update (allocator), and scoped delete.
Provisioning additionally requires index-management privileges. Production
connections should use TLS and appropriate network access controls.

## Verification

Public-seam unit tests in `python/tests/unit/test_history_provider.py` cover lossless
content/additional properties, framework filters and attribution, ordering,
concurrency, retries, isolation, lifecycle, errors, versions, and explicit indexes.
The language-neutral contract is
`python/tests/contracts/fixtures/history_contract.json`.
