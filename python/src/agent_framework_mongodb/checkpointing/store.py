"""MongoDB-backed Agent Framework workflow checkpoints."""

from __future__ import annotations

import asyncio
import base64
import copyreg
import hashlib
import io
import json
import logging
import pickle  # nosec B403 -- restricted unpickling of authorized checkpoint storage
from collections.abc import Callable, Mapping, Set
from dataclasses import dataclass, fields, is_dataclass
from datetime import date, datetime, timedelta, timezone
from datetime import time as datetime_time
from decimal import Decimal
from enum import Enum
from math import isfinite
from types import TracebackType
from typing import Any, ClassVar, TypeAlias, cast
from uuid import UUID

from agent_framework import (
    CheckpointID,
    CheckpointStorage,
    WorkflowCheckpoint,
    WorkflowCheckpointException,
)
from bson.binary import Binary
from pymongo import ASCENDING, DESCENDING, AsyncMongoClient, ReturnDocument
from pymongo.asynchronous.collection import AsyncCollection
from pymongo.errors import (
    DuplicateKeyError,
    PyMongoError,
)

from .._shared.client import MongoClientHandle
from .._shared.error_handling import OperationKind, translate_pymongo_error
from .._shared.observability import instrument
from ..errors import (
    MongoDBConcurrencyError,
    MongoDBConfigurationError,
    MongoDBIndexMismatchError,
    MongoDBIndexMissingError,
    MongoDBMappingError,
    MongoDBPersistenceError,
    MongoDBRetrievalError,
    MongoDBSerializationError,
)

MongoDocument: TypeAlias = dict[str, Any]
_LOGGER = logging.getLogger(__name__)

_SAFE_GLOBALS = frozenset(
    {
        "builtins:object",
        "builtins:complex",
        "builtins:range",
        "builtins:slice",
        "builtins:int",
        "builtins:float",
        "builtins:str",
        "builtins:bytes",
        "builtins:bytearray",
        "builtins:bool",
        "builtins:set",
        "builtins:frozenset",
        "builtins:list",
        "builtins:dict",
        "builtins:tuple",
        "copyreg:_reconstructor",
        "datetime:datetime",
        "datetime:date",
        "datetime:time",
        "datetime:timedelta",
        "datetime:timezone",
        "decimal:Decimal",
        "uuid:UUID",
        "collections:OrderedDict",
        "collections:defaultdict",
        "collections:deque",
    }
)


class MongoDBCheckpointNotFoundError(
    MongoDBRetrievalError,
    WorkflowCheckpointException,
):
    """Raised when no checkpoint exists in the complete authorized scope."""


