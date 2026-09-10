---
status: proposed
contact: sgsshankar
date: 2026-07-31
deciders: [sgsshankar]
consulted: [Microsoft Agent Framework maintainers]
informed: [Contributors]
---

# Integrate through public Agent Framework contracts

## Context and Problem Statement

The providers need framework lifecycle integration without coupling this repository to internal or obsolete Agent Framework types.

## Decision Drivers

- Preserve compatibility across supported Agent Framework releases.
- Avoid framework-core changes made only for MongoDB behavior.
- Retain framework source attribution, filtering, and session conventions.

## Considered Options

- Implement current public provider contracts.
- Depend on internal framework implementation types.
- Fork or modify Agent Framework core contracts.

## Decision Outcome

Chosen option: "Implement current public provider contracts." Python uses `ContextProvider` and `HistoryProvider`; .NET uses `AIContextProvider`/`MessageAIContextProvider` and `ChatHistoryProvider`. .NET RAG may compose `TextSearchProvider` only after compatibility tests prove cancellation, citations, result preservation, and on-demand behavior.

### Consequences

- Good, because framework upgrades are bounded by documented public surfaces.
- Good, because provider attribution and lifecycle behavior remain framework-consistent.
- Bad, because sealed or incomplete framework adapters may require a dedicated compatibility layer.

## Validation

CI must test the oldest and newest supported Agent Framework versions and include a focused .NET `TextSearchProvider` compatibility suite.
