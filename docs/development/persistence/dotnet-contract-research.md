# .NET Session Store contract verification

This note records the primary-source, reflection-based verification performed on
2026-08-04 for [Session Store](../../spec/features/persistence.md) and slice 16 of
the [implementation map](../../spec/implementation-map.md). The
[persistence specification](../../spec/features/persistence.md) and
[ADR 0018 (version-gate persistence contracts)](../../decisions/0018-version-gate-persistence-contracts.md)
remain normative; this note documents the exact compatibility finding that
triggered the version-gated facade design used by `MongoDBAgentSessionStore`.

## Question

Does the resolved `Microsoft.Agents.AI.Abstractions` version expose a public
session-hosting persistence contract (an interface or abstract base a MongoDB
implementation could implement, analogous to a `ChatMessageStore` for chat
history) that `MongoDBAgentSessionStore` should implement directly instead of a
narrower facade?

## Resolved version

`dotnet/src/MongoDB.AgentFramework/MongoDB.AgentFramework.csproj` pins
`Microsoft.Agents.AI.Abstractions` to the range `[1.13.0,2.0.0)`. NuGet range
resolution (verified in `obj/project.assets.json` after `dotnet restore`)
selects the **lowest** version satisfying an open range absent a floating
(`1.13.*`) specifier or a higher transitive constraint elsewhere in the
dependency graph, so the version this project actually builds and ships against
is **1.13.0**, not the newest version published to NuGet.

## Method

1. Resolved `Microsoft.Agents.AI.Abstractions` 1.13.0 from the configured NuGet
   feed (`azure-default` → `https://packagefeedproxy.microsoft.io/nuget/v3/index.json`,
   proxying an Azure DevOps Artifacts feed) and loaded the assembly directly with
   `Assembly.LoadFrom`, resolving its `Microsoft.Extensions.AI.Abstractions`
   10.6.0 dependency through an `AssemblyResolve` handler.
2. Enumerated `GetExportedTypes()` and filtered for `Session|Persist|Store|Checkpoint|Host`.
3. Enumerated the full 30-type exported surface of the assembly with no filter,
   to confirm no differently-named session-hosting type exists.
4. Inspected member signatures (constructors, methods, XML doc comments) of
   every session-related type found, and of `AIAgent`.
5. Repeated steps 1-4 against the newest version published at research time,
   **1.16.0** (downloaded and extracted directly from the NuGet feed), to
   confirm the finding was not specific to the pinned floor and would not
   silently change if the version range were widened.

## Finding

`Microsoft.Agents.AI.Abstractions` 1.13.0 through 1.16.0 (verified: identical
exported-type set and DLL size at both ends of the checked range) exposes only
the following session-related public types, and **no session-hosting
persistence contract**:

| Type | Role |
| --- | --- |
| `AgentSession` | Abstract base for agent-defined session state. |
| `AgentSessionStateBag` | Keyed bag of JSON-serializable session state (`SetValue<T>`/`GetValue<T>`/`TryGetValue<T>`, `Serialize()`/static `Deserialize(JsonElement)`). `T` must be a reference type. |
| `AgentSessionExtensions` | Extension helpers over `AgentSession`. |
| `AgentSessionStateBagJsonConverter` | `System.Text.Json` converter used internally by the bag. |
| `ProviderSessionState<T>` | Per-provider typed state slot within a session. |

There is no `ISessionStore`, `IAgentSessionStore`, `ISessionHost`, or any other
interface a MongoDB (or any other) storage implementation could implement to
participate in agent session hosting. The only public serialization surface for
a complete session is declared on `AIAgent` itself, not on `AgentSession` or any
free-standing serializer:

```csharp
// Microsoft.Agents.AI.AIAgent
public ValueTask<JsonElement> SerializeSessionAsync(
    AgentSession session,
    JsonSerializerOptions? jsonSerializerOptions = null,
    CancellationToken cancellationToken = default);

public ValueTask<AgentSession> DeserializeSessionAsync(
    JsonElement serializedSession,
    JsonSerializerOptions? jsonSerializerOptions = null,
    CancellationToken cancellationToken = default);
```

Both are **instance methods on the originating `AIAgent`**, not static or
free-standing. Their XML documentation confirms they are the intended public
serialization surface ("Serializes an agent session to its JSON
representation."). This is a real, load-bearing framework design constraint:
`AgentSession`'s JSON shape is agent-defined (each agent implementation decides
what state to carry and how to shape it), so no agent-independent serializer
can exist. Any consumer of `MongoDBAgentSessionStore` must supply the
originating `AIAgent` instance to load or save a session.

Primary sources:

- [Microsoft.Agents.AI.Abstractions NuGet registration](https://api.nuget.org/v3/registration5-semver1/microsoft.agents.ai.abstractions/index.json)
- Reflection over the installed 1.13.0 and downloaded 1.16.0
  `Microsoft.Agents.AI.Abstractions.dll` (methodology above; no third-party
  documentation substitutes for the shipped binary and its embedded XML docs).

## Decision

Per [ADR 0018](../../decisions/0018-version-gate-persistence-contracts.md),
because no public session-hosting contract exists in the resolved and verified
version range, `MongoDBAgentSessionStore` does **not** implement any Agent
Framework interface (there is none to implement) and does **not** invent one.
Instead it is a narrow, standalone facade over `AIAgent.SerializeSessionAsync`
/ `DeserializeSessionAsync`:

- The store's public methods (`GetAsync`, `CreateAsync`, `SetAsync`) accept an
  `AIAgent` parameter used solely to (de)serialize the session payload; the
  store performs no other agent invocation.
- Storage, authorization, optimistic concurrency, TTL, and indexing are handled
  entirely by `MongoDBAgentSessionStore` against a stable BSON envelope; only
  the `session` sub-document's shape is agent-defined and treated as opaque by
  the store (parsed and stored losslessly, never inspected or mapped field by
  field).
- The internal `Internal.Persistence.IAgentSessionCodec` seam isolates the
  "serialize/deserialize a session" concern from the rest of the store. If a
  future `Microsoft.Agents.AI.Abstractions` version publishes a real
  session-hosting contract, a new `IAgentSessionCodec` implementation (or a
  parallel adapter type built on the same BSON envelope) can be added without
  changing the store's storage schema, its BSON envelope, or any already-stored
  documents.
- If a future package version changes the `AgentSession` JSON shape in an
  incompatible way, `MongoDBAgentSessionStore` will still refuse to load
  mismatched documents: every stored envelope carries `schema_version` and
  `framework_version` markers, and loading a document that does not match the
  version this build understands throws `MongoDBMappingException` rather than
  attempting a lossy or silent migration.

This decision will be revisited if a later `Microsoft.Agents.AI.Abstractions`
release publishes a session-hosting contract; re-run the reflection
methodology above against the newly resolved version before adding an adapter.
