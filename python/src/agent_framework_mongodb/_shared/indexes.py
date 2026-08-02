"""Shared explicit MongoDB Vector Search index lifecycle mechanics."""

from __future__ import annotations

import asyncio
import time
from collections.abc import Awaitable, Mapping
from contextlib import suppress
from dataclasses import dataclass
from typing import Any, Protocol, TypeVar, cast

from pymongo.errors import ConnectionFailure, OperationFailure, PyMongoError
from pymongo.operations import SearchIndexModel

from ..errors import (
    MongoDBAuthorizationError,
    MongoDBCapabilityError,
    MongoDBConfigurationError,
    MongoDBIndexFailedError,
    MongoDBIndexMismatchError,
    MongoDBIndexMissingError,
    MongoDBIndexNotReadyError,
    MongoDBRetrievalError,
    MongoDBTimeoutError,
    MongoDBTransientRetrievalError,
)
from ..indexing import (
    MongoDBIndexResult,
    MongoDBIndexState,
    MongoDBRegularIndexDefinition,
    MongoDBSearchIndexDefinition,
    MongoDBVectorIndexDefinition,
)


class _Cursor(Protocol):
    async def to_list(self, *, length: int | None) -> list[dict[str, Any]]: ...


class _SearchIndexCollection(Protocol):
    async def list_search_indexes(self, *, name: str | None = None) -> _Cursor: ...

    async def create_search_index(self, model: SearchIndexModel) -> str: ...

    async def update_search_index(self, name: str, definition: Mapping[str, Any]) -> None: ...

    async def drop_search_index(self, name: str) -> None: ...


class _RegularIndexCollection(Protocol):
    async def list_indexes(self) -> _Cursor: ...

    async def create_index(self, keys: list[tuple[str, int]], **kwargs: Any) -> str: ...

    async def drop_index(self, name: str) -> None: ...


_T = TypeVar("_T")


class _IndexPollingTimeout(Exception):
    pass


async def _await_before_deadline(awaitable: Awaitable[_T], deadline: float) -> _T:
    """Await one polling request without asyncio.wait_for cancellation races."""
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise _IndexPollingTimeout
    task = asyncio.ensure_future(awaitable)
    try:
        done, _ = await asyncio.wait({task}, timeout=remaining)
    except asyncio.CancelledError:
        task.cancel()
        with suppress(asyncio.CancelledError, asyncio.TimeoutError):
            await task
        raise
    if task not in done:
        task.cancel()
        with suppress(asyncio.CancelledError, asyncio.TimeoutError):
            await task
        raise _IndexPollingTimeout
    try:
        return task.result()
    except asyncio.TimeoutError as exc:
        raise _IndexPollingTimeout from exc
    except TimeoutError as exc:
        raise _IndexPollingTimeout from exc


