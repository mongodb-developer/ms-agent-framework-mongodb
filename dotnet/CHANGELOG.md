# Changelog

All notable changes to the `MongoDB.AgentFramework` NuGet package are
documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this package
uses [Semantic Versioning](https://semver.org/) independently from the
Python `agent-framework-mongodb` distribution (see
[compatibility and migration](../docs/spec/compatibility-migration.md)).

No version listed below has been published to NuGet.org, and no `dotnet-v*`
git tag has been created. Publishing is gated on
[ADR 0013](../docs/decisions/0013-establish-project-and-publishing-governance.md)
(project and publishing governance), which remains `proposed`. See the
[.NET packaging and release engineering guide](../docs/development/release/dotnet-packaging-release.md)
for full detail.

## [Unreleased]

## [0.1.0-preview.1]

Pre-1.0 preview. This is **not** a first stable release and does not
establish a public API compatibility baseline in the SemVer sense -- see
[PublicAPI baseline (pre-1.0)](../docs/development/release/dotnet-packaging-release.md#publicapi-baseline-pre-10).

### Added

- `MongoDBMemoryProvider` (`AIContextProvider`) and `MongoDBMemoryIndexManager`
  for scoped semantic conversation recall over MongoDB Vector Search.
- `MongoDBChatHistoryProvider` (`ChatHistoryProvider`) for lossless, ordered
  exact chat-history replay.
- `MongoDBRAGProvider` (VectorAnn/ENN, FullText, HybridRrf), typed
  `MongoDBRAGFilter`, `MongoDBRAGContextProvider`, and
  `MongoDBRAGIndexManager` for read-only authoritative knowledge retrieval.
- `MongoDBAgentSessionStore` -- a compatibility-blocked facade over
  `AIAgent.SerializeSessionAsync`/`DeserializeSessionAsync` pending a public
  upstream session-hosting persistence contract. See
  [Session Store](../dotnet/README.md#session-store).
- `MongoDBCheckpointStore` -- a real `JsonCheckpointStore` implementation for
  resumable Agent Framework workflow checkpoint state and lineage.
- Finalized NuGet package metadata: authors, license (MIT), project/
  repository URLs, tags, release notes, README embedding, symbol packages
  (`.snupkg`), and SourceLink (`Microsoft.SourceLink.GitHub`).
- Deterministic, reproducible package builds and an automated
  package-content allowlist test guaranteeing no sample/test/internal
  assembly ever leaks into the shipped package.
- A pre-1.0 Roslyn PublicAPI analyzer baseline
  (`PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt`).
- A clean, isolated NuGet consumer smoke test constructing every public
  feature area from the packed `.nupkg`.
- SHA-pinned CI workflows: `dotnet-quality.yml` (restore/format/build/test/
  pack/package-smoke/sample builds), `dotnet-integration.yml` (credentialed
  MongoDB integration test categories, gated, never on `pull_request`),
  `dotnet-sbom-provenance.yml` (credential-free SPDX + CycloneDX SBOM,
  checksum manifest; holds no elevated permissions and is safe against
  untrusted fork pull requests), and `dotnet-release-attestation.yml`
  (custom SLSA v1.0 provenance attestation binding the attested package to an
  independently ancestry-validated commit SHA, triggered exclusively by
  `workflow_run` reacting to `dotnet-sbom-provenance.yml` so its privileged
  job graph is always sourced from the default branch; rebuilds and
  re-verifies the package fresh from that validated commit before generating
  and attesting the predicate; documented-but-disabled NuGet signing step).

### Known limitations

- Session Store cannot yet implement a real framework interface (no public
  upstream contract exists); the package cannot claim a 1.0 release while
  this remains true.
- No owner-confirmed publishing identity, security contact, support
  channel, or signing certificate exists yet (ADR 0013 `proposed`).
- Five sample scenarios from `docs/spec/samples.md`
  (`OnDemandRetrievalTool`, `WorkflowRetrieval`, `MemoryAndRAG`,
  `StructuredMetadataRetrieval`, standalone `MongoDBDocumentLoader`) are not
  yet implemented; see the
  [.NET samples inventory](../docs/development/release/dotnet-samples-inventory.md).

[Unreleased]: https://github.com/mongo/ms-agent-framework-mongodb/compare/dotnet-v0.1.0-preview.1...HEAD
[0.1.0-preview.1]: https://github.com/mongo/ms-agent-framework-mongodb/releases/tag/dotnet-v0.1.0-preview.1
