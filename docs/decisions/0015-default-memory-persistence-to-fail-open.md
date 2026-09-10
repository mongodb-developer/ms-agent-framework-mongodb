---
status: proposed
contact: sgsshankar
date: 2026-07-31
deciders: [sgsshankar]
consulted: [Microsoft Agent Framework maintainers]
informed: [Contributors]
---

# Default Memory persistence to fail open

## Context and Problem Statement

Memory persistence happens after a model response. A transient storage failure should not normally discard that response, while applications with transactional durability requirements need explicit fail-fast behavior.

## Considered Options

- Log a redacted warning by default and offer fail-fast persistence.
- Always fail the invocation when Memory storage fails.
- Always suppress Memory storage failures with no application control.

## Decision Outcome

Chosen option: "Log a redacted warning by default and offer fail-fast persistence." The provider preserves the model response for operational storage failures by default. Configuration, authorization, unsafe filter, and cancellation errors are never suppressed.

### Consequences

- Good, because transient persistence outages do not normally hide successful model responses.
- Bad, because default behavior can leave a gap in semantic Memory.
- Good, because durability-sensitive applications can opt into documented fail-fast behavior.

## Validation

Tests must prove both policies, response behavior, idempotent retries, cancellation propagation, and content-safe logging.
