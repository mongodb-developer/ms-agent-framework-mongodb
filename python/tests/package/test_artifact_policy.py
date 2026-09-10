from __future__ import annotations

import hashlib
import json
import tarfile
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

from scripts.verify_artifacts import verify_artifact, verify_supplemental


def test_wheel_policy_accepts_only_runtime_and_metadata_files(tmp_path: Path) -> None:
    wheel = tmp_path / "agent_framework_mongodb-0.1.0-py3-none-any.whl"
    with ZipFile(wheel, "w", ZIP_DEFLATED) as archive:
        archive.writestr("agent_framework_mongodb/__init__.py", "")
        archive.writestr("agent_framework_mongodb/py.typed", "")
        archive.writestr("agent_framework_mongodb-0.1.0.dist-info/METADATA", "")
        archive.writestr("agent_framework_mongodb-0.1.0.dist-info/WHEEL", "")
        archive.writestr("agent_framework_mongodb-0.1.0.dist-info/RECORD", "")
        archive.writestr("agent_framework_mongodb-0.1.0.dist-info/licenses/LICENSE", "")

    assert verify_artifact(wheel) == []


def test_sdist_policy_rejects_tests_secrets_and_local_files(tmp_path: Path) -> None:
    sdist = tmp_path / "agent_framework_mongodb-0.1.0.tar.gz"
    root = "agent_framework_mongodb-0.1.0"
    files = {
        f"{root}/LICENSE": b"",
        f"{root}/README.md": b"",
        f"{root}/pyproject.toml": b"",
        f"{root}/PKG-INFO": b"",
        f"{root}/src/agent_framework_mongodb/__init__.py": b"",
        f"{root}/src/agent_framework_mongodb/py.typed": b"",
        f"{root}/tests/test_private.py": b"",
        f"{root}/.env": b"MONGODB_URI=not-a-real-secret",
        f"{root}/local.settings.json": b"{}",
    }
    with tarfile.open(sdist, "w:gz") as archive:
        for name, content in files.items():
            info = tarfile.TarInfo(name)
            info.size = len(content)
            archive.addfile(info, fileobj=__import__("io").BytesIO(content))

    issues = verify_artifact(sdist)

    assert any("tests/test_private.py" in issue for issue in issues)
    assert any(".env" in issue for issue in issues)
    assert any("local.settings.json" in issue for issue in issues)


def test_sbom_and_checksums_are_validated_as_supplemental_files(tmp_path: Path) -> None:
    wheel = tmp_path / "agent_framework_mongodb-0.1.0-py3-none-any.whl"
    wheel.write_bytes(b"wheel")
    sbom = tmp_path / "agent-framework-mongodb.sbom.cdx.json"
    sbom.write_text(
        json.dumps(
            {
                "bomFormat": "CycloneDX",
                "specVersion": "1.6",
                "version": 1,
                "components": [],
            }
        ),
        encoding="utf-8",
    )
    checksums = tmp_path / "SHA256SUMS"
    checksums.write_text(
        f"{hashlib.sha256(wheel.read_bytes()).hexdigest()}  {wheel.name}\n"
        f"{hashlib.sha256(sbom.read_bytes()).hexdigest()}  {sbom.name}\n",
        encoding="utf-8",
    )

    assert verify_artifact(sbom) == [f"unsupported distribution artifact type: {sbom.name}"]
    assert verify_supplemental(sbom) == []
    assert verify_supplemental(checksums) == []


def test_supplemental_policy_rejects_invalid_sbom_and_checksum(tmp_path: Path) -> None:
    sbom = tmp_path / "package.sbom.cdx.json"
    sbom.write_text('{"bomFormat":"not-cyclonedx"}', encoding="utf-8")
    checksums = tmp_path / "SHA256SUMS"
    checksums.write_text(f"{'0' * 64}  missing.whl\n", encoding="utf-8")

    assert any("CycloneDX" in issue for issue in verify_supplemental(sbom))
    assert any("does not exist" in issue for issue in verify_supplemental(checksums))
