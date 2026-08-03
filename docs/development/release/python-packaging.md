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

## Compatibility matrix

Only credential-free evidence is available. A dependency range is a support
claim only after both endpoint jobs pass; MongoDB deployment cells remain
unadvertised until a named owner records real-deployment evidence.

| Surface | Declared range or mode | Evidence on 2026-08-03 | Release status |
| --- | --- | --- | --- |
| Python | `>=3.10` | complete local gate uses CPython 3.10.4; CI uses 3.10 | Python versions above 3.10 are not yet release-evidenced |
| Agent Framework Core | `>=1.13,<2` | minimum and newest-allowed local resolutions both use 1.13.0; CI repeats both | endpoint CI required on reviewed tag |
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

`release-python.yml` is manual and accepts only an existing
`python-v<version>` tag whose version exactly matches both `pyproject.toml` and
`api-baseline.json`. The current reviewed tag is therefore
`python-v0.1.0.dev0`; a different release requires a reviewed commit updating
both version sources rather than unreviewed build-time substitution. The
workflow rebuilds from the tagged commit, exact-tests wheel and sdist in
separate environments, and repeats the credential-free gate. Publication is skipped
unless owners configure both:

1. `PYTHON_PROVENANCE_APPROVED=true`, enabling GitHub artifact provenance; and
2. `PYPI_ENVIRONMENT`, naming an owner-created protected GitHub environment
   configured for PyPI trusted publishing.

The publish job has only `contents: read` and `id-token: write`; it accepts no
password or token secret. Release-sensitive actions are pinned to full,
reviewed commit SHAs with their upstream major/ref recorded inline. After a
successful publish, a protected job waits for the exact PyPI version, downloads
both distributions, compares their SHA-256 hashes to the pre-publish artifacts,
installs each separately, and repeats versioned public API smoke. Tag
protection, environment reviewers, PyPI project
ownership, support/security contacts, release approvers, and signature policy
are owner settings and remain blockers. No signing placeholder is selected
until that policy is known.

## Local verification

From `python` on Python 3.10:

```powershell
python -m pytest --cov=agent_framework_mongodb --cov-report=term -q
python -m ruff check src tests samples scripts ..\scripts\scan_credentials.py
python -m ruff format --check src tests samples scripts ..\scripts\scan_credentials.py
python -m mypy
python -m pyright
python -m build
python -m twine check dist\*.whl dist\*.tar.gz
python scripts\verify_artifacts.py dist\*.whl dist\*.tar.gz
python scripts\verify_artifacts.py --supplemental dist\*.sbom.cdx.json dist\SHA256SUMS
python scripts\check_api_baseline.py api-baseline.json
python ..\scripts\scan_credentials.py
```

Clean artifact installs, dependency endpoints, `pip-audit`, SBOM generation,
and checksums are scripted in the workflows because their paths are
platform-specific. The [release checklist](../../release/python-release-checklist.md)
records evidence and external blockers.
