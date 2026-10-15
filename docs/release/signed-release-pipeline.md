# Signed release pipeline

`.github/workflows/release.yml` is a manual, drop-in release pipeline that
covers the three controls the ABX launch requires:

| Control | Where it happens |
| --- | --- |
| Code signing | `prepare` creates a GPG-signed tag; `sign-and-release` writes a detached `.sig` for every distribution. Both use the Garasign container. |
| PyPI through OIDC | `publish` uses `pypa/gh-action-pypi-publish` with trusted publishing. No PyPI token exists anywhere. |
| Signatures on a draft GitHub Release | `sign-and-release` creates the release as a draft with `--verify-tag` and attaches the distributions, their `.sig` files, and `authorized-publication.txt`. |

It is additive: `release-python.yml` (the manifest-driven coordinator) is
unchanged. Before the first real release, decide which workflow is canonical,
because a PyPI trusted publisher is bound to one workflow filename (you may
register both).

## Files

```text
.github/workflows/release.yml                  orchestrator (4 jobs)
.github/actions/release-setup/action.yml       AWS OIDC -> ECR login -> Secrets Manager -> Garasign env
.github/actions/garasign-git/action.yml        run a git command inside the Garasign container
.github/actions/sign-artifacts/action.yml      detached, armored GPG signature per file
.github/actions/create-draft-release/action.yml authorized-publication record + draft release
```

The composite actions mirror `mongodb-labs/drivers-github-tools@v3`
(`setup`, `git-sign`, `gpg-sign`, `authorized-pub`, and the draft-release step
of `python/post-publish`). A repository that is comfortable depending on that
public repo can replace the four local actions with the upstream references.

## Flow

```text
workflow_dispatch(ref, dry_run, publish_to_test_pypi)
  |
  v
prepare   read version from <PACKAGE_DIR>/pyproject.toml, require canonical PEP 440
          refuse if <TAG_PREFIX><version> exists on another commit
          release-setup -> garasign-git: git tag -a -s <tag>
          verify-tag against gpg-public-url (if set) -> push tag (unless dry_run)
  |
  v
build     checkout the exact SHA -> python -m build -> twine check -> upload dist-<run_id>
  |
  v
publish   [environment: release] download dist
          pypa/gh-action-pypi-publish (OIDC)      (unless dry_run)
          actions/attest-build-provenance         (unless dry_run)
  |
  v
sign-and- [environment: release] release-setup -> download dist
release   sign-artifacts: dist/* -> $RELEASE_ASSETS/*.sig
          create-draft-release: authorized-publication.txt
                                gh release create --draft --verify-tag
                                gh release upload *.sig dist/* authorized-publication.txt
```

Every job after `prepare` checks out `needs.prepare.outputs.sha`, so the tag,
the built files, the PyPI upload, and the signatures all describe one commit.

## One-time setup

### 1. Repository secrets

`Settings > Secrets and variables > Actions > Secrets`:

| Secret | Value |
| --- | --- |
| `AWS_ROLE_ARN` | IAM role that can read the release secret. Its trust policy must allow `token.actions.githubusercontent.com` with `sub` = `repo:<owner>/<repo>:environment:release`. |
| `AWS_REGION` | Region of that role and the secret, for example `us-east-1`. |
| `AWS_SECRET_ID` | Secrets Manager secret id, for example `release-secrets/agent-framework-mongodb`. |

### 2. AWS Secrets Manager secret

JSON keys read by `release-setup`:

| Key | Required | Purpose |
| --- | --- | --- |
| `garasign-username` | yes | Garasign service account |
| `garasign-password` | yes | Garasign service account |
| `gpg-key-id` | yes | Key id passed to `git tag --local-user` |
| `gpg-public-url` | no | Public key URL; when set, `prepare` runs `git verify-tag` |

`release-setup` also pulls `release-infrastructure/garasign-git` from ECR
account `901841024863` using role `ecr-role-gha-ro`. That role's trust policy
must list this repository's OIDC subject. Both the role trust updates and the
Garasign account are owned by the release-infrastructure team; request them
early. Which GPG key signs ABX artifacts (the DBX drivers key or a new one) is
a policy decision this pipeline does not make; the checklist item in
`python-release-checklist.md` stays open until it is recorded.

