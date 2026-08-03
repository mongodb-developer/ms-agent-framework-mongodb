from __future__ import annotations

# pyright: reportPrivateUsage=false, reportUnknownMemberType=false
import asyncio
from collections.abc import AsyncIterator, Awaitable, Sequence
from typing import Any, cast

import pytest
from agent_framework import Embedding, GeneratedEmbeddings
from pymongo import DeleteOne, ReplaceOne

from samples.incremental_ingestion import IngestionSettings
from samples.ingestion_helpers import (
    IncrementalIngestor,
    IngestionDocument,
    MongoDBDocumentLoader,
)


class SourceCursor:
    def __init__(self, documents: list[dict[str, Any]]) -> None:
        self._documents = documents
        self._limit = len(documents)

    def sort(self, field: str, direction: int) -> SourceCursor:
        self._documents.sort(key=lambda document: document[field], reverse=direction < 0)
        return self

    def limit(self, limit: int) -> SourceCursor:
        self._limit = limit
        return self

    async def to_list(self, *, length: int | None) -> list[dict[str, Any]]:
        limit = self._limit if length is None else min(length, self._limit)
        return self._documents[:limit]


class SourceCollection:
    def __init__(self, documents: list[dict[str, Any]]) -> None:
        self._documents = documents
        self.reads: list[tuple[dict[str, Any], dict[str, int]]] = []

    def find(self, query: dict[str, Any], projection: dict[str, int]) -> SourceCursor:
        self.reads.append((query, projection))
        bounds = query["source_key"]
        documents = [
            dict(document)
            for document in self._documents
            if document["source_key"] >= bounds["$gte"]
            and document["source_key"] < bounds["$lt"]
            and document["source_key"] > bounds.get("$gt", "")
        ]
        return SourceCursor(documents)


@pytest.mark.asyncio
async def test_loader_pages_only_prefixed_documents_into_neutral_records() -> None:
    collection = SourceCollection(
        [
            {
                "source_key": "sample-test-a",
                "body": "alpha",
                "heading": "A",
                "source_url": "https://example.invalid/a",
                "attributes": {"section": 1},
                "tenant": "sample-tenant",
            },
            {
                "source_key": "sample-test-b",
                "body": "beta",
                "heading": "B",
                "source_url": "https://example.invalid/b",
                "attributes": {"section": 2},
                "tenant": "sample-tenant",
            },
            {
                "source_key": "production-c",
                "body": "must not load",
                "heading": "C",
                "source_url": "https://example.invalid/c",
                "attributes": {},
                "tenant": "production",
            },
        ]
    )
    loader = MongoDBDocumentLoader(
        collection,
        sample_prefix="sample-test-",
        page_size=1,
        source_id_field="source_key",
        content_field="body",
        title_field="heading",
        url_field="source_url",
        metadata_field="attributes",
        tenant_field="tenant",
    )

    loaded = [document async for document in loader.load()]

    assert loaded == [
        IngestionDocument(
            source_id="sample-test-a",
            content="alpha",
            title="A",
            url="https://example.invalid/a",
            metadata={"section": 1},
            tenant_id="sample-tenant",
        ),
        IngestionDocument(
            source_id="sample-test-b",
            content="beta",
            title="B",
            url="https://example.invalid/b",
            metadata={"section": 2},
            tenant_id="sample-tenant",
        ),
    ]
    assert len(collection.reads) == 3
    assert all(
        read[1]
        == {
            "source_key": 1,
            "body": 1,
            "heading": 1,
            "source_url": 1,
            "attributes": 1,
            "tenant": 1,
            "deleted": 1,
        }
        for read in collection.reads
    )


@pytest.mark.parametrize(
    ("option", "value"),
    [
        ("sample_prefix", "production-"),
        ("page_size", 0),
        ("page_size", 1001),
        ("source_id_field", "$where"),
        ("content_field", "content..text"),
        ("title_field", "0"),
        ("url_field", "url\x00value"),
        ("metadata_field", ""),
        ("tenant_field", "$tenant"),
    ],
)
def test_loader_rejects_unbounded_or_unsafe_configuration(option: str, value: object) -> None:
    arguments: dict[str, object] = {"sample_prefix": "sample-test-"}
    arguments[option] = value

    with pytest.raises(ValueError):
        MongoDBDocumentLoader(SourceCollection([]), **cast(Any, arguments))


