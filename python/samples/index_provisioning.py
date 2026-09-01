"""Explicit, provisioner-only MongoDB RAG index mutation sample.

This command creates or updates indexes on the configured collection. Review the
target and run it only with a dedicated provisioner identity.
"""

from __future__ import annotations

import argparse
import asyncio
import os
import sys
from collections.abc import Awaitable, Sequence
from typing import Any

from agent_framework import Embedding, GeneratedEmbeddings

from agent_framework_mongodb import (
    MongoDBRAGContextProvider,
    MongoDBRAGProvider,
    MongoDBRAGProviderOptions,
    MongoDBSearchMode,
)


class CollectionEmbeddingGenerator:
    """Replace with the generator used for the existing knowledge collection."""

    additional_properties: dict[str, Any] = {}

    def __init__(self, dimensions: int) -> None:
        self.dimensions = dimensions

    def get_embeddings(
        self, values: Sequence[str], *, options: Any | None = None
    ) -> Awaitable[GeneratedEmbeddings[list[float], Any]]:
        del options

        async def generate() -> GeneratedEmbeddings[list[float], Any]:
            vector = [1.0, *([0.0] * (self.dimensions - 1))]
            return GeneratedEmbeddings([Embedding(vector=vector) for _ in values])

        return generate()


def required(name: str) -> str:
    value = os.getenv(name)
    if not value:
        raise RuntimeError(f"Set {name} before running index provisioning.")
    return value


def positive_integer(value: str) -> int:
    """Parse a strictly positive integer before provider construction."""
    try:
        parsed = int(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("must be a positive integer") from exc
    if parsed <= 0:
        raise argparse.ArgumentTypeError("must be a positive integer")
    return parsed


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Explicitly create or update MongoDB RAG indexes. This mutates the configured "
            "collection and requires a provisioner identity."
        )
    )
    parser.add_argument(
        "--apply",
        action="store_true",
        help="confirm that index create/update operations should be submitted",
    )
    parser.add_argument(
        "--vector-dimensions",
        type=positive_integer,
        default=os.getenv("MONGODB_RAG_VECTOR_DIMENSIONS"),
        help="embedding dimensions (or set MONGODB_RAG_VECTOR_DIMENSIONS)",
    )
    options = parser.parse_args(argv)
    if not options.apply:
        parser.error("--apply is required because this command mutates indexes")
    if options.vector_dimensions is None:
        parser.error("--vector-dimensions or MONGODB_RAG_VECTOR_DIMENSIONS is required")
    return options


async def main(argv: Sequence[str] | None = None) -> None:
    options = parse_args(argv)
    dimensions = int(options.vector_dimensions)
    print(
        "WARNING: submitting explicit MongoDB Search index create/update operations.",
        file=sys.stderr,
    )
    direct = MongoDBRAGProvider(
        MongoDBRAGProviderOptions(
            mode=MongoDBSearchMode.HYBRID_RRF,
            vector_dimensions=dimensions,
            vector_field=os.getenv("MONGODB_RAG_VECTOR_FIELD", "embedding"),
            vector_index_name=required("MONGODB_RAG_VECTOR_INDEX"),
            search_index_name=required("MONGODB_RAG_SEARCH_INDEX"),
            text_fields=(os.getenv("MONGODB_RAG_TEXT_FIELD", "content"),),
        ),
        embedding_generator=CollectionEmbeddingGenerator(dimensions),
        connection_string=required("MONGODB_URI"),
        database_name=required("MONGODB_DATABASE"),
        collection_name=required("MONGODB_RAG_COLLECTION"),
    )
    provider = MongoDBRAGContextProvider(direct)
    async with provider:
        vector = await provider.ensure_vector_search_index(
            wait_until_ready=True, timeout=600, poll_interval=2
        )
        search = await provider.ensure_search_index(
            wait_until_ready=True, timeout=600, poll_interval=2
        )
        print(vector.definition.name, vector.state.value)
        print(search.definition.name, search.state.value)


if __name__ == "__main__":
    asyncio.run(main())
