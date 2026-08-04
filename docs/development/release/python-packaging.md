# Python packaging and release evidence

This document implements
[implementation-map slice 20](../../spec/implementation-map.md) for the Python
distribution. The normative requirements are
[packages](../../spec/packages.md),
[quality and release](../../spec/quality-release.md), and
[compatibility and migration](../../spec/compatibility-migration.md). ADRs
[0004](../../decisions/0004-publish-independent-language-packages.md),
[0011](../../decisions/0011-release-features-through-staged-quality-gates.md),
[0013](../../decisions/0013-establish-project-and-publishing-governance.md),
and [0014](../../decisions/0014-publish-only-tested-compatibility-ranges.md)
record the proposed rationale and do not override those specifications.

## Identity and metadata

- Distribution: `agent-framework-mongodb`
- Import root: `agent_framework_mongodb`
- Version source: `python/pyproject.toml`
- Runtime version access: `agent_framework_mongodb.__version__`
- License: the repository's MIT `LICENSE`
- Author: `Shankar Narayanan SGS`, as recorded by the license and repository
  history; no maintainer, support address, or publishing identity is inferred
- Source URL: `https://github.com/mongo/ms-agent-framework-mongodb`

The package is currently a pre-release (`0.1.0.dev0`). The classifiers advertise
only Python 3.10 because it is the only runtime in the current release gate.
`py.typed` declares the shipped inline typing information.

## Artifact boundary

Hatch builds the wheel from `src/agent_framework_mongodb`. The source
distribution allowlist contains only `LICENSE`, `README.md`, `pyproject.toml`,
Hatch's generated source ignore metadata, and that source tree.
`scripts/verify_artifacts.py` inspects archives without
extracting them and fails on tests, samples, caches, bytecode, local settings,
environment files, credential-key extensions, links, path traversal, or any
file outside the allowlist. The wheel must include metadata, license, and the
typing marker. Its separate `--supplemental` mode validates CycloneDX JSON and
recomputes SHA-256 manifest entries; supplemental files are never passed to
Twine as distributions.

`scripts/smoke_public_api.py` runs against clean wheel and sdist installations.
It imports the installed version and constructs Memory, History, RAG, Session
Store, and Workflow Checkpoint Store public providers without contacting
MongoDB. The provider clients are then closed.

## Public API compatibility

`api-baseline.json` is the reviewed first-release candidate baseline. It records
every top-level export, package-owned constructor, and every visible public
method, property accessor, classmethod, and staticmethod defined by a
package-owned class in the exported class's inheritance chain. Private members
and members inherited from foreign dependencies are excluded. Exported Enum
classes additionally record every declared `__members__` name, including
aliases, its canonical member name, and its deterministic JSON value.
`scripts/check_api_baseline.py` fails on additions, removals, renames,
signature/default changes, or any mismatch between `baseline_version` and the
installed package version. Later intentional changes require semantic-version,
migration, and deprecation review before regenerating it with `--write`.

## Dynamic Agent Framework compatibility

`.github/workflows/python-agent-framework-compatibility.yml` implements the
runtime-resolved compatibility gate. `scripts/resolve_framework_versions.py`
reads the official PyPI JSON API and uses `packaging.version.Version`; it
excludes releases with no distributions and releases whose distributions are
all yanked. The default matrix is latest stable plus immediately previous
stable. Manual mode adds latest preview when present and an optional exact
version. Stable is never relabeled or substituted as preview.

Each matrix row force-installs the exact resolved version and runs the complete
credential-free test, Ruff, MyPy, Pyright, API baseline, credential scan,
package build/validation, and clean wheel/sdist consumer gates. JUnit XML,
machine-readable JSON, Markdown, and `pip freeze` are retained. Pull requests,
the Python build branch, `main`, weekly upstream drift, and manual dispatch are
covered. These dynamic results are run evidence, not a hard-coded compatibility
claim.

Only credential-free evidence is available. MongoDB deployment cells remain
unadvertised until a named owner records real-deployment evidence.

