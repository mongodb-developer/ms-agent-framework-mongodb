from collections.abc import Awaitable, Sequence
from typing import Any, cast
from unittest.mock import patch

import pytest
from agent_framework import ContextProvider, GeneratedEmbeddings

from agent_framework_mongodb import (
    MongoDBConfigurationError,
    MongoDBMemoryContextProvider,
)


class FakeEmbeddingGenerator:
    additional_properties: dict[str, Any] = {}

    def get_embeddings(
        self,
        values: Sequence[str],
        *,
        options: Any | None = None,
    ) -> Awaitable[GeneratedEmbeddings[list[float], Any]]:
        raise AssertionError("Construction must not generate embeddings.")


class FakeCollection:
    def __bool__(self) -> bool:
        raise AssertionError("MongoDB collections must not be truth tested.")


class FakeDatabase:
    def __init__(self, collection: FakeCollection) -> None:
        self.collection = collection
        self.requested_collection: str | None = None

    def __getitem__(self, name: str) -> FakeCollection:
        self.requested_collection = name
        return self.collection


class FakeClient:
    def __init__(self) -> None:
        self.collection = FakeCollection()
        self.database = FakeDatabase(self.collection)
        self.requested_database: str | None = None
        self.close_count = 0

    def __getitem__(self, name: str) -> FakeDatabase:
        self.requested_database = name
        return self.database

    def close(self) -> None:
        self.close_count += 1


def create_provider(**kwargs: Any) -> MongoDBMemoryContextProvider:
    options: dict[str, Any] = {
        "vector_dimensions": 3,
        "user_id": "user-1",
        "collection": cast(Any, FakeCollection()),
    }
    options.update(kwargs)
    return MongoDBMemoryContextProvider(
        FakeEmbeddingGenerator(),
        **options,
    )


def test_provider_uses_public_context_provider_contract() -> None:
    assert issubclass(MongoDBMemoryContextProvider, ContextProvider)


def test_injected_collection_is_retained_without_truth_testing() -> None:
    collection = FakeCollection()

    provider = MongoDBMemoryContextProvider(
        FakeEmbeddingGenerator(),
        vector_dimensions=3,
        user_id="user-1",
        collection=cast(Any, collection),
    )

    assert provider.collection is collection
    assert provider.owns_client is False


@pytest.mark.parametrize(
    ("kwargs", "message"),
    [
        ({"vector_dimensions": 0}, "positive integer"),
        ({"source_id": " "}, "source_id"),
        ({"database_name": ""}, "database_name"),
        ({"collection_name": ""}, "collection_name"),
        ({"index_name": ""}, "index_name"),
        ({"user_id": " "}, "user_id"),
    ],
)
def test_invalid_options_fail_before_mongodb_access(
    kwargs: dict[str, Any],
    message: str,
) -> None:
    with pytest.raises(MongoDBConfigurationError, match=message):
        create_provider(**kwargs)


def test_provider_requires_a_durable_scope() -> None:
    with pytest.raises(MongoDBConfigurationError, match="At least one"):
        MongoDBMemoryContextProvider(
            FakeEmbeddingGenerator(),
            vector_dimensions=3,
            collection=cast(Any, FakeCollection()),
        )


def test_collection_and_client_are_mutually_exclusive() -> None:
    with pytest.raises(MongoDBConfigurationError, match="either collection or mongo_client"):
        MongoDBMemoryContextProvider(
            FakeEmbeddingGenerator(),
            vector_dimensions=3,
            user_id="user-1",
            mongo_client=cast(Any, FakeClient()),
            collection=cast(Any, FakeCollection()),
        )


async def test_injected_client_is_used_but_not_closed() -> None:
    client = FakeClient()
    provider = MongoDBMemoryContextProvider(
        FakeEmbeddingGenerator(),
        database_name="memory_db",
        collection_name="memory_docs",
        vector_dimensions=3,
        user_id="user-1",
        mongo_client=cast(Any, client),
    )

    await provider.close()

    assert provider.collection is client.collection
    assert client.requested_database == "memory_db"
    assert client.database.requested_collection == "memory_docs"
    assert provider.owns_client is False
    assert client.close_count == 0


async def test_provider_created_client_is_closed_once() -> None:
    client = FakeClient()
    with patch(
        "agent_framework_mongodb._shared.client.AsyncMongoClient",
        return_value=client,
    ):
        provider = MongoDBMemoryContextProvider(
            FakeEmbeddingGenerator(),
            connection_string="mongodb://example",
            vector_dimensions=3,
            user_id="user-1",
        )

    await provider.close()
    await provider.close()

    assert provider.owns_client is True
    assert client.close_count == 1
