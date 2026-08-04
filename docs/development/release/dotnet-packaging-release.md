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
  package. Asserts an *exact* expected entry set and multiplicity
  (`dotnet/scripts/PackageAllowlist.ps1`), with its own fixture-based
  self-test (`verify-package.allowlist.tests.ps1`).
- A clean, isolated NuGet consumer smoke test
  (`dotnet/tests/PackageSmokeTest/`) that restores the *packed* `.nupkg` (not
  a project reference) into an isolated package cache and constructs every
  public feature area: Memory, Chat History, RAG (all four
  `MongoDBSearchMode` values -- `VectorAnn`/`VectorEnn`/`FullText`/
  `HybridRrf` -- plus `MongoDBRAGContextProvider`), both Index Managers,
  Session Store, and Checkpoint Store (including
  `CheckpointManager.CreateJson`). Construction-only, no MongoDB I/O -- it
  does not require a live deployment. Multi-targets every shipped TFM
  (`net8.0`/`net9.0`/`net10.0`), each run independently via `dotnet run -f`.
- An explicit Microsoft Agent Framework compatibility matrix (`1.13.0`
  minimum, `1.16.0` newest verified) exercised via an `$(AgentFrameworkVersion)`
  MSBuild property override -- see
  [.NET Agent Framework compatibility matrix](dotnet-agent-framework-compatibility-matrix.md).
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

Step 3's nuspec-metadata assertions are exact, not merely "at least one
dependency exists": `PackageMetadataAssertions.ps1` asserts the packed
`.nuspec` has *exactly* three dependency groups -- `net8.0`, `net9.0`, and
`net10.0`, no more and no fewer -- and that each group has *exactly* the
five expected direct package ids and version ranges
(`Microsoft.Agents.AI.Abstractions`, `Microsoft.Agents.AI.Workflows`,
`Microsoft.Extensions.AI.Abstractions`, `Microsoft.Extensions.Logging.Abstractions`,
`MongoDB.Driver`), and that no analyzer or source-link package (which are
referenced with `PrivateAssets="all"` and correctly never flow into the
nuspec) leaks into any group's dependency list. A wrong/missing/extra
dependency group, a wrong/missing/extra package id within a group, or a
wrong version range all fail this check individually and by name; see
`dotnet/scripts/verify-package.metadata.tests.ps1` for the fixtures proving
each failure mode, and
`dotnet/scripts/verify-package.metadata-integration.tests.ps1` for an
end-to-end integration self-test that packs the real project, parses the
real (`System.Xml.XmlElement`) `.nuspec` metadata -- not a synthetic
fixture object -- and invokes the same assertions from three deliberately
different invocation-scope shapes (see "Closure/scope regression" below).

#### Closure/scope regression (and why the original self-test missed it)

A review pass reported that `verify-package.ps1`'s production Step 3 failed
all three per-TFM dependency-group assertions with "the term
'Get-NuspecDependencyGroupsByTfm' is not recognized", even though
`verify-package.metadata.tests.ps1`'s self-test passed the exact same named
assertions. Investigation found two distinct, closely related bugs in
`PackageMetadataAssertions.ps1`'s use of `.GetNewClosure()`, plus a gap in
why the existing self-test could not have caught either one:

1. **Function-call resolution.** The three per-TFM assertions are each
   built with `.GetNewClosure()` (so each can independently capture its own
   loop-scoped TFM name) and, as written, called sibling function
   `Get-NuspecDependencyGroupsByTfm` by bare command name from inside the
   closured scriptblock body. `.GetNewClosure()` gives a scriptblock its
   own isolated dynamic module/session state; ordinary lexical *variables*
   referenced in the body are snapshotted into that state by
   `.GetNewClosure()` itself, but a bare function *call* is instead resolved
   by ordinary PowerShell command-name lookup at the moment the closure is
   later invoked -- and that lookup depends on the closure's own session
   state chaining back to whatever scope originally dot-sourced this file,
   which is an implementation detail, not a documented guarantee, and does
   not reliably hold in every invocation context. **Fix:** capture each
   helper function's definition as a scriptblock *value* (`${function:Name}`)
   in `Get-NuspecMetadataAssertions`'s own scope -- where both helpers are
   unconditionally visible, since this file dot-sources them together --
   and invoke that captured value via `&` from inside the closure, instead
   of relying on ambient command-name resolution.

