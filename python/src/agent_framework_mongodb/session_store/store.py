"""MongoDB-backed Agent Framework session snapshots."""

from __future__ import annotations

import asyncio
import hashlib
import json
import logging
import time
from collections.abc import Mapping
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from types import TracebackType
from typing import Any, ClassVar, cast

from agent_framework import AgentSession, SessionStore
from pymongo import ASCENDING, AsyncMongoClient
from pymongo.asynchronous.collection import AsyncCollection
from pymongo.errors import (
    ConnectionFailure,
    DuplicateKeyError,
    OperationFailure,
    PyMongoError,
    ServerSelectionTimeoutError,
)

from .._shared.client import MongoClientHandle
from ..errors import (
    MongoDBAuthorizationError,
    MongoDBConcurrencyError,
    MongoDBConfigurationError,
    MongoDBIndexMismatchError,
    MongoDBIndexMissingError,
    MongoDBMappingError,
    MongoDBPersistenceError,
    MongoDBRetrievalError,
    MongoDBTransientPersistenceError,
    MongoDBTransientRetrievalError,
)

MongoDocument = dict[str, Any]
_LOGGER = logging.getLogger(__name__)


def _scope_value(value: object, name: str) -> str | None:
    if value is None:
        return None
    if not isinstance(value, str):
        raise MongoDBConfigurationError(f"{name} must be a string.")
    normalized = value.strip()
    if not normalized:
        raise MongoDBConfigurationError(f"{name} must not be empty.")
    return normalized


@dataclass(frozen=True, slots=True)
class MongoDBSessionStoreOptions:
    """Immutable authorization scope for session persistence."""

    tenant_id: str | None = None
    application_id: str | None = None
    agent_id: str | None = None
    ttl: timedelta | None = None

    def __post_init__(self) -> None:
        for name in ("tenant_id", "application_id", "agent_id"):
            object.__setattr__(self, name, _scope_value(getattr(self, name), name))
        if not any((self.tenant_id, self.application_id, self.agent_id)):
            raise MongoDBConfigurationError(
                "At least one tenant_id, application_id, or agent_id "
                "authorization scope is required."
            )
        if self.ttl is not None and (type(self.ttl) is not timedelta or self.ttl <= timedelta(0)):
            raise MongoDBConfigurationError("ttl must be a positive duration.")


@dataclass(frozen=True, slots=True)
class MongoDBVersionedSession:
    """A restored session and its optimistic concurrency metadata."""

    session: AgentSession
    version: int
    expires_at: datetime | None


