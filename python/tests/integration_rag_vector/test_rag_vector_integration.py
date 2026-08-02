from __future__ import annotations

import os
import uuid
from collections.abc import Awaitable, Sequence
from typing import Any

import pytest
from agent_framework import Embedding, GeneratedEmbeddings
from pymongo import AsyncMongoClient

from agent_framework_mongodb import (
    EqualFilter,
    MongoDBCapabilityError,
    MongoDBIndexNotReadyError,
    MongoDBRAGProvider,
    MongoDBRAGProviderOptions,
    MongoDBSearchMode,
)

pytestmark = pytest.mark.integration_rag_vector


class IntegrationEmbeddingGenerator:
    additional_properties: dict[str, Any] = {}

    async def _generate(self, values: Sequence[str]) -> GeneratedEmbeddings[list[float], Any]:
        vectors = [
            [1.0, 0.0, 0.0] if "vector" in value.lower() else [0.0, 1.0, 0.0] for value in values
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
        pytest.skip(
            "MONGODB_URI and MONGODB_DATABASE are required for integration-rag-vector tests"
        )
    return uri, database


@pytest.mark.parametrize(
    "mode",
    [MongoDBSearchMode.VECTOR_ANN, MongoDBSearchMode.VECTOR_ENN],
)
async def test_vector_rag_isolates_tenants_for_ann_and_enn(
    mongodb_settings: tuple[str, str],
    mode: MongoDBSearchMode,
) -> None:
    uri, database_name = mongodb_settings
    unique = uuid.uuid4().hex
    collection_name = f"af_rag_vector_test_{unique}"
    index_name = f"af_rag_vector_{unique}"
    client: AsyncMongoClient[dict[str, Any]] = AsyncMongoClient(uri)
    collection = client[database_name][collection_name]
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=mode,
            vector_dimensions=3,
            vector_index_name=index_name,
            filter=EqualFilter("tenant_id", "tenant-a"),
            num_candidates=10 if mode is MongoDBSearchMode.VECTOR_ANN else None,
        ),
        embedding_generator=IntegrationEmbeddingGenerator(),
        collection=collection,
    )
    try:
        await collection.insert_many(
            [
                {
                    "_id": "authorized",
                    "tenant_id": "tenant-a",
                    "content": "Authorized vector guide",
                    "embedding": [1.0, 0.0, 0.0],
                    "source": {"name": "Authorized guide"},
                },
                {
                    "_id": "forbidden",
                    "tenant_id": "tenant-b",
                    "content": "Cross-tenant vector guide",
                    "embedding": [1.0, 0.0, 0.0],
                    "source": {"name": "Forbidden guide"},
                },
            ]
        )
        try:
            await provider.ensure_vector_search_index(
                wait_until_ready=True,
                timeout=180,
                poll_interval=2,
            )
            results = await provider.search("vector")
        except (MongoDBCapabilityError, MongoDBIndexNotReadyError) as exc:
            pytest.skip(f"{mode.value} capability unavailable: {type(exc).__name__}: {exc}")
        assert [result.id for result in results] == ["authorized"]
        assert results[0].source_name == "Authorized guide"
    finally:
        assert collection_name.startswith("af_rag_vector_test_")
        await client[database_name].drop_collection(collection_name)
        await provider.close()
        await client.close()
