# Python release checklist

Use this checklist for `agent-framework-mongodb` only. A checked item must link
to a retained workflow or independently reviewable evidence; local success does
not authorize publication.

## Reviewed source and metadata

- [ ] Release commit is reviewed and has no unrelated changes.
- [ ] `pyproject.toml` and `api-baseline.json` contain the same reviewed release
  version. The explicit `main` release workflow creates or verifies the
  protected `python-v<version>` tag at that reachable commit.
- [ ] Version is canonical `MAJOR.MINOR.PATCH` PEP 440 with only supported
  `a`/`b`/`rc`/`.dev` prerelease segments; it has no epoch, local, or post part.
- [ ] Distribution/import identities, README, MIT license, author, classifiers,
  dependencies, and source URL match repository facts.
- [ ] `api-baseline.json` names the first published version and intentional API
  changes have semantic-version and migration review.
- [ ] Changelog/release notes describe public API, schema, index, capability,
  dependency, and deprecation changes. There is no package changelog before the
  first release because no release history exists.

## Credential-free gates

- [ ] Python 3.10 tests and configured coverage pass.
- [ ] Ruff format/check, MyPy, and Pyright pass.
- [ ] Exact minimum and newest-allowed dependencies resolve and pass constructor
  smoke; retained `pip freeze` output records versions.
- [ ] Dynamically resolved latest and previous stable Agent Framework Core rows
  pass; any requested preview/exact rows and their JUnit/JSON/Markdown reports
  are reviewed.
- [ ] Wheel and sdist pass Twine and archive allow/deny policy.
- [ ] Exact wheel and sdist install into separate clean environments and public
  provider constructors run.
- [ ] Every sample imports without credentials and reports missing setup before
  network access.
- [ ] `pip-audit`, dependency review, CodeQL, and credential scan pass.
- [ ] CycloneDX SBOM, SHA-256 checksums, and approved provenance are retained.
- [ ] `git diff --check` and the final staged-diff review pass.
- [ ] Build-branch required checks are green: version readiness, quality,
  compatibility-readiness, CodeQL, credential scan, and vulnerability audit.
- [ ] Dependency Review passes on the promotion pull request; it is PR-only
  because the action requires pull-request dependency-diff context.

## Credentialed implementation gates

- [ ] Memory integration evidence records deployment, server, Agent Framework,
  PyMongo, Python, date, and owner.
- [ ] History integration evidence records the same fields.
- [ ] Vector ANN and ENN each have current Search-capable deployment evidence.
- [ ] Full-text Search has current deployment evidence.
- [ ] Hybrid native RRF has MongoDB 8.0+ evidence.
- [ ] Session Store and Workflow Checkpoint Store integration evidence passes.
- [ ] Parent-document, on-demand, workflow retrieval, Memory-and-RAG,
  structured metadata, loader, incremental ingestion, session, and checkpoint
  scenarios pass against the credentialed release deployment. Their
  provider-agnostic construction and setup tests pass without credentials.

## Owner-controlled blockers

- [ ] `mongo` owners confirm PyPI project-name availability and ownership.
- [ ] Named package publishing owners, release approvers, support team, and
  security contact are published.
- [ ] Owners create a protected GitHub environment, store only its name in
  `PYPI_ENVIRONMENT`, require reviewers, and configure PyPI OIDC trusted
  publishing for this repository/workflow/environment.
- [ ] ADR 0013 is accepted and publishing ownership, approvers, support, and
  security contacts are confirmed before owners set
  `PYPI_PUBLISHING_APPROVED=true`; otherwise it remains unset or false.
- [ ] Owners approve provenance by setting
  `PYTHON_PROVENANCE_APPROVED=true`.
- [ ] Organization signature policy is recorded and implemented; do not invent
  a signing identity or key.
- [ ] Protected tag policy for `python-v<version>` is enabled.
- [ ] The manifest-changing `main` push automatically selected only the Python
  release workflow and bound it to the merge SHA. Manual dispatch is used only
  for recovery; `publish` is true only for an intended publication retry.
- [ ] Published-package verification downloads from PyPI, verifies metadata,
  hashes/attestations/signatures per policy, and repeats public API smoke.

Do not publish while any owner-controlled or credentialed gate is open.
