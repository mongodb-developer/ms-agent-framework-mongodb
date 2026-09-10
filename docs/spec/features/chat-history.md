# Chat History

## Chat History feature requirements

### Purpose

The Chat History provider stores and retrieves the exact ordered conversation for one Agent Framework session. It is
the MongoDB equivalent of framework-native history persistence, not semantic recall. It MUST NOT embed messages,
perform Vector Search, search across sessions, or rank messages by relevance.

### Public types

- Python: `MongoDBHistoryProvider(HistoryProvider)`
- .NET: `MongoDBChatHistoryProvider : ChatHistoryProvider`
- Options: `MongoDBHistoryProviderOptions` / `MongoDBChatHistoryProviderOptions`

The providers SHOULD expose `clear_messages(...)`/`ClearMessagesAsync(...)` in addition to required framework hooks.
Administrative pagination MAY be exposed separately from model-facing history loading.

### Exact-history behavior

- Append selected input, context, and output messages according to the framework base provider's filters.
- Load only the effective tenant/application/agent/session partition.
- Return messages in deterministic conversation order.
- Preserve every framework-supported content item needed for replay, including text, images/references, tool calls,
  tool results, approvals, annotations, author/name, additional properties, and message identifiers.
- Preserve tool-call/result ordering and assistant-message grouping; do not flatten messages to text.
- Use idempotent writes so an agent retry cannot append the same message twice.
- Support configurable maximum loaded messages and optional age/retention limits.
- Support clearing one authorized session without affecting semantic Memory or other sessions.
- Detect an incompatible stored schema/version and fail with migration guidance rather than dropping unknown content.
- Warn or fail when the underlying AI service already owns conversation history and enabling MongoDB history would
  duplicate it.

### Canonical history document

Each stored message SHOULD use one document with a versioned serialized payload:

```json
{
  "_id": "stable scoped message identifier",
  "schema_version": 1,
  "tenant_id": "optional mandatory isolation scope",
  "application_id": "optional scope",
  "agent_id": "optional scope",
  "session_id": "required opaque session identifier",
  "sequence": 42,
  "message_id": "optional framework message identifier",
  "role": "user | assistant | system | tool",
  "created_at": "UTC timestamp",
  "expires_at": "optional UTC timestamp",
  "message": {
    "framework-compatible serialized message": true
  }
}
```

The `message` payload MUST use public framework serialization where available. Language-specific envelopes may differ,
and cross-language history interoperability MUST NOT be promised until fixtures prove every content type. Plain-text
projection MAY be stored for diagnostics or Search, but it is not authoritative for replay.

### Ordering and concurrency

The provider MUST define one ordering strategy before implementation:

1. an application-assigned monotonic sequence persisted in provider session state, or
2. a MongoDB atomic per-session sequence allocator.

Timestamp-only ordering is insufficient. The compound identity MUST include isolation scope, session ID, and stable
message ID. Concurrent writers require either optimistic concurrency with an expected version or an atomic sequence
allocator. The initial release MAY document single-writer-per-session as a constraint, but it MUST detect duplicate
sequence/message IDs and remain idempotent.

Required regular indexes:

- unique scoped message identity
- scoped session plus `sequence` ascending for ordered load
- optional `expires_at` TTL index

History reads MUST apply mandatory isolation fields in MongoDB before sorting and limiting. To load the most recent
`N` messages while returning chronological order, query by sequence descending with a limit and reverse the bounded
result, or use an equivalent indexed pipeline.

### History retention and compaction

`MaxMessages` limits model-visible history and is not necessarily a deletion policy. Physical retention MUST be a
separate option. Chat reducers/compaction belong to Agent Framework or the application; the MongoDB provider stores
the messages selected by framework filters and MUST NOT invent summaries. Conversation compaction is out of scope.
