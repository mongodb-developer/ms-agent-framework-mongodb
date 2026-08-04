from __future__ import annotations

import importlib.util
import subprocess
import sys
from pathlib import Path
from types import ModuleType

import pytest

_PYTHON_ROOT = Path(__file__).resolve().parents[2]


def _load_script(name: str) -> ModuleType:
    path = _PYTHON_ROOT / "scripts" / f"{name}.py"
    spec = importlib.util.spec_from_file_location(name, path)
    assert spec is not None and spec.loader is not None
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def _pypi_payload() -> dict[str, object]:
    return {
        "releases": {
            "1.11.0": [{"yanked": False}],
            "1.12.0": [{"yanked": False}],
            "1.13.0": [{"yanked": False}],
            "1.14.0rc1": [{"yanked": False}],
            "1.14.0b1": [{"yanked": True}],
            "1.15.0.dev1": [],
            "not-a-version": [{"yanked": False}],
        }
    }


def test_framework_resolution_distinguishes_stable_preview_and_yanked() -> None:
    resolver = _load_script("resolve_framework_versions")

    matrix = resolver.resolve_matrix(_pypi_payload(), include_preview=True)

    assert matrix == [
        {"label": "latest-stable", "version": "1.13.0", "channel": "stable"},
        {"label": "previous-stable", "version": "1.12.0", "channel": "stable"},
        {"label": "latest-preview", "version": "1.14.0rc1", "channel": "preview"},
    ]


def test_framework_resolution_rejects_yanked_or_unknown_exact_version() -> None:
    resolver = _load_script("resolve_framework_versions")

    with pytest.raises(ValueError, match="absent, yanked"):
        resolver.resolve_matrix(_pypi_payload(), exact="1.14.0b1")


def test_framework_resolution_deduplicates_an_exact_selected_version() -> None:
    resolver = _load_script("resolve_framework_versions")

    matrix = resolver.resolve_matrix(_pypi_payload(), exact="1.13.0")

    assert [item["version"] for item in matrix] == ["1.13.0", "1.12.0"]


def test_release_rehearsal_dry_run_is_non_publishing() -> None:
    script = _PYTHON_ROOT / "scripts" / "rehearse_release.py"

    completed = subprocess.run(  # noqa: S603
        [sys.executable, str(script), "--dry-run"],
        cwd=_PYTHON_ROOT,
        check=True,
        capture_output=True,
        text=True,
    )

    assert "pytest" in completed.stdout
    assert "--cov=agent_framework_mongodb" in completed.stdout
    assert "build" in completed.stdout
    assert "latest-stable and previous-stable" in completed.stdout
    assert "No upload or publication command is present." in completed.stdout
    assert "twine upload" not in completed.stdout


def test_release_rehearsal_reuses_dynamic_resolution_and_row_runner() -> None:
    script = (_PYTHON_ROOT / "scripts" / "rehearse_release.py").read_text(encoding="utf-8")

    assert "resolve_matrix(fetch_pypi())" in script
    assert 'row["channel"],' in script
    assert "run_row(" in script
    assert "framework-resolution.json" in script
    assert "framework-resolution.md" in script


def test_compatibility_runner_retains_failure_evidence_contract() -> None:
    script = (_PYTHON_ROOT / "scripts" / "run_framework_compatibility.py").read_text(
        encoding="utf-8"
    )

    assert "pip-freeze.txt" in script
    assert "pytest.xml" in script
    assert "summary.json" in script
    assert "summary.md" in script
    assert "publishing_attempted" in script
    assert "--cov=agent_framework_mongodb" in script
    assert "finally:" in script


def test_version_readiness_requires_canonical_pep440_and_matching_tag(
    tmp_path: Path,
) -> None:
    validator = _load_script("validate_version_readiness")
    pyproject = tmp_path / "pyproject.toml"
    baseline = tmp_path / "api-baseline.json"
    pyproject.write_text('[project]\nversion = "1.2.0rc1"\n', encoding="utf-8")
    baseline.write_text('{"baseline_version": "1.2.0rc1"}\n', encoding="utf-8")

    assert validator.validate_version_readiness(
        pyproject,
        baseline,
        "python-v1.2.0rc1",
    ) == ("1.2.0rc1", "python-v1.2.0rc1")

    pyproject.write_text('[project]\nversion = "1.2.0-rc1"\n', encoding="utf-8")
    with pytest.raises(ValueError, match="canonical PEP 440"):
        validator.validate_version_readiness(pyproject, baseline)


@pytest.mark.parametrize(
    "version",
    [
        "1.0.0",
        "1.0.0a1",
        "1.0.0b2",
        "1.0.0rc1",
        "1.0.0.dev1",
        "1.0.0rc1.dev2",
    ],
)
def test_publishable_version_contract_accepts_semver_shaped_pep440(
    version: str,
    tmp_path: Path,
) -> None:
    validator = _load_script("validate_version_readiness")
    release_validator = _load_script("validate_release_tag")
    pyproject = tmp_path / "pyproject.toml"
    baseline = tmp_path / "api-baseline.json"
    pyproject.write_text(f'[project]\nversion = "{version}"\n', encoding="utf-8")
    baseline.write_text(f'{{"baseline_version": "{version}"}}\n', encoding="utf-8")

    assert validator.validate_publishable_version(version) == version
    assert (
        release_validator.validate_release_tag(
            f"python-v{version}",
            pyproject,
            baseline,
        )
        == version
    )


@pytest.mark.parametrize(
    ("version", "error"),
    [
        ("1.0", "MAJOR.MINOR.PATCH"),
        ("1.0.0-rc1", "canonical PEP 440"),
        ("1.0.0.post1", "post release"),
        ("1!1.0.0", "epoch"),
        ("1.0.0+linux", "local version"),
        ("1.0.0.0", "MAJOR.MINOR.PATCH"),
    ],
)
def test_publishable_version_contract_rejects_non_release_forms(
    version: str,
    error: str,
) -> None:
    validator = _load_script("validate_version_readiness")

    with pytest.raises(ValueError, match=error):
        validator.validate_publishable_version(version)


def test_version_readiness_rejects_baseline_and_tag_mismatches(tmp_path: Path) -> None:
    validator = _load_script("validate_version_readiness")
    pyproject = tmp_path / "pyproject.toml"
    baseline = tmp_path / "api-baseline.json"
    pyproject.write_text('[project]\nversion = "1.2.3"\n', encoding="utf-8")
    baseline.write_text('{"baseline_version": "1.2.2"}\n', encoding="utf-8")

    with pytest.raises(ValueError, match="API baseline"):
        validator.validate_version_readiness(pyproject, baseline)

    baseline.write_text('{"baseline_version": "1.2.3"}\n', encoding="utf-8")
    with pytest.raises(ValueError, match="does not match manifest tag"):
        validator.validate_version_readiness(pyproject, baseline, "python-v1.2.2")
