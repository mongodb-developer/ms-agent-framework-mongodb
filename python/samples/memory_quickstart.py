"""Minimal MongoDB Memory provisioning and direct-API quickstart."""

from __future__ import annotations

import argparse
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


async def run_memory_quickstart(
    provider: MongoDBMemoryContextProvider,
    *,
    keep: bool = False,
) -> None:
    """Store and retrieve one memory record with optional retention."""
    async with provider:
        await provider.store(
            [Message("user", ["MongoDB is my preferred database."], message_id="quickstart-1")],
            session_id="quickstart-session",
        )
        await provider.ensure_vector_search_index(wait_until_ready=True)
        for memory in await provider.search("preferred database", exact=True):
            print(memory.text)
        if keep:
            print("Authorized cleanup skipped by --keep.")
        else:
            await provider.clear_session("quickstart-session")


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    """Parse the sample's retention option."""
    parser = argparse.ArgumentParser(description="Store and retrieve one MongoDB memory.")
    parser.add_argument(
        "--keep",
        action="store_true",
        help="retain the sample session instead of clearing it after retrieval",
    )
    return parser.parse_args(argv)


async def main(argv: Sequence[str] | None = None) -> None:
    arguments = parse_args(argv)
    provider = MongoDBMemoryContextProvider(
        DemoEmbeddingGenerator(),
        connection_string=required_environment("MONGODB_URI"),
        database_name=required_environment("MONGODB_DATABASE"),
        collection_name=required_environment("MONGODB_MEMORY_COLLECTION"),
        vector_dimensions=3,
        application_id="memory-quickstart",
        user_id="quickstart-user",
    )
    await run_memory_quickstart(provider, keep=arguments.keep)


if __name__ == "__main__":
    asyncio.run(main())
