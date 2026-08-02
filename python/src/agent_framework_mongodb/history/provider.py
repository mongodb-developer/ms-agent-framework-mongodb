"""Agent Framework exact-history provider backed by MongoDB."""

from __future__ import annotations

import asyncio
import hashlib
import json
import logging
import time
import uuid
from collections.abc import Awaitable, Mapping, Sequence
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from types import TracebackType
from typing import Any, ClassVar, TypeVar, cast

from agent_framework import AgentSession, HistoryProvider, Message, SessionContext, SupportsAgentRun
from pymongo import ASCENDING, DESCENDING, AsyncMongoClient, ReturnDocument
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
    MongoDBConfigurationError,
    MongoDBIndexMismatchError,
    MongoDBIndexMissingError,
    MongoDBMappingError,
    MongoDBPersistenceError,
    MongoDBRetrievalError,
    MongoDBTimeoutError,
    MongoDBTransientPersistenceError,
    MongoDBTransientRetrievalError,
)

MongoDocument = dict[str, Any]
_LOGGER = logging.getLogger(__name__)
_T = TypeVar("_T")


def _scope_value(value: object, name: str) -> str | None:
    if value is None:
        return None
    if not isinstance(value, str):
        raise MongoDBConfigurationError(f"{name} must be a string.")
    normalized = value.strip()
    if not normalized:
        raise MongoDBConfigurationError(f"{name} must not be empty.")
    return normalized


def _required_scope_value(value: object, name: str) -> str:
    normalized = _scope_value(value, name)
    if normalized is None:
        raise MongoDBConfigurationError(f"{name} is required.")
    return normalized


def _positive_duration(value: object, name: str) -> timedelta | None:
    if value is not None and (not isinstance(value, timedelta) or value <= timedelta(0)):
        raise MongoDBConfigurationError(f"{name} must be a positive duration.")
    return value


def _positive_timeout(value: object, name: str) -> float | None:
    if value is not None and (
        isinstance(value, bool) or not isinstance(value, (int, float)) or value <= 0
    ):
        raise MongoDBConfigurationError(f"{name} must be a positive number.")
    return float(value) if value is not None else None


@dataclass(frozen=True, slots=True)
class MongoDBHistoryProviderOptions:
    """Immutable scope, filtering, ordering, and retention configuration."""

    session_id: str
    tenant_id: str | None = None
    application_id: str | None = None
    agent_id: str | None = None
    user_id: str | None = None
    max_messages: int = 100
    max_age: timedelta | None = None
    retention: timedelta | None = None
    retrieval_timeout: float | None = None
    persistence_timeout: float | None = None
    source_id: str = "mongodb-history"
    load_messages: bool = True
    store_inputs: bool = True
    store_context_messages: bool = False
    store_context_from: frozenset[str] | None = None
    store_outputs: bool = True

    def __post_init__(self) -> None:
        for name in ("tenant_id", "application_id", "agent_id", "user_id"):
            object.__setattr__(self, name, _scope_value(getattr(self, name), name))
        object.__setattr__(
            self,
            "session_id",
            _required_scope_value(self.session_id, "session_id"),
        )
        object.__setattr__(
            self,
            "source_id",
            _required_scope_value(self.source_id, "source_id"),
        )
        if not any((self.tenant_id, self.application_id, self.agent_id, self.user_id)):
            raise MongoDBConfigurationError(
                "At least one tenant_id, application_id, agent_id, or user_id "
                "authorization scope is required."
            )
        if type(self.max_messages) is not int:
            raise MongoDBConfigurationError("max_messages must be a positive integer.")
        if self.max_messages <= 0 or self.max_messages > 10_000:
            raise MongoDBConfigurationError("max_messages must be between 1 and 10000.")
        object.__setattr__(self, "max_age", _positive_duration(self.max_age, "max_age"))
        object.__setattr__(self, "retention", _positive_duration(self.retention, "retention"))
        object.__setattr__(
            self,
            "retrieval_timeout",
            _positive_timeout(self.retrieval_timeout, "retrieval_timeout"),
        )
        object.__setattr__(
            self,
            "persistence_timeout",
            _positive_timeout(self.persistence_timeout, "persistence_timeout"),
        )
        if self.store_context_from is not None:
            sources = frozenset(
                _scope_value(value, "store_context_from") for value in self.store_context_from
            )
            object.__setattr__(self, "store_context_from", cast(frozenset[str], sources))


