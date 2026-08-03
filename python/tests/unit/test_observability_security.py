from __future__ import annotations

import asyncio
import logging
from collections.abc import Generator
from contextlib import contextmanager
from pathlib import Path
from typing import Any, cast

import pytest
from opentelemetry import trace
from pymongo.errors import NetworkTimeout, OperationFailure

from agent_framework_mongodb import (
    MongoDBAuthorizationError,
    MongoDBCapabilityError,
    MongoDBConfigurationError,
    MongoDBIndexMismatchError,
    MongoDBIndexMissingError,
    MongoDBIndexNotReadyError,
    MongoDBRAGProvider,
    MongoDBRAGProviderOptions,
    MongoDBRetrievalError,
    MongoDBSearchMode,
    MongoDBSessionStore,
    MongoDBSessionStoreOptions,
    MongoDBTimeoutError,
    MongoDBTransientRetrievalError,
)


class _Collection:
    def __init__(self, error: BaseException | None = None) -> None:
        self.error = error

    async def find_one(self, query: dict[str, Any]) -> None:
        del query
        if self.error is not None:
            raise self.error


class _Span:
    def __init__(self) -> None:
        self.attributes: dict[str, object] = {}

    def set_attribute(self, name: str, value: object) -> None:
        self.attributes[name] = value

    def set_status(self, status: object) -> None:
        del status


class _Tracer:
    def __init__(self) -> None:
        self.spans: list[_Span] = []

    @contextmanager
    def start_as_current_span(self, name: str, **kwargs: object) -> Generator[_Span]:
        assert name == "agent_framework_mongodb.session_store.load"
        assert kwargs == {"record_exception": False, "set_status_on_exception": False}
        span = _Span()
        self.spans.append(span)
        yield span


def _store(collection: _Collection) -> MongoDBSessionStore:
    return MongoDBSessionStore(
        cast(Any, collection),
        options=MongoDBSessionStoreOptions(tenant_id="sensitive-tenant"),
    )


def _log_field(record: logging.LogRecord, name: str) -> object:
    return cast(dict[str, object], record.__dict__)[name]


def _get_tracer(
    instrumentation_name: str,
    instrumentation_version: str | None = None,
    schema_url: str | None = None,
    attributes: dict[str, object] | None = None,
) -> _Tracer:
    del instrumentation_name, instrumentation_version, schema_url, attributes
    return _trace_capture


_trace_capture = _Tracer()


