"""Run one isolated, non-publishing Agent Framework compatibility row."""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from collections.abc import Sequence
from email.parser import BytesParser
from pathlib import Path
from zipfile import ZipFile

_ROOT = Path(__file__).resolve().parents[1]


def _venv_python(path: Path) -> Path:
    return path / ("Scripts/python.exe" if os.name == "nt" else "bin/python")


def _non_framework_dependencies(wheel: Path) -> list[str]:
    with ZipFile(wheel) as archive:
        metadata_names = (
            name for name in archive.namelist() if name.endswith(".dist-info/METADATA")
        )
        metadata_name = next(metadata_names)
        metadata = BytesParser().parsebytes(archive.read(metadata_name))
    dependencies = metadata.get_all("Requires-Dist", [])
    return [
        dependency
        for dependency in dependencies
        if not dependency.startswith("agent-framework-core")
    ]


def _record(
    command: Sequence[str],
    results: list[dict[str, object]],
    *,
    capture: bool = False,
) -> subprocess.CompletedProcess[str]:
    started = time.monotonic()
    completed = subprocess.run(  # noqa: S603
        command,
        cwd=_ROOT,
        check=False,
        capture_output=capture,
        text=True,
    )
    results.append(
        {
            "command": subprocess.list2cmdline(command),
            "exit_code": completed.returncode,
            "duration_seconds": round(time.monotonic() - started, 3),
        }
    )
    return completed


def _write_junit_failure(path: Path, message: str) -> None:
    suite = ET.Element(
        "testsuite",
        name="agent-framework-compatibility-setup",
        tests="1",
        failures="1",
    )
    case = ET.SubElement(suite, "testcase", name="compatibility-row")
    ET.SubElement(case, "failure", message=message).text = message
    ET.ElementTree(suite).write(path, encoding="utf-8", xml_declaration=True)


def _write_reports(
    report_dir: Path,
    *,
    label: str,
    version: str,
    channel: str,
    results: list[dict[str, object]],
    succeeded: bool,
) -> None:
    machine = {
        "label": label,
        "version": version,
        "channel": channel,
        "succeeded": succeeded,
        "publishing_attempted": False,
        "commands": results,
    }
    (report_dir / "summary.json").write_text(json.dumps(machine, indent=2) + "\n", encoding="utf-8")
    rows = "\n".join(
        f"| `{item['command']}` | {item['exit_code']} | {item['duration_seconds']} |"
        for item in results
    )
    (report_dir / "summary.md").write_text(
        "# Agent Framework Core compatibility\n\n"
        f"- Selection: `{label}`\n"
        f"- Version: `{version}`\n"
        f"- Channel: `{channel}`\n"
        f"- Result: **{'passed' if succeeded else 'failed'}**\n"
        "- Publishing attempted: **no**\n\n"
        "| Command | Exit | Seconds |\n| --- | ---: | ---: |\n"
        f"{rows}\n",
        encoding="utf-8",
    )