class MongoDBHistoryProvider(HistoryProvider):
    """Persist and replay an authorized exact Agent Framework transcript."""

    SCHEMA_VERSION: ClassVar[int] = 2
    FRAMEWORK_SERIALIZATION_VERSION: ClassVar[int] = 1
    DEFAULT_DATABASE_NAME: ClassVar[str] = "agent_framework"
    DEFAULT_COLLECTION_NAME: ClassVar[str] = "chat_history"

    def __init__(
        self,
        collection: AsyncCollection[MongoDocument] | None = None,
        *,
        options: MongoDBHistoryProviderOptions,
        connection_string: str = "mongodb://localhost:27017",
        database_name: str = DEFAULT_DATABASE_NAME,
        collection_name: str = DEFAULT_COLLECTION_NAME,
        mongo_client: AsyncMongoClient[MongoDocument] | None = None,
    ) -> None:
        super().__init__(
            options.source_id,
            load_messages=options.load_messages,
            store_inputs=options.store_inputs,
            store_context_messages=options.store_context_messages,
            store_context_from=(
                set(options.store_context_from) if options.store_context_from is not None else None
            ),
            store_outputs=options.store_outputs,
        )
        if collection is not None and mongo_client is not None:
            raise MongoDBConfigurationError("Provide either collection or mongo_client, not both.")
        self.options = options
        self.database_name = cast(str, _scope_value(database_name, "database_name"))
        self.collection_name = cast(str, _scope_value(collection_name, "collection_name"))
        self._direct_retry_state: dict[str, Any] = {}
        self._active_retry_attempts: set[str] = set()
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
        """Return whether this provider created its MongoDB client."""
        return self._client_handle is not None and self._client_handle.owns_client

    def _session_scope(self, session_id: str | None) -> MongoDocument:
        effective = (
            self.options.session_id
            if session_id is None
            else _scope_value(session_id, "session_id")
        )
        if effective != self.options.session_id:
            raise MongoDBConfigurationError(
                "The requested session_id does not match this provider's authorized session."
            )
        dimensions = {
            name: getattr(self.options, name)
            for name in ("tenant_id", "application_id", "agent_id", "user_id")
        }
        scope: MongoDocument = {
            "scope_discriminator": _canonical_hash({"version": 1, "dimensions": dimensions}),
            **dimensions,
            "session_id": self.options.session_id,
        }
        return scope

    @staticmethod
    def _reject_service_managed_history(context: SessionContext) -> None:
        if context.service_session_id is not None:
            raise MongoDBConfigurationError(
                "MongoDB History cannot be combined with service-managed conversation history; "
                "disable one history owner to avoid duplicate replay."
            )

    async def before_run(
        self,
        *,
        agent: SupportsAgentRun,
        session: AgentSession,
        context: SessionContext,
        state: dict[str, Any],
    ) -> None:
        """Reject duplicate history ownership, then use framework loading conventions."""
        self._reject_service_managed_history(context)
        await super().before_run(agent=agent, session=session, context=context, state=state)

    async def after_run(
        self,
        *,
        agent: SupportsAgentRun,
        session: AgentSession,
        context: SessionContext,
        state: dict[str, Any],
    ) -> None:
        """Reject duplicate history ownership, then use framework storage filters."""
        self._reject_service_managed_history(context)
        await super().after_run(agent=agent, session=session, context=context, state=state)

    async def get_messages(
        self,
        session_id: str | None,
        *,
        state: dict[str, Any] | None = None,
        **kwargs: Any,
    ) -> list[Message]:
        """Load the latest authorized messages and return them chronologically."""
        del state, kwargs
        scope = self._session_scope(session_id)
        started = time.monotonic()
        try:
            messages = await _with_timeout(
                self._get_messages(scope),
                self.options.retrieval_timeout,
                operation="retrieval",
            )
        except asyncio.CancelledError:
            raise
        except (MongoDBMappingError, MongoDBTimeoutError):
            raise
        except PyMongoError as exc:
            _log_failure("load", started, _error_category(exc, "retrieval"))
            raise _translate_mongo_error(exc, "retrieval") from exc
        _log_success("load", started, len(messages))
        return messages

    async def _get_messages(self, scope: MongoDocument) -> list[Message]:
        query: MongoDocument = {"_kind": "message", **scope}
        if self.options.max_age is not None:
            query["created_at"] = {
                "$gte": datetime.now(timezone.utc) - self.options.max_age,
            }
        cursor = (
            self.collection.find(query)
            .sort("sequence", DESCENDING)
            .limit(self.options.max_messages)
        )
        documents = await cursor.to_list(length=self.options.max_messages)
        documents.reverse()
        return [_message_from_document(document) for document in documents]

    async def save_messages(
        self,
        session_id: str | None,
        messages: Sequence[Message],
        *,
        state: dict[str, Any] | None = None,
        **kwargs: Any,
    ) -> None:
        """Append one exact, idempotent message envelope per selected message."""
        del kwargs
        scope = self._session_scope(session_id)
        if not messages:
            return
        started = time.monotonic()
        try:
            await _with_timeout(
                self._save_messages(scope, messages, state),
                self.options.persistence_timeout,
                operation="persistence",
            )
        except asyncio.CancelledError:
            raise
        except (MongoDBMappingError, MongoDBTimeoutError):
            raise
        except PyMongoError as exc:
            _log_failure("persist", started, _error_category(exc, "persistence"))
            raise _translate_mongo_error(exc, "persistence") from exc
        _log_success("persist", started, len(messages))

    async def _save_messages(
        self,
        scope: MongoDocument,
        messages: Sequence[Message],
        state: dict[str, Any] | None,
    ) -> None:
        retry_state = state if state is not None else self._direct_retry_state
        batch_fingerprint = _history_batch_fingerprint(messages, scope)
        retry_attempt = _begin_history_retry_attempt(
            retry_state,
            batch_fingerprint,
            self._active_retry_attempts,
            token_hint=(
                f"explicit:{batch_fingerprint}"
                if all(message.message_id for message in messages)
                else None
            ),
        )
        _attempt_id, attempt = retry_attempt
        try:
            candidates: list[MongoDocument] = []
            existing_by_id: dict[str, MongoDocument] = {}
            retry_ids = cast(dict[str, Any], attempt["ids"])
            for ordinal, message in enumerate(messages):
                payload = _serialize_message(message)
                stable_message_id = _stable_message_id(
                    message,
                    scope,
                    ordinal,
                    retry_ids,
                )
                document_id = _document_id(scope, stable_message_id)
                candidate: MongoDocument = {
                    "_id": document_id,
                    "_kind": "message",
                    "schema_version": self.SCHEMA_VERSION,
                    "framework_version": self.FRAMEWORK_SERIALIZATION_VERSION,
                    **scope,
                    "stable_message_id": stable_message_id,
                    "message_id": message.message_id,
                    "role": message.role,
                    "message": payload,
                }
                candidates.append(candidate)
                existing = await self.collection.find_one(
                    {"_id": document_id, "_kind": "message", **scope}
                )
                if existing is not None:
                    _validate_duplicate(existing, candidate)
                    existing_by_id[document_id] = existing

            token = cast(str, attempt["token"])
            if len(existing_by_id) == len(candidates):
                await self._delete_reservation(scope, token)
            else:
                first_sequence = await self._reserve_sequence(
                    scope,
                    token=token,
                    count=len(candidates),
                )
                now = datetime.now(timezone.utc)
                for ordinal, candidate in enumerate(candidates):
                    candidate["sequence"] = first_sequence + ordinal
                    candidate["created_at"] = now
                    if self.options.retention is not None:
                        candidate["expires_at"] = now + self.options.retention
                    existing = existing_by_id.get(cast(str, candidate["_id"]))
                    if existing is not None:
                        _validate_duplicate(existing, candidate, include_sequence=True)
                        continue
                    try:
                        await self.collection.insert_one(candidate)
                    except DuplicateKeyError:
                        existing = await self.collection.find_one(
                            {
                                "_kind": "message",
                                **scope,
                                "stable_message_id": candidate["stable_message_id"],
                            }
                        )
                        if existing is None:
                            raise
                        _validate_duplicate(existing, candidate, include_sequence=True)
                await self._delete_reservation(scope, token)
        except (asyncio.CancelledError, Exception):
            _finish_history_retry_attempt(
                retry_state,
                batch_fingerprint,
                retry_attempt,
                self._active_retry_attempts,
                succeeded=False,
            )
            raise
        _finish_history_retry_attempt(
            retry_state,
            batch_fingerprint,
            retry_attempt,
            self._active_retry_attempts,
            succeeded=True,
        )

    async def _reserve_sequence(
        self,
        scope: MongoDocument,
        *,
        token: str,
        count: int,
    ) -> int:
        reservation_id = _reservation_id(scope, token)
        reservation_filter = {
            "_id": reservation_id,
            "_kind": "reservation",
            **scope,
        }
        existing = await self.collection.find_one(reservation_filter)
        if existing is not None:
            return _validate_reservation(existing, count)
        first_sequence = await self._allocate_sequence(scope, count)
        reservation: MongoDocument = {
            **reservation_filter,
            "schema_version": self.SCHEMA_VERSION,
            "framework_version": self.FRAMEWORK_SERIALIZATION_VERSION,
            "token": token,
            "count": count,
            "first_sequence": first_sequence,
        }
        try:
            await self.collection.insert_one(reservation)
        except DuplicateKeyError:
            existing = await self.collection.find_one(reservation_filter)
            if existing is None:
                raise
            return _validate_reservation(existing, count)
        return first_sequence

    async def _delete_reservation(self, scope: MongoDocument, token: str) -> None:
        await self.collection.delete_one(
            {
                "_id": _reservation_id(scope, token),
                "_kind": "reservation",
                **scope,
            }
        )

    async def _allocate_sequence(self, scope: MongoDocument, count: int) -> int:
        counter_id = _counter_id(scope)
        try:
            counter = await self.collection.find_one_and_update(
                {"_id": counter_id, "_kind": "sequence", **scope},
                {
                    "$inc": {"sequence": count},
                    "$setOnInsert": {
                        "schema_version": self.SCHEMA_VERSION,
                        "framework_version": self.FRAMEWORK_SERIALIZATION_VERSION,
                    },
                },
                upsert=True,
                return_document=ReturnDocument.AFTER,
            )
        except DuplicateKeyError:
            counter = await self.collection.find_one_and_update(
                {"_id": counter_id, "_kind": "sequence", **scope},
                {"$inc": {"sequence": count}},
                return_document=ReturnDocument.AFTER,
            )
        if counter is None or not isinstance(counter.get("sequence"), int):
            raise MongoDBPersistenceError("MongoDB History sequence allocation returned no value.")
        return cast(int, counter["sequence"]) - count + 1

    async def clear_messages(self, session_id: str | None = None) -> int:
        """Clear exactly one authorized session and return acknowledged message count."""
        scope = self._session_scope(session_id)
        started = time.monotonic()
        try:
            result = await _with_timeout(
                self.collection.delete_many({"_kind": "message", **scope}),
                self.options.persistence_timeout,
                operation="persistence",
            )
            await _with_timeout(
                self.collection.delete_one(
                    {"_id": _counter_id(scope), "_kind": "sequence", **scope}
                ),
                self.options.persistence_timeout,
                operation="persistence",
            )
            await _with_timeout(
                self.collection.delete_many({"_kind": "reservation", **scope}),
                self.options.persistence_timeout,
                operation="persistence",
            )
        except asyncio.CancelledError:
            raise
        except MongoDBTimeoutError:
            raise
        except PyMongoError as exc:
            _log_failure("delete", started, _error_category(exc, "persistence"))
            raise _translate_mongo_error(exc, "persistence") from exc
        count = int(result.deleted_count)
        _log_success("delete", started, count)
        return count

    async def ensure_indexes(self) -> tuple[str, ...]:
        """Explicitly create regular uniqueness, ordering, and optional TTL indexes."""
        scope_keys = [
            ("scope_discriminator", ASCENDING),
            ("session_id", ASCENDING),
        ]
        partial = {
            "_kind": "message",
            "scope_discriminator": {"$type": "string"},
        }
        definitions: list[tuple[list[tuple[str, int]], MongoDocument]] = [
            (
                [*scope_keys, ("stable_message_id", ASCENDING)],
                {
                    "name": "history_scoped_message_unique",
                    "unique": True,
                    "partialFilterExpression": partial,
                },
            ),
            (
                [*scope_keys, ("sequence", ASCENDING)],
                {
                    "name": "history_scoped_sequence",
                    "unique": True,
                    "partialFilterExpression": partial,
                },
            ),
        ]
        if self.options.retention is not None:
            definitions.append(
                (
                    [("expires_at", ASCENDING)],
                    {
                        "name": "history_expiration_ttl",
                        "expireAfterSeconds": 0,
                        "partialFilterExpression": partial,
                    },
                )
            )
        try:
            return tuple(
                [await self.collection.create_index(keys, **kwargs) for keys, kwargs in definitions]
            )
        except asyncio.CancelledError:
            raise
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
            "_kind": "message",
            "scope_discriminator": {"$type": "string"},
        }
        required = {
            "history_scoped_message_unique": {
                "keys": (
                    ("scope_discriminator", 1),
                    ("session_id", 1),
                    ("stable_message_id", 1),
                ),
                "unique": True,
                "partial": partial,
            },
            "history_scoped_sequence": {
                "keys": (
                    ("scope_discriminator", 1),
                    ("session_id", 1),
                    ("sequence", 1),
                ),
                "unique": True,
                "partial": partial,
            },
        }
        for name, expected in required.items():
            index = by_name.get(name)
            if index is None:
                raise MongoDBIndexMissingError(
                    f"Regular index '{name}' does not exist; create it explicitly."
                )
            if _index_keys(index) != expected["keys"]:
                raise MongoDBIndexMismatchError(
                    f"Regular index '{name}' has incompatible keys or key order; "
                    "recreate it with ensure_indexes()."
                )
            if index.get("unique") is not expected["unique"]:
                raise MongoDBIndexMismatchError(
                    f"Regular index '{name}' has an incompatible unique flag; "
                    "recreate it with ensure_indexes()."
                )
            if index.get("partialFilterExpression") != expected["partial"]:
                raise MongoDBIndexMismatchError(
                    f"Regular index '{name}' has an incompatible "
                    "partialFilterExpression; recreate it with ensure_indexes()."
                )
        if self.options.retention is not None:
            ttl = by_name.get("history_expiration_ttl")
            if ttl is None:
                raise MongoDBIndexMissingError(
                    "Regular index 'history_expiration_ttl' does not exist; create it explicitly."
                )
            if _index_keys(ttl) != (("expires_at", 1),):
                raise MongoDBIndexMismatchError(
                    "Regular index 'history_expiration_ttl' has incompatible keys or key order; "
                    "recreate it with ensure_indexes()."
                )
            if ttl.get("unique", False) is not False:
                raise MongoDBIndexMismatchError(
                    "Regular index 'history_expiration_ttl' must not be unique; "
                    "recreate it with ensure_indexes()."
                )
            if ttl.get("partialFilterExpression") != partial:
                raise MongoDBIndexMismatchError(
                    "Regular index 'history_expiration_ttl' has an incompatible "
                    "partialFilterExpression; recreate it with ensure_indexes()."
                )
            if ttl.get("expireAfterSeconds") != 0:
                raise MongoDBIndexMismatchError(
                    "Regular index 'history_expiration_ttl' has an incompatible "
                    "expireAfterSeconds value; recreate it with ensure_indexes()."
                )

    async def close(self) -> None:
        """Close only a MongoDB client created by this provider."""
        if self._client_handle is not None:
            await self._client_handle.close()

    async def __aenter__(self) -> MongoDBHistoryProvider:
        return self

    async def __aexit__(
        self,
        exc_type: type[BaseException] | None,
        exc_value: BaseException | None,
        traceback: TracebackType | None,
    ) -> None:
        await self.close()


