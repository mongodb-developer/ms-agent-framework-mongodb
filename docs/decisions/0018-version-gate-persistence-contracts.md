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
`MongoDBAgentSessionStore` through the supported public Agent Framework session-hosting contract and
`MongoDBCheckpointStore(JsonCheckpointStore)`. Neither language serializes internal runtime objects independently.

### Consequences

- Good, because stored state is tied to tested public serializers and explicit compatibility gates.
- Bad, because unsupported framework versions must be rejected rather than accepted on a best-effort basis.

## Validation

Package metadata and documentation must declare supported framework versions. Both languages must pass public
serialization, unknown-version rejection, concurrency, lineage, resumption, and migration-guidance tests before the
corresponding implementation gate closes.
