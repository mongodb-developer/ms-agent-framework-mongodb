from __future__ import annotations

import json
import subprocess
import sys
from copy import deepcopy
from enum import Enum
from importlib.metadata import metadata, version
from pathlib import Path
from types import ModuleType
from typing import Any, cast

import agent_framework_mongodb
from scripts.check_api_baseline import snapshot_public_api


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


def _run_api_check(project_root: Path, baseline: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [
            sys.executable,
            str(project_root / "scripts" / "check_api_baseline.py"),
            str(baseline),
        ],
        cwd=project_root,
        check=False,
        capture_output=True,
        text=True,
    )


def test_api_baseline_covers_public_methods_and_properties() -> None:
    project_root = Path(__file__).resolve().parents[2]
    baseline = json.loads((project_root / "api-baseline.json").read_text(encoding="utf-8"))

    assert baseline["baseline_version"] == agent_framework_mongodb.__version__
    assert baseline["classes"]["MongoDBSessionStore"]["constructor"]
    assert baseline["classes"]["MongoDBSessionStore"]["members"]["create"]["kind"] == "method"
    assert baseline["classes"]["MongoDBSessionStore"]["members"]["owns_client"] == {
        "getter": "(self) -> 'bool'",
        "kind": "property",
    }
    assert baseline["classes"]["MongoDBRAGResult"]["members"]["to_citation"]["kind"] == "method"


def test_api_check_rejects_baseline_version_mismatch(tmp_path: Path) -> None:
    project_root = Path(__file__).resolve().parents[2]
    baseline = json.loads((project_root / "api-baseline.json").read_text(encoding="utf-8"))
    baseline["baseline_version"] = "999.0.0"
    changed = tmp_path / "api-baseline.json"
    changed.write_text(json.dumps(baseline), encoding="utf-8")

    result = _run_api_check(project_root, changed)

    assert result.returncode == 1
    assert "baseline version 999.0.0 does not match installed version" in result.stdout


def test_api_check_rejects_public_method_removal(tmp_path: Path) -> None:
    project_root = Path(__file__).resolve().parents[2]
    baseline = json.loads((project_root / "api-baseline.json").read_text(encoding="utf-8"))
    changed_baseline = deepcopy(baseline)
    del changed_baseline["classes"]["MongoDBSessionStore"]["members"]["create"]
    changed = tmp_path / "api-baseline.json"
    changed.write_text(json.dumps(changed_baseline), encoding="utf-8")

    result = _run_api_check(project_root, changed)

    assert result.returncode == 1
    assert "Public API differs from the reviewed baseline." in result.stdout


def test_api_baseline_covers_complete_exported_enum_members() -> None:
    project_root = Path(__file__).resolve().parents[2]
    baseline = json.loads((project_root / "api-baseline.json").read_text(encoding="utf-8"))

    assert baseline["classes"]["MongoDBSearchMode"]["enum_members"] == {
        "FULL_TEXT": {"canonical": "FULL_TEXT", "serialized_value": '"full_text"'},
        "HYBRID_RRF": {"canonical": "HYBRID_RRF", "serialized_value": '"hybrid_rrf"'},
        "VECTOR_ANN": {"canonical": "VECTOR_ANN", "serialized_value": '"vector_ann"'},
        "VECTOR_ENN": {"canonical": "VECTOR_ENN", "serialized_value": '"vector_enn"'},
    }
    assert baseline["classes"]["MongoDBIndexState"]["enum_members"] == {
        "BUILDING": {"canonical": "BUILDING", "serialized_value": '"building"'},
        "FAILED": {"canonical": "FAILED", "serialized_value": '"failed"'},
        "MISSING": {"canonical": "MISSING", "serialized_value": '"missing"'},
        "READY": {"canonical": "READY", "serialized_value": '"ready"'},
        "READY_NOT_QUERYABLE": {
            "canonical": "READY_NOT_QUERYABLE",
            "serialized_value": '"ready_not_queryable"',
        },
        "TIMEOUT": {"canonical": "TIMEOUT", "serialized_value": '"timeout"'},
    }


