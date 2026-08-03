"""Translate an application-owned structured query plan to typed MongoDB filters."""

from __future__ import annotations

import asyncio
import os
from dataclasses import dataclass
from typing import Literal

from agent_framework_mongodb import (
    AndFilter,
    EqualFilter,
    InFilter,
    MongoDBFilter,
    MongoDBRAGProvider,
    MongoDBRAGProviderOptions,
    MongoDBRAGSearchOptions,
    MongoDBSearchMode,
)

Visibility = Literal["public", "internal"]


@dataclass(frozen=True)
class RetrievalPlan:
    query: str
    category: str
    visibility: tuple[Visibility, ...]

    def to_filter(self) -> MongoDBFilter:
        if not self.category.strip():
            raise ValueError("category must be non-empty")
        if not self.visibility:
            raise ValueError("visibility must contain at least one approved value")
        return AndFilter(
            EqualFilter("metadata.category", self.category),
            InFilter("visibility", self.visibility),
        )


def required(name: str) -> str:
    value = os.getenv(name, "").strip()
    if not value:
        raise RuntimeError(f"Set {name} before running structured metadata retrieval.")
    return value


async def main() -> None:
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.FULL_TEXT,
            search_index_name=required("MONGODB_RAG_SEARCH_INDEX"),
            filter=EqualFilter("tenant_id", required("MONGODB_RAG_TENANT")),
            metadata_fields=("metadata.category", "visibility"),
        ),
        connection_string=required("MONGODB_URI"),
        database_name=required("MONGODB_DATABASE"),
        collection_name=required("MONGODB_RAG_COLLECTION"),
    )
    plan = RetrievalPlan(
        query="How is tenant access enforced?",
        category="security",
        visibility=("public",),
    )
    async with provider:
        await provider.validate_search_index()
        results = await provider.search(
            plan.query,
            options=MongoDBRAGSearchOptions(filter=plan.to_filter(), top_k=3),
        )
        for result in results:
            print(f"{result.score:.4f} {result.source_name or result.id}: {result.text}")


if __name__ == "__main__":
    asyncio.run(main())
