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
    assert "build" in completed.stdout
    assert "No upload or publication command is present." in completed.stdout
    assert "twine upload" not in completed.stdout
