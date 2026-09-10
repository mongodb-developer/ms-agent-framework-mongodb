from __future__ import annotations

import json
from collections.abc import Awaitable, Sequence
from pathlib import Path
from typing import Any, cast

from agent_framework import Embedding, GeneratedEmbeddings

from agent_framework_mongodb import MongoDBMemoryContextProvider


class ContractEmbeddingGenerator:
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


class EmptyCursor:
    async def to_list(self, *, length: int | None) -> list[dict[str, Any]]:
        del length
        return []


class ContractCollection:
    def __init__(self) -> None:
        self.pipeline: list[dict[str, Any]] = []

    async def aggregate(self, pipeline: list[dict[str, Any]]) -> EmptyCursor:
        self.pipeline = pipeline
        return EmptyCursor()


async def test_language_neutral_scope_filter_contract() -> None:
    fixture_path = (
        Path(__file__).parents[3] / "tests" / "fixtures" / "memory" / "scope-filters.json"
    )
    cases = cast(dict[str, list[dict[str, Any]]], json.loads(fixture_path.read_text()))["cases"]

    for case in cases:
        collection = ContractCollection()
        scope = cast(dict[str, str], case["provider_scope"])
        provider = MongoDBMemoryContextProvider(
            ContractEmbeddingGenerator(),
            vector_dimensions=3,
            collection=cast(Any, collection),
            application_id=scope.get("application_id"),
            agent_id=scope.get("agent_id"),
            user_id=scope.get("user_id"),
        )

        await provider.search("contract query", session_id=cast(str | None, case["session_id"]))

        assert collection.pipeline[0]["$vectorSearch"]["filter"] == case["expected_filter"]
