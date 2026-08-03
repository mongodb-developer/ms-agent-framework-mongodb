from __future__ import annotations

# pyright: reportMissingModuleSource=false, reportMissingTypeStubs=false
import re
from pathlib import Path

import yaml

_ROOT = Path(__file__).resolve().parents[3]
_WORKFLOWS = _ROOT / ".github" / "workflows"


def _workflow(name: str) -> str:
    return (_WORKFLOWS / name).read_text(encoding="utf-8")


def _trigger_block(workflow: str, trigger: str, next_trigger: str) -> str:
    return workflow.split(f"  {trigger}:", 1)[1].split(f"  {next_trigger}:", 1)[0]


def test_workflow_yaml_is_syntactically_valid() -> None:
    for path in _WORKFLOWS.glob("*.yml"):
        parsed = yaml.load(path.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)
        assert isinstance(parsed, dict), path
        assert "on" in parsed, path
        assert "jobs" in parsed, path


def test_codeql_pushes_run_only_for_trusted_branches() -> None:
    workflow = _workflow("codeql.yml")
    push = _trigger_block(workflow, "push", "schedule")

    assert '      - "main"' in push
    assert '      - "feature/python-implementation"' in push
    assert "dependabot" not in push
    assert "  pull_request:" in workflow
    assert workflow.split("jobs:", 1)[0].count("security-events: write") == 0
    assert workflow.split("jobs:", 1)[1].count("security-events: write") == 1


def test_vulnerability_scan_audits_clean_installed_environment_read_only() -> None:
    workflow = _workflow("python-vulnerability-scan.yml")

    assert "  pull_request:" in workflow
    assert "  schedule:" in workflow
    assert re.search(r'PIP_AUDIT_VERSION: "2\.10\.1"', workflow)
    assert "python -m venv .audit-venv" in workflow
    assert "pip install --quiet ." in workflow
    assert "pip uninstall --yes --quiet agent-framework-mongodb setuptools" in workflow
    assert "pip uninstall --yes --quiet pip" in workflow
    assert "pip-audit --path .audit-venv/lib/python3.10/site-packages" in workflow
    assert ".github/workflows/python-vulnerability-scan.yml" in workflow
    assert "permissions:\n  contents: read" in workflow
    assert "security-events: write" not in workflow
    assert "${{ secrets." not in workflow


def test_python_quality_verifies_release_artifacts_and_dependency_endpoints() -> None:
    workflow = _workflow("python-quality.yml")
    pull_request = _trigger_block(workflow, "pull_request", "push")
    push = _trigger_block(workflow, "push", "workflow_dispatch")

    assert ".github/workflows/release-python.yml" in pull_request
    assert ".github/workflows/release-python.yml" in push
    assert "scripts/check_api_baseline.py api-baseline.json" in workflow
    assert "scripts/verify_artifacts.py dist/*.whl dist/*.tar.gz" in workflow
    assert "scripts/smoke_public_api.py" in workflow
    assert "python -m pydoc agent_framework_mongodb" in workflow
    assert "agent-framework-core==1.13.0" in workflow
    assert "pymongo==4.13.0" in workflow
    assert "--upgrade-strategy eager" in workflow
    assert "--format cyclonedx-json" in workflow


def test_python_release_requires_owner_environment_and_oidc() -> None:
    workflow = _workflow("release-python.yml")

    assert "  workflow_dispatch:" in workflow
    assert "python-v<version>" in workflow
    assert "vars.PYPI_ENVIRONMENT != ''" in workflow
    assert "environment: ${{ vars.PYPI_ENVIRONMENT }}" in workflow
    assert "id-token: write" in workflow
    assert "validate_release_tag.py" in workflow
    assert ".release-smoke-wheel" in workflow
    assert ".release-smoke-sdist" in workflow
    assert workflow.count("scripts/smoke_public_api.py --expected-version") >= 4
    assert "verify-published:" in workflow
    published = workflow.split("  verify-published:", 1)[1]
    assert "environment: ${{ vars.PYPI_ENVIRONMENT }}" in published
    assert "pip download" in workflow
    assert "sha256sum --check" in workflow
    assert "${{ secrets." not in workflow
    assert "password:" not in workflow


def test_python_release_actions_are_pinned_to_reviewed_commits() -> None:
    workflow = _workflow("release-python.yml")
    action_lines = [
        line.strip() for line in workflow.splitlines() if line.strip().startswith("- uses:")
    ]

    assert action_lines
    for line in action_lines:
        reference = line.rsplit("@", 1)[1].split()[0]
        assert re.fullmatch(r"[0-9a-f]{40}", reference), line
    assert "# actions/checkout v4" in workflow
    assert "# actions/setup-python v5" in workflow
    assert "# pypa/gh-action-pypi-publish release/v1" in workflow
