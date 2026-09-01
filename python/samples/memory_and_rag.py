"""Run one provider-agnostic agent with separate MongoDB Memory and RAG context."""

from __future__ import annotations

import asyncio
import os
from collections.abc import Awaitable, Mapping, Sequence
from typing import Any, cast

from agent_framework import (
    Agent,
    ChatResponse,
    Embedding,
    GeneratedEmbeddings,
    Message,
)

from agent_framework_mongodb import (
    EqualFilter,
    MongoDBMemoryContextProvider,
    MongoDBRAGContextProvider,
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


class FixtureChatClient:
    """Local model-free client proving provider composition and attribution."""

    additional_properties: dict[str, Any] = {}

    def get_response(
        self,
        messages: Sequence[Message],
        *,
        stream: bool = False,
        options: Mapping[str, Any] | None = None,
        **kwargs: Any,
    ) -> Awaitable[ChatResponse[Any]]:
        del options, kwargs
        if stream:
            raise ValueError("The fixture client supports non-streaming sample runs only.")

        async def respond() -> ChatResponse[Any]:
            attributed = sorted(
                {
                    str(attribution["source_id"])
                    for message in messages
                    if isinstance(
                        attribution := message.additional_properties.get("_attribution"),
                        Mapping,
                    )
                    and attribution.get("source_id")
                }
            )
            sources = ", ".join(attributed) or "no provider context"
            return ChatResponse(
                messages=[
                    Message(
                        "assistant",
                        [f"Fixture response observed attributed context from: {sources}."],
                    )
                ],
                response_id="mongodb-memory-rag-fixture",
            )

        return respond()


def required(name: str) -> str:
    value = os.getenv(name, "").strip()
    if not value:
        raise RuntimeError(f"Set {name} before running Memory and RAG.")
    return value


async def main() -> None:
    connection_string = required("MONGODB_URI")
    database_name = required("MONGODB_DATABASE")
    generator = DemoEmbeddingGenerator()
    memory = MongoDBMemoryContextProvider(
        generator,
        connection_string=connection_string,
        database_name=database_name,
        collection_name=required("MONGODB_MEMORY_COLLECTION"),
        vector_dimensions=3,
        application_id="memory-rag-sample",
        user_id=required("MONGODB_MEMORY_USER_ID"),
    )
    direct_rag = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ANN,
            vector_dimensions=3,
            vector_index_name=required("MONGODB_RAG_VECTOR_INDEX"),
            filter=EqualFilter("tenant_id", required("MONGODB_RAG_TENANT")),
        ),
        embedding_generator=generator,
        connection_string=connection_string,
        database_name=database_name,
        collection_name=required("MONGODB_RAG_COLLECTION"),
    )
    rag = MongoDBRAGContextProvider(direct_rag)
    agent = Agent(
        cast(Any, FixtureChatClient()),
        instructions=(
            "Use conversational Memory only as attributed prior context and RAG only as "
            "authoritative knowledge."
        ),
        context_providers=[memory, rag],
    )
    try:
        await memory.validate_vector_search_index()
        await direct_rag.validate_vector_search_index()
        response = await cast(
            Awaitable[Any],
            agent.run("What do prior context and authoritative sources say about access?"),
        )
        print(response.text)
    finally:
        await asyncio.gather(memory.close(), rag.close())


if __name__ == "__main__":
    asyncio.run(main())