async def _with_timeout(
    awaitable: Awaitable[_T],
    timeout: float | None,
    *,
    operation: str,
) -> _T:
    try:
        return await asyncio.wait_for(awaitable, timeout=timeout)
    except asyncio.TimeoutError as exc:
        raise MongoDBTimeoutError(f"MongoDB History {operation} deadline exceeded.") from exc


def _serialize_message(message: Message) -> MongoDocument:
    try:
        value = json.loads(message.to_json())
    except (TypeError, ValueError) as exc:
        raise MongoDBMappingError(
            "Agent Framework Message could not be serialized losslessly."
        ) from exc
    if not isinstance(value, dict):
        raise MongoDBMappingError(
            "Agent Framework Message serialization returned an invalid payload."
        )
    return cast(MongoDocument, value)


def _message_from_document(document: Mapping[str, Any]) -> Message:
    schema_version = document.get("schema_version")
    if schema_version != MongoDBHistoryProvider.SCHEMA_VERSION:
        raise MongoDBMappingError(
            f"Unsupported History schema version {schema_version!r}; "
            "run a supported history migration "
            "before replay."
        )
    framework_version = document.get("framework_version")
    if framework_version != MongoDBHistoryProvider.FRAMEWORK_SERIALIZATION_VERSION:
        raise MongoDBMappingError(
            f"Unsupported framework serialization version {framework_version!r}; "
            "migrate the stored "
            "Message payload before replay."
        )
    payload = document.get("message")
    if not isinstance(payload, Mapping):
        raise MongoDBMappingError(
            "Stored History message payload is missing or invalid; migration is required."
        )
    try:
        return Message.from_dict(dict(cast(Mapping[str, Any], payload)))
    except (TypeError, ValueError, KeyError) as exc:
        raise MongoDBMappingError(
            "Stored History message payload is incompatible; run a supported migration."
        ) from exc


