from __future__ import annotations

import asyncio
import copy
from datetime import datetime, timedelta, timezone
from typing import Any, cast
from unittest.mock import patch

import pytest
from agent_framework import AgentSession, SessionStore, register_state_type
from pymongo import ASCENDING
from pymongo.errors import ConnectionFailure, DuplicateKeyError

from agent_framework_mongodb import (
    MongoDBConcurrencyError,
    MongoDBIndexMismatchError,
    MongoDBIndexMissingError,
    MongoDBMappingError,
    MongoDBSessionStore,
    MongoDBSessionStoreOptions,
    MongoDBTransientPersistenceError,
    MongoDBTransientRetrievalError,
)
from agent_framework_mongodb._shared.client import MongoClientHandle


class ProviderState:
    def __init__(self, counter: int) -> None:
        self.counter = counter

    def to_dict(self) -> dict[str, Any]:
        return {"counter": self.counter}

    @classmethod
    def from_dict(cls, data: dict[str, Any]) -> ProviderState:
        return cls(cast(int, data["counter"]))


register_state_type(
    ProviderState,
    type_id="agent-framework-mongodb.tests.session-store-provider-state",
)


class Result:
    def __init__(self, *, matched_count: int = 0, deleted_count: int = 0) -> None:
        self.matched_count = matched_count
        self.deleted_count = deleted_count


class FakeCollection:
    def __init__(self) -> None:
        self.documents: list[dict[str, Any]] = []
        self.deleted_filters: list[dict[str, Any]] = []
        self.created_indexes: list[tuple[Any, dict[str, Any]]] = []
        self.regular_indexes: list[dict[str, Any]] = []
        self.fail_reads = False
        self.fail_writes = False
        self.cancel_writes = False

    async def find_one(self, query: dict[str, Any]) -> dict[str, Any] | None:
        if self.fail_reads:
            raise ConnectionFailure("private-host.invalid")
        document = next(
            (document for document in self.documents if matches_query(document, query)),
            None,
        )
        return copy.deepcopy(document)

    async def insert_one(self, document: dict[str, Any]) -> Result:
        if self.fail_writes:
            raise ConnectionFailure("private-host.invalid")
        if any(item["_id"] == document["_id"] for item in self.documents):
            raise DuplicateKeyError("duplicate")
        self.documents.append(copy.deepcopy(document))
        return Result()

    async def replace_one(
        self,
        query: dict[str, Any],
        replacement: dict[str, Any],
        *,
        upsert: bool = False,
    ) -> Result:
        del upsert
        if self.cancel_writes:
            raise asyncio.CancelledError
        if self.fail_writes:
            raise ConnectionFailure("private-host.invalid")
        for index, document in enumerate(self.documents):
            if matches_query(document, query):
                self.documents[index] = copy.deepcopy(replacement)
                return Result(matched_count=1)
        return Result()

    async def delete_one(self, query: dict[str, Any]) -> Result:
        if self.fail_writes:
            raise ConnectionFailure("private-host.invalid")
        self.deleted_filters.append(copy.deepcopy(query))
        for index, document in enumerate(self.documents):
            if matches_query(document, query):
                del self.documents[index]
                return Result(deleted_count=1)
        return Result()

    async def create_index(self, keys: Any, **kwargs: Any) -> str:
        self.created_indexes.append((keys, kwargs))
        return cast(str, kwargs["name"])

    async def list_indexes(self) -> FakeIndexCursor:
        return FakeIndexCursor(copy.deepcopy(self.regular_indexes))


class FakeIndexCursor:
    def __init__(self, indexes: list[dict[str, Any]]) -> None:
        self.indexes = indexes

    async def to_list(self, *, length: int | None) -> list[dict[str, Any]]:
        del length
        return self.indexes


class FakeDatabase:
    def __init__(self, collection: FakeCollection) -> None:
        self.collection = collection

    def __getitem__(self, _name: str) -> FakeCollection:
        return self.collection


class FakeClient:
    def __init__(self) -> None:
        self.collection = FakeCollection()
        self.database = FakeDatabase(self.collection)
        self.close_count = 0

    def __getitem__(self, _name: str) -> FakeDatabase:
        return self.database

    def close(self) -> None:
        self.close_count += 1


def matches_query(document: dict[str, Any], query: dict[str, Any]) -> bool:
    return all(document.get(key) == value for key, value in query.items())