def _required_scope(value: object, name: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise MongoDBConfigurationError(f"{name} must be a non-empty string.")
    return value.strip()


@dataclass(frozen=True, slots=True)
class MongoDBCheckpointStorageOptions:
    """Immutable workflow, run, authorization, retention, and paging settings."""

    tenant_id: str = ""
    workflow_name: str = ""
    session_id: str = ""
    application_id: str | None = None
    ttl: timedelta | None = None
    page_size: int = 100
    max_page_size: int = 1000
    allowed_checkpoint_types: tuple[str, ...] = ()

    def __post_init__(self) -> None:
        for name in ("tenant_id", "workflow_name", "session_id"):
            object.__setattr__(self, name, _required_scope(getattr(self, name), name))
        if self.application_id is not None:
            object.__setattr__(
                self,
                "application_id",
                _required_scope(self.application_id, "application_id"),
            )
        if self.ttl is not None and (type(self.ttl) is not timedelta or self.ttl <= timedelta(0)):
            raise MongoDBConfigurationError("ttl must be a positive duration.")
        if type(self.page_size) is not int or self.page_size < 1:
            raise MongoDBConfigurationError("page_size must be a positive integer.")
        if type(self.max_page_size) is not int or self.max_page_size < 1:
            raise MongoDBConfigurationError("max_page_size must be a positive integer.")
        if self.page_size > self.max_page_size:
            raise MongoDBConfigurationError("page_size must not exceed max_page_size.")
        normalized_types: list[str] = []
        for type_key in self.allowed_checkpoint_types:
            if ":" not in type_key or not all(part.strip() for part in type_key.split(":", 1)):
                raise MongoDBConfigurationError(
                    "allowed_checkpoint_types entries must use 'module:qualname' format."
                )
            normalized_types.append(type_key.strip())
        object.__setattr__(self, "allowed_checkpoint_types", tuple(normalized_types))


@dataclass(frozen=True, slots=True)
class MongoDBCheckpointPage:
    """One bounded, deterministic page of checkpoints."""

    checkpoints: tuple[WorkflowCheckpoint, ...]
    next_cursor: str | None


@dataclass(frozen=True, slots=True)
class MongoDBCheckpointClearResult:
    """Acknowledged counts from an authorized best-effort run cleanup."""

    checkpoints_deleted: int
    counter_deleted: int
    acknowledged: bool = True


class MongoDBCheckpointStorage(CheckpointStorage):
    """Persist immutable checkpoints in one constructor-bound authorized run."""

    SCHEMA_VERSION: ClassVar[int] = 1
    CURSOR_VERSION: ClassVar[int] = 1
    IDEMPOTENCY_HASH_VERSION: ClassVar[int] = 2
    FRAMEWORK_SERIALIZATION_VERSION: ClassVar[str] = (
        "agent-framework-core/1:WorkflowCheckpoint.to_dict/v1"
    )
    SUPPORTED_PAYLOAD_VERSIONS: ClassVar[frozenset[str]] = frozenset({"1.0"})
    DEFAULT_DATABASE_NAME: ClassVar[str] = "agent_framework"
    DEFAULT_COLLECTION_NAME: ClassVar[str] = "workflow_checkpoints"

    def __init__(
        self,
        collection: AsyncCollection[MongoDocument] | None = None,
        *,
        options: MongoDBCheckpointStorageOptions,
        connection_string: str = "mongodb://localhost:27017",
        database_name: str = DEFAULT_DATABASE_NAME,
        collection_name: str = DEFAULT_COLLECTION_NAME,
        mongo_client: AsyncMongoClient[MongoDocument] | None = None,
    ) -> None:
        if collection is not None and mongo_client is not None:
            raise MongoDBConfigurationError("Provide either collection or mongo_client, not both.")
        self.options = options
        self.database_name = _required_scope(database_name, "database_name")
        self.collection_name = _required_scope(collection_name, "collection_name")
        self._scope_discriminator = _canonical_hash(
            {
                "version": 1,
                "tenant_id": options.tenant_id,
                "application_id": options.application_id,
                "workflow_name": options.workflow_name,
                "session_id": options.session_id,
            }
        )
        self._allowed_types = frozenset(options.allowed_checkpoint_types)
        self._client_handle: MongoClientHandle | None
        if collection is not None:
            self._client_handle = None
            self.collection = collection
        else:
            self._client_handle = (
                MongoClientHandle.from_client(mongo_client)
                if mongo_client is not None
                else MongoClientHandle.from_uri(connection_string)
            )
            client = cast(AsyncMongoClient[MongoDocument], self._client_handle.client)
            self.collection = client[self.database_name][self.collection_name]

    @property
    def owns_client(self) -> bool:
        """Return whether this storage created its MongoDB client."""
        return self._client_handle is not None and self._client_handle.owns_client

    def _validate_workflow(self, workflow_name: str) -> str:
        workflow_name = _required_scope(workflow_name, "workflow_name")
        if workflow_name != self.options.workflow_name:
            raise MongoDBConfigurationError(
                "workflow_name must match the constructor-bound workflow_name."
            )
        return workflow_name

    def _partition(self, workflow_name: str) -> MongoDocument:
        return {
            "_kind": "workflow_checkpoint",
            "scope_discriminator": self._scope_discriminator,
            "tenant_id": self.options.tenant_id,
            "application_id": self.options.application_id,
            "workflow_name": self._validate_workflow(workflow_name),
            "session_id": self.options.session_id,
        }

    def _identity(self, checkpoint_id: CheckpointID) -> MongoDocument:
        checkpoint_id = _required_scope(checkpoint_id, "checkpoint_id")
        partition = self._partition(self.options.workflow_name)
        return {
            "_id": _canonical_hash(
                {
                    "kind": "workflow_checkpoint",
                    "scope_discriminator": self._scope_discriminator,
                    "workflow_name": self.options.workflow_name,
                    "session_id": self.options.session_id,
                    "checkpoint_id": checkpoint_id,
                }
            ),
            **partition,
            "checkpoint_id": checkpoint_id,
        }

    @instrument("checkpoint_store", "persist")
    async def save(self, checkpoint: WorkflowCheckpoint) -> CheckpointID:
        """Save once, return the stable ID on an identical retry, and reject conflicts."""
        if type(checkpoint) is not WorkflowCheckpoint:
            raise TypeError("checkpoint must be a WorkflowCheckpoint.")
        self._validate_workflow(checkpoint.workflow_name)
        if checkpoint.previous_checkpoint_id == checkpoint.checkpoint_id:
            raise MongoDBConfigurationError("A checkpoint cannot be its own parent.")
        identity = self._identity(checkpoint.checkpoint_id)
        payload, payload_hash = _serialize(checkpoint, self._allowed_types)
        _validate_payload_version(checkpoint.version)
        existing = await self._find_one(identity)
        if existing is not None:
            _validate_versions(existing)
            if existing.get("payload_hash") == payload_hash:
                return checkpoint.checkpoint_id
            raise MongoDBConcurrencyError(
                "The checkpoint ID already exists with a different payload."
            )

        now = _to_bson_utc_milliseconds(datetime.now(timezone.utc))
        expires_at = (
            _to_bson_utc_milliseconds(now + self.options.ttl)
            if self.options.ttl is not None
            else None
        )
        sequence = await self._allocate_sequence(now=now, expires_at=expires_at)
        document: MongoDocument = {
            **identity,
            "schema_version": self.SCHEMA_VERSION,
            "framework_version": self.FRAMEWORK_SERIALIZATION_VERSION,
            "payload_version": checkpoint.version,
            "idempotency_hash_version": self.IDEMPOTENCY_HASH_VERSION,
            "parent_checkpoint_id": checkpoint.previous_checkpoint_id,
            "sequence": sequence,
            "created_at": now,
            "checkpoint": payload,
            "payload_hash": payload_hash,
        }
        if expires_at is not None:
            document["expires_at"] = expires_at
        try:
            await self.collection.insert_one(document)
        except DuplicateKeyError:
            winner = await self._find_one(identity)
            if winner is not None:
                _validate_versions(winner)
                if winner.get("payload_hash") == payload_hash:
                    return checkpoint.checkpoint_id
            raise MongoDBConcurrencyError(
                "The checkpoint ID or sequence was claimed by a conflicting save."
            ) from None
        except PyMongoError as exc:
            raise _translate_mongo_error(exc, "persistence") from exc
        return checkpoint.checkpoint_id

    @instrument("checkpoint_store", "load")
    async def load(self, checkpoint_id: CheckpointID) -> WorkflowCheckpoint:
        """Load one checkpoint from the complete authorized scope."""
        document = await self._find_one(self._identity(checkpoint_id))
        if document is None:
            raise MongoDBCheckpointNotFoundError(
                "No checkpoint was found in the authorized workflow session."
            )
        restored = self._restore(document)
        return restored

    async def list_checkpoints(self, *, workflow_name: str) -> list[WorkflowCheckpoint]:
        """Enumerate all checkpoints through bounded pages in monotonic order."""
        checkpoints: list[WorkflowCheckpoint] = []
        cursor: str | None = None
        while True:
            page = await self.list_checkpoint_page(
                workflow_name=workflow_name,
                cursor=cursor,
            )
            checkpoints.extend(page.checkpoints)
            if page.next_cursor is None:
                return checkpoints
            cursor = page.next_cursor

    @instrument("checkpoint_store", "list")
    async def list_checkpoint_page(
        self,
        *,
        workflow_name: str,
        cursor: str | None = None,
        limit: int | None = None,
    ) -> MongoDBCheckpointPage:
        """Return a bounded page and an opaque cursor for the next page."""
        workflow_name = self._validate_workflow(workflow_name)
        effective_limit = self.options.page_size if limit is None else limit
        if (
            type(effective_limit) is not int
            or not 1 <= effective_limit <= self.options.max_page_size
        ):
            raise MongoDBConfigurationError(
                f"limit must be between 1 and {self.options.max_page_size}."
            )
        query = self._partition(workflow_name)
        if cursor is not None:
            sequence, checkpoint_id = _decode_cursor(cursor)
            query = {
                **query,
                "$or": [
                    {"sequence": {"$gt": sequence}},
                    {"sequence": sequence, "checkpoint_id": {"$gt": checkpoint_id}},
                ],
            }
        documents = await self._find_many(query, effective_limit + 1)
        has_more = len(documents) > effective_limit
        selected = documents[:effective_limit]
        checkpoints = tuple(self._restore(document) for document in selected)
        next_cursor = None
        if has_more and selected:
            last = selected[-1]
            next_cursor = _encode_cursor(
                _document_sequence(last),
                cast(str, last["checkpoint_id"]),
            )
        return MongoDBCheckpointPage(checkpoints=checkpoints, next_cursor=next_cursor)

    @instrument("checkpoint_store", "delete")
    async def delete(self, checkpoint_id: CheckpointID) -> bool:
        """Delete one checkpoint from the complete authorized scope."""
        try:
            result = await self.collection.delete_one(self._identity(checkpoint_id))
        except PyMongoError as exc:
            raise _translate_mongo_error(exc, "persistence") from exc
        return result.deleted_count == 1

    @instrument("checkpoint_store", "delete")
    async def clear_run(self) -> MongoDBCheckpointClearResult:
        """Best-effort delete all records in this exact authorized workflow run."""
        partition = self._partition(self.options.workflow_name)
        counter_identity = self._counter_identity()
        try:
            checkpoints_result = await self.collection.delete_many(partition)
            counter_result = await self.collection.delete_one(counter_identity)
        except PyMongoError as exc:
            raise _translate_mongo_error(exc, "persistence") from exc
        checkpoints_deleted = _acknowledged_delete_count(checkpoints_result)
        counter_deleted = _acknowledged_delete_count(counter_result)
        return MongoDBCheckpointClearResult(
            checkpoints_deleted=checkpoints_deleted,
            counter_deleted=counter_deleted,
        )

    @instrument("checkpoint_store", "load")
    async def get_latest(self, *, workflow_name: str) -> WorkflowCheckpoint | None:
        """Load the greatest monotonic sequence in the authorized workflow session."""
        document = await self._find_one(
            self._partition(workflow_name),
            sort=[("sequence", DESCENDING), ("checkpoint_id", DESCENDING)],
        )
        if document is None:
            return None
        restored = self._restore(document)
        return restored

    async def list_checkpoint_ids(self, *, workflow_name: str) -> list[CheckpointID]:
        """Enumerate all checkpoint IDs through bounded pages in monotonic order."""
        checkpoints = await self.list_checkpoints(workflow_name=workflow_name)
        return [checkpoint.checkpoint_id for checkpoint in checkpoints]

    def _counter_identity(self) -> MongoDocument:
        return {
            "_id": _canonical_hash(
                {
                    "kind": "workflow_checkpoint_counter",
                    "scope_discriminator": self._scope_discriminator,
                    "workflow_name": self.options.workflow_name,
                    "session_id": self.options.session_id,
                }
            ),
            "_kind": "workflow_checkpoint_counter",
            "scope_discriminator": self._scope_discriminator,
            "tenant_id": self.options.tenant_id,
            "application_id": self.options.application_id,
            "workflow_name": self.options.workflow_name,
            "session_id": self.options.session_id,
        }

    async def _allocate_sequence(
        self,
        *,
        now: datetime,
        expires_at: datetime | None,
    ) -> int:
        retained = await self._find_one(
            self._partition(self.options.workflow_name),
            sort=[("sequence", DESCENDING)],
            projection={"sequence": True, "_id": False},
        )
        retained_max_sequence = _document_sequence(retained) if retained is not None else 0
        update = _counter_update_pipeline(
            now=now,
            expires_at=expires_at,
            retained_max_sequence=retained_max_sequence,
            batch_count=1,
        )
        try:
            counter = await self.collection.find_one_and_update(
                self._counter_identity(),
                update,
                upsert=True,
                return_document=ReturnDocument.AFTER,
            )
        except PyMongoError as exc:
            raise _translate_mongo_error(exc, "persistence") from exc
        if counter is None:
            raise MongoDBPersistenceError(
                "MongoDB Workflow Checkpoint sequence allocation returned no document."
            )
        return _document_sequence(counter)

    async def _find_one(
        self,
        query: MongoDocument,
        *,
        sort: list[tuple[str, int]] | None = None,
        projection: MongoDocument | None = None,
    ) -> MongoDocument | None:
        try:
            return await self.collection.find_one(query, sort=sort, projection=projection)
        except PyMongoError as exc:
            raise _translate_mongo_error(exc, "retrieval") from exc

    async def _find_many(self, query: MongoDocument, limit: int) -> list[MongoDocument]:
        try:
            cursor = self.collection.find(query)
            cursor = cursor.sort([("sequence", ASCENDING), ("checkpoint_id", ASCENDING)])
            cursor = cursor.limit(limit)
            documents = await cursor.to_list(length=limit)
        except PyMongoError as exc:
            raise _translate_mongo_error(exc, "retrieval") from exc
        return documents

    def _restore(self, document: MongoDocument) -> WorkflowCheckpoint:
        _validate_versions(document)
        _document_sequence(document)
        payload_version = document.get("payload_version")
        _validate_payload_version(payload_version)
        payload = document.get("checkpoint")
        if not isinstance(payload, (bytes, Binary)):
            raise MongoDBMappingError(
                "Stored checkpoint payload is invalid; migrate or delete the authorized checkpoint."
            )
        try:
            decoded = _restricted_loads(bytes(payload), self._allowed_types)
        except Exception as exc:
            if isinstance(exc, MongoDBMappingError):
                raise
            raise MongoDBMappingError(
                "Stored checkpoint payload cannot be restored; "
                "migrate or delete the authorized checkpoint."
            ) from exc
        if not isinstance(decoded, dict):
            raise MongoDBMappingError(
                "Stored checkpoint payload is not a public WorkflowCheckpoint dictionary; "
                "migrate the authorized checkpoint."
            )
        try:
            checkpoint = WorkflowCheckpoint.from_dict(cast(dict[str, Any], decoded))
        except WorkflowCheckpointException as exc:
            raise MongoDBMappingError(
                "Stored checkpoint payload cannot be restored; "
                "migrate or delete the authorized checkpoint."
            ) from exc
        if (
            checkpoint.checkpoint_id != document.get("checkpoint_id")
            or checkpoint.workflow_name != document.get("workflow_name")
            or checkpoint.previous_checkpoint_id != document.get("parent_checkpoint_id")
            or checkpoint.version != payload_version
        ):
            raise MongoDBMappingError(
                "Stored checkpoint envelope and payload disagree; "
                "migrate the authorized checkpoint."
            )
        if document.get("payload_hash") != _logical_payload_hash(
            checkpoint,
            self._allowed_types,
        ):
            raise MongoDBMappingError(
                "Stored checkpoint canonical hash does not match its public payload; "
                "migrate or delete the authorized checkpoint."
            )
        return checkpoint

    @instrument("indexing", "ensure_index")
    async def ensure_indexes(self) -> tuple[str, ...]:
        """Explicitly create checkpoint identity, ordering, lineage, and TTL indexes."""
        partial = {
            "_kind": "workflow_checkpoint",
            "scope_discriminator": {"$type": "string"},
        }
        counter_partial = {
            "_kind": "workflow_checkpoint_counter",
            "scope_discriminator": {"$type": "string"},
        }
        prefix = [
            ("scope_discriminator", ASCENDING),
            ("workflow_name", ASCENDING),
            ("session_id", ASCENDING),
        ]
        definitions: list[tuple[list[tuple[str, int]], dict[str, Any]]] = [
            (
                [*prefix, ("checkpoint_id", ASCENDING)],
                {
                    "name": "checkpoint_scope_identity",
                    "unique": True,
                    "collation": {"locale": "simple"},
                    "partialFilterExpression": partial,
                },
            ),
            (
                [*prefix, ("sequence", ASCENDING)],
                {
                    "name": "checkpoint_scope_sequence",
                    "unique": True,
                    "collation": {"locale": "simple"},
                    "partialFilterExpression": partial,
                },
            ),
            (
                [*prefix, ("parent_checkpoint_id", ASCENDING)],
                {
                    "name": "checkpoint_scope_lineage",
                    "collation": {"locale": "simple"},
                    "partialFilterExpression": partial,
                },
            ),
            (
                [("expires_at", ASCENDING)],
                {
                    "name": "checkpoint_expiration",
                    "expireAfterSeconds": 0,
                    "partialFilterExpression": partial,
                },
            ),
            (
                [("expires_at", ASCENDING)],
                {
                    "name": "checkpoint_counter_expiration",
                    "expireAfterSeconds": 0,
                    "partialFilterExpression": counter_partial,
                },
            ),
        ]
        try:
            return tuple(
                [await self.collection.create_index(keys, **kwargs) for keys, kwargs in definitions]
            )
        except PyMongoError as exc:
            raise _translate_mongo_error(exc, "persistence") from exc

    @instrument("indexing", "validate_index")
    async def validate_indexes(self) -> None:
        """Validate required regular indexes without mutating MongoDB."""
        try:
            cursor = await self.collection.list_indexes()
            indexes = await cursor.to_list(length=None)
        except asyncio.CancelledError:
            raise
        except PyMongoError as exc:
            raise _translate_mongo_error(exc, "retrieval") from exc
        by_name = {str(index.get("name")): index for index in indexes}
        partial = {
            "_kind": "workflow_checkpoint",
            "scope_discriminator": {"$type": "string"},
        }
        counter_partial = {
            "_kind": "workflow_checkpoint_counter",
            "scope_discriminator": {"$type": "string"},
        }
        prefix = (
            ("scope_discriminator", 1),
            ("workflow_name", 1),
            ("session_id", 1),
        )
        required = {
            "checkpoint_scope_identity": (
                (*prefix, ("checkpoint_id", 1)),
                True,
                None,
                partial,
            ),
            "checkpoint_scope_sequence": (
                (*prefix, ("sequence", 1)),
                True,
                None,
                partial,
            ),
            "checkpoint_scope_lineage": (
                (*prefix, ("parent_checkpoint_id", 1)),
                False,
                None,
                partial,
            ),
            "checkpoint_expiration": (
                (("expires_at", 1),),
                False,
                0,
                partial,
            ),
            "checkpoint_counter_expiration": (
                (("expires_at", 1),),
                False,
                0,
                counter_partial,
            ),
        }
        for name, (keys, unique, expire_after, expected_partial) in required.items():
            index = by_name.get(name)
            if index is None:
                raise MongoDBIndexMissingError(
                    f"Regular index '{name}' does not exist; create it explicitly."
                )
            if (
                _index_keys(index) != keys
                or bool(index.get("unique", False)) is not unique
                or index.get("partialFilterExpression") != expected_partial
                or (expire_after is None and not _has_simple_collation(index))
                or (expire_after is not None and index.get("expireAfterSeconds") != expire_after)
            ):
                raise MongoDBIndexMismatchError(
                    f"Regular index '{name}' is incompatible; recreate it with ensure_indexes()."
                )

    async def close(self) -> None:
        """Close only the client created by this storage."""
        if self._client_handle is not None:
            await self._client_handle.close()

    async def __aenter__(self) -> MongoDBCheckpointStorage:
        return self

    async def __aexit__(
        self,
        exc_type: type[BaseException] | None,
        exc_value: BaseException | None,
        traceback: TracebackType | None,
    ) -> None:
        await self.close()


class _RestrictedCheckpointUnpickler(pickle.Unpickler):  # nosec B301
    def __init__(self, payload: bytes, allowed_types: frozenset[str]) -> None:
        super().__init__(io.BytesIO(payload))
        self._allowed_types = allowed_types

    def find_class(self, module: str, name: str) -> Any:
        key = f"{module}:{name}"
        if key in _SAFE_GLOBALS or key in self._allowed_types:
            resolved = super().find_class(module, name)  # nosec B301
            if isinstance(resolved, type) or key in _SAFE_GLOBALS:
                return resolved
        if module.startswith("agent_framework."):
            resolved = super().find_class(module, name)  # nosec B301
            if isinstance(resolved, type):
                return resolved
        raise pickle.UnpicklingError(
            f"Checkpoint deserialization blocked for type '{key}'. "
            "Add the application type to allowed_checkpoint_types before loading."
        )


class _RestrictedCheckpointPickler(pickle.Pickler):
    def reducer_override(self, value: object) -> Any:
        _reject_unapproved_pickle_hooks(value)
        return NotImplemented


def _serialize(
    checkpoint: WorkflowCheckpoint,
    allowed_types: frozenset[str],
) -> tuple[Binary, str]:
    public_payload = checkpoint.to_dict()
    _validate_serialized_type_graph(public_payload, active_ids=set())
    canonical = _canonical_checkpoint_payload(public_payload, allowed_types)
    try:
        buffer = io.BytesIO()
        pickler = _RestrictedCheckpointPickler(
            buffer,
            protocol=pickle.HIGHEST_PROTOCOL,
        )
        pickler.dispatch_table = {}
        pickler.dump(public_payload)
        encoded = buffer.getvalue()
    except (pickle.PickleError, TypeError, AttributeError) as exc:
        raise MongoDBMappingError(
            "Checkpoint public state cannot be serialized; "
            "store only serializable workflow and executor state."
        ) from exc
    try:
        decoded = _restricted_loads(encoded, allowed_types)
        if type(decoded) is not dict:
            raise MongoDBSerializationError(
                "Checkpoint payload does not restore to an exact public dictionary."
            )
        restored = WorkflowCheckpoint.from_dict(cast(dict[str, Any], decoded))
        round_trip_canonical = _canonical_checkpoint_payload(
            restored.to_dict(),
            allowed_types,
        )
    except MongoDBSerializationError:
        raise
    except Exception as exc:
        raise MongoDBSerializationError(
            "Checkpoint payload cannot be restored through the approved load path; "
            "migrate it to supported plain values."
        ) from exc
    if round_trip_canonical != canonical:
        raise MongoDBSerializationError(
            "Checkpoint payload changes during serialization round trip; "
            "migrate it to supported plain values."
        )
    return Binary(encoded), _canonical_payload_hash(canonical)


def _logical_payload_hash(
    checkpoint: WorkflowCheckpoint,
    allowed_types: frozenset[str],
) -> str:
    """Hash a canonical logical representation of public checkpoint state."""
    canonical = _canonical_checkpoint_payload(checkpoint.to_dict(), allowed_types)
    return _canonical_payload_hash(canonical)


def _canonical_checkpoint_payload(
    public_payload: object,
    allowed_types: frozenset[str],
) -> object:
    return {
        "version": MongoDBCheckpointStorage.IDEMPOTENCY_HASH_VERSION,
        "checkpoint": _canonical_checkpoint_value(
            public_payload,
            allowed_types=allowed_types,
            active_ids=set(),
        ),
    }


def _canonical_payload_hash(canonical: object) -> str:
    encoded = json.dumps(
        canonical,
        sort_keys=True,
        separators=(",", ":"),
        ensure_ascii=True,
    ).encode("utf-8")
    return hashlib.sha256(encoded).hexdigest()


def _canonical_checkpoint_value(
    value: object,
    *,
    allowed_types: frozenset[str],
    active_ids: set[int],
) -> object:
    if _is_unsupported_mapping(value):
        raise MongoDBSerializationError(
            "Checkpoint mapping values must be exact built-in dict instances; "
            f"'{_type_key(type(value))}' cannot be serialized canonically. "
            "Migrate it to plain dict/list structures before persistence."
        )
    _reject_unapproved_pickle_hooks(value)
    if value is None:
        return {"type": "none"}
    if type(value) is bool:
        return {"type": "bool", "value": value}
    if type(value) is int:
        return {"type": "int", "value": str(value)}
    if type(value) is float:
        if not isfinite(value):
            raise _noncanonical_error(value)
        return {"type": "float", "value": value.hex()}
    if type(value) is str:
        return {"type": "str", "value": value}
    if type(value) is bytes:
        return {
            "type": "bytes",
            "value": base64.b64encode(value).decode("ascii"),
        }
    if type(value) is bytearray:
        return {
            "type": "bytearray",
            "value": base64.b64encode(bytes(value)).decode("ascii"),
        }
    if isinstance(value, datetime):
        return {"type": "datetime", "value": value.isoformat(), "fold": value.fold}
    if isinstance(value, date):
        return {"type": "date", "value": value.isoformat()}
    if isinstance(value, datetime_time):
        return {"type": "time", "value": value.isoformat(), "fold": value.fold}
    if isinstance(value, timezone):
        offset = value.utcoffset(None)
        return {
            "type": "timezone",
            "offset_seconds": offset.total_seconds(),
            "name": value.tzname(None),
        }
    if isinstance(value, timedelta):
        return {
            "type": "timedelta",
            "days": value.days,
            "seconds": value.seconds,
            "microseconds": value.microseconds,
        }
    if isinstance(value, UUID):
        return {"type": "uuid", "value": value.hex}
    if isinstance(value, Decimal):
        decimal_tuple = value.as_tuple()
        return {
            "type": "decimal",
            "sign": decimal_tuple.sign,
            "digits": list(decimal_tuple.digits),
            "exponent": decimal_tuple.exponent,
        }
    if isinstance(value, Enum):
        return {
            "type": "enum",
            "class": _type_key(type(value)),
            "name": value.name,
        }
    if isinstance(value, type):
        type_key = _type_key(value)
        if value.__module__.startswith("agent_framework.") or type_key in allowed_types:
            return {"type": "type_reference", "class": type_key}
        raise _noncanonical_error(value)

    value_id = id(value)
    if value_id in active_ids:
        raise MongoDBMappingError(
            "Checkpoint public state contains a cycle and has no canonical serialization."
        )
    active_ids.add(value_id)
    try:
        if type(value) is dict:
            mapping = cast(Mapping[object, object], value)
            pairs = [
                [
                    _canonical_checkpoint_value(
                        key,
                        allowed_types=allowed_types,
                        active_ids=active_ids,
                    ),
                    _canonical_checkpoint_value(
                        item,
                        allowed_types=allowed_types,
                        active_ids=active_ids,
                    ),
                ]
                for key, item in mapping.items()
            ]
            pairs.sort(key=lambda pair: _canonical_sort_key(pair[0]))
            return {"type": "mapping", "items": pairs}
        if isinstance(value, list):
            list_value = cast(list[object], value)
            return {
                "type": "list",
                "items": [
                    _canonical_checkpoint_value(
                        item,
                        allowed_types=allowed_types,
                        active_ids=active_ids,
                    )
                    for item in list_value
                ],
            }
        if isinstance(value, tuple):
            tuple_value = cast(tuple[object, ...], value)
            return {
                "type": "tuple",
                "items": [
                    _canonical_checkpoint_value(
                        item,
                        allowed_types=allowed_types,
                        active_ids=active_ids,
                    )
                    for item in tuple_value
                ],
            }
        if isinstance(value, (set, frozenset)):
            set_value = cast(Set[object], value)
            items = [
                _canonical_checkpoint_value(
                    item,
                    allowed_types=allowed_types,
                    active_ids=active_ids,
                )
                for item in set_value
            ]
            items.sort(key=_canonical_sort_key)
            return {
                "type": "frozenset" if isinstance(value, frozenset) else "set",
                "items": items,
            }

        type_key = _type_key(type(value))
        type_is_allowed = (
            type(value).__module__.startswith("agent_framework.") or type_key in allowed_types
        )
        to_dict = getattr(value, "to_dict", None)
        if type_is_allowed and callable(to_dict):
            public_value = cast(Callable[[], object], to_dict)()
            if not isinstance(public_value, Mapping):
                raise _noncanonical_error(value)
            return {
                "type": "object",
                "class": type_key,
                "value": _canonical_checkpoint_value(
                    cast(Mapping[object, object], public_value),
                    allowed_types=allowed_types,
                    active_ids=active_ids,
                ),
            }
        if type_is_allowed and is_dataclass(value) and not isinstance(value, type):
            return {
                "type": "dataclass",
                "class": type_key,
                "fields": [
                    [
                        field.name,
                        _canonical_checkpoint_value(
                            getattr(value, field.name),
                            allowed_types=allowed_types,
                            active_ids=active_ids,
                        ),
                    ]
                    for field in fields(value)
                ],
            }
        raise _noncanonical_error(value)
    finally:
        active_ids.remove(value_id)


def _canonical_sort_key(value: object) -> str:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True)


