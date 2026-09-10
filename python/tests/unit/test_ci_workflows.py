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
    assert '      - "build/python-packaging-release"' in push
    assert "dependabot" not in push
    assert "  pull_request:" in workflow
    assert "  push:" in workflow
    push = _trigger_block(workflow, "push", "schedule")
    assert "build/python-packaging-release" in push
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
    assert ".github/workflows/python-agent-framework-compatibility.yml" in workflow
    assert "scripts/check_api_baseline.py api-baseline.json" in workflow
    assert "scripts/verify_artifacts.py dist/*.whl dist/*.tar.gz" in workflow
    assert "python -m twine check dist/*.whl dist/*.tar.gz" in workflow
    assert (
        "scripts/verify_artifacts.py --supplemental dist/*.sbom.cdx.json dist/SHA256SUMS"
    ) in workflow
    assert "python -m twine check dist/*\n" not in workflow
    assert "scripts/smoke_public_api.py" in workflow
    assert "python -m pydoc agent_framework_mongodb" in workflow
    assert "agent-framework-core==1.13.0" in workflow
    assert "pymongo==4.13.0" in workflow
    assert "--upgrade-strategy eager" in workflow
    assert "--format cyclonedx-json" in workflow
    assert "version-readiness:" in workflow
    assert "refs/heads/build/python-packaging-release" in workflow
    assert "scripts/validate_version_readiness.py" in workflow
    assert "git ls-remote --tags origin" in workflow
    assert "dist/readiness/*.whl dist/readiness/*.tar.gz" in workflow


def test_every_build_branch_push_runs_python_readiness_workflows() -> None:
    quality = _workflow("python-quality.yml")
    compatibility = _workflow("python-agent-framework-compatibility.yml")
    codeql = _workflow("codeql.yml")
    vulnerability = _workflow("python-vulnerability-scan.yml")
    credentials = _workflow("credential-scan.yml")

    assert "  push:" in quality
    assert "build/python-packaging-release" in compatibility
    assert "build/python-packaging-release" in codeql
    assert "build/python-packaging-release" in vulnerability
    assert "  push:" in credentials
    assert "  push:" not in _workflow("dependency-review.yml")


def test_python_release_creates_main_reachable_tag_and_requires_explicit_publish() -> None:
    workflow = _workflow("release-python.yml")

    assert "  workflow_dispatch:" in workflow
    push = _trigger_block(workflow, "push", "workflow_dispatch")
    assert "      - main" in push
    assert '      - "python/pyproject.toml"' in push
    assert "dotnet" not in push
    assert "build/python-packaging-release" not in push
    assert "default: false" in workflow
    assert 'test "$DISPATCH_REF" = main' in workflow
    assert 'git merge-base --is-ancestor "$SHA" origin/main' in workflow
    assert "github.event_name == 'push' && github.sha || inputs.commit" in workflow
    assert 'if [ "${{ github.event_name }}" = push ]; then' in workflow
    assert 'test "$SHA" = "$GITHUB_SHA"' in workflow
    assert "--output tag" in workflow
    assert 'VERSION="${TAG#python-v}"' in workflow
    assert 'git push origin "refs/tags/$TAG"' in workflow
    assert "inputs.publish" in workflow
    assert "(github.event_name == 'push' || inputs.publish)" in workflow
    assert "vars.PYPI_PUBLISHING_APPROVED == 'true'" in workflow
    assert "vars.PYPI_ENVIRONMENT != ''" in workflow
    assert "environment: ${{ vars.PYPI_ENVIRONMENT }}" in workflow
    assert "id-token: write" in workflow
    assert "validate_release_tag.py" in workflow
    assert workflow.count("python scripts/validate_release_tag.py") >= 2
    assert workflow.index("python scripts/validate_release_tag.py") < workflow.index(
        'git push origin "refs/tags/$TAG"'
    )
    assert ".release-smoke-wheel" in workflow
    assert ".release-smoke-sdist" in workflow
    assert workflow.count("scripts/smoke_public_api.py") >= 4
    assert "verify-published:" in workflow
    published = workflow.split("  verify-published:", 1)[1]
    assert "environment: ${{ vars.PYPI_ENVIRONMENT }}" in published
    assert "pip download" in workflow
    assert "sha256sum --check" in workflow
    assert "python -m twine check dist/packages/*.whl dist/packages/*.tar.gz" in workflow
    assert "scripts/verify_artifacts.py --supplemental" in workflow
    assert "dist/*.sbom.cdx.json dist/PACKAGE_SHA256SUMS dist/SHA256SUMS" in workflow
    assert "python -m twine check dist/packages/*\n" not in workflow
    assert "${{ secrets." not in workflow
    assert "password:" not in workflow
    assert "github-release:" in workflow
    assert 'gh release create "$RELEASE_TAG"' in workflow
    assert "steps.attest.outputs.bundle-path" in workflow
    assert "uses: actions/attest@508db95dd578ae2727ebd6217d5ba78e4fbda05d" in workflow
    assert "predicate-type: https://slsa.dev/provenance/v1" in workflow
    assert "predicate-path: dist/release-provenance-predicate.json" in workflow
    assert "RELEASE_SHA: ${{ needs.tag.outputs.sha }}" in workflow
    assert '"digest": {"gitCommit": sha}' in workflow
    assert "PACKAGE_SHA256SUMS" in workflow
    assert "agent-framework-mongodb.sbom.cdx.json" in workflow
    assert "scripts/run_framework_compatibility.py" in workflow
    assert workflow.count("continue-on-error: true") >= 4
    assert workflow.count("if: always()") >= 6
    assert "pip-freeze.txt" in workflow
    assert "release-gate.json" in workflow
    assert "Enforce compatibility outcome after retaining evidence" in workflow
    assert "Enforce release outcomes after retaining evidence" in workflow


