# .NET packaging and release engineering

This document describes implementation-map
[slice 20](../../spec/implementation-map.md) (.NET only), governed by
[packages.md](../../spec/packages.md), [quality-release.md](../../spec/quality-release.md),
and [compatibility-migration.md](../../spec/compatibility-migration.md), and
ADR rationale [0011](../../decisions/0011-release-features-through-staged-quality-gates.md)
and [0013](../../decisions/0013-establish-project-and-publishing-governance.md).
ADR 0013 remains `proposed`, not `accepted` -- it records rationale but does
not itself authorize a publisher identity, security contact, support channel,
or signing certificate. This packaging engineering has been completed to the
maximum extent possible without an owner-confirmed publishing identity and
without a live MongoDB deployment; both blockers are called out explicitly
below rather than worked around.

## What this slice delivers

- Finalized `MongoDB.AgentFramework` NuGet package metadata and tested
  dependency ranges (`dotnet/src/MongoDB.AgentFramework/MongoDB.AgentFramework.csproj`).
- A deterministic, reproducible package build (`Deterministic`,
  `ContinuousIntegrationBuild`, `EnablePackageValidation`).
- Symbol packages (portable PDB `.snupkg`) and SourceLink
  (`Microsoft.SourceLink.GitHub`) so consumers can step into and attribute
  release source.
- A pre-1.0 Roslyn PublicAPI analyzer baseline
  (`PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt`) that fails the build on
  any accidental public API break or undocumented addition, generated from
  the *current* intended public surface -- explicitly **not** a first
  published-release baseline (there has been no first publish yet).
- A package-content allowlist test proving the packed `.nupkg`/`.snupkg`
  contain only the runtime library, XML docs, README, and license/SourceLink
  metadata -- no sample, test, or internal-only assembly ever leaks into the
  package.
- A clean, isolated NuGet consumer smoke test
  (`dotnet/tests/PackageSmokeTest/`) that restores the *packed* `.nupkg` (not
  a project reference) into an isolated package cache and constructs every
  public feature area: Memory, Chat History, RAG (VectorAnn/FullText/
  HybridRrf, `MongoDBRAGContextProvider`), both Index Managers, Session
  Store, and Checkpoint Store (including `CheckpointManager.CreateJson`).
  Construction-only, no MongoDB I/O -- it does not require a live deployment.
- A single orchestration script, `dotnet/scripts/verify-package.ps1`, that
  packs, checks the content allowlist, asserts nuspec metadata, packs a
  second time and compares the two packages for reproducibility, runs the
  consumer smoke test against the freshly packed artifact, and prints a
  SHA256 checksum manifest.
- Three new SHA-pinned GitHub Actions workflows (below).
- This document, a changelog, and a samples inventory (below).

## Package metadata

`MongoDB.AgentFramework.csproj` sets:

