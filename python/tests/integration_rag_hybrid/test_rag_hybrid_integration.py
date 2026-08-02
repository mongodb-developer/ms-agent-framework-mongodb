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
    MongoDBIndexFailedError,
    MongoDBIndexMismatchError,
    MongoDBIndexNotReadyError,
    MongoDBRAGProvider,
    MongoDBRAGProviderOptions,
    MongoDBSearchMode,
)

pytestmark = pytest.mark.integration_rag_hybrid


class IntegrationEmbeddingGenerator:
    additional_properties: dict[str, Any] = {}

    async def _generate(self, values: Sequence[str]) -> GeneratedEmbeddings[list[float], Any]:
        return GeneratedEmbeddings([Embedding(vector=[1.0, 0.0, 0.0]) for _ in values])

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
            "MONGODB_URI and MONGODB_DATABASE are required for integration-rag-hybrid tests"
        )
    return uri, database


async def test_hybrid_rrf_deduplicates_weights_and_excludes_cross_tenant_candidates(
    mongodb_settings: tuple[str, str],
) -> None:
    uri, database_name = mongodb_settings
    unique = uuid.uuid4().hex
    collection_name = f"af_rag_hybrid_test_{unique}"
    vector_index = f"af_rag_hybrid_vector_{unique}"
    search_index = f"af_rag_hybrid_search_{unique}"
    client: AsyncMongoClient[dict[str, Any]] = AsyncMongoClient(uri)
    collection = client[database_name][collection_name]

    def provider(vector_weight: float, text_weight: float) -> MongoDBRAGProvider:
        return MongoDBRAGProvider(
            MongoDBRAGProviderOptions(
                mode=MongoDBSearchMode.HYBRID_RRF,
                vector_dimensions=3,
                vector_index_name=vector_index,
                search_index_name=search_index,
                filter=EqualFilter("tenant_id", "tenant-a"),
                top_k=3,
                num_candidates=10,
                vector_weight=vector_weight,
                text_weight=text_weight,
            ),
            embedding_generator=IntegrationEmbeddingGenerator(),
            collection=collection,
        )

    vector_favored = provider(10.0, 0.0)
    text_favored = provider(0.0, 10.0)
    try:
        await collection.insert_many(
            [
                {
                    "_id": "vector-first",
                    "tenant_id": "tenant-a",
                    "content": "semantic-only material",
                    "embedding": [1.0, 0.0, 0.0],
                },
                {
                    "_id": "text-first",
                    "tenant_id": "tenant-a",
                    "content": "hybridkeyword",
                    "embedding": [0.0, 1.0, 0.0],
                },
                {
                    "_id": "both-branches",
                    "tenant_id": "tenant-a",
                    "content": (
                        "filler filler filler filler filler hybridkeyword "
                        "filler filler filler filler filler"
                    ),
                    "embedding": [0.8, 0.6, 0.0],
                },
                {
                    "_id": "forbidden",
                    "tenant_id": "tenant-b",
                    "content": "hybridkeyword hybridkeyword hybridkeyword hybridkeyword",
                    "embedding": [1.0, 0.0, 0.0],
                },
            ]
        )
        try:
            await vector_favored.ensure_vector_search_index(
                wait_until_ready=True,
                timeout=180,
                poll_interval=2,
            )
            await vector_favored.ensure_search_index(
                wait_until_ready=True,
                timeout=180,
                poll_interval=2,
            )
            await vector_favored.validate_capabilities(refresh=True)
            vector_results = await vector_favored.search("hybridkeyword")
            text_results = await text_favored.search("hybridkeyword")
        except (
            MongoDBCapabilityError,
            MongoDBIndexFailedError,
            MongoDBIndexMismatchError,
            MongoDBIndexNotReadyError,
        ) as exc:
            pytest.skip(
                "native hybrid capability/index unavailable after public validation: "
                f"{type(exc).__name__}: {exc}"
            )

        assert vector_results[0].id == "vector-first"
        assert text_results[0].id == "text-first"
        assert vector_results[0].id != text_results[0].id
        for results in (vector_results, text_results):
            identifiers = [result.id for result in results]
            assert "forbidden" not in identifiers
            assert identifiers.count("both-branches") == 1
            assert len(identifiers) == len(set(identifiers))
            assert all(result.score > 0 for result in results)
    finally:
        assert collection_name.startswith("af_rag_hybrid_test_")
        await client[database_name].drop_collection(collection_name)
        await vector_favored.close()
        await text_favored.close()
        await client.close()