2. **A second, more dangerous bug found while reproducing the first.**
   While building an integration self-test to reproduce (1) against a real
   nuspec, the *other* (non-per-TFM) assertions -- `id equals
   MongoDB.AgentFramework`, `version is set`, etc., none of which were
   `.GetNewClosure()`'d at all -- were discovered to silently return the
   wrong boolean `$false` when invoked from a scope that does not happen to
   have an in-scope variable literally named `$Metadata`. A plain
   (non-closed) PowerShell scriptblock is *dynamically* scoped when later
   invoked via `& $Body`: it resolves a free variable like `$Metadata` by
   walking the actual call stack at the moment of invocation, not by
   binding to wherever the scriptblock was lexically written. These
   assertions had only ever "worked" because both `verify-package.ps1`
   (`$metadata = $nuspec.package.metadata`) and the existing self-test's
   `Test-AllAssertions` helper (parameter `$Metadata`) happen to use a
   variable with that exact name (PowerShell variable names are
   case-insensitive) somewhere in the call chain -- a naming coincidence,
   not real closure semantics, and strictly more dangerous than bug (1)
   because it fails silently (wrong result, no error) rather than loudly.
   **Fix:** every assertion scriptblock, not only the per-TFM ones, is now
   `.GetNewClosure()`'d, so `$Metadata` is explicitly snapshotted as data
   regardless of what variable names exist in whatever scope later invokes
   it.

**Why the original self-test missed both bugs:** `verify-package.metadata.tests.ps1`
always builds and evaluates assertions using a single, fixed invocation
shape -- a `Test-AllAssertions` helper that both is one function level deep
*and* happens to name its parameter `$Metadata` -- so it never varied the
invocation-scope shape enough to expose either bug. It also only ever used
synthetic `[pscustomobject]` fixtures, never a real, `[xml]`-parsed
`System.Xml.XmlElement`, so it could not tell the two apart from a shape
that genuinely differs from production. `verify-package.metadata-integration.tests.ps1`
closes this gap: it packs the real project, parses the real nuspec, and
specifically varies the invocation-scope shape (flat top-level, one function
level deep, and two function levels deep via a second dot-sourced file) to
prove the fix holds regardless of caller shape, not merely in the one shape
that happened to work before.

Both Agent Framework `PackageReference` entries in
`MongoDB.AgentFramework.csproj` (and the matching explicit reference in the
test project) are expressed via a `$(AgentFrameworkVersion)` MSBuild
property that defaults to the same `[1.13.0, 1.17.0)` range when not
overridden. This lets a CI matrix (or a local developer) pin the *exact*
resolved version at either bound with `-p:AgentFrameworkVersion=1.13.0` (or
`1.16.0`) without editing any tracked file, while an ordinary build/pack
with no override still resolves to the range's floor (`1.13.0`), preserving
prior default behavior and the shipped package's range dependency. See
[.NET Agent Framework compatibility matrix](dotnet-agent-framework-compatibility-matrix.md)
for the verification script, CI job, and findings.

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

`dotnet/scripts/PackageAllowlist.ps1` factors the allowlist check into a
dependency-free, pure function (`Test-PackageContentAllowlist`) that compares
a package's actual (normalized) entry list against an **exact** expected
entry set, including multiplicity -- not a "does every entry match some
allowed pattern" regex check, which can never notice a required entry being
silently absent. `verify-package.ps1` step 2 asserts the packed `.nupkg`
contains exactly the OPC wrapper (`_rels/.rels`, `[Content_Types].xml`, one
`psmdcp` part), the `.nuspec`, `README.md`, and
`lib/net{8,9,10}.0/MongoDB.AgentFramework.{dll,xml}` -- eleven entries, no
more, no fewer, each exactly once. The `.snupkg` contains the same OPC
wrapper, the `.nuspec`, and `lib/net{8,9,10}.0/MongoDB.AgentFramework.pdb`
only -- seven entries. Any sample, test, or internal-only assembly
accidentally referenced by the packable project would add an unexpected
entry and fail this check immediately; a missing required entry (for example
a TFM's assembly failing to build) fails it just as loudly; a duplicated
entry (wrong multiplicity) fails it too.

`dotnet/scripts/verify-package.allowlist.tests.ps1` is a self-test for
`Test-PackageContentAllowlist` itself, run directly (no packed artifact
needed) against six deliberately-broken fixtures: a valid entry set (must
pass), a required file removed (must fail as Missing), an extra/unexpected
file added (must fail as Unexpected), a required entry duplicated (must fail
as MultiplicityMismatch, proving multiplicity is actually checked, not just
set membership), two different pack runs' random `psmdcp` GUIDs (both must
still pass, proving GUID normalization works), and a malformed
`core-properties` filename that is not a real 32-hex-digit GUID (must fail,
proving normalization does not silently wave through anything under
`core-properties/`).

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
- Multi-targets `net8.0;net9.0;net10.0` -- every TFM
  `MongoDB.AgentFramework` itself ships.

