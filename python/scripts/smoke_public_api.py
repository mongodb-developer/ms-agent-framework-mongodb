"""Import the built package and construct every public provider without network access."""

from __future__ import annotations

import asyncio
from collections.abc import Awaitable, Sequence
from importlib.metadata import version
from typing import Any

from agent_framework import Embedding, GeneratedEmbeddings

import agent_framework_mongodb as mongodb


class _EmbeddingGenerator:
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


async def _smoke() -> None:
    generator = _EmbeddingGenerator()
    memory = mongodb.MongoDBMemoryContextProvider(
        generator,
        vector_dimensions=3,
        application_id="artifact-smoke",
        user_id="artifact-smoke",
    )
    history = mongodb.MongoDBHistoryProvider(
        options=mongodb.MongoDBHistoryProviderOptions(
            application_id="artifact-smoke",
            agent_id="artifact-smoke",
            session_id="artifact-smoke",
        )
    )
    direct_rag = mongodb.MongoDBRAGProvider(
        mongodb.MongoDBRAGProviderOptions(
            mode=mongodb.MongoDBSearchMode.FULL_TEXT,
            search_index_name="artifact-smoke",
            filter=mongodb.EqualFilter("tenant_id", "artifact-smoke"),
        )
    )
    rag = mongodb.MongoDBRAGContextProvider(direct_rag)
    sessions = mongodb.MongoDBSessionStore(
        options=mongodb.MongoDBSessionStoreOptions(
            tenant_id="artifact-smoke",
            application_id="artifact-smoke",
            agent_id="artifact-smoke",
        )
    )
    checkpoints = mongodb.MongoDBCheckpointStorage(
        options=mongodb.MongoDBCheckpointStorageOptions(
            tenant_id="artifact-smoke",
            workflow_name="artifact-smoke",
            session_id="artifact-smoke",
        )
    )
    assert mongodb.__version__ == version("agent-framework-mongodb")
    assert mongodb.MongoDBRAGSearchOptions(top_k=1).top_k == 1
    assert (
        mongodb.MongoDBVectorIndexDefinition(
            name="artifact-smoke",
            path="embedding",
            dimensions=3,
            similarity="cosine",
        ).dimensions
        == 3
    )
    await asyncio.gather(
        memory.close(),
        history.close(),
        rag.close(),
        sessions.close(),
        checkpoints.close(),
    )


if __name__ == "__main__":
    asyncio.run(_smoke())
    print("Installed public API constructor smoke passed.")
