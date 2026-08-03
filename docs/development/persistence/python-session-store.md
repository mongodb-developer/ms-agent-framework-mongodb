# Python Session Store implementation

This document describes implementation-map slice 15. Normative requirements are
[persistence](../../spec/features/persistence.md),
[interfaces](../../spec/interfaces.md), [resilience](../../spec/resilience.md),
[security](../../spec/observability-security.md), and
[testing](../../spec/testing.md). ADRs
[0012](../../decisions/0012-include-session-and-checkpoint-stores.md),
[0018](../../decisions/0018-version-gate-persistence-contracts.md), and
[0009](../../decisions/0009-enforce-behavioral-not-physical-parity.md) record
rationale; their proposed status does not override the specifications.

## Public contract and lifecycle

`agent_framework_mongodb.MongoDBSessionStore` derives only from the public
`agent_framework.SessionStore` in Agent Framework Core 1.13. The inherited
`get(session_id)`, `set(session_id, session)`, and `delete(session_id)` seam is
preserved. Serialization calls the public `AgentSession.to_dict()` and
`AgentSession.from_dict()` methods. Agent Framework's public
`register_state_type()` registry therefore preserves registered provider-owned
state without this package inspecting framework internals.

The additional concurrency surface is:

- `get_versioned()` returns `MongoDBVersionedSession(session, version, expires_at)`;
- `create()` is create-only and returns version 1;
- `compare_and_set(..., expected_version=...)` returns the winning version; and
- `compare_and_delete(..., expected_version=...)` returns whether it deleted.

Identical create/update retries return the stored version. Different create
payloads, stale updates, and stale deletes raise `MongoDBConcurrencyError`.
Unconditional `set()` resolves bounded CAS races rather than issuing a
last-writer update that could silently lose a concurrent write. Unconditional
`delete()` remains idempotent.

`MongoDBSessionStoreOptions` is frozen. Tenant, application, and agent scope
cannot change after construction, and at least one is required. Injected async
collections and `AsyncMongoClient` instances remain caller-owned. A store built
from connection settings owns its client; `close()` and the async context
manager close it exactly once. Construction performs no I/O or index mutation.

## Authorization, document identity, and schema

Every database filter includes `_id`, `_kind`, the canonical
`scope_discriminator`, every raw scope dimension (including BSON null),
and `session_id`. The identifier is a SHA-256 digest of a versioned canonical
scope and the opaque session-store key. A document ID or session ID alone is
never used as authorization. There is no bulk or empty-filter deletion API.

One current snapshot is stored per authorized key:

```json
{
  "_id": "<scoped sha-256>",
  "_kind": "agent_session",
  "schema_version": 1,
  "framework_version": "agent-framework-core/1:AgentSession.to_dict/v1",
  "scope_discriminator": "<scope sha-256>",
  "tenant_id": "tenant-1",
  "application_id": "application-1",
  "agent_id": "agent-1",
  "session_id": "opaque-store-key",
  "version": 2,
  "created_at": "<UTC BSON datetime>",
  "updated_at": "<UTC BSON datetime>",
  "expires_at": "<optional UTC BSON datetime>",
  "session": {"type": "session", "session_id": "...", "state": {}},
  "payload_hash": "<idempotency sha-256>"
}
```

`schema_version` gates this MongoDB envelope. `framework_version` gates the
verified public AgentSession dictionary format. Unknown versions, malformed
versions, payloads, and expiration values raise `MongoDBMappingError` with
migration guidance; they are never interpreted best-effort. Python/.NET
physical collection interoperability is not claimed.

Updates replace the complete document only when the scoped current version
matches. `created_at` is stable, `updated_at` advances, and versions are positive
monotonic integers. `expires_at` must be future, timezone-aware input and is
normalized to UTC. `options.ttl` supplies a default expiration independently
from Memory and Chat History.

## Explicit regular indexes

`ensure_indexes()` is the only provisioning path; `validate_indexes()` is
read-only. Both identity indexes and the TTL index use a partial filter for
`_kind: "agent_session"` and string `scope_discriminator`.
The identity and version indexes explicitly use simple binary collation so
opaque session IDs retain case-sensitive identity under any collection default.

| Name | Keys | Options |
| --- | --- | --- |
| `session_store_scope_identity` | `scope_discriminator`, `session_id` | unique |
| `session_store_scope_version` | `scope_discriminator`, `session_id`, `version` | regular |
| `session_store_expiration` | `expires_at` | optional, `expireAfterSeconds: 0` |

The expiration index is required by validation and created only when `ttl` is
configured. MongoDB TTL deletion is asynchronous, so applications must not
depend on immediate physical deletion at the expiration instant.

Runtime privileges are find, insert, replace/update, and targeted delete on the
session collection. Index provisioning additionally requires `createIndex`;
validation requires index-list access. Use TLS, network controls, and MongoDB
encryption at rest. Client-side field-level encryption is deployment-owned and
is not configured automatically.

## Errors, cancellation, and observability

Direct operations fail to callers. Driver failures preserve the original
exception as `__cause__` and map to authorization, retrieval, persistence, or
transient categories. Cancellation propagates without translation. Logs contain
only feature, operation, outcome, bounded result count, duration, and error
category. They never contain IDs, scopes, session payloads, database or
collection names, hosts, filters, or driver messages.

## Verification

Public-seam unit tests are in
`python/tests/unit/test_session_store.py`. Language-neutral schema, index, and
concurrency outcomes are in
`python/tests/contracts/fixtures/session_store_contract.json`.
Credential-gated deployment coverage is in
`python/tests/integration_persistence/test_session_store_integration.py` and
uses a unique `test-session-` collection prefix with targeted cleanup.

From `python`, run:

```powershell
uv run pytest tests\unit\test_session_store.py tests\contracts\test_session_store_contract.py
uv run pytest tests\integration_persistence -m integration_persistence
uv run ruff check src tests samples
uv run ruff format --check src tests samples
uv run mypy
uv run pyright
```

The integration command skips cleanly without `MONGODB_URI`.
