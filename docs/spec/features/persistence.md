# Session and Workflow Persistence

## Persistence requirements

Session Store and Workflow Checkpoint Store are required, first-class features in both languages. Their public APIs
MUST preserve equivalent observable behavior while adapting to the supported public Agent Framework contracts in each
language. They MUST remain separate from Memory and exact Chat History and from each other.

### MongoDB Session Store

The Session Store persists a complete framework `AgentSession` snapshot for stateless hosting. It includes provider
state such as recent-message windows and counters that exact Chat History alone may not contain.

Public types:

- Python: `MongoDBSessionStore(SessionStore)`
- .NET: `MongoDBAgentSessionStore` implementing the supported public Agent Framework hosting/session persistence contract

Required API semantics:

```text
get(session_id, isolation_scope) -> serialized AgentSession or null
set(session_id, session, expected_version?, expires_at?) -> new version
delete(session_id, isolation_scope, expected_version?) -> acknowledged result
```

Canonical envelope:

```json
{
  "_id": "scoped session identifier",
  "schema_version": 1,
  "framework_version": "serialization compatibility marker",
  "tenant_id": "optional mandatory isolation scope",
  "application_id": "optional scope",
  "agent_id": "optional scope",
  "session_id": "required opaque identifier",
  "version": 7,
  "created_at": "UTC timestamp",
  "updated_at": "UTC timestamp",
  "expires_at": "optional UTC timestamp",
  "session": { "framework-supported serialized AgentSession": true }
}
```

Requirements:

- use the framework's public serializer/deserializer; do not reflect over internal state
- use a replace/upsert with optimistic concurrency on `version` to prevent lost updates
- make create-only, compare-and-swap, and unconditional replacement semantics explicit
- require isolation scope in every get/set/delete filter
- support optional TTL independently from exact-history and memory retention
- reject unsupported schema/framework versions with actionable migration guidance
- keep encryption-at-rest and client-side field-level encryption deployment concerns documented but outside automatic
  provider configuration
- test unknown provider-owned session state, serialization round trips, concurrent updates, deletion, and expiration

The Session Store SHOULD store one current snapshot per scoped session initially. Snapshot history is a separate
feature and MUST NOT be retained accidentally through unbounded inserts.

### MongoDB Workflow Checkpoint Store

Workflow checkpoints persist resumable execution state, pending requests, executor state, and checkpoint lineage.
They are immutable historical records except for explicit deletion/retention operations.

Public types:

- Python: `MongoDBCheckpointStorage(CheckpointStorage)`
- .NET: `MongoDBCheckpointStore`, deriving from the supported public `JsonCheckpointStore` contract

Canonical envelope:

```json
{
  "_id": "checkpoint identifier",
  "schema_version": 1,
  "tenant_id": "optional mandatory isolation scope",
  "workflow_id": "workflow definition identifier",
  "session_id": "workflow session/run partition",
  "checkpoint_id": "required unique identifier",
  "parent_checkpoint_id": "optional lineage edge",
  "sequence": 12,
  "created_at": "UTC timestamp",
  "expires_at": "optional UTC timestamp",
  "checkpoint": { "framework-compatible checkpoint payload": true }
}
```

Required operations are save, load by ID, list in deterministic order, get latest, delete by ID, and list IDs. The
implementation MUST:

- preserve checkpoint IDs and parent lineage exactly
- make save idempotent for the same checkpoint ID and reject conflicting payloads
- query latest by a monotonic sequence or framework-defined order, never timestamp alone
- use unique scoped checkpoint identity and an indexed scoped `(workflow_id, session_id, sequence)` lookup
- support bounded pagination rather than loading unbounded workflow history
- allow optional TTL while documenting that expiring a parent can leave lineage gaps
- preserve framework serialization/version metadata and reject incompatible payloads
- isolate workflows and tenants before sorting, limiting, loading, or deleting
- test pending human approvals, resumption, branched lineage where supported, concurrent saves, latest lookup, and
  cleanup

Session snapshots and checkpoints MAY share an internal versioned BSON envelope utility. They MUST use separate
collections by default and separate public types.
