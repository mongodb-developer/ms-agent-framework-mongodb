---
status: proposed
contact: sgsshankar
date: 2026-07-31
deciders: [sgsshankar]
consulted: [Microsoft Agent Framework maintainers, MongoDB integration maintainers]
informed: [Contributors]
---

# Store versioned exact history with atomic ordering

## Context and Problem Statement

Exact Chat History must preserve every supported message content item and return deterministic order under retries and concurrent writers. Timestamps alone cannot guarantee conversation order.

## Decision Drivers

- Preserve lossless framework message replay.
- Make writes idempotent under retries.
- Support deterministic ordering without a single-process assumption.

## Considered Options

- MongoDB atomic per-session sequence allocation.
- Application-assigned sequence in provider session state.
- Timestamp ordering with an ID tiebreaker.

## Decision Outcome

Chosen option: "MongoDB atomic per-session sequence allocation." Store one versioned message envelope per document, use a unique scoped message identity, and allocate monotonic sequence values atomically per authorized session.

### Consequences

- Good, because concurrent writers receive deterministic order.
- Good, because retry deduplication can use stable scoped message IDs.
- Bad, because sequence allocation adds a write and an additional internal record or transaction pattern.

## Validation

Contract and integration tests must cover every supported content type, colliding timestamps, concurrent writers, retries, latest-`N` loading, unknown schema versions, and authorized session clearing.
