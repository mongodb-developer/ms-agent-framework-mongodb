---
status: proposed
contact: sgsshankar
date: 2026-07-31
deciders: [sgsshankar]
consulted: [Microsoft Agent Framework maintainers, MongoDB integration maintainers]
informed: [Contributors]
---

# Separate Memory, Chat History, RAG, and persistence

## Context and Problem Statement

Semantic recall, exact conversation replay, knowledge retrieval, session snapshots, and workflow checkpoints have different data and lifecycle semantics. Sharing one provider would make those semantics ambiguous and unsafe.

## Decision Drivers

- Keep read/write behavior and authorization boundaries explicit.
- Prevent semantic Memory from being mistaken for exact history.
- Keep every feature on a supported public contract with independent compatibility validation.

## Considered Options

- Separate public providers with shared internal MongoDB mechanics.
- One configurable MongoDB provider for all features.
- Separate repositories for every feature.

## Decision Outcome

Chosen option: "Separate public providers with shared internal MongoDB mechanics." Memory, Chat History, RAG, Session Store, and Workflow Checkpoint Store must not call each other or share a public provider class.

### Consequences

- Good, because each provider has one understandable lifecycle and contract.
- Good, because shared internal index, filter, serialization, and ownership utilities reduce duplication.
- Bad, because the public package contains more provider types and explicit configuration.

## Validation

Architecture tests and reviews must enforce inward dependencies from feature modules to shared internals and prohibit cross-feature calls.
