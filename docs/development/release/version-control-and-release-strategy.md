# Version control and release strategy

This document defines the shared release contract for the Python
`agent-framework-mongodb` and .NET `MongoDB.AgentFramework` packages. Language-specific
build commands, compatibility matrices, and publishing workflows live on their respective
build branches.

Repository administrators must apply the
[GitHub repository configuration](../../release/github-repository-configuration.md)
before either package is published.

## Branch and promotion model

| Branch | Purpose | Release authority |
|---|---|---|
| `foundation-core` | Shared specifications, governance, and release policy | Never publishes |
| `build/python-packaging-release` | Stages and proves Python packaging changes | Never tags or publishes directly |
| `build/dotnet-packaging-release` | Stages and proves .NET packaging changes | Never tags or publishes directly |
| `main` | Reviewed integration and release source | The only branch from which release tags may be created |

Changes are developed and validated on the appropriate build branch, merged through a
reviewed pull request to `main`, and released from the resulting `main` commit. Every push to
a build branch runs that language's complete credential-free readiness suite. Credentialed
integration jobs may require approval, but are triggered by the same push so their result is
part of the release evidence.

A change is release-bearing only when it changes that language's package-version manifest.
After the reviewed build branch is merged, the `main` push for that manifest automatically
starts the corresponding language release coordinator. The coordinator must bind the tag to
the immutable `main` event SHA and reject a commit that is not reachable from `origin/main`.
This keeps the build branches useful as pre-release proving grounds without making them
alternate release lines.

Do not rebase a published release tag. If an artifact is wrong, fix it on a branch, merge the
fix to `main`, increment the package version, and create a new tag.

## Versions and tags

The two packages use Semantic Versioning independently; their versions do not need to match
each other or Microsoft Agent Framework. Compatibility with Agent Framework is expressed by
tested dependency ranges and a published compatibility report, not by copying the framework
version.

| Package | Manifest | Tag |
|---|---|---|
| Python | `python/pyproject.toml` | `python-v<PEP-440-version>` |
| .NET | `dotnet/src/MongoDB.AgentFramework/MongoDB.AgentFramework.csproj` | `dotnet-v<SemVer-version>` |