`verify-package.ps1` step 5 runs it with `NUGET_PACKAGES` redirected to a
fresh, isolated cache directory under `artifacts/`, guaranteeing the
resolved assembly is the one just packed, not a stale cached copy. The
`nuget.config` also declares `packageSourceMapping`:
`MongoDB.AgentFramework` is mapped *exclusively* to the local packed feed
(`../../artifacts/packages`), and every other package id is mapped to
`nuget.org` only -- so a same-id/same-version package could never be
silently satisfied from the wrong source even if both feeds happened to
carry a matching version. `verify-package.ps1` goes one step further than
trusting a successful restore exit code: after restore it parses the
generated `tests/PackageSmokeTest/obj/project.assets.json`, locates the
`MongoDB.AgentFramework` library entry, and compares its recorded `sha512`
hash against a fresh SHA512 computed over the actual packed `.nupkg` bytes
(`ConsumerCacheVerification.ps1`'s `Test-ConsumerCacheResolvedPackedPackage`,
self-tested in `verify-consumer-cache.tests.ps1`) -- proving the restore
resolved *that exact local artifact*, not merely "a" package satisfying the
version range. A single `dotnet restore` covers the whole multi-targeted
project graph; the script
then runs `dotnet run --framework <tfm>` once per TFM read directly from the
project's own `<TargetFrameworks>` element (so this script can never
silently drift out of sync with the project it runs), proving the package
actually restores and *runs* -- not just compiles -- on every shipped
target. The program constructs (never calls network I/O on) every public
feature area, including all four `MongoDBSearchMode` values (`VectorAnn`,
`VectorEnn`, `FullText`, `HybridRrf`), and exits `0` on each TFM only if
every construction succeeds.

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
  every sample project (looped one `dotnet build` invocation per project,
  discovered dynamically from the `samples/` directory listing -- `dotnet
  build` only ever accepts a single project/solution argument, so a single
  multi-path invocation is not possible), and artifact upload of the packed
  `.nupkg`/`.snupkg`. Requires no secrets, so it is safe to run against
  untrusted fork pull requests.
- **`dotnet-agent-framework-compat`** (a job within `dotnet-quality.yml`,
  matrixed over `agent-framework-version: ["1.13.0", "1.16.0"]`) -- see
  [.NET Agent Framework compatibility matrix](dotnet-agent-framework-compatibility-matrix.md).
  Requires no secrets.
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
  untrusted fork code can never see credentialed secrets. Because a
  job-level `if:` cannot reference `secrets.*`, an absent secret cannot be
  used to skip the job out of existence (which would report a
  false-green no-op); instead a preflight step fails loudly and
  actionably first when either required secret is empty. Each category's
  `dotnet test` step also runs with a per-job TRX logger
  (`--logger "trx;LogFileName=<job>.trx"`), and a following step
  (`dotnet/scripts/assert-trx-executed.ps1`, backed by the shared
  `TrxResults.ps1`/`Get-TrxExecutedCount` also used by the Agent Framework
  compatibility script) parses the TRX's `<Counters executed="...">` and
  fails the job unless it is strictly greater than zero -- so a
  credentialed run whose filtered category matched nothing, or whose tests
  all skipped themselves, fails rather than reporting a false-green
  "0 executed, 0 failed". No production test code changes were needed
  beyond this: every integration test already skips cleanly (xUnit
  `Skip = "..."`) when the environment variables are absent, which is
  exactly what a credential-free local contributor run still exercises.