@pytest.mark.asyncio
async def test_loader_maps_dotted_fields_and_tombstones() -> None:
    collection = SourceCollection(
        [
            {
                "source_key": "sample-test-deleted",
                "payload": {
                    "body": "old content",
                    "metadata": {"section": 3},
                    "deleted": True,
                },
                "heading": "Removed",
                "source_url": "https://example.invalid/deleted",
                "tenant": "sample-tenant",
            }
        ]
    )
    loader = MongoDBDocumentLoader(
        collection,
        sample_prefix="sample-test-",
        content_field="payload.body",
        metadata_field="payload.metadata",
        deleted_field="payload.deleted",
        source_id_field="source_key",
        title_field="heading",
        url_field="source_url",
        tenant_field="tenant",
    )

    loaded = [document async for document in loader.load()]

    assert loaded == [
        IngestionDocument(
            source_id="sample-test-deleted",
            content="old content",
            title="Removed",
            url="https://example.invalid/deleted",
            metadata={"section": 3},
            tenant_id="sample-tenant",
            deleted=True,
        )
    ]


class Embeddings:
    additional_properties: dict[str, Any] = {}

    def __init__(self) -> None:
        self.calls: list[list[str]] = []

    def get_embeddings(
        self, values: Sequence[str], *, options: Any | None = None
    ) -> Awaitable[GeneratedEmbeddings[list[float], Any]]:
        del options
        self.calls.append(list(values))

        async def generate() -> GeneratedEmbeddings[list[float], Any]:
            return GeneratedEmbeddings(
                [Embedding(vector=[float(len(value)), 1.0, 0.0]) for value in values]
            )

        return generate()


class ResultCursor:
    def __init__(self, documents: list[dict[str, Any]]) -> None:
        self._documents = documents

    async def to_list(self, *, length: int | None) -> list[dict[str, Any]]:
        return self._documents if length is None else self._documents[:length]


class TargetCollection:
    def __init__(self) -> None:
        self.documents: dict[str, dict[str, Any]] = {}
        self.bulk_sizes: list[int] = []

    def find(self, query: dict[str, Any], projection: dict[str, int]) -> ResultCursor:
        del projection
        identifiers = query["_id"]["$in"]
        return ResultCursor(
            [
                {"_id": identifier, "content_hash": self.documents[identifier]["content_hash"]}
                for identifier in identifiers
                if identifier in self.documents
            ]
        )

    async def bulk_write(self, operations: list[Any], *, ordered: bool) -> object:
        assert ordered is False
        self.bulk_sizes.append(len(operations))
        for operation in operations:
            if isinstance(operation, ReplaceOne):
                assert operation._upsert is True
                write_filter = cast(dict[str, Any], operation._filter)
                replacement = cast(dict[str, Any], operation._doc)
                self.documents[write_filter["_id"]] = replacement
            elif isinstance(operation, DeleteOne):
                delete_filter = cast(dict[str, Any], operation._filter)
                self.documents.pop(delete_filter["_id"], None)
            else:
                raise AssertionError(f"Unexpected write model: {operation!r}")
        return object()

    async def delete_many(self, query: dict[str, Any]) -> object:
        bounds = query["_id"]
        deleted_count = 0
        for identifier in list(self.documents):
            if bounds["$gte"] <= identifier < bounds["$lt"]:
                del self.documents[identifier]
                deleted_count += 1
        return type("DeleteResult", (), {"deleted_count": deleted_count})()


async def documents(*values: IngestionDocument) -> AsyncIterator[IngestionDocument]:
    for value in values:
        yield value


@pytest.mark.asyncio
async def test_incremental_ingestion_is_deterministic_and_skips_unchanged_content() -> None:
    target = TargetCollection()
    embeddings = Embeddings()
    ingestor = IncrementalIngestor(
        target,
        embeddings,
        sample_prefix="sample-ingest-",
        vector_dimensions=3,
        batch_size=2,
    )
    initial = IngestionDocument(
        source_id="sample-source-a",
        content="alpha",
        title="Alpha",
        url="https://example.invalid/a",
        metadata={"section": 1},
        tenant_id="sample-tenant",
    )

    first = await ingestor.ingest(documents(initial))
    second = await ingestor.ingest(documents(initial))
    changed = await ingestor.ingest(
        documents(
            IngestionDocument(
                source_id=initial.source_id,
                content="alpha changed",
                title=initial.title,
                url=initial.url,
                metadata=initial.metadata,
                tenant_id=initial.tenant_id,
            )
        )
    )

    identifier = "sample-ingest-7f0dae11dec9aa8c1377c337540ef1428de7814b3ce02da948853db71492fc2a"
    assert (first.scanned, first.upserted, first.unchanged) == (1, 1, 0)
    assert (second.scanned, second.upserted, second.unchanged) == (1, 0, 1)
    assert (changed.scanned, changed.upserted, changed.unchanged) == (1, 1, 0)
    assert embeddings.calls == [["alpha"], ["alpha changed"]]
    assert target.bulk_sizes == [1, 1]
    assert target.documents[identifier]["_id"] == identifier
    assert target.documents[identifier]["content"] == "alpha changed"
    assert target.documents[identifier]["embedding"] == [13.0, 1.0, 0.0]


