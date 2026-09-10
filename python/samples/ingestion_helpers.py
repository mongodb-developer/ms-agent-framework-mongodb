"""Non-production helpers for the incremental ingestion sample."""

from __future__ import annotations

import hashlib
import json
import math
from collections.abc import AsyncIterable, AsyncIterator, Mapping, Sequence
from dataclasses import dataclass
from typing import Any, cast

from pymongo import DeleteOne, ReplaceOne
from pymongo.collation import Collation

_SIMPLE_COLLATION = Collation(locale="simple")


class IngestionDataError(ValueError):
    """Raised when sample source data cannot be ingested deterministically."""


@dataclass(frozen=True)
class IngestionDocument:
    """Ingestion-neutral document emitted by the sample loader."""

    source_id: str
    content: str
    title: str
    url: str
    metadata: Mapping[str, object]
    tenant_id: str
    deleted: bool = False


@dataclass(frozen=True)
class IngestionResult:
    """Counts from one bounded incremental ingestion pass."""

    scanned: int = 0
    upserted: int = 0
    unchanged: int = 0
    deleted: int = 0


class IncrementalIngestor:
    """Sample-only, write-capable incremental ingestion helper."""

    def __init__(
        self,
        collection: Any,
        embedding_generator: Any,
        *,
        sample_prefix: str,
        vector_dimensions: int,
        embedding_model: str = "sample-model",
        batch_size: int = 100,
        id_field: str = "_id",
        source_id_field: str = "source_id",
        content_field: str = "content",
        vector_field: str = "embedding",
        embedding_model_field: str = "embedding_model",
        content_hash_field: str = "content_hash",
        title_field: str = "title",
        url_field: str = "url",
        metadata_field: str = "metadata",
        tenant_field: str = "tenant_id",
    ) -> None:
        if not sample_prefix.startswith(("sample-", "test-")):
            raise ValueError("sample_prefix must start with 'sample-' or 'test-'.")
        if isinstance(vector_dimensions, bool) or vector_dimensions <= 0:
            raise ValueError("vector_dimensions must be a positive integer.")
        if isinstance(batch_size, bool) or not 1 <= batch_size <= 1000:
            raise ValueError("batch_size must be an integer from 1 through 1000.")
        self._collection = collection
        self._embedding_generator = embedding_generator
        self._sample_prefix = sample_prefix
        self._prefix_upper_bound = _exclusive_prefix_upper_bound(sample_prefix)
        self._vector_dimensions = vector_dimensions
        if not embedding_model.strip():
            raise ValueError("embedding_model must be a non-empty caller-provided identifier.")
        self._embedding_model = embedding_model
        self._batch_size = batch_size
        configured_fields = {
            name: _validated_field(value, name)
            for name, value in {
                "id_field": id_field,
                "source_id_field": source_id_field,
                "content_field": content_field,
                "vector_field": vector_field,
                "embedding_model_field": embedding_model_field,
                "content_hash_field": content_hash_field,
                "title_field": title_field,
                "url_field": url_field,
                "metadata_field": metadata_field,
                "tenant_field": tenant_field,
            }.items()
        }
        _validate_distinct_paths(configured_fields)
        self._id_field = configured_fields["id_field"]
        self._source_id_field = configured_fields["source_id_field"]
        self._content_field = configured_fields["content_field"]
        self._vector_field = configured_fields["vector_field"]
        self._embedding_model_field = configured_fields["embedding_model_field"]
        self._content_hash_field = configured_fields["content_hash_field"]
        self._title_field = configured_fields["title_field"]
        self._url_field = configured_fields["url_field"]
        self._metadata_field = configured_fields["metadata_field"]
        self._tenant_field = configured_fields["tenant_field"]

    async def ingest(self, source: AsyncIterable[IngestionDocument]) -> IngestionResult:
        """Ingest bounded batches, replacing only changed sample documents."""
        result = IngestionResult()
        batch: list[IngestionDocument] = []
        seen_source_ids: set[str] = set()
        async for document in source:
            if document.source_id in seen_source_ids:
                raise ValueError(f"Duplicate source_id '{document.source_id}' in ingestion pass.")
            seen_source_ids.add(document.source_id)
            batch.append(document)
            if len(batch) == self._batch_size:
                result = _combine(result, await self._ingest_batch(batch))
                batch = []
        if batch:
            result = _combine(result, await self._ingest_batch(batch))
        return result

    async def cleanup(self) -> int:
        """Delete only records owned by this sample prefix."""
        result = await self._collection.delete_many(
            {
                self._id_field: {
                    "$gte": self._sample_prefix,
                    "$lt": self._prefix_upper_bound,
                }
            },
            collation=_SIMPLE_COLLATION,
        )
        return int(result.deleted_count)

    async def _ingest_batch(self, documents: Sequence[IngestionDocument]) -> IngestionResult:
        prepared = [
            (
                _document_id(self._sample_prefix, document.source_id),
                _content_hash(document, self._embedding_model, self._vector_dimensions),
                document,
            )
            for document in documents
        ]
        cursor = self._collection.find(
            {self._id_field: {"$in": [identifier for identifier, _, _ in prepared]}},
            {self._id_field: 1, self._content_hash_field: 1},
        )
        existing_documents = await cursor.to_list(length=len(prepared))
        existing = {
            _resolve(item, self._id_field): _resolve(item, self._content_hash_field)
            for item in existing_documents
        }
        changed = [
            item for item in prepared if not item[2].deleted and existing.get(item[0]) != item[1]
        ]
        vectors: list[list[float]] = []
        if changed:
            generated = await self._embedding_generator.get_embeddings(
                [document.content for _, _, document in changed]
            )
            vectors = [
                _validated_vector(embedding.vector, self._vector_dimensions)
                for embedding in generated
            ]
            if len(vectors) != len(changed):
                raise ValueError(
                    "Embedding generator returned a different number of vectors than inputs."
                )

        operations: list[Any] = []
        for (identifier, content_hash, document), vector in zip(changed, vectors, strict=True):
            operations.append(
                ReplaceOne(
                    {self._id_field: identifier},
                    self._replacement_document(identifier, content_hash, document, vector),
                    upsert=True,
                )
            )
        deleted = 0
        for identifier, _, document in prepared:
            if document.deleted and identifier in existing:
                operations.append(DeleteOne({self._id_field: identifier}))
                deleted += 1
        if operations:
            await self._collection.bulk_write(operations, ordered=False)
        return IngestionResult(
            scanned=len(prepared),
            upserted=len(changed),
            unchanged=len(prepared) - len(changed) - deleted,
            deleted=deleted,
        )

    def _replacement_document(
        self,
        identifier: str,
        content_hash: str,
        document: IngestionDocument,
        vector: list[float],
    ) -> dict[str, Any]:
        replacement: dict[str, Any] = {}
        for path, value in (
            (self._id_field, identifier),
            (self._source_id_field, document.source_id),
            (self._content_field, document.content),
            (self._vector_field, vector),
            (self._embedding_model_field, self._embedding_model),
            (self._content_hash_field, content_hash),
            (self._title_field, document.title),
            (self._url_field, document.url),
            (self._metadata_field, dict(document.metadata)),
            (self._tenant_field, document.tenant_id),
        ):
            _set_path(replacement, path, value)
        return replacement


