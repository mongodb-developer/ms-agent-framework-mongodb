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

`Get-NuspecDependencyGroupsByTfm` also rejects, by throwing before ever
building its lookup dictionary, a nuspec with two `<group>` elements
normalizing to the same TFM (including a case/whitespace-only difference
such as `"  NET8.0  "` vs. `net8.0`) or two `<dependency>` elements sharing
one id within the same group -- ordinary hashtable/`[ordered]` dictionary
assignment silently keeps only the *last* duplicate written and would
otherwise mask a real duplication/drift bug behind an apparently passing
check. `dotnet/scripts/verify-package.metadata.tests.ps1` includes fixtures
for both duplicate shapes, each asserting that every dependency-group-
dependent assertion fails together (since they all share the one throwing
helper call).

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

Packing the same commit twice (`verify-package.ps1` step 4,
`dotnet/scripts/PackageReproducibility.ps1`) proves the *build* is
deterministic by comparing **every** zip entry across two independent
`dotnet pack` invocations from a clean `bin`/`obj`/`artifacts` state --
including the full content of `_rels/.rels` and every `*.psmdcp` entry, not
just their presence. Two OPC container artifacts are not byte-identical
*before* normalization, and this is expected NuGet.Client `dotnet pack`
packaging behavior, not a build determinism regression:

- `package/services/metadata/core-properties/{guid}.psmdcp` -- the part
  *filename* (and every reference to it) is a freshly generated random GUID
  on every `dotnet pack` invocation.
- `_rels/.rels` -- its `Relationship Id="..."` attributes are also
  regenerated per invocation.

`Test-PackageReproducibility` normalizes *only* those two generated-identifier
shapes (the 32-hex-digit psmdcp GUID, wherever it appears in an entry name or
in `_rels/.rels` content, and the `Id="R<hex>"` relationship-id attribute
values) and then requires the resulting normalized bytes of **every** entry,
including `_rels/.rels` and each `*.psmdcp` part in full, to match exactly.
This is a meaningful check, not a tautology that always passes those two
entries: `dotnet/scripts/verify-package.reproducibility.tests.ps1` proves a
genuine `.psmdcp` content difference (e.g. a different embedded
`dc:creator`/`dc:identifier`) and a genuine `_rels/.rels` content difference
(e.g. a relationship pointing at the wrong `Target`) both still fail the
comparison, and separately reproduces the *original*, buggy
"exclude-these-two-entries-entirely" comparison shape to demonstrate it would
have silently passed both of those same corruptions -- confirming the current
implementation is a real regression guard, not just normalization theater.

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
- **`dotnet-sbom-provenance.yml`** (renamed
  `.NET package SBOM (credential-free verification)`) -- runs on
  `pull_request`/push-to-`main`/push of a `dotnet-v*` tag/`workflow_dispatch`,
  with only `contents: read` on its single `sbom` job, and requests **no**
  elevated OIDC/attestation permissions anywhere in the file, so it is safe
  to run against untrusted fork pull requests, including a tag push, since
  attestation authority no longer lives anywhere in this file at all (see the
  architectural note below). It runs `scripts/verify-package.ps1` (the same
  script `dotnet-quality.yml` runs) to pack and verify the library -- never
  an independent, unverified `dotnet pack` -- then generates an SPDX and a
  CycloneDX SBOM via `anchore/sbom-action` over that exact, already-verified
  primary `.nupkg`/`.snupkg`, prints a SHA256 checksum manifest, and uploads
  all of the above as one workflow artifact. On a `dotnet-v*` tag push or a
  manual `workflow_dispatch`, it runs the script's FULL verification
  (allowlist, nuspec metadata, double-pack reproducibility, and the
  multi-TFM isolated consumer smoke test); for `pull_request`/push-to-`main`
  it runs the faster pack+allowlist+metadata-only path, since
  `dotnet-quality.yml` already runs the full script for those triggers on
  both OS matrix legs. It also asserts, for a tag push, that the packed
  `.nuspec`'s `<version>` exactly matches `dotnet-v<version>` for the
  triggering tag (`scripts/verify-release-tag.ps1`/
  `scripts/ReleaseVersionTag.ps1`) -- purely as an early, informational
  signal for maintainers, since **this job's own uploaded artifact is never
  itself the thing that gets attested**; see `dotnet-release-attestation.yml`
  below, which independently re-derives and re-verifies this exact match from
  its own trusted rebuild before ever attesting anything.

