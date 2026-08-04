# MongoDB.AgentFramework

`MongoDB.AgentFramework` provides MongoDB-backed integrations for Microsoft Agent Framework.

## Status

For local non-publishing builds, dynamic Agent Framework compatibility reports,
version promotion, and protected release configuration, see the
[.NET release operations guide](../docs/development/release/dotnet-release-operations.md).
Every push to `build/dotnet-packaging-release` automatically runs the complete
credential-free readiness/security/SBOM surface and requests protected
integration approval. A .NET manifest change merged to `main` automatically
coordinates immutable tagging and credential-free release evidence, but cannot
publish while the governance approval described below remains unset.

The package version is a **pre-1.0 preview** (`0.1.0-preview.*`). The public
API surface, package metadata, symbols/SourceLink, and packaging pipeline are
implemented and verified; see the
[.NET packaging and release engineering guide](../docs/development/release/dotnet-packaging-release.md)
for exactly what has been validated and what has not.

| Area | Packaging readiness | Notes |
| --- | --- | --- |
| Semantic Memory | Packageable | See [Semantic Memory](#semantic-memory) below. |
| Exact Chat History | Packageable | See [Exact Chat History](#exact-chat-history) below. |
| RAG (Vector/FullText/HybridRrf) | Packageable | See [RAG contracts, typed filters, Vector Search (ANN/ENN), FullText, and HybridRrf](#rag-contracts-typed-filters-vector-search-annenn-fulltext-and-hybridrrf) below. |
| Index Management | Packageable | See [Index Management](#index-management) below. |
| Workflow Checkpoint Store | Packageable, complete | Derives from the real public `JsonCheckpointStore` extension point; see [Workflow Checkpoint Store](#workflow-checkpoint-store) below. |
| Session Store | **Compatibility-blocked, non-1.0-complete** | No public session-hosting persistence contract exists yet upstream; see [Session Store](#session-store) below. |

Because Session Store cannot yet implement a real framework interface, this
package **cannot claim a 1.0 release** even though every other area is
packageable. 1.0 is gated on an upstream Agent Framework session-hosting
contract becoming public, not on anything in this repository.

Publishing governance (release owner identity, security contact, support
channel, and NuGet package/organization ownership) is tracked in
[ADR 0013](../docs/decisions/0013-establish-project-and-publishing-governance.md),
which remains `proposed`, not `accepted`. No package version described in this
README has been published to NuGet.org, and no `dotnet-v*` tag has been
created.

## Semantic Memory

`MongoDBMemoryProvider : AIContextProvider` recalls scoped conversation
messages before an invocation and stores selected user, assistant, and system
text afterward. It is semantic recall, not exact chat-history replay or RAG.

```csharp
var memory = new MongoDBMemoryProvider(
    database,
    "memories",
    embeddingGenerator,
    vectorDimensions: 1536,
    _ => new MongoDBMemoryProvider.State(
        new MongoDBMemoryScope(
            applicationId: "my-app",
            userId: "user-123")),
    new MongoDBMemoryProviderOptions
    {
        MaxResults = 3,
        NumCandidates = 30,
    });

await memory.EnsureVectorSearchIndexAsync(waitUntilReady: true);
```

The state factory may return different `SearchScope` and `StorageScope`
instances. Search crosses sessions unless its scope includes `SessionId`.
Mandatory application, agent, user, and optional session fields are placed
inside `$vectorSearch.filter`.

### Direct APIs

- `StoreAsync` batches one embedding call and writes one document per eligible
  message.
- `SearchAsync` supports ANN by default and ENN with `exact: true`.
- `DeleteByIdAsync`, `ClearSessionAsync`, `ClearUserAsync`, and `ListAsync`
  always require a durable authorization scope.
- `EnsureVectorSearchIndexAsync` is the only provisioning path;
  `ValidateVectorSearchIndexAsync` is read-only.

Direct APIs surface stable `MongoDBIntegrationException` categories.
Framework retrieval fails open only for operational retrieval/embedding
failures. Framework persistence fails open by default and can be made
fail-fast with `PersistenceFailFast`. Cancellation always propagates.
Fallback IDs used by framework persistence retries are versioned in
`AgentSession.StateBag` under the provider's advertised `StateKeys`, so they
survive session serialization and provider recreation.

Injected clients, databases, collections, and embedding generators remain
caller-owned. Only a client created by the connection-string constructor is
disposed by the provider.

Run the sample after setting `MONGODB_URI`, `MONGODB_DATABASE`, and optionally
`MONGODB_MEMORY_COLLECTION`:

```powershell
dotnet run --project samples\MemoryQuickstart\MemoryQuickstart.csproj
```

The sample uses a deterministic three-dimensional demonstration embedding
generator; replace it for production. It explicitly creates a Vector Search
index and prints the closest stored message (or `No memory found.`). It leaves
the configured collection intact; remove that collection when finished if it
is dedicated to the sample. The MongoDB principal therefore needs collection
read/write and Search index-management privileges. See the
[.NET Memory developer guide](../docs/development/memory/dotnet-memory.md) and
[implementation specifications](../docs/spec/README.md).

See [Status](#status) above for packaging readiness and publishing blockers.

## Exact Chat History

`MongoDBChatHistoryProvider : ChatHistoryProvider` stores lossless, ordered
`ChatMessage` payloads for one immutable application/agent/session authorization
scope. It uses Agent Framework's public JSON serialization and delegates lifecycle
filtering, merging, and source attribution to the framework base provider.

```csharp
await using var history = new MongoDBChatHistoryProvider(
    collection,
    new MongoDBChatHistoryProviderOptions
    {
        ApplicationId = "my-app",
        AgentId = "assistant",
        SessionId = "session-123",
        MaxMessages = 100,
    });

await history.EnsureIndexesAsync();
await history.SaveMessagesAsync(
    "session-123",
    [new ChatMessage(ChatRole.User, "Hello") { MessageId = "message-1" }]);
IReadOnlyList<ChatMessage> messages =
    await history.GetMessagesAsync("session-123");
```

`EnsureIndexesAsync` is the only mutating provisioning operation. Runtime history
does not use MongoDB Search or the Memory collection. `ClearMessagesAsync` rejects
any session other than the configured authorization scope. Unknown stored versions
fail with migration guidance. History schema version 2 adds canonical scope
discrimination and is a breaking authorization-boundary change; version 1
collections require migration before replay or index provisioning.

Run the sample after setting `MONGODB_URI` and `MONGODB_DATABASE`:

```powershell
dotnet run --project samples\HistoryQuickstart\HistoryQuickstart.csproj
```

Optional variables are `MONGODB_HISTORY_COLLECTION`,
`MONGODB_HISTORY_APPLICATION_ID`, `MONGODB_HISTORY_AGENT_ID`, and
`MONGODB_HISTORY_SESSION_ID`. Set `MONGODB_HISTORY_CLEAR=true` only when the
sample's authorized session should be removed. See the
[.NET Chat History developer guide](../docs/development/history/dotnet-history.md).

## Session Store

`Microsoft.Agents.AI.Abstractions` (verified 1.13.0 through 1.16.0; see
[contract verification](../docs/development/persistence/dotnet-contract-research.md))
exposes no public session-hosting persistence contract, so
`MongoDBAgentSessionStore` is a **compatibility-blocked, non-1.0-complete**
facade over the public `AIAgent.SerializeSessionAsync`/`DeserializeSessionAsync`
serialization surface rather than an implementation of a framework interface --
there is none to implement yet. Every constructor validates the resolved
`Microsoft.Agents.AI.Abstractions` assembly version against the verified range
`[1.13.0, 1.17.0)` and fails closed (`MongoDBConfigurationException`) for any
other resolved version, and the `PackageReference` itself is pinned to that
same range.

```csharp
await using var store = new MongoDBAgentSessionStore(
    collection,
    new MongoDBAgentSessionStoreOptions
    {
        ApplicationId = "my-app",
        AgentId = "assistant",
        DefaultExpiration = TimeSpan.FromDays(30),
    });

await store.EnsureIndexesAsync();

MongoDBAgentSessionRecord created = await store.CreateAsync("session-123", session, agent);

MongoDBAgentSessionRecord? loaded = await store.GetAsync("session-123", agent);

// Optimistic compare-and-swap: throws MongoDBConcurrencyException on a real
// conflict; a retried, already-applied write converges instead of throwing
// (identical content, and either an identical explicit expiry or -- when
// expiresAt is omitted and DefaultExpiration is configured -- a still-future
// persisted expiry that is not extended).
MongoDBAgentSessionRecord updated = await store.SetAsync(
    "session-123", session, agent, expectedVersion: loaded!.Version);

await store.DeleteAsync("session-123", expectedVersion: updated.Version);
```

Every stored document is a single versioned snapshot (not a message log): a
canonical application/agent/session scope, optional tenant/user scope, an
incrementing `version` for compare-and-swap, optional `expires_at` backed by
an explicit TTL index, and the complete framework-serialized session --
persisted as the public serializer's exact UTF-8 JSON bytes wrapped verbatim
in a BSON `Binary` field, never re-parsed through `BsonDocument` -- so unknown
or future `AgentSessionStateBag` entries (including numeric literals beyond
double precision) round-trip byte-for-byte. `CreateAsync` and `SetAsync` never
silently last-write-wins: a genuine conflict always throws
`MongoDBConcurrencyException`, while a retried call whose payload is already
durably stored converges instead of erroring. The expiry half of that
comparison depends on how it was derived: an explicit caller `expiresAt` must
match the stored value exactly, and a *different* explicit intended expiry is
a genuine conflict, not silently converged; a default-derived expiry (no
`expiresAt` supplied, `DefaultExpiration` configured) is instead recomputed
from "now" on every call, so a retry converges whenever the persisted
`expires_at` is still non-null and in the future, and the retry never extends
it -- only a genuine content change gets a freshly computed default expiry.
`session_id` is opaque and never trimmed -- only null/empty/whitespace-only
values are rejected, so leading/trailing-space session IDs remain distinct and
independently reachable. `EnsureIndexesAsync` is the only mutating
provisioning operation; `ValidateIndexesAsync` is read-only. `ListAsync`
excludes sessions whose `expires_at` has already passed.

Every `CreateAsync`/`SetAsync`/`DeleteAsync` mutation filter also requires the
stored document's `schema_version`/`framework_version` to match this build's
supported markers. A scoped record that exists but carries an incompatible
marker is detected read-only, before any mutation is attempted, and raises a
migration exception distinct from a not-found result or a compare-and-swap
conflict; see the
[.NET Session Store migration guide](../docs/development/persistence/dotnet-session-store-migration.md)
for the required manual remediation (there is no automated migration).

Injected clients, databases, and collections remain caller-owned; only a
client created by the connection-string constructor is disposed by the store,
including when a later construction step (such as resolving the
database/collection) fails after the client was created.

Run the sample after setting `MONGODB_URI` and `MONGODB_DATABASE`:

```powershell
dotnet run --project samples\SessionPersistenceQuickstart\SessionPersistenceQuickstart.csproj
```

Optional variables are `MONGODB_SESSION_COLLECTION`,
`MONGODB_SESSION_APPLICATION_ID`, `MONGODB_SESSION_AGENT_ID`, and
`MONGODB_SESSION_ID`. Set `MONGODB_SESSION_CLEAR=true` only when the sample's
authorized session should be removed. The MongoDB principal needs collection
read/write privileges, plus index-management privileges to run
`EnsureIndexesAsync`. No Python Session Store exists yet; see the
[implementation map](../docs/spec/implementation-map.md) for cross-language
sequencing. See the
[.NET Session Store developer guide](../docs/development/persistence/dotnet-session-store.md),
the
[.NET Session Store contract verification](../docs/development/persistence/dotnet-contract-research.md),
and the
[.NET Session Store migration guide](../docs/development/persistence/dotnet-session-store-migration.md).


## Workflow Checkpoint Store

`Microsoft.Agents.AI.Workflows` (verified 1.13.0 through 1.16.0; see
[contract verification](../docs/development/persistence/dotnet-checkpoint-contract-research.md))
publishes a real public checkpoint-storage extension point,
`Microsoft.Agents.AI.Workflows.Checkpointing.JsonCheckpointStore`, and
`MongoDBCheckpointStore` derives from it directly and implements all three
required abstract hooks. Every constructor validates the resolved
`Microsoft.Agents.AI.Workflows` assembly version against the verified range
`[1.13.0, 1.17.0)` and fails closed (`MongoDBConfigurationException`) for any
other resolved version, and the `PackageReference` itself is pinned to that
same range.

```csharp
byte[] signingKey = Convert.FromBase64String(
    Environment.GetEnvironmentVariable("MONGODB_CHECKPOINT_SIGNING_KEY")!); // >= 32 random bytes; see below

await using var store = new MongoDBCheckpointStore(
    collection,
    new MongoDBCheckpointStoreOptions
    {
        WorkflowId = "my-workflow",
        ContinuationTokenSigningKey = signingKey,
        DefaultExpiration = TimeSpan.FromDays(30),
    });

await store.EnsureIndexesAsync();

// CheckpointManager.CreateJson accepts MongoDBCheckpointStore as a real
// ICheckpointStore<JsonElement> -- a drop-in JsonCheckpointStore.
CheckpointManager manager = CheckpointManager.CreateJson(store);

CheckpointInfo root = await store.CreateCheckpointAsync("run-7", payload);
CheckpointInfo next = await store.CreateCheckpointAsync("run-7", nextPayload, root);

// Explicit, cancellable facade with a caller-supplied checkpoint id:
MongoDBCheckpointRecord saved = await store.SaveCheckpointAsync(
    "run-7", "checkpoint-42", payload, parentCheckpointId: root.CheckpointId);

MongoDBCheckpointRecord? latest = await store.GetLatestCheckpointAsync("run-7");
MongoDBCheckpointPage page = await store.ListCheckpointsAsync("run-7", limit: 100);
await store.DeleteCheckpointAsync("run-7", "checkpoint-42");
```

Checkpoints are **immutable historical records** stored in a collection and
document `doc_type` kept entirely separate from Session Store's session
documents. A canonical tenant (optional)/workflow (required) authorization
scope is applied to every query before any sort, limit, or delete. Each
checkpoint carries a monotonically, atomically allocated `sequence` number
that establishes commit order independent of wall-clock timestamps --
`GetLatestCheckpointAsync` and pagination always order by `sequence`, never
`created_at`. Sequence allocation and the checkpoint write commit together
inside one MongoDB transaction (`collection.Database.Client.StartSessionAsync`
+ `IClientSessionHandle.WithTransactionAsync`), so concurrent writers for the
same session genuinely serialize on the shared sequence counter and no two
checkpoints ever observe the same sequence, and a duplicate idempotent retry
never burns a sequence value. This requires a deployment that supports
multi-document transactions (a replica set, sharded cluster, or `mongos`); a
standalone `mongod` rejects transaction usage, and `SaveCheckpointAsync`/
`CreateCheckpointAsync` fail with `MongoDBCapabilityException` rather than
silently giving up the ordering guarantee. The exact framework-produced
checkpoint JSON payload is stored as the serializer's exact UTF-8 bytes
wrapped verbatim in a BSON `Binary` field, never re-parsed through
`BsonDocument`, so unusual numeric literals round-trip byte-for-byte.

Saving under an already-used checkpoint identifier with identical payload
bytes and identical parent lineage converges (idempotent retry, no new
sequence allocated, `expires_at` never extended); saving with a *different*
payload or a *different* parent throws `MongoDBConcurrencyException` -- a
real conflict against an immutable record is never silently overwritten.
Branched lineage (multiple children of the same parent) is fully supported
and independently retrievable. Every document identifier, cache key, and
signed payload is built from length-prefixed binary framing of its
components (never delimiter-joined text), so an opaque caller-controlled
identifier containing any character -- including one this store could
otherwise have chosen as a delimiter -- can never collide with a different
logical identity. `ListCheckpointsAsync` is bounded per call and returns an
opaque, scoped, versioned, tamper-rejecting continuation token for the next
page; a token from a different tenant/workflow scope, or one that has been
altered, is rejected with `MongoDBConfigurationException` rather than
silently returning wrong-scope or skipped data. Continuation tokens are
HMAC-SHA256-signed with the required, server-held
`MongoDBCheckpointStoreOptions.ContinuationTokenSigningKey` (at least 32
cryptographically random bytes, generated for example with
`RandomNumberGenerator.GetBytes(32)`, loaded from a secret manager or
protected environment variable, and kept stable and identical across every
store instance that must accept each other's tokens) -- the key is combined
with this store's own tenant/workflow scope for domain separation and is
never derived from a token's own contents, so a token cannot be forged or
replayed across a differently scoped store without knowledge of the secret.
`ContinuationTokenSigningKey` is excluded from `MongoDBCheckpointStoreOptions.ToString()`
so it is never accidentally logged.

The raw framework hooks (`CreateCheckpointAsync`, `RetrieveCheckpointAsync`,
`RetrieveIndexAsync`) accept no `CancellationToken` -- a real, verified
`JsonCheckpointStore` contract constraint, not a design choice -- so
`MongoDBCheckpointStore` additionally exposes a richer, explicitly
cancellable facade (`SaveCheckpointAsync`, `LoadCheckpointAsync`,
`GetLatestCheckpointAsync`, `ListCheckpointsAsync`, `DeleteCheckpointAsync`)
sharing the same internal storage core. `RetrieveCheckpointAsync` throws
`KeyNotFoundException` when a checkpoint is absent (matching
`ICheckpointManager`'s documented convention); `LoadCheckpointAsync` instead
returns `null`. `CreateCheckpointAsync` still applies the configured
`PersistenceTimeout` even though the base contract gives it no
`CancellationToken` to observe an external one -- a hung write fails with a
stable `MongoDBTimeoutException` rather than blocking the caller indefinitely.

Every save/load/delete filter also requires the stored document's
`schema_version` to match this build's supported constant. A scoped
checkpoint that exists but carries an incompatible marker is detected
read-only, before any mutation is attempted, and raises a migration
exception; see the
[.NET Workflow Checkpoint Store migration guide](../docs/development/persistence/dotnet-checkpoint-store-migration.md)
for the required manual remediation (there is no automated migration).

Injected clients, databases, and collections remain caller-owned; only a
client created by the connection-string constructor is disposed by the
store, including when a later construction step fails after the client was
created.

Run the sample after setting `MONGODB_URI`, `MONGODB_DATABASE`, and
`MONGODB_CHECKPOINT_SIGNING_KEY` (a base64-encoded, at least 32-byte
cryptographically random secret, for example generated with
`openssl rand -base64 32`):

```powershell
dotnet run --project samples\WorkflowCheckpointResumeQuickstart\WorkflowCheckpointResumeQuickstart.csproj
```

Optional variables are `MONGODB_CHECKPOINT_COLLECTION`,
`MONGODB_CHECKPOINT_WORKFLOW_ID`, `MONGODB_CHECKPOINT_TENANT_ID`, and
`MONGODB_CHECKPOINT_SESSION_ID`. Set `MONGODB_CHECKPOINT_CLEAR=true` only when
the sample's checkpoints should be removed. The MongoDB principal needs
collection read/write privileges (including `update`/`findAndModify` for the
sequence-counter document), plus index-management privileges to run
`EnsureIndexesAsync`, and the target deployment must support multi-document
transactions (a replica set, sharded cluster, or `mongos`). No Python
Workflow Checkpoint Store exists yet; see the
[implementation map](../docs/spec/implementation-map.md) for cross-language
sequencing. See the
[.NET Workflow Checkpoint Store developer guide](../docs/development/persistence/dotnet-checkpoint-store.md),
the
[.NET Workflow Checkpoint Store contract verification](../docs/development/persistence/dotnet-checkpoint-contract-research.md),
and the
[.NET Workflow Checkpoint Store migration guide](../docs/development/persistence/dotnet-checkpoint-store-migration.md).



## RAG contracts, typed filters, Vector Search (ANN/ENN), FullText, and HybridRrf

`MongoDBSearchMode` (`VectorAnn`, `VectorEnn`, `FullText`, `HybridRrf`), the bounded typed `MongoDBRAGFilter` AST,
the immutable `MongoDBRAGResult`, and `MongoDBRAGProviderOptions` are available under
`dotnet/src/MongoDB.AgentFramework/RAG/`. `MongoDBRAGFilter` is created only through static factories
(`Equal`, `NotEqual`, `In`, `NotIn`, `Range`, `And`, `Or`) with bounded nesting depth and value counts, and is
completely translatable into a `$vectorSearch` match filter or a `$search` compound filter through the internal
`RAGFilterTranslator`.

`MongoDBRAGProvider` executes live `VectorAnn`/`VectorEnn`/`FullText`/`HybridRrf` retrieval through `SearchAsync`,
and `MongoDBRAGContextProvider` composes it as a before-invoke `AIContextProvider` that supplies retrieved chunks as
attributed `ChatRole.Tool` context messages. `HybridRrf` uses MongoDB's native `$rankFusion` stage to combine a
Vector Search ANN input and a Search text input (weighted reciprocal-rank fusion) and requires **both** an
embedding generator/dimensions and Search index/field configuration; `ValidateHybridSearchCapabilityAsync` is an
opt-in seam validating MongoDB 8.0+ and both indexes without ever being called implicitly by `SearchAsync`.
`FullText` never requires or invokes an embedding generator: a dedicated constructor overload family
(`MongoDBRAGProvider(database, collectionName, options, ...)`, and the matching collection/client/connection-string
overloads) accepts no `embeddingGenerator`/`vectorDimensions` parameters at all.

```csharp
MongoDBRAGFilter filter = MongoDBRAGFilter.And(
    MongoDBRAGFilter.Equal("tenant_id", "tenant-a"),
    MongoDBRAGFilter.In("category", ["news", "docs"]));

var vectorOptions = new MongoDBRAGProviderOptions
{
    SearchMode = MongoDBSearchMode.VectorAnn,
    VectorIndexName = "knowledge_vector_index",
    VectorFieldName = "embedding",
    TopK = 5,
    MandatoryFilter = filter,
};

await using var rag = new MongoDBRAGProvider(
    database,
    "knowledge_chunks",
    embeddingGenerator,
    vectorDimensions: 1536,
    vectorOptions);

IReadOnlyList<MongoDBRAGResult> results = await rag.SearchAsync("What color do widgets ship in?");

var contextProvider = new MongoDBRAGContextProvider(rag);

// FullText: no embedding generator required.
var fullTextOptions = new MongoDBRAGProviderOptions
{
    SearchMode = MongoDBSearchMode.FullText,
    SearchIndexName = "knowledge_search_index",
    SearchTextFieldNames = ["text"],
    TopK = 5,
    MandatoryFilter = filter,
};

await using var fullTextRag = new MongoDBRAGProvider(database, "knowledge_chunks", fullTextOptions);
IReadOnlyList<MongoDBRAGResult> fullTextResults = await fullTextRag.SearchAsync("What color do widgets ship in?");

// HybridRrf: native $rankFusion over both a Vector Search input and a Search input; requires both an embedding
// generator/dimensions and Search index/field configuration.
var hybridOptions = new MongoDBRAGProviderOptions
{
    SearchMode = MongoDBSearchMode.HybridRrf,
    VectorIndexName = "knowledge_vector_index",
    SearchIndexName = "knowledge_search_index",
    SearchTextFieldNames = ["text"],
    TopK = 5,
    MandatoryFilter = filter,
};

await using var hybridRag = new MongoDBRAGProvider(
    database, "knowledge_chunks", embeddingGenerator, vectorDimensions: 1536, hybridOptions);
IReadOnlyList<MongoDBRAGResult> hybridResults = await hybridRag.SearchAsync("What color do widgets ship in?");
```

This slice does not provision Vector Search or Search indexes itself; the target index/indexes must already exist
before `MongoDBRAGProvider` connects. See [Index Management](#index-management) below for the separate facade that
provisions them. Injected clients/databases/collections/embedding generators remain caller-owned; only a client
created by the connection-string constructor is disposed by the provider.

Run the sample after setting `MONGODB_URI`, `MONGODB_DATABASE`, and a pre-provisioned Vector Search index
(`MONGODB_RAG_VECTOR_INDEX`, optionally `MONGODB_RAG_COLLECTION`). Additionally set `MONGODB_RAG_SEARCH_INDEX` to a
pre-provisioned Search index to also see the FullText and HybridRrf demonstrations (both skipped otherwise;
HybridRrf additionally requires a MongoDB 8.0+ deployment):

```powershell
dotnet run --project samples\RAGQuickstart\RAGQuickstart.csproj
```

See the [.NET RAG contracts developer guide](../docs/development/rag/dotnet-rag.md), the
[.NET Vector RAG developer guide](../docs/development/rag/dotnet-rag-vector-search.md), the
[.NET FullText RAG developer guide](../docs/development/rag/dotnet-rag-full-text-search.md), and the
[.NET HybridRrf RAG developer guide](../docs/development/rag/dotnet-rag-hybrid-rrf.md) for the full public
surface, pipeline shape, and deferred work.

## Index Management

`MongoDBMemoryIndexManager` and `MongoDBRAGIndexManager` (`dotnet/src/MongoDB.AgentFramework/Memory/` and
`.../RAG/`) are explicit, feature-specific facades over the same shared internal index mechanics
`MongoDBMemoryProvider`/`MongoDBRAGProvider` use, independently constructible from a database, collection, client,
or connection string without requiring a provider or an embedding generator. They exist to keep the "provisioner"
role (creating, updating, waiting for, and dropping indexes) operationally and privilege-separate from the
"runtime" role a running provider plays (ADR
[0006](../docs/decisions/0006-make-index-provisioning-explicit.md)/[0016](../docs/decisions/0016-keep-index-facades-in-runtime-packages.md)):

```csharp
var definition = new MongoDBVectorSearchIndexDefinition(
    indexName: "agent_framework_memory",
    vectorFieldName: "content_embedding",
    vectorDimensions: 1536,
    filterFieldPaths: ["application_id", "agent_id", "user_id", "session_id"]);

// Provisioner: run under a distinct, more privileged identity than the runtime provider connects with.
await using var provisioner = new MongoDBMemoryIndexManager(client, "my_database", "memories", definition);
await provisioner.EnsureIndexAsync(waitUntilReady: true, timeout: TimeSpan.FromMinutes(5));

// Runtime: read-only validation only -- never creates, updates, or drops.
await using var runtime = new MongoDBMemoryIndexManager(client, "my_database", "memories", definition);
MongoDBIndexComparison comparison = await runtime.ValidateIndexAsync();
```

Every `Get*`/`List*`/`Validate*` method never mutates MongoDB; only `Create*`/`Ensure*`/`Update*`/`Drop*` do, and only
when explicitly called -- never from a constructor or a framework lifecycle hook. `Create*` is a strict, non-idempotent
create-only operation (throws `MongoDBIndexAlreadyExistsException` if the index already exists); `Ensure*` is the
idempotent reconciliation operation shown above (creates if missing, updates if mismatched) and is what a deployment
retry loop should call. Both always re-inspect and validate the index's final state after any create/update attempt,
so a rival concurrent creator winning with an incompatible definition is still caught. Comparison is semantic and
order-insensitive, and distinguishes an actionable mismatch (`MongoDBIndexComparison.Mismatches`) from a merely
informational compatible difference (`CompatibleDifferences`). A terminal `Failed` build is never automatically
retried or repaired -- it surfaces immediately as `MongoDBIndexFailedException`, never enters the polling loop, and
`Ensure*` never treats it as something to auto-update. `WaitUntilReadyAsync`/`Ensure*(waitUntilReady: true)` poll
with a bounded, cancellable exponential backoff whose per-attempt deadline bounds even a hung underlying call,
distinguishing the caller's own cancellation from the bounded timeout. A connected identity lacking
index-management privileges raises `MongoDBIndexPrivilegeException` distinctly from a generic deployment error.

**Least privilege:** runtime identities (what `MongoDBMemoryProvider`/`MongoDBRAGProvider` connect with) should only
ever need collection read/write/aggregate plus Search query permissions -- never `createSearchIndexes`/
`updateSearchIndexes`/`dropSearchIndexes`. Reserve those index-management privileges for a separate provisioner
identity that runs the `Ensure*`/`Update*`/`Drop*` calls, typically as a deployment-pipeline step. Exact
built-in/custom MongoDB roles must still be verified against the target deployment before package publication.

Run the sample after setting `MONGODB_URI` and `MONGODB_DATABASE` (optionally `MONGODB_MEMORY_COLLECTION` and
`MONGODB_RAG_COLLECTION`):

```powershell
dotnet run --project samples\IndexManagementQuickstart\IndexManagementQuickstart.csproj
```

The sample constructs separate provisioner and runtime facade instances side by side over both a Memory Vector
Search index and a RAG Hybrid (Vector Search + Search) index pair, then drops all three indexes at the end. See the
[.NET Index Management developer guide](../docs/development/index-management/dotnet-index-management.md).

## Ingestion samples (sample-only, not part of the runtime package)

`dotnet/samples/IngestionSamples` is a **sample-only** class library (`MongoDB.AgentFramework.Samples.Ingestion`,
`IsPackable=false`) demonstrating deterministic, incremental, tenant-scoped knowledge ingestion and the
parent-document RAG pattern, per docs/spec/features/ingestion.md's "Knowledge ingestion and bootstrap boundary". It
adds **no public type to `MongoDB.AgentFramework`**; `dotnet pack` on the runtime project never includes any
ingestion sample type. It calls the same public `IEmbeddingGenerator<string, Embedding<float>>` abstraction and the
existing `MongoDBRAGProvider`/`MongoDBRAGIndexManager` for querying and provisioning, rather than duplicating them.

`IncrementalIngestionPipeline` (flat chunk schema) and `ParentDocumentIngestionPipeline` (parent + embedded child
chunk schema) share the same reconciliation shape: `DocumentChunker` produces bounded, overlap-configurable,
non-empty/non-duplicate chunks; `DeterministicId`/`ContentHash` derive stable IDs and change-detecting hashes from
canonical source identity via `CanonicalFraming`'s unambiguous length-prefixed binary encoding (never delimiter
concatenation, and never a random GUID or timestamp); `BatchEmbedder` embeds only new/changed text in bounded batches
with dimension/finite-value validation; and `IChunkStore.UpsertAsync`/`DeleteAsync` reconcile unchanged (skipped),
changed (re-embedded and upserted), and stale (deleted) records. `MongoChunkStore`'s upsert/delete filters always
match `_id` **and** `tenant_id` **and** `source_id` (upsert additionally matches `record_type`), so even an accidental
or hash-collided `_id` can never cross a tenant/source/record-type scope. `IChunkStore.DeleteSourceAsync` deletes an
entire source's records in bounded pages, and `SourceManifestReconciler` compares a caller-supplied "currently known
sources" manifest against `ListSourceIdsAsync` to tombstone whole sources that have disappeared from the corpus.
Cancellation propagates through every read/embed/write/cleanup step.

`ParentDocumentRetriever` performs the parent-document RAG pattern's retrieval half: a child-only
`IChildChunkSearcher.SearchAsync` (backed by `MongoDBRAGChildChunkSearcher` over an existing `MongoDBRAGProvider`
constrained to child records), then one bounded, de-duplicated, tenant-scoped `IParentLookup.FindParentsAsync` call
hydrating at most `maxParents` distinct best-scoring parents with source attribution -- never a per-child lookup,
unbounded fan-out, or caller-suppliable pipeline callback. An optional `ParentContextBoundingOptions`
(`MaxCharactersPerParent`, `MaxTotalContextCharacters`) truncates each returned parent's content and the total
returned context deterministically, after ordering/de-duplication/attribution are finalized, without ever splitting
a UTF-16 surrogate pair.

Run the samples after setting `MONGODB_URI` and `MONGODB_DATABASE`:

```powershell
dotnet run --project samples\IncrementalIngestionQuickstart\IncrementalIngestionQuickstart.csproj
dotnet run --project samples\ParentDocumentRAGQuickstart\ParentDocumentRAGQuickstart.csproj
```

Neither sample accepts a user-supplied Vector Search index name. Each generates its own unique, sample-prefixed
index name at startup (e.g. `agent_framework_sample_incr_<guid>` / `agent_framework_sample_pd_<guid>`), provisions
it via `MongoDBRAGIndexManager.EnsureVectorSearchIndexAsync(waitUntilReady: true)` only if it doesn't already exist,
and tracks whether this run created it so cleanup never drops a pre-existing or externally configured index.
`IncrementalIngestionQuickstart` runs four steps against the same tenant: three sequential ingestions of one source
(new, unchanged, changed+stale-deleted) printing upserted/unchanged/deleted counts, then a fourth run that ingests a
second source and demonstrates `SourceManifestReconciler` tombstoning it once a subsequent manifest omits it; a
`finally` block always cleans up both sources and drops the index only if this run created it.
`ParentDocumentRAGQuickstart` ingests a parent+child document, searches and hydrates the parent via
`ParentDocumentRetriever`, and likewise always cleans up its own data and drops the index only if this run created
it. Both samples require collection read/write and Search index-management privileges, and use a deterministic
demonstration embedding generator; replace it for any real embedding model. See the
[.NET Ingestion samples developer guide](../docs/development/ingestion/dotnet-ingestion-samples.md).