- **`dotnet-sbom-provenance.yml`** -- split into two trust-gated jobs:
  - `sbom` runs on `pull_request`/push-to-`main`/push of a `dotnet-v*` tag/
    `workflow_dispatch`, with only `contents: read`. It runs
    `scripts/verify-package.ps1` (the same script `dotnet-quality.yml` runs)
    to pack and verify the library -- never an independent, unverified
    `dotnet pack` -- then generates an SPDX and a CycloneDX SBOM via
    `anchore/sbom-action` over that exact, already-verified primary
    `.nupkg`/`.snupkg`, prints a SHA256 checksum manifest, and uploads all of
    the above as one workflow artifact. On a trusted `dotnet-v*` tag push or
    a manual `workflow_dispatch`, it runs the script's FULL verification
    (allowlist, nuspec metadata, double-pack reproducibility, and the
    multi-TFM isolated consumer smoke test); for `pull_request`/push-to-
    `main` it runs the faster pack+allowlist+metadata-only path, since
    `dotnet-quality.yml` already runs the full script for those triggers on
    both OS matrix legs, and this workflow is the only one of this
    repository's .NET workflows that ever triggers on a tag push at all, so
    it cannot assume any other workflow already fully verified a tag-pushed
    artifact. It also asserts, for a trusted tag push only, that the packed
    `.nuspec`'s `<version>` exactly matches `dotnet-v<version>` for the
    triggering tag (`scripts/verify-release-tag.ps1`/
    `scripts/ReleaseVersionTag.ps1`, self-tested in
    `scripts/verify-release-tag.tests.ps1`) before anything is uploaded --
    refusing to upload/attest a mismatched tag/version pair. A manual
    `workflow_dispatch` instead only *records* the packed version (there is
    no tag to validate against in that mode). Requires no secrets and never
    requests elevated OIDC/attestation permissions, so it is safe to run
    against untrusted fork pull requests.
  - `provenance-attestation` (`needs: sbom`) downloads that exact artifact
    (rather than re-packing, so the attested bytes are provably the same
    ones the `sbom` job already verified, tag/version-checked, SBOM'd, and
    checksummed) and requests a GitHub build-provenance attestation via
    `actions/attest-build-provenance`
    (`id-token: write`, `attestations: write`, `artifact-metadata: write`,
    `contents: read` -- the exact permissions that action's own
    documentation requires, nothing broader). It runs **only** for a
    trusted push of a `dotnet-v*` tag, or a `workflow_dispatch` run with an
    explicit `confirm_attestation: yes` input, and is additionally gated by
    a `dotnet-release-attestation` GitHub Environment an owner must
    configure (protection rules/required reviewers) before this job can
    ever execute -- a fork's `pull_request` event can never reach it at
    all, since that event type is not in its trigger list. It also contains
    the same **permanently disabled** (`if: false`) NuGet package-signing
    step as before, documenting the exact `dotnet nuget sign` invocation a
    future owner-approved signing certificate would need, referencing
    placeholder secret names (`NUGET_SIGNING_CERTIFICATE_PATH`/`_PASSWORD`)
    purely as documentation -- it never executes and never claims a
    certificate exists.

## Versioning and tagging

The package version is `0.1.0-preview.1`. Per `packages.md`/
`compatibility-migration.md`, the eventual tag convention is
`dotnet-v<version>` (for example `dotnet-v0.1.0-preview.1`). **No tag has
been created by this work** -- tagging and publishing are explicitly
excluded from this packaging-engineering slice pending ADR 0013 acceptance.
When a `dotnet-v*` tag is eventually pushed, `dotnet-sbom-provenance.yml`'s
`sbom` job asserts the packed `.nuspec`'s `<version>` exactly matches that
tag (`scripts/verify-release-tag.ps1`) before any upload or attestation can
proceed, so a tag/version mismatch fails the workflow rather than silently
publishing/attesting the wrong artifact. Both steps that compare
`github.ref_name` against the packed version pass it through a
step-level `env: RELEASE_TAG: ${{ github.ref_name }}` and reference only
`$env:RELEASE_TAG` inside the PowerShell `run:` block -- never
string-interpolating the ref directly into script text -- and
`Test-ValidReleaseTagGrammar` (`ReleaseVersionTag.ps1`) additionally
rejects any ref not matching `^dotnet-v[0-9A-Za-z][0-9A-Za-z.-]*$` before
any comparison runs, as defense-in-depth against a maliciously-named ref
attempting shell/PowerShell injection (self-tested end-to-end in
`verify-release-tag.tests.ps1` with real `$()`/quote/semicolon/backtick
payloads passed as actual `-RefName` parameters).

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
   workflow's `provenance-attestation` job but permanently disabled
   (`if: false`) until an owner issues a certificate and the corresponding
   secrets.
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
6. **`dotnet-release-attestation` GitHub Environment not yet configured.**
   `dotnet-sbom-provenance.yml`'s `provenance-attestation` job references a
   `dotnet-release-attestation` GitHub Environment as an explicit,
   owner-configurable protection gate (required reviewers/branch
   restrictions). This repository cannot create or configure that
   Environment's protection rules itself; until an owner does, the job's
   `if:` condition (trusted `dotnet-v*` tag push, or `workflow_dispatch`
   with an explicit `confirm_attestation: yes` input) is necessary but not
   sufficient -- GitHub will not grant the environment's protections until
   they are configured, which is itself an additional (fail-safe, not
   fail-open) barrier to accidental attestation runs.