class MongoDBSessionStore(SessionStore):
    """Persist complete authorized Agent Framework session snapshots."""

    SCHEMA_VERSION: ClassVar[int] = 1
    FRAMEWORK_SERIALIZATION_VERSION: ClassVar[str] = (
        "agent-framework-core/1:AgentSession.to_dict/v1"
    )
    DEFAULT_DATABASE_NAME: ClassVar[str] = "agent_framework"
    DEFAULT_COLLECTION_NAME: ClassVar[str] = "agent_sessions"

    def __init__(
        self,
        collection: AsyncCollection[MongoDocument] | None = None,
        *,
        options: MongoDBSessionStoreOptions,
        connection_string: str = "mongodb://localhost:27017",
        database_name: str = DEFAULT_DATABASE_NAME,
        collection_name: str = DEFAULT_COLLECTION_NAME,
        mongo_client: AsyncMongoClient[MongoDocument] | None = None,
    ) -> None:
        if collection is not None and mongo_client is not None:
            raise MongoDBConfigurationError("Provide either collection or mongo_client, not both.")
        self.options = options
        self.database_name = cast(str, _scope_value(database_name, "database_name"))
        self.collection_name = cast(str, _scope_value(collection_name, "collection_name"))
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
        """Return whether this store created its MongoDB client."""
        return self._client_handle is not None and self._client_handle.owns_client

    def _scope(self, session_id: str) -> MongoDocument:
        self.validate_session_id(session_id)
        dimensions = {
            "tenant_id": self.options.tenant_id,
            "application_id": self.options.application_id,
            "agent_id": self.options.agent_id,
        }
        discriminator = _canonical_hash({"version": 1, "dimensions": dimensions})
        return {
            "_id": _canonical_hash(
                {
                    "kind": "agent_session",
                    "scope_discriminator": discriminator,
                    "session_id": session_id,
                }
            ),
            "_kind": "agent_session",
            "scope_discriminator": discriminator,
            **dimensions,
            "session_id": session_id,
        }

    async def get(self, session_id: str) -> AgentSession | None:
        """Load an independent complete session snapshot from the authorized scope."""
        versioned = await self.get_versioned(session_id)
        return versioned.session if versioned is not None else None

    async def get_versioned(self, session_id: str) -> MongoDBVersionedSession | None:
        """Load a snapshot with the version needed for compare-and-swap."""
        started = time.monotonic()
        try:
            document = await self.collection.find_one(self._scope(session_id))
        except PyMongoError as exc:
            _log_failure("load", started, _error_category(exc, "retrieval"))
            raise _translate_mongo_error(exc, "retrieval") from exc
        if document is None:
            _log_success("load", started, 0)
            return None
        restored = _restore(document)
        _log_success("load", started, 1)
        return restored

    async def set(self, session_id: str, session: AgentSession) -> None:
        """Idempotently replace a complete session snapshot in the authorized scope."""
        for _ in range(10):
            existing = await self.get_versioned(session_id)
            try:
                if existing is None:
                    await self.create(session_id, session)
                else:
                    await self.compare_and_set(
                        session_id,
                        session,
                        expected_version=existing.version,
                    )
                return
            except MongoDBConcurrencyError:
                continue
        raise MongoDBConcurrencyError(
            "MongoDB Session Store unconditional replacement could not resolve concurrent writes."
        )

    async def create(
        self,
        session_id: str,
        session: AgentSession,
        *,
        expires_at: datetime | None = None,
    ) -> int:
        """Create version 1, or return version 1 for an identical retry."""
        scope = self._scope(session_id)
        payload = _serialize(session)
        payload_hash = _canonical_hash(payload)
        now = datetime.now(timezone.utc)
        effective_expiry = self._expiration(expires_at, now)
        document: MongoDocument = {
            **scope,
            "schema_version": self.SCHEMA_VERSION,
            "framework_version": self.FRAMEWORK_SERIALIZATION_VERSION,
            "version": 1,
            "created_at": now,
            "updated_at": now,
            "session": payload,
            "payload_hash": payload_hash,
        }
        if effective_expiry is not None:
            document["expires_at"] = effective_expiry
        started = time.monotonic()
        try:
            await self.collection.insert_one(document)
        except DuplicateKeyError:
            existing = await self._read_after_conflict(scope)
            if existing is not None:
                _validate_versions(existing)
                if _same_snapshot(
                    existing,
                    payload_hash,
                    effective_expiry,
                    expiration_was_explicit=expires_at is not None,
                ):
                    return _document_version(existing)
            raise MongoDBConcurrencyError(
                f"Session {session_id!r} already exists in the authorized scope."
            ) from None
        except PyMongoError as exc:
            _log_failure("persist", started, _error_category(exc, "persistence"))
            raise _translate_mongo_error(exc, "persistence") from exc
        _log_success("persist", started, 1)
        return 1

    async def compare_and_set(
        self,
        session_id: str,
        session: AgentSession,
        *,
        expected_version: int,
        expires_at: datetime | None = None,
    ) -> int:
        """Replace only the expected version and return the incremented version."""
        expected_version = _expected_version(expected_version)
        scope = self._scope(session_id)
        payload = _serialize(session)
        payload_hash = _canonical_hash(payload)
        existing = await self._read_after_conflict(scope)
        if existing is None:
            raise MongoDBConcurrencyError(
                f"Session {session_id!r} does not exist at expected version {expected_version}."
            )
        _validate_versions(existing)
        effective_expiry = self._expiration(expires_at, datetime.now(timezone.utc))
        if _document_version(existing) != expected_version:
            if _same_snapshot(
                existing,
                payload_hash,
                effective_expiry,
                expiration_was_explicit=expires_at is not None,
            ):
                return _document_version(existing)
            raise MongoDBConcurrencyError(
                f"Session {session_id!r} is not at expected version {expected_version}."
            )
        now = datetime.now(timezone.utc)
        replacement: MongoDocument = {
            **scope,
            "schema_version": self.SCHEMA_VERSION,
            "framework_version": self.FRAMEWORK_SERIALIZATION_VERSION,
            "version": expected_version + 1,
            "created_at": existing["created_at"],
            "updated_at": now,
            "session": payload,
            "payload_hash": payload_hash,
        }
        if effective_expiry is not None:
            replacement["expires_at"] = effective_expiry
        started = time.monotonic()
        try:
            result = await self.collection.replace_one(
                {**scope, "version": expected_version},
                replacement,
                upsert=False,
            )
        except PyMongoError as exc:
            _log_failure("persist", started, _error_category(exc, "persistence"))
            raise _translate_mongo_error(exc, "persistence") from exc
        if result.matched_count == 1:
            _log_success("persist", started, 1)
            return expected_version + 1
        winner = await self._read_after_conflict(scope)
        if winner is not None and _same_snapshot(
            winner,
            payload_hash,
            effective_expiry,
            expiration_was_explicit=expires_at is not None,
        ):
            return _document_version(winner)
        raise MongoDBConcurrencyError(
            f"Session {session_id!r} changed from expected version {expected_version}."
        )

    async def delete(self, session_id: str) -> None:
        """Idempotently delete one session from the complete authorized scope."""
        scope = self._scope(session_id)
        await self._delete_one(scope)

    async def compare_and_delete(self, session_id: str, *, expected_version: int) -> bool:
        """Delete only the expected version; report whether a document was removed."""
        expected_version = _expected_version(expected_version)
        scope = self._scope(session_id)
        result = await self._delete_one({**scope, "version": expected_version})
        if result:
            return True
        existing = await self._read_after_conflict(scope)
        if existing is None:
            return False
        raise MongoDBConcurrencyError(
            f"Session {session_id!r} is not at expected version {expected_version}."
        )

    async def _delete_one(self, query: MongoDocument) -> bool:
        started = time.monotonic()
        try:
            result = await self.collection.delete_one(query)
        except PyMongoError as exc:
            _log_failure("delete", started, _error_category(exc, "persistence"))
            raise _translate_mongo_error(exc, "persistence") from exc
        _log_success("delete", started, result.deleted_count)
        return result.deleted_count == 1

    async def _read_after_conflict(self, scope: MongoDocument) -> MongoDocument | None:
        try:
            return await self.collection.find_one(scope)
        except PyMongoError as exc:
            raise _translate_mongo_error(exc, "retrieval") from exc

    def _expiration(self, expires_at: datetime | None, now: datetime) -> datetime | None:
        if expires_at is not None:
            if expires_at.tzinfo is None or expires_at.utcoffset() is None:
                raise MongoDBConfigurationError("expires_at must be timezone-aware.")
            normalized = expires_at.astimezone(timezone.utc)
            if normalized <= now:
                raise MongoDBConfigurationError("expires_at must be in the future.")
            return normalized
        return now + self.options.ttl if self.options.ttl is not None else None

    async def ensure_indexes(self) -> tuple[str, ...]:
        """Explicitly create regular scope, version, and configured TTL indexes."""
        partial = {
            "_kind": "agent_session",
            "scope_discriminator": {"$type": "string"},
        }
        definitions: list[tuple[list[tuple[str, int]], dict[str, Any]]] = [
            (
                [("scope_discriminator", ASCENDING), ("session_id", ASCENDING)],
                {
                    "name": "session_store_scope_identity",
                    "unique": True,
                    "collation": {"locale": "simple"},
                    "partialFilterExpression": partial,
                },
            ),
            (
                [
                    ("scope_discriminator", ASCENDING),
                    ("session_id", ASCENDING),
                    ("version", ASCENDING),
                ],
                {
                    "name": "session_store_scope_version",
                    "collation": {"locale": "simple"},
                    "partialFilterExpression": partial,
                },
            ),
        ]
        if self.options.ttl is not None:
            definitions.append(
                (
                    [("expires_at", ASCENDING)],
                    {
                        "name": "session_store_expiration",
                        "expireAfterSeconds": 0,
                        "partialFilterExpression": partial,
                    },
                )
            )
        try:
            return tuple(
                [await self.collection.create_index(keys, **kwargs) for keys, kwargs in definitions]
            )
        except PyMongoError as exc:
            raise _translate_mongo_error(exc, "persistence") from exc

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
            "_kind": "agent_session",
            "scope_discriminator": {"$type": "string"},
        }
        required: dict[str, tuple[tuple[tuple[str, int], ...], bool, int | None]] = {
            "session_store_scope_identity": (
                (("scope_discriminator", 1), ("session_id", 1)),
                True,
                None,
            ),
            "session_store_scope_version": (
                (("scope_discriminator", 1), ("session_id", 1), ("version", 1)),
                False,
                None,
            ),
        }
        if self.options.ttl is not None:
            required["session_store_expiration"] = ((("expires_at", 1),), False, 0)
        for name, (keys, unique, expire_after) in required.items():
            index = by_name.get(name)
            if index is None:
                raise MongoDBIndexMissingError(
                    f"Regular index '{name}' does not exist; create it explicitly."
                )
            if (
                _index_keys(index) != keys
                or bool(index.get("unique", False)) is not unique
                or index.get("partialFilterExpression") != partial
                or (expire_after is None and not _has_simple_collation(index))
                or (expire_after is not None and index.get("expireAfterSeconds") != expire_after)
            ):
                raise MongoDBIndexMismatchError(
                    f"Regular index '{name}' is incompatible; recreate it with ensure_indexes()."
                )

    async def close(self) -> None:
        """Close only the client created by this store."""
        if self._client_handle is not None:
            await self._client_handle.close()

    async def __aenter__(self) -> MongoDBSessionStore:
        return self

    async def __aexit__(
        self,
        exc_type: type[BaseException] | None,
        exc_value: BaseException | None,
        traceback: TracebackType | None,
    ) -> None:
        await self.close()


