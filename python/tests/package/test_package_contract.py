from __future__ import annotations

import subprocess
import sys
from importlib.metadata import metadata, version
from pathlib import Path

import agent_framework_mongodb


def test_distribution_metadata_uses_canonical_repository_facts() -> None:
    package_metadata = metadata("agent-framework-mongodb")

    assert package_metadata["Name"] == "agent-framework-mongodb"
    assert package_metadata["License-Expression"] == "MIT"
    assert package_metadata["Author"] == "Shankar Narayanan SGS"
    assert package_metadata["Requires-Python"] == ">=3.10"
    assert package_metadata["Project-URL"] == (
        "Source, https://github.com/mongo/ms-agent-framework-mongodb"
    )
    assert set(package_metadata.get_all("Classifier", [])) == {
        "Development Status :: 2 - Pre-Alpha",
        "License :: OSI Approved :: MIT License",
        "Operating System :: OS Independent",
        "Programming Language :: Python :: 3",
        "Programming Language :: Python :: 3.10",
        "Programming Language :: Python :: 3 :: Only",
        "Typing :: Typed",
    }


def test_package_exposes_installed_distribution_version() -> None:
    assert agent_framework_mongodb.__version__ == version("agent-framework-mongodb")


def test_package_contains_a_typing_marker() -> None:
    marker = Path(agent_framework_mongodb.__file__).parent / "py.typed"

    assert marker.read_text(encoding="utf-8") == ""


def test_public_api_matches_first_release_baseline() -> None:
    project_root = Path(__file__).resolve().parents[2]
    result = subprocess.run(
        [
            sys.executable,
            str(project_root / "scripts" / "check_api_baseline.py"),
            str(project_root / "api-baseline.json"),
        ],
        cwd=project_root,
        check=False,
        capture_output=True,
        text=True,
    )

    assert result.returncode == 0, result.stdout + result.stderr
