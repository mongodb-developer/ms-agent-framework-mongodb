# MongoDB Integration Specifications

This directory is the canonical implementation specification for MongoDB integrations for Microsoft Agent Framework. Every normative implementation requirement is maintained in this document set; architectural decisions are recorded separately in [Architectural Decision Records](../decisions/README.md).

## Canonical identities

| Surface | Canonical identity |
| --- | --- |
| GitHub repository | [`mongo/ms-agent-framework-mongodb`](https://github.com/mongo/ms-agent-framework-mongodb) |
| Python distribution | `agent-framework-mongodb` |
| Python import root | `agent_framework_mongodb` |
| .NET package and namespace | `MongoDB.AgentFramework` |

## Document map

- [Project scope and decisions](project/scope.md)
- [System architecture](architecture/system.md)
- [Packages and namespaces](packages.md)
- [Memory](features/memory.md)
- [Chat History](features/chat-history.md)
- [RAG](features/rag.md)
- [Index management](features/index-management.md)
- [Knowledge ingestion](features/ingestion.md)
- [Interfaces and parity](interfaces.md)
- [Configuration](configuration.md)
- [Resilience and errors](resilience.md)
- [Observability and security](observability-security.md)
- [Session and workflow persistence](features/persistence.md)
- [Implementation map](implementation-map.md)
- [Testing](testing.md)
- [Quality and release](quality-release.md)
- [Samples and documentation](samples.md)
- [Compatibility and migration](compatibility-migration.md)
- [References](references.md)

## Precedence and governance

These specifications are the implementation source of truth. Accepted ADRs record approved architectural choices. If an accepted ADR and these specifications conflict, implementation is blocked until a dedicated documentation change reconciles them. Proposed ADRs do not authorize deviations. Public API, stored schema, index definition, package identity, compatibility, security-boundary, and release-policy changes require an ADR.

## Document purpose

This document is the implementation specification for the independently maintained repository
`mongo/ms-agent-framework-mongodb`. The repository will provide MongoDB integrations for Microsoft Agent Framework in both
Python and .NET.

The repository contains five distinct public runtime features:

1. **Memory**: persistent semantic recall of agent conversations using MongoDB Vector Search.
2. **Chat History**: exact, ordered persistence of one conversation through Agent Framework history abstractions.
3. **Retrieval-Augmented Generation (RAG)**: read-only retrieval from an existing MongoDB knowledge collection using
  ANN, ENN, full-text, and hybrid RRF search.
4. **Session Store**: complete serialized `AgentSession` snapshots, including provider-owned session state.
5. **Workflow Checkpoint Store**: resumable workflow execution state, lineage, pending requests, and committed
  executor state.

This document is intended to be sufficient context for an implementation team or coding agent. It records the
architecture, required behavior, interface direction, migration plan, validation requirements, release model, and
primary references. Implementation must not begin by changing Microsoft Agent Framework core types.

### Requirement language

The key words **MUST**, **MUST NOT**, **REQUIRED**, **SHOULD**, **SHOULD NOT**, and **MAY** in this document describe
implementation priority:

- **MUST/MUST NOT/REQUIRED**: release-blocking requirements.
- **SHOULD/SHOULD NOT**: strong recommendations that require a recorded design decision to override.
- **MAY**: optional behavior that must not change required semantics.

When this document distinguishes a **verified fact** from a **design recommendation**, verified facts come from the
referenced framework source or MongoDB documentation. Recommendations define this project's intended interface and may
be changed only through an architectural decision record (ADR).
