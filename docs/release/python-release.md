# Releasing the Python package

This runbook applies only to `agent-framework-mongodb`. The shared
[version-control and release strategy](../development/release/version-control-and-release-strategy.md)
is authoritative; the [Python packaging design](../development/release/python-packaging.md)
explains the implementation. Repository administrators must first apply the
[GitHub repository configuration](github-repository-configuration.md).

## Promotion and version preparation

1. Develop and prove packaging changes on `build/python-packaging-release`.
   Every relevant push automatically runs Python quality/full credential-free
   coverage, Ruff, MyPy, Pyright, API/package/clean-install checks, canonical
   version readiness, up to the latest two supported stable compatibility
   rows, CodeQL, credential
   scanning, and dependency vulnerability audit. This staging branch cannot
   tag or publish.
2. Change the static `project.version` in `python/pyproject.toml` and the
   `baseline_version` in `python/api-baseline.json` together. Use PEP 440 and
   choose the next independent package version; do not copy the Agent Framework
   version. The publishable grammar requires exactly `MAJOR.MINOR.PATCH` and
   permits canonical `a`, `b`, `rc`, and `.dev` prerelease forms. It rejects
   two-part versions, noncanonical spellings, epochs, local versions, post
   releases, and release tuples with other than three components.
3. Update release notes and compatibility evidence, run the local rehearsal,
   and merge the reviewed branch to `main`.
4. Merge the reviewed pull request with its intentional
   `python/pyproject.toml` version change. That exact manifest-changing `main`
   push automatically starts **Release Python package**, binds the release to
   immutable `github.sha`, derives `python-v<version>`, and requests
   publication. A .NET-only main change does not start this workflow.

The workflow rejects dispatches from a non-`main` workflow ref, commits not
reachable from `origin/main`, version/baseline mismatches, and an existing tag
that points elsewhere. It creates the annotated tag itself. The same workflow
then checks out the immutable commit and continues the build; it does **not**
expect a `GITHUB_TOKEN` tag push to recursively start another workflow.
The exact shared validator used by the clean build runs before tag push, so an
immutable tag cannot be created under a grammar that downstream jobs reject.

Manual dispatch remains a recovery path. Supply the original full main SHA (or
`main` when it still identifies that commit); `publish` defaults false and must
be deliberately selected for a publication retry. An existing tag is accepted
idempotently only when its peeled commit equals the selected SHA. Never move a
conflicting tag.

## Build-branch protection

Protect `build/python-packaging-release` and require these fixed checks before
merge:

- `Python quality / version-readiness`;
- `Python quality / quality`;
- `Python Agent Framework compatibility / compatibility-readiness`;
- `CodeQL / analyze`;
- `Credential pattern scan / scan`;
- `Python dependency vulnerability scan / audit`.

Also require pull-request review and `Dependency review / dependency-review`
for pull requests. Dependency Review intentionally remains PR-only because
GitHub's dependency-review action needs the base/head dependency diff available
only in pull-request context. Credentialed integration checks become additional
required checks when the shared policy marks their protected environment ready.

## Protected publication

`publish: true` is necessary but insufficient. Repository owners must set:

- `PYTHON_PROVENANCE_APPROVED=true`;
- `PYPI_PUBLISHING_APPROVED=true`;
- `PYPI_ENVIRONMENT` to the protected GitHub Environment configured as the PyPI
  trusted publisher for `.github/workflows/release-python.yml`.

`PYPI_PUBLISHING_APPROVED` is a governance kill switch, not a developer
convenience flag. Owners may set it only after ADR 0013 is accepted and package
publishing ownership, approvers, support, and security contacts are confirmed.
Until then it must remain unset or false.

The environment must require reviewer approval. The publish job receives only
`id-token: write` and `contents: read`; there is no password or API-token input.
After approval it uploads the exact clean-built wheel and sdist, downloads both
back from PyPI, verifies their checksums and clean installs, and only then
creates the GitHub Release. The release contains those distributions,
checksums, JUnit/release reports, compatibility reports, CycloneDX SBOM, and
GitHub provenance bundle.

A build-branch push never tags or publishes. A manifest-changing main push
requests publication, but an unset/false `PYPI_PUBLISHING_APPROVED`, an unset
`PYPI_ENVIRONMENT`, missing provenance approval, or a rejected environment
deployment never uploads to PyPI. Manual dispatch additionally requires
`publish: true`.

## Agent Framework compatibility

`Python Agent Framework compatibility` runs on relevant pull requests, pushes
to `main` and the Python build branch, and weekly. Its default gate asks the
official PyPI JSON API for the latest non-yanked stable
`agent-framework-core` version and, when another stable release is inside the
declared supported range, the immediately previous supported stable version.
Versions are ordered with `packaging.version.Version`; releases with no files or
only yanked files are excluded.

A manual dispatch may also request the latest preview and an optional exact
PEP 440 version. “Preview” means a real prerelease/dev release; when none
exists the resolution report says so and never substitutes stable. An exact
yanked, missing, or distribution-less release fails resolution.

Each exact-version row runs all credential-free tests, Ruff, MyPy, Pyright, API
baseline and credential checks, builds and validates wheel/sdist, and installs
both into clean consumers. Artifacts include `pytest.xml`, `summary.json`,
`summary.md`, and `pip-freeze.txt`. Resolution JSON and Markdown identify the
PyPI source and selected channels.

## Local rehearsal

Install the development extra, then run from `python`:

```powershell
python -m pip install -e ".[dev]"
python scripts\rehearse_release.py
```

The command removes only `python/dist/rehearsal`, runs credential-free quality
and coverage, dynamically resolves up to the latest two supported stable Agent
Framework Core releases from PyPI, and runs each exact version through the same
isolated compatibility-row helper used by release CI. It then builds exactly
one wheel and sdist, validates both, installs each local artifact in its own
environment, and writes:

- `dist/rehearsal/SHA256SUMS`;
- `dist/rehearsal/tests.xml`;
- `dist/rehearsal/rehearsal-report.json`;
- `dist/rehearsal/rehearsal-report.md`.

Each compatibility subdirectory also retains coverage, JUnit, `pip freeze`,
JSON, and Markdown evidence. Checksums and reports are local evidence only;
neither the rehearsal nor the compatibility helper contains a publish action.

Use `python scripts\rehearse_release.py --dry-run` to inspect the plan without
changing files. The script contains no upload operation.

## Failure recovery

- **Before tag creation:** fix on the build branch, merge, bump if the reviewed
  version changes, and dispatch again.
- **Tag exists at the requested commit:** correct transient CI or environment
  setup and rerun with the same commit. The workflow reuses only that tag/commit
  relationship and rebuilds.
- **Tag points elsewhere:** stop. Never move a release tag; merge a fix and use
  a new version.
- **Publication is rejected or unavailable:** do not upload manually. Correct
  environment/trusted-publisher configuration and rerun the explicit workflow.
- **PyPI upload succeeded but verification or GitHub Release failed:** preserve
  the immutable PyPI version, diagnose the retained artifacts, and rerun the
  workflow against the same tag only when doing so cannot duplicate the upload.
  Otherwise create the GitHub Release from the verified retained run evidence
  under the repository owner's recovery process.
- **Artifact is wrong after publication:** fix, merge to `main`, increment the
  version, and release a new tag. Never replace published files.
