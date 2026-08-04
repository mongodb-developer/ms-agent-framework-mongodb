"""Validate that a Python release tag matches reviewed package metadata."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from validate_version_readiness import validate_version_readiness


def validate_release_tag(tag: str, pyproject: Path, baseline: Path) -> str:
    project_version, _ = validate_version_readiness(
        pyproject,
        baseline,
        expected_tag=tag,
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
