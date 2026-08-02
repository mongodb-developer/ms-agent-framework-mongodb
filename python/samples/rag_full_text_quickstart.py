"""MongoDB full-text RAG explicit provisioning and direct-search quickstart."""

from __future__ import annotations

import asyncio
import os

from agent_framework_mongodb import (
    EqualFilter,
    MongoDBRAGContextProvider,
    MongoDBRAGProvider,
    MongoDBRAGProviderOptions,
    MongoDBSearchMode,
)


def required_environment(name: str) -> str:
    value = os.getenv(name)
    if not value:
        raise RuntimeError(f"Set {name} before running the full-text RAG quickstart.")
    return value


async def main() -> None:
    direct = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.FULL_TEXT,
            search_index_name=required_environment("MONGODB_RAG_SEARCH_INDEX"),
            text_fields=("content",),
            search_analyzer="lucene.standard",
            filter=EqualFilter(
                "tenant_id",
                required_environment("MONGODB_RAG_TENANT"),
            ),
        ),
        connection_string=required_environment("MONGODB_URI"),
        database_name=required_environment("MONGODB_DATABASE"),
        collection_name=required_environment("MONGODB_RAG_COLLECTION"),
    )
    rag = MongoDBRAGContextProvider(direct)
    async with rag:
        # Run this only under a provisioner identity; normal searches never mutate indexes.
        await direct.ensure_search_index(wait_until_ready=True)
        for result in await rag.search("How does this system isolate tenants?"):
            print(f"{result.score:.4f} {result.source_name or result.id}: {result.text}")


if __name__ == "__main__":
    asyncio.run(main())
