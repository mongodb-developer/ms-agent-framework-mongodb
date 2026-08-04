"""Read bounded sample-prefixed documents into ingestion-neutral records."""

from __future__ import annotations

import argparse
import asyncio
import os
from collections.abc import Sequence
from typing import Any

from pymongo import AsyncMongoClient

try:
    from samples.ingestion_helpers import MongoDBDocumentLoader
except ModuleNotFoundError as exc:
    if exc.name != "samples":
        raise
    from ingestion_helpers import MongoDBDocumentLoader


def required(name: str) -> str:
    value = os.getenv(name, "").strip()
    if not value:
        raise RuntimeError(f"Set {name} before running the document loader.")
    return value


def bounded_integer(value: str) -> int:
    try:
        parsed = int(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("must be an integer from 1 through 1000") from exc
    if not 1 <= parsed <= 1000:
        raise argparse.ArgumentTypeError("must be an integer from 1 through 1000")
    return parsed


async def main(argv: Sequence[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--page-size", type=bounded_integer, default=100)
    parser.add_argument("--max-documents", type=bounded_integer, default=10)
    args = parser.parse_args(argv)
    connection_string = required("MONGODB_URI")
    database_name = required("MONGODB_DATABASE")
    collection_name = required("MONGODB_INGESTION_SOURCE_COLLECTION")
    sample_prefix = required("MONGODB_RAG_SAMPLE_PREFIX")
    client: AsyncMongoClient[dict[str, Any]] = AsyncMongoClient(connection_string)
    loader = MongoDBDocumentLoader(
        client[database_name][collection_name],
        sample_prefix=sample_prefix,
        page_size=args.page_size,
    )
    loaded = 0
    try:
        async for document in loader.load():
            print(
                f"{document.source_id}: title={document.title!r}, "
                f"tenant={document.tenant_id!r}, deleted={document.deleted}"
            )
            loaded += 1
            if loaded == args.max_documents:
                break
    finally:
        await client.close()
    print(f"Mapped {loaded} bounded source document(s).")


if __name__ == "__main__":
    asyncio.run(main())