- **`dotnet-release-attestation.yml`** (`.NET release provenance
  attestation`) -- the ONLY workflow in this repository holding
  `id-token`/`attestations`/`artifact-metadata` write permissions, and
  deliberately triggered **exclusively** by `workflow_run` reacting to
  `dotnet-sbom-provenance.yml` completing -- never `push`, `workflow_dispatch`,
  `release`, or any other event whose reacting-workflow-file content an
  operator/pusher/tag-creator can otherwise select.

  **Why this is a separate file, and why `workflow_run` specifically:**
  GitHub Actions resolves and runs an ENTIRE workflow file's job graph --
  every job's own `permissions:`/`needs:`/`if:` definitions and step list,
  not merely the scripts a job happens to invoke -- using the exact file
  content present at whatever ref triggered that specific run. An earlier
  revision of this repository's attestation workflow held its elevated
  permissions in the SAME file as its `push`/`workflow_dispatch`-triggered
  `sbom` job, with an in-file `validate-attestation-eligibility` job that
  checked out and ran its scripts from the repository's real `main` branch
  (never `github.ref`) before the OIDC-permissioned job started. That fix was
  **insufficient**: the validator job's own `permissions:`/`needs:`/`if:`
  wiring -- not merely the scripts it called -- was itself sourced from the
  same untrusted `push`/`workflow_dispatch` ref, so whoever controlled that
  ref could rewrite the job graph itself (e.g. remove the `needs:` gate
  entirely, or grant itself the elevated permissions directly), completely
  defeating the split regardless of how carefully any individual step was
  written. `workflow_run` is the one trigger GitHub's own documentation
  guarantees always resolves and runs the reacting workflow's file exactly
  as it exists on the repository's *default branch*, regardless of what
  ref/event triggered the upstream run it reacts to -- the standard
  "unprivileged build, privileged act" pattern GitHub's own security-hardening
  guidance recommends for exactly this situation. (`release: types:
  [published]`, an initially-considered alternative, was rejected: GitHub's
  own event-reference documentation does **not** give the `release` event the
  same default-branch guarantee -- its `GITHUB_SHA`/`GITHUB_REF` are
  documented as the tagged commit/tag ref, structurally identical in trust to
  a tag `push`, so it would not have closed this gap.)

  Because `dotnet-sbom-provenance.yml` itself holds no elevated permissions
  on any job (see above), it is safe to let it run against a fully untrusted
  fork `pull_request` -- a compromised/attacker-controlled copy of its job
  graph can, at most, produce an inert SBOM/package upload or a spurious
  `workflow_run` event, never anything privileged. `dotnet-release-attestation.yml`
  never trusts anything that upstream run built or asserted:

  - `validate-attestation-eligibility` (`permissions: contents: read` only)
    checks out this repository's `main` (`fetch-depth: 0`, explicit and
    auditable alongside -- never a substitute for -- the `workflow_run`
    trigger's own default-branch guarantee) and reconstructs a candidate full
    ref from the upstream run's bare, GitHub-verified
    `head_branch`/`event`/`head_sha` fields
    (`scripts/verify-workflow-run-attestation-ref.ps1`, backed by
    `Resolve-WorkflowRunAttestationRef`/`Test-AttestationRefEligible` in
    `scripts/ReleaseVersionTag.ps1`) -- independently confirming, via this
    trusted checkout's own git history, that a claimed tag genuinely exists
    and points at the exact claimed commit (GitHub's `workflow_run` payload
    cannot otherwise distinguish "a tag named X" from "a branch named X" by
    the bare `head_branch` name alone). A second step,
    **Validate commit is reachable from origin/main (ancestry check)**, then
    proves the claimed commit is real, ancestry-verified history of
    `origin/main` (`git merge-base --is-ancestor`) using the upstream run's
    `head_sha` -- catching a tag pushed against a commit that was never
    actually merged. Manual attestation remains restricted to a
    `workflow_dispatch` of the upstream `sbom` workflow against **exactly**
    `refs/heads/main` (never a tag), for the same reason documented on
    `Test-AttestationRefEligible`: there is no equivalent trusted tag/
    package-version match check for a manual dispatch. The upstream event's
    `event`/`head_branch`/`head_sha` fields are always passed to this script
    through step-level `env:` values, never interpolated directly into
    shell/PowerShell source, for the same injection-surface reason documented
    on `verify-release-tag.ps1`. Its final step records the already-validated
    `head_sha` as this job's `validated-sha` output -- the ONLY thing the
    downstream attestation job is allowed to trust as "the commit to build
    and attest".
  - `provenance-attestation` (`needs: validate-attestation-eligibility`,
    `environment: dotnet-release-attestation`) checks out the validated,
    ancestry-proven commit SHA -- never anything from the upstream `sbom`
    run's own ref, and never this workflow's own `github.sha`/`github.ref`
    (which, under a `workflow_run` trigger, always describe `main`'s current
    tip, not the artifact's commit) -- and **rebuilds the package fresh**
    with `scripts/verify-package.ps1` from that checkout. It never downloads
    or trusts the upstream `sbom` job's own uploaded artifact: because the
    ancestry check above already proved this exact commit is reachable from
    `origin/main`, its content is by definition already-reviewed/merged
    history, so rebuilding from it is provably equivalent in trust to
    building any other ordinary, reviewed commit in this repository. It then
    independently re-verifies the tag/package-version match from this fresh
    rebuild's own artifact (for a tag-push-derived ref) before requesting a
    GitHub build-provenance attestation via `actions/attest-build-provenance`
    (`id-token: write`, `attestations: write`, `artifact-metadata: write`,
    `contents: read` -- the exact permissions that action's own documentation
    requires, nothing broader) over the freshly rebuilt `.nupkg`/`.snupkg`.

    The `dotnet-release-attestation` GitHub Environment (protection
    rules/required reviewers an owner must configure) remains a genuine,
    independent layer of defense-in-depth on top of the `workflow_run`
    trigger isolation and the trusted-`main`-first validator job -- it is
    not this design's sole or primary gate. It also contains the same
    **permanently disabled** (`if: false`) NuGet package-signing step as
    before, documenting the exact `dotnet nuget sign` invocation a future
    owner-approved signing certificate would need, referencing placeholder
    secret names (`NUGET_SIGNING_CERTIFICATE_PATH`/`_PASSWORD`) purely as
    documentation -- it never executes and never claims a certificate
    exists.

## Versioning and tagging

The package version is `0.1.0-preview.1`. Per `packages.md`/
`compatibility-migration.md`, the eventual tag convention is
`dotnet-v<version>` (for example `dotnet-v0.1.0-preview.1`). **No tag has
been created by this work** -- tagging and publishing are explicitly
excluded from this packaging-engineering slice pending ADR 0013 acceptance.
When a `dotnet-v*` tag is eventually pushed, `dotnet-sbom-provenance.yml`'s
`sbom` job asserts the packed `.nuspec`'s `<version>` exactly matches that
tag (`scripts/verify-release-tag.ps1`) before its own SBOM/checksum upload,
as an early, informational signal for maintainers -- a mismatch fails that
job's own upload, but that job's artifact is never itself attested (see
"CI workflows" above). The AUTHORITATIVE tag/version-match gate for
attestation is `dotnet-release-attestation.yml`'s `provenance-attestation`
job, which independently re-derives and re-verifies the same match from its
own trusted, freshly rebuilt artifact before ever attesting anything. Both
workflows' steps that compare a tag name against the packed version pass it
through a step-level `env:` value (`RELEASE_TAG`) and reference only
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
   `dotnet-release-attestation.yml`. The signing step is present in that
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
   `dotnet-release-attestation.yml`'s `provenance-attestation` job references
   a `dotnet-release-attestation` GitHub Environment as an owner-configurable
   protection gate (required reviewers/branch restrictions). This repository
   cannot create or configure that Environment's protection rules itself;
   until an owner does, that particular layer is simply absent. This is not a
   fail-open exposure, though: the workflow-level trigger isolation (this
   file's `on:` is exclusively `workflow_run`, so its entire job graph --
   including this Environment reference and every job's `permissions:`/
   `needs:`/`if:` wiring -- is always resolved from the repository's default
   branch, never an operator-selected/triggering ref) and the separate,
   no-OIDC `validate-attestation-eligibility` job's steps ("Validate upstream
   event/ref/commit is eligible"
   [`scripts/verify-workflow-run-attestation-ref.ps1`/
   `Test-AttestationRefEligible`, always run from a trusted `main` checkout]
   and "Validate commit is reachable from origin/main") independently require
   a real `dotnet-v*` release tag or exactly `refs/heads/main` with
   ancestry-verified commit history before the OIDC-permissioned
   `provenance-attestation` job is even allowed to start, regardless of
   whether the Environment has any protection configured. The Environment
   remains a genuine, recommended additional layer of defense-in-depth
   (manual reviewer approval) once an owner configures it; it was never
   intended to be, and is not, the *only* thing standing between an
   arbitrary triggering event and attestation.

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
- `dotnet/scripts/verify-package.reproducibility.tests.ps1` (new self-test
  for `PackageReproducibility.ps1`: 6 cases -- GUID/relationship-id-only
  differences across two real packed-nupkg entry maps normalize and pass;
  a genuine `.psmdcp` content difference [different `dc:creator`] fails,
  attributed to the `.psmdcp` entry specifically; a genuine `_rels/.rels`
  content difference [wrong `Target`] fails, attributed to `_rels/.rels`
  specifically; a genuine real-payload `.dll` difference still fails
  [regression guard]; a missing/extra entry reports as an
  `EntrySetMismatch`; and a reproduction of the *original*, buggy
  "exclude `.psmdcp`/`_rels/.rels` entirely" comparison shape is shown to
  have silently passed both the psmdcp and `_rels/.rels` content-difference
  cases above, proving the current implementation is a meaningful
  regression guard rather than normalization theater -- all 6 passed).