### 3. GitHub environment `release`

`Settings > Environments > New environment > release`:

- Required reviewers: the release approvers.
- Deployment branches: `main` only (plus `develop` if you dry-run from it).
- No secrets are needed in the environment itself.

`prepare`, `publish`, and `sign-and-release` each wait for approval.

### 4. PyPI trusted publisher

On pypi.org, as the account that owns (or will own) the project:
`Account settings > Publishing > Add a new pending publisher` (or
`Manage project > Publishing` once the project exists):

| Field | Value |
| --- | --- |
| PyPI project name | `agent-framework-mongodb` |
| Owner | `mongodb-developer` |
| Repository name | `ms-agent-framework-mongodb` |
| Workflow name | `release.yml` |
| Environment name | `release` |

Repeat on test.pypi.org if you want `publish_to_test_pypi`.

How the OIDC exchange works at run time:

1. The `publish` job has `id-token: write`, so the runner can request a GitHub
   OIDC token whose claims name this repository, workflow file, environment,
   ref, and run.
2. `pypa/gh-action-pypi-publish` requests that token with audience `pypi`,
   sends it to PyPI, and PyPI checks the claims against the trusted publisher
   row above.
3. PyPI answers with a short-lived (about 15 minutes), project-scoped upload
   token; the action uploads the files and discards it.

Nothing in the repository, its secrets, or the logs ever holds a PyPI
credential, so there is nothing to rotate or leak.

## Running it

1. Merge the version you intend to release into the ref you will dispatch
   from (`python/pyproject.toml`; keep `api-baseline.json` in step as the
   existing checklist requires).
2. `Actions > Signed release > Run workflow` with `dry_run: true`. This
   exercises AWS, ECR, Garasign, tag signing, the build, and the signature
   step without pushing, uploading, or creating anything.
3. Run again with `dry_run: false`. Approve the three environment gates.
4. Open the draft release, review the assets, add notes, publish it.

Verify a signature locally:

```bash
curl -fsSL "$GPG_PUBLIC_URL" | gpg --import
gpg --verify agent_framework_mongodb-<version>-py3-none-any.whl.sig \
             agent_framework_mongodb-<version>-py3-none-any.whl
sha256sum --check <(grep -A1 'Filename' authorized-publication.txt | paste - - | awk '{print $NF"  "$2}')
```

## Porting to another repository

Change the three values under `env:` in `release.yml`:

```yaml
env:
  PRODUCT_NAME: <distribution name>
  PACKAGE_DIR: <directory containing pyproject.toml, or "." >
  TAG_PREFIX: <"v", "python-v", or "">
```

Then copy the four `.github/actions/*` directories unchanged and repeat the
one-time setup. The pipeline only assumes a PEP 517 project with a static
`project.version`.

## What was trimmed from mongo-python-driver's pipeline

| In mongo-python-driver | Here | Why |
| --- | --- | --- |
| GitHub App checkout (`secure-checkout`) and signed version-bump commits before and after release | Removed; the version must already be committed | Avoids an App installation and a branch-protection bypass. Tags are pushed with `GITHUB_TOKEN`. |
| Nightly scheduled dry run and automatic TestPyPI upload | Manual dispatch; TestPyPI is opt-in | A new project usually has no TestPyPI publisher yet. |
| `dist.yml` cibuildwheel matrix (9 platforms, QEMU) | `python -m build` | Pure-Python wheel. |
| CodeQL job inside the release graph | Not duplicated | `codeql.yml` already runs on PRs, pushes, and weekly. |
| Kondukto SBOM augmentation, SARIF export, SSDLC compliance report, S3 upload | Not included | Those produce the release-tracking evidence bundle. Add `full-report` + `upload-s3-assets` from drivers-github-tools once the ABX S3 bucket and Kondukto project exist; the `authorized-publication.txt` record is kept because it costs nothing. |
| Evergreen project wiring | Not applicable | |
| `attest-sbom` | Not included | Depends on the Kondukto-augmented SBOM above. |
