# .NET Chat History implementation

This guide describes implementation-map slice 5. The normative requirements are
[Chat History](../../spec/features/chat-history.md), [interfaces](../../spec/interfaces.md),
and [system architecture](../../spec/architecture/system.md). ADRs
[0002](../../decisions/0002-separate-memory-history-rag-and-persistence.md),
[0008](../../decisions/0008-store-versioned-exact-history-with-atomic-ordering.md), and
[0009](../../decisions/0009-enforce-behavioral-not-physical-parity.md) record rationale
without overriding those specifications.

## Public surface and ownership

`MongoDBChatHistoryProvider` in
`dotnet/src/MongoDB.AgentFramework/History/MongoDBChatHistoryProvider.cs` derives
from `Microsoft.Agents.AI.ChatHistoryProvider`. Its direct APIs are
`GetMessagesAsync`, `SaveMessagesAsync`, `ClearMessagesAsync`,
`EnsureIndexesAsync`, and `ValidateIndexesAsync`. The options record fixes the
tenant (optional), application, agent, and session scope at construction.
Application, agent, and session are required; a session ID alone is not an
authorization boundary.

Injected clients, databases, and collections remain caller-owned. The
connection-string constructor creates one owned `MongoClient`, disposed exactly
once by `DisposeAsync`. Construction neither contacts MongoDB nor creates indexes.
All APIs pass `CancellationToken` to the driver. Optional operation deadlines
raise `MongoDBTimeoutException`; caller cancellation remains cancellation.
Driver failures preserve their cause in stable retrieval or persistence errors.

## Framework lifecycle and data flow

The provider overrides only `ProvideChatHistoryAsync` and
`StoreChatHistoryAsync`. The public base provider continues to filter stored
request/response messages, merge loaded history with current input, stamp loaded
messages as `AgentRequestMessageSourceType.ChatHistory`, and avoid re-storing
history-origin messages. Direct storage and lifecycle storage use the same exact
serialization and authorization path.

Every message document contains `_kind: "message"`, `schema_version: 2`,
`framework_version: 1`, the complete canonical scope, an atomic sequence,
stable storage identity, optional framework message identity, role, UTC timestamps,
and a structured `message` payload. The canonical scope stores a versioned
`scope_discriminator`, every scope dimension, explicit BSON nulls for absent
`tenant_id` and `user_id`, and `session_id`. Therefore a tenantless provider cannot
match a tenant-scoped document or a legacy document that omitted dimensions.
`System.Text.Json` uses
`AgentAbstractionsJsonUtilities.DefaultOptions`, the public Agent Framework JSON
configuration for `ChatMessage` polymorphism and additional properties. BSON is
only the envelope representation; no text flattening or Memory document is used.
Unknown envelope or framework versions raise `MongoDBMappingException` with
migration guidance. Schema version 2 is a breaking authorization-boundary change;
version 1 data must be migrated rather than replayed in place.

Internal sequence and reservation documents have deterministic IDs derived from
the complete canonical scope. `FindOneAndUpdateAsync` atomically allocates a
contiguous range, and a token-keyed reservation persists its original start before
the first message insert. Partial retries reuse every original ordinal even when
later batches have completed, preventing retry reordering. Stable scoped message
IDs make writes idempotent. Messages without an ID receive random fallback IDs
tracked with the reservation token in version 2 pending-attempt state advertised
through `StateKeys`. Framework lifecycle storage keeps that state in
`AgentSession.StateBag`; direct storage keeps it in the provider. Operational and
cancelled attempts retain IDs and reservations across session serialization and
provider recreation. Confirmed success or compatible duplicate convergence deletes
the reservation and retires the attempt, so a later identical turn receives a new
identity. Ambiguous version 1, malformed, or unsupported retry state fails with
migration guidance while unrelated session state remains intact. Latest-N reads apply every
scope field before a descending sequence sort and limit, then reverse the bounded
result to chronological order. Applications must not clear and write the same
session concurrently.

## Schema and indexes

Representative message:

```json
{
  "_kind": "message",
  "schema_version": 2,
  "framework_version": 1,
  "scope_discriminator": "canonical SHA-256 discriminator",
  "tenant_id": null,
  "application_id": "app",
  "agent_id": "agent",
  "user_id": null,
  "session_id": "session",
  "sequence": 42,
  "stable_message_id": "message-42",
  "message_id": "message-42",
  "created_at": "UTC BSON date",
  "expires_at": "optional UTC BSON date",
  "message": { "role": "assistant", "contents": [] }
}
```

`EnsureIndexesAsync` explicitly creates regular indexes only: unique
`scope_discriminator`/session/stable-message identity, unique
`scope_discriminator`/session/sequence, and (when retention is configured) an
`expires_at` TTL index. Every index has the canonical message partial filter.
`ValidateIndexesAsync` checks exact key order, unique flags, partial filters, and
TTL expiry. It is read-only. Runtime privileges
are find, insert, allocator update, and scoped delete; provisioning additionally
needs index-management privileges. Retention is physical expiry while
`MaxMessages` only bounds model-visible history.

The .NET payload is not claimed physically interoperable with Python. Observable
scope, latest-N, ordering, and retry behavior share
`python/tests/contracts/fixtures/history_contract.json`. The canonical scope
discriminator is deliberately identical for the fixture dimensions; that narrow
identity contract does not imply complete payload or collection interoperability.

## Verification and operations

Offline public-seam tests under
`dotnet/tests/MongoDB.AgentFramework.Tests/History` cover exact content and
additional-property replay, tool call/result order, base lifecycle behavior,
authorization including tenantless isolation, atomic concurrency, preserved
sequence reservations, retry idempotency, latest-N, schema migration rejection,
pending-state recovery and validation, duplicate-key convergence, complete index
contracts, cancellation, errors, and ownership. The credential-gated
`integration-history` test uses an `af_history_dotnet_test_` collection and
targeted `finally` cleanup.

Run:

```powershell
dotnet test dotnet\MongoDB.AgentFramework.slnx
dotnet run --project dotnet\samples\HistoryQuickstart\HistoryQuickstart.csproj
```

The sample requires `MONGODB_URI` and `MONGODB_DATABASE`; optional History
variables are documented in `dotnet/README.md`. Logs and exceptions do not expose
payloads, embeddings, queries, scope values, collection names, or connection
strings. MongoDB TLS, network controls, encryption at rest, and least privilege
remain deployment responsibilities.
