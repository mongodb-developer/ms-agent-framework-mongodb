"""Verify Python release archives contain only intended package files."""

from __future__ import annotations

import argparse
import glob
import hashlib
import json
import re
import tarfile
from pathlib import Path, PurePosixPath
from zipfile import ZipFile

_DENIED_PARTS = {
    ".env",
    ".git",
    ".github",
    ".mypy_cache",
    ".pytest_cache",
    ".ruff_cache",
    "__pycache__",
    "samples",
    "tests",
}
_DENIED_SUFFIXES = {".key", ".p12", ".pem", ".pfx", ".pyc", ".pyo"}
_SDIST_FILES = {".gitignore", "LICENSE", "PKG-INFO", "README.md", "pyproject.toml"}


def _path_issue(name: str) -> str | None:
    if "\\" in name:
        return f"non-portable archive path: {name}"
    path = PurePosixPath(name)
    if path.is_absolute() or ".." in path.parts:
        return f"unsafe archive path: {name}"
    lowered = {part.lower() for part in path.parts}
    if lowered & _DENIED_PARTS:
        return f"denied package content: {name}"
    if path.name.lower() == "local.settings.json":
        return f"denied local configuration: {name}"
    if path.suffix.lower() in _DENIED_SUFFIXES:
        return f"denied generated or credential file: {name}"
    return None


def _verify_wheel(path: Path) -> list[str]:
    with ZipFile(path) as archive:
        names = [name for name in archive.namelist() if not name.endswith("/")]
    issues = [issue for name in names if (issue := _path_issue(name)) is not None]
    for name in names:
        first = PurePosixPath(name).parts[0]
        if first == "agent_framework_mongodb":
            continue
        if first.startswith("agent_framework_mongodb-") and first.endswith(".dist-info"):
            continue
        issues.append(f"unexpected wheel content: {name}")
    required_suffixes = {
        "agent_framework_mongodb/__init__.py",
        "agent_framework_mongodb/py.typed",
        ".dist-info/METADATA",
        ".dist-info/WHEEL",
        ".dist-info/RECORD",
        ".dist-info/licenses/LICENSE",
    }
    for required in required_suffixes:
        if not any(name.endswith(required) for name in names):
            issues.append(f"missing wheel content: *{required}")
    return issues


def _verify_sdist(path: Path) -> list[str]:
    with tarfile.open(path, "r:gz") as archive:
        members = archive.getmembers()
    issues: list[str] = []
    roots = {PurePosixPath(member.name).parts[0] for member in members if member.name}
    if len(roots) != 1:
        issues.append("source distribution must have exactly one root directory")
        return issues
    root = next(iter(roots))
    names: list[str] = []
    for member in members:
        if member.isdir():
            continue
        if not member.isfile():
            issues.append(f"source distribution contains a link or special file: {member.name}")
            continue
        names.append(member.name)
        if issue := _path_issue(member.name):
            issues.append(issue)
        relative = PurePosixPath(member.name).relative_to(root).as_posix()
        if relative in _SDIST_FILES:
            continue
        if relative.startswith("src/agent_framework_mongodb/"):
            continue
        issues.append(f"unexpected source distribution content: {member.name}")
    required = {
        "LICENSE",
        "PKG-INFO",
        "README.md",
        "pyproject.toml",
        "src/agent_framework_mongodb/__init__.py",
        "src/agent_framework_mongodb/py.typed",
    }
    relative_names = {PurePosixPath(name).relative_to(root).as_posix() for name in names}
    for required_name in required - relative_names:
        issues.append(f"missing source distribution content: {required_name}")
    return issues


def verify_artifact(path: Path) -> list[str]:
    if path.name.endswith(".whl"):
        return _verify_wheel(path)
    if path.name.endswith(".tar.gz"):
        return _verify_sdist(path)
    return [f"unsupported distribution artifact type: {path.name}"]


def _verify_sbom(path: Path) -> list[str]:
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        return [f"invalid CycloneDX JSON: {exc}"]
    issues: list[str] = []
    if not isinstance(document, dict) or document.get("bomFormat") != "CycloneDX":
        issues.append("SBOM must be a CycloneDX JSON document")
        return issues
    if not isinstance(document.get("specVersion"), str):
        issues.append("CycloneDX SBOM must declare specVersion")
    if not isinstance(document.get("version"), int):
        issues.append("CycloneDX SBOM must declare an integer version")
    if not isinstance(document.get("components"), list):
        issues.append("CycloneDX SBOM must contain a components list")
    return issues


def _checksum_target(checksum_file: Path, name: str) -> Path | None:
    relative = Path(name)
    if relative.is_absolute() or ".." in relative.parts:
        return None
    candidates = (
        relative,
        checksum_file.parent / relative,
        checksum_file.parent / "packages" / relative,
    )
    return next((candidate for candidate in candidates if candidate.is_file()), None)


def _verify_checksums(path: Path) -> list[str]:
    issues: list[str] = []
    seen: set[str] = set()
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except (OSError, UnicodeError) as exc:
        return [f"unable to read checksum manifest: {exc}"]
    if not lines:
        return ["checksum manifest must not be empty"]
    for line_number, line in enumerate(lines, start=1):
        match = re.fullmatch(r"([0-9a-fA-F]{64}) [ *](.+)", line)
        if match is None:
            issues.append(f"invalid checksum entry on line {line_number}")
            continue
        expected, name = match.groups()
        if name in seen:
            issues.append(f"duplicate checksum entry: {name}")
            continue
        seen.add(name)
        if not (
            name.endswith(".whl") or name.endswith(".tar.gz") or name.endswith(".sbom.cdx.json")
        ):
            issues.append(f"unsupported checksum target: {name}")
            continue
        target = _checksum_target(path, name)
        if target is None:
            issues.append(f"checksum target does not exist: {name}")
            continue
        actual = hashlib.sha256(target.read_bytes()).hexdigest()
        if actual.lower() != expected.lower():
            issues.append(f"checksum mismatch: {name}")
    return issues


def verify_supplemental(path: Path) -> list[str]:
    if path.name.endswith(".sbom.cdx.json"):
        return _verify_sbom(path)
    if path.name.endswith("SHA256SUMS"):
        return _verify_checksums(path)
    return [f"unsupported supplemental artifact type: {path.name}"]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifacts", nargs="*")
    parser.add_argument(
        "--supplemental",
        action="store_true",
        help="validate SBOM and checksum files separately from distributions",
    )
    args = parser.parse_args()
    if not args.artifacts:
        parser.error("at least one artifact is required")
    failed = False
    artifacts = [
        Path(match) for pattern in args.artifacts for match in (glob.glob(pattern) or [pattern])
    ]
    for artifact in artifacts:
        issues = verify_supplemental(artifact) if args.supplemental else verify_artifact(artifact)
        if issues:
            failed = True
            print(f"{artifact}:")
            for issue in issues:
                print(f"  - {issue}")
        else:
            artifact_kind = "supplemental" if args.supplemental else "package content"
            print(f"{artifact}: {artifact_kind} policy passed")
    return int(failed)


if __name__ == "__main__":
    raise SystemExit(main())
