---
status: proposed
contact: sgsshankar
date: 2026-07-31
deciders: [sgsshankar]
consulted: [MongoDB integration maintainers, Operators]
informed: [Contributors]
---

# Keep explicit index facades in runtime packages

## Context and Problem Statement

Applications and deployment tools need one authoritative implementation for index definition validation and readiness. Moving all helpers to samples would duplicate behavior and weaken diagnostics.

## Considered Options

- Keep explicit validation and provisioning facades backed by one internal runtime index manager.
- Move all index operations into sample-only tooling.
- Provision indexes implicitly during runtime queries.

## Decision Outcome

Chosen option: "Keep explicit validation and provisioning facades backed by one internal runtime index manager." Mutating methods remain opt-in deployment/startup actions and are never called by retrieval or persistence hooks.

### Consequences

- Good, because samples, tests, and production tooling share one validated implementation.
- Bad, because runtime packages expose privileged operations that applications must isolate operationally.

## Validation

Documentation must separate runtime and provisioner privileges, and tests must prove that normal provider paths never invoke mutating index methods.
