"""Retrieve authorized child chunks and hydrate bounded parent documents."""

from __future__ import annotations

import asyncio
import os
from collections.abc import Awaitable, Sequence
from typing import Any

from agent_framework import Embedding, GeneratedEmbeddings

from agent_framework_mongodb import (
    EqualFilter,
    MongoDBRAGParentOptions,
    MongoDBRAGProvider,
    MongoDBRAGProviderOptions,
    MongoDBSearchMode,
)


class DemoEmbeddingGenerator:
    """Deterministic three-dimensional vectors for sample fixtures only."""

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


def required(name: str) -> str:
    value = os.getenv(name, "").strip()
    if not value:
        raise RuntimeError(f"Set {name} before running parent-document RAG.")
    return value


async def main() -> None:
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ANN,
            vector_dimensions=3,
            vector_index_name=required("MONGODB_RAG_VECTOR_INDEX"),
            filter=EqualFilter("tenant_id", required("MONGODB_RAG_TENANT")),
            parent=MongoDBRAGParentOptions(
                parent_id_field="parent_id",
                parent_document_id_field="_id",
                parent_text_field="content",
                child_record_field="record_type",
                child_record_value="child",
                max_parents=3,
                max_lookup_fan_out=10,
                max_context_tokens=2000,
            ),
        ),
        embedding_generator=DemoEmbeddingGenerator(),
        connection_string=required("MONGODB_URI"),
        database_name=required("MONGODB_DATABASE"),
        collection_name=required("MONGODB_RAG_COLLECTION"),
    )
    async with provider:
        await provider.validate_vector_search_index()
        for result in await provider.search("How is tenant access enforced?"):
            print(f"{result.score:.4f} {result.source_name or result.id}: {result.text}")


if __name__ == "__main__":
    asyncio.run(main())
