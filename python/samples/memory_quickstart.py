"""Minimal MongoDB Memory provisioning and direct-API quickstart."""

from __future__ import annotations

import asyncio
import os
from collections.abc import Awaitable, Sequence
from typing import Any

from agent_framework import Embedding, GeneratedEmbeddings, Message

from agent_framework_mongodb import MongoDBMemoryContextProvider


class DemoEmbeddingGenerator:
    """Deterministic local vectors for setup demonstration only."""

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
        raise RuntimeError(f"Set {name} before running the Memory quickstart.")
    return value


async def main() -> None:
    provider = MongoDBMemoryContextProvider(
        DemoEmbeddingGenerator(),
        connection_string=required_environment("MONGODB_URI"),
        database_name=required_environment("MONGODB_DATABASE"),
        collection_name=required_environment("MONGODB_MEMORY_COLLECTION"),
        vector_dimensions=3,
        application_id="memory-quickstart",
        user_id="quickstart-user",
    )
    async with provider:
        await provider.ensure_vector_search_index(wait_until_ready=True)
        await provider.store(
            [Message("user", ["MongoDB is my preferred database."], message_id="quickstart-1")],
            session_id="quickstart-session",
        )
        for memory in await provider.search("preferred database", exact=True):
            print(memory.text)
        await provider.clear_session("quickstart-session")


if __name__ == "__main__":
    asyncio.run(main())
