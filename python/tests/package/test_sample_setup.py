from __future__ import annotations

import os
import subprocess
import sys
from pathlib import Path

import pytest

_SAMPLES = Path(__file__).resolve().parents[2] / "samples"


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
