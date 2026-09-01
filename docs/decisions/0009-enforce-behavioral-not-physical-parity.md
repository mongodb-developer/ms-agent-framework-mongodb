---
status: proposed
contact: sgsshankar
date: 2026-07-31
deciders: [sgsshankar]
consulted: [Python and .NET maintainers]
informed: [Contributors]
---

# Enforce behavioral rather than physical parity

## Context and Problem Statement

Python and .NET should present one product, but their drivers, serializers, naming conventions, and framework contracts differ. Requiring identical BSON can break language-native implementations without improving observable behavior.

## Decision Drivers

- Give users equivalent scopes, filters, limits, results, cancellation, and lifecycle behavior.
- Preserve language-native APIs and proven connector behavior.
- Avoid unsupported claims of shared physical collections.

## Considered Options

- Shared behavioral fixtures with documented physical differences.
- Identical public syntax and BSON schemas in both languages.
- Independent implementations without parity requirements.

## Decision Outcome

Chosen option: "Shared behavioral fixtures with documented physical differences." Cross-language Memory or exact-history interoperability is not promised until serialization fixtures prove it; RAG may share collections through explicit field mappings.

### Consequences

- Good, because parity focuses on user-visible guarantees and security behavior.
- Good, because language APIs remain idiomatic.
- Bad, because physical schema differences require clear documentation and migration care.

## Validation

Language-neutral fixtures must cover scopes, filters, option validation, results, citations, index states, ownership, ordering, and idempotency. Intentional differences require documented rationale.
