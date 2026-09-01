---
status: proposed
contact: sgsshankar
date: 2026-07-31
deciders: [sgsshankar]
consulted: [Package publishing owners, Security contact]
informed: [Contributors]
---

# Establish project and publishing governance

## Context and Problem Statement

The repository has an MIT license but cannot publish supported packages without named maintainers, publishing identities, security response, and contribution policy.

## Considered Options

- Establish external project governance before package publication.
- Publish under personal credentials without a documented support model.
- Defer governance until version 1.0.

## Decision Outcome

Chosen option: "Establish external project governance before package publication." The owning GitHub organization is
`mongo`. Its support team, PyPI and NuGet identities, security contact, and release approvers must be recorded before
publication. The MIT license and contribution policy are present. A license change requires a superseding ADR.

### Consequences

- Good, because users know who publishes, supports, and secures the packages.
- Bad, because package publication is blocked until the identities and policies are confirmed.

## Validation

Repository settings and public documentation must name the confirmed owners and contacts; CI publishing environments must require their protected approval.
