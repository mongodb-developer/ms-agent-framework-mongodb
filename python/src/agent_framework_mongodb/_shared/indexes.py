"""Shared explicit MongoDB Vector Search index lifecycle mechanics."""

from __future__ import annotations

import asyncio
import time
from collections.abc import Mapping
from dataclasses import dataclass
from typing import Any, Protocol, cast

from pymongo.errors import ConnectionFailure, OperationFailure, PyMongoError
from pymongo.operations import SearchIndexModel

from ..errors import (
    MongoDBAuthorizationError,
    MongoDBCapabilityError,
    MongoDBIndexFailedError,
    MongoDBIndexMismatchError,
    MongoDBIndexMissingError,
    MongoDBIndexNotReadyError,
    MongoDBRetrievalError,
    MongoDBTransientRetrievalError,
)


class _Cursor(Protocol):
    async def to_list(self, *, length: int | None) -> list[dict[str, Any]]: ...


class _SearchIndexCollection(Protocol):
    async def list_search_indexes(self, *, name: str) -> _Cursor: ...

    async def create_search_index(self, model: SearchIndexModel) -> str: ...

    async def update_search_index(self, name: str, definition: Mapping[str, Any]) -> None: ...


@dataclass(frozen=True, slots=True)
class VectorIndexDefinition:
    """Expected application-owned Vector Search index properties."""

    name: str
    path: str
    dimensions: int
    similarity: str
    filter_paths: tuple[str, ...] = ()

    def document(self) -> dict[str, Any]:
        return {
            "fields": [
                {
                    "type": "vector",
                    "path": self.path,
                    "numDimensions": self.dimensions,
                    "similarity": self.similarity,
                },
                *[{"type": "filter", "path": path} for path in self.filter_paths],
            ]
        }