- `dotnet/scripts/resolve-workflow-run-attestation-ref.tests.ps1` (new
  pure-function self-test for `Resolve-WorkflowRunAttestationRef` in
  `ReleaseVersionTag.ps1`: 8 assertions -- a `push` upstream event
  reconstructs `refs/tags/<head_branch>`; a `workflow_dispatch` upstream
  event reconstructs `refs/heads/<head_branch>` for both `main` and an
  arbitrary branch [rejection of non-main branches is
  `Test-AttestationRefEligible`'s job, not this function's]; `pull_request`,
  an unrecognized event name, an empty event name, and a
  wrong-case `PUSH` all produce no ref candidate at all -- all 8 passed).
- `dotnet/scripts/verify-workflow-run-attestation-ref.tests.ps1` (new
  integration-style self-test using a REAL scratch git repository, not
  fixture strings, for `verify-workflow-run-attestation-ref.ps1`: 10
  assertions -- a real tag pointing at the exact claimed commit is eligible;
  the same real tag with a *different* claimed commit is rejected [the
  upstream-run-claims-a-commit-the-tag-does-not-point-at mismatch]; a
  nonexistent tag name is rejected; a real *branch* named `main` in the
  scratch repo [not a tag] is rejected even though its bare name alone would
  otherwise look tag-shaped -- proving the head_branch-is-ambiguous-with-a-
  real-tag-name gap this script closes is not merely theoretical;
  `workflow_dispatch` against `main` is eligible and against an arbitrary
  branch is rejected; `pull_request` is rejected regardless of
  head_branch/head_sha -- all 10 passed).
