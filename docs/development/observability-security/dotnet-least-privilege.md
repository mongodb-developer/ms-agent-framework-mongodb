# .NET least-privilege roles

This document consolidates the per-feature least-privilege guidance already documented across the .NET provider
into one index, per [observability-security.md](../../spec/observability-security.md)'s requirement to "Document
least-privilege roles separately for runtime retrieval, memory writes, and index provisioning." It intentionally
does not restate each feature's exact privilege list (that stays owned by the feature's own document, to avoid
two documents drifting apart); it records the shared pattern and links to the authoritative detail.

## Shared pattern: two distinct roles, never one identity

Every feature that touches a MongoDB Search or Vector Search index recognizes the same two roles, per ADR
[0006](../../decisions/0006-make-index-provisioning-explicit.md) (index provisioning is an explicit, separate
operation, never implicit at construction/agent-hook time) and
[0016](../../decisions/0016-keep-index-facades-in-runtime-packages.md):

- **Runtime role**: used by the deployed application for retrieval, writes, and deletes. Never granted
  `createSearchIndexes`/`dropSearchIndexes`/`updateSearchIndexes` (or the equivalent index-management privilege on
  a non-Search collection index). This is the identity a `MongoDBMemoryProvider`, `MongoDBRAGProvider`,
  `MongoDBChatHistoryProvider`, `MongoDBAgentSessionStore`, or `MongoDBCheckpointStore` instance is constructed
  with in production.
- **Provisioner role**: a separately authorized, deployment-time-only identity used to create, update, drop, or
  validate indexes (`EnsureIndexesAsync`, the `MongoDBMemoryIndexManager`/`MongoDBRAGIndexManager` facades, and
  each store's own `EnsureIndexesAsync`/`ValidateIndexesAsync`). This identity is never embedded in application
  configuration alongside the runtime role's credentials, and this project never creates or updates an index
  implicitly from provider construction, an agent hook, or a direct search/read/write call -- provisioning is
  always an explicit, separately invoked operation.

## Per-feature privilege detail (authoritative source)

| Feature | Runtime privileges | Provisioning privileges | Detail |
| --- | --- | --- | --- |
| Memory | Collection read/write/delete | Index management (create/update/drop) via a separately authorized principal | [memory/dotnet-memory.md](../memory/dotnet-memory.md) |
| RAG (Vector/FullText/Hybrid) | Collection read only (retrieval is read-only; RAG never inserts/updates/deletes) | Index management (create/update/drop, `createSearchIndexes`/`dropSearchIndexes`/`updateSearchIndexes`) via a separately authorized principal | [index-management/dotnet-index-management.md](../index-management/dotnet-index-management.md); [rag/dotnet-rag.md](../rag/dotnet-rag.md) |
| Chat History | Collection read/write | Index management, separate from runtime | [history/dotnet-history.md](../history/dotnet-history.md) |
| Session Store | find, insert, update, scoped delete | Index management, separate from runtime | [persistence/dotnet-session-store.md](../persistence/dotnet-session-store.md) |
| Workflow Checkpoint Store | find, insert, scoped delete, update/`findAndModify` (the latter for `AllocateSequenceAsync`'s per-session sequence counter), plus transaction usage against a replica set/sharded cluster/`mongos` deployment | Index management, separate from runtime | [persistence/dotnet-checkpoint-store.md](../persistence/dotnet-checkpoint-store.md) |

**RAG's runtime role deserves emphasis**: unlike every other feature, RAG's runtime identity needs no write
privilege at all. `MongoDBRAGProvider.SearchAsync` and its supporting validation methods only ever issue
`aggregate`/`listSearchIndexes`/`runCommand` (buildInfo capability check) calls; there is no code path anywhere in
`MongoDBRAGProvider` that inserts, updates, replaces, upserts, or deletes a document. This is enforced
incidentally by every RAG test's fake collection double (`RAGCollectionProxy`,
`dotnet/tests/MongoDB.AgentFramework.Tests/RAG/RAGTestDoubles.cs`), which only implements
`AggregateAsync`/`get_SearchIndexes`/`get_Database`/`get_DocumentSerializer`/`get_Settings` and throws
`NotSupportedException` for anything else -- a mutating call from `MongoDBRAGProvider` would immediately fail
every existing RAG test, not only a dedicated one.

## Exact built-in/custom MongoDB role names remain deployment-specific

This project intentionally does not hard-code a specific Atlas or MongoDB Enterprise built-in role name (for
example a custom role granting exactly `find`+`insert` on one collection): exact role definitions must be
verified against the target deployment and documented by the integrating application before production use,
consistent with the existing (pre-this-slice) deferral noted in
[index-management/dotnet-index-management.md](../index-management/dotnet-index-management.md). What this project
guarantees is the *shape* of the privilege split above (runtime vs. provisioner, read-only vs. read/write per
feature) and that its own code never requires more than that shape to function.

## CI credential scope

The CI workflow added by this slice (`.github/workflows/dotnet-security.yml`) follows the same least-privilege
principle for automation identities: each job declares the narrowest `permissions:` block it needs
(`contents: read` for the dependency audit and secret scan; `contents: read` + `security-events: write` +
`actions: read` only for the CodeQL job, which needs `security-events: write` to upload its SARIF results and
`actions: read` per GitHub's own recommended CodeQL workflow permissions for private repositories) rather than
defaulting to broader repository write access. Every `uses:` reference in the workflow is pinned to an immutable
full commit SHA (with a `# vX.Y.Z` comment recording the release), not a mutable tag, so the workflow's supply
chain cannot change without a reviewable diff to this repository. The secret-scan job runs a repository-local
`git grep` script (`.github/scripts/secret-scan.sh`) rather than downloading a third-party scanner binary,
removing the need to pin or verify a release artifact's checksum at all.