def _type_key(value_type: type[object]) -> str:
    return f"{value_type.__module__}:{value_type.__qualname__}"


_APPROVED_CUSTOM_PICKLE_TYPES = frozenset(
    {
        bytearray,
        bool,
        bytes,
        date,
        datetime,
        datetime_time,
        Decimal,
        dict,
        float,
        frozenset,
        int,
        list,
        type(None),
        set,
        str,
        timedelta,
        timezone,
        tuple,
        UUID,
    }
)


def _is_unsupported_mapping(value: object) -> bool:
    if type(value) is dict:
        return False
    return isinstance(value, Mapping)


def _validate_serialized_type_graph(value: object, *, active_ids: set[int]) -> None:
    _reject_unapproved_pickle_hooks(value)
    if (
        value is None
        or type(value) in {bool, int, float, str, bytes, bytearray}
        or isinstance(value, (date, datetime_time, timedelta, timezone, UUID, Decimal, Enum))
        or isinstance(value, type)
    ):
        return

    value_id = id(value)
    if value_id in active_ids:
        return
    active_ids.add(value_id)
    try:
        if type(value) is dict:
            mapping = cast(dict[object, object], value)
            for key, item in mapping.items():
                _validate_serialized_type_graph(key, active_ids=active_ids)
                _validate_serialized_type_graph(item, active_ids=active_ids)
            return
        if isinstance(value, (list, tuple, set, frozenset)):
            sequence = cast(
                list[object] | tuple[object, ...] | set[object] | frozenset[object], value
            )
            for item in sequence:
                _validate_serialized_type_graph(item, active_ids=active_ids)
            return

        try:
            instance_state = vars(value)
        except TypeError:
            instance_state = {}
        for item in instance_state.values():
            _validate_serialized_type_graph(item, active_ids=active_ids)
        for value_type in type(value).__mro__:
            slots = value_type.__dict__.get("__slots__", ())
            if isinstance(slots, str):
                slots = (slots,)
            for slot in cast(tuple[str, ...], slots):
                if slot not in {"__dict__", "__weakref__"} and hasattr(value, slot):
                    _validate_serialized_type_graph(
                        getattr(value, slot),
                        active_ids=active_ids,
                    )
    finally:
        active_ids.remove(value_id)


