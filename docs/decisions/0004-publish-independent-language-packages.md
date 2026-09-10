---
status: proposed
contact: sgsshankar
date: 2026-07-31
deciders: [sgsshankar]
consulted: [Package publishing owners]
informed: [Contributors]
---

# Publish independent language packages

## Context and Problem Statement

Python and .NET need equivalent product behavior but can evolve and release independently. External ownership also rules out an unapproved `Microsoft.*` .NET namespace.

## Decision Drivers

- Use ecosystem-native package and namespace conventions.
- Permit independent fixes and versions without abandoning parity.
- Avoid misleading package ownership.

## Considered Options

- `agent-framework-mongodb` and `MongoDB.AgentFramework` with independent semantic versions.
- One synchronized repository version for both packages.
- Publish the .NET package under a `Microsoft.*` namespace.

## Decision Outcome

Chosen option: "`agent-framework-mongodb` and `MongoDB.AgentFramework` with independent semantic versions." Tags use
`python-v<version>` and `dotnet-v<version>`. Publishing owners must confirm registry availability and ownership before
publication.

### Consequences

- Good, because each ecosystem can release on its own schedule.
- Good, because package identity reflects external ownership.
- Bad, because users need a compatibility matrix rather than assuming matching versions.

## Validation

Before publication, owners must confirm name availability, publishing identities, tag conventions, license, support policy, and security contact.