- `Version` = `0.1.0-preview.1` (pre-1.0; see [Versioning](#versioning-and-tagging)).
- `Authors`, `Copyright`, `PackageProjectUrl`
  (`https://github.com/mongo/ms-agent-framework-mongodb`), `RepositoryUrl`,
  `RepositoryType=git`.
- `PackageLicenseExpression=MIT` plus a legacy `PackageLicenseUrl` fallback
  for older consumer tooling that does not read license expressions.
- `PackageReadmeFile` embedding this package's `README.md`.
- `PackageTags` and `PackageReleaseNotes` (pointing at the changelog).
- `GenerateDocumentationFile=true` with `WarningsAsErrors` including
  `CS1591` (missing public XML doc) -- the build itself fails on any
  undocumented public member, so there is no separate lint step needed to
  enforce complete XML docs.
- Symbols: `IncludeSymbols=true`, `SymbolPackageFormat=snupkg`,
  `DebugType=portable`.
- SourceLink: `PackageReference Include="Microsoft.SourceLink.GitHub"`,
  `PublishRepositoryUrl=true`, `EmbedUntrackedSources=true`.
- Determinism: `Deterministic=true` always;
  `ContinuousIntegrationBuild=true` only when the `CI` environment variable
  is set (so local non-CI builds keep the usual local debug experience,
  while CI and `verify-package.ps1` runs -- which set `CI=true` -- produce
  the canonical deterministic build path-mapped for SourceLink).
- `EnablePackageValidation=true` (Microsoft's package-validation SDK
  target) enforces baseline compatibility once a `PackageValidationBaselineVersion`
  is set after a first real publish; left unset here since there is no
  published baseline yet.
- `PackageOutputPath` fixed to `dotnet/artifacts/packages` so every pack
  invocation (local, CI, or `verify-package.ps1`) lands in one
  `.gitignore`d, known location.
- `Microsoft.CodeAnalysis.PublicApiAnalyzers` is referenced as a
  `PrivateAssets=all` analyzer package (never flows to consumers) and reads
  `PublicAPI.Shipped.txt`/`PublicAPI.Unshipped.txt` from the project
  directory.

### Dependency ranges

Package dependency version ranges are the same verified ranges already
established and tested by the feature slices (for example
`Microsoft.Agents.AI.Abstractions`/`Microsoft.Agents.AI.Workflows`
`[1.13.0, 1.17.0)`; see
[Session Store contract verification](../persistence/dotnet-contract-research.md)
and [Checkpoint Store contract verification](../persistence/dotnet-checkpoint-contract-research.md)).
This packaging slice did not widen or narrow any previously verified range;
it only made sure the ranges are what actually ship in the `.nuspec`
dependency groups (asserted by `verify-package.ps1`, step 3).

## PublicAPI baseline (pre-1.0)

`PublicAPI.Shipped.txt` is intentionally **empty**. `PublicAPI.Unshipped.txt`
contains the full current public API surface (620 entries), generated with:

```powershell
dotnet format analyzers dotnet/src/MongoDB.AgentFramework/MongoDB.AgentFramework.csproj `
  --diagnostics RS0016 --severity info --verbosity diagnostic
```

This is deliberately **not** treated as a "first published release" baseline
per `quality-release.md` -- there has been no first publish. Framing it as
`Unshipped` (rather than moving it to `Shipped`) keeps the analyzer able to
flag any *future* public addition until an owner explicitly promotes a
release's surface to `Shipped` after that release actually ships. Any
subsequent removal or breaking signature change against `Unshipped` still
fails the build today (RS0016/RS0017), so the safety net is active
immediately, not only after 1.0.

Two Roslyn PublicApiAnalyzer rules, RS0026 (multiple overloads with optional
parameters) and RS0027 (public API with optional parameter(s) should have the
most parameters amongst its public overloads), are suppressed project-wide in
`dotnet/src/MongoDB.AgentFramework/.editorconfig`. This codebase's documented,
tested convention (see `PublicConstructorBaselineTests.cs`) is to add a new
*sibling* constructor overload with a required trailing `ILogger<T>`
parameter rather than widen an existing constructor with an optional
parameter, specifically to preserve binary compatibility for callers using
positional arguments against the existing overloads. RS0026/RS0027 assume the
opposite convention and would otherwise force a design change unrelated to
this packaging slice; the suppression is scoped to exactly these two rules
with the rationale recorded inline.

## Reproducible, deterministic packing

Packing the same commit twice (`verify-package.ps1` step 4) produces
byte-identical `lib/**/*.dll`, `lib/**/*.xml`, `*.nuspec`, and `README.md`
package entries -- this proves the *build* is deterministic. Two OPC
container artifacts are **not** byte-identical across repacks, and this is
expected NuGet.Client `dotnet pack` packaging behavior, not a build
determinism regression:

- `package/services/metadata/core-properties/{guid}.psmdcp` -- the part
  *filename* is a freshly generated random GUID on every `dotnet pack`
  invocation (the part's *content*, aside from the filename, was verified
  identical).
- `_rels/.rels` -- its `Relationship Id="..."` attributes (and the reference
  to the psmdcp GUID filename above) are also regenerated per invocation.

`verify-package.ps1` normalizes/excludes exactly these two artifacts and
requires every other zip entry to match byte-for-byte across two independent
`dotnet pack` invocations from a clean `bin`/`obj`/`artifacts` state.

## Package-content allowlist

`verify-package.ps1` step 2 asserts the packed `.nupkg` contains exactly the
OPC wrapper (`_rels/.rels`, `[Content_Types].xml`, one `psmdcp` part), the
`.nuspec`, `README.md`, and `lib/net{8,9,10}.0/MongoDB.AgentFramework.{dll,xml}`
-- eleven entries, no more, no less. The `.snupkg` contains the same OPC
wrapper, the `.nuspec`, and `lib/net{8,9,10}.0/MongoDB.AgentFramework.pdb`
only -- seven entries. Any sample, test, or internal-only assembly
accidentally referenced by the packable project would add an unexpected
entry and fail this check immediately.

## Clean, isolated consumer smoke test

`dotnet/tests/PackageSmokeTest/PackageSmokeTest.csproj` is a plain console
app that:

- Is **not** added to `MongoDB.AgentFramework.slnx` (so ordinary solution
  builds/tests never touch it, and it can never accidentally pick up a
  `ProjectReference` to the library under test).
- References `MongoDB.AgentFramework` only via `PackageReference
  Version="0.1.0-preview.1"`.
- Ships its own `nuget.config` (`<clear/>` + `nuget.org` + the local packed
  feed at `../../artifacts/packages`), so it can never silently resolve the
  package from an ambient global package cache that happens to have an
  older copy.

`verify-package.ps1` step 5 runs it with `NUGET_PACKAGES` redirected to a
fresh, isolated cache directory under `artifacts/`, guaranteeing the
resolved assembly is the one just packed, not a stale cached copy. The
program constructs (never calls network I/O on) every public feature area
and exits `0` only if all eleven constructions succeed.

## CI workflows

Three new, SHA-pinned workflows follow the existing
`.github/workflows/dotnet-security.yml` pinning convention (comment with the
human-readable tag next to the pinned commit SHA):

- **`dotnet-quality.yml`** -- runs on `pull_request`, push to `main`, and
  `workflow_dispatch`; matrix over `ubuntu-latest`/`windows-latest` (the
  meaningful OS axis, since multi-targeting `net8.0`/`net9.0`/`net10.0`
  already covers every TFM within one build). Steps: checkout, setup .NET
  8/9/10 SDKs, restore, `dotnet format --verify-no-changes`, `dotnet build`
  (Release, warnings-as-errors), `dotnet test` (Release, every project --
  credentialed integration tests skip cleanly without `MONGODB_URI`),
  `scripts/verify-package.ps1 -Configuration Release`, an explicit build of
  every sample project, and artifact upload of the packed `.nupkg`/`.snupkg`.
  Requires no secrets, so it is safe to run against untrusted fork pull
  requests.
- **`dotnet-integration.yml`** -- runs the credentialed `integration-*` test
  categories (`integration-memory`, `integration-history`,
  `integration-rag` [vector], `integration-rag-search`,
  `integration-rag-hybrid`, `integration-index-management`,
  `integration-persistence`) already marked with xUnit
  `[Trait("Category", "...")]` in
  `dotnet/tests/MongoDB.AgentFramework.Tests`. Gated behind a
  `dotnet-integration` GitHub Environment supplying `MONGODB_URI`/
  `MONGODB_DATABASE` secrets, and triggers **only** on push to `main`,
  `schedule`, and `workflow_dispatch` -- never on `pull_request` -- so
  untrusted fork code can never see credentialed secrets. No test code
  changes were needed: every integration test already skips cleanly (xUnit
  `Skip = "..."`) when the environment variables are absent, which is
  exactly what happens when this workflow is not run.
- **`dotnet-sbom-provenance.yml`** -- packs the library, generates an SPDX
  and a CycloneDX SBOM via `anchore/sbom-action`, requests a GitHub
  build-provenance attestation via `actions/attest-build-provenance` (using
  the workflow's real ambient OIDC identity -- nothing invented), prints a
  SHA256 checksum manifest, and uploads all of the above as workflow
  artifacts. Runs on `pull_request`/push-to-`main`/`workflow_dispatch` and
  requires no secrets to succeed. It also contains a **permanently
  disabled** (`if: false`) NuGet package-signing step that documents the
  exact `dotnet nuget sign` invocation a future owner-approved signing
  certificate would need, referencing placeholder secret names
  (`NUGET_SIGNING_CERTIFICATE_PATH`/`_PASSWORD`) purely as documentation --
  it never executes and never claims a certificate exists.

## Versioning and tagging

The package version is `0.1.0-preview.1`. Per `packages.md`/
`compatibility-migration.md`, the eventual tag convention is
`dotnet-v<version>` (for example `dotnet-v0.1.0-preview.1`). **No tag has
been created by this work** -- tagging and publishing are explicitly
excluded from this packaging-engineering slice pending ADR 0013 acceptance.

## Known blockers (not resolved by this slice)

These are genuine external/governance blockers, not omissions in the
packaging engineering itself:

1. **Publishing governance is unconfirmed.** ADR
   [0013](../../decisions/0013-establish-project-and-publishing-governance.md)
   remains `proposed`. There is no owner-confirmed NuGet.org publisher
   identity or organization, no confirmed security contact, no confirmed
   support channel, and no NuGet Trusted Publishing / API-key configuration.
   Nothing in this repository can invent these without owner action.
2. **No signing certificate.** NuGet package (Authenticode-style) signing is
   distinct from the SLSA build-provenance attestation added by
   `dotnet-sbom-provenance.yml`. The signing step is present in that
   workflow but permanently disabled (`if: false`) until an owner issues a
   certificate and the corresponding secrets.
3. **No live MongoDB deployment in this environment.** All `integration-*`
   test categories skip cleanly without `MONGODB_URI`/`MONGODB_DATABASE`;
   `dotnet-integration.yml` is ready to exercise them once a
   `dotnet-integration` environment with real credentials is configured by
   an owner, but that has not been done here.
4. **Session Store remains compatibility-blocked.** See
   [Session Store contract verification](../persistence/dotnet-contract-research.md).
   Because no public session-hosting persistence contract exists upstream
   yet, `MongoDBAgentSessionStore` cannot implement a real framework
   interface, and the package as a whole **cannot claim a 1.0 release**
   even though every other feature area (Memory, Chat History, RAG, Index
   Management, Checkpoints) is otherwise packageable today.
5. **Samples inventory gap.** See
   [.NET samples inventory](dotnet-samples-inventory.md) for the specific
   scenarios from `docs/spec/samples.md` not yet implemented; documented as
   an out-of-scope gap for this packaging-only branch, not silently
   dropped.

## Validation performed

Every check below was run against this branch and is reproducible via the
listed command; see the branch's final commit message(s) for the exact
recorded output.

- `dotnet format MongoDB.AgentFramework.slnx --verify-no-changes` (0 of 351
  files needed formatting).
- `dotnet build MongoDB.AgentFramework.slnx --configuration Release`
  (0 warnings, 0 errors, all TFMs).
- `dotnet test MongoDB.AgentFramework.slnx --configuration Release`
  (1100 passed, 0 failed, 13 skipped -- credentialed integration tests skip
  cleanly without a live deployment).
- `dotnet/scripts/verify-package.ps1 -Configuration Release` (pack, content
  allowlist, nuspec metadata, double-pack reproducibility, isolated
  consumer smoke test, checksum manifest -- all passed).
- Explicit `dotnet build --configuration Release` of every one of the nine
  sample projects under `dotnet/samples/` (0 errors each).
- `dotnet list MongoDB.AgentFramework.slnx package --vulnerable --include-transitive`
  (no vulnerable packages found across all twelve projects).
- `.github/scripts/secret-scan.sh` (no secret patterns found across tracked
  files, run via Git Bash on Windows).
- `git diff --cached --check` (no whitespace errors) before committing.
