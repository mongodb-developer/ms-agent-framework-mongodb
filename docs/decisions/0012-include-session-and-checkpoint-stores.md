---
status: proposed
contact: sgsshankar
date: 2026-07-31
deciders: [sgsshankar]
consulted: [Microsoft Agent Framework maintainers]
informed: [Contributors]
---

# Include Session Store and Workflow Checkpoint Store

## Context and Problem Statement

Applications need complete agent-session persistence and resumable workflow checkpoints in addition to semantic
Memory and exact Chat History. Omitting either persistence module would leave the package incomplete and encourage
applications to misuse History or serialize framework internals.

## Decision Drivers

- Provide all five product modules as separate public features.
- Use framework-supported serialization instead of internal runtime objects.
- Preserve distinct snapshot and workflow-lineage semantics.
- Make compatibility failures explicit and actionable.

## Considered Options

- Include both persistence adapters as required package features.
- Omit persistence adapters from the package.
- Treat exact Chat History as session or checkpoint persistence.

## Decision Outcome

Chosen option: "Include both persistence adapters as required package features." Python provides
`MongoDBSessionStore(SessionStore)` and `MongoDBCheckpointStorage(CheckpointStorage)`. .NET provides
`MongoDBAgentSessionStore` through the supported public Agent Framework session-hosting contract and
`MongoDBCheckpointStore(JsonCheckpointStore)`. The modules use separate public types and collections by default.

### Consequences

- Good, because the package supports stateless agent hosting and resumable workflows without conflating data models.
- Good, because public framework serializers and compatibility gates protect stored state.
- Bad, because the complete 1.0 release requires additional implementation and integration-test infrastructure.

## Validation

Both languages must pass public serialization, incompatible-version, isolation, optimistic-concurrency, retention,
lineage, ordering, resumption, built-package, sample, and real-deployment integration tests.