async def test_public_operations_emit_only_approved_logs_and_trace_attributes(
    caplog: pytest.LogCaptureFixture,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    tracer = _Tracer()
    global _trace_capture
    _trace_capture = tracer
    monkeypatch.setattr(trace, "get_tracer", _get_tracer)
    caplog.set_level(logging.INFO, logger="agent_framework_mongodb")

    assert await _store(_Collection()).get("sensitive-document-id") is None

    records = [record for record in caplog.records if record.name == "agent_framework_mongodb"]
    assert len(records) == 1
    record = records[0]
    assert _log_field(record, "feature") == "session_store"
    assert _log_field(record, "operation") == "load"
    assert _log_field(record, "outcome") == "empty"
    assert _log_field(record, "result_count") == 0
    assert isinstance(_log_field(record, "duration_ms"), float)
    assert "sensitive" not in caplog.text
    assert set(tracer.spans[0].attributes) == {
        "agent_framework_mongodb.feature",
        "agent_framework_mongodb.operation",
        "agent_framework_mongodb.outcome",
        "agent_framework_mongodb.result_count",
        "agent_framework_mongodb.duration_ms",
    }


async def test_failure_telemetry_is_redacted_and_authorization_propagates(
    caplog: pytest.LogCaptureFixture,
) -> None:
    caplog.set_level(logging.WARNING, logger="agent_framework_mongodb")
    error = OperationFailure(
        "mongodb://credential@private-host.invalid/sensitive-database",
        code=13,
    )

    with pytest.raises(MongoDBAuthorizationError) as raised:
        await _store(_Collection(error)).get("sensitive-document-id")

    assert raised.value.__cause__ is error
    assert len(caplog.records) == 1
    assert _log_field(caplog.records[0], "error_category") == "authorization"
    assert "private-host" not in caplog.text
    assert "credential" not in caplog.text


@pytest.mark.parametrize(
    ("driver_error", "expected", "category"),
    [
        (OperationFailure("secret", code=18), MongoDBAuthorizationError, "authorization"),
        (OperationFailure("secret", code=27), MongoDBIndexMissingError, "index_missing"),
        (OperationFailure("secret", code=85), MongoDBIndexMismatchError, "index_mismatch"),
        (
            OperationFailure("secret", details={"codeName": "SearchIndexNotReady"}),
            MongoDBIndexNotReadyError,
            "index_not_ready",
        ),
        (OperationFailure("secret", code=59), MongoDBCapabilityError, "capability"),
        (OperationFailure("secret", code=2), MongoDBConfigurationError, "configuration"),
        (OperationFailure("secret", code=91), MongoDBTransientRetrievalError, "retrieval"),
        (NetworkTimeout("secret"), MongoDBTimeoutError, "timeout"),
        (OperationFailure("secret", code=8), MongoDBRetrievalError, "retrieval"),
    ],
)
async def test_public_driver_errors_use_stable_integration_categories(
    driver_error: OperationFailure | NetworkTimeout,
    expected: type[Exception],
    category: str,
    caplog: pytest.LogCaptureFixture,
) -> None:
    with pytest.raises(expected) as raised:
        await _store(_Collection(driver_error)).get("session")

    assert raised.value.__cause__ is driver_error
    assert _log_field(caplog.records[-1], "error_category") == category
    assert _log_field(caplog.records[-1], "result_count") == 0


async def test_cancellation_is_logged_without_suppression(
    caplog: pytest.LogCaptureFixture,
) -> None:
    caplog.set_level(logging.INFO, logger="agent_framework_mongodb")

    with pytest.raises(asyncio.CancelledError):
        await _store(_Collection(asyncio.CancelledError())).get("session")

    assert len(caplog.records) == 1
    assert _log_field(caplog.records[0], "outcome") == "cancelled"
    assert _log_field(caplog.records[0], "error_category") == "cancellation"


class _Cursor:
    def __init__(self, documents: list[dict[str, Any]]) -> None:
        self.documents = documents

    async def to_list(self, *, length: int | None) -> list[dict[str, Any]]:
        return self.documents if length is None else self.documents[:length]


class _Database:
    async def command(self, command: object) -> dict[str, object]:
        if command == "buildInfo":
            return {"version": "8.0.0"}
        if command == "hello":
            return {}
        return {}


class _ReadOnlyCollection:
    name = "knowledge"

    def __init__(self) -> None:
        self.database = _Database()
        self.write_calls: list[str] = []

    async def list_search_indexes(self, *, name: str | None = None) -> _Cursor:
        definitions = {
            "vector": {
                "name": "vector",
                "type": "vectorSearch",
                "status": "READY",
                "queryable": True,
                "latestDefinition": {
                    "fields": [
                        {
                            "type": "vector",
                            "path": "embedding",
                            "numDimensions": 3,
                            "similarity": "cosine",
                        }
                    ]
                },
            },
            "search": {
                "name": "search",
                "type": "search",
                "status": "READY",
                "queryable": True,
                "latestDefinition": {
                    "mappings": {
                        "dynamic": True,
                        "fields": {
                            "content": {
                                "type": "string",
                                "analyzer": "lucene.standard",
                                "searchAnalyzer": "lucene.standard",
                            }
                        },
                    }
                },
            },
        }
        return _Cursor([definitions[name]] if name is not None else list(definitions.values()))

    async def aggregate(self, pipeline: list[dict[str, Any]]) -> _Cursor:
        assert pipeline
        return _Cursor([])

    def __getattr__(self, name: str) -> Any:
        if name.startswith(("insert", "update", "replace", "delete", "find_one_and_update")):
            self.write_calls.append(name)
            raise AssertionError(f"RAG runtime attempted write operation {name}")
        raise AttributeError(name)


class _Embedding:
    async def get_embeddings(self, values: list[str]) -> list[Any]:
        return [type("_Vector", (), {"vector": [1.0, 0.0, 0.0]})() for _ in values]


@pytest.mark.parametrize(
    "mode",
    [
        MongoDBSearchMode.VECTOR_ANN,
        MongoDBSearchMode.VECTOR_ENN,
        MongoDBSearchMode.FULL_TEXT,
        MongoDBSearchMode.HYBRID_RRF,
    ],
)
async def test_all_rag_runtime_modes_are_read_only(mode: MongoDBSearchMode) -> None:
    collection = _ReadOnlyCollection()
    vector_mode = mode is not MongoDBSearchMode.FULL_TEXT
    search_mode = mode in {MongoDBSearchMode.FULL_TEXT, MongoDBSearchMode.HYBRID_RRF}
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=mode,
            vector_dimensions=3 if vector_mode else None,
            vector_index_name="vector" if vector_mode else None,
            search_index_name="search" if search_mode else None,
        ),
        embedding_generator=_Embedding() if vector_mode else None,  # type: ignore[arg-type]
        collection=cast(Any, collection),
    )

    assert await provider.search("sensitive query") == []
    assert collection.write_calls == []


def test_dependency_constraints_and_samples_are_secret_free() -> None:
    python_root = Path(__file__).parents[2]
    configuration = (python_root / "pyproject.toml").read_text(encoding="utf-8")
    assert '"agent-framework-core>=1.13,<2"' in configuration
    assert '"opentelemetry-api>=1.39,<2"' in configuration
    assert '"pymongo>=4.13,<5"' in configuration

    forbidden = ("mongodb+srv://", "mongodb://", "api_key=", "password=")
    for sample in (python_root / "samples").glob("*.py"):
        source = sample.read_text(encoding="utf-8").lower()
        assert not any(pattern in source for pattern in forbidden), sample
