from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

import pytest

_SAMPLES = Path(__file__).resolve().parents[2] / "samples"
_EXPECTED_SAMPLE_ENVIRONMENT = {
    "MONGODB_CHECKPOINT_APPLICATION_ID",
    "MONGODB_CHECKPOINT_COLLECTION",
    "MONGODB_CHECKPOINT_SESSION_ID",
    "MONGODB_CHECKPOINT_TENANT_ID",
    "MONGODB_CHECKPOINT_TTL_SECONDS",
    "MONGODB_CHECKPOINT_WORKFLOW_NAME",
    "MONGODB_DATABASE",
    "MONGODB_EMBEDDING_FACTORY",
    "MONGODB_EMBEDDING_MODEL",
    "MONGODB_HISTORY_AGENT_ID",
    "MONGODB_HISTORY_APPLICATION_ID",
    "MONGODB_HISTORY_CLEAR",
    "MONGODB_HISTORY_COLLECTION",
    "MONGODB_HISTORY_SESSION_ID",
    "MONGODB_INGESTION_CONTENT_FIELD",
    "MONGODB_INGESTION_DELETED_FIELD",
    "MONGODB_INGESTION_METADATA_FIELD",
    "MONGODB_INGESTION_SOURCE_COLLECTION",
    "MONGODB_INGESTION_SOURCE_ID_FIELD",
    "MONGODB_INGESTION_TENANT_FIELD",
    "MONGODB_INGESTION_TITLE_FIELD",
    "MONGODB_INGESTION_URI",
    "MONGODB_INGESTION_URL_FIELD",
    "MONGODB_MEMORY_COLLECTION",
    "MONGODB_MEMORY_USER_ID",
    "MONGODB_RAG_COLLECTION",
    "MONGODB_RAG_SAMPLE_PREFIX",
    "MONGODB_RAG_SEARCH_INDEX",
    "MONGODB_RAG_TENANT",
    "MONGODB_RAG_TEXT_FIELD",
    "MONGODB_RAG_VECTOR_DIMENSIONS",
    "MONGODB_RAG_VECTOR_FIELD",
    "MONGODB_RAG_VECTOR_INDEX",
    "MONGODB_SESSION_AGENT_ID",
    "MONGODB_SESSION_APPLICATION_ID",
    "MONGODB_SESSION_COLLECTION",
    "MONGODB_SESSION_ID",
    "MONGODB_SESSION_TENANT_ID",
    "MONGODB_SESSION_TTL_SECONDS",
    "MONGODB_URI",
}


def test_sample_environment_template_is_complete_and_safe() -> None:
    template = _SAMPLES / ".env.example"
    assignments = {
        line.partition("=")[0]: line.partition("=")[2]
        for line in template.read_text(encoding="utf-8").splitlines()
        if line and not line.startswith("#")
    }

    assert set(assignments) == _EXPECTED_SAMPLE_ENVIRONMENT
    assert assignments["MONGODB_URI"] == ""
    assert assignments["MONGODB_INGESTION_URI"] == ""
    assert not any(
        "mongodb+srv://" in value or "mongodb://" in value for value in assignments.values()
    )


@pytest.mark.parametrize(
    ("name", "arguments", "expected"),
    [
        ("history_quickstart.py", [], "MONGODB_HISTORY_APPLICATION_ID"),
        ("memory_quickstart.py", [], "MONGODB_URI"),
        ("memory_and_rag.py", [], "MONGODB_URI"),
        ("document_loader.py", [], "MONGODB_URI"),
        ("on_demand_retrieval_tool.py", [], "MONGODB_RAG_SEARCH_INDEX"),
        ("rag_full_text_quickstart.py", [], "MONGODB_RAG_SEARCH_INDEX"),
        ("rag_hybrid_quickstart.py", [], "MONGODB_RAG_VECTOR_INDEX"),
        ("rag_parent_document.py", [], "MONGODB_RAG_VECTOR_INDEX"),
        ("rag_vector_quickstart.py", [], "MONGODB_RAG_VECTOR_INDEX"),
        ("session_persistence.py", [], "MONGODB_SESSION_ID"),
        ("structured_metadata_retrieval.py", [], "MONGODB_RAG_SEARCH_INDEX"),
        ("workflow_retrieval.py", [], "MONGODB_RAG_SEARCH_INDEX"),
        ("workflow_checkpoint_resume.py", [], "MONGODB_URI"),
        (
            "index_provisioning.py",
            ["--apply", "--vector-dimensions", "3"],
            "MONGODB_RAG_VECTOR_INDEX",
        ),
        ("incremental_ingestion.py", ["--apply"], "MONGODB_INGESTION_URI"),
    ],
)
def test_sample_validates_setup_before_network_access(
    name: str,
    arguments: list[str],
    expected: str,
) -> None:
    environment = {
        key: value for key, value in os.environ.items() if not key.startswith("MONGODB_")
    }
    result = subprocess.run(
        [sys.executable, str(_SAMPLES / name), *arguments],
        cwd=_SAMPLES.parent,
        env=environment,
        check=False,
        capture_output=True,
        text=True,
        timeout=10,
    )

    assert result.returncode != 0
    assert expected in result.stdout + result.stderr


@pytest.mark.parametrize("sample", sorted(_SAMPLES.glob("*.py")))
def test_sample_imports_without_credentials(sample: Path) -> None:
    environment = {
        key: value for key, value in os.environ.items() if not key.startswith("MONGODB_")
    }
    result = subprocess.run(
        [
            sys.executable,
            "-c",
            "import runpy,sys; runpy.run_path(sys.argv[1], run_name='sample_import')",
            str(sample),
        ],
        cwd=_SAMPLES.parent,
        env=environment,
        check=False,
        capture_output=True,
        text=True,
        timeout=10,
    )

    assert result.returncode == 0, result.stdout + result.stderr


def test_required_python_scenarios_are_present() -> None:
    expected = {
        "document_loader.py",
        "incremental_ingestion.py",
        "memory_and_rag.py",
        "on_demand_retrieval_tool.py",
        "rag_parent_document.py",
        "session_persistence.py",
        "structured_metadata_retrieval.py",
        "workflow_checkpoint_resume.py",
        "workflow_retrieval.py",
    }

    assert expected <= {sample.name for sample in _SAMPLES.glob("*.py")}