def _stable_message_id(
    message: Message,
    scope: Mapping[str, Any],
    ordinal: int,
    retry_ids: dict[str, Any],
) -> str:
    if message.message_id:
        return message.message_id
    key = _canonical_hash(
        {
            "ordinal": ordinal,
            "scope": dict(scope),
            "message": _serialize_message(message),
        }
    )
    existing = retry_ids.get(key)
    message_id = existing if isinstance(existing, str) else str(uuid.uuid4())
    retry_ids[key] = message_id
    return message_id


def _history_batch_fingerprint(
    messages: Sequence[Message],
    scope: Mapping[str, Any],
) -> str:
    return _canonical_hash(
        {
            "scope": dict(scope),
            "messages": [_serialize_message(message) for message in messages],
        }
    )


def _begin_history_retry_attempt(
    state: dict[str, Any],
    batch_fingerprint: str,
    active_attempts: set[str],
    *,
    token_hint: str | None,
) -> tuple[str, dict[str, Any]]:
    legacy = state.get("mongodb_history_pending_ids")
    if legacy:
        raise _invalid_history_retry_state(
            "legacy mongodb_history_pending_ids cannot distinguish completed turns; "
            "clear it after migration review"
        )
    if legacy is not None:
        state.pop("mongodb_history_pending_ids", None)
    batches = _normalize_history_retry_state(state, active_attempts)
    batch = batches.setdefault(
        batch_fingerprint,
        {"failed": [], "in_flight": {}},
    )
    failed = cast(list[Any], batch["failed"])
    in_flight = cast(dict[str, Any], batch["in_flight"])
    attempt: dict[str, Any]
    if failed:
        attempt = cast(dict[str, Any], failed.pop(0))
    else:
        attempt = {"token": token_hint or str(uuid.uuid4()), "ids": {}}
    attempt_id = str(uuid.uuid4())
    in_flight[attempt_id] = attempt
    active_attempts.add(attempt_id)
    return attempt_id, attempt


