# GitHub repository configuration

This guide lists the GitHub-side configuration required before this repository
can use its protected build, integration, attestation, and package-publication
workflows. Apply these settings after the validated `main`,
`build/dotnet-packaging-release`, and `build/python-packaging-release` branches
have been pushed.

Repository administrators own these settings. Do not enable either publishing
approval variable until package ownership, approvers, support, and security
contacts are confirmed and
[ADR 0013](../decisions/0013-establish-project-and-publishing-governance.md)
is accepted.

## 1. General repository settings

Under **Settings > General**:

1. Set the default branch to `main`.
2. Allow merge commits. The documented promotion flow merges reviewed staging
   branches into `main`; do not require linear history.
3. Enable automatic deletion of merged pull-request branches.
4. Disable force pushes and branch deletion through the rulesets below.
5. Keep **Allow GitHub Actions to create and approve pull requests** disabled.

Under **Settings > Actions > General**:

1. Allow the actions used by this repository. If the organization uses an
   allowlist, it must include:
   - `actions/*`;
   - `github/codeql-action/*`;
   - `anchore/sbom-action/*`;
   - `pypa/gh-action-pypi-publish/*`.
2. Set the default workflow token permission to **Read repository contents**.
   Workflows request their narrowly scoped write permissions at job level.
3. Require approval for workflows from first-time or outside contributors.
4. Do not make repository or environment secrets available to fork pull
   requests.
5. Retain workflow logs and artifacts for at least 90 days for release
   evidence. Individual workflows may use a shorter retention period for
   non-release artifacts.

## 2. Branch rulesets

Create separate branch rulesets under **Settings > Rules > Rulesets**. Use
rulesets rather than relying on reviewer convention alone.

### Common rules

Apply these rules to `main` and both build branches:

- require a pull request before merging;
- require at least one approval (two approvals are recommended for `main`);
- dismiss stale approvals when new commits are pushed;
- require approval of the most recent reviewable push;
- require all review conversations to be resolved;
- require status checks to pass and require branches to be up to date;
- block force pushes;
- block branch deletion;
- do not allow bypass except for a small, audited release-administrator team.

Do not require signed commits until the organization has confirmed that every
human and automation path can satisfy that policy. The release workflows create
tags, not commits.

If the organization keeps `develop` as a long-lived integration branch, apply
the same common rules to it. The current release contract gives `develop` no
tagging or publishing authority: release-bearing changes must still pass
through the applicable build branch and reach `main` by reviewed pull request.

### `main`

Require these checks because they run on every pull request:

- `.NET build, test, and package quality / dotnet-quality (ubuntu-latest)`;
- `.NET build, test, and package quality / dotnet-quality (windows-latest)`;
- `.NET dependency, secret, and code scanning / .NET dependency vulnerability audit`;
- `.NET dependency, secret, and code scanning / Repository secret scan`;
- `.NET dependency, secret, and code scanning / CodeQL code scanning (C#)`;
- `.NET package SBOM (credential-free verification) / SBOM (credential-free)`;
- `Credential pattern scan / scan`;
- `Dependency review / dependency-review`.

Python workflows use path filters, so making their checks unconditional on
`main` can leave an unrelated pull request waiting for a check that was
correctly not created. For a Python-affecting pull request, reviewers must also
verify these applicable checks before merge:

- `Python quality / quality`;
- `Python Agent Framework compatibility / compatibility-readiness`;
- `CodeQL / analyze`;
- `Python dependency vulnerability scan / audit`.

Package-manifest changes should reach `main` only from the corresponding build
branch:

- `python/pyproject.toml` from `build/python-packaging-release`;
- `dotnet/src/MongoDB.AgentFramework/MongoDB.AgentFramework.csproj` from
  `build/dotnet-packaging-release`.

### `build/dotnet-packaging-release`

Require:

- both `.NET build, test, and package quality / dotnet-quality (...)` checks;
- `.NET build, test, and package quality / .NET manifest release readiness`;
- the three `.NET dependency, secret, and code scanning / ...` checks;
- `.NET package SBOM (credential-free verification) / SBOM (credential-free)`;
- `Credential pattern scan / scan`;
- `Dependency review / dependency-review`.

Do not require `integration-*` as pull-request checks. They are push-only and
must instead pass on the resulting protected staging-branch commit before
promotion to `main`. The manifest-readiness aggregate enforces all dynamically
resolved .NET compatibility rows.

### `build/python-packaging-release`

This branch is for Python release changes, so every pull request targeting it
must touch the Python/workflow paths that start its readiness workflows.
Require:

- `Python quality / version-readiness`;
- `Python quality / quality`;
- `Python Agent Framework compatibility / compatibility-readiness`;
- `CodeQL / analyze`;
- `Credential pattern scan / scan`;
- `Python dependency vulnerability scan / audit`;
- `Dependency review / dependency-review`.

GitHub exposes a check for selection only after it has run at least once. Push
the workflows, run them on the branch, and then select the exact check names
shown above. Do not substitute a dynamic matrix row for a stable aggregate.

## 3. Release-tag rulesets

Create tag rulesets for:

