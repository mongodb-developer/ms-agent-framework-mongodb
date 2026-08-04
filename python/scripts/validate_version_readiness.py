"""Validate canonical Python manifest, API baseline, and release-tag agreement."""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from pathlib import Path

from packaging.version import InvalidVersion, Version

_PROJECT_VERSION = re.compile(r'^version = "([^"]+)"$', re.MULTILINE)


def validate_version_readiness(
    pyproject: Path,
    baseline: Path,
    expected_tag: str | None = None,
) -> tuple[str, str]:
    project_text = pyproject.read_text(encoding="utf-8")
    matches = _PROJECT_VERSION.findall(project_text)
    if len(matches) != 1:
        raise ValueError("pyproject.toml must contain exactly one static project version")
    raw_version = matches[0]
    try:
        parsed = Version(raw_version)
    except InvalidVersion as exc:
        raise ValueError(f"project version is not valid PEP 440: {raw_version}") from exc
    canonical_version = str(parsed)
    if raw_version != canonical_version:
        raise ValueError(f"project version must use canonical PEP 440 form: {canonical_version}")
    baseline_version = json.loads(baseline.read_text(encoding="utf-8")).get("baseline_version")
    if baseline_version != canonical_version:
        raise ValueError(
            f"API baseline version {baseline_version} does not match {canonical_version}"
        )
    tag = f"python-v{canonical_version}"
    if expected_tag is not None and expected_tag != tag:
        raise ValueError(f"expected tag {expected_tag} does not match manifest tag {tag}")
    return canonical_version, tag


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pyproject", required=True, type=Path)
    parser.add_argument("--baseline", required=True, type=Path)
    parser.add_argument("--expected-tag")
    parser.add_argument(
        "--output",
        choices=("summary", "version", "tag"),
        default="summary",
        help="select the successful stdout value",
    )
    args = parser.parse_args()
    try:
        version, tag = validate_version_readiness(
            args.pyproject,
            args.baseline,
            args.expected_tag,
        )
        github_output = os.environ.get("GITHUB_OUTPUT")
        if github_output:
            with Path(github_output).open("a", encoding="utf-8") as output:
                output.write(f"version={version}\ntag={tag}\n")
    except (OSError, ValueError, json.JSONDecodeError) as exc:
        print(exc, file=sys.stderr)
        return 1
    values = {"summary": f"{tag} ({version})", "version": version, "tag": tag}
    print(values[args.output])
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
