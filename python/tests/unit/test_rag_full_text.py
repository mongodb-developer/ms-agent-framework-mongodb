from __future__ import annotations

import asyncio
from dataclasses import dataclass
from typing import Any

import pytest
from agent_framework import AgentSession, Message, SessionContext
from pymongo.errors import OperationFailure

from agent_framework_mongodb import (
    EqualFilter,
    GreaterThanOrEqualFilter,
    MongoDBConfigurationError,
    MongoDBFilter,
    MongoDBFilterTranslationError,
    MongoDBIndexMismatchError,
    MongoDBIndexMissingError,
    MongoDBRAGContextProvider,
    MongoDBRAGParentOptions,
    MongoDBRAGProvider,
    MongoDBRAGProviderOptions,
    MongoDBRAGSearchOptions,
    MongoDBSearchMode,
)


class FakeCursor:
    def __init__(self, documents: list[dict[str, Any]]) -> None:
        self.documents = documents

    async def to_list(self, *, length: int | None) -> list[dict[str, Any]]:
        return self.documents if length is None else self.documents[:length]


class FakeDatabase:
    def __init__(self) -> None:
        self.collections: dict[str, FakeCollection] = {}

    def __getitem__(self, name: str) -> FakeCollection:
        return self.collections[name]


class FakeCollection:
    def __init__(self, name: str = "knowledge") -> None:
        self.name = name
        self.database = FakeDatabase()
        self.pipelines: list[list[dict[str, Any]]] = []
        self.documents: list[dict[str, Any]] = []
        self.read_error: BaseException | None = None
        self.search_indexes: list[dict[str, Any]] = [
            {
                "name": "knowledge_search",
                "type": "search",
                "status": "READY",
                "queryable": True,
                "latestDefinition": {
                    "mappings": {
                        "dynamic": True,
                        "fields": {
                            "content": {
                                "type": "string",
                                "analyzer": "lucene.standard",
                            },
                            "tenant_id": {"type": "token"},
                            "published_year": {"type": "number"},
                        },
                    }
                },
            }
        ]
        self.index_reads = 0
        self.created_search_model: Any | None = None
        self.updated_search_definition: tuple[str, dict[str, Any]] | None = None

    async def aggregate(self, pipeline: list[dict[str, Any]]) -> FakeCursor:
        self.pipelines.append(pipeline)
        if self.read_error is not None:
            raise self.read_error
        return FakeCursor(self.documents)

    async def list_search_indexes(self, *, name: str) -> FakeCursor:
        self.index_reads += 1
        return FakeCursor([index for index in self.search_indexes if index["name"] == name])

    async def create_search_index(self, model: Any) -> str:
        self.created_search_model = model
        document = model.document
        self.search_indexes = [
            {
                "name": document["name"],
                "type": document.get("type", "search"),
                "status": "BUILDING",
                "queryable": False,
                "latestDefinition": document["definition"],
            }
        ]
        return document["name"]

    async def update_search_index(self, name: str, definition: dict[str, Any]) -> None:
        self.updated_search_definition = (name, definition)


def full_text_options(**overrides: Any) -> MongoDBRAGProviderOptions:
    values: dict[str, Any] = {
        "mode": MongoDBSearchMode.FULL_TEXT,
        "search_index_name": "knowledge_search",
        "filter": EqualFilter("tenant_id", "tenant-a"),
    }
    values.update(overrides)
    return MongoDBRAGProviderOptions(**values)


def test_full_text_validates_analyzer_configuration() -> None:
    with pytest.raises(MongoDBConfigurationError, match="search_analyzer"):
        full_text_options(search_analyzer="$invalid")


async def test_full_text_search_builds_first_stage_filter_and_maps_search_score() -> None:
    collection = FakeCollection()
    collection.documents = [
        {
            "_id": "guide-1",
            "content": "Use compound filters before limiting.",
            "source": {"name": "Security guide", "url": "https://example.test/security"},
            "kind": "guide",
            "_ragScore": 4.25,
        }
    ]
    provider = MongoDBRAGProvider(
        full_text_options(metadata_fields=("kind",), top_k=8),
        collection=collection,  # type: ignore[arg-type]
    )

    results = await provider.search(
        "tenant isolation",
        options=MongoDBRAGSearchOptions(
            top_k=3,
            filter=GreaterThanOrEqualFilter("published_year", 2025),
        ),
    )

    assert collection.pipelines == [
        [
            {
                "$search": {
                    "index": "knowledge_search",
                    "compound": {
                        "must": [
                            {
                                "text": {
                                    "query": "tenant isolation",
                                    "path": ["content"],
                                }
                            }
                        ],
                        "filter": [
                            {"equals": {"path": "tenant_id", "value": "tenant-a"}},
                            {"range": {"path": "published_year", "gte": 2025}},
                        ],
                    },
                }
            },
            {"$limit": 3},
            {"$set": {"_ragScore": {"$meta": "searchScore"}}},
        ]
    ]
    assert [(result.id, result.score, result.source_name) for result in results] == [
        ("guide-1", 4.25, "Security guide")
    ]
    assert results[0].source_url == "https://example.test/security"
    assert results[0].metadata == {"kind": "guide"}
    citation = results[0].to_citation()
    assert citation.get("title") == "Security guide"
    assert citation.get("url") == "https://example.test/security"
    assert citation.get("additional_properties", {}).get("score") == 4.25


