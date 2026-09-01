---
status: proposed
contact: sgsshankar
date: 2026-07-31
deciders: [sgsshankar]
consulted: [MongoDB Search specialists, Security reviewers]
informed: [Contributors]
---

# Use typed filters and native search pipelines

## Context and Problem Statement

RAG must support vector ANN, vector ENN, full-text, and hybrid RRF retrieval without allowing model-controlled BSON or
moving authorization checks after candidate selection.

## Decision Drivers

- Enforce tenant and authorization filters inside every retrieval branch.
- Preserve MongoDB-native score and rank semantics.
- Prevent injection through fields, operators, indexes, or pipelines.

## Considered Options

- Typed filter AST translated completely into structured native pipelines.
- Accept raw BSON filters and pipelines from callers or tools.
- Retrieve broadly and filter or fuse results in application memory.

## Decision Outcome

Chosen option: "Typed filter AST translated completely into structured native pipelines." Vector ANN and ENN use
`$vectorSearch`, full text uses `$search`, and hybrid RRF uses native `$rankFusion`. Unsupported translation or
capability fails clearly; modes never silently downgrade.

### Consequences

- Good, because authorization happens before result limiting.
- Good, because score semantics match MongoDB capabilities.
- Bad, because the required operator surface is intentionally bounded.
- Bad, because each search mode needs a complete filter translator and capability gate.

## Validation

Pipeline tests must assert stage order, filter placement in every branch, option exclusivity, bounded inputs, field-path validation, read-only behavior, and rejection of partial translations.