def _canonical_hash(value: object) -> str:
    encoded = json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False)
    return hashlib.sha256(encoded.encode("utf-8")).hexdigest()


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
    collation = cast(Mapping[str, Any], raw)
    return collation.get("locale") == "simple"


def _serialize(session: AgentSession) -> MongoDocument:
    if type(session) is not AgentSession:
        raise TypeError(
            "MongoDBSessionStore supports AgentSession instances only; "
            "custom subclasses require a custom SessionStore."
        )
    return session.to_dict()


def _restore(document: MongoDocument) -> MongoDBVersionedSession:
    _validate_versions(document)
    version = _document_version(document)
    payload = document.get("session")
    if not isinstance(payload, dict):
        raise MongoDBMappingError(
            "Stored AgentSession payload is invalid; migrate or delete the authorized snapshot."
        )
    expires_at = document.get("expires_at")
    if expires_at is not None and (
        not isinstance(expires_at, datetime)
        or expires_at.tzinfo is None
        or expires_at.utcoffset() is None
    ):
        raise MongoDBMappingError(
            "Stored Session Store expires_at is invalid; migrate the authorized snapshot."
        )
    try:
        session = AgentSession.from_dict(cast(MongoDocument, payload))
    except (KeyError, TypeError, ValueError) as exc:
        raise MongoDBMappingError(
            "Stored AgentSession payload cannot be restored; "
            "migrate or delete the authorized snapshot."
        ) from exc
    return MongoDBVersionedSession(
        session=session,
        version=version,
        expires_at=expires_at.astimezone(timezone.utc) if expires_at is not None else None,
    )