@pytest.mark.asyncio
@pytest.mark.parametrize("vectors", [[], [[1.0, 2.0]]])
async def test_incremental_ingestion_validates_embedding_batches(
    vectors: list[list[float]],
) -> None:
    class InvalidEmbeddings:
        async def get_embeddings(self, values: Sequence[str]) -> list[Embedding[list[float]]]:
            del values
            return [Embedding(vector=vector) for vector in vectors]

    ingestor = IncrementalIngestor(
        TargetCollection(),
        InvalidEmbeddings(),
        sample_prefix="test-ingest-validation-",
        vector_dimensions=3,
    )
    item = IngestionDocument(
        source_id="test-source-validation",
        content="content",
        title="title",
        url="https://example.invalid/validation",
        metadata={},
        tenant_id="test-tenant",
    )

    with pytest.raises(ValueError, match="Embedding"):
        await ingestor.ingest(documents(item))


@pytest.mark.asyncio
async def test_incremental_ingestion_handles_tombstones_and_targeted_cleanup() -> None:
    target = TargetCollection()
    ingestor = IncrementalIngestor(
        target,
        Embeddings(),
        sample_prefix="test-ingest-cleanup-",
        vector_dimensions=3,
    )
    item = IngestionDocument(
        source_id="test-source-cleanup",
        content="content",
        title="title",
        url="https://example.invalid/cleanup",
        metadata={},
        tenant_id="test-tenant",
    )
    await ingestor.ingest(documents(item))
    target.documents["production-record"] = {"content_hash": "preserve"}

    removal = await ingestor.ingest(
        documents(
            IngestionDocument(
                source_id=item.source_id,
                content=item.content,
                title=item.title,
                url=item.url,
                metadata=item.metadata,
                tenant_id=item.tenant_id,
                deleted=True,
            )
        )
    )
    await ingestor.ingest(documents(item))
    cleaned = await ingestor.cleanup()

    assert (removal.deleted, removal.upserted) == (1, 0)
    assert cleaned == 1
    assert target.documents == {"production-record": {"content_hash": "preserve"}}


@pytest.mark.asyncio
async def test_incremental_ingestion_uses_validated_target_field_paths() -> None:
    class RecordingTarget:
        def __init__(self) -> None:
            self.query: dict[str, Any] = {}
            self.operation: ReplaceOne[dict[str, Any]] | None = None

        def find(self, query: dict[str, Any], projection: dict[str, int]) -> ResultCursor:
            self.query = query
            assert projection == {"record.id": 1, "ingestion.hash": 1}
            return ResultCursor([])

        async def bulk_write(self, operations: list[Any], *, ordered: bool) -> object:
            del ordered
            self.operation = cast(ReplaceOne[dict[str, Any]], operations[0])
            return object()

    target = RecordingTarget()
    ingestor = IncrementalIngestor(
        target,
        Embeddings(),
        sample_prefix="test-ingest-fields-",
        vector_dimensions=3,
        id_field="record.id",
        content_field="rag.content",
        vector_field="rag.vector",
        content_hash_field="ingestion.hash",
        title_field="source.title",
        url_field="source.url",
        metadata_field="source.metadata",
        tenant_field="security.tenant",
    )
    item = IngestionDocument(
        source_id="test-source-fields",
        content="content",
        title="title",
        url="https://example.invalid/fields",
        metadata={"section": 4},
        tenant_id="test-tenant",
    )

    await ingestor.ingest(documents(item))

    identifier = (
        "test-ingest-fields-6aecfc80340d4eff2a331975b1701ed5701220d2f64dfbe84944859b665afa41"
    )
    assert list(target.query) == ["record.id"]
    assert target.operation is not None
    assert target.operation._filter == {"record.id": identifier}
    replacement = cast(dict[str, Any], target.operation._doc)
    assert replacement["record"] == {"id": identifier}
    assert replacement["rag"] == {
        "content": "content",
        "vector": [7.0, 1.0, 0.0],
    }
    assert replacement["source"] == {
        "title": "title",
        "url": "https://example.invalid/fields",
        "metadata": {"section": 4},
    }
    assert replacement["security"] == {"tenant": "test-tenant"}
    assert len(replacement["ingestion"]["hash"]) == 64


