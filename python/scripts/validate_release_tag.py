"""Validate that a Python release tag matches reviewed package metadata."""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

_TAG = re.compile(
    r"python-v(?P<version>[0-9]+\.[0-9]+\.[0-9]+"
    r"(?:(?:a|b|rc)[0-9]+|\.dev[0-9]+)?)"
)
_PROJECT_VERSION = re.compile(r'^version = "([^"]+)"$', re.MULTILINE)


def validate_release_tag(tag: str, pyproject: Path, baseline: Path) -> str:
    match = _TAG.fullmatch(tag)
    if match is None:
        raise ValueError("tag must use canonical python-v<version> syntax")
    tag_version = match.group("version")
    project_text = pyproject.read_text(encoding="utf-8")
    project_match = _PROJECT_VERSION.search(project_text)
    if project_match is None:
        raise ValueError("pyproject.toml must contain one static project version")
    project_version = project_match.group(1)
    if tag_version != project_version:
        raise ValueError(
            f"tag version {tag_version} does not match reviewed package version {project_version}"
        )
    baseline_version = json.loads(baseline.read_text(encoding="utf-8")).get("baseline_version")
    if baseline_version != project_version:
        raise ValueError(
            f"API baseline version {baseline_version} does not match reviewed package "
            f"version {project_version}"
        )
    return project_version


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("tag")
    parser.add_argument("--pyproject", required=True, type=Path)
    parser.add_argument("--baseline", required=True, type=Path)
    args = parser.parse_args()
    try:
        release_version = validate_release_tag(
            args.tag,
            args.pyproject,
            args.baseline,
        )
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(exc, file=sys.stderr)
        return 1
    print(release_version)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