def _reject_unapproved_pickle_hooks(value: object) -> None:
    value_type = type(value)
    _reject_copyreg_extension(value_type)
    if isinstance(value, type):
        _reject_copyreg_extension(cast(type[object], value))
    if value_type in _APPROVED_CUSTOM_PICKLE_TYPES or isinstance(value, type):
        return
    if isinstance(value, Enum):
        if (
            getattr(value_type, "__reduce_ex__", None) is Enum.__reduce_ex__
            and getattr(value_type, "__reduce__", None) is object.__reduce__
        ):
            return
    hooks = (
        ("__reduce_ex__", getattr(object, "__reduce_ex__", None)),
        ("__reduce__", getattr(object, "__reduce__", None)),
        ("__getstate__", getattr(object, "__getstate__", None)),
        ("__setstate__", getattr(object, "__setstate__", None)),
        ("__getnewargs__", None),
        ("__getnewargs_ex__", None),
    )
    if any(getattr(value_type, name, None) is not default for name, default in hooks):
        raise MongoDBSerializationError(
            "Checkpoint public state contains a type with custom pickle hooks "
            f"'{_type_key(value_type)}'; migrate it to a framework-supported type, "
            "application dataclass with default serialization, or plain dict/list structures."
        )


def _reject_copyreg_extension(value_type: type[object]) -> None:
    registry_value = getattr(copyreg, "_extension_registry", None)
    if not isinstance(registry_value, dict):
        raise MongoDBSerializationError(
            "The Python copyreg extension registry cannot be validated; "
            "use a supported Python runtime before persisting checkpoints."
        )
    registry = cast(dict[tuple[str, str], int], registry_value)
    module = value_type.__module__
    names = {value_type.__name__, value_type.__qualname__}
    if any((module, name) in registry for name in names):
        raise MongoDBSerializationError(
            "Checkpoint public state contains a type registered in the copyreg "
            f"extension registry '{module}:{value_type.__qualname__}'; remove the "
            "extension registration or migrate it to approved plain values."
        )


