---
status: proposed
contact: sgsshankar
date: 2026-07-31
deciders: [sgsshankar]
consulted: [Package publishing owners, Security reviewers]
informed: [Contributors]
---

# Release features through staged quality gates

## Context and Problem Statement

Memory, exact History, each RAG mode, Session Store, and Workflow Checkpoint Store have different capability and
integration risks. Each required feature needs independently reviewable evidence before the complete 1.0 release.

## Decision Drivers

- Require real-deployment evidence for every advertised capability.
- Build and test the exact package artifacts that are published.
- Keep Python and .NET releases independently rerunnable.

## Considered Options

- Required implementation gates culminating in one complete 1.0 release.
- One undifferentiated gate for every feature.
- Release directly from local developer builds.

## Decision Outcome

Chosen option: "Required implementation gates culminating in one complete 1.0 release." Gates close in this order:
Foundation, Memory, Chat History, Vector RAG, Full-text RAG, Hybrid RAG, Session Store, Workflow Checkpoint Store, and
Complete Release 1.0. Every feature is required; gate ordering controls implementation dependencies and evidence, not
product scope.

### Consequences

- Good, because support claims are tied to current test evidence for all five modules and all four RAG modes.
- Good, because package provenance and compatibility checks precede publication.
- Bad, because release automation and integration environments require significant setup.

## Validation

Protected CI must run credential-free quality, contract, package-install, API, dependency, and security checks. Approved environments run isolated Search-capable integration tests without exposing secrets to untrusted fork code.
