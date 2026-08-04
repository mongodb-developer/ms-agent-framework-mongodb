"""Run the complete local, non-publishing Python release rehearsal."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
import time
from pathlib import Path

from resolve_framework_versions import fetch_pypi, resolve_matrix, write_report
from run_framework_compatibility import run_row

_ROOT = Path(__file__).resolve().parents[1]
_REPOSITORY = _ROOT.parent
_OUTPUT = _ROOT / "dist" / "rehearsal"


def _venv_python(path: Path) -> Path:
    return path / ("Scripts/python.exe" if os.name == "nt" else "bin/python")


def command_plan(python: str = sys.executable) -> list[list[str]]:
    return [
        [
            python,
            "-m",
            "pytest",
            "--cov=agent_framework_mongodb",
            "--cov-report=term",
            "--cov-report=xml:dist/rehearsal/coverage.xml",
            "--junitxml=dist/rehearsal/tests.xml",
            "-q",
        ],
        [
            python,
            "-m",
            "ruff",
            "check",
            "src",
            "tests",
            "samples",
            "scripts",
            "../scripts/scan_credentials.py",
        ],
        [
            python,
            "-m",
            "ruff",
            "format",
            "--check",
            "src",
            "tests",
            "samples",
            "scripts",
            "../scripts/scan_credentials.py",
        ],
        [python, "-m", "mypy"],
        [python, "-m", "pyright"],
        [python, "scripts/check_api_baseline.py", "api-baseline.json"],
        [python, "../scripts/scan_credentials.py"],
        [python, "-m", "build", "--outdir", "dist/rehearsal/packages"],
    ]


def _run(command: list[str], report: list[dict[str, object]]) -> None:
    started = time.monotonic()
    completed = subprocess.run(command, cwd=_ROOT, check=False)  # noqa: S603
    report.append(
        {
            "command": subprocess.list2cmdline(command),
            "exit_code": completed.returncode,
            "duration_seconds": round(time.monotonic() - started, 3),
        }
    )
    if completed.returncode:
        raise subprocess.CalledProcessError(completed.returncode, command)


def _write_checksums(paths: list[Path]) -> None:
    entries = [
        f"{hashlib.sha256(path.read_bytes()).hexdigest()} *{path.name}"
        for path in sorted(paths, key=lambda item: item.name)
    ]
    (_OUTPUT / "SHA256SUMS").write_text("\n".join(entries) + "\n", encoding="utf-8")


def _write_report(commands: list[dict[str, object]], succeeded: bool) -> None:
    machine = {"succeeded": succeeded, "publishing_attempted": False, "commands": commands}
    (_OUTPUT / "rehearsal-report.json").write_text(
        json.dumps(machine, indent=2) + "\n", encoding="utf-8"
    )
    rows = "\n".join(
        f"| `{item['command']}` | {item['exit_code']} | {item['duration_seconds']} |"
        for item in commands
    )
    (_OUTPUT / "rehearsal-report.md").write_text(
        "# Python release rehearsal\n\n"
        f"Result: **{'passed' if succeeded else 'failed'}**\n\n"
        "Publishing attempted: **no**\n\n"
        "| Command | Exit | Seconds |\n| --- | ---: | ---: |\n"
        f"{rows}\n",
        encoding="utf-8",
    )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="print the validated command plan without changing files or running tools",
    )
    args = parser.parse_args()
    plan = command_plan()
    if args.dry_run:
        print("\n".join(subprocess.list2cmdline(command) for command in plan))
        print(
            "Dynamically resolve and run isolated latest-stable and previous-stable "
            "Agent Framework compatibility rows."
        )
        print("No upload or publication command is present.")
        return 0

    if Path.cwd().resolve() != _ROOT:
        print(f"run this command from {_ROOT}", file=sys.stderr)
        return 2
    shutil.rmtree(_OUTPUT, ignore_errors=True)
    _OUTPUT.mkdir(parents=True)
    commands: list[dict[str, object]] = []
    environments: list[Path] = []
    succeeded = False
    try:
        for command in plan:
            _run(command, commands)
        matrix = resolve_matrix(fetch_pypi())
        (_OUTPUT / "framework-resolution.json").write_text(
            json.dumps({"include": matrix}, indent=2) + "\n", encoding="utf-8"
        )
        write_report(_OUTPUT / "framework-resolution.md", matrix, False)
        for row in matrix:
            compatibility_dir = _OUTPUT / "compatibility" / f"{row['label']}-{row['version']}"
            started = time.monotonic()
            return_code = run_row(
                row["version"],
                row["label"],
                row["channel"],
                compatibility_dir,
            )
            commands.append(
                {
                    "command": (
                        "run isolated Agent Framework compatibility row "
                        f"{row['label']}=={row['version']}"
                    ),
                    "exit_code": return_code,
                    "duration_seconds": round(time.monotonic() - started, 3),
                }
            )
            if return_code:
                raise RuntimeError(
                    f"Agent Framework compatibility failed for {row['label']} ({row['version']})"
                )
        packages = list((_OUTPUT / "packages").glob("*.whl")) + list(
            (_OUTPUT / "packages").glob("*.tar.gz")
        )
        if len(packages) != 2:
            raise RuntimeError("build must produce exactly one wheel and one source distribution")
        _run(
            [sys.executable, "-m", "twine", "check", *(str(path) for path in packages)],
            commands,
        )
        _run(
            [sys.executable, "scripts/verify_artifacts.py", *(str(path) for path in packages)],
            commands,
        )
        for kind, artifact in (("wheel", packages[0]), ("sdist", packages[1])):
            environment = _ROOT / f".rehearsal-{kind}"
            shutil.rmtree(environment, ignore_errors=True)
            environments.append(environment)
            _run([sys.executable, "-m", "venv", str(environment)], commands)
            consumer_python = str(_venv_python(environment))
            _run(
                [
                    consumer_python,
                    "-m",
                    "pip",
                    "install",
                    "--disable-pip-version-check",
                    "--no-cache-dir",
                    str(artifact),
                ],
                commands,
            )
            _run([consumer_python, "scripts/smoke_public_api.py"], commands)
            _run([consumer_python, "-m", "pydoc", "agent_framework_mongodb"], commands)
        _write_checksums(packages)
        succeeded = True
    except (OSError, RuntimeError, subprocess.CalledProcessError) as exc:
        print(f"release rehearsal failed: {exc}", file=sys.stderr)
    finally:
        _write_report(commands, succeeded)
        for environment in environments:
            shutil.rmtree(environment, ignore_errors=True)
    return int(not succeeded)


if __name__ == "__main__":
    raise SystemExit(main())