def _noncanonical_error(value: object) -> MongoDBMappingError:
    return MongoDBMappingError(
        "Checkpoint public state contains unsupported noncanonical type "
        f"'{_type_key(type(value))}'; use supported values or register an "
        "application dataclass/public to_dict type in allowed_checkpoint_types."
    )


def _restricted_loads(payload: bytes, allowed_types: frozenset[str]) -> Any:
    return _RestrictedCheckpointUnpickler(payload, allowed_types).load()


def _canonical_hash(value: object) -> str:
    encoded = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return hashlib.sha256(encoded.encode("utf-8")).hexdigest()


def _counter_update_pipeline(
    *,
    now: datetime,
    expires_at: datetime | None,
    retained_max_sequence: int,
    batch_count: int,
) -> list[MongoDocument]:
    common: MongoDocument = {
        "sequence": {
            "$add": [
                {
                    "$max": [
                        {"$ifNull": ["$sequence", 0]},
                        retained_max_sequence,
                    ]
                },
                batch_count,
            ]
        },
        "created_at": {"$ifNull": ["$created_at", now]},
    }
    if expires_at is None:
        return [
            {
                "$set": {
                    **common,
                    "retention_mode": "permanent",
                }
            },
            {"$set": {"expires_at": "$$REMOVE"}},
        ]

    existing_permanent = {
        "$or": [
            {"$eq": ["$retention_mode", "permanent"]},
            {
                "$and": [
                    {"$ne": [{"$ifNull": ["$sequence", None]}, None]},
                    {"$eq": [{"$ifNull": ["$expires_at", None]}, None]},
                ]
            },
        ]
    }
    return [
        {
            "$set": {
                **common,
                "retention_mode": {
                    "$cond": [existing_permanent, "permanent", "ttl"],
                },
            }
        },
        {
            "$set": {
                "expires_at": {
                    "$cond": [
                        {"$eq": ["$retention_mode", "permanent"]},
                        "$$REMOVE",
                        {
                            "$cond": [
                                {
                                    "$gt": [
                                        {"$ifNull": ["$expires_at", expires_at]},
                                        expires_at,
                                    ]
                                },
                                "$expires_at",
                                expires_at,
                            ]
                        },
                    ]
                }
            }
        },
    ]