def test_api_check_rejects_enum_removal_alias_and_value_changes(tmp_path: Path) -> None:
    project_root = Path(__file__).resolve().parents[2]
    baseline = json.loads((project_root / "api-baseline.json").read_text(encoding="utf-8"))
    variants: list[dict[str, Any]] = []
    removed = deepcopy(baseline)
    del removed["classes"]["MongoDBSearchMode"]["enum_members"]["VECTOR_ENN"]
    variants.append(removed)
    renamed = deepcopy(baseline)
    renamed["classes"]["MongoDBSearchMode"]["enum_members"]["VECTOR_EXACT"] = renamed["classes"][
        "MongoDBSearchMode"
    ]["enum_members"].pop("VECTOR_ENN")
    variants.append(renamed)
    changed_alias = deepcopy(baseline)
    changed_alias["classes"]["MongoDBSearchMode"]["enum_members"]["VECTOR_ANN"]["canonical"] = (
        "VECTOR_ENN"
    )
    variants.append(changed_alias)
    changed_value = deepcopy(baseline)
    changed_value["classes"]["MongoDBIndexState"]["enum_members"]["READY"]["serialized_value"] = (
        '"available"'
    )
    variants.append(changed_value)

    for index, changed_baseline in enumerate(variants):
        changed = tmp_path / f"api-baseline-{index}.json"
        changed.write_text(json.dumps(changed_baseline), encoding="utf-8")
        result = _run_api_check(project_root, changed)
        assert result.returncode == 1
        assert "Public API differs from the reviewed baseline." in result.stdout


def test_api_snapshot_recurses_package_owned_descriptor_kinds() -> None:
    class PublicBase:
        @property
        def enabled(self) -> bool:
            return True

        def inherited(self, value: int = 1) -> int:
            return value

    class PublicProvider(PublicBase):
        @classmethod
        def create(cls, name: str) -> PublicProvider:
            del name
            return cls()

        @staticmethod
        def normalize(value: str) -> str:
            return value

    package = ModuleType("fixture_package")
    PublicBase.__module__ = package.__name__
    PublicProvider.__module__ = package.__name__
    dynamic_package = cast(Any, package)
    dynamic_package.__all__ = ["PublicProvider"]
    dynamic_package.PublicProvider = PublicProvider

    provider = snapshot_public_api(package)["classes"]["PublicProvider"]

    assert provider["members"] == {
        "create": {
            "kind": "classmethod",
            "signature": "(name: 'str') -> 'PublicProvider'",
        },
        "enabled": {"getter": "(self) -> 'bool'", "kind": "property"},
        "inherited": {
            "kind": "method",
            "signature": "(self, value: 'int' = 1) -> 'int'",
        },
        "normalize": {
            "kind": "staticmethod",
            "signature": "(value: 'str') -> 'str'",
        },
    }


def test_api_snapshot_records_enum_aliases_and_stable_values() -> None:
    class PublicStatus(Enum):
        READY = "ready"
        AVAILABLE = "ready"
        FAILED = "failed"

    package = ModuleType("fixture_package")
    PublicStatus.__module__ = package.__name__
    dynamic_package = cast(Any, package)
    dynamic_package.__all__ = ["PublicStatus"]
    dynamic_package.PublicStatus = PublicStatus

    status = snapshot_public_api(package)["classes"]["PublicStatus"]

    assert status["enum_members"] == {
        "AVAILABLE": {"canonical": "READY", "serialized_value": '"ready"'},
        "FAILED": {"canonical": "FAILED", "serialized_value": '"failed"'},
        "READY": {"canonical": "READY", "serialized_value": '"ready"'},
    }


def test_release_tag_must_match_reviewed_package_and_baseline_version() -> None:
    project_root = Path(__file__).resolve().parents[2]
    result = subprocess.run(
        [
            sys.executable,
            str(project_root / "scripts" / "validate_release_tag.py"),
            "python-v0.1.0.dev0",
            "--pyproject",
            str(project_root / "pyproject.toml"),
            "--baseline",
            str(project_root / "api-baseline.json"),
        ],
        check=False,
        capture_output=True,
        text=True,
    )

    assert result.returncode == 0, result.stdout + result.stderr
    assert result.stdout.strip() == "0.1.0.dev0"


def test_release_tag_rejects_unreviewed_version() -> None:
    project_root = Path(__file__).resolve().parents[2]
    result = subprocess.run(
        [
            sys.executable,
            str(project_root / "scripts" / "validate_release_tag.py"),
            "python-v0.1.0",
            "--pyproject",
            str(project_root / "pyproject.toml"),
            "--baseline",
            str(project_root / "api-baseline.json"),
        ],
        check=False,
        capture_output=True,
        text=True,
    )

    assert result.returncode == 1
    assert (
        "expected tag python-v0.1.0 does not match manifest tag python-v0.1.0.dev0" in result.stderr
    )


def test_public_smoke_rejects_unexpected_installed_version() -> None:
    project_root = Path(__file__).resolve().parents[2]
    result = subprocess.run(
        [
            sys.executable,
            str(project_root / "scripts" / "smoke_public_api.py"),
            "--expected-version",
            "999.0.0",
        ],
        check=False,
        capture_output=True,
        text=True,
    )

    assert result.returncode != 0
    assert "expected installed version 999.0.0" in result.stdout + result.stderr