## Validation performed

Every check below was run against this branch and is reproducible via the
listed command; see the branch's final commit message(s) for the exact
recorded output.

- `dotnet format MongoDB.AgentFramework.slnx --verify-no-changes` (no files
  needed formatting).
- `dotnet build MongoDB.AgentFramework.slnx --configuration Release`
  (0 warnings, 0 errors, all TFMs).
- `dotnet test MongoDB.AgentFramework.slnx --configuration Release`
  (1100 passed [974 + 126], 0 failed, 13 skipped [10 + 3] across both test
  projects -- credentialed integration tests skip cleanly without a live
  deployment).
- `dotnet/scripts/verify-package.ps1 -Configuration Release` (pack; exact
  package-content allowlist with multiplicity for both `.nupkg` [11
  entries] and `.snupkg` [7 entries]; nuspec metadata asserted via
  `Test-NuspecAssertion`/`Get-NuspecMetadataAssertions`
  [`PackageMetadataAssertions.ps1`], which requires each assertion to
  return a strict boolean rather than discarding its result; double-pack
  reproducibility with every real payload entry byte-identical; isolated
  consumer smoke test across all three shipped TFMs
  [net8.0/net9.0/net10.0], each constructing all public-API surfaces
  including all four `MongoDBSearchMode` values (`VectorAnn`/`VectorEnn`/
  `FullText`/`HybridRrf`); checksum manifest -- all steps passed).
- `dotnet/scripts/verify-package.allowlist.tests.ps1` (self-test: 17
  assertions across 6 fixtures -- valid entries pass; a missing required
  file, an unexpected extra file, and a duplicated entry each fail with the
  correct classification [`Missing`/`Unexpected`/`MultiplicityMismatch`];
  two independently random `psmdcp` GUIDs both normalize and pass; a
  malformed non-GUID `psmdcp` filename fails -- all 17 passed).