class MongoDBDocumentLoader:
    """Page through sample-prefixed MongoDB source documents."""

    def __init__(
        self,
        collection: Any,
        *,
        sample_prefix: str,
        page_size: int = 100,
        source_id_field: str = "source_id",
        content_field: str = "content",
        title_field: str = "title",
        url_field: str = "url",
        metadata_field: str = "metadata",
        tenant_field: str = "tenant_id",
        deleted_field: str = "deleted",
    ) -> None:
        if not sample_prefix.startswith(("sample-", "test-")):
            raise ValueError("sample_prefix must start with 'sample-' or 'test-'.")
        if isinstance(page_size, bool) or not 1 <= page_size <= 1000:
            raise ValueError("page_size must be an integer from 1 through 1000.")
        self._collection = collection
        self._sample_prefix = sample_prefix
        self._prefix_upper_bound = _exclusive_prefix_upper_bound(sample_prefix)
        self._page_size = page_size
        self._source_id_field = _validated_field(source_id_field, "source_id_field")
        self._content_field = _validated_field(content_field, "content_field")
        self._title_field = _validated_field(title_field, "title_field")
        self._url_field = _validated_field(url_field, "url_field")
        self._metadata_field = _validated_field(metadata_field, "metadata_field")
        self._tenant_field = _validated_field(tenant_field, "tenant_field")
        self._deleted_field = _validated_field(deleted_field, "deleted_field")

    async def load(self) -> AsyncIterator[IngestionDocument]:
        """Yield mapped documents in stable source-ID order."""
        await self._validate_unique_source_ids()
        last_source_id: str | None = None
        projection = {
            self._source_id_field: 1,
            self._content_field: 1,
            self._title_field: 1,
            self._url_field: 1,
            self._metadata_field: 1,
            self._tenant_field: 1,
            self._deleted_field: 1,
        }
        while True:
            bounds = {
                "$gte": self._sample_prefix,
                "$lt": self._prefix_upper_bound,
            }
            if last_source_id is not None:
                bounds["$gt"] = last_source_id
            cursor = (
                self._collection.find({self._source_id_field: bounds}, projection)
                .collation(_SIMPLE_COLLATION)
                .sort(self._source_id_field, 1)
                .limit(self._page_size)
            )
            page = await cursor.to_list(length=self._page_size)
            if not page:
                return
            for item in page:
                source_id = _resolve(item, self._source_id_field)
                yield IngestionDocument(
                    source_id=source_id,
                    content=_resolve(item, self._content_field),
                    title=_resolve(item, self._title_field),
                    url=_resolve(item, self._url_field),
                    metadata=_resolve(item, self._metadata_field),
                    tenant_id=_resolve(item, self._tenant_field),
                    deleted=bool(_resolve(item, self._deleted_field, default=False)),
                )
                last_source_id = source_id

    async def _validate_unique_source_ids(self) -> None:
        duplicate_cursor = await self._collection.aggregate(
            [
                {
                    "$match": {
                        self._source_id_field: {
                            "$gte": self._sample_prefix,
                            "$lt": self._prefix_upper_bound,
                        }
                    }
                },
                {
                    "$group": {
                        "_id": f"${self._source_id_field}",
                        "count": {"$sum": 1},
                    }
                },
                {"$match": {"count": {"$gt": 1}}},
                {"$limit": 1},
            ],
            collation=_SIMPLE_COLLATION,
        )
        if await duplicate_cursor.to_list(length=1):
            raise IngestionDataError(
                "Source contains a duplicate source ID within the sample prefix."
            )


