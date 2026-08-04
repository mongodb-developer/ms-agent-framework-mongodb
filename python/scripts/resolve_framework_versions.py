"""Resolve Agent Framework Core compatibility versions from the official PyPI API."""

from __future__ import annotations

import argparse
import json
import os
import sys
import urllib.request
from pathlib import Path
from typing import Any

from packaging.requirements import Requirement
from packaging.specifiers import SpecifierSet
from packaging.version import InvalidVersion, Version

_ROOT = Path(__file__).resolve().parents[1]
_PYPI_URL = "https://pypi.org/pypi/agent-framework-core/json"


def supported_framework_specifier(pyproject: Path = _ROOT / "pyproject.toml") -> SpecifierSet:
    """Read the package's declared Agent Framework Core compatibility range."""
    for line in pyproject.read_text(encoding="utf-8").splitlines():
        dependency = line.strip().removesuffix(",").strip('"')
        if dependency.startswith("agent-framework-core"):
            return Requirement(dependency).specifier
    raise ValueError(f"{pyproject} does not declare agent-framework-core")


def available_versions(payload: dict[str, Any]) -> list[Version]:
    """Return ordered, non-yanked releases that have at least one distribution."""
    versions: list[Version] = []
    releases = payload.get("releases")
    if not isinstance(releases, dict):
        raise ValueError("PyPI response does not contain a releases mapping")
    for raw_version, files in releases.items():
        if not isinstance(files, list) or not files:
            continue
        if all(isinstance(file, dict) and file.get("yanked", False) for file in files):
            continue
        try:
            versions.append(Version(raw_version))
        except InvalidVersion:
            continue
    return sorted(set(versions), reverse=True)


def resolve_matrix(
    payload: dict[str, Any],
    *,
    include_preview: bool = False,
    exact: str | None = None,
    supported: SpecifierSet | None = None,
) -> list[dict[str, str]]:
    """Build a de-duplicated compatibility matrix from PyPI release metadata."""
    supported = supported or supported_framework_specifier()
    available = available_versions(payload)
    versions = [version for version in available if supported.contains(version, prereleases=True)]
    stable = [
        version for version in versions if not version.is_prerelease and not version.is_devrelease
    ]
    if not stable:
        raise ValueError(
            f"PyPI does not contain a non-yanked stable release in supported range {supported}"
        )

    selected: list[tuple[str, Version]] = [("latest-stable", stable[0])]
    if len(stable) > 1:
        selected.append(("previous-stable", stable[1]))
    if include_preview:
        previews = [
            version for version in versions if version.is_prerelease or version.is_devrelease
        ]
        if previews:
            selected.append(("latest-preview", previews[0]))

    if exact:
        try:
            exact_version = Version(exact)
        except InvalidVersion as exc:
            raise ValueError(f"invalid exact PEP 440 version: {exact}") from exc
        if exact_version not in available:
            raise ValueError(f"exact version is absent, yanked, or has no distributions: {exact}")
        if exact_version not in supported:
            raise ValueError(f"exact version {exact} is outside the supported range {supported}")
        selected.append(("exact", exact_version))

    matrix: list[dict[str, str]] = []
    seen: set[Version] = set()
    for label, version in selected:
        if version in seen:
            continue
        seen.add(version)
        matrix.append(
            {
                "label": label,
                "version": str(version),
                "channel": (
                    "preview" if version.is_prerelease or version.is_devrelease else "stable"
                ),
            }
        )
    return matrix


def fetch_pypi() -> dict[str, Any]:
    request = urllib.request.Request(
        _PYPI_URL,
        headers={"Accept": "application/json", "User-Agent": "ms-agent-framework-mongodb-ci"},
    )
    with urllib.request.urlopen(request, timeout=30) as response:  # noqa: S310
        document = json.load(response)
    if not isinstance(document, dict):
        raise ValueError("PyPI returned a non-object response")
    return document


def write_report(path: Path, matrix: list[dict[str, str]], preview_requested: bool) -> None:
    preview_available = any(item["label"] == "latest-preview" for item in matrix)
    lines = [
        "# Agent Framework Core version resolution",
        "",
        f"Source: `{_PYPI_URL}`",
        "",
        "| Selection | Version | Channel |",
        "| --- | --- | --- |",
    ]
    lines.extend(
        f"| {item['label']} | `{item['version']}` | {item['channel']} |" for item in matrix
    )
    if preview_requested and not preview_available:
        lines.extend(["", "No non-yanked preview release is currently available."])
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--include-preview", action="store_true")
    parser.add_argument("--exact")
    parser.add_argument("--json-output", type=Path, required=True)
    parser.add_argument("--markdown-output", type=Path, required=True)
    args = parser.parse_args()
    try:
        matrix = resolve_matrix(
            fetch_pypi(),
            include_preview=args.include_preview,
            exact=args.exact or None,
        )
        args.json_output.parent.mkdir(parents=True, exist_ok=True)
        args.json_output.write_text(
            json.dumps({"include": matrix}, indent=2) + "\n", encoding="utf-8"
        )
        write_report(args.markdown_output, matrix, args.include_preview)
        github_output = os.environ.get("GITHUB_OUTPUT")
        if github_output:
            with Path(github_output).open("a", encoding="utf-8") as output:
                output.write(f"matrix={json.dumps({'include': matrix}, separators=(',', ':'))}\n")
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(exc, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