async def test_full_text_rejects_incomplete_translation_before_index_or_aggregate_io() -> None:
    @dataclass(frozen=True, slots=True)
    class UnsupportedFilter(MongoDBFilter):
        pass

    collection = FakeCollection()
    provider = MongoDBRAGProvider(
        full_text_options(),
        collection=collection,  # type: ignore[arg-type]
    )

    with pytest.raises(MongoDBFilterTranslationError, match="unsupported"):
        await provider.search(
            "query",
            options=MongoDBRAGSearchOptions(filter=UnsupportedFilter()),
        )

    assert collection.index_reads == 0
    assert collection.pipelines == []


async def test_full_text_parent_hydration_reapplies_provider_authorization() -> None:
    children = FakeCollection()
    parents = FakeCollection("parents")
    children.database.collections["parents"] = parents
    children.documents = [
        {
            "_id": "chunk-1",
            "parent_id": "parent-1",
            "content": "matching child",
            "_ragScore": 2.0,
        }
    ]
    parents.documents = [
        {
            "_id": "parent-1",
            "tenant_id": "tenant-a",
            "content": "Authorized parent text",
        }
    ]
    provider = MongoDBRAGProvider(
        full_text_options(
            parent=MongoDBRAGParentOptions(collection_name="parents"),
        ),
        collection=children,  # type: ignore[arg-type]
    )

    results = await provider.search("parent query")

    assert [result.text for result in results] == ["Authorized parent text"]
    assert parents.pipelines == [
        [
            {
                "$match": {
                    "$and": [
                        {"_id": {"$in": ["parent-1"]}},
                        {"tenant_id": {"$eq": "tenant-a"}},
                    ]
                }
            }
        ]
    ]


async def test_search_index_facade_is_read_only_until_explicit_ensure() -> None:
    collection = FakeCollection()
    collection.search_indexes = []
    provider = MongoDBRAGProvider(
        full_text_options(),
        collection=collection,  # type: ignore[arg-type]
    )

    with pytest.raises(MongoDBIndexMissingError):
        await provider.search("query")
    assert collection.created_search_model is None

    await provider.ensure_search_index()

    assert collection.created_search_model is not None
    assert collection.created_search_model.document == {
        "name": "knowledge_search",
        "definition": {
            "mappings": {
                "dynamic": True,
                "fields": {
                    "content": {
                        "type": "string",
                        "analyzer": "lucene.standard",
                    },
                    "tenant_id": {"type": "token"},
                },
            }
        },
    }


async def test_search_index_validation_rejects_analyzer_mismatch() -> None:
    collection = FakeCollection()
    collection.search_indexes[0]["latestDefinition"]["mappings"]["fields"]["content"][
        "analyzer"
    ] = "lucene.english"
    provider = MongoDBRAGProvider(
        full_text_options(),
        collection=collection,  # type: ignore[arg-type]
    )

    with pytest.raises(MongoDBIndexMismatchError, match="analyzer"):
        await provider.validate_search_index()


async def test_full_text_adapter_fails_open_only_for_transient_errors_and_propagates_cancel() -> (
    None
):
    collection = FakeCollection()
    provider = MongoDBRAGProvider(
        full_text_options(),
        collection=collection,  # type: ignore[arg-type]
    )
    adapter = MongoDBRAGContextProvider(provider)
    context = SessionContext(input_messages=[Message("user", ["query"])])
    collection.read_error = OperationFailure("private query", code=91)

    await adapter.before_run(
        agent=object(),
        session=AgentSession(),
        context=context,
        state={},
    )
    assert context.context_messages == {}

    collection.read_error = asyncio.CancelledError()
    with pytest.raises(asyncio.CancelledError):
        await adapter.before_run(
            agent=object(),
            session=AgentSession(),
            context=context,
            state={},
        )
