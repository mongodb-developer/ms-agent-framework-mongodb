---
status: proposed
contact: sgsshankar
date: 2026-07-31
deciders: [sgsshankar]
consulted: [Microsoft Agent Framework maintainers, MongoDB driver maintainers]
informed: [Contributors]
---

# Publish only tested compatibility ranges

## Context and Problem Statement

Agent Framework, drivers, runtimes, MongoDB server versions, and Search deployment capabilities evolve independently. Static assumptions would overstate support.

## Considered Options

- Set minimums from verified APIs and test the oldest and newest supported versions.
- Support only the newest dependency and deployment versions.
- Declare broad version ranges without real-deployment evidence.

## Decision Outcome

Chosen option: "Set minimums from verified APIs and test the oldest and newest supported versions." Exact minimum Agent Framework, PyMongo, MongoDB.Driver, server, and deployment versions are set during implementation from required public APIs and current official documentation. A deployment/mode combination is supported only when the capability matrix cites current test evidence.

### Consequences

- Good, because compatibility claims remain auditable and current.
- Bad, because scheduled and release-gate test infrastructure is required.

## Validation

CI must test dependency-range endpoints, and every advertised Search capability cell must record deployment, server, driver, date, and test owner.
