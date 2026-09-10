"""MongoDB native hybrid RRF explicit provisioning and direct-search quickstart."""

from __future__ import annotations

import asyncio
import os
from collections.abc import Awaitable, Sequence
from typing import Any

from agent_framework import Embedding, GeneratedEmbeddings

from agent_framework_mongodb import (
    EqualFilter,
    MongoDBRAGContextProvider,
    MongoDBRAGProvider,
    MongoDBRAGProviderOptions,
    MongoDBSearchMode,
)


class DemoEmbeddingGenerator:
    """Replace with the generator used to embed the existing collection."""

    additional_properties: dict[str, Any] = {}

    async def _generate(self, values: Sequence[str]) -> GeneratedEmbeddings[list[float], Any]:
        vectors = [[1.0, 0.0, 0.0] for _ in values]
        return GeneratedEmbeddings([Embedding(vector=vector) for vector in vectors])

    def get_embeddings(
        self,
        values: Sequence[str],
        *,
        options: Any | None = None,
    ) -> Awaitable[GeneratedEmbeddings[list[float], Any]]:
        del options
        return self._generate(values)


def required_environment(name: str) -> str:
    value = os.getenv(name)
    if not value:
        raise RuntimeError(f"Set {name} before running the hybrid RAG quickstart.")
    return value


async def main() -> None:
    direct = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.HYBRID_RRF,
            vector_dimensions=3,
            vector_index_name=required_environment("MONGODB_RAG_VECTOR_INDEX"),
            search_index_name=required_environment("MONGODB_RAG_SEARCH_INDEX"),
            text_fields=("content",),
            vector_field="embedding",
            num_candidates=50,
            top_k=5,
            vector_weight=1.0,
            text_weight=1.0,
            filter=EqualFilter(
                "tenant_id",
                required_environment("MONGODB_RAG_TENANT"),
            ),
        ),
        embedding_generator=DemoEmbeddingGenerator(),
        connection_string=required_environment("MONGODB_URI"),
        database_name=required_environment("MONGODB_DATABASE"),
        collection_name=required_environment("MONGODB_RAG_COLLECTION"),
    )
    rag = MongoDBRAGContextProvider(direct)
    async with rag:
        # Run these only under a provisioner identity; runtime hybrid search is read-only.
        await direct.ensure_vector_search_index(wait_until_ready=True)
        await direct.ensure_search_index(wait_until_ready=True)
        await direct.validate_capabilities(refresh=True)
        for result in await rag.search("How does this system isolate tenants?"):
            print(f"{result.score:.6f} {result.source_name or result.id}: {result.text}")


if __name__ == "__main__":
    asyncio.run(main())
