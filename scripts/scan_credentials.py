"""Scan tracked text files for high-confidence credential patterns."""

from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

_PATTERNS = {
    "AWS access key": re.compile(r"\bAKIA[0-9A-Z]{16}\b"),
    "GitHub token": re.compile(r"\b(?:gh[pousr]_[A-Za-z0-9_]{36,}|github_pat_[A-Za-z0-9_]{50,})\b"),
    "MongoDB URI credentials": re.compile(
        r"mongodb(?:\+srv)?://[^/\s:@]+:[^@\s/]+@",
        re.IGNORECASE,
    ),
    "private key": re.compile(r"-----BEGIN (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----"),
}


def _tracked_files() -> tuple[Path, ...]:
    result = subprocess.run(
        ["git", "ls-files", "-z"],
        check=True,
        capture_output=True,
    )
    return tuple(Path(value) for value in result.stdout.decode("utf-8").split("\0") if value)


def main() -> int:
    findings: list[str] = []
    for path in _tracked_files():
        try:
            content = path.read_text(encoding="utf-8")
        except (OSError, UnicodeDecodeError):
            continue
        for line_number, line in enumerate(content.splitlines(), start=1):
            for label, pattern in _PATTERNS.items():
                if pattern.search(line):
                    findings.append(f"{path}:{line_number}: possible {label}")
    if findings:
        print("\n".join(findings), file=sys.stderr)
        return 1
    print("No high-confidence credential patterns found.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
