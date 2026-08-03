"""Sample-grade incremental ingestion for an existing MongoDB RAG collection."""

from __future__ import annotations

import argparse
import asyncio
import importlib
import os
from collections.abc import Mapping, Sequence
from dataclasses import dataclass
from typing import Any

from pymongo import AsyncMongoClient

from agent_framework_mongodb import (
    MongoDBRAGProvider,
    MongoDBRAGProviderOptions,
    MongoDBSearchMode,
)
from samples.ingestion_helpers import IncrementalIngestor, MongoDBDocumentLoader


@dataclass(frozen=True)
class IngestionSettings:
    """Required external configuration for the write-capable sample process."""

    connection_string: str
    database_name: str
    source_collection: str
    target_collection: str
    vector_index: str
    vector_dimensions: int
    sample_prefix: str
    embedding_model: str
    embedding_factory: str

    @classmethod
    def from_environment(cls, environment: Mapping[str, str] = os.environ) -> IngestionSettings:
        """Load settings while reporting all missing values together."""
        names = (
            "MONGODB_INGESTION_URI",
            "MONGODB_DATABASE",
            "MONGODB_INGESTION_SOURCE_COLLECTION",
            "MONGODB_RAG_COLLECTION",
            "MONGODB_RAG_VECTOR_INDEX",
            "MONGODB_RAG_VECTOR_DIMENSIONS",
            "MONGODB_RAG_SAMPLE_PREFIX",
            "MONGODB_EMBEDDING_MODEL",
            "MONGODB_EMBEDDING_FACTORY",
        )
        missing = [name for name in names if not environment.get(name)]
        if missing:
            raise RuntimeError(f"Set required ingestion sample variables: {', '.join(missing)}.")
        try:
            dimensions = int(environment["MONGODB_RAG_VECTOR_DIMENSIONS"])
        except ValueError as exc:
            raise RuntimeError("MONGODB_RAG_VECTOR_DIMENSIONS must be a positive integer.") from exc
        if dimensions <= 0:
            raise RuntimeError("MONGODB_RAG_VECTOR_DIMENSIONS must be a positive integer.")
        prefix = environment["MONGODB_RAG_SAMPLE_PREFIX"]
        if not prefix.startswith(("sample-", "test-")):
            raise RuntimeError("MONGODB_RAG_SAMPLE_PREFIX must start with 'sample-' or 'test-'.")
        return cls(
            connection_string=environment["MONGODB_INGESTION_URI"],
            database_name=environment["MONGODB_DATABASE"],
            source_collection=environment["MONGODB_INGESTION_SOURCE_COLLECTION"],
            target_collection=environment["MONGODB_RAG_COLLECTION"],
            vector_index=environment["MONGODB_RAG_VECTOR_INDEX"],
            vector_dimensions=dimensions,
            sample_prefix=prefix,
            embedding_model=environment["MONGODB_EMBEDDING_MODEL"],
            embedding_factory=environment["MONGODB_EMBEDDING_FACTORY"],
        )


def load_embedding_generator(factory_path: str, model: str) -> Any:
    """Create the caller-provided generator named as ``module:callable``."""
    module_name, separator, attribute = factory_path.partition(":")
    if not separator or not module_name or not attribute:
        raise RuntimeError("MONGODB_EMBEDDING_FACTORY must use 'module:callable' syntax.")
    try:
        factory = getattr(importlib.import_module(module_name), attribute)
        generator = factory(model)
    except Exception as exc:
        raise RuntimeError("Could not create the configured embedding generator.") from exc
    if not callable(getattr(generator, "get_embeddings", None)):
        raise RuntimeError("The embedding factory must return a get_embeddings generator.")
    return generator


def positive_bounded_integer(value: str) -> int:
    """Parse a batch or page size from 1 through 1000."""
    try:
        parsed = int(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("must be an integer from 1 through 1000") from exc
    if not 1 <= parsed <= 1000:
        raise argparse.ArgumentTypeError("must be an integer from 1 through 1000")
    return parsed


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Sample-only write-capable ingestion for an existing RAG collection. "
            "Use a dedicated ingestion identity, never runtime retrieval credentials."
        )
    )
    parser.add_argument("--apply", action="store_true", help="confirm sample document writes")
    parser.add_argument(
        "--cleanup",
        action="store_true",
        help="delete only target documents owned by MONGODB_RAG_SAMPLE_PREFIX",
    )
    parser.add_argument("--page-size", type=positive_bounded_integer, default=100)
    parser.add_argument("--batch-size", type=positive_bounded_integer, default=100)
    options = parser.parse_args(argv)
    if not options.apply:
        parser.error("--apply is required because this sample writes to MongoDB")
    return options


async def main(argv: Sequence[str] | None = None) -> None:
    """Validate index readiness, then ingest or clean sample-prefixed records."""
    arguments = parse_args(argv)
    settings = IngestionSettings.from_environment()
    if settings.source_collection == settings.target_collection:
        raise RuntimeError(
            "Source and target collections must differ for this bounded demo loader."
        )
    generator = load_embedding_generator(settings.embedding_factory, settings.embedding_model)
    client: AsyncMongoClient[dict[str, Any]] = AsyncMongoClient(settings.connection_string)
    database = client[settings.database_name]
    source = database[settings.source_collection]
    target = database[settings.target_collection]
    ingestor = IncrementalIngestor(
        target,
        generator,
        sample_prefix=settings.sample_prefix,
        vector_dimensions=settings.vector_dimensions,
        embedding_model=settings.embedding_model,
        batch_size=arguments.batch_size,
        content_field=os.getenv("MONGODB_RAG_TEXT_FIELD", "content"),
        vector_field=os.getenv("MONGODB_RAG_VECTOR_FIELD", "embedding"),
    )
    provider = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.VECTOR_ANN,
            vector_dimensions=settings.vector_dimensions,
            vector_index_name=settings.vector_index,
            text_fields=(os.getenv("MONGODB_RAG_TEXT_FIELD", "content"),),
            vector_field=os.getenv("MONGODB_RAG_VECTOR_FIELD", "embedding"),
        ),
        embedding_generator=generator,
        collection=target,
    )
    try:
        await provider.wait_until_vector_search_index_ready(timeout=600, poll_interval=2)
        if arguments.cleanup:
            deleted = await ingestor.cleanup()
            print(f"Removed {deleted} sample-owned target documents.")
            return
        loader = MongoDBDocumentLoader(
            source,
            sample_prefix=settings.sample_prefix,
            page_size=arguments.page_size,
            source_id_field=os.getenv("MONGODB_INGESTION_SOURCE_ID_FIELD", "source_id"),
            content_field=os.getenv("MONGODB_INGESTION_CONTENT_FIELD", "content"),
            title_field=os.getenv("MONGODB_INGESTION_TITLE_FIELD", "title"),
            url_field=os.getenv("MONGODB_INGESTION_URL_FIELD", "url"),
            metadata_field=os.getenv("MONGODB_INGESTION_METADATA_FIELD", "metadata"),
            tenant_field=os.getenv("MONGODB_INGESTION_TENANT_FIELD", "tenant_id"),
            deleted_field=os.getenv("MONGODB_INGESTION_DELETED_FIELD", "deleted"),
        )
        result = await ingestor.ingest(loader.load())
        print(
            f"Scanned {result.scanned}; upserted {result.upserted}; "
            f"unchanged {result.unchanged}; deleted {result.deleted}."
        )
    finally:
        await provider.close()
        await client.close()


if __name__ == "__main__":
    asyncio.run(main())