def _polling_timeout(
    *, label: str, name: str, previous_state: MongoDBIndexState
) -> MongoDBIndexNotReadyError:
    return MongoDBIndexNotReadyError(
        f"{label} index '{name}' was not queryable before timeout; last state: TIMEOUT "
        f"(previous: {previous_state.name}); remediation: inspect the definition and "
        "explicitly update or recreate it."
    )


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

    @property
    def definition(self) -> MongoDBVectorIndexDefinition:
        """Return the immutable public expected definition."""
        return MongoDBVectorIndexDefinition(
            name=self.expected.name,
            path=self.expected.path,
            dimensions=self.expected.dimensions,
            similarity=self.expected.similarity,
            filter_paths=self.expected.filter_paths,
        )

    async def list(self) -> tuple[MongoDBIndexResult, ...]:
        """List Vector Search indexes without mutation."""
        try:
            cursor = await self._collection.list_search_indexes()
            documents = await cursor.to_list(length=None)
        except asyncio.CancelledError:
            raise
        except PyMongoError as exc:
            raise _translate_index_error(exc) from exc
        return tuple(
            _vector_result(document, self.definition)
            for document in documents
            if document.get("type") == "vectorSearch"
        )

    async def inspect_result(self) -> MongoDBIndexResult:
        """Inspect the configured index, representing absence as MISSING."""
        inspected = await self.inspect()
        return _vector_result(inspected, self.definition)

    async def validate_result(self, *, require_ready: bool = True) -> MongoDBIndexResult:
        """Validate and return the redacted immutable state."""
        inspected = await self.validate(require_ready=require_ready)
        return _vector_result(inspected, self.definition)

    async def create(self) -> MongoDBIndexResult:
        """Explicitly submit index creation without reporting command acceptance as ready."""
        try:
            await self._collection.create_search_index(
                SearchIndexModel(
                    definition=self.expected.document(),
                    name=self.expected.name,
                    type="vectorSearch",
                )
            )
        except asyncio.CancelledError:
            raise
        except PyMongoError as exc:
            raise _translate_index_error(exc) from exc
        return MongoDBIndexResult(self.definition, MongoDBIndexState.BUILDING, "ACCEPTED", False)

    async def update(self) -> MongoDBIndexResult:
        """Explicitly submit an update to the expected definition."""
        try:
            await self._collection.update_search_index(self.expected.name, self.expected.document())
        except asyncio.CancelledError:
            raise
        except PyMongoError as exc:
            raise _translate_index_error(exc) from exc
        return MongoDBIndexResult(self.definition, MongoDBIndexState.BUILDING, "ACCEPTED", False)

    async def drop(self) -> None:
        """Explicitly drop the configured index."""
        try:
            await self._collection.drop_search_index(self.expected.name)
        except asyncio.CancelledError:
            raise
        except PyMongoError as exc:
            raise _translate_index_error(exc) from exc

    async def inspect(self) -> Mapping[str, Any] | None:
        try:
            cursor = await self._collection.list_search_indexes(name=self.expected.name)
            documents = await cursor.to_list(length=1)
        except asyncio.CancelledError:
            raise
        except asyncio.TimeoutError as exc:
            raise MongoDBTimeoutError(
                f"Vector Search index '{self.expected.name}' inspection timed out."
            ) from exc
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
        mutated = False
        try:
            if inspected is None:
                await self._collection.create_search_index(
                    SearchIndexModel(
                        definition=definition,
                        name=self.expected.name,
                        type="vectorSearch",
                    )
                )
                mutated = True
            else:
                self._raise_if_failed(inspected)
                try:
                    self._validate_definition(inspected)
                except MongoDBIndexMismatchError:
                    await self._collection.update_search_index(self.expected.name, definition)
                    mutated = True
        except asyncio.CancelledError:
            raise
        except PyMongoError as exc:
            raise _translate_index_error(exc) from exc
        if mutated and not wait_until_ready:
            return _accepted_document(
                name=self.expected.name,
                index_type="vectorSearch",
                definition=definition,
            )
        if not wait_until_ready:
            assert inspected is not None
            self._validate_inspected(inspected, require_ready=False)
            return inspected
        return await self.wait_until_ready(timeout=timeout, poll_interval=poll_interval)

    async def ensure_result(
        self,
        *,
        wait_until_ready: bool,
        timeout: float,
        poll_interval: float,
    ) -> MongoDBIndexResult:
        inspected = await self.ensure(
            wait_until_ready=wait_until_ready,
            timeout=timeout,
            poll_interval=poll_interval,
        )
        return _vector_result(inspected, self.definition)

    async def wait_until_ready(
        self,
        *,
        timeout: float,
        poll_interval: float,
    ) -> Mapping[str, Any]:
        if timeout <= 0 or poll_interval <= 0:
            raise MongoDBConfigurationError("timeout and poll_interval must be positive.")
        deadline = time.monotonic() + timeout
        last_state = MongoDBIndexState.MISSING
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise _polling_timeout(
                    label="Vector Search",
                    name=self.expected.name,
                    previous_state=last_state,
                )
            try:
                inspected = await _await_before_deadline(self.inspect(), deadline)
            except (_IndexPollingTimeout, MongoDBTimeoutError) as exc:
                raise _polling_timeout(
                    label="Vector Search",
                    name=self.expected.name,
                    previous_state=last_state,
                ) from exc
            if inspected is None:
                last_state = MongoDBIndexState.MISSING
            else:
                last_state = _state(inspected)[0]
                try:
                    self._validate_inspected(inspected, require_ready=True)
                    return inspected
                except (MongoDBIndexMismatchError, MongoDBIndexNotReadyError):
                    pass
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise _polling_timeout(
                    label="Vector Search",
                    name=self.expected.name,
                    previous_state=last_state,
                )
            await asyncio.sleep(min(poll_interval, remaining))

    async def wait_result(self, *, timeout: float, poll_interval: float) -> MongoDBIndexResult:
        inspected = await self.wait_until_ready(timeout=timeout, poll_interval=poll_interval)
        return _vector_result(inspected, self.definition)

    def _validate_definition(self, inspected: Mapping[str, Any]) -> None:
        if inspected.get("name") != self.expected.name:
            raise MongoDBIndexMismatchError(
                f"Vector Search index '{self.expected.name}' has the wrong index name."
            )
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
                f"Vector Search index '{self.expected.name}' is missing required filter paths: "
                f"{', '.join(sorted(missing))}."
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
                {
                    "type": "string",
                    "analyzer": self.analyzer,
                    "searchAnalyzer": self.analyzer,
                },
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

    @property
    def definition(self) -> MongoDBSearchIndexDefinition:
        """Return the immutable public expected definition."""
        return MongoDBSearchIndexDefinition(
            name=self.expected.name,
            text_paths=self.expected.text_paths,
            analyzer=self.expected.analyzer,
            filter_fields=self.expected.filter_fields,
            search_analyzer=self.expected.analyzer,
        )

    async def list(self) -> tuple[MongoDBIndexResult, ...]:
        """List MongoDB Search indexes without mutation."""
        try:
            cursor = await self._collection.list_search_indexes()
            documents = await cursor.to_list(length=None)
        except asyncio.CancelledError:
            raise
        except PyMongoError as exc:
            raise _translate_search_index_error(exc) from exc
        return tuple(
            _search_result(document, self.definition)
            for document in documents
            if document.get("type", "search") == "search"
        )

    async def inspect_result(self) -> MongoDBIndexResult:
        inspected = await self.inspect()
        return _search_result(inspected, self.definition)

    async def validate_result(self, *, require_ready: bool = True) -> MongoDBIndexResult:
        inspected = await self.validate(require_ready=require_ready)
        return _search_result(inspected, self.definition)

    async def create(self) -> MongoDBIndexResult:
        try:
            await self._collection.create_search_index(
                SearchIndexModel(definition=self.expected.document(), name=self.expected.name)
            )
        except asyncio.CancelledError:
            raise
        except PyMongoError as exc:
            raise _translate_search_index_error(exc) from exc
        return MongoDBIndexResult(self.definition, MongoDBIndexState.BUILDING, "ACCEPTED", False)

    async def update(self) -> MongoDBIndexResult:
        try:
            await self._collection.update_search_index(self.expected.name, self.expected.document())
        except asyncio.CancelledError:
            raise
        except PyMongoError as exc:
            raise _translate_search_index_error(exc) from exc
        return MongoDBIndexResult(self.definition, MongoDBIndexState.BUILDING, "ACCEPTED", False)

    async def drop(self) -> None:
        try:
            await self._collection.drop_search_index(self.expected.name)
        except asyncio.CancelledError:
            raise
        except PyMongoError as exc:
            raise _translate_search_index_error(exc) from exc

    async def inspect(self) -> Mapping[str, Any] | None:
        try:
            cursor = await self._collection.list_search_indexes(name=self.expected.name)
            documents = await cursor.to_list(length=1)
        except asyncio.CancelledError:
            raise
        except asyncio.TimeoutError as exc:
            raise MongoDBTimeoutError(
                f"MongoDB Search index '{self.expected.name}' inspection timed out."
            ) from exc
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
        mutated = False
        try:
            if inspected is None:
                await self._collection.create_search_index(
                    SearchIndexModel(definition=definition, name=self.expected.name)
                )
                mutated = True
            else:
                self._raise_if_failed(inspected)
                try:
                    self._validate_definition(inspected)
                except MongoDBIndexMismatchError:
                    await self._collection.update_search_index(self.expected.name, definition)
                    mutated = True
        except asyncio.CancelledError:
            raise
        except PyMongoError as exc:
            raise _translate_search_index_error(exc) from exc
        if mutated and not wait_until_ready:
            return _accepted_document(
                name=self.expected.name,
                index_type="search",
                definition=definition,
            )
        if not wait_until_ready:
            assert inspected is not None
            self._validate_inspected(inspected, require_ready=False)
            return inspected
        return await self.wait_until_ready(timeout=timeout, poll_interval=poll_interval)

    async def ensure_result(
        self,
        *,
        wait_until_ready: bool,
        timeout: float,
        poll_interval: float,
    ) -> MongoDBIndexResult:
        inspected = await self.ensure(
            wait_until_ready=wait_until_ready,
            timeout=timeout,
            poll_interval=poll_interval,
        )
        return _search_result(inspected, self.definition)

    async def wait_until_ready(
        self,
        *,
        timeout: float,
        poll_interval: float,
    ) -> Mapping[str, Any]:
        if timeout <= 0 or poll_interval <= 0:
            raise MongoDBConfigurationError("timeout and poll_interval must be positive.")
        deadline = time.monotonic() + timeout
        last_state = MongoDBIndexState.MISSING
        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise _polling_timeout(
                    label="MongoDB Search",
                    name=self.expected.name,
                    previous_state=last_state,
                )
            try:
                inspected = await _await_before_deadline(self.inspect(), deadline)
            except (_IndexPollingTimeout, MongoDBTimeoutError) as exc:
                raise _polling_timeout(
                    label="MongoDB Search",
                    name=self.expected.name,
                    previous_state=last_state,
                ) from exc
            if inspected is None:
                last_state = MongoDBIndexState.MISSING
            else:
                last_state = _state(inspected)[0]
                try:
                    self._validate_inspected(inspected, require_ready=True)
                    return inspected
                except (MongoDBIndexMismatchError, MongoDBIndexNotReadyError):
                    pass
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise _polling_timeout(
                    label="MongoDB Search",
                    name=self.expected.name,
                    previous_state=last_state,
                )
            await asyncio.sleep(min(poll_interval, remaining))

    async def wait_result(self, *, timeout: float, poll_interval: float) -> MongoDBIndexResult:
        inspected = await self.wait_until_ready(timeout=timeout, poll_interval=poll_interval)
        return _search_result(inspected, self.definition)

    def _validate_definition(self, inspected: Mapping[str, Any]) -> None:
        if inspected.get("name") != self.expected.name:
            raise MongoDBIndexMismatchError(
                f"MongoDB Search index '{self.expected.name}' has the wrong index name."
            )
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
        typed_mappings = cast(Mapping[str, object], mappings)
        if typed_mappings.get("dynamic", True) is not True:
            raise MongoDBIndexMismatchError(
                f"MongoDB Search index '{self.expected.name}' has the wrong dynamic mapping mode."
            )
        fields = typed_mappings.get("fields")
        if not isinstance(fields, Mapping):
            raise MongoDBIndexMismatchError(
                f"MongoDB Search index '{self.expected.name}' has no fields definition."
            )
        typed_fields = cast(Mapping[str, object], fields)
        for path in self.expected.text_paths:
            mappings_for_path = _search_mappings_for_path(typed_fields, path)
            string_mappings = tuple(
                mapping for mapping in mappings_for_path if mapping.get("type") == "string"
            )
            if not string_mappings:
                raise MongoDBIndexMismatchError(
                    f"MongoDB Search index '{self.expected.name}' is missing text path '{path}'."
                )
            matching_analyzer = tuple(
                mapping
                for mapping in string_mappings
                if mapping.get("analyzer") == self.expected.analyzer
            )
            if not matching_analyzer:
                raise MongoDBIndexMismatchError(
                    f"MongoDB Search index '{self.expected.name}' has the wrong analyzer "
                    f"for text path '{path}'."
                )
            if not any(
                mapping.get("searchAnalyzer", mapping.get("analyzer")) == self.expected.analyzer
                for mapping in matching_analyzer
            ):
                raise MongoDBIndexMismatchError(
                    f"MongoDB Search index '{self.expected.name}' has the wrong search analyzer "
                    f"for text path '{path}'."
                )
        for path, expected_type in self.expected.filter_fields:
            mappings_for_path = _search_mappings_for_path(typed_fields, path)
            if not any(mapping.get("type") == expected_type for mapping in mappings_for_path):
                raise MongoDBIndexMismatchError(
                    f"MongoDB Search index '{self.expected.name}' is missing required "
                    f"filter path '{path}' with type '{expected_type}'."
                )