def test_all_workflow_actions_are_pinned() -> None:
    for path in _WORKFLOWS.glob("*.yml"):
        workflow = path.read_text(encoding="utf-8")
        for line in workflow.splitlines():
            if not line.strip().startswith("- uses:"):
                continue
            reference = line.rsplit("@", 1)[1].split()[0]
            assert re.fullmatch(r"[0-9a-f]{40}", reference), f"{path.name}: {line}"


def test_common_workflow_actions_are_pinned_to_their_reviewed_repositories() -> None:
    reviewed: dict[str, set[str]] = {
        "actions/checkout": {
            "11d5960a326750d5838078e36cf38b85af677262",
            "08c6903cd8c0fde910a37f88322edcfb5dd907a8",
        },
        "actions/upload-artifact": {
            "ea165f8d65b6e75b540449e92b4886f43607fa02",
            "043fb46d1a93c77aae656e7c1c64a875d1fc6a0a",
        },
        "actions/download-artifact": {
            "d3f86a106a0bac45b974a628896c90dbdf5c8093",
            "3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c",
        },
        "github/codeql-action/init": {"c4dd10e44af883a891fe31ced449bcb4a6728b9b"},
        "github/codeql-action/analyze": {"c4dd10e44af883a891fe31ced449bcb4a6728b9b"},
    }
    for path in _WORKFLOWS.glob("*.yml"):
        for line in path.read_text(encoding="utf-8").splitlines():
            stripped = line.strip()
            if not stripped.startswith("- uses:"):
                continue
            action, reference = stripped.removeprefix("- uses: ").split("@", 1)
            sha = reference.split()[0]
            if action in reviewed:
                assert sha in reviewed[action], f"{path.name}: {line}"


def test_python_release_actions_are_pinned_to_reviewed_commits() -> None:
    for name in ("release-python.yml", "python-agent-framework-compatibility.yml"):
        workflow = _workflow(name)
        action_lines = [
            line.strip() for line in workflow.splitlines() if line.strip().startswith("- uses:")
        ]

        assert action_lines
        for line in action_lines:
            reference = line.rsplit("@", 1)[1].split()[0]
            assert re.fullmatch(r"[0-9a-f]{40}", reference), line
        assert "# actions/checkout v4" in workflow
        assert "# actions/setup-python v5" in workflow


def test_framework_compatibility_workflow_is_dynamic_and_reports_every_row() -> None:
    workflow = _workflow("python-agent-framework-compatibility.yml")

    assert "pypi.org/pypi/agent-framework-core/json" not in workflow
    assert "scripts/resolve_framework_versions.py" in workflow
    assert "--include-preview" in workflow
    assert "--exact" in workflow
    assert "schedule:" in workflow
    assert "fromJSON(needs.resolve.outputs.matrix)" in workflow
    assert "scripts/run_framework_compatibility.py" in workflow
    runner = (_ROOT / "python" / "scripts" / "run_framework_compatibility.py").read_text(
        encoding="utf-8"
    )
    assert "agent-framework-core=={version}" in runner
    assert "--junitxml=" in runner
    assert "summary.json" in runner
    assert "summary.md" in runner
    assert "continue-on-error: true" in workflow