def _normalize_history_retry_state(
    state: dict[str, Any],
    active_attempts: set[str],
) -> dict[str, dict[str, Any]]:
    value = state.get("mongodb_history_pending_batches")
    if value is None:
        envelope: dict[str, Any] = {"version": 1, "batches": {}}
        state["mongodb_history_pending_batches"] = envelope
    elif not isinstance(value, dict):
        raise _invalid_history_retry_state("state envelope must be a mapping")
    else:
        envelope = cast(dict[str, Any], value)
    if set(envelope) != {"version", "batches"} or envelope.get("version") != 1:
        raise _invalid_history_retry_state("state envelope version or fields are unsupported")
    batches_value = envelope.get("batches")
    if not isinstance(batches_value, dict):
        raise _invalid_history_retry_state("batches must be a mapping")
    raw_batches = cast(dict[object, object], batches_value)
    batches = cast(dict[str, dict[str, Any]], batches_value)
    for fingerprint, batch_value in raw_batches.items():
        if not isinstance(fingerprint, str) or not fingerprint or not isinstance(batch_value, dict):
            raise _invalid_history_retry_state("batch fingerprints and values are invalid")
        batch = cast(dict[str, Any], batch_value)
        if set(batch) != {"failed", "in_flight"}:
            raise _invalid_history_retry_state("batch fields are unsupported")
        failed_value = batch.get("failed")
        in_flight_value = batch.get("in_flight")
        if not isinstance(failed_value, list) or not isinstance(in_flight_value, dict):
            raise _invalid_history_retry_state("attempt containers are invalid")
        failed = cast(list[Any], failed_value)  # type: ignore[redundant-cast]
        in_flight = cast(dict[str, Any], in_flight_value)
        if not all(_is_history_retry_attempt(attempt) for attempt in failed):
            raise _invalid_history_retry_state("failed attempts are invalid")
        if not all(
            isinstance(attempt_id, str) and bool(attempt_id) and _is_history_retry_attempt(attempt)
            for attempt_id, attempt in cast(dict[object, object], in_flight_value).items()
        ):
            raise _invalid_history_retry_state("in-flight attempts are invalid")
        for attempt_id, attempt in list(in_flight.items()):
            if attempt_id not in active_attempts:
                failed.append(attempt)
                in_flight.pop(attempt_id)
    return batches