def _set_search_mapping(
    fields: dict[str, object],
    path: str,
    mapping: dict[str, object],
) -> None:
    segments = path.split(".")
    current = fields
    for segment in segments[:-1]:
        existing = current.get(segment)
        document_mapping: dict[str, object] | None
        if existing is None:
            document_mapping = {"type": "document", "fields": {}}
            current[segment] = document_mapping
        else:
            mappings = _construction_mappings(existing, path)
            document_mapping = next(
                (candidate for candidate in mappings if candidate.get("type") == "document"),
                None,
            )
            if document_mapping is None:
                document_mapping = {"type": "document", "fields": {}}
                mappings.append(document_mapping)
                current[segment] = _canonical_mapping_value(mappings)
        nested = document_mapping.get("fields")
        if not isinstance(nested, dict):
            raise MongoDBConfigurationError(
                f"Search index path '{path}' conflicts with a non-document mapping."
            )
        current = cast(dict[str, object], nested)
    existing_leaf = current.get(segments[-1])
    if existing_leaf is None:
        current[segments[-1]] = mapping
    else:
        mappings = _construction_mappings(existing_leaf, path)
        if mapping not in mappings:
            mappings.append(mapping)
        current[segments[-1]] = _canonical_mapping_value(mappings)


def _construction_mappings(value: object, path: str) -> list[dict[str, object]]:
    raw_mappings: list[object] = cast(list[object], value) if isinstance(value, list) else [value]
    if not all(isinstance(item, dict) for item in raw_mappings):
        raise MongoDBConfigurationError(
            f"Search index path '{path}' has an invalid scalar/document mapping conflict."
        )
    return [cast(dict[str, object], item) for item in raw_mappings]


