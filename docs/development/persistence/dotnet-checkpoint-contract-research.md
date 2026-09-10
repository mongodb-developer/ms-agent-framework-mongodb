# .NET Workflow Checkpoint Store contract verification

This note records the primary-source, reflection-based verification performed
on 2026-08-04 for [Workflow Checkpoint Store](../../spec/features/persistence.md)
and slice 18 of the [implementation map](../../spec/implementation-map.md). The
[persistence specification](../../spec/features/persistence.md) and
[ADR 0018 (version-gate persistence contracts)](../../decisions/0018-version-gate-persistence-contracts.md)
remain normative; this note documents the exact public extension-point
contract, the framework design gaps found in it, and the runtime version-gate
design those gaps drove for `MongoDBCheckpointStore`.
[ADR 0012](../../decisions/0012-include-session-and-checkpoint-stores.md)
records the rationale for shipping Workflow Checkpoint Store as a separate
product boundary from Session Store; this note does not revisit that
decision.

## Question

Does the resolved `Microsoft.Agents.AI.Workflows` version publish a public
checkpoint-storage extension point a MongoDB implementation can derive from,
and if so, what is its exact contract (namespace, abstract members,
cancellation support, identifier-assignment behavior, and ordering
guarantees a caller may rely on)?

## Resolved version

`dotnet/src/MongoDB.AgentFramework/MongoDB.AgentFramework.csproj` pins
`Microsoft.Agents.AI.Workflows` to the range `[1.13.0,1.17.0)` -- matching the
already-verified `Microsoft.Agents.AI.Abstractions` range used by
`MongoDBAgentSessionStore`. NuGet range resolution (verified in
`obj/project.assets.json` after `dotnet restore`) selects the **lowest**
version satisfying an open range absent a floating specifier, so the version
this project actually builds and ships against is **1.13.0**, not the newest
version published to NuGet at research time (**1.16.0**).

## Method

1. Resolved `Microsoft.Agents.AI.Workflows` 1.13.0 from the configured NuGet
   feed (`azure-default`) and inspected its dependency graph
   (`Microsoft.Extensions.Logging.Abstractions >= 10.0.9` transitively,
   requiring this package's own floor for that dependency to be raised from
   `10.0.0`).
2. Fetched the primary-source implementation from
   `github.com/microsoft/agent-framework`
   (`dotnet/src/Microsoft.Agents.AI.Workflows/{CheckpointInfo.cs,CheckpointManager.cs}`,
   `Checkpointing/{JsonCheckpointStore.cs,FileSystemJsonCheckpointStore.cs}`)
   and the Cosmos reference implementation
   (`Microsoft.Agents.AI.CosmosNoSql/CosmosCheckpointStore.cs`) and the
   framework's own contract test
   (`Microsoft.Agents.AI.Workflows.UnitTests/CheckpointManagerLatestTests.cs`).
3. Loaded the installed 1.13.0 and 1.16.0 `Microsoft.Agents.AI.Workflows.dll`
   directly with `Assembly.LoadFrom` and enumerated `CheckpointInfo`'s and
   `CheckpointManager`'s exported members with reflection at both versions,
   to confirm the source-level finding against the actually shipped binaries
   and detect any difference across the verified range.

## Finding: the abstract extension point

`Microsoft.Agents.AI.Workflows` publishes exactly one public
checkpoint-storage extension point: the abstract class
`Microsoft.Agents.AI.Workflows.Checkpointing.JsonCheckpointStore`
(`ICheckpointStore<JsonElement>`), with three abstract hooks:

```csharp
// Microsoft.Agents.AI.Workflows.Checkpointing.JsonCheckpointStore
public abstract ValueTask<CheckpointInfo> CreateCheckpointAsync(
    string sessionId, JsonElement value, CheckpointInfo? parent = null);

public abstract ValueTask<JsonElement> RetrieveCheckpointAsync(
    string sessionId, CheckpointInfo key);

public abstract ValueTask<IEnumerable<CheckpointInfo>> RetrieveIndexAsync(
    string sessionId, CheckpointInfo? withParent = null);
```

Two real, verified constraints follow directly from this signature set, both
confirmed against the reference `FileSystemJsonCheckpointStore` and
`CosmosCheckpointStore` implementations (neither accepts or threads a
`CancellationToken` through these hooks either):

- **No `CancellationToken` parameter on any of the three hooks.** A
  MongoDB-backed store cannot honor caller cancellation on this surface at
  all; `MongoDBCheckpointStore` runs these three overrides with
  `CancellationToken.None` and instead exposes a richer, explicitly
  cancellable public facade (`SaveCheckpointAsync`, `LoadCheckpointAsync`,
  `GetLatestCheckpointAsync`, `ListCheckpointsAsync`, `DeleteCheckpointAsync`)
  that delegates to the same internal storage core, so both surfaces share
  identical idempotency, lineage, and version-gate behavior.
- **`CreateCheckpointAsync` gives the caller no way to supply an explicit
  checkpoint identifier.** `MongoDBCheckpointStore.CreateCheckpointAsync`
  therefore always allocates a fresh `Guid.NewGuid().ToString("N")` -- exactly
  mirroring `FileSystemJsonCheckpointStore`'s and `CosmosCheckpointStore`'s own
  behavior -- while the facade's `SaveCheckpointAsync` accepts an explicit,
  caller-supplied `checkpointId` for direct/test/resume scenarios that need a
  known identifier.

