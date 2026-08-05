# .NET release operations

This is the operator guide for implementation-map
[slice 20](../../spec/implementation-map.md). The normative policy is
[version control and release strategy](version-control-and-release-strategy.md);
GitHub branch rulesets, Actions settings, environments, variables, and secrets
are defined in the
[GitHub repository configuration](../../release/github-repository-configuration.md);
package internals are described in
[.NET packaging and release engineering](dotnet-packaging-release.md). The
design follows ADRs [0004](../../decisions/0004-publish-independent-language-packages.md),
[0011](../../decisions/0011-release-features-through-staged-quality-gates.md),
[0013](../../decisions/0013-establish-project-and-publishing-governance.md),
and [0014](../../decisions/0014-publish-only-tested-compatibility-ranges.md).

## Branch promotion and version bump

`build/dotnet-packaging-release` is a non-publishing staging branch. Change the
single `<Version>` in
`dotnet/src/MongoDB.AgentFramework/MongoDB.AgentFramework.csproj`, update
`dotnet/tests/PackageSmokeTest/PackageSmokeTest.csproj` to consume that version,
and update `dotnet/CHANGELOG.md`. Run the rehearsal below and merge a reviewed
pull request to `main`.

Every push to the staging branch automatically starts all readiness surfaces
below. Pull requests targeting `build/dotnet-packaging-release` start the
credential-free quality, manifest, security, and SBOM rows; credentialed
integration runs only after the pull request is merged and the staging branch
push occurs.

| Workflow/status check(s) | Required evidence |
| --- | --- |
| `dotnet-quality (ubuntu-latest)`, `dotnet-quality (windows-latest)` | format, analyzers, build, every credential-free test project with unique TRX, package/reproducibility/consumer smoke, samples |
| `.NET manifest release readiness` | stable aggregate that depends on both quality matrix legs and every dynamic `dotnet-agent-framework-compat (<version>)` row; canonical manifest/tag/package agreement and absence of the expected remote tag |
| `.NET dependency vulnerability audit`, `Repository secret scan`, `CodeQL code scanning (C#)` | dependency, secret, and CodeQL security checks |
| `SBOM (credential-free)` | package checks, checksums, SPDX and CycloneDX SBOM |
| `integration-memory`, `integration-history`, `integration-rag-vector`, `integration-rag-search`, `integration-rag-hybrid`, `integration-index-management`, `integration-persistence` | protected-environment credentialed integration categories; may wait for approval |

Configure branch protection for `build/dotnet-packaging-release` to require the
credential-free pull-request statuses above, including the exact stable status name
`.NET manifest release readiness`. That aggregate uses `always()` so a failed,
skipped, or cancelled dependency cannot skip the required status; it then fails
explicitly unless both `dotnet-quality` and
`dotnet-agent-framework-compat` report `success`, and only afterward runs
manifest/version/package validation. It runs for pushes to the protected branch
and pull requests targeting `build/dotnet-packaging-release`; unrelated pull
request targets do not require this staging-only status. Do not configure the
push-only integration job names as pull-request required checks. Instead, require
their successful staging-branch run as release evidence. The integration environment
must restrict this branch and supply `MONGODB_URI` and `MONGODB_DATABASE`; declining
approval is not equivalent to passing integration readiness.

After a reviewed manifest change reaches `main`, **.NET release coordinator**
starts automatically because its push trigger is path-filtered to
`dotnet/src/MongoDB.AgentFramework/MongoDB.AgentFramework.csproj`. A Python-only
manifest change cannot start it. The coordinator canonicalizes the version with
`NuGet.Versioning`, verifies the immutable event SHA against freshly fetched
`origin/main`, and creates `dotnet-v<version>`. Only GitHub Actions creates
release tags. If the exact tag already targets that exact SHA, a rerun accepts
it and safely redispatches; a tag at any other SHA fails and is never moved.
For recovery, manually dispatch the workflow from `main` with `RELEASE`.

GitHub suppresses workflow recursion for a tag created with `GITHUB_TOKEN`.
The coordinator captures the fixed push or `workflow_dispatch` `github.sha`, checks out
and tags that exact commit, freshly fetches `origin/main`, proves the SHA is
reachable, and dispatches the credential-free SBOM workflow on the exact tag;
it never retargets to a later mutable `main` HEAD and does not wait for a
tag-push event that will never
arrive. `workflow_dispatch` is a documented `GITHUB_TOKEN` trigger exception.
Completion raises `workflow_run`, so the privileged attestation/publish graph
is still loaded exclusively from the default branch. That graph independently
checks tag/SHA/main ancestry, derives the manifest tag, proves the annotated tag points
to the validated commit, rebuilds, tests, verifies, generates SBOM/provenance,
and dynamically tests latest/previous common stable Agent Framework versions
for that exact release SHA before protected publication.

