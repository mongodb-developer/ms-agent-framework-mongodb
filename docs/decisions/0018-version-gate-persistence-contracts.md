---
status: proposed
contact: sgsshankar
date: 2026-07-31
deciders: [sgsshankar]
consulted: [Microsoft Agent Framework maintainers]
informed: [Contributors]
---

# Version-gate persistence contracts

## Context and Problem Statement

Persistence stores serialize complete framework state, so dependency compatibility and public serialization contracts
are release-critical. Implementations against internal framework state would create migration and data-loss risk.

## Considered Options

- Ship persistence through canonical package surfaces tied to verified supported public contracts.
- Implement adapters against internal contracts.
- Implement repository-specific session and checkpoint abstractions.

## Decision Outcome

Chosen option: "Ship persistence through canonical package surfaces tied to verified supported public contracts."
Python provides `MongoDBSessionStore(SessionStore)` and `MongoDBCheckpointStorage(CheckpointStorage)`. .NET provides
`MongoDBAgentSessionStore` and `MongoDBCheckpointStore(JsonCheckpointStore)`. Neither language serializes internal
runtime objects independently.

Reflection-based verification against `Microsoft.Agents.AI.Abstractions` 1.13.0 through 1.16.0 (the pinned and
currently resolved range; see
[dotnet-contract-research.md](../development/persistence/dotnet-contract-research.md)) found **no public
session-hosting persistence contract** for .NET to implement -- only `AgentSession`, `AgentSessionStateBag`, and
`AIAgent.SerializeSessionAsync`/`DeserializeSessionAsync`. Consistent with this ADR's chosen option (verified
supported public contracts only, never an invented or internal one), `MongoDBAgentSessionStore` does not implement
any Agent Framework interface. It is a standalone, version-gated facade over `AIAgent.SerializeSessionAsync`/
`DeserializeSessionAsync`, isolated behind the internal `IAgentSessionCodec` seam so a real adapter can be added
later, against a genuine session-hosting contract, without changing the storage schema or any already-stored
documents. This gate must be re-verified against the newly resolved version before adding such an adapter.

### Consequences

- Good, because stored state is tied to tested public serializers and explicit compatibility gates.
- Bad, because unsupported framework versions must be rejected rather than accepted on a best-effort basis.
- Bad, because the .NET Session Store cannot be plugged into automatic framework session-hosting lifecycle
  management until a future `Microsoft.Agents.AI.Abstractions` release publishes such a contract; callers must call
  its public API directly and supply the originating `AIAgent` themselves.

## Validation

Package metadata and documentation must declare supported framework versions. Both languages must pass public
serialization, unknown-version rejection, concurrency, lineage, resumption, and migration-guidance tests before the
corresponding implementation gate closes.