`CheckpointInfo` (the checkpoint identity/lineage handle returned by
`CreateCheckpointAsync` and consumed by `RetrieveCheckpointAsync`/
`RetrieveIndexAsync`) and `CheckpointManager` (a convenience wrapper built on
top of `ICheckpointStore<JsonElement>`, via the static factory
`CheckpointManager.CreateJson(ICheckpointStore<JsonElement> store, ...)`) are
both declared in the **root** `Microsoft.Agents.AI.Workflows` namespace, not
`Microsoft.Agents.AI.Workflows.Checkpointing` where `JsonCheckpointStore`
itself lives -- a real namespace split that is easy to miss and that requires
both `using Microsoft.Agents.AI.Workflows;` and
`using Microsoft.Agents.AI.Workflows.Checkpointing;` side by side.

## Finding: the `JsonCheckpointStore` abstract contract is stable; `CheckpointManager` is not

The `JsonCheckpointStore` abstract contract itself (the three hooks above,
their signatures, and the no-cancellation/always-generated-ID constraints) is
**identical** at both ends of the verified range -- confirmed by reflection
over both the installed 1.13.0 and the downloaded 1.16.0
`Microsoft.Agents.AI.Workflows.dll`.

`CheckpointManager`, a separate public type layered over that contract, is
**not** identical across the same range. Reflection over
`CheckpointManager`'s exported methods found:

| Member | Present at 1.13.0 | Present at 1.16.0 |
| --- | --- | --- |
| `CreateInMemory()` | Yes | Yes |
| `Default` (property) | Yes | Yes |
| `CreateJson(ICheckpointStore<JsonElement>, JsonSerializerOptions?)` | Yes | Yes |
| `GetLatestCheckpointAsync(string, CancellationToken)` | **No** | Yes |

`GetLatestCheckpointAsync` was added to `CheckpointManager` somewhere between
1.13.0 and 1.16.0. Because this package's verified, tested floor is 1.13.0,
`MongoDBCheckpointStore` and its tests intentionally do not depend on this
method: `RetrieveIndexAsync` always returns checkpoints in ascending,
monotonic `sequence` order (never timestamp order) specifically so that any
caller -- including one restricted to the 1.13.0 floor using only
`RetrieveIndexAsync` directly -- can find the latest checkpoint as the
index's last element, matching what the newer convenience method itself is
documented to do internally. The real framework round-trip test fixture
(`MongoDBCheckpointStoreBehaviorTests.RealJsonCheckpointStoreRoundTripThroughCheckpointManagerResumesAtLatestCommittedCheckpoint`)
still constructs a real `CheckpointManager.CreateJson(store)` to prove
`MongoDBCheckpointStore` is accepted by the framework's own manager factory,
but asserts "latest" via `RetrieveIndexAsync(...).Last()` rather than the
newer method.

Primary sources:

- `github.com/microsoft/agent-framework`,
  `dotnet/src/Microsoft.Agents.AI.Workflows/{CheckpointInfo.cs,CheckpointManager.cs}`,
  `Checkpointing/{JsonCheckpointStore.cs,FileSystemJsonCheckpointStore.cs}`,
  `Microsoft.Agents.AI.CosmosNoSql/CosmosCheckpointStore.cs`,
  `Microsoft.Agents.AI.Workflows.UnitTests/CheckpointManagerLatestTests.cs`.
- Reflection over the installed 1.13.0 and downloaded 1.16.0
  `Microsoft.Agents.AI.Workflows.dll` (methodology above; no third-party
  documentation substitutes for the shipped binary).

## Decision

`MongoDBCheckpointStore` derives directly from
`Microsoft.Agents.AI.Workflows.Checkpointing.JsonCheckpointStore` and
implements all three required abstract hooks, satisfying the mapped slice's
normative public-type requirement -- unlike Session Store's interim,
compatibility-blocked facade (see
[dotnet-contract-research.md](dotnet-contract-research.md)), Workflow
Checkpoint Store has a real public extension point to implement directly.
The two verified, real gaps above (no cancellation on the abstract hooks;
`CheckpointManager.GetLatestCheckpointAsync`'s narrower version window) are
implementation constraints this store designs around, not blockers to
implementing the contract itself.

## Runtime version enforcement

Because this research is a point-in-time reflection sample, not a permanent
guarantee, the package narrows `Microsoft.Agents.AI.Workflows` to the
verified range `[1.13.0,1.17.0)` (the pinned floor through the verified
next-minor exclusive upper bound) in
`dotnet/src/MongoDB.AgentFramework/MongoDB.AgentFramework.csproj`, and
`MongoDBCheckpointStore` inspects the loaded `JsonCheckpointStore` assembly's
version at construction, rejecting any resolved version outside
`[1.13.0,1.17.0)` with a clear `MongoDBConfigurationException` naming the
detected and required versions. An internal `Func<Version>` constructor seam
lets tests inject an out-of-range version without loading multiple real
assemblies side by side. If this range is widened after re-verifying against
a newer `Microsoft.Agents.AI.Workflows` release, both the `PackageReference`
range and `MongoDBCheckpointStore`'s two version constants must be updated
together, and the `CheckpointManager` comparison table above should be
re-run against the newly resolved version.
