from __future__ import annotations

import os
import uuid
from datetime import timedelta
from typing import Any

import pytest
from agent_framework import Content, Message
from pymongo import AsyncMongoClient

from agent_framework_mongodb import MongoDBHistoryProvider, MongoDBHistoryProviderOptions

pytestmark = pytest.mark.integration_history


@pytest.fixture
def mongodb_settings() -> tuple[str, str]:
    uri = os.getenv("MONGODB_URI")
    database = os.getenv("MONGODB_DATABASE")
    if not uri or not database:
        pytest.skip("MONGODB_URI and MONGODB_DATABASE are required for integration-history tests")
    return uri, database


async def test_exact_history_reload_continuation_isolation_and_clear(
    mongodb_settings: tuple[str, str],
) -> None:
    uri, database_name = mongodb_settings
    collection_name = f"af_history_test_{uuid.uuid4().hex}"
    client: AsyncMongoClient[dict[str, Any]] = AsyncMongoClient(uri)
    collection = client[database_name][collection_name]

    def history_options(tenant_id: str) -> MongoDBHistoryProviderOptions:
        return MongoDBHistoryProviderOptions(
            tenant_id=tenant_id,
            application_id="integration-history",
            agent_id="history-agent",
            session_id="session-a",
            retention=timedelta(days=1),
            max_messages=3,
        )

    provider = MongoDBHistoryProvider(
        collection,
        options=history_options("tenant-a"),
    )
    reloaded = MongoDBHistoryProvider(
        collection,
        options=history_options("tenant-a"),
    )
    other_tenant = MongoDBHistoryProvider(
        collection,
        options=history_options("tenant-b"),
    )
    try:
        await provider.ensure_indexes()
        await provider.validate_indexes()
        first = Message(
            "user",
            [
                Content(type="text", text="weather"),
                Content(type="uri", uri="https://example.invalid/radar.png"),
            ],
            message_id="input-1",
            additional_properties={"fixture": {"lossless": True}},
        )
        call = Message(
            "assistant",
            [
                Content(
                    type="function_call",
                    call_id="weather-1",
                    name="weather",
                    arguments={"city": "London"},
                )
            ],
            message_id="call-1",
        )
        result = Message(
            "tool",
            [Content(type="function_result", call_id="weather-1", result={"temperature": 19})],
            message_id="result-1",
        )
        await provider.save_messages("session-a", [first, call, result])
        await provider.save_messages("session-a", [first, call, result])
        await other_tenant.save_messages(
            "session-a",
            [Message("user", ["must stay isolated"], message_id="other-1")],
        )
        continued = Message("assistant", ["It is 19 C."], message_id="answer-1")
        await reloaded.save_messages("session-a", [continued])

        restored = await reloaded.get_messages("session-a")

        assert [message.message_id for message in restored] == ["call-1", "result-1", "answer-1"]
        assert restored[0].contents[0].type == "function_call"
        assert restored[1].contents[0].type == "function_result"
        assert await reloaded.clear_messages("session-a") == 4
        assert [message.message_id for message in await other_tenant.get_messages("session-a")] == [
            "other-1"
        ]
        assert await other_tenant.clear_messages("session-a") == 1
    finally:
        assert collection_name.startswith("af_history_test_")
        await client[database_name].drop_collection(collection_name)
        await client.close()