- `dotnet/scripts/verify-attestation-ref.tests.ps1` (self-test for
  `Test-AttestationRefEligible` in `ReleaseVersionTag.ps1`: ~23 assertions covering
  valid/malformed tag pushes; `workflow_dispatch` targeting `refs/heads/main`
  [eligible] vs. an arbitrary feature branch [the core fail-open regression
  this function closes -- NOT eligible]; `workflow_dispatch` targeting a
  validly-formed, otherwise-real release tag [the tag/package-version-
  mismatch regression this round closes -- NOT eligible, since manual
  dispatch has no trusted tag/package-version match check the way a real tag
  `push` does]; `push` targeting `refs/heads/main` [NOT eligible -- only
  `workflow_dispatch` may target main]; injection-shaped refs [`$()`,
  quote+semicolon, trailing whitespace] on both trigger types; other event
  types [`pull_request`, `schedule`, an empty event name]; and
  case-sensitivity of both the event name and the ref
  [`PUSH`/`REFS/HEADS/main`/`refs/heads/MAIN` are all correctly rejected via
  `-ceq`, not silently matched by PowerShell's case-insensitive default
  `-eq`] -- all ~23 passed).
- `dotnet/scripts/verify-workflow-attestation-structure.tests.ps1` (static/
  structural regression self-test, rewritten this round for the two-file
  `workflow_run`-triggered architecture, generically auditing **every**
  workflow file under `.github/workflows/` -- no live GitHub Actions run and
  no new YAML-parsing dependency required -- proving: (1) global audit: any
  job across any workflow file requesting `id-token`/`attestations`/
  `artifact-metadata` write permissions must live in a file whose ONLY
  top-level trigger is `workflow_run` [currently only
  `dotnet-release-attestation.yml` qualifies; every other workflow file has
  no elevated-permission job and is unconstrained]; (2)
  `dotnet-sbom-provenance.yml` has NO job anywhere with elevated permissions
  and no longer defines the old `validate-attestation-eligibility`/
  `provenance-attestation` jobs at all, and its `on:` excludes `release`;
  (3) `dotnet-release-attestation.yml`'s `on:` is EXACTLY `workflow_run`
  [no sibling `push`/`workflow_dispatch`/`release`/`pull_request` trigger]
  referencing the exact upstream workflow name, its
  `validate-attestation-eligibility` job requests only `contents: read` and
  pins `ref: main`, its `provenance-attestation` job retains the elevated
  permissions, `needs:` the validator job, checks out only the validator's
  `validated-sha` output [never `github.ref`/`github.sha`/
  `github.event.workflow_run.head_sha` directly], and rebuilds fresh via
  `verify-package.ps1` rather than downloading the upstream `sbom` job's
  artifact. Also includes a "malicious selected-ref validator" fixture
  proving a forged validator COULD report a false "eligible" verdict --
  concretely justifying why the structural assertions matter -- and a
  functional regression assertion that `workflow_dispatch` against a real,
  validly-formed release tag is rejected. All 27 assertions passed against
  the current two-file design; a genuine bug in this rewrite itself was
  caught and fixed during this round -- see "PowerShell single-element-list
  flattening regression" below.
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
- `python -c "import yaml; yaml.safe_load(...)"` against all five workflow
  files (`dotnet-quality.yml`, `dotnet-sbom-provenance.yml`,
  `dotnet-release-attestation.yml`, `dotnet-integration.yml`,
  `dotnet-security.yml`) -- all parse cleanly, and `dotnet-sbom-provenance.yml`
  shows only its single `sbom` job, while `dotnet-release-attestation.yml`
  shows exactly `validate-attestation-eligibility`/`provenance-attestation`
  with the expected `if:`/`permissions:`/`needs:`/`outputs:`/step list for
  each.
- Manual grep audit of every `${{\s*(github\.|inputs\.|steps\.)` occurrence
  across all five workflow files' `run:` step bodies, confirming each one is
  passed through a step-level `env:` value and referenced as
  `$NAME`/`$env:NAME` inside the shell/PowerShell source, never interpolated
  directly into that source text -- the same injection-surface pattern
  already applied to `verify-release-tag.ps1`'s `-RefName` parameter in an
  earlier round; `dotnet-release-attestation.yml`'s new
  `github.event.workflow_run.event`/`.head_branch`/`.head_sha` occurrences
  follow the identical pattern.
- **PowerShell single-element-list flattening regression (caught and fixed
  during this round's own rewrite):** `verify-workflow-attestation-structure.tests.ps1`'s
  new `Get-WorkflowTriggerKeys` helper initially returned a bare
  `System.Collections.Generic.List[string]` from `return $keys`; PowerShell's
  default pipeline enumeration silently unwraps a single-element collection
  to a bare scalar on return, which is invisible for workflows with 2+
  triggers (e.g. `dotnet-sbom-provenance.yml`'s three) but corrupts the
  result for `dotnet-release-attestation.yml`'s intentionally single
  `workflow_run` trigger -- `.Count` still (correctly) reported `1` via
  PowerShell's synthetic scalar adapter, but indexing `[0]` then silently
  indexed into the *string's characters* instead of the list, causing a
  false test failure that looked like a real defect. Fixed by returning
  `, $keys` (the unary comma operator forces single-object emission of the
  list itself); the self-test was re-run and all 27 assertions pass. This
  is recorded here as a genuine defect this round's own tooling introduced
  and then caught via its own execution, not merely a design note.
- `git diff --cached --check` (no whitespace errors) before committing.