| Surface | Declared range or mode | Evidence on 2026-08-03 | Release status |
| --- | --- | --- | --- |
| Python | `>=3.10` | complete local gate uses CPython 3.10.4; CI uses 3.10 | Python versions above 3.10 are not yet release-evidenced |
| Agent Framework Core | `>=1.13,<2` | latest/previous stable resolved at workflow runtime; manual preview/exact modes | retained compatibility workflow required on reviewed tag |
| PyMongo | `>=4.13,<5` | local minimum 4.13.0 and newest-allowed 4.17.0 both pass constructor smoke; CI repeats both | endpoint CI required on reviewed tag |
| OpenTelemetry API | `>=1.39,<2` | local minimum 1.39.0 and newest-allowed 1.44.0 both pass constructor smoke; CI repeats both | endpoint CI required on reviewed tag |
| Vector ANN / ENN | pre-created MongoDB Vector Search index | no credentialed deployment evidence recorded | unsupported for publication |
| Full-text | pre-created MongoDB Search index | no credentialed deployment evidence recorded | unsupported for publication |
| Hybrid RRF | MongoDB 8.0+ with Search, Vector Search, and native `$rankFusion` | no credentialed deployment evidence recorded | unsupported for publication |
| History / persistence | compatible MongoDB deployment | no credentialed deployment evidence recorded | unsupported for publication |

The endpoint jobs print `pip freeze` as immutable run evidence. They do not
convert a future untested resolver result into a permanent compatibility claim.

## CI and release flow

`python-quality.yml` runs tests and coverage, Ruff format/check, MyPy, Pyright,
Twine, archive policy, API baseline, exact wheel/sdist clean installs,
constructor smoke, dependency endpoints, a CycloneDX SBOM, checksums, and
artifact retention. Security workflows separately run dependency review,
credential scanning, CodeQL, and `pip-audit`.

`release-python.yml` is manual and must be dispatched from `main`. It accepts a
commit, proves that commit is reachable from `origin/main`, verifies the static
manifest/API-baseline version, and creates or verifies the annotated
`python-v<version>` tag. It then continues in the same workflow run, avoiding
the incorrect assumption that a `GITHUB_TOKEN`-created tag triggers another
workflow. Build-branch pushes and ordinary merges cannot tag or publish.

The workflow clean-builds from the exact SHA, runs latest/previous stable
compatibility rows and release gates, attests the distributions, and retains
wheel, sdist, JUnit/report files, checksums, SBOM, and provenance. Publication
also requires the explicit `publish` input and both owner settings:

1. `PYTHON_PROVENANCE_APPROVED=true`, enabling GitHub artifact provenance; and
2. `PYPI_ENVIRONMENT`, naming an owner-created protected GitHub environment
   configured for PyPI trusted publishing.

The publish job has only `contents: read` and `id-token: write`; it accepts no
password or token secret. Release-sensitive actions are pinned to full,
reviewed commit SHAs with their upstream major/ref recorded inline. After a
successful publish, a protected job waits for the exact PyPI version,
downloads both distributions, compares their SHA-256 hashes to the pre-publish
artifacts, installs each separately, and repeats versioned public API smoke.
Only then is the GitHub Release created with the exact artifacts and evidence.
Tag
protection, environment reviewers, PyPI project
ownership, support/security contacts, release approvers, and signature policy
are owner settings and remain blockers. No signing placeholder is selected
until that policy is known.

## Local verification

From `python` on Python 3.10, the single local rehearsal command is:

```powershell
python -m pip install -e ".[dev]"
python scripts\rehearse_release.py
```

It cleans only `dist/rehearsal`, executes quality/tests/API/credential checks,
builds and validates wheel and sdist, clean-installs each local artifact, and
writes SHA-256, JUnit, JSON, and Markdown evidence. `--dry-run` validates and
prints the plan. It has no publishing code. CI additionally creates the
CycloneDX SBOM and GitHub provenance. The
[release runbook](../../release/python-release.md) documents inputs,
environments, promotion, reports, and failure recovery; the
[release checklist](../../release/python-release-checklist.md) records blockers.