def options(**overrides: Any) -> MongoDBSessionStoreOptions:
    values: dict[str, Any] = {
        "tenant_id": "tenant-1",
        "application_id": "app-1",
        "agent_id": "agent-1",
    }
    values.update(overrides)
    return MongoDBSessionStoreOptions(**values)


def test_session_store_uses_public_framework_contract() -> None:
    store = MongoDBSessionStore(
        cast(Any, FakeCollection()),
        options=MongoDBSessionStoreOptions(
            tenant_id="tenant-1",
            application_id="app-1",
            agent_id="agent-1",
        ),
    )

    assert isinstance(store, SessionStore)


@pytest.mark.asyncio
async def test_set_and_get_round_trip_registered_provider_state() -> None:
    store = MongoDBSessionStore(cast(Any, FakeCollection()), options=options())
    session = AgentSession(session_id="framework-session")
    session.state["unknown-provider"] = ProviderState(counter=7)

    await store.set("store-key", session)
    loaded = await store.get("store-key")

    assert loaded is not session
    assert loaded is not None
    assert loaded.session_id == "framework-session"
    restored = loaded.state["unknown-provider"]
    assert isinstance(restored, ProviderState)
    assert restored.counter == 7


@pytest.mark.asyncio
async def test_create_and_compare_and_set_are_idempotent_and_detect_conflicts() -> None:
    store = MongoDBSessionStore(cast(Any, FakeCollection()), options=options())
    first = AgentSession(session_id="framework-session")
    first.state["turn"] = 1
    second = AgentSession(session_id="framework-session")
    second.state["turn"] = 2

    assert await store.create("store-key", first) == 1
    assert await store.create("store-key", first) == 1
    with pytest.raises(MongoDBConcurrencyError, match="already exists"):
        await store.create("store-key", second)

    assert await store.compare_and_set("store-key", second, expected_version=1) == 2
    assert await store.compare_and_set("store-key", second, expected_version=1) == 2
    with pytest.raises(MongoDBConcurrencyError, match="expected version 1"):
        await store.compare_and_set("store-key", first, expected_version=1)

    versioned = await store.get_versioned("store-key")
    assert versioned is not None
    assert versioned.version == 2
    assert versioned.session.state == {"turn": 2}


@pytest.mark.asyncio
async def test_compare_and_delete_requires_scope_and_expected_version() -> None:
    collection = FakeCollection()
    store = MongoDBSessionStore(cast(Any, collection), options=options())
    await store.create("store-key", AgentSession())

    with pytest.raises(MongoDBConcurrencyError, match="expected version 2"):
        await store.compare_and_delete("store-key", expected_version=2)
    assert await store.compare_and_delete("store-key", expected_version=1)
    assert not await store.compare_and_delete("store-key", expected_version=1)

    delete_filter = collection.deleted_filters[-1]
    assert delete_filter["_id"]
    assert delete_filter["_kind"] == "agent_session"
    assert delete_filter["tenant_id"] == "tenant-1"
    assert delete_filter["application_id"] == "app-1"
    assert delete_filter["agent_id"] == "agent-1"
    assert delete_filter["session_id"] == "store-key"
    assert delete_filter["version"] == 1


@pytest.mark.asyncio
async def test_all_operations_isolate_authorization_scopes() -> None:
    collection = FakeCollection()
    tenant_one = MongoDBSessionStore(
        cast(Any, collection),
        options=options(tenant_id="tenant-1"),
    )
    tenant_two = MongoDBSessionStore(
        cast(Any, collection),
        options=options(tenant_id="tenant-2"),
    )

    await tenant_one.set("same-key", AgentSession(session_id="one"))
    assert await tenant_two.get("same-key") is None
    await tenant_two.set("same-key", AgentSession(session_id="two"))
    await tenant_one.delete("same-key")

    loaded = await tenant_two.get("same-key")
    assert loaded is not None
    assert loaded.session_id == "two"
    assert len(collection.documents) == 1


@pytest.mark.asyncio
async def test_expiration_is_utc_and_versions_are_migration_gated() -> None:
    collection = FakeCollection()
    store = MongoDBSessionStore(
        cast(Any, collection),
        options=options(ttl=timedelta(hours=1)),
    )
    explicit_expiry = datetime(2030, 1, 2, 3, 4, tzinfo=timezone(timedelta(hours=-5)))

    await store.create("store-key", AgentSession(), expires_at=explicit_expiry)
    assert collection.documents[0]["expires_at"] == datetime(2030, 1, 2, 8, 4, tzinfo=timezone.utc)

    collection.documents[0]["schema_version"] = 999
    with pytest.raises(MongoDBMappingError, match="migrate"):
        await store.get("store-key")
    with pytest.raises(MongoDBMappingError, match="migrate"):
        await store.create("store-key", AgentSession())
    collection.documents[0]["schema_version"] = 1
    collection.documents[0]["framework_version"] = "future"
    with pytest.raises(MongoDBMappingError, match="supported Agent Framework"):
        await store.get("store-key")


