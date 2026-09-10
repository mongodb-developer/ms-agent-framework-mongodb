from __future__ import annotations

import os
import uuid
from collections.abc import Awaitable, Sequence
from typing import Any

import pytest
from agent_framework import Embedding, GeneratedEmbeddings
from pymongo import AsyncMongoClient

from agent_framework_mongodb import (
    MongoDBIndexState,
    MongoDBMemoryContextProvider,
    MongoDBRAGContextProvider,
    MongoDBRAGProvider,
    MongoDBRAGProviderOptions,
    MongoDBSearchMode,
)

pytestmark = pytest.mark.integration_indexing


class Embeddings:
    additional_properties: dict[str, Any] = {}

    def get_embeddings(
        self, values: Sequence[str], *, options: Any | None = None
    ) -> Awaitable[GeneratedEmbeddings[list[float], Any]]:
        del options

        async def generate() -> GeneratedEmbeddings[list[float], Any]:
            return GeneratedEmbeddings([Embedding(vector=[1.0, 0.0, 0.0]) for _ in values])

        return generate()


async def test_public_facades_manage_real_indexes_with_targeted_cleanup() -> None:
    uri = os.getenv("MONGODB_URI")
    database_name = os.getenv("MONGODB_DATABASE")
    if not uri or not database_name:
        pytest.skip("MONGODB_URI and MONGODB_DATABASE are required for indexing integration tests")

    unique = uuid.uuid4().hex
    collection_name = f"af_index_test_{unique}"
    client: AsyncMongoClient[dict[str, Any]] = AsyncMongoClient(uri)
    collection = client[database_name][collection_name]
    memory = MongoDBMemoryContextProvider(
        Embeddings(),
        vector_dimensions=3,
        application_id="index-integration",
        index_name=f"af_index_test_memory_{unique}",
        collection=collection,
    )
    rag = MongoDBRAGContextProvider(
        MongoDBRAGProvider(
            MongoDBRAGProviderOptions(
                mode=MongoDBSearchMode.HYBRID_RRF,
                vector_dimensions=3,
                vector_index_name=f"af_index_test_vector_{unique}",
                search_index_name=f"af_index_test_search_{unique}",
            ),
            embedding_generator=Embeddings(),
            collection=collection,
        )
    )
    try:
        regular = await memory.ensure_regular_indexes()
        assert all(item.state is MongoDBIndexState.READY for item in regular)

        memory_vector = await memory.ensure_vector_search_index(
            wait_until_ready=True, timeout=300, poll_interval=2
        )
        rag_vector = await rag.ensure_vector_search_index(
            wait_until_ready=True, timeout=300, poll_interval=2
        )
        rag_search = await rag.ensure_search_index(
            wait_until_ready=True, timeout=300, poll_interval=2
        )
        assert {
            memory_vector.state,
            rag_vector.state,
            rag_search.state,
        } == {MongoDBIndexState.READY}
        assert len(await rag.list_indexes()) == 2
        assert (await rag.inspect_search_index()).queryable is True

        await rag.update_search_index()
        assert (
            await rag.wait_until_search_index_ready(timeout=300, poll_interval=2)
        ).state is MongoDBIndexState.READY
        await rag.drop_search_index()
        assert (await rag.inspect_search_index()).state is MongoDBIndexState.MISSING
    finally:
        assert collection_name.startswith("af_index_test_")
        await client[database_name].drop_collection(collection_name)
        await memory.close()
        await rag.close()
        await client.close()