def _exclusive_prefix_upper_bound(prefix: str) -> str:
    try:
        prefix.encode("utf-8")
    except UnicodeEncodeError as exc:
        raise ValueError("sample_prefix must contain valid Unicode scalar values.") from exc
    for index in range(len(prefix) - 1, -1, -1):
        code_point = ord(prefix[index])
        if code_point == 0x10FFFF:
            continue
        successor = code_point + 1
        if 0xD800 <= successor <= 0xDFFF:
            successor = 0xE000
        return f"{prefix[:index]}{chr(successor)}"
    raise ValueError("sample_prefix has no exclusive Unicode successor.")


def _validated_field(value: str, name: str) -> str:
    if not value or "\x00" in value:
        raise ValueError(f"{name} must be a non-empty safe field path.")
    segments = value.split(".")
    if any(
        not segment or segment.startswith("$") or segment.isdecimal() or segment == "$[]"
        for segment in segments
    ):
        raise ValueError(f"{name} must be a safe field path.")
    return value


def _validate_distinct_paths(fields: Mapping[str, str]) -> None:
    values = list(fields.values())
    for index, left in enumerate(values):
        for right in values[index + 1 :]:
            if left == right or left.startswith(f"{right}.") or right.startswith(f"{left}."):
                raise ValueError("Configured target field paths must not overlap.")


def _set_path(document: dict[str, Any], path: str, value: Any) -> None:
    current: dict[str, Any] = document
    segments = path.split(".")
    for segment in segments[:-1]:
        nested: Any = current.setdefault(segment, {})
        if not isinstance(nested, dict):
            raise ValueError("Configured target field paths must not overlap.")
        current = cast(dict[str, Any], nested)
    current[segments[-1]] = value


def _resolve(document: Mapping[str, Any], path: str, *, default: Any = ...) -> Any:
    current: object = document
    for segment in path.split("."):
        if not isinstance(current, Mapping) or segment not in current:
            if default is not ...:
                return default
            raise ValueError(f"Source document is missing configured field '{path}'.")
        current = cast(Mapping[str, object], current)[segment]
    return cast(Any, current)


def _document_id(prefix: str, source_id: str) -> str:
    return f"{prefix}{hashlib.sha256(source_id.encode('utf-8')).hexdigest()}"


def _content_hash(document: IngestionDocument, embedding_model: str, dimensions: int) -> str:
    payload = json.dumps(
        {
            "content": document.content,
            "embedding_dimensions": dimensions,
            "embedding_model": embedding_model,
            "metadata": document.metadata,
            "tenant_id": document.tenant_id,
            "title": document.title,
            "url": document.url,
        },
        ensure_ascii=False,
        separators=(",", ":"),
        sort_keys=True,
    )
    return hashlib.sha256(payload.encode("utf-8")).hexdigest()


def _validated_vector(vector: Sequence[float], dimensions: int) -> list[float]:
    if len(vector) != dimensions:
        raise ValueError(f"Embedding vector must contain exactly {dimensions} dimensions.")
    normalized = [float(value) for value in vector]
    if not all(math.isfinite(value) for value in normalized):
        raise ValueError("Embedding vector values must be finite.")
    return normalized


def _combine(left: IngestionResult, right: IngestionResult) -> IngestionResult:
    return IngestionResult(
        scanned=left.scanned + right.scanned,
        upserted=left.upserted + right.upserted,
        unchanged=left.unchanged + right.unchanged,
        deleted=left.deleted + right.deleted,
    )
