# Compatibility, Migration, and Acceptance

## Compatibility and versioning

- Publish an explicit Agent Framework compatibility matrix.
- Test the oldest supported stable Agent Framework version and the newest supported stable version.
- Support only tested public Agent Framework contracts and reject unsupported versions with actionable guidance.
- Use semantic versioning independently for Python and .NET packages, even if releases are coordinated.
- Keep behavioral parity but do not force identical version numbers when one language changes independently.
- Treat public provider names, option names, physical stored schema, and index definitions as compatibility surfaces.
- Document supported MongoDB deployment/server versions and Search capability requirements for every search mode.
- Capability requirements must be verified against current official MongoDB documentation during implementation; do
  not hardcode assumptions from an old Atlas documentation page.

## Migration from the current branch

1. Create the new external repository with ownership, license, security policy, and CI foundations.
2. Extract Python Memory implementation, tests, sample, and package metadata from `feature/mongodb-memory`.
3. Rename the Python class to `MongoDBMemoryContextProvider` and update documentation/tests.
4. Extract .NET Memory implementation, tests, and sample.
5. Rename the .NET package, namespace, and provider types for external ownership.
6. Remove monorepo-specific project references, central package versions, solution registration, release filters, and
   workflow paths.
7. Replace them with standalone package management and CI.
8. Re-run all Memory validation in the external repository before implementing RAG.
9. Implement Python and .NET exact Chat History with lossless serialization fixtures.
10. Implement vector RAG and validate it independently.
11. Add automatic/on-demand invocation and required scenario samples.
12. Add full-text and hybrid RRF modes after current MongoDB capability requirements are verified.
13. Implement Session Store and Workflow Checkpoint Store against verified public framework contracts.
14. Publish packages after all acceptance criteria and implementation gates pass.
15. Update Agent Framework samples/docs to consume the published packages.
16. After migration is verified, rewrite or replace the current feature branch so it contains only intended Agent
    Framework discovery samples/documentation, if those are accepted by that repository.

Do not delete or rewrite the current prototype until the extracted repository reproduces its tests and package builds.

## Delivery sequence

Use focused commits that preserve a reviewable implementation story:

1. Scaffold external repository, package builds, CI, ownership, and security files.
2. Extract Python Memory provider and unit tests.
3. Add Python Memory sample, real-deployment test, and documentation.
4. Extract .NET Memory provider and unit tests.
5. Add .NET Memory sample, real-deployment test, and documentation.
6. Add shared compatibility and release automation.
7. Add Python exact Chat History provider, tests, and sample.
8. Add .NET exact Chat History provider, tests, and sample.
9. Add Python vector RAG provider and unit tests.
10. Add Python vector RAG sample and real-deployment test.
11. Add .NET vector RAG provider and unit tests.
12. Add .NET vector RAG sample and real-deployment test.
13. Add on-demand, parent-document, workflow, combined, structured metadata, loader, and incremental-ingestion samples.
14. Add full-text retrieval in both languages.
15. Add hybrid retrieval and capability gating in both languages.
16. Add external-package discovery samples/documentation to Microsoft Agent Framework.
17. Add Python Session Store, .NET Session Store, Python Workflow Checkpoint Store, and .NET Workflow Checkpoint Store
  in separate reviewable commits after their shared contracts and serializers are validated.

Do not combine extraction, public renaming, RAG implementation, and monorepo cleanup into one commit.

## Acceptance criteria

The project is ready for package publication when all of the following are true:

- The external repository has named maintainers and package-publishing owners.
- Python and .NET Memory providers pass unit and real MongoDB integration tests.
- Python and .NET exact Chat History providers pass unit and real MongoDB integration tests.
- Python and .NET vector RAG providers pass unit and real MongoDB integration tests.
- Python and .NET full-text and hybrid RRF providers pass unit and real MongoDB integration tests.
- Python and .NET Session Store providers pass serialization, concurrency, isolation, retention, and integration tests.
- Python and .NET Workflow Checkpoint Store providers pass serialization, lineage, resumption, retention, and
  integration tests.
- Memory, Chat History, and RAG are separate public types with no mixed lifecycle behavior.
- Memory supports authorized deletion and optional TTL retention.
- Chat History preserves exact supported message content, deterministic ordering, and idempotency.
- RAG supports documented automatic and on-demand modes without exposing model-controlled MongoDB queries.
- Runtime providers depend only on public Agent Framework interfaces.
- Caller-owned clients are never disposed by providers.
- Index creation is explicit and index validation produces actionable errors.
- Tenant/security filters are executed in MongoDB before limiting results.
- RAG results preserve source names, URLs, scores, metadata, and raw documents.
- Parent-document and structured metadata retrieval enforce authorization before limiting or hydration.
- Samples run from documented environment variables without embedded secrets.
- Package build, lint, type, analyzer, vulnerability, and compatibility checks pass.
- The compatibility matrix and supported MongoDB deployment requirements are published.
- Agent Framework discovery documentation points to the external packages and repository.