## Actions configuration

Configure repository variables:

| Name | Meaning |
| --- | --- |
| `NUGET_ENVIRONMENT` | Required protected GitHub Environment name; the publish job is skipped when absent. |
| `NUGET_PUBLISHING_APPROVED` | Must be exactly `true` before publication is enabled. Governance owners set it only after ADR 0013 is accepted; all other values keep publishing disabled. |
| `NUGET_SOURCE_URL` | Must be exactly the official `https://api.nuget.org/v3/index.json`; the workflow rejects every other URL before exposing the API key. |

ADR 0013 is currently proposed, so `NUGET_PUBLISHING_APPROVED` MUST remain
unset/false. Configure `NUGET_API_KEY` only as a secret in the environment named by
`NUGET_ENVIRONMENT`. Use a package-scoped key. Require reviewers and restrict
the environment to `main`/release tags. The environment approval is mandatory
before the publish job receives the key. The workflow does not accept
credentials as inputs.

`dotnet-release-attestation` is a separate protected environment for the
OIDC-backed provenance job. Keep required reviewers on it. Action dependencies
are immutable SHA pins; jobs use only job-scoped `contents`, `actions`,
`id-token`, `attestations`, and `artifact-metadata` permissions they require.

After approval, the workflow pushes the exact `.nupkg` (and associated
`.snupkg`) using `dotnet nuget push`, performs a bounded clean restore from the
configured feed, and creates the GitHub Release. Its assets are the exact
package and symbols, SHA-256 manifest, SPDX/CycloneDX SBOMs, TRX and Markdown/
JSON reports, custom SLSA predicate, and GitHub attestation bundle.

## Compatibility modes and evidence

The package declaration remains `[1.13.0,1.17.0)`; dynamic drift results never
widen it. `dotnet-quality.yml` queries official NuGet V3 service/registration
APIs and tests the latest and immediately previous **common listed stable**
versions of `Microsoft.Agents.AI.Abstractions` and
`Microsoft.Agents.AI.Workflows`. NuGet's own `NuGet.Versioning` implementation
orders versions.

The **.NET Agent Framework upstream compatibility** manual workflow tests the
latest stable, latest preview when one exists, and optional `exact_version`.
Missing preview is reported as unavailable and never replaced with a stable.
An exact version must be listed for both packages. Its Monday schedule runs the
same upstream-drift mode. Every row restores/tests/builds/packs and runs the
local-feed consumer with both real dependencies pinned exactly. Artifacts
contain TRX, JSON, and Markdown evidence.

The fixed `1.13.0`/`1.16.0` rows remain the declared-range evidence until a
separate reviewed change updates the package range and compatibility claims.

## Local non-publishing rehearsal

From the repository root:

```powershell
pwsh dotnet/scripts/invoke-release-rehearsal.ps1 -Configuration Release
```

It restores, checks formatting, builds with warnings as errors, runs tests with
TRX validation, resolves and checks current/previous stable compatibility,
fully verifies package metadata/content/reproducibility, runs the isolated
local-feed consumer, and writes checksums and reports under
`dotnet/artifacts/release-rehearsal`. It contains no tag, push, or NuGet
publication operation.

## Failure recovery

- **Before tag creation:** fix the build branch, rerun the rehearsal, and merge.
- **Coordinator rejects a version/tag:** do not move or delete a release tag.
  Correct the manifest/changelog on a branch, increment the version, and merge.
- **Coordinator rerun after tag creation:** rerun the same SHA. Exact-tag
  idempotency redispatches the credential-free chain; it never retags.
- **SBOM, tests, compatibility, or attestation fails:** no publication job can
  run. Fix forward with a new version; retain failed run evidence.
- **Environment approval rejected or configuration missing:** nothing is
  published. Configure the variables, secret, restrictions, and reviewers,
  then use a new version if a tag already exists.
- **NuGet push fails before acceptance:** inspect the protected job log and
  feed configuration. Never use `--skip-duplicate` to conceal uncertainty.
- **NuGet accepted but later verification/release creation fails:** verify the
  package on the configured feed, preserve the immutable tag/package, and
  create the GitHub Release from the retained `dotnet-release-bundle` artifact
  under an audited maintainer procedure. Do not republish different bytes.

No documented command in this page publishes locally. Automatic release
coordination can produce all credential-free evidence when governance variables
are unset, but NuGet publication and GitHub Release creation remain skipped.
They occur only after ADR 0013 acceptance, explicit governance enablement, and
protected-environment approval.
