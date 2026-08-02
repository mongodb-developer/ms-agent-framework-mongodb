from __future__ import annotations

import json
from pathlib import Path
from typing import Any, cast

from agent_framework import Message

from agent_framework_mongodb import MongoDBHistoryProvider, MongoDBHistoryProviderOptions


class Cursor:
    def __init__(self, documents: list[dict[str, Any]]) -> None:
        self.documents = documents
        self.maximum = len(documents)

    def sort(self, _field: str, _direction: int) -> Cursor:
        self.documents.sort(key=lambda document: document["sequence"], reverse=True)
        return self

    def limit(self, value: int) -> Cursor:
        self.maximum = value
        return self

    async def to_list(self, *, length: int | None) -> list[dict[str, Any]]:
        return self.documents[: min(length or self.maximum, self.maximum)]


class Collection:
    def __init__(self) -> None:
        self.documents: list[dict[str, Any]] = []
        self.sequence = 0

    async def find_one_and_update(self, *args: Any, **_kwargs: Any) -> dict[str, int]:
        update = cast(dict[str, dict[str, int]], args[1])
        self.sequence += update["$inc"]["sequence"]
        return {"sequence": self.sequence}

    async def find_one(self, query: dict[str, Any]) -> dict[str, Any] | None:
        return next(
            (
                document
                for document in self.documents
                if all(document.get(key) == value for key, value in query.items())
            ),
            None,
        )

    async def insert_one(self, document: dict[str, Any]) -> object:
        if any(existing["_id"] == document["_id"] for existing in self.documents):
            from pymongo.errors import DuplicateKeyError

            raise DuplicateKeyError("duplicate")
        self.documents.append(document)
        return object()

    async def delete_one(self, query: dict[str, Any]) -> object:
        self.documents = [
            document
            for document in self.documents
            if not all(document.get(key) == value for key, value in query.items())
        ]
        return object()

    def find(self, query: dict[str, Any]) -> Cursor:
        return Cursor(
            [
                document
                for document in self.documents
                if all(document.get(key) == value for key, value in query.items())
            ]
        )


async def test_language_neutral_history_order_and_retry_contract() -> None:
    fixture = json.loads(
        (Path(__file__).parent / "fixtures" / "history_contract.json").read_text(encoding="utf-8")
    )
    scope = fixture["scope"]
    collection = Collection()
    provider = MongoDBHistoryProvider(
        cast(Any, collection),
        options=MongoDBHistoryProviderOptions(
            tenant_id=scope["tenant_id"],
            application_id=scope["application_id"],
            agent_id=scope["agent_id"],
            user_id=scope["user_id"],
            session_id=scope["session_id"],
            max_messages=fixture["max_messages"],
        ),
    )
    messages = [
        Message(item["role"], [item["text"]], message_id=item["message_id"])
        for item in fixture["messages"]
    ]

    await provider.save_messages(scope["session_id"], messages)
    await provider.save_messages(scope["session_id"], messages)
    restored = await provider.get_messages(scope["session_id"])

    assert len(collection.documents) == fixture["retry_expected_document_count"]
    assert [message.message_id for message in restored] == fixture[
        "expected_latest_chronological_ids"
    ]
