# Quality and Release Requirements

## Quality gates

### Python

- unit tests and coverage
- Search-capable deployment integration tests
- Ruff lint and format
- Pyright
- MyPy
- package build and metadata validation
- minimum and maximum supported dependency resolution
- generated API documentation/import smoke test
- wheel and source-distribution installation tests in clean environments

### .NET

- unit and Search-capable deployment integration tests
- build all target frameworks with warnings as errors
- formatting/analyzer validation
- package creation validation
- public interface compatibility checks after the first release
- required UTF-8 BOM and copyright/XML documentation conventions where adopted by the repository
- NuGet install and runtime smoke tests from the produced package, not project references

### Cross-language

- equivalent behavioral contract tests for scopes, filters, search limits, result mapping, and ownership
- compatibility matrix against supported Agent Framework versions
- dependency and vulnerability scanning
- sample smoke builds
- public API baseline comparison against the previous release
- equivalent defaults and validation fixtures

The current Memory prototype has already demonstrated the following local checks and they should remain the baseline:

- Python unit tests, lint/format, Pyright, MyPy, and lock validation
- .NET provider tests and builds on .NET 8, 9, and 10
- .NET sample build
- Agent Framework core regression tests

Real MongoDB integration tests still require suitable credentials and Search-capable infrastructure.

### CI workflow topology

The external repository SHOULD use independently rerunnable jobs/workflows:

1. `python-quality`: lint, format, typing, unit tests, coverage, build, metadata, package install.
2. `dotnet-quality`: restore, build all TFMs, analyzers/format, unit tests, pack, package install.
3. `contract`: shared fixture validation and public parity checks.
4. `integration-memory`: Python and .NET Memory against isolated Search-capable deployment.
5. `integration-history`: Python and .NET exact-history persistence against a real deployment.
6. `integration-rag-vector`: ANN and ENN.
7. `integration-rag-search`: full text.
8. `integration-rag-hybrid`: native `$rankFusion` with capability diagnostics.
9. `integration-persistence`: required Session Store and Workflow Checkpoint tests.
10. `samples`: build/run smoke tests with deterministic fixtures where credentials are available.
11. `security`: secret scan, dependency review, vulnerability scan, and code scanning.
12. `release-python` and `release-dotnet`: independent trusted publishing after protected approval.

Pull requests MUST run all credential-free jobs. Credentialed integration jobs SHOULD run in an approved environment,
must never execute untrusted fork code with secrets, and must use short-lived credentials when the platform supports
them. Scheduled jobs SHOULD catch upstream Agent Framework, driver, and MongoDB service changes.

## Release engineering and supply-chain requirements

### Package build and provenance

- Build release artifacts in CI from a protected tag that resolves to the reviewed commit.
- Do not upload developer-machine artifacts.
- Use PyPI trusted publishing/OIDC where available; avoid long-lived API tokens.
- Use NuGet trusted/signing infrastructure approved by the owning organization.
- Sign NuGet packages when organizational infrastructure supports it and verify signatures after download.
- Generate provenance attestations for published artifacts and retain workflow/run references.
- Generate an SBOM for each release or repository release bundle.
- Publish checksums for repository-attached artifacts.
- Verify wheel/sdist/NuGet contents exclude tests, secrets, local configuration, and unrelated monorepo files.
- Install and smoke test the exact artifacts before publication, then verify the artifacts downloaded from PyPI/NuGet.

Python and .NET releases MAY use different versions and dates. Repository tags MUST unambiguously identify the final
package and version using `python-v<version>` and `dotnet-v<version>`.

### Public API compatibility

From the first published release:

- Python CI MUST detect removed/renamed public exports and incompatible signature/default changes.
- .NET CI MUST compare public API surface and detect binary/source-breaking changes.
- Stored Memory, Chat History, Session Store, and Workflow Checkpoint schemas and expected index definitions require
  migration notes when changed.
- Capability support removed by an upstream dependency requires a release note and versioning decision.
- Deprecations MUST include replacement guidance and remain for a documented period before stable removal.

The 1.0 API baseline is stable under semantic versioning.

### Implementation gates

#### Foundation

- external repository ownership, license, security, contribution, and release identities are established
- canonical package identities, package builds, CI, and shared internal mechanics are established
- exact supported dependency and deployment versions are verified
- explicit index validation/provisioning and resource ownership are documented

#### Memory

- Python and .NET providers pass scoped recall, persistence, deletion, retention, ownership, and index tests
- built artifacts and runnable samples pass in clean environments

#### Chat History

- Python and .NET providers pass lossless serialization, atomic ordering, idempotency, retention, and authorized
  deletion tests
- built artifacts and runnable samples pass in clean environments

#### Vector RAG

- Python and .NET vector providers support ANN and ENN
- typed filters, source mapping, citations, direct search, and read-only behavior are proven
- vector capability and index validation errors are actionable
- real-deployment vector integration tests pass
- automatic and on-demand retrieval, parent-document, and structured metadata sample paths are
  documented and tested at their stated support level

#### Full-text RAG

- bounded Search operator surface and filter translation are implemented in both languages
- Search score semantics and source mapping are documented
- real-deployment full-text tests pass

#### Hybrid RAG

- native `$rankFusion` is implemented in both languages
- server/deployment/driver gating and 8.0 caveat behavior are tested
- authorization filters are proven in both input pipelines
- de-duplication, weights, candidate limits, score details, and post-fusion enrichment are tested

#### Session Store

- Python and .NET public session-hosting contracts and framework serialization are verified
- isolation, optimistic concurrency, TTL, deletion, and incompatible-version handling are proven
- built artifacts, runnable samples, and `integration-persistence` pass

#### Workflow Checkpoint Store

- Python and .NET public checkpoint contracts and framework serialization are verified
- idempotency, lineage, ordering, pagination, resumption, retention, and incompatible-version handling are proven
- built artifacts, runnable samples, and `integration-persistence` pass

#### Complete Release 1.0

- public APIs and defaults are reviewed and baselined
- every advertised capability-matrix cell has current evidence
- packages are signed/attested according to owner policy and include SBOM/provenance
- compatibility matrices cover Agent Framework, runtimes, drivers, and MongoDB deployments
- migration documentation from prototypes is complete
- support ownership, security response, release cadence, and deprecation policy are public
- all core quickstarts and required scenario samples run against published packages
- Agent Framework discovery samples/documentation use published packages

[Back to the specification index](README.md)