Examples are `python-v0.1.0rc1` and `dotnet-v0.1.0-rc.1`. The tag version must exactly match
the package manifest. Python versions follow
[PEP 440](https://packaging.python.org/en/latest/specifications/version-specifiers/);
.NET versions follow
[NuGet package versioning](https://learn.microsoft.com/nuget/concepts/package-versioning).
Both packages apply [Semantic Versioning 2.0.0](https://semver.org/):

- increment MAJOR for incompatible public API or persisted-contract changes;
- increment MINOR for backward-compatible capabilities;
- increment PATCH for backward-compatible fixes;
- use ecosystem-native prerelease identifiers before a stable release.

## GitHub Actions release flow

1. A push to `build/python-packaging-release` or `build/dotnet-packaging-release` starts the
   language's quality, full credential-free tests, version validation, current/previous
   stable compatibility, package/consumer smoke, security, vulnerability, and SBOM checks.
2. Branch protection requires every readiness check before the build branch can be merged.
   Credentialed integration checks use a protected test environment and are required when
   release policy marks them available.
3. A reviewed pull request merges the exact build-branch commit into `main`, including one
   intentional package-manifest version change.
4. The manifest path change automatically starts only that language's release coordinator.
   The coordinator validates the immutable event SHA, canonical manifest version, tag
   uniqueness, and `origin/main` ancestry before creating the annotated tag.
5. Because a tag created with `GITHUB_TOKEN` does not recursively start a tag-push workflow,
   the coordinator explicitly invokes or continues the trusted release jobs for that exact
   SHA and tag.
6. The release graph rebuilds from the tagged commit and reruns the release gates; it never
   reuses build-branch or developer-machine artifacts.
7. A protected GitHub Environment requires approval before package publication. Publishing
   also remains disabled until the language's governance approval variable is explicitly set.
8. After publication, the workflow downloads the exact package from PyPI or NuGet, verifies
   it, and creates or updates the GitHub Release with checksums, SBOM, provenance, and test
   reports.

Manual dispatch remains available for a failed-run recovery or an on-demand compatibility
report. It must apply the same SHA, version, ancestry, governance, and environment checks as
the automatic path; it is not a bypass.

GitHub only runs `workflow_dispatch` workflows that exist on the default branch, so release
and on-demand compatibility workflows become operational after their build-branch changes
are merged to `main`. Automatic `push` triggers use the workflow definition present in the
pushed commit. See GitHub's
[manual workflow documentation](https://docs.github.com/actions/managing-workflow-runs/manually-running-a-workflow)
and [workflow trigger reference](https://docs.github.com/actions/using-workflows/events-that-trigger-workflows).

Release jobs use least-privilege, job-scoped permissions. Tag creation and GitHub Release
jobs require `contents: write`; provenance requires `id-token: write` and
`attestations: write`. GitHub Environment approvals protect publishing credentials and OIDC
claims; see
[deployment environments](https://docs.github.com/actions/deployment/targeting-different-environments/using-environments-for-deployment).

## Publishing configuration

No credential belongs in source control or a workflow input.

### Python

Use PyPI Trusted Publishing with the `pypi` GitHub Environment and
`pypa/gh-action-pypi-publish`. The environment name is supplied through the
`PYPI_ENVIRONMENT` repository variable. Configure the repository, workflow filename, and
environment as a trusted publisher in PyPI before enabling publication. No API token is
required. See PyPI's
[trusted publisher documentation](https://docs.pypi.org/trusted-publishers/using-a-publisher/)
and the
[`gh-action-pypi-publish` documentation](https://github.com/pypa/gh-action-pypi-publish).

### .NET

Use a scoped NuGet.org API key stored as `NUGET_API_KEY` in the protected environment named
by the `NUGET_ENVIRONMENT` repository variable. Supply the feed through the
`NUGET_SOURCE_URL` repository variable; the expected production value is
`https://api.nuget.org/v3/index.json`. See Microsoft's
[`dotnet nuget push` documentation](https://learn.microsoft.com/dotnet/core/tools/dotnet-nuget-push)
and NuGet's
[scoped API key guidance](https://learn.microsoft.com/nuget/nuget-org/scoped-api-keys).

Publishing jobs remain skipped until the corresponding environment variable is configured
and an environment reviewer approves the deployment.

## Agent Framework compatibility policy

Pre-release gates test the most recent stable Agent Framework version and the stable version
immediately preceding it. This detects a too-narrow dependency declaration while keeping the
mandatory matrix bounded. A manually dispatched compatibility workflow additionally tests:

- latest stable;
- latest preview, when one exists;
- an optional exact version supplied by a maintainer.

Versions are resolved at workflow runtime from first-party package indexes:

- Python `agent-framework-core`: the
  [PyPI JSON API](https://docs.pypi.org/api/json/) and
  [project index](https://pypi.org/project/agent-framework-core/);
- .NET `Microsoft.Agents.AI.Abstractions` and `Microsoft.Agents.AI.Workflows`: the
  [NuGet V3 service index](https://learn.microsoft.com/nuget/api/service-index) and their
  package registration/flat-container resources.

Resolution excludes yanked/unlisted releases and orders versions with the target ecosystem's
version parser. A preview is reported as unavailable rather than silently substituting a
stable version.

Each matrix row restores the exact resolved Agent Framework version, runs the complete
credential-free test suite, builds the package, installs the built artifact into a clean
consumer environment, and uploads both machine-readable test results and a Markdown summary.
Credentialed MongoDB integration suites remain separately approval-gated and consume the same
resolved version when run.

## Local release rehearsal

Every language build branch provides one documented command that:

1. cleans only generated release output;
2. restores pinned build tooling;
3. runs credential-free quality and compatibility checks;
4. builds package artifacts;
5. validates metadata and package contents;
6. installs and smoke-tests the artifact from a local-only package source;
7. writes checksums and a test report.

The local command must not contain a publish operation. Publishing is CI-only from a protected
tag on `main`.

## Maintainer checklist

- [ ] Version changed in exactly one language manifest.
- [ ] Changelog and compatibility documentation describe the release.
- [ ] Build-branch quality, full tests, version, package, security, SBOM, stable-current, and
      stable-previous jobs pass.
- [ ] On-demand stable/preview report is reviewed when preparing a release.
- [ ] Build branch is merged to `main` without changing generated artifacts.
- [ ] The manifest-changing `main` merge starts the expected language release and no other.
- [ ] Automatic tag and manifest versions match the immutable `main` merge SHA.
- [ ] Protected publishing environment is approved.
- [ ] Published artifact is downloaded and verified.
- [ ] GitHub Release contains checksums, SBOM/provenance, compatibility report, and notes.