def _encode_cursor(sequence: int, checkpoint_id: str) -> str:
    payload = json.dumps(
        {"v": MongoDBCheckpointStorage.CURSOR_VERSION, "s": sequence, "i": checkpoint_id},
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")
    return base64.urlsafe_b64encode(payload).decode("ascii").rstrip("=")


def _decode_cursor(cursor: str) -> tuple[int, str]:
    if not cursor:
        raise MongoDBConfigurationError("cursor must be a non-empty string.")
    try:
        padding = "=" * (-len(cursor) % 4)
        decoded = cast(
            object,
            json.loads(base64.b64decode(cursor + padding, altchars=b"-_", validate=True)),
        )
    except (ValueError, TypeError, json.JSONDecodeError) as exc:
        raise MongoDBConfigurationError("cursor is invalid or incompatible.") from exc
    if not isinstance(decoded, dict):
        raise MongoDBConfigurationError("cursor is invalid or incompatible.")
    values = cast(dict[str, object], decoded)
    version = values.get("v")
    sequence = values.get("s")
    checkpoint_id = values.get("i")
    if (
        version != MongoDBCheckpointStorage.CURSOR_VERSION
        or type(sequence) is not int
        or sequence < 1
        or not isinstance(checkpoint_id, str)
        or not checkpoint_id
    ):
        raise MongoDBConfigurationError("cursor is invalid or incompatible.")
    return sequence, checkpoint_id


def _document_sequence(document: Mapping[str, Any]) -> int:
    sequence = document.get("sequence")
    if type(sequence) is not int or sequence < 1:
        raise MongoDBMappingError(
            "Stored checkpoint sequence is invalid; migrate the authorized workflow session."
        )
    return sequence


def _validate_payload_version(version: object) -> None:
    if version not in MongoDBCheckpointStorage.SUPPORTED_PAYLOAD_VERSIONS:
        raise MongoDBMappingError(
            f"Unsupported WorkflowCheckpoint payload version {version!r}; "
            "migrate it with a supported Agent Framework version before loading."
        )


def _validate_versions(document: Mapping[str, Any]) -> None:
    schema_version = document.get("schema_version")
    if schema_version != MongoDBCheckpointStorage.SCHEMA_VERSION:
        raise MongoDBMappingError(
            f"Unsupported checkpoint schema version {schema_version!r}; "
            "migrate the authorized checkpoint to schema version 1 before loading it."
        )
    framework_version = document.get("framework_version")
    if framework_version != MongoDBCheckpointStorage.FRAMEWORK_SERIALIZATION_VERSION:
        raise MongoDBMappingError(
            "Unsupported WorkflowCheckpoint framework serialization version "
            f"{framework_version!r}; "
            "migrate the authorized checkpoint with a supported Agent Framework version."
        )
    hash_version = document.get("idempotency_hash_version")
    if hash_version != MongoDBCheckpointStorage.IDEMPOTENCY_HASH_VERSION:
        raise MongoDBMappingError(
            f"Unsupported checkpoint idempotency hash version {hash_version!r}; "
            "migrate the authorized checkpoint with the canonical version 2 hash."
        )


def _to_bson_utc_milliseconds(value: datetime) -> datetime:
    normalized = value.astimezone(timezone.utc)
    return normalized.replace(microsecond=(normalized.microsecond // 1000) * 1000)


def _index_keys(index: Mapping[str, Any]) -> tuple[tuple[str, int], ...]:
    raw = index.get("key")
    if not isinstance(raw, Mapping):
        return ()
    keys = cast(Mapping[str, int], raw)
    return tuple((name, value) for name, value in keys.items())


def _has_simple_collation(index: Mapping[str, Any]) -> bool:
    raw = index.get("collation")
    if raw is None:
        return True
    if not isinstance(raw, Mapping):
        return False
    collation = cast(Mapping[str, object], raw)
    return collation.get("locale") == "simple"


def _acknowledged_delete_count(result: object) -> int:
    acknowledged = getattr(result, "acknowledged", True)
    if acknowledged is not True:
        raise MongoDBPersistenceError(
            "MongoDB Workflow Checkpoint cleanup requires acknowledged writes."
        )
    deleted_count = getattr(result, "deleted_count", None)
    if type(deleted_count) is not int or deleted_count < 0:
        raise MongoDBPersistenceError(
            "MongoDB Workflow Checkpoint cleanup returned an invalid delete count."
        )
    return deleted_count


def _translate_mongo_error(error: PyMongoError, operation: OperationKind) -> Exception:
    return translate_pymongo_error(error, operation, feature="checkpoint_store")