@pytest.mark.asyncio
async def test_regular_index_provisioning_is_explicit_and_includes_ttl() -> None:
    collection = FakeCollection()
    store = MongoDBSessionStore(
        cast(Any, collection),
        options=options(ttl=timedelta(days=7)),
    )

    assert collection.created_indexes == []
    assert await store.ensure_indexes() == (
        "session_store_scope_identity",
        "session_store_scope_version",
        "session_store_expiration",
    )
    assert collection.created_indexes == [
        (
            [("scope_discriminator", ASCENDING), ("session_id", ASCENDING)],
            {
                "name": "session_store_scope_identity",
                "unique": True,
                "collation": {"locale": "simple"},
                "partialFilterExpression": {
                    "_kind": "agent_session",
                    "scope_discriminator": {"$type": "string"},
                },
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
                "partialFilterExpression": {
                    "_kind": "agent_session",
                    "scope_discriminator": {"$type": "string"},
                },
            },
        ),
        (
            [("expires_at", ASCENDING)],
            {
                "name": "session_store_expiration",
                "expireAfterSeconds": 0,
                "partialFilterExpression": {
                    "_kind": "agent_session",
                    "scope_discriminator": {"$type": "string"},
                },
            },
        ),
    ]

    with pytest.raises(MongoDBIndexMissingError, match="scope_identity"):
        await store.validate_indexes()
    collection.regular_indexes = [
        {
            "name": kwargs["name"],
            "key": dict(keys),
            **{key: value for key, value in kwargs.items() if key != "name"},
        }
        for keys, kwargs in collection.created_indexes
    ]
    await store.validate_indexes()
    collection.regular_indexes[0]["collation"] = {"locale": "en", "strength": 2}
    with pytest.raises(MongoDBIndexMismatchError, match="scope_identity"):
        await store.validate_indexes()
    collection.regular_indexes[0]["collation"] = {"locale": "simple"}
    collection.regular_indexes[1]["key"] = {"session_id": 1}
    with pytest.raises(MongoDBIndexMismatchError, match="scope_version"):
        await store.validate_indexes()


@pytest.mark.asyncio
async def test_driver_errors_are_typed_and_cancellation_propagates() -> None:
    collection = FakeCollection()
    store = MongoDBSessionStore(cast(Any, collection), options=options())
    collection.fail_reads = True
    with pytest.raises(MongoDBTransientRetrievalError) as read_error:
        await store.get("store-key")
    assert isinstance(read_error.value.__cause__, ConnectionFailure)

    collection.fail_reads = False
    collection.fail_writes = True
    with pytest.raises(MongoDBTransientPersistenceError) as write_error:
        await store.set("store-key", AgentSession())
    assert isinstance(write_error.value.__cause__, ConnectionFailure)

    collection.fail_writes = False
    await store.create("store-key", AgentSession())
    collection.cancel_writes = True
    with pytest.raises(asyncio.CancelledError):
        await store.compare_and_set("store-key", AgentSession(), expected_version=1)


def test_options_require_bounded_authorization_and_valid_ttl() -> None:
    with pytest.raises(ValueError, match="authorization scope"):
        MongoDBSessionStoreOptions()
    with pytest.raises(ValueError, match="ttl must be a positive duration"):
        options(ttl=timedelta(0))


@pytest.mark.asyncio
async def test_client_ownership_is_immutable_and_cleanup_is_idempotent() -> None:
    injected = FakeClient()
    injected_store = MongoDBSessionStore(
        options=options(),
        mongo_client=cast(Any, injected),
    )
    assert not injected_store.owns_client
    await injected_store.close()
    assert injected.close_count == 0

    created = FakeClient()
    with patch(
        "agent_framework_mongodb.session_store.store.MongoClientHandle.from_uri",
        return_value=MongoClientHandle(created, owns_client=True),
    ):
        owned_store = MongoDBSessionStore(options=options())
    assert owned_store.owns_client
    await owned_store.close()
    await owned_store.close()
    assert created.close_count == 1
