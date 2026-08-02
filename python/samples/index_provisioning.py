"""Explicit, provisioner-only MongoDB RAG index lifecycle sample."""

from __future__ import annotations

import asyncio
import os
from collections.abc import Awaitable, Sequence
from typing import Any

from agent_framework import Embedding, GeneratedEmbeddings

from agent_framework_mongodb import (
    MongoDBRAGContextProvider,
    MongoDBRAGProvider,
    MongoDBRAGProviderOptions,
    MongoDBSearchMode,
)


class CollectionEmbeddingGenerator:
    """Replace with the generator used for the existing knowledge collection."""

    additional_properties: dict[str, Any] = {}

    def get_embeddings(
        self, values: Sequence[str], *, options: Any | None = None
    ) -> Awaitable[GeneratedEmbeddings[list[float], Any]]:
        del options

        async def generate() -> GeneratedEmbeddings[list[float], Any]:
            return GeneratedEmbeddings([Embedding(vector=[1.0, 0.0, 0.0]) for _ in values])

        return generate()


def required(name: str) -> str:
    value = os.getenv(name)
    if not value:
        raise RuntimeError(f"Set {name} before running index provisioning.")
    return value


async def main() -> None:
    direct = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.HYBRID_RRF,
            vector_dimensions=3,
            vector_field=os.getenv("MONGODB_RAG_VECTOR_FIELD", "embedding"),
            vector_index_name=required("MONGODB_RAG_VECTOR_INDEX"),
            search_index_name=required("MONGODB_RAG_SEARCH_INDEX"),
            text_fields=(os.getenv("MONGODB_RAG_TEXT_FIELD", "content"),),
        ),
        embedding_generator=CollectionEmbeddingGenerator(),
        connection_string=required("MONGODB_URI"),
        database_name=required("MONGODB_DATABASE"),
        collection_name=required("MONGODB_RAG_COLLECTION"),
    )
    provider = MongoDBRAGContextProvider(direct)
    async with provider:
        vector = await provider.ensure_vector_search_index(
            wait_until_ready=True, timeout=600, poll_interval=2
        )
        search = await provider.ensure_search_index(
            wait_until_ready=True, timeout=600, poll_interval=2
        )
        print(vector.definition.name, vector.state.value)
        print(search.definition.name, search.state.value)


if __name__ == "__main__":
    asyncio.run(main())
