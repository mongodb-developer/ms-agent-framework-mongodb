from __future__ import annotations

import asyncio
from collections.abc import Awaitable, Sequence
from typing import Any, cast

import pytest
from agent_framework import (
    AgentSession,
    Embedding,
    GeneratedEmbeddings,
    Message,
    SessionContext,
)
from pymongo.errors import OperationFailure

from agent_framework_mongodb import (
    EqualFilter,
    GreaterThanOrEqualFilter,
    MongoDBCapabilityError,
    MongoDBIndexMismatchError,
    MongoDBRAGContextProvider,
    MongoDBRAGParentOptions,
    MongoDBRAGProvider,
    MongoDBRAGProviderOptions,
    MongoDBRAGSearchOptions,
    MongoDBRetrievalError,
    MongoDBSearchMode,
)


class FakeEmbeddingGenerator:
    additional_properties: dict[str, Any] = {}

    def __init__(self) -> None:
        self.calls: list[list[str]] = []

    async def _generate(self, values: Sequence[str]) -> GeneratedEmbeddings[list[float], Any]:
        self.calls.append(list(values))
        return GeneratedEmbeddings([Embedding(vector=[1.0, 0.0, 0.5]) for _ in values])

    def get_embeddings(
        self,
        values: Sequence[str],
        *,
        options: Any | None = None,
    ) -> Awaitable[GeneratedEmbeddings[list[float], Any]]:
        del options
        return self._generate(values)


class FakeCursor:
    def __init__(
        self,
        documents: list[dict[str, Any]],
        error: BaseException | None = None,
    ) -> None:
        self.documents = documents
        self.error = error

    async def to_list(self, *, length: int | None) -> list[dict[str, Any]]:
        if self.error is not None:
            raise self.error
        return self.documents if length is None else self.documents[:length]


class FakeDatabase:
    def __init__(self) -> None:
        self.command_calls: list[dict[str, Any] | str] = []
        self.explain_error: BaseException | None = None
        self.server_version = "8.0.0"
        self.collections: dict[str, FakeCollection] = {}

    def __getitem__(self, name: str) -> FakeCollection:
        return self.collections[name]

    async def command(self, command: dict[str, Any] | str) -> dict[str, Any]:
        self.command_calls.append(command)
        if command == "buildInfo":
            return {"version": self.server_version}
        if command == "hello":
            return {"msg": "isdbgrid"}
        if isinstance(command, dict) and "explain" in command and self.explain_error is not None:
            raise self.explain_error
        return {"ok": 1}


class FakeCollection:
    name = "knowledge"

    def __init__(self) -> None:
        self.database = FakeDatabase()
        self.pipeline: list[dict[str, Any]] | None = None
        self.pipelines: list[list[dict[str, Any]]] = []
        self.documents: list[dict[str, Any]] = []
        self.aggregate_responses: list[list[dict[str, Any]]] = []
        self.aggregate_error: BaseException | None = None
        self.cursor_error: BaseException | None = None
        self.search_indexes: list[dict[str, Any]] = [
            {
                "name": "knowledge_vector",
                "type": "vectorSearch",
                "status": "READY",
                "queryable": True,
                "latestDefinition": {
                    "fields": [
                        {
                            "type": "vector",
                            "path": "embedding",
                            "numDimensions": 3,
                            "similarity": "cosine",
                        },
                        {"type": "filter", "path": "tenant_id"},
                        {"type": "filter", "path": "published_year"},
                        {"type": "filter", "path": "record_type"},
                    ]
                },
            },
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
                                "searchAnalyzer": "lucene.standard",
                            },
                            "tenant_id": {"type": "token"},
                            "published_year": {"type": "number"},
                            "record_type": {"type": "token"},
                        },
                    }
                },
            },
        ]

    async def aggregate(self, pipeline: list[dict[str, Any]]) -> FakeCursor:
        self.pipeline = pipeline
        self.pipelines.append(pipeline)
        if self.aggregate_error is not None:
            raise self.aggregate_error
        documents = self.aggregate_responses.pop(0) if self.aggregate_responses else self.documents
        return FakeCursor(documents, self.cursor_error)

    async def list_search_indexes(self, *, name: str) -> FakeCursor:
        return FakeCursor([index for index in self.search_indexes if index["name"] == name])


def hybrid_options(**overrides: Any) -> MongoDBRAGProviderOptions:
    values: dict[str, Any] = {
        "mode": MongoDBSearchMode.HYBRID_RRF,
        "vector_dimensions": 3,
        "vector_index_name": "knowledge_vector",
        "search_index_name": "knowledge_search",
        "filter": EqualFilter("tenant_id", "tenant-a"),
    }
    values.update(overrides)
    return MongoDBRAGProviderOptions(**values)


