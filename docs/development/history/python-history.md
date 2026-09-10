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
`MongoDBHistoryProviderOptions` is frozen: tenant/application/agent/user authorization,
the one permitted session, limits, retention, timeouts, and framework filter choices
cannot change after construction. A session ID alone is not accepted as authorization.
Service-managed AI history is rejected before replay to prevent duplicate ownership.

The constructor accepts an injected async PyMongo collection or client. Both remain
caller-owned. With connection settings, the provider creates an `AsyncMongoClient`;
`close()` and the async context manager close that client exactly once. Construction
does not contact MongoDB or provision indexes.

## Stored schema, ordering, and replay

Every authoritative message document has `_kind: "message"`, `schema_version: 2`,
`framework_version: 1`, all tenant/application/agent/user scope fields (including
explicit `null` values), `scope_discriminator`, `session_id`, monotonic `sequence`,
required internal `stable_message_id`, optional framework `message_id`, role, UTC
`created_at`, optional `expires_at`, and the public `Message.to_json()` payload
parsed as structured BSON-safe data under `message`. The discriminator hashes a
canonical versioned representation of every scope dimension. Every MongoDB
read/write/delete filter requires both it and the complete raw scope, so an absent
dimension never behaves as a wildcard into a more-specific partition.

Replay uses `Message.from_dict()`. Raw service representations excluded by Agent
Framework public serialization are intentionally not persisted. Schema version 1
documents require migration because they lack the complete discriminator. Before
returning current or empty history, the provider probes for authorized version 1
documents under the exact raw scope and session. For each absent dimension, the
probe accepts only explicit BSON null or a missing field; it cannot match a
non-null, more-specific partition. Detection raises `MongoDBMappingError` with
migration guidance instead of silently hiding legacy history.

Internal `_kind: "sequence"` and `_kind: "reservation"` documents identify the same
complete scope. `find_one_and_update($inc, upsert=True, return_document=AFTER)`
atomically assigns a range. Before inserting any message, the provider durably
records that range under a retry-attempt token. A partial failure therefore retries
the original message IDs and sequence slots rather than allocating a split range.
Concurrent anonymous attempts receive separate tokens and ranges. Explicit-ID
attempts use a deterministic reservation token, so overlapping writers reconcile
to the winning range even when one writer completed before the other looked up the
reservation.

Agent Framework provider state stores a versioned envelope of failed and in-flight
attempts. Successful attempts are removed, so a later identical anonymous turn gets
new identities; failed attempts retain their token and generated IDs. On restored
sessions, orphaned in-flight attempts become retryable failed attempts. Malformed,
unknown-version, and legacy `mongodb_history_pending_ids` state fails with migration
guidance rather than ambiguously collapsing a legitimate turn. Stable scoped
document IDs and the message uniqueness index make retries idempotent; duplicate
stored data is accepted only when its payload, versions, and reserved sequence agree.
Messages without framework IDs retain `message_id: null` in their exact payload.
Reservations include `created_at` and a seven-day `expires_at`. Completed metadata
remains available for losing concurrent writers and is bounded by the explicit
reservation TTL index; failed attempts have the same bounded recovery window.
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

1. unique `scope_discriminator`/session/message identity;
2. unique `scope_discriminator`/session/sequence ordering;
3. optional message `expires_at` TTL with `expireAfterSeconds: 0`;
4. required reservation `expires_at` TTL with `expireAfterSeconds: 0`.

All definitions require a partial filter for message documents with a string scope
discriminator. `validate_indexes()` checks exact key order, uniqueness, partial
filter semantics, and TTL configuration and provides recreate guidance for every
mismatch. Identity and ordering indexes are created with `locale: simple`; validation
accepts the server-equivalent omission or explicit simple representation and rejects
case-insensitive or other non-binary collations.

`clear_messages()` requires the configured
authorization and exact session, deletes only that partition, resets its allocator,
removes retry reservations, and returns the acknowledged message-document count.
Applications must not clear a session concurrently with writes.

Runtime privileges require find, insert, update (allocator), and scoped delete.
Provisioning additionally requires index-management privileges. Production
connections should use TLS and appropriate network access controls.

## Verification

Public-seam unit tests in `python/tests/unit/test_history_provider.py` cover lossless
content/additional properties, framework filters and attribution, ordering,
concurrency, retries, isolation, lifecycle, errors, versions, and explicit indexes.
The language-neutral contract is
`python/tests/contracts/fixtures/history_contract.json`.

Credential-gated `python/tests/integration_history/test_history_integration.py`
uses a uniquely prefixed collection and targeted `finally` cleanup. Run the sample
with:

```powershell
python samples\history_quickstart.py
```

Set `MONGODB_URI`, `MONGODB_DATABASE`, `MONGODB_HISTORY_COLLECTION`,
`MONGODB_HISTORY_APPLICATION_ID`, `MONGODB_HISTORY_AGENT_ID`, and
`MONGODB_HISTORY_SESSION_ID`. Set `MONGODB_HISTORY_CLEAR=true` only to clear that
sample's authorized session.
