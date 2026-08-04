# .NET Memory implementation

This document describes implementation-map
[slice 3](../../spec/implementation-map.md), governed by the
[Memory specification](../../spec/features/memory.md), the
[interface contract](../../spec/interfaces.md), and ADR rationale
[0002](../../decisions/0002-separate-memory-history-rag-and-persistence.md),
[0010](../../decisions/0010-fail-open-only-at-agent-adapter-boundaries.md), and
[0015](../../decisions/0015-default-memory-persistence-to-fail-open.md).
The ADRs remain proposed and do not override the specification.

## Public boundary and ownership

`MongoDBMemoryProvider` in
`dotnet/src/MongoDB.AgentFramework/Memory/MongoDBMemoryProvider.cs` derives
from the restored `Microsoft.Agents.AI.AIContextProvider` 1.13.0 contract. Its
database, client, collection, and connection-string constructors perform no
server or embedding operation. `MongoDBMemoryScope` requires at least one
application, agent, or user identity and is immutable. The per-invocation
`MongoDBMemoryProvider.State` may use distinct search and storage scopes.

Injected resources and `IEmbeddingGenerator<string, Embedding<float>>` remain
caller-owned. The connection-string constructor alone creates an owned
`MongoClient`; `DisposeAsync()` closes it idempotently. Options are validated
and copied during construction so later caller mutation cannot change field
paths, limits, or index names.

Optional positive `RetrievalTimeout` and `PersistenceTimeout` values bound the
complete embedding/database operation. Deadline expiry raises
`MongoDBTimeoutException`; caller cancellation remains `OperationCanceledException`.

## Data and control flow

`StoreAsync` accepts Agent Framework `ChatMessage` values, selects non-empty
user, assistant, and system text, and excludes provider-attributed messages.
It calls `GenerateAsync` once for the complete batch, validates count,
dimension, and finite values, then calls one unordered `InsertManyAsync`.
Cancellation reaches both boundaries.

The .NET physical BSON schema is lowercase:

```json
{
  "_id": "string",
  "role": "user",
  "message_id": "optional",
  "author_name": "optional",
  "application_id": "optional",
  "agent_id": "optional",
  "user_id": "optional",
  "session_id": "optional",
  "content": "text",
  "created_at": "UTC BSON date",
  "content_embedding": [0.0],
  "expires_at": "optional UTC BSON date"
}
```

Messages with framework IDs use SHA-256 of immutable scope and message ID.
Messages without IDs receive UUIDs retained only for failed attempts. Direct
calls keep retry state in the provider instance. Framework-hook calls advertise
`mongodb_memory_pending_batches` through `AIContextProvider.StateKeys` and use
the restored Agent Framework 1.13.0 `AgentSession.StateBag` public
`TryGetValue`, `SetValue`, and `TryRemoveValue` APIs. The JSON-native,
versioned state is:

```json
{
  "Version": 1,
  "Batches": {
    "<batch fingerprint>": {
      "Failed": [{"<message fingerprint>": "<memory UUID>"}],
      "InFlight": {
        "<attempt UUID>": {"<message fingerprint>": "<memory UUID>"}
      }
    }
  }
}
```

An invocation persists its in-flight IDs before insertion. Failure moves them
to `Failed`; retry claims one failed slot; confirmed success removes its slot.
A later identical successful batch therefore receives distinct IDs, while
concurrent attempts have separate attempt IDs. After session deserialization,
in-flight attempts unknown to the recreated provider are recovered as failed
attempts. Other `AgentSessionStateBag` keys remain untouched. Unknown versions
and malformed provider state fail with `MongoDBConfigurationException` and
explicit migration guidance rather than being discarded.

`SearchAsync` makes one embedding request and builds structured BSON for
MongoDB Vector Search. ANN uses `numCandidates`; ENN uses `exact: true`.
Application, agent, user, and session authorization fields are in
`$vectorSearch.filter`, before candidate and result limiting. Results preserve
the role, message identity, author, score, and origin session. The implementation
does not claim Python/.NET physical collection interoperability.

The restored `AIContextProvider.InvokingAsync` lifecycle combines current input
through the framework filter, calls `ProvideAIContextAsync`, and attributes the
returned messages. The provider supplies the configured untrusted-memory
instruction only when results exist. `InvokedAsync` calls
`StoreAIContextAsync`; the framework's input/output filters prevent recursively
storing provider context.

## Errors, lifecycle, and indexes

Direct store, search, deletion, listing, validation, and provisioning preserve
driver failures as inner exceptions in stable integration categories.
Cancellation and configuration/mapping/index failures are never suppressed.
At the framework boundary, operational retrieval and embedding failures return
empty additional context with content-free logging. Operational persistence
fails open by default; `PersistenceFailFast` propagates it.

`DeleteByIdAsync` combines `_id` with scope. `ClearSessionAsync` combines a
non-empty session with scope. `ClearUserAsync` requires user plus application
or agent. `ListAsync` returns content-free metadata with a maximum page size of
100 and `_id` keyset cursors. MongoDB deletion does not remove independent
backup, replica, application audit, or legal-retention copies.

No constructor, direct runtime API, or framework hook provisions an index.
`EnsureVectorSearchIndexAsync` explicitly creates the configured vector index
with all four filter paths and can poll for readiness. After creation, polling
tolerates both temporarily missing and building index observations. Deadline
expiry raises `MongoDBTimeoutException` with the last index state as its inner
exception; caller cancellation always propagates.
`ValidateVectorSearchIndexAsync` is read-only and checks path, dimensions,
similarity, filters, READY status, and queryability. Runtime roles need normal
collection read/write/delete privileges; index provisioning should use a
separately authorized principal.

## Verification

Offline public-seam tests are under
`dotnet/tests/MongoDB.AgentFramework.Tests/Memory/`. They use small boundary
fakes for the official MongoDB and embedding interfaces. The contract test
loads `tests/fixtures/memory/scope-filters.json`. The credentialed test uses a
uniquely prefixed collection and skips unless `MONGODB_URI` and
`MONGODB_DATABASE` are set. The runnable sample is
`dotnet/samples/MemoryQuickstart`.

Validated commands are recorded in the implementing change. Real MongoDB
Vector Search behavior is not claimed when the credential-gated test skips.
