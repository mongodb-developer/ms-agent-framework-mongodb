---
status: proposed
contact: sgsshankar
date: 2026-07-31
deciders: [sgsshankar]
consulted: [Microsoft Agent Framework maintainers]
informed: [Contributors]
---

# Fail open only at agent adapter boundaries

## Context and Problem Statement

An operational retrieval failure should not necessarily suppress an agent response, but configuration, security, cancellation, and direct API failures must remain visible.

## Decision Drivers

- Match Agent Framework resilience conventions.
- Keep direct APIs deterministic and diagnosable.
- Never hide unsafe filters, invalid configuration, or cancellation.

## Considered Options

- Fail open for operational errors only in agent hooks.
- Fail open for every provider operation.
- Fail fast for every provider operation.

## Decision Outcome

Chosen option: "Fail open for operational errors only in agent hooks." Public search, storage, validation, and provisioning always surface stable integration errors with driver causes. Cancellation, capability, index-definition, configuration, and filter errors always propagate.

### Consequences

- Good, because transient retrieval failures need not prevent model invocation.
- Good, because direct workflows can rely on explicit failure.
- Bad, because adapters and direct services intentionally have different failure behavior.

## Validation

Tests must cover the exception taxonomy, cancellation propagation, redacted logs, bounded timeouts, driver retry
interaction, and the configurable Memory persistence policy. See ADR 0015 for the default Memory persistence behavior.
