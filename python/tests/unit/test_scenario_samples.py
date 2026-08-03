from __future__ import annotations

from typing import Any, cast

from agent_framework import Message

from agent_framework_mongodb import AndFilter, EqualFilter, InFilter, MongoDBRAGResult
from samples.memory_and_rag import FixtureChatClient
from samples.on_demand_retrieval_tool import build_retrieval_tool
from samples.structured_metadata_retrieval import RetrievalPlan


class StubRAGProvider:
    async def search(self, query: str) -> list[MongoDBRAGResult]:
        assert query == "authorized question"
        return [
            MongoDBRAGResult(
                id="source-1",
                text="authorized answer",
                score=1.0,
                metadata={},
                raw_document={},
                source_name="fixture",
            )
        ]


async def test_on_demand_tool_exposes_only_query_text() -> None:
    retrieval = build_retrieval_tool(cast(Any, StubRAGProvider()))

    assert retrieval.parameters()["properties"] == {"query": {"title": "Query", "type": "string"}}
    assert (
        await retrieval.invoke(
            arguments={"query": "authorized question"},
            skip_parsing=True,
        )
        == "[fixture] authorized answer"
    )


def test_structured_plan_translates_only_to_typed_filters() -> None:
    translated = RetrievalPlan(
        query="question",
        category="security",
        visibility=("public", "internal"),
    ).to_filter()

    assert translated == AndFilter(
        EqualFilter("metadata.category", "security"),
        InFilter("visibility", ("public", "internal")),
    )


async def test_memory_and_rag_fixture_reports_attributed_context() -> None:
    response = await FixtureChatClient().get_response(
        [
            Message(
                "system",
                ["memory"],
                additional_properties={"_attribution": {"source_id": "mongodb-memory"}},
            ),
            Message(
                "system",
                ["knowledge"],
                additional_properties={"_attribution": {"source_id": "mongodb-rag"}},
            ),
        ]
    )

    assert response.text == (
        "Fixture response observed attributed context from: mongodb-memory, mongodb-rag."
    )