class VectorIndexManager:
    """Inspect, validate, and explicitly provision one Vector Search index."""

    def __init__(
        self,
        collection: _SearchIndexCollection,
        expected: VectorIndexDefinition,
    ) -> None:
        self._collection = collection
        self.expected = expected

    async def inspect(self) -> Mapping[str, Any] | None:
        try:
            cursor = await self._collection.list_search_indexes(name=self.expected.name)
            documents = await cursor.to_list(length=1)
        except asyncio.CancelledError:
            raise
        except PyMongoError as exc:
            raise _translate_index_error(exc) from exc
        return documents[0] if documents else None

    async def validate(self, *, require_ready: bool = True) -> Mapping[str, Any]:
        inspected = await self.inspect()
        if inspected is None:
            raise MongoDBIndexMissingError(
                f"Vector Search index '{self.expected.name}' does not exist; create it explicitly."
            )
        self._validate_inspected(inspected, require_ready=require_ready)
        return inspected

    def _validate_inspected(
        self,
        inspected: Mapping[str, Any],
        *,
        require_ready: bool,
    ) -> None:
        status = self._raise_if_failed(inspected)
        self._validate_definition(inspected)
        if require_ready and (status != "READY" or inspected.get("queryable") is not True):
            raise MongoDBIndexNotReadyError(
                f"Vector Search index '{self.expected.name}' is not READY and queryable."
            )

    def _raise_if_failed(self, inspected: Mapping[str, Any]) -> object:
        raw_status = inspected.get("status")
        status = raw_status.upper() if isinstance(raw_status, str) else raw_status
        if status == "FAILED":
            raise MongoDBIndexFailedError(
                f"Vector Search index '{self.expected.name}' is FAILED; remediation: "
                "inspect the deployment index error, then explicitly update, drop, or recreate "
                "the index definition."
            )
        return status

    async def ensure(
        self,
        *,
        wait_until_ready: bool,
        timeout: float,
        poll_interval: float,
    ) -> Mapping[str, Any] | None:
        inspected = await self.inspect()
        definition = self.expected.document()
        try:
            if inspected is None:
                await self._collection.create_search_index(
                    SearchIndexModel(
                        definition=definition,
                        name=self.expected.name,
                        type="vectorSearch",
                    )
                )
            else:
                self._raise_if_failed(inspected)
                try:
                    self._validate_definition(inspected)
                except MongoDBIndexMismatchError:
                    await self._collection.update_search_index(self.expected.name, definition)
        except asyncio.CancelledError:
            raise
        except PyMongoError as exc:
            raise _translate_index_error(exc) from exc
        if not wait_until_ready:
            final = await self.inspect()
            if final is None:
                raise MongoDBIndexMissingError(
                    f"Vector Search index '{self.expected.name}' was not inspectable after "
                    "the ensure command was accepted; inspect it again before use."
                )
            self._validate_inspected(final, require_ready=False)
            return final
        return await self.wait_until_ready(timeout=timeout, poll_interval=poll_interval)

    async def wait_until_ready(
        self,
        *,
        timeout: float,
        poll_interval: float,
    ) -> Mapping[str, Any]:
        if timeout <= 0 or poll_interval <= 0:
            raise ValueError("timeout and poll_interval must be positive.")
        deadline = time.monotonic() + timeout
        while True:
            try:
                return await self.validate(require_ready=True)
            except (MongoDBIndexMissingError, MongoDBIndexNotReadyError) as exc:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise MongoDBIndexNotReadyError(
                        f"Vector Search index '{self.expected.name}' was not queryable "
                        f"before timeout; last state: {type(exc).__name__}."
                    ) from exc
                await asyncio.sleep(min(poll_interval, remaining))

    def _validate_definition(self, inspected: Mapping[str, Any]) -> None:
        if inspected.get("type") != "vectorSearch":
            raise MongoDBIndexMismatchError(
                f"Vector Search index '{self.expected.name}' has the wrong index type."
            )
        raw_definition = inspected.get("latestDefinition", inspected.get("definition"))
        if not isinstance(raw_definition, Mapping):
            raise MongoDBIndexMismatchError(
                f"Vector Search index '{self.expected.name}' has no inspectable definition."
            )
        definition = cast(Mapping[str, object], raw_definition)
        raw_fields = definition.get("fields")
        if not isinstance(raw_fields, list):
            raise MongoDBIndexMismatchError(
                f"Vector Search index '{self.expected.name}' has no fields definition."
            )
        fields = [
            cast(Mapping[str, object], field)
            for field in cast(list[object], raw_fields)
            if isinstance(field, Mapping)
        ]
        vector = next((field for field in fields if field.get("type") == "vector"), None)
        if vector is None or vector.get("path") != self.expected.path:
            raise MongoDBIndexMismatchError(
                f"Vector Search index '{self.expected.name}' has the wrong vector path."
            )
        if vector.get("numDimensions") != self.expected.dimensions:
            raise MongoDBIndexMismatchError(
                f"Vector Search index '{self.expected.name}' has the wrong dimensions."
            )
        if vector.get("similarity") != self.expected.similarity:
            raise MongoDBIndexMismatchError(
                f"Vector Search index '{self.expected.name}' has the wrong similarity."
            )
        actual_filters = {
            str(field.get("path")) for field in fields if field.get("type") == "filter"
        }
        missing = set(self.expected.filter_paths) - actual_filters
        if missing:
            raise MongoDBIndexMismatchError(
                f"Vector Search index '{self.expected.name}' is missing required filter paths."
            )


def _translate_index_error(error: PyMongoError) -> Exception:
    if isinstance(error, OperationFailure):
        if error.code in {13, 18}:
            return MongoDBAuthorizationError("MongoDB index authorization failed.")
        if error.code in {59, 303}:
            return MongoDBCapabilityError("MongoDB Vector Search indexes are unavailable.")
        if error.code == 27:
            return MongoDBIndexMissingError("The required MongoDB Vector Search index is missing.")
    if isinstance(error, ConnectionFailure) or (
        isinstance(error, OperationFailure)
        and error.code in {6, 7, 89, 91, 189, 262, 9001, 10107, 11600, 11602}
    ):
        return MongoDBTransientRetrievalError(
            "MongoDB Vector Search index operation failed transiently."
        )
    return MongoDBRetrievalError("MongoDB Vector Search index operation failed.")