def _canonical_mapping_value(mappings: list[dict[str, object]]) -> object:
    order = {
        "string": 0,
        "token": 1,
        "boolean": 2,
        "date": 3,
        "number": 4,
        "document": 5,
        "embeddedDocuments": 6,
    }
    mappings.sort(key=lambda item: order.get(str(item.get("type")), len(order)))
    return mappings[0] if len(mappings) == 1 else mappings


def _search_mappings_for_path(
    fields: Mapping[str, object],
    path: str,
) -> tuple[Mapping[str, object], ...]:
    current = fields
    segments = path.split(".")
    for index, segment in enumerate(segments):
        value = current.get(segment)
        mappings = _search_mapping_sequence(value)
        if index == len(segments) - 1:
            return mappings
        parent_mapping = next(
            (mapping for mapping in mappings if isinstance(mapping.get("fields"), Mapping)),
            None,
        )
        if parent_mapping is None:
            return ()
        nested = parent_mapping.get("fields")
        if not isinstance(nested, Mapping):
            return ()
        current = cast(Mapping[str, object], nested)
    return ()


def _search_mapping_sequence(value: object) -> tuple[Mapping[str, object], ...]:
    if isinstance(value, Mapping):
        return (cast(Mapping[str, object], value),)
    if isinstance(value, list):
        return tuple(
            cast(Mapping[str, object], item)
            for item in cast(list[object], value)
            if isinstance(item, Mapping)
        )
    return ()


