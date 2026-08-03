"""Inward-only redacted logging and tracing for public operations."""

from __future__ import annotations

import asyncio
import functools
import logging
import time
from collections.abc import Callable, Collection, Coroutine
from types import CoroutineType
from typing import Any, ParamSpec, TypeVar, cast

from opentelemetry import trace
from opentelemetry.trace import Status, StatusCode

from ..errors import (
    MongoDBAuthorizationError,
    MongoDBCapabilityError,
    MongoDBConcurrencyError,
    MongoDBConfigurationError,
    MongoDBEmbeddingError,
    MongoDBFilterTranslationError,
    MongoDBIndexMismatchError,
    MongoDBIndexMissingError,
    MongoDBIndexNotReadyError,
    MongoDBMappingError,
    MongoDBPersistenceError,
    MongoDBRetrievalError,
    MongoDBTimeoutError,
)

_LOGGER = logging.getLogger("agent_framework_mongodb")
_INSTRUMENTATION_NAME = "agent_framework_mongodb"
_P = ParamSpec("_P")
_T = TypeVar("_T")


def error_category(error: BaseException) -> str:
    """Return the stable, low-cardinality category without inspecting messages."""
    if isinstance(error, asyncio.CancelledError):
        return "cancellation"
    if isinstance(error, MongoDBAuthorizationError):
        return "authorization"
    if isinstance(error, MongoDBConfigurationError):
        return "configuration"
    if isinstance(error, MongoDBEmbeddingError):
        return "embedding"
    if isinstance(error, MongoDBCapabilityError):
        return "capability"
    if isinstance(error, MongoDBIndexMissingError):
        return "index_missing"
    if isinstance(error, MongoDBIndexMismatchError):
        return "index_mismatch"
    if isinstance(error, MongoDBIndexNotReadyError):
        return "index_not_ready"
    if isinstance(error, MongoDBFilterTranslationError):
        return "filter_translation"
    if isinstance(error, MongoDBMappingError):
        return "mapping"
    if isinstance(error, MongoDBConcurrencyError):
        return "persistence"
    if isinstance(error, MongoDBTimeoutError):
        return "timeout"
    if isinstance(error, MongoDBRetrievalError):
        return "retrieval"
    if isinstance(error, MongoDBPersistenceError):
        return "persistence"
    return "programmer"


def instrument(
    feature: str,
    operation: str,
    *,
    mode: Callable[[Any], str | None] | None = None,
    result_count: Callable[[tuple[object, ...], dict[str, object], object], int] | None = None,
) -> Callable[
    [Callable[_P, Coroutine[Any, Any, _T]]],
    Callable[_P, CoroutineType[Any, Any, _T]],
]:
    """Instrument one async public seam with an approved attribute allowlist."""

    def decorate(
        function: Callable[_P, Coroutine[Any, Any, _T]],
    ) -> Callable[_P, CoroutineType[Any, Any, _T]]:
        @functools.wraps(function)
        async def observed(*args: _P.args, **kwargs: _P.kwargs) -> _T:
            started = time.monotonic()
            mode_value = mode(args[0]) if mode is not None and args else None
            tracer = trace.get_tracer(_INSTRUMENTATION_NAME)
            with tracer.start_as_current_span(
                f"{_INSTRUMENTATION_NAME}.{feature}.{operation}",
                record_exception=False,
                set_status_on_exception=False,
            ) as span:
                base: dict[str, str] = {
                    "agent_framework_mongodb.feature": feature,
                    "agent_framework_mongodb.operation": operation,
                }
                if mode_value is not None:
                    base["agent_framework_mongodb.mode"] = mode_value
                for name, value in base.items():
                    span.set_attribute(name, value)
                try:
                    result = await function(*args, **kwargs)
                except asyncio.CancelledError as error:
                    _complete(
                        span,
                        feature,
                        operation,
                        started,
                        outcome="cancelled",
                        count=0,
                        category=error_category(error),
                        mode=mode_value,
                    )
                    raise
                except Exception as error:
                    _complete(
                        span,
                        feature,
                        operation,
                        started,
                        outcome="failed",
                        count=0,
                        category=error_category(error),
                        mode=mode_value,
                    )
                    raise
                count = (
                    result_count(
                        cast(tuple[object, ...], args),
                        cast(dict[str, object], kwargs),
                        result,
                    )
                    if result_count is not None
                    else _result_count(result)
                )
                _complete(
                    span,
                    feature,
                    operation,
                    started,
                    outcome=(
                        "empty"
                        if count == 0 and operation in {"retrieve", "load", "list"}
                        else "success"
                    ),
                    count=count,
                    mode=mode_value,
                )
                return result

        return cast(Callable[_P, CoroutineType[Any, Any, _T]], observed)

    return decorate


def _result_count(result: object) -> int:
    if result is None:
        return 0
    if isinstance(result, bool):
        return int(result)
    if isinstance(result, int):
        return max(result, 0)
    if isinstance(result, Collection) and not isinstance(result, (str, bytes, bytearray)):
        return len(cast(Collection[object], result))
    for attribute in ("items", "checkpoints"):
        value = getattr(result, attribute, None)
        if isinstance(value, Collection):
            return len(cast(Collection[object], value))
    return 1


def _complete(
    span: Any,
    feature: str,
    operation: str,
    started: float,
    *,
    outcome: str,
    count: int | None = None,
    category: str | None = None,
    mode: str | None = None,
) -> None:
    duration_ms = (time.monotonic() - started) * 1000
    fields: dict[str, object] = {
        "feature": feature,
        "operation": operation,
        "outcome": outcome,
        "duration_ms": duration_ms,
    }
    attributes: dict[str, object] = {
        "agent_framework_mongodb.outcome": outcome,
        "agent_framework_mongodb.duration_ms": duration_ms,
    }
    if count is not None:
        fields["result_count"] = count
        attributes["agent_framework_mongodb.result_count"] = count
    if category is not None:
        fields["error_category"] = category
        attributes["agent_framework_mongodb.error_category"] = category
    if mode is not None:
        fields["mode"] = mode
    for name, value in attributes.items():
        span.set_attribute(name, cast(str | bool | int | float, value))
    if outcome in {"failed", "cancelled"}:
        span.set_status(Status(StatusCode.ERROR))
        _LOGGER.warning("MongoDB operation failed", extra=fields)
    else:
        span.set_status(Status(StatusCode.OK))
        _LOGGER.info("MongoDB operation completed", extra=fields)
