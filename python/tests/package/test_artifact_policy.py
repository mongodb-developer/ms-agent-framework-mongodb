from __future__ import annotations

import tarfile
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

from scripts.verify_artifacts import verify_artifact


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