def _translate_search_index_error(error: PyMongoError) -> Exception:
    translated = _translate_index_error(error)
    if isinstance(translated, MongoDBCapabilityError):
        return MongoDBCapabilityError("MongoDB Search indexes are unavailable.")
    if isinstance(translated, MongoDBTransientRetrievalError):
        return MongoDBTransientRetrievalError("MongoDB Search index operation failed transiently.")
    if isinstance(translated, MongoDBRetrievalError):
        return MongoDBRetrievalError("MongoDB Search index operation failed.")
    return translated


def _state(document: Mapping[str, Any] | None) -> tuple[MongoDBIndexState, str | None, bool]:
    if document is None:
        return MongoDBIndexState.MISSING, None, False
    raw_status = document.get("status")
    status = str(raw_status).upper() if raw_status is not None else None
    queryable = document.get("queryable") is True
    if status == "FAILED":
        state = MongoDBIndexState.FAILED
    elif status == "READY" and queryable:
        state = MongoDBIndexState.READY
    elif status == "READY":
        state = MongoDBIndexState.READY_NOT_QUERYABLE
    else:
        state = MongoDBIndexState.BUILDING
    return state, status, queryable


def _accepted_document(
    *, name: str, index_type: str, definition: Mapping[str, Any]
) -> Mapping[str, Any]:
    return {
        "name": name,
        "type": index_type,
        "status": "ACCEPTED",
        "queryable": False,
        "latestDefinition": definition,
    }


def _vector_result(
    document: Mapping[str, Any] | None,
    definition: MongoDBVectorIndexDefinition,
) -> MongoDBIndexResult:
    state, status, queryable = _state(document)
    observed = _observed_vector_definition(document, definition)
    return MongoDBIndexResult(observed, state, status, queryable)