- `dotnet-v*`;
- `python-v*`.

Block tag updates and deletion. Release tags are immutable after creation. If
the organization also restricts tag creation, grant the minimum bypass needed
for the repository's GitHub Actions release coordinators and test both
coordinators before enabling publication. Human maintainers must not create,
move, or delete release tags as a normal release procedure.

## 4. GitHub Environments

Create four environments under **Settings > Environments**.

### `dotnet-integration`

- Required reviewers: integration/release maintainers.
- Deployment branches: `main` and `build/dotnet-packaging-release` only.
- Secrets:
  - `MONGODB_URI`;
  - `MONGODB_DATABASE`.

Use a dedicated Atlas test database and least-privilege test identity with the
Search/Vector Search index permissions required by the integration suite. Do
not reuse production credentials. Restrict network access to the runner path
chosen by the organization and rotate the identity on the normal secret
rotation schedule.

### `dotnet-release-attestation`

- Required reviewers: release or security maintainers.
- Deployment branch: `main` only.
- No package-publishing credential is required.

This approval protects the OIDC-backed rebuild and provenance job. Keep it
separate from the NuGet publishing environment.

### NuGet production environment

Create an environment such as `nuget-production`:

- Required reviewers: NuGet package owners; prevent self-review when the
  repository plan supports it.
- Deployment branch: `main` only.
- Secret:
  - `NUGET_API_KEY`, scoped to the `MongoDB.AgentFramework` package and minimum
    required push permissions.

Set the repository variable `NUGET_ENVIRONMENT` to this exact environment name.
Do not add the API key as a repository secret.

### PyPI production environment

Create an environment such as `pypi`:

- Required reviewers: PyPI package owners; prevent self-review when supported.
- Deployment branch: `main` only.
- No PyPI password or API token.

In PyPI, configure a Trusted Publisher (or a pending publisher before the first
upload) with these exact values:

| Field | Value |
| --- | --- |
| Owner | GitHub repository owner/organization |
| Repository | `ms-agent-framework-mongodb` |
| Workflow | `release-python.yml` |
| Environment | the exact value of `PYPI_ENVIRONMENT` |

The workflow uses GitHub OIDC and `pypa/gh-action-pypi-publish`; adding a PyPI
token would create an unnecessary long-lived credential.

## 5. Repository variables and secrets

Configure variables under **Settings > Secrets and variables > Actions >
Variables**:

| Variable | Required value or initial state |
| --- | --- |
| `NUGET_ENVIRONMENT` | Exact NuGet production environment name |
| `NUGET_SOURCE_URL` | `https://api.nuget.org/v3/index.json` |
| `NUGET_PUBLISHING_APPROVED` | Unset or `false` until governance approval; then exactly `true` |
| `PYPI_ENVIRONMENT` | Exact PyPI production environment name |
| `PYPI_PUBLISHING_APPROVED` | Unset or `false` until governance approval; then exactly `true` |
| `PYTHON_PROVENANCE_APPROVED` | Unset or `false` until provenance/release ownership is approved; then exactly `true` |

Environment secrets are limited to:

- `dotnet-integration`: `MONGODB_URI`, `MONGODB_DATABASE`;
- the environment named by `NUGET_ENVIRONMENT`: `NUGET_API_KEY`.

The disabled NuGet signing step mentions
`NUGET_SIGNING_CERTIFICATE_PATH` and
`NUGET_SIGNING_CERTIFICATE_PASSWORD`, but do not create those secrets until a
separate signing policy and certificate lifecycle are approved.

## 6. Security settings

Under **Settings > Security** (names vary by GitHub plan):

1. Enable the dependency graph.
2. Enable Dependabot alerts and security updates.
3. Enable code scanning with GitHub Actions; do not also enable a conflicting
   default CodeQL setup.
4. Enable secret scanning and push protection.
5. Enable private vulnerability reporting.

Use [SECURITY.md](../../SECURITY.md) as the public reporting policy. Configure
repository access so only the confirmed maintainer, release, and security teams
can approve protected environments or bypass rulesets.

## 7. Verification before enabling publication

1. Confirm all three protected branches exist remotely and `main` is the
   default branch.
2. Open non-publishing test pull requests against each build branch and confirm
   every required check appears and blocks merge when failed.
3. Push a trusted .NET staging commit and approve `dotnet-integration`; confirm
   every `integration-*` category executes at least one test.
4. Manually dispatch both compatibility workflows and retain their reports.
5. Run each release coordinator with publishing approval variables unset.
   Confirm it can create/reuse the immutable tag and produce release evidence,
   but cannot publish.
6. Confirm rejected environment approval prevents publication.
7. Confirm PyPI Trusted Publishing matches the workflow and environment exactly.
8. Confirm the NuGet API key cannot publish any unrelated package.
9. Accept ADR 0013 and record named owners, approvers, support, security,
   signing, and recovery responsibilities.
10. Only then set the approval variables to `true` and perform the first
    prerelease.

See the [.NET release operator guide](../development/release/dotnet-release-operations.md)
and [Python release runbook](python-release.md) for language-specific release
and recovery procedures.
