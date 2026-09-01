---
status: proposed
contact: sgsshankar
date: 2026-07-31
deciders: [sgsshankar]
consulted: [Microsoft Agent Framework telemetry maintainers, Privacy reviewers]
informed: [Contributors]
---

# Use standard telemetry without unapproved markers

## Context and Problem Statement

The integration needs diagnostics, but external feature markers in Agent Framework telemetry require framework-owner and privacy approval.

## Considered Options

- Emit standard logs and tracing attributes, adding framework markers only after approval.
- Add custom Agent Framework feature markers immediately.
- Provide no integration telemetry.

## Decision Outcome

Chosen option: "Emit standard logs and tracing attributes, adding framework markers only after approval." Default telemetry contains operation category, duration, count, mode, and error category, but excludes query text, content, embeddings, credentials, and user-bearing filters.

### Consequences

- Good, because diagnostics are available without claiming an upstream telemetry contract.
- Bad, because early releases may not appear in Agent Framework-specific feature reports.

## Validation

Privacy tests and review must verify default attribute allowlists. Adding an Agent Framework integration marker
requires documented upstream acceptance and a superseding or amended ADR.