def _search_result(
    document: Mapping[str, Any] | None,
    definition: MongoDBSearchIndexDefinition,
) -> MongoDBIndexResult:
    state, status, queryable = _state(document)
    observed = _observed_search_definition(document, definition)
    return MongoDBIndexResult(observed, state, status, queryable)


def _observed_vector_definition(
    document: Mapping[str, Any] | None,
    fallback: MongoDBVectorIndexDefinition,
) -> MongoDBVectorIndexDefinition:
    if document is None:
        return fallback
    raw_definition = document.get("latestDefinition", document.get("definition"))
    fields_value = (
        cast(Mapping[str, object], raw_definition).get("fields")
        if isinstance(raw_definition, Mapping)
        else None
    )
    fields = (
        tuple(
            cast(Mapping[str, object], item)
            for item in cast(list[object], fields_value)
            if isinstance(item, Mapping)
        )
        if isinstance(fields_value, list)
        else ()
    )
    empty_vector: Mapping[str, object] = {}
    vector = next(
        (item for item in fields if item.get("type") == "vector"),
        empty_vector,
    )
    dimensions = vector.get("numDimensions")
    return MongoDBVectorIndexDefinition(
        name=str(document.get("name", fallback.name)),
        path=str(vector.get("path", "")),
        dimensions=dimensions if isinstance(dimensions, int) else 0,
        similarity=str(vector.get("similarity", "")),
        filter_paths=tuple(
            sorted(str(item.get("path")) for item in fields if item.get("type") == "filter")
        ),
    )


def _observed_search_definition(
    document: Mapping[str, Any] | None,
    fallback: MongoDBSearchIndexDefinition,
) -> MongoDBSearchIndexDefinition:
    if document is None:
        return fallback
    raw_definition = document.get("latestDefinition", document.get("definition"))
    raw_mappings = (
        cast(Mapping[str, object], raw_definition).get("mappings")
        if isinstance(raw_definition, Mapping)
        else None
    )
    mappings: Mapping[str, object] = (
        cast(Mapping[str, object], raw_mappings) if isinstance(raw_mappings, Mapping) else {}
    )
    raw_fields = mappings.get("fields")
    fields: Mapping[str, object] = (
        cast(Mapping[str, object], raw_fields) if isinstance(raw_fields, Mapping) else {}
    )
    leaves = tuple(_search_leaf_mappings(fields))
    strings = tuple((path, item) for path, item in leaves if item.get("type") == "string")
    analyzer = next(
        (value for _, item in strings if isinstance((value := item.get("analyzer")), str)),
        "",
    )
    search_analyzer_value = next(
        (value for _, item in strings if isinstance((value := item.get("searchAnalyzer")), str)),
        None,
    )
    search_analyzer = search_analyzer_value if isinstance(search_analyzer_value, str) else None
    return MongoDBSearchIndexDefinition(
        name=str(document.get("name", fallback.name)),
        text_paths=tuple(sorted(path for path, _ in strings)),
        analyzer=analyzer,
        filter_fields=tuple(
            sorted(
                (path, str(item.get("type")))
                for path, item in leaves
                if item.get("type") not in {"string", "document"}
            )
        ),
        search_analyzer=search_analyzer,
        dynamic=mappings.get("dynamic", True) is True,
    )


def _search_leaf_mappings(
    fields: Mapping[str, object], prefix: str = ""
) -> tuple[tuple[str, Mapping[str, object]], ...]:
    leaves: list[tuple[str, Mapping[str, object]]] = []
    for name, value in fields.items():
        path = f"{prefix}.{name}" if prefix else name
        for mapping in _search_mapping_sequence(value):
            nested = mapping.get("fields")
            if isinstance(nested, Mapping):
                leaves.extend(_search_leaf_mappings(cast(Mapping[str, object], nested), path))
            else:
                leaves.append((path, mapping))
    return tuple(leaves)


