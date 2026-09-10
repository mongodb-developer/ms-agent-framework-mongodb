"""Run deterministic MongoDB retrieval inside an Agent Framework workflow step."""

from __future__ import annotations

import asyncio
import os

from agent_framework import Executor, WorkflowBuilder, WorkflowContext, handler

from agent_framework_mongodb import (
    EqualFilter,
    MongoDBRAGProvider,
    MongoDBRAGProviderOptions,
    MongoDBSearchMode,
)


def required(name: str) -> str:
    value = os.getenv(name, "").strip()
    if not value:
        raise RuntimeError(f"Set {name} before running workflow retrieval.")
    return value


class RetrievalExecutor(Executor):
    def __init__(self, provider: MongoDBRAGProvider) -> None:
        super().__init__(id="mongodb-retrieval")
        self._provider = provider

    @handler(input=str, output=str, workflow_output=str)
    async def retrieve(
        self,
        query: str,
        context: WorkflowContext[str, str],
    ) -> None:
        results = await self._provider.search(query)
        await context.yield_output(
            "\n\n".join(f"[{result.source_name or result.id}] {result.text}" for result in results)
            or "No authorized knowledge matched."
        )


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
    workflow = WorkflowBuilder(
        name="mongodb-retrieval",
        start_executor=RetrievalExecutor(provider),
    ).build()
    async with provider:
        await provider.validate_search_index()
        result = await workflow.run("How is tenant access enforced?")
        print(result.get_outputs()[0])


if __name__ == "__main__":
    asyncio.run(main())
