"""Expose query-text-only MongoDB retrieval as an on-demand framework tool."""

from __future__ import annotations

import asyncio
import os

from agent_framework import FunctionTool, tool

from agent_framework_mongodb import (
    EqualFilter,
    MongoDBRAGProvider,
    MongoDBRAGProviderOptions,
    MongoDBSearchMode,
)


def required(name: str) -> str:
    value = os.getenv(name, "").strip()
    if not value:
        raise RuntimeError(f"Set {name} before running on-demand retrieval.")
    return value


def build_retrieval_tool(provider: MongoDBRAGProvider) -> FunctionTool:
    @tool(
        name="retrieve_knowledge",
        description="Retrieve application-authorized knowledge for one natural-language query.",
    )
    async def retrieve_knowledge(query: str) -> str:
        results = await provider.search(query)
        return "\n\n".join(
            f"[{result.source_name or result.id}] {result.text}" for result in results
        )

    return retrieve_knowledge


async def main() -> None:
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.FULL_TEXT,
            search_index_name=required("MONGODB_RAG_SEARCH_INDEX"),
            filter=EqualFilter("tenant_id", required("MONGODB_RAG_TENANT")),
        ),
        connection_string=required("MONGODB_URI"),
        database_name=required("MONGODB_DATABASE"),
        collection_name=required("MONGODB_RAG_COLLECTION"),
    )
    retrieval = build_retrieval_tool(provider)
    if set(retrieval.parameters().get("properties", {})) != {"query"}:
        raise RuntimeError("The retrieval tool schema must expose only query text.")
    async with provider:
        await provider.validate_search_index()
        answer = await retrieval.invoke(
            arguments={"query": "How is tenant access enforced?"},
            skip_parsing=True,
        )
        print(answer or "No authorized knowledge matched.")


if __name__ == "__main__":
    asyncio.run(main())