def _is_history_retry_attempt(value: object) -> bool:
    if not isinstance(value, dict):
        return False
    raw_value = cast(dict[object, object], value)
    if set(raw_value) != {"token", "ids"}:
        return False
    attempt = cast(dict[str, object], value)
    token = attempt.get("token")
    ids = attempt.get("ids")
    return (
        isinstance(token, str)
        and bool(token)
        and isinstance(ids, dict)
        and all(
            isinstance(key, str) and bool(key) and isinstance(message_id, str) and bool(message_id)
            for key, message_id in cast(dict[object, object], ids).items()
        )
    )


def _finish_history_retry_attempt(
    state: dict[str, Any],
    batch_fingerprint: str,
    retry_attempt: tuple[str, dict[str, Any]],
    active_attempts: set[str],
    *,
    succeeded: bool,
) -> None:
    attempt_id, attempt = retry_attempt
    active_attempts.discard(attempt_id)
    envelope_value = state.get("mongodb_history_pending_batches")
    if not isinstance(envelope_value, dict):
        return
    envelope = cast(dict[str, Any], envelope_value)
    batches_value = envelope.get("batches")
    if not isinstance(batches_value, dict):
        return
    batches = cast(dict[str, Any], batches_value)
    batch_value = batches.get(batch_fingerprint)
    if not isinstance(batch_value, dict):
        return
    batch = cast(dict[str, Any], batch_value)
    failed = cast(list[Any], batch.get("failed"))
    in_flight = cast(dict[str, Any], batch.get("in_flight"))
    in_flight.pop(attempt_id, None)
    if not succeeded:
        failed.append(attempt)
    if not failed and not in_flight:
        batches.pop(batch_fingerprint, None)
    if not batches:
        state.pop("mongodb_history_pending_batches", None)