def document_keys(value: object) -> set[str]:
    if isinstance(value, dict):
        result: set[str] = set()
        for key, child in cast(dict[object, object], value).items():
            if isinstance(key, str):
                result.add(key)
            result.update(document_keys(child))
        return result
    if isinstance(value, list):
        result = set()
        for child in cast(list[object], value):
            result.update(document_keys(child))
        return result
    return set()


async def test_hybrid_search_uses_native_rank_fusion_with_filters_in_both_inputs() -> None:
    collection = FakeCollection()
    collection.documents = [
        {
            "_id": "guide-1",
            "document_id": "guide-1",
            "content": "Native reciprocal-rank fusion.",
            "_ragScore": 0.031,
        }
    ]
    embeddings = FakeEmbeddingGenerator()
    provider = MongoDBRAGProvider(
        hybrid_options(
            id_field="document_id",
            top_k=4,
            num_candidates=20,
            vector_weight=2.0,
            text_weight=0.5,
        ),
        embedding_generator=embeddings,
        collection=collection,  # type: ignore[arg-type]
    )

    results = await provider.search(
        "hybrid query",
        options=MongoDBRAGSearchOptions(
            top_k=2,
            num_candidates=12,
            filter=GreaterThanOrEqualFilter("published_year", 2025),
            include_score_details=True,
        ),
    )

    assert embeddings.calls == [["hybrid query"]]
    assert collection.pipeline == [
        {
            "$rankFusion": {
                "input": {
                    "pipelines": {
                        "vector": [
                            {
                                "$vectorSearch": {
                                    "index": "knowledge_vector",
                                    "path": "embedding",
                                    "queryVector": [1.0, 0.0, 0.5],
                                    "numCandidates": 12,
                                    "limit": 12,
                                    "filter": {
                                        "$and": [
                                            {"tenant_id": {"$eq": "tenant-a"}},
                                            {"published_year": {"$gte": 2025}},
                                        ]
                                    },
                                }
                            }
                        ],
                        "text": [
                            {
                                "$search": {
                                    "index": "knowledge_search",
                                    "compound": {
                                        "must": [
                                            {
                                                "text": {
                                                    "query": "hybrid query",
                                                    "path": ["content"],
                                                }
                                            }
                                        ],
                                        "filter": [
                                            {
                                                "equals": {
                                                    "path": "tenant_id",
                                                    "value": "tenant-a",
                                                }
                                            },
                                            {
                                                "range": {
                                                    "path": "published_year",
                                                    "gte": 2025,
                                                }
                                            },
                                        ],
                                    },
                                }
                            },
                            {"$limit": 12},
                        ],
                    }
                },
                "combination": {"weights": {"vector": 2.0, "text": 0.5}},
                "scoreDetails": True,
            }
        },
        {
            "$set": {
                "_ragScore": {"$meta": "score"},
                "_ragScoreDetails": {"$meta": "scoreDetails"},
            }
        },
        {"$match": {"_ragScore": {"$gt": 0}}},
        {"$sort": {"_ragScore": -1, "document_id": 1}},
        {
            "$group": {
                "_id": "$document_id",
                "_ragDocument": {"$first": "$$ROOT"},
                "_ragScore": {"$first": "$_ragScore"},
            }
        },
        {"$replaceWith": {"$mergeObjects": ["$_ragDocument", {"_ragScore": "$_ragScore"}]}},
        {"$sort": {"_ragScore": -1, "document_id": 1}},
        {"$limit": 2},
    ]
    assert [(result.id, result.score) for result in results] == [("guide-1", 0.031)]
    assert collection.pipeline is not None
    assert {"$out", "$merge"}.isdisjoint(document_keys(collection.pipeline))


async def test_hybrid_validates_both_indexes_and_all_filter_paths_before_embedding() -> None:
    collection = FakeCollection()
    search_index = collection.search_indexes[1]
    search_index["latestDefinition"]["mappings"]["fields"].pop("published_year")
    embeddings = FakeEmbeddingGenerator()
    provider = MongoDBRAGProvider(
        hybrid_options(),
        embedding_generator=embeddings,
        collection=collection,  # type: ignore[arg-type]
    )

    with pytest.raises(MongoDBIndexMismatchError, match="filter"):
        await provider.search(
            "query",
            options=MongoDBRAGSearchOptions(
                filter=GreaterThanOrEqualFilter("published_year", 2025)
            ),
        )

    assert embeddings.calls == []
    assert collection.database.command_calls == []
    assert collection.pipeline is None