@dataclass(frozen=True, slots=True)
class SearchIndexDefinition:
    """Expected application-owned MongoDB Search index properties."""

    name: str
    text_paths: tuple[str, ...]
    analyzer: str
    filter_fields: tuple[tuple[str, str], ...] = ()

    def document(self) -> dict[str, Any]:
        fields: dict[str, object] = {}
        for path in self.text_paths:
            _set_search_mapping(
                fields,
                path,
                {"type": "string", "analyzer": self.analyzer},
            )
        for path, field_type in self.filter_fields:
            _set_search_mapping(fields, path, {"type": field_type})
        return {"mappings": {"dynamic": True, "fields": fields}}


class SearchIndexManager:
    """Inspect, validate, and explicitly provision one MongoDB Search index."""

    def __init__(
        self,
        collection: _SearchIndexCollection,
        expected: SearchIndexDefinition,
    ) -> None:
        self._collection = collection
        self.expected = expected

    async def inspect(self) -> Mapping[str, Any] | None:
        try:
            cursor = await self._collection.list_search_indexes(name=self.expected.name)
            documents = await cursor.to_list(length=1)
        except asyncio.CancelledError:
            raise
        except PyMongoError as exc:
            raise _translate_search_index_error(exc) from exc
        return documents[0] if documents else None

    async def validate(self, *, require_ready: bool = True) -> Mapping[str, Any]:
        inspected = await self.inspect()
        if inspected is None:
            raise MongoDBIndexMissingError(
                f"MongoDB Search index '{self.expected.name}' does not exist; create it explicitly."
            )
        self._validate_inspected(inspected, require_ready=require_ready)
        return inspected

    def _validate_inspected(
        self,
        inspected: Mapping[str, Any],
        *,
        require_ready: bool,
    ) -> None:
        status = self._raise_if_failed(inspected)
        self._validate_definition(inspected)
        if require_ready and (status != "READY" or inspected.get("queryable") is not True):
            raise MongoDBIndexNotReadyError(
                f"MongoDB Search index '{self.expected.name}' is not READY and queryable."
            )

    def _raise_if_failed(self, inspected: Mapping[str, Any]) -> object:
        raw_status = inspected.get("status")
        status = raw_status.upper() if isinstance(raw_status, str) else raw_status
        if status == "FAILED":
            raise MongoDBIndexFailedError(
                f"MongoDB Search index '{self.expected.name}' is FAILED; remediation: "
                "inspect the deployment index error, then explicitly update, drop, or recreate "
                "the index definition."
            )
        return status

    async def ensure(
        self,
        *,
        wait_until_ready: bool,
        timeout: float,
        poll_interval: float,
    ) -> Mapping[str, Any] | None:
        inspected = await self.inspect()
        definition = self.expected.document()
        try:
            if inspected is None:
                await self._collection.create_search_index(
                    SearchIndexModel(definition=definition, name=self.expected.name)
                )
            else:
                self._raise_if_failed(inspected)
                try:
                    self._validate_definition(inspected)
                except MongoDBIndexMismatchError:
                    await self._collection.update_search_index(self.expected.name, definition)
        except asyncio.CancelledError:
            raise
        except PyMongoError as exc:
            raise _translate_search_index_error(exc) from exc
        if not wait_until_ready:
            final = await self.inspect()
            if final is None:
                raise MongoDBIndexMissingError(
                    f"MongoDB Search index '{self.expected.name}' was not inspectable after "
                    "the ensure command was accepted; inspect it again before use."
                )
            self._validate_inspected(final, require_ready=False)
            return final
        return await self.wait_until_ready(timeout=timeout, poll_interval=poll_interval)

    async def wait_until_ready(
        self,
        *,
        timeout: float,
        poll_interval: float,
    ) -> Mapping[str, Any]:
        if timeout <= 0 or poll_interval <= 0:
            raise ValueError("timeout and poll_interval must be positive.")
        deadline = time.monotonic() + timeout
        while True:
            try:
                return await self.validate(require_ready=True)
            except (MongoDBIndexMissingError, MongoDBIndexNotReadyError) as exc:
                remaining = deadline - time.monotonic()
                if remaining <= 0:
                    raise MongoDBIndexNotReadyError(
                        f"MongoDB Search index '{self.expected.name}' was not queryable "
                        f"before timeout; last state: {type(exc).__name__}."
                    ) from exc
                await asyncio.sleep(min(poll_interval, remaining))

    def _validate_definition(self, inspected: Mapping[str, Any]) -> None:
        if inspected.get("type", "search") != "search":
            raise MongoDBIndexMismatchError(
                f"MongoDB Search index '{self.expected.name}' has the wrong index type."
            )
        raw_definition = inspected.get("latestDefinition", inspected.get("definition"))
        if not isinstance(raw_definition, Mapping):
            raise MongoDBIndexMismatchError(
                f"MongoDB Search index '{self.expected.name}' has no inspectable definition."
            )
        mappings = cast(Mapping[str, object], raw_definition).get("mappings")
        if not isinstance(mappings, Mapping):
            raise MongoDBIndexMismatchError(
                f"MongoDB Search index '{self.expected.name}' has no mappings definition."
            )
        fields = cast(Mapping[str, object], mappings).get("fields")
        if not isinstance(fields, Mapping):
            raise MongoDBIndexMismatchError(
                f"MongoDB Search index '{self.expected.name}' has no fields definition."
            )
        typed_fields = cast(Mapping[str, object], fields)
        for path in self.expected.text_paths:
            mapping = _search_mapping_for_path(typed_fields, path)
            if mapping is None or mapping.get("type") != "string":
                raise MongoDBIndexMismatchError(
                    f"MongoDB Search index '{self.expected.name}' is missing text path '{path}'."
                )
            if mapping.get("analyzer") != self.expected.analyzer:
                raise MongoDBIndexMismatchError(
                    f"MongoDB Search index '{self.expected.name}' has the wrong analyzer "
                    f"for text path '{path}'."
                )
        for path, expected_type in self.expected.filter_fields:
            mapping = _search_mapping_for_path(typed_fields, path)
            if mapping is None or mapping.get("type") != expected_type:
                raise MongoDBIndexMismatchError(
                    f"MongoDB Search index '{self.expected.name}' is missing required "
                    f"filter path '{path}' with type '{expected_type}'."
                )


