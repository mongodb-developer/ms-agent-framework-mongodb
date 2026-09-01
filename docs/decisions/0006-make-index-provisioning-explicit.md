---
status: proposed
contact: sgsshankar
date: 2026-07-31
deciders: [sgsshankar]
consulted: [MongoDB integration maintainers]
informed: [Contributors, Operators]
---

# Make index provisioning explicit

## Context and Problem Statement

MongoDB Search and Vector Search index creation is asynchronous, privileged, and potentially expensive. Retrieval cannot safely imply index creation or report command acceptance as readiness.

## Decision Drivers

- Separate runtime and provisioning privileges.
- Make production index changes deliberate and observable.
- Return actionable definition and readiness errors.

## Considered Options

- One internal index manager with explicit validate and ensure operations.
- Create missing indexes automatically during retrieval.
- Leave all index behavior outside the packages.

## Decision Outcome

Chosen option: "One internal index manager with explicit validate and ensure operations." Validation is read-only; mutation and bounded readiness polling occur only through explicit provisioning calls.

### Consequences

- Good, because runtime identities need fewer privileges.
- Good, because asynchronous states and mismatches are visible.
- Bad, because deployment workflows must provision indexes before traffic.

## Validation

Tests must cover missing, building, ready, non-queryable, failed, mismatched, cancelled, and timed-out states. Provider hooks and direct search must never invoke provisioning.