@pytest.mark.parametrize(
    ("index_kind", "validation_method"),
    [
        ("vector", "validate_vector_search_index"),
        ("search", "validate_search_index"),
    ],
)
async def test_parent_index_validation_requires_child_discriminator_in_both_indexes(
    index_kind: str,
    validation_method: str,
) -> None:
    collection = FakeCollection()
    if index_kind == "vector":
        fields = collection.search_indexes[0]["latestDefinition"]["fields"]
        collection.search_indexes[0]["latestDefinition"]["fields"] = [
            field for field in fields if field.get("path") != "record_type"
        ]
    else:
        collection.search_indexes[1]["latestDefinition"]["mappings"]["fields"].pop("record_type")
    provider = MongoDBRAGProvider(
        hybrid_options(parent=MongoDBRAGParentOptions()),
        embedding_generator=FakeEmbeddingGenerator(),
        collection=collection,  # type: ignore[arg-type]
    )

    with pytest.raises(MongoDBIndexMismatchError, match="record_type"):
        await getattr(provider, validation_method)()


async def test_hybrid_caches_only_confirmed_unsupported_rank_fusion_evidence() -> None:
    collection = FakeCollection()
    collection.database.explain_error = OperationFailure(
        "Unrecognized pipeline stage name: '$rankFusion'",
        code=40324,
        details={
            "codeName": "Location40324",
            "errmsg": "Unrecognized pipeline stage name: '$rankFusion'",
        },
    )
    embeddings = FakeEmbeddingGenerator()
    provider = MongoDBRAGProvider(
        hybrid_options(),
        embedding_generator=embeddings,
        collection=collection,  # type: ignore[arg-type]
    )

    for _ in range(2):
        with pytest.raises(MongoDBCapabilityError, match=r"native \$rankFusion.*unavailable"):
            await provider.search("query")

    explain_calls = [
        command
        for command in collection.database.command_calls
        if isinstance(command, dict) and "explain" in command
    ]
    assert len(explain_calls) == 1
    assert embeddings.calls == []
    assert collection.pipeline is None


async def test_hybrid_rejects_and_caches_a_confirmed_pre_8_server_before_embedding() -> None:
    collection = FakeCollection()
    collection.database.server_version = "7.0.18"
    embeddings = FakeEmbeddingGenerator()
    provider = MongoDBRAGProvider(
        hybrid_options(),
        embedding_generator=embeddings,
        collection=collection,  # type: ignore[arg-type]
    )

    for _ in range(2):
        with pytest.raises(MongoDBCapabilityError, match="MongoDB 8.0"):
            await provider.search("query")

    assert collection.database.command_calls == ["buildInfo", "hello"]
    assert embeddings.calls == []


async def test_hybrid_rechecks_supported_capability_and_preserves_raw_fused_score() -> None:
    collection = FakeCollection()
    document = {
        "_id": "physical-1",
        "content": "Fused result",
        "_ragScore": 0.024,
        "_ragScoreDetails": {"value": 0.024},
    }
    collection.documents = [document]
    provider = MongoDBRAGProvider(
        hybrid_options(),
        embedding_generator=FakeEmbeddingGenerator(),
        collection=collection,  # type: ignore[arg-type]
    )

    first = await provider.search("first")
    second = await provider.search("second")

    explain_calls = [
        command
        for command in collection.database.command_calls
        if isinstance(command, dict) and "explain" in command
    ]
    assert len(explain_calls) == 2
    assert first[0].score == second[0].score == 0.024
    assert first[0].raw_document is document


async def test_hybrid_does_not_cache_an_inconclusive_capability_failure() -> None:
    collection = FakeCollection()
    collection.database.explain_error = OperationFailure("unknown probe failure", code=8)
    collection.documents = [{"_id": "doc-1", "content": "Recovered result", "_ragScore": 0.02}]
    provider = MongoDBRAGProvider(
        hybrid_options(),
        embedding_generator=FakeEmbeddingGenerator(),
        collection=collection,  # type: ignore[arg-type]
    )

    with pytest.raises(MongoDBRetrievalError):
        await provider.search("first")
    collection.database.explain_error = None
    results = await provider.search("second")

    explain_calls = [
        command
        for command in collection.database.command_calls
        if isinstance(command, dict) and "explain" in command
    ]
    assert len(explain_calls) == 2
    assert [result.id for result in results] == ["doc-1"]