def _invalid_history_retry_state(detail: str) -> MongoDBConfigurationError:
    return MongoDBConfigurationError(
        "History provider retry state is invalid and requires migration: "
        f"{detail}. Clear the affected retry state or restore a supported state version."
    )


def _document_id(scope: Mapping[str, Any], message_id: str) -> str:
    return _canonical_hash({"kind": "message", "scope": dict(scope), "message_id": message_id})


def _counter_id(scope: Mapping[str, Any]) -> str:
    return f"history-sequence:{_canonical_hash(dict(scope))}"


def _reservation_id(scope: Mapping[str, Any], token: str) -> str:
    return f"history-reservation:{_canonical_hash({'scope': dict(scope), 'token': token})}"


def _validate_reservation(document: Mapping[str, Any], expected_count: int) -> int:
    if (
        document.get("schema_version") != MongoDBHistoryProvider.SCHEMA_VERSION
        or document.get("framework_version")
        != MongoDBHistoryProvider.FRAMEWORK_SERIALIZATION_VERSION
        or document.get("count") != expected_count
        or not isinstance(document.get("first_sequence"), int)
    ):
        raise MongoDBPersistenceError(
            "Stored History sequence reservation is incompatible; "
            "clear the authorized session reservation after migration review."
        )
    return cast(int, document["first_sequence"])