class RegularIndexManager:
    """Inspect and explicitly provision regular MongoDB indexes."""

    def __init__(
        self,
        collection: _RegularIndexCollection,
        expected: tuple[MongoDBRegularIndexDefinition, ...],
    ) -> None:
        self._collection = collection
        self.expected = expected

    async def list(self) -> tuple[MongoDBIndexResult, ...]:
        try:
            cursor = await self._collection.list_indexes()
            documents = await cursor.to_list(length=None)
        except asyncio.CancelledError:
            raise
        except PyMongoError as exc:
            raise _translate_index_error(exc) from exc
        return tuple(self._result(document) for document in documents)

    async def inspect(self, name: str) -> MongoDBIndexResult:
        listed = await self.list()
        result = next((item for item in listed if item.definition.name == name), None)
        if result is not None:
            return result
        expected = self._expected(name)
        return MongoDBIndexResult(expected, MongoDBIndexState.MISSING, None, False)

    async def validate(self) -> tuple[MongoDBIndexResult, ...]:
        listed = await self.list()
        by_name = {item.definition.name: item for item in listed}
        validated: list[MongoDBIndexResult] = []
        for expected in self.expected:
            actual = by_name.get(expected.name)
            if actual is None:
                raise MongoDBIndexMissingError(
                    f"Regular index '{expected.name}' does not exist; create it explicitly."
                )
            if not self._equivalent(actual.definition, expected):
                raise MongoDBIndexMismatchError(
                    f"Regular index '{expected.name}' does not match the expected definition; "
                    "remediation: explicitly update or recreate it."
                )
            validated.append(actual)
        return tuple(validated)

    async def create(self) -> tuple[MongoDBIndexResult, ...]:
        results: list[MongoDBIndexResult] = []
        for definition in self.expected:
            results.append(await self.create_named(definition.name))
        return tuple(results)

    async def create_named(self, name: str) -> MongoDBIndexResult:
        definition = self._expected(name)
        kwargs: dict[str, Any] = {"name": definition.name}
        if definition.expire_after_seconds is not None:
            kwargs["expireAfterSeconds"] = definition.expire_after_seconds
        if definition.collation is not None:
            kwargs["collation"] = dict(definition.collation)
        try:
            await self._collection.create_index(list(definition.keys), **kwargs)
        except asyncio.CancelledError:
            raise
        except PyMongoError as exc:
            raise _translate_index_error(exc) from exc
        return await self.inspect(name)

    async def ensure(self) -> tuple[MongoDBIndexResult, ...]:
        listed = await self.list()
        by_name = {item.definition.name: item for item in listed}
        for expected in self.expected:
            actual = by_name.get(expected.name)
            if actual is None:
                await self.create_named(expected.name)
            elif not self._equivalent(actual.definition, expected):
                await self.update(expected.name)
        return await self.validate()

    async def update(self, name: str) -> MongoDBIndexResult:
        await self.drop(name)
        return await self.create_named(name)

    async def drop(self, name: str) -> None:
        self._expected(name)
        try:
            await self._collection.drop_index(name)
        except asyncio.CancelledError:
            raise
        except PyMongoError as exc:
            raise _translate_index_error(exc) from exc

    def _expected(self, name: str) -> MongoDBRegularIndexDefinition:
        definition = next((item for item in self.expected if item.name == name), None)
        if definition is None:
            raise MongoDBConfigurationError(f"Regular index '{name}' is not provider-owned.")
        return definition

    def _result(self, document: Mapping[str, Any]) -> MongoDBIndexResult:
        name = str(document.get("name", ""))
        keys_value = document.get("key")
        keys = (
            tuple(
                (str(key), int(value))
                for key, value in cast(Mapping[object, object], keys_value).items()
                if isinstance(value, (int, float))
            )
            if isinstance(keys_value, Mapping)
            else ()
        )
        expire = document.get("expireAfterSeconds")
        collation_value = document.get("collation")
        collation = (
            tuple(
                sorted(
                    (str(key), value)
                    for key, value in cast(Mapping[object, object], collation_value).items()
                )
            )
            if isinstance(collation_value, Mapping)
            else None
        )
        definition = MongoDBRegularIndexDefinition(
            name=name,
            keys=keys,
            expire_after_seconds=int(expire) if isinstance(expire, int) else None,
            collation=collation,
        )
        return MongoDBIndexResult(definition, MongoDBIndexState.READY, "READY", True)

    @staticmethod
    def _equivalent(actual: object, expected: MongoDBRegularIndexDefinition) -> bool:
        if not isinstance(actual, MongoDBRegularIndexDefinition):
            return False
        if (
            actual.name != expected.name
            or actual.keys != expected.keys
            or actual.expire_after_seconds != expected.expire_after_seconds
        ):
            return False
        return expected.collation is None or actual.collation == expected.collation
