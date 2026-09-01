---
status: proposed
contact: sgsshankar
date: 2026-07-31
deciders: [sgsshankar]
consulted: [MongoDB driver maintainers]
informed: [Contributors]
---

# Fix resource ownership at construction

## Context and Problem Statement

Providers can construct MongoDB clients from settings or accept caller-supplied clients, databases, collections, and embedding generators. Cleanup must not dispose caller-owned resources or change ownership after failures.

## Decision Drivers

- Prevent double disposal and hidden lifetime coupling.
- Support dependency injection and test doubles.
- Make success and failure cleanup deterministic.

## Considered Options

- Record ownership at construction and dispose only provider-created resources.
- Always dispose all resources reachable from the provider.
- Never dispose any dependency.

## Decision Outcome

Chosen option: "Record ownership at construction and dispose only provider-created resources." Injected resources remain caller-owned; provider-created clients are disposed exactly once through language-native asynchronous cleanup.

### Consequences

- Good, because ownership is predictable and testable.
- Bad, because constructors and wrappers must retain explicit ownership metadata internally.

## Validation

Unit tests must cover injected and constructed resources, constructor and operation failures, cancellation, repeated cleanup, Python async context managers, and .NET `IAsyncDisposable`.