- `dotnet/scripts/verify-package.metadata.tests.ps1` (self-test: 33+
  assertions -- `Test-NuspecAssertion`'s contract for `$true`/`$false`
  [without throwing, the shape of the original bug]/thrown/non-boolean/
  `$null` results; every one of the 18 required nuspec assertions passes
  against a fully valid fixture (including the exact-dependency-group/
  package/version-range/analyzer-absence assertions); single-field and
  dependency-mutation fixtures each prove *exactly and only* the
  corresponding named assertion(s) fail -- including zero dependency
  groups, a missing `net10.0` group, an extra unexpected `net7.0` group, a
  missing/extra package id within a group, a wrong version range, and an
  analyzer package leaking into a group -- all passed. Reintroducing the
  original bug shape into `PackageMetadataAssertions.ps1` [ignoring the
  scriptblock's return value] makes this self-test fail, confirming it is a
  meaningful regression guard, not a tautological pass).
- `dotnet/scripts/verify-package.metadata-integration.tests.ps1` (new
  integration self-test, added to close a gap the self-test above could not
  catch -- see "Closure/scope regression" below for the full root-cause
  narrative. Packs the real `MongoDB.AgentFramework.csproj` [Release,
  deterministic/CI mode], extracts and `[xml]`-parses the real packed
  `.nuspec` exactly as `verify-package.ps1`'s Step 3 does, confirms
  `$Metadata` is a genuine `System.Xml.XmlElement` [not a synthetic
  fixture], then invokes `Get-NuspecMetadataAssertions`/`Test-NuspecAssertion`
  from three deliberately different invocation-scope shapes -- flat
  top-level [matching `verify-package.ps1`], one function level deep
  [matching this self-test's own wrapper shape], and two function levels
  deep via a second, separately dot-sourced helper file [a shape neither
  other script exercises] -- asserting every required assertion, especially
  the three per-TFM dependency-group checks, executes and passes in all
  three shapes; finally corrupts one dependency's version range directly in
  the raw nuspec XML text [not a fixture object] and confirms exactly the
  corresponding per-TFM assertion fails against real XML shape too -- all
  assertions passed).
- `dotnet/scripts/verify-consumer-cache.tests.ps1` (self-test for
  `ConsumerCacheVerification.ps1`'s content-hash proof: 7 assertions --
  hash reproduction against .NET's own SHA512, a matching hash passes, a
  wrong/stale hash fails, a missing library entry fails, a non-`package`
  type fails, a missing `sha512` fails, and looking up the wrong version
  fails -- all 7 passed).
- `dotnet/scripts/verify-release-tag.tests.ps1` (self-test: 30+ assertions --
  pure tag/version comparison for exact match, mismatch, pre-release
  match/mismatch, missing `dotnet-` prefix, non-tag branch ref, and
  case-sensitivity; `Test-ValidReleaseTagGrammar` fixtures for valid tags,
  a plain branch name, an empty ref, an incomplete prefix, wrong case, and
  six injection-shaped strings (`$()`, quotes, semicolons, backticks,
  `&&`); `Get-NupkgVersion` parses both a normal and a pre-release version
  from a real in-memory `.nuspec`-containing zip fixture; end-to-end
  process-exit-code checks of `verify-release-tag.ps1` for both the
  `-EnforceMatch` [tag-push] and record-only [`workflow_dispatch`]
  invocation shapes, plus real injection-shaped `-RefName` values passed as
  actual process parameters proving no embedded command ever executes
  [marker-file technique] and the process still exits 1 -- all passed).
- `dotnet/scripts/verify-trx-results.tests.ps1` and
  `dotnet/scripts/verify-assert-trx-executed.tests.ps1` (self-tests for the
  shared `TrxResults.ps1`/`Get-TrxExecutedCount` function and its
  `assert-trx-executed.ps1` CLI wrapper used by both
  `verify-agent-framework-compatibility.ps1` and `dotnet-integration.yml`:
  a well-formed TRX with a nonzero executed count passes/exits 0; a TRX
  where every matched test was skipped [`executed="0"`, `total>0`] fails/
  exits 1 rather than returning the total or `$null`; a missing TRX file
  and a malformed/non-XML TRX file both fail/exit 1 rather than throwing
  -- all passed).
- `dotnet/scripts/verify-agent-framework-compatibility.ps1 -Configuration
  Release` (restores `MongoDB.AgentFramework.Tests.csproj` -- which pulls in
  `MongoDB.AgentFramework.csproj` via `ProjectReference` -- and builds both
  projects with `-p:AgentFrameworkVersion` overridden to both `1.13.0` and
  `1.16.0`; asserts the exact resolved `Microsoft.Agents.AI.Abstractions`/
  `Workflows` versions at each bound from `project.assets.json`; confirms
  transitive `Microsoft.Extensions.Logging.Abstractions` stays at `10.0.9`
  -- within its declared range -- at both bounds; runs
  `dotnet test --no-build --no-restore` with a TRX logger and asserts,
  via the shared `TrxResults.ps1`/`Get-TrxExecutedCount` function also used
  by `assert-trx-executed.ps1`/`dotnet-integration.yml`, that the
  TRX's `executed` counter is nonzero [974 executed / 984 total / 10
  skipped at both bounds] rather than trusting console output or exit code
  alone -- both bounds passed).
- Explicit `dotnet build --configuration Release` of every one of the nine
  sample projects under `dotnet/samples/` individually (0 errors each), and
  separately via `dotnet-quality.yml`'s per-project loop step (`dotnet
  build` accepts only one project/solution path per invocation, so a single
  multi-path command is not possible; the workflow discovers sample
  projects dynamically from the `samples/` directory listing rather than a
  hardcoded list).
- `dotnet list MongoDB.AgentFramework.slnx package --vulnerable --include-transitive`
  (no vulnerable packages found across all twelve projects).
- `.github/scripts/secret-scan.sh` (no secret patterns found across tracked
  files, run via Git Bash on Windows) and
  `.github/scripts/secret-scan.test.sh` (self-test: detects a planted AWS
  key fixture, respects the `SENTINEL-SECRET-` exclusion fixture, and
  reports no findings on a clean fixture -- all three scenarios passed).
- `python -c "import yaml; yaml.safe_load(...)"` against all three
  workflow files (`dotnet-quality.yml`, `dotnet-sbom-provenance.yml`,
  `dotnet-integration.yml`) -- all parse cleanly, and
  `dotnet-quality.yml`/`dotnet-sbom-provenance.yml` show the expected job
  names (`dotnet-quality`, `dotnet-agent-framework-compat`; `sbom`,
  `provenance-attestation`).
- `git diff --cached --check` (no whitespace errors) before committing.
