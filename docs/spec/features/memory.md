# Memory

## Memory feature requirements

### Purpose

The Memory provider stores selected conversation messages and retrieves semantically related messages before a model
invocation. It is semantic chat-history recall, not fact extraction, consolidation, profile inference, or a knowledge
graph.

### Required behavior

- Store user, assistant, and system text messages after a run.
- Batch embedding requests when multiple messages are stored.
- Store one MongoDB document per message.
- Search relevant memories before a run and inject them through the Agent Framework context-provider mechanism.
- Search across sessions by default within the configured scope.
- Permit optional session-specific search.
- Support application, agent, user, and session scope fields.
- Require at least one durable scope for storage and retrieval.
- Permit distinct storage and search scopes in .NET and an equivalent capability in Python if needed by callers.
- Exclude provider-generated context and chat-history replay from storage/search input according to framework
  conventions, preventing recursive memory ingestion.
- Support configurable maximum results, candidate count, context prompt, index name, vector dimensions, similarity,
  and exact versus approximate search.
- Expose explicit index creation and validation operations.
- Expose scoped deletion and retention operations independently from retrieval.
- Use stable message/document identifiers so retrying a batch does not create duplicate memories.

### Canonical memory document

Language-specific casing is allowed, but the logical fields must be equivalent:

```json
{
  "_id": "string identifier",
  "role": "user | assistant | system",
  "message_id": "optional framework message identifier",
  "author_name": "optional author",
  "application_id": "optional scope",
  "agent_id": "optional scope",
  "user_id": "optional scope",
  "session_id": "optional scope",
  "content": "message text",
  "created_at": "UTC timestamp",
  "content_embedding": [0.0]
}
```

Do not require both languages to use identical field casing in their first release if doing so would break the proven
connector implementation. Document the physical schema for each language. Prefer a configurable schema or common
lowercase schema before declaring cross-language collection interoperability.

### Index requirements

- Default memory vector field: `content_embedding` in Python; document the .NET connector's physical field.
- Filter fields: application, agent, user, and session identifiers.
- Validate index existence, vector field path, dimensions, similarity where available, filter fields, and queryable
  status.
- Collection creation does not imply Vector Search index creation.
- Exact search is a query option, not a separate index type.

### Memory lifecycle, deletion, and retention

Memory is user data and MUST support explicit lifecycle management. Both languages MUST provide equivalent operations:

```text
delete memory by ID within mandatory scope
clear memories for one session within mandatory scope
clear memories for one user within application/agent scope
enumerate memory metadata with bounded pagination for administration
```

Deletion methods MUST require the same application/agent/user authorization scope used for retrieval. An ID alone is
never an authorization boundary. Bulk deletion MUST return an acknowledged count and MUST NOT accept an unbounded
empty filter.

Optional expiration SHOULD use a regular MongoDB TTL index over an `expires_at` UTC date field. Requirements:

- retention is disabled unless explicitly configured
- expiration is eventual according to MongoDB TTL behavior, not an exact scheduling guarantee
- permanent and expiring documents MAY coexist by omitting `expires_at` for permanent records
- refresh-on-read is disabled by default because retrieval must normally remain read-only
- if refresh-on-read is enabled, it MUST be documented as a write, use scoped conditional updates, and not block
  retrieval success when the resilience policy permits
- Search index management and regular TTL/compound index management MUST remain separate
- privacy deletion documentation MUST explain backups, replicas, and application-level audit obligations

Batch insertion SHOULD use deterministic IDs derived from a framework message ID plus scope, or caller-supplied stable
IDs. If no stable source ID exists, generate it once and persist it in provider session state before a retry.