def run_row(version: str, label: str, channel: str, report_dir: Path) -> int:
    """Execute a complete exact-version row and retain evidence on every failure."""
    report_dir.mkdir(parents=True, exist_ok=True)
    environment = _ROOT / f".framework-{label}"
    consumers = [_ROOT / f".framework-{label}-wheel", _ROOT / f".framework-{label}-sdist"]
    for path in [environment, *consumers]:
        shutil.rmtree(path, ignore_errors=True)

    results: list[dict[str, object]] = []
    failure = ""
    try:
        setup_commands = [
            [sys.executable, "-m", "venv", str(environment)],
            [
                str(_venv_python(environment)),
                "-m",
                "pip",
                "install",
                "--disable-pip-version-check",
                "-e",
                ".[dev]",
            ],
            [
                str(_venv_python(environment)),
                "-m",
                "pip",
                "install",
                "--disable-pip-version-check",
                "--force-reinstall",
                "--no-deps",
                f"agent-framework-core=={version}",
            ],
        ]
        for command in setup_commands:
            completed = _record(command, results)
            if completed.returncode:
                failure = f"setup command failed with exit code {completed.returncode}"
                break

        row_python = str(_venv_python(environment))
        if not failure:
            commands = [
                [
                    row_python,
                    "-m",
                    "pytest",
                    "--cov=agent_framework_mongodb",
                    "--cov-report=term",
                    f"--cov-report=xml:{report_dir / 'coverage.xml'}",
                    f"--junitxml={report_dir / 'pytest.xml'}",
                    "-q",
                ],
                [
                    row_python,
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
                    row_python,
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
                [row_python, "-m", "mypy"],
                [row_python, "-m", "pyright"],
                [row_python, "scripts/check_api_baseline.py", "api-baseline.json"],
                [row_python, "../scripts/scan_credentials.py"],
                [row_python, "-m", "build", "--outdir", str(report_dir / "packages")],
            ]
            for command in commands:
                completed = _record(command, results)
                if completed.returncode:
                    failure = f"gate command failed with exit code {completed.returncode}"
                    break

        packages = sorted((report_dir / "packages").glob("*")) if not failure else []
        wheel = next((path for path in packages if path.suffix == ".whl"), None)
        sdist = next((path for path in packages if path.name.endswith(".tar.gz")), None)
        if not failure and (wheel is None or sdist is None or len(packages) != 2):
            failure = "build did not produce exactly one wheel and one source distribution"
        if not failure:
            validation = [
                [row_python, "-m", "twine", "check", str(wheel), str(sdist)],
                [row_python, "scripts/verify_artifacts.py", str(wheel), str(sdist)],
            ]
            for command in validation:
                completed = _record(command, results)
                if completed.returncode:
                    failure = f"package validation failed with exit code {completed.returncode}"
                    break

        for kind, artifact, consumer in (
            ("wheel", wheel, consumers[0]),
            ("sdist", sdist, consumers[1]),
        ):
            if failure:
                break
            assert artifact is not None
            consumer_commands = [
                [sys.executable, "-m", "venv", str(consumer)],
                [
                    str(_venv_python(consumer)),
                    "-m",
                    "pip",
                    "install",
                    "--disable-pip-version-check",
                    f"agent-framework-core=={version}",
                    *_non_framework_dependencies(wheel),
                ],
                [
                    str(_venv_python(consumer)),
                    "-m",
                    "pip",
                    "install",
                    "--disable-pip-version-check",
                    "--no-deps",
                    str(artifact),
                ],
                [str(_venv_python(consumer)), "scripts/smoke_public_api.py"],
            ]
            if kind == "wheel":
                consumer_commands.append(
                    [str(_venv_python(consumer)), "-m", "pydoc", "agent_framework_mongodb"]
                )
            for command in consumer_commands:
                completed = _record(command, results)
                if completed.returncode:
                    failure = f"{kind} consumer failed with exit code {completed.returncode}"
                    break
    except OSError as exc:
        failure = str(exc)
    finally:
        row_python = _venv_python(environment)
        if row_python.is_file():
            freeze = _record(
                [str(row_python), "-m", "pip", "freeze"],
                results,
                capture=True,
            )
            freeze_text = freeze.stdout if freeze.returncode == 0 else f"unavailable: {failure}\n"
        else:
            freeze_text = f"unavailable: {failure or 'environment creation failed'}\n"
        (report_dir / "pip-freeze.txt").write_text(freeze_text, encoding="utf-8")
        if not (report_dir / "pytest.xml").is_file():
            _write_junit_failure(report_dir / "pytest.xml", failure or "pytest did not run")
        _write_reports(
            report_dir,
            label=label,
            version=version,
            channel=channel,
            results=results,
            succeeded=not failure,
        )
        for path in [environment, *consumers]:
            shutil.rmtree(path, ignore_errors=True)
    if failure:
        print(f"{label} ({version}) failed: {failure}", file=sys.stderr)
        return 1
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True)
    parser.add_argument("--label", required=True)
    parser.add_argument("--channel", required=True, choices=("stable", "preview"))
    parser.add_argument("--report-dir", required=True, type=Path)
    args = parser.parse_args()
    return run_row(args.version, args.label, args.channel, args.report_dir.resolve())


if __name__ == "__main__":
    raise SystemExit(main())