@pytest.mark.parametrize("boundary", ["capability", "embedding", "aggregate", "cursor"])
async def test_hybrid_propagates_cancellation_from_every_async_boundary(boundary: str) -> None:
    class CancellingEmbeddingGenerator(FakeEmbeddingGenerator):
        async def _generate(
            self,
            values: Sequence[str],
        ) -> GeneratedEmbeddings[list[float], Any]:
            del values
            raise asyncio.CancelledError

    collection = FakeCollection()
    embedding_generator: FakeEmbeddingGenerator = FakeEmbeddingGenerator()
    if boundary == "capability":
        collection.database.explain_error = asyncio.CancelledError()
    elif boundary == "embedding":
        embedding_generator = CancellingEmbeddingGenerator()
    elif boundary == "aggregate":
        collection.aggregate_error = asyncio.CancelledError()
    else:
        collection.cursor_error = asyncio.CancelledError()
    provider = MongoDBRAGProvider(
        hybrid_options(),
        embedding_generator=embedding_generator,
        collection=collection,  # type: ignore[arg-type]
    )

    with pytest.raises(asyncio.CancelledError):
        await provider.search("query")


async def test_hybrid_context_provider_fails_open_only_for_transient_retrieval(
    caplog: pytest.LogCaptureFixture,
) -> None:
    collection = FakeCollection()
    collection.aggregate_error = OperationFailure(
        "sensitive-host.invalid secret query",
        code=91,
    )
    direct = MongoDBRAGProvider(
        hybrid_options(),
        embedding_generator=FakeEmbeddingGenerator(),
        collection=collection,  # type: ignore[arg-type]
    )
    provider = MongoDBRAGContextProvider(direct)
    context = SessionContext(input_messages=[Message("user", ["secret hybrid query"])])

    await provider.before_run(
        agent=object(),
        session=AgentSession(),
        context=context,
        state={},
    )

    assert context.context_messages == {}
    assert "secret hybrid query" not in caplog.text
    assert "sensitive-host" not in caplog.text


async def test_hybrid_context_provider_does_not_suppress_capability_failure() -> None:
    collection = FakeCollection()
    collection.database.server_version = "7.0.18"
    provider = MongoDBRAGContextProvider(
        MongoDBRAGProvider(
            hybrid_options(),
            embedding_generator=FakeEmbeddingGenerator(),
            collection=collection,  # type: ignore[arg-type]
        )
    )

    with pytest.raises(MongoDBCapabilityError):
        await provider.before_run(
            agent=object(),
            session=AgentSession(),
            context=SessionContext(input_messages=[Message("user", ["query"])]),
            state={},
        )


async def test_hybrid_parent_hydration_reapplies_only_mandatory_authorization() -> None:
    collection = FakeCollection()
    parent_document = {
        "_id": "parent-1",
        "tenant_id": "tenant-a",
        "record_type": "parent",
        "content": "Authorized parent",
    }
    child_document = {
        "_id": "chunk-1",
        "parent_id": "parent-1",
        "tenant_id": "tenant-a",
        "record_type": "child",
        "content": "matching child",
        "_ragScore": 0.03,
    }
    collection.aggregate_responses = [[child_document], [parent_document]]
    provider = MongoDBRAGProvider(
        hybrid_options(parent=MongoDBRAGParentOptions()),
        embedding_generator=FakeEmbeddingGenerator(),
        collection=collection,  # type: ignore[arg-type]
    )

    results = await provider.search(
        "parent query",
        options=MongoDBRAGSearchOptions(filter=GreaterThanOrEqualFilter("published_year", 2025)),
    )

    assert [(result.id, result.text, result.score) for result in results] == [
        ("parent-1", "Authorized parent", 0.03)
    ]
    assert results[0].raw_document is parent_document
    rank_fusion = collection.pipelines[0][0]["$rankFusion"]
    vector_filter = rank_fusion["input"]["pipelines"]["vector"][0]["$vectorSearch"]["filter"]
    search_filter = rank_fusion["input"]["pipelines"]["text"][0]["$search"]["compound"]["filter"]
    assert vector_filter == {
        "$and": [
            {
                "$and": [
                    {"tenant_id": {"$eq": "tenant-a"}},
                    {"published_year": {"$gte": 2025}},
                ]
            },
            {"record_type": {"$eq": "child"}},
        ]
    }
    assert search_filter == [
        {
            "compound": {
                "filter": [
                    {"equals": {"path": "tenant_id", "value": "tenant-a"}},
                    {"range": {"path": "published_year", "gte": 2025}},
                ]
            }
        },
        {"equals": {"path": "record_type", "value": "child"}},
    ]
    assert collection.pipelines[1] == [
        {
            "$match": {
                "$and": [
                    {"_id": {"$in": ["parent-1"]}},
                    {"tenant_id": {"$eq": "tenant-a"}},
                ]
            }
        }
    ]
