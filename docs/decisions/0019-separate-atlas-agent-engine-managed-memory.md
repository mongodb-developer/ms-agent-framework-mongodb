---
status: proposed
contact: sgsshankar
date: 2026-11-20
deciders: [sgsshankar]
consulted: []
informed: [Python contributors]
---

# Separate Atlas Agent Engine managed memory from direct MongoDB memory

## Context and Problem Statement

The [Memory specification](../spec/features/memory.md) defines semantic recall of stored conversation
messages, not fact extraction. Its direct MongoDB provider owns the collection contract, including
scoped deletion, retention, and explicit index management. The standalone
[Atlas Agent Engine Memory SDK](https://www.mongodb.com/docs/agentengine/sdk/python/packages/agent-engine-sdk-memory/)
can record turns and retrieve both conversation context and durable memories extracted by the server.
The host controls extraction, data retention, storage, and index configuration. This public-preview
service is not a drop-in backend for the existing provider.

## Decision Drivers

- Customers must be able to keep using the direct MongoDB provider when they adopt the SDK.
- Sending conversation data to a managed service must be an explicit, independently scoped choice.
- Existing Python 3.10 installs and all .NET surfaces must remain unaffected.
- The package must not claim lifecycle, durability, or platform support it cannot verify.

## Considered Options

### Extend the existing provider with a backend selector

- Good: One provider type to discover.
- Bad: Its existing lifecycle, index, and retry contracts would become backend-dependent. Switching
  backends could silently change what is stored, where it is processed, and what can be deleted.

### Add a distinct, opt-in managed memory provider

- Good: A separate `ContextProvider` and source ID preserve the existing provider and allow explicit
  composition without migrating data. The SDK can stay an optional Python 3.11+ dependency.
- Bad: Callers must choose which provider receives conversation turns. Customers configuring both
  providers as writers intentionally store the same conversation in two systems.

### Add only a managed recall adapter

- Good: The adapter never sends newly completed conversation turns to managed storage.
- Bad: The adapter cannot learn from the conversation unless the application records turns separately.

## Decision Outcome

Proposed option: add a distinct Python provider for managed recall and **explicitly enabled** turn
recording, initially for the hosted project-scoped SDK transport. Keep the existing provider, its
schema and lifecycle API, base dependencies, and .NET unchanged. Use only the standalone Memory
SDK and public Agent Framework hooks; do not introduce a deployment runner, a checkpoint store,
new RAG behavior, or a fact extraction implementation in this repository.

The application supplies the SDK client and trusted identity. The provider does not close an
injected client. Reads and writes must be scoped by the service to the intended user; turns also
require an explicit session scope. Do not ingest replayed history, generated context from either
provider, or tool output as conversation. Retrieval failure may fail open only at the agent hook,
following the repository's error and logging conventions. Cancellation must propagate.

The managed service, not this adapter, decides whether recorded turns are extracted. Document
that distinction, along with the project configuration needed to govern extraction and the
service's lifecycle operations. Do not automatically create indexes or claim equivalence with
direct MongoDB lifecycle methods. Require verified, stable scoped write keys before enabling turn
recording; do not add a fallback that can silently duplicate writes.

This ADR remains proposed pending repository review. The separate
[managed memory specification](../spec/features/atlas-agent-engine-memory.md) and mapped slice
define the feature-branch contract without changing the existing Memory requirements.

## Validation

- Exercise the public `ContextProvider` hooks and official SDK calls: scoped retrieval and source
  attribution, opt-in recording of only eligible current turns, replay-safe writes, composition,
  redacted operational failures, and cancellation.
- Verify the Python quality gate, built wheel and sdist, and base imports without the optional SDK.
  Verify the SDK extra against the tested Python and SDK versions.
- Use a dedicated hosted test project and synthetic messages to verify user and session isolation,
  the turn-to-recall round trip, and retry behavior. Verify that cleanup targets only test data
  before running live writes. Keep the main worktree unmodified until validation and approval.

## SDK contract evidence

The [public SDK source](https://github.com/mongodb/agent-engine-client-libraries/tree/main/python/packages/agent-engine-sdk-memory)
and published package metadata report version `0.11.15` and Python 3.11 or later. This is a
candidate version, not a tested compatibility claim. Its public `Memory` methods are synchronous;
calling them inline would block Agent Framework's async hooks. An async adapter must not block
the event loop, and cancellation of an awaiting hook must not be interpreted as cancellation of
an HTTP write already accepted by the service.

The hosted SDK exposes `Memory.bind` for scoped identity, `build_context` with a user and optional
session, and `record_turn` with a caller-supplied `idempotency_key`. If the caller omits the key,
the SDK only deduplicates retries of that individual call, not repeated calls from the
application. The [SDK capability matrix](https://www.mongodb.com/docs/agentengine/sdk/python/packages/agent-engine-sdk-memory/docs/capability-matrix/)
documents that app-bound transport does not forward the caller's key. This is why app-bound
recording is outside the initial proposal.
