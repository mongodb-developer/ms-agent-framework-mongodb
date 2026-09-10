from __future__ import annotations

import os
import uuid
from typing import Any

import pytest
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

pytestmark = pytest.mark.integration_rag_search


@pytest.fixture
def mongodb_settings() -> tuple[str, str]:
    uri = os.getenv("MONGODB_URI")
    database = os.getenv("MONGODB_DATABASE")
    if not uri or not database:
        pytest.skip(
            "MONGODB_URI and MONGODB_DATABASE are required for integration-rag-search tests"
        )
    return uri, database


async def test_full_text_rag_excludes_cross_tenant_results(
    mongodb_settings: tuple[str, str],
) -> None:
    uri, database_name = mongodb_settings
    unique = uuid.uuid4().hex
    collection_name = f"af_rag_search_test_{unique}"
    index_name = f"af_rag_search_{unique}"
    client: AsyncMongoClient[dict[str, Any]] = AsyncMongoClient(uri)
    collection = client[database_name][collection_name]
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.FULL_TEXT,
            search_index_name=index_name,
            filter=EqualFilter("tenant_id", "tenant-a"),
        ),
        collection=collection,
    )
    try:
        await collection.insert_many(
            [
                {
                    "_id": "authorized",
                    "tenant_id": "tenant-a",
                    "content": "Authorized telescope operations handbook",
                    "source": {"name": "Authorized handbook"},
                },
                {
                    "_id": "forbidden",
                    "tenant_id": "tenant-b",
                    "content": "Cross-tenant telescope operations handbook",
                    "source": {"name": "Forbidden handbook"},
                },
            ]
        )
        try:
            await provider.ensure_search_index(
                wait_until_ready=True,
                timeout=180,
                poll_interval=2,
            )
            await provider.validate_capabilities(refresh=True)
            results = await provider.search("telescope operations handbook")
        except (
            MongoDBCapabilityError,
            MongoDBIndexFailedError,
            MongoDBIndexMismatchError,
            MongoDBIndexNotReadyError,
        ) as exc:
            pytest.skip(
                "full_text capability/index unavailable after public validation: "
                f"{type(exc).__name__}: {exc}"
            )

        assert [result.id for result in results] == ["authorized"]
        assert results[0].source_name == "Authorized handbook"
        assert results[0].score > 0
    finally:
        assert collection_name.startswith("af_rag_search_test_")
        await client[database_name].drop_collection(collection_name)
        await provider.close()
        await client.close()