def _set_search_mapping(
    fields: dict[str, object],
    path: str,
    mapping: dict[str, str],
) -> None:
    segments = path.split(".")
    current = fields
    for segment in segments[:-1]:
        existing = current.setdefault(segment, {"type": "document", "fields": {}})
        if not isinstance(existing, Mapping):
            raise ValueError(f"Search index path '{path}' conflicts with another configured path.")
        existing_mapping = cast(Mapping[str, object], existing)
        nested = existing_mapping.get("fields")
        if not isinstance(nested, dict):
            raise ValueError(f"Search index path '{path}' conflicts with another configured path.")
        current = cast(dict[str, object], nested)
    existing_leaf = current.get(segments[-1])
    if existing_leaf is not None and existing_leaf != mapping:
        raise ValueError(f"Search index path '{path}' has conflicting configured mappings.")
    current[segments[-1]] = mapping


def _search_mapping_for_path(
    fields: Mapping[str, object],
    path: str,
) -> Mapping[str, object] | None:
    current = fields
    for index, segment in enumerate(path.split(".")):
        value = current.get(segment)
        if isinstance(value, list):
            mapped_value: Mapping[str, object] | None = None
            for item in cast(list[object], value):
                if isinstance(item, Mapping):
                    mapped_value = cast(Mapping[str, object], item)
                    break
            value = mapped_value
        if not isinstance(value, Mapping):
            return None
        mapping = cast(Mapping[str, object], value)
        if index == len(path.split(".")) - 1:
            return mapping
        nested = mapping.get("fields")
        if not isinstance(nested, Mapping):
            return None
        current = cast(Mapping[str, object], nested)
    return None


def _translate_search_index_error(error: PyMongoError) -> Exception:
    translated = _translate_index_error(error)
    if isinstance(translated, MongoDBCapabilityError):
        return MongoDBCapabilityError("MongoDB Search indexes are unavailable.")
    if isinstance(translated, MongoDBTransientRetrievalError):
        return MongoDBTransientRetrievalError("MongoDB Search index operation failed transiently.")
    if isinstance(translated, MongoDBRetrievalError):
        return MongoDBRetrievalError("MongoDB Search index operation failed.")
    return translated