@pytest.mark.asyncio
async def test_incremental_ingestion_rejects_duplicate_source_ids() -> None:
    item = IngestionDocument(
        source_id="test-source-duplicate",
        content="content",
        title="title",
        url="https://example.invalid/duplicate",
        metadata={},
        tenant_id="test-tenant",
    )
    ingestor = IncrementalIngestor(
        TargetCollection(),
        Embeddings(),
        sample_prefix="test-ingest-duplicate-",
        vector_dimensions=3,
        batch_size=1,
    )

    with pytest.raises(ValueError, match="source_id"):
        await ingestor.ingest(documents(item, item))


@pytest.mark.asyncio
async def test_incremental_ingestion_refreshes_vectors_when_model_changes() -> None:
    target = TargetCollection()
    item = IngestionDocument(
        source_id="test-source-model",
        content="content",
        title="title",
        url="https://example.invalid/model",
        metadata={},
        tenant_id="test-tenant",
    )
    first_embeddings = Embeddings()
    second_embeddings = Embeddings()
    first = IncrementalIngestor(
        target,
        first_embeddings,
        sample_prefix="test-ingest-model-",
        vector_dimensions=3,
        embedding_model="model-v1",
    )
    second = IncrementalIngestor(
        target,
        second_embeddings,
        sample_prefix="test-ingest-model-",
        vector_dimensions=3,
        embedding_model="model-v2",
    )

    await first.ingest(documents(item))
    result = await second.ingest(documents(item))

    assert result.upserted == 1
    assert second_embeddings.calls == [["content"]]


@pytest.mark.asyncio
async def test_incremental_ingestion_preserves_batch_bounds_and_cancellation() -> None:
    items = [
        IngestionDocument(
            source_id=f"test-source-batch-{index}",
            content=f"content {index}",
            title=f"title {index}",
            url=f"https://example.invalid/batch/{index}",
            metadata={},
            tenant_id="test-tenant",
        )
        for index in range(3)
    ]
    target = TargetCollection()
    embeddings = Embeddings()
    ingestor = IncrementalIngestor(
        target,
        embeddings,
        sample_prefix="test-ingest-batch-",
        vector_dimensions=3,
        batch_size=2,
    )

    result = await ingestor.ingest(documents(*items))

    assert result.scanned == 3
    assert target.bulk_sizes == [2, 1]
    assert embeddings.calls == [["content 0", "content 1"], ["content 2"]]

    class CancellingEmbeddings:
        async def get_embeddings(self, values: Sequence[str]) -> None:
            del values
            raise asyncio.CancelledError

    cancelling = IncrementalIngestor(
        TargetCollection(),
        CancellingEmbeddings(),
        sample_prefix="test-ingest-cancel-",
        vector_dimensions=3,
    )
    with pytest.raises(asyncio.CancelledError):
        await cancelling.ingest(documents(items[0]))


def test_ingestion_sample_requires_explicit_write_and_model_configuration() -> None:
    with pytest.raises(
        RuntimeError,
        match="MONGODB_INGESTION_URI, MONGODB_DATABASE",
    ):
        IngestionSettings.from_environment({})

    settings = IngestionSettings.from_environment(
        {
            "MONGODB_INGESTION_URI": "mongodb://example.invalid",
            "MONGODB_DATABASE": "sample_database",
            "MONGODB_INGESTION_SOURCE_COLLECTION": "sample_source",
            "MONGODB_RAG_COLLECTION": "sample_knowledge",
            "MONGODB_RAG_VECTOR_INDEX": "sample_vector",
            "MONGODB_RAG_VECTOR_DIMENSIONS": "3",
            "MONGODB_RAG_SAMPLE_PREFIX": "sample-run-123-",
            "MONGODB_EMBEDDING_MODEL": "example-model",
            "MONGODB_EMBEDDING_FACTORY": "example_embeddings:create",
        }
    )

    assert settings.sample_prefix == "sample-run-123-"
    assert settings.vector_dimensions == 3
    assert settings.embedding_factory == "example_embeddings:create"