def _document_version(document: MongoDocument) -> int:
    version = document.get("version")
    if type(version) is not int or version < 1:
        raise MongoDBMappingError(
            "Stored Session Store version is invalid; migrate the authorized snapshot."
        )
    return version


def _expected_version(value: object) -> int:
    if type(value) is not int or value < 1:
        raise MongoDBConfigurationError("expected_version must be a positive integer.")
    return value


def _same_snapshot(
    document: MongoDocument,
    payload_hash: str,
    expires_at: datetime | None,
    *,
    expiration_was_explicit: bool,
) -> bool:
    if document.get("payload_hash") != payload_hash:
        return False
    return not expiration_was_explicit or document.get("expires_at") == expires_at


def _validate_versions(document: MongoDocument) -> None:
    schema_version = document.get("schema_version")
    if schema_version != MongoDBSessionStore.SCHEMA_VERSION:
        raise MongoDBMappingError(
            f"Unsupported Session Store schema version {schema_version!r}; "
            "migrate the authorized snapshot to schema version 1 before loading it."
        )
    framework_version = document.get("framework_version")
    if framework_version != MongoDBSessionStore.FRAMEWORK_SERIALIZATION_VERSION:
        raise MongoDBMappingError(
            f"Unsupported AgentSession framework serialization version {framework_version!r}; "
            "migrate the authorized snapshot with a supported Agent Framework version."
        )


def _translate_mongo_error(error: PyMongoError, operation: str) -> Exception:
    if isinstance(error, OperationFailure) and error.code in {13, 18}:
        return MongoDBAuthorizationError("MongoDB authorization failed.")
    transient = isinstance(error, (ConnectionFailure, ServerSelectionTimeoutError))
    if operation == "retrieval":
        if transient:
            return MongoDBTransientRetrievalError(
                "MongoDB Session Store retrieval failed transiently."
            )
        return MongoDBRetrievalError("MongoDB Session Store retrieval failed.")
    if transient:
        return MongoDBTransientPersistenceError(
            "MongoDB Session Store persistence failed transiently."
        )
    return MongoDBPersistenceError("MongoDB Session Store persistence failed.")


def _error_category(error: PyMongoError, operation: str) -> str:
    return _translate_mongo_error(error, operation).__class__.__name__


def _log_success(operation: str, started: float, count: int) -> None:
    _LOGGER.info(
        "MongoDB Session Store operation completed",
        extra={
            "feature": "session_store",
            "operation": operation,
            "outcome": "success" if count else "empty",
            "result_count": count,
            "duration_ms": round((time.monotonic() - started) * 1000),
        },
    )


def _log_failure(operation: str, started: float, category: str) -> None:
    _LOGGER.warning(
        "MongoDB Session Store operation failed",
        extra={
            "feature": "session_store",
            "operation": operation,
            "outcome": "failed",
            "error_category": category,
            "duration_ms": round((time.monotonic() - started) * 1000),
        },
    )
