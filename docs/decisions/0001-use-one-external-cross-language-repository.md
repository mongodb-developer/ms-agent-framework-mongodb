---
status: proposed
contact: sgsshankar
date: 2026-07-31
deciders:
  - sgsshankar
consulted:
  - Microsoft Agent Framework maintainers
  - MongoDB integration maintainers
informed:
  - Contributors
---

# Use one external cross-language repository

## Context and Problem Statement

The MongoDB integration needs Python and .NET implementations with a release cadence, support model, and MongoDB-specific test infrastructure that differ from Microsoft Agent Framework core. We need to decide whether the integration belongs in the framework monorepo, in separate language repositories, or in one independently maintained repository.

## Decision Drivers

- Release MongoDB-specific fixes without waiting for Agent Framework core releases.
- Keep equivalent Python and .NET behavior visible and reviewable together.
- Give MongoDB-specific issues, security response, and package ownership a clear home.
- Avoid provider-specific query and lifecycle code in Agent Framework core.

## Considered Options

- One external repository containing Python and .NET packages.
- Keep both implementations in the Microsoft Agent Framework monorepo.
- Create separate Python and .NET repositories.

## Decision Outcome

Chosen option: "One external repository containing Python and .NET packages." The repository will publish
`agent-framework-mongodb` and `MongoDB.AgentFramework` independently. Publishing owners must confirm registry
availability and ownership before publication. Agent Framework will retain only lightweight discovery samples and
documentation links.

### Consequences

- Good, because shared requirements, fixtures, and integration infrastructure can enforce behavioral parity.
- Good, because package releases are independent from Agent Framework core.
- Bad, because maintainers must establish separate release, security, and support processes.
- Bad, because cross-language repository automation is more complex than a single-language project.

## Validation

- The repository contains independent Python and .NET package roots and release workflows.
- Runtime packages depend only on public Agent Framework contracts.
- Published package metadata identifies the confirmed external owners and support channels.

## Pros and Cons of the Options

### One external repository containing Python and .NET packages

- Good, because integration behavior and parity fixtures remain co-located.
- Good, because MongoDB-specific work has independent ownership and releases.
- Bad, because CI and release automation must support two ecosystems.

### Keep both implementations in the Agent Framework monorepo

- Good, because framework changes and provider changes can be coordinated atomically.
- Bad, because provider releases inherit the framework monorepo cadence and governance.
- Bad, because MongoDB-specific implementation details expand framework core ownership.

### Create separate Python and .NET repositories

- Good, because each repository can follow language-specific conventions.
- Bad, because requirements, fixtures, and behavior can drift across languages.
- Bad, because users and maintainers must navigate two issue and release surfaces.

## More Information

The repository is owned by the `mongo` GitHub organization. The PyPI owner, NuGet owner, security contact, support
team, and support policy are Foundation verification inputs and must be confirmed before package publication. See
[Resolved implementation decisions](../spec/project/scope.md#resolved-implementation-decisions).
