from __future__ import annotations

import os
import uuid
from collections.abc import Awaitable, Sequence
from typing import Any

import pytest
from agent_framework import Embedding, GeneratedEmbeddings, Message
from pymongo import AsyncMongoClient

from agent_framework_mongodb import MongoDBMemoryContextProvider

pytestmark = pytest.mark.integration_memory


class IntegrationEmbeddingGenerator:
    additional_properties: dict[str, Any] = {}

    async def _generate(self, values: Sequence[str]) -> GeneratedEmbeddings[list[float], Any]:
        vectors = [
            [1.0, 0.0, 0.0] if "blue" in value.lower() else [0.0, 1.0, 0.0] for value in values
        ]
        return GeneratedEmbeddings([Embedding(vector=vector) for vector in vectors])

    def get_embeddings(
        self,
        values: Sequence[str],
        *,
        options: Any | None = None,
    ) -> Awaitable[GeneratedEmbeddings[list[float], Any]]:
        del options
        return self._generate(values)


@pytest.fixture
def mongodb_settings() -> tuple[str, str]:
    uri = os.getenv("MONGODB_URI")
    database = os.getenv("MONGODB_DATABASE")
    if not uri or not database:
        pytest.skip("MONGODB_URI and MONGODB_DATABASE are required for integration-memory tests")
    return uri, database


async def test_memory_storage_retrieval_and_targeted_cleanup(
    mongodb_settings: tuple[str, str],
) -> None:
    uri, database_name = mongodb_settings
    collection_name = f"af_memory_test_{uuid.uuid4().hex}"
    client: AsyncMongoClient[dict[str, Any]] = AsyncMongoClient(uri)
    provider = MongoDBMemoryContextProvider(
        IntegrationEmbeddingGenerator(),
        mongo_client=client,
        database_name=database_name,
        collection_name=collection_name,
        vector_dimensions=3,
        application_id="integration-memory",
        user_id="user-a",
        num_candidates=10,
    )
    try:
        await provider.store(
            [Message("user", ["Remember that blue is preferred."], message_id="fixture-1")],
            session_id="session-a",
        )
        await provider.ensure_vector_search_index(wait_until_ready=True, timeout=120)
        results = await provider.search("blue", exact=True)
        assert [message.text for message in results] == ["Remember that blue is preferred."]
        assert await provider.clear_session("session-a") == 1
    finally:
        assert collection_name.startswith("af_memory_test_")
        await client[database_name].drop_collection(collection_name)
        await provider.close()
        await client.close()