def _canonical_hash(value: object) -> str:
    return hashlib.sha256(
        json.dumps(
            value, allow_nan=False, ensure_ascii=False, separators=(",", ":"), sort_keys=True
        ).encode()
    ).hexdigest()


def _validate_duplicate(
    existing: Mapping[str, Any],
    candidate: Mapping[str, Any],
    *,
    include_sequence: bool = False,
) -> None:
    for field in (
        "schema_version",
        "framework_version",
        "stable_message_id",
        "message_id",
        "message",
    ):
        if existing.get(field) != candidate.get(field):
            raise MongoDBPersistenceError(
                "A duplicate History message identity contains incompatible stored data."
            )
    if include_sequence and existing.get("sequence") != candidate.get("sequence"):
        raise MongoDBPersistenceError(
            "A duplicate History message identity has an incompatible sequence; "
            "retry with the original sequence reservation."
        )


def _index_keys(index: Mapping[str, Any]) -> tuple[tuple[str, int], ...]:
    key = index.get("key", {})
    if not isinstance(key, Mapping):
        return ()
    typed_key = cast(Mapping[str, object], key)
    return tuple(
        (name, direction) for name, direction in typed_key.items() if isinstance(direction, int)
    )


def _error_category(error: PyMongoError, operation: str) -> str:
    translated = _translate_mongo_error(error, operation)
    return translated.__class__.__name__


def _translate_mongo_error(error: PyMongoError, operation: str) -> Exception:
    if isinstance(error, OperationFailure) and error.code in {13, 18}:
        return MongoDBAuthorizationError("MongoDB authorization failed.")
    transient = isinstance(error, (ConnectionFailure, ServerSelectionTimeoutError))
    if operation == "retrieval":
        if transient:
            return MongoDBTransientRetrievalError("MongoDB History retrieval failed transiently.")
        return MongoDBRetrievalError("MongoDB History retrieval failed.")
    if transient:
        return MongoDBTransientPersistenceError("MongoDB History persistence failed transiently.")
    return MongoDBPersistenceError("MongoDB History persistence failed.")


def _log_success(operation: str, started: float, count: int) -> None:
    _LOGGER.info(
        "MongoDB History operation completed",
        extra={
            "feature": "history",
            "operation": operation,
            "outcome": "success",
            "result_count": count,
            "duration_ms": round((time.monotonic() - started) * 1000),
        },
    )


def _log_failure(operation: str, started: float, category: str) -> None:
    _LOGGER.warning(
        "MongoDB History operation failed",
        extra={
            "feature": "history",
            "operation": operation,
            "outcome": "failed",
            "error_category": category,
            "duration_ms": round((time.monotonic() - started) * 1000),
        },
    )
