from __future__ import annotations

import asyncio
from collections.abc import Awaitable, Sequence
from typing import Any

import pytest
from agent_framework import Embedding, GeneratedEmbeddings

from agent_framework_mongodb import (
    MongoDBIndexFailedError,
    MongoDBIndexMismatchError,
    MongoDBIndexNotReadyError,
    MongoDBIndexState,
    MongoDBMemoryContextProvider,
    MongoDBRAGContextProvider,
    MongoDBRAGProvider,
    MongoDBRAGProviderOptions,
    MongoDBRegularIndexDefinition,
    MongoDBSearchMode,
)


class Embeddings:
    additional_properties: dict[str, Any] = {}

    def get_embeddings(
        self, values: Sequence[str], *, options: Any | None = None
    ) -> Awaitable[GeneratedEmbeddings[list[float], Any]]:
        del options

        async def generate() -> GeneratedEmbeddings[list[float], Any]:
            return GeneratedEmbeddings([Embedding(vector=[1.0, 0.0, 0.5]) for _ in values])

        return generate()


class Cursor:
    def __init__(self, documents: list[dict[str, Any]]) -> None:
        self.documents = documents

    async def to_list(self, *, length: int | None) -> list[dict[str, Any]]:
        return self.documents if length is None else self.documents[:length]


class Collection:
    def __init__(self) -> None:
        self.search_indexes: list[dict[str, Any]] = []
        self.regular_indexes: list[dict[str, Any]] = [{"name": "_id_", "key": {"_id": 1}}]
        self.created: list[Any] = []
        self.updated: list[tuple[str, dict[str, Any]]] = []
        self.dropped_search: list[str] = []
        self.dropped_regular: list[str] = []

    async def list_search_indexes(self, *, name: str | None = None) -> Cursor:
        documents = self.search_indexes
        if name is not None:
            documents = [item for item in documents if item["name"] == name]
        return Cursor(documents)

    async def create_search_index(self, model: Any) -> str:
        self.created.append(model)
        document = model.document
        self.search_indexes.append(
            {
                "name": document["name"],
                "type": document.get("type", "search"),
                "status": "BUILDING",
                "queryable": False,
                "latestDefinition": document["definition"],
            }
        )
        return str(document["name"])

    async def update_search_index(self, name: str, definition: dict[str, Any]) -> None:
        self.updated.append((name, definition))

    async def drop_search_index(self, name: str) -> None:
        self.dropped_search.append(name)
        self.search_indexes = [item for item in self.search_indexes if item["name"] != name]

    async def list_indexes(self) -> Cursor:
        return Cursor(self.regular_indexes)

    async def create_index(self, keys: Any, **kwargs: Any) -> str:
        self.regular_indexes.append({"name": kwargs["name"], "key": dict(keys), **kwargs})
        return str(kwargs["name"])

    async def drop_index(self, name: str) -> None:
        self.dropped_regular.append(name)
        self.regular_indexes = [item for item in self.regular_indexes if item["name"] != name]


def memory(collection: Collection) -> MongoDBMemoryContextProvider:
    return MongoDBMemoryContextProvider(
        Embeddings(),
        vector_dimensions=3,
        application_id="app",
        retention=__import__("datetime").timedelta(days=7),
        collection=collection,  # type: ignore[arg-type]
    )


def rag(collection: Collection) -> MongoDBRAGContextProvider:
    direct = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.HYBRID_RRF,
            vector_dimensions=3,
            vector_index_name="knowledge_vector",
            search_index_name="knowledge_search",
        ),
        embedding_generator=Embeddings(),
        collection=collection,  # type: ignore[arg-type]
    )
    return MongoDBRAGContextProvider(direct)


async def test_memory_vector_facade_reports_building_then_ready_and_drops_explicitly() -> None:
    collection = Collection()
    provider = memory(collection)

    accepted = await provider.create_vector_search_index()
    assert accepted.state is MongoDBIndexState.BUILDING
    assert accepted.queryable is False

    collection.search_indexes[0]["status"] = "READY"
    collection.search_indexes[0]["queryable"] = True
    ready = await provider.wait_until_vector_search_index_ready(timeout=0.1, poll_interval=0.01)
    assert ready.state is MongoDBIndexState.READY
    assert ready.definition.name == "agent_framework_memory"

    await provider.drop_vector_search_index()
    assert collection.dropped_search == ["agent_framework_memory"]


async def test_memory_regular_compound_and_ttl_indexes_are_separate_and_validated() -> None:
    collection = Collection()
    provider = memory(collection)

    created = await provider.create_regular_indexes()
    assert [result.definition.name for result in created] == [
        "memory_scope_admin",
        "memory_expiration_ttl",
    ]
    validated = await provider.validate_regular_indexes()
    assert len(validated) == 2
    assert isinstance(validated[1].definition, MongoDBRegularIndexDefinition)
    assert validated[1].definition.expire_after_seconds == 0

    collection.regular_indexes[-1]["expireAfterSeconds"] = 60
    with pytest.raises(MongoDBIndexMismatchError, match="memory_expiration_ttl"):
        await provider.validate_regular_indexes()

    repaired = await provider.update_regular_index("memory_expiration_ttl")
    assert isinstance(repaired.definition, MongoDBRegularIndexDefinition)
    assert repaired.definition.expire_after_seconds == 0
    await provider.drop_regular_index("memory_expiration_ttl")
    assert collection.dropped_regular == [
        "memory_expiration_ttl",
        "memory_expiration_ttl",
    ]


async def test_rag_context_facade_manages_vector_and_search_indexes_independently() -> None:
    collection = Collection()
    provider = rag(collection)

    vector = await provider.create_vector_search_index()
    search = await provider.create_search_index()
    assert vector.definition.index_type == "vectorSearch"
    assert search.definition.index_type == "search"
    assert len(await provider.list_indexes()) == 2

    await provider.update_vector_search_index()
    await provider.update_search_index()
    assert [name for name, _ in collection.updated] == [
        "knowledge_vector",
        "knowledge_search",
    ]

    await provider.drop_vector_search_index()
    await provider.drop_search_index()
    assert collection.dropped_search == ["knowledge_vector", "knowledge_search"]


async def test_failed_index_is_not_automatically_retried_or_updated() -> None:
    collection = Collection()
    provider = rag(collection)
    await provider.create_vector_search_index()
    collection.search_indexes[0]["status"] = "FAILED"

    with pytest.raises(MongoDBIndexFailedError, match="explicitly update, drop, or recreate"):
        await provider.ensure_vector_search_index()
    assert collection.updated == []


async def test_wait_distinguishes_ready_not_queryable_and_timeout() -> None:
    collection = Collection()
    provider = rag(collection)
    await provider.create_vector_search_index()
    collection.search_indexes[0]["status"] = "READY"

    inspected = await provider.inspect_vector_search_index()
    assert inspected.state is MongoDBIndexState.READY_NOT_QUERYABLE
    with pytest.raises(MongoDBIndexNotReadyError, match="READY_NOT_QUERYABLE.*remediation"):
        await provider.wait_until_vector_search_index_ready(timeout=0.01, poll_interval=0.005)


async def test_cancellation_propagates_from_polling_delay() -> None:
    collection = Collection()
    provider = rag(collection)
    await provider.create_search_index()

    task = asyncio.create_task(provider.wait_until_search_index_ready(timeout=10, poll_interval=10))
    await asyncio.sleep(0)
    task.cancel()
    with pytest.raises(asyncio.CancelledError):
        await task
