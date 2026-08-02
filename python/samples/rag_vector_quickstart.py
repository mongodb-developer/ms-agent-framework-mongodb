"""MongoDB Vector RAG explicit provisioning and direct-search quickstart."""

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
    """Replace with the model used to embed the existing knowledge collection."""

    additional_properties: dict[str, Any] = {}

    async def _generate(self, values: Sequence[str]) -> GeneratedEmbeddings[list[float], Any]:
        return GeneratedEmbeddings(
            [Embedding(vector=[float(len(value)), 1.0, 0.0]) for value in values]
        )

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
        raise RuntimeError(f"Set {name} before running the Vector RAG quickstart.")
    return value


async def main() -> None:
    direct = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ANN,
            vector_dimensions=3,
            vector_index_name=required_environment("MONGODB_RAG_VECTOR_INDEX"),
            filter=EqualFilter("tenant_id", required_environment("MONGODB_RAG_TENANT")),
            num_candidates=50,
        ),
        embedding_generator=DemoEmbeddingGenerator(),
        connection_string=required_environment("MONGODB_URI"),
        database_name=required_environment("MONGODB_DATABASE"),
        collection_name=required_environment("MONGODB_RAG_COLLECTION"),
    )
    rag = MongoDBRAGContextProvider(direct)
    async with rag:
        # Run this only under a provisioner identity; normal searches never mutate indexes.
        await direct.ensure_vector_search_index(wait_until_ready=True)
        for result in await rag.search("How does this system isolate tenants?"):
            print(f"{result.score:.4f} {result.source_name or result.id}: {result.text}")


if __name__ == "__main__":
    asyncio.run(main())
