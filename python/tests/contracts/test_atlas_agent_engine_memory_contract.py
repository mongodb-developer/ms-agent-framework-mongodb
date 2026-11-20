"""Verify the official SDK wire contract through a real Agent Framework run."""

from __future__ import annotations

import json
from collections.abc import Awaitable, Sequence
from typing import Any, Literal, overload

import pytest
from agent_framework import (
    Agent,
    AgentSession,
    ChatResponse,
    ChatResponseUpdate,
    Message,
    ResponseStream,
)

from agent_framework_mongodb.atlas_agent_engine_memory import AtlasAgentEngineMemoryContextProvider


class FixtureChatClient:
    additional_properties: dict[str, Any] = {}

    def __init__(self) -> None:
        self.messages: list[Message] = []

    @overload
    def get_response(
        self, messages: Sequence[Message], *, stream: Literal[False] = False, **kwargs: Any
    ) -> Awaitable[ChatResponse[Any]]: ...

    @overload
    def get_response(
        self, messages: Sequence[Message], *, stream: Literal[True], **kwargs: Any
    ) -> ResponseStream[ChatResponseUpdate, ChatResponse[Any]]: ...

    def get_response(
        self, messages: Sequence[Message], *, stream: bool = False, **kwargs: Any
    ) -> Awaitable[ChatResponse[Any]]:
        assert not stream
        self.messages = list(messages)

        async def respond() -> ChatResponse[Any]:
            return ChatResponse(
                messages=[Message("assistant", ["Synthetic reply"], message_id="reply-1")],
                response_id="response-1",
            )

        return respond()


async def test_real_agent_and_sdk_serialize_scoped_recall_and_turns(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    sdk = pytest.importorskip("agent_engine_sdk_memory")
    httpx = pytest.importorskip("httpx")
    requests: list[tuple[str, dict[str, Any]]] = []

    def handle(request: Any) -> Any:
        body = json.loads(request.content)
        requests.append((request.url.path, body))
        if request.url.path.endswith("/context"):
            return httpx.Response(
                200,
                json={
                    "formatted_context": "unused server-formatted context",
                    "metadata": {},
                    "selected_memories": [
                        {
                            "id": "test-item",
                            "content": "Synthetic prior memory",
                            "source": "semantic",
                            "timestamp": "2026-01-01T00:00:00Z",
                        }
                    ],
                },
            )
        return httpx.Response(
            200, json={"id": "test-turn", "session_id": "session-1", "turn_seq": 1}
        )

    def client_factory(**kwargs: Any) -> Any:
        kwargs["transport"] = httpx.MockTransport(handle)
        return original_client(**kwargs)

    original_client = httpx.Client
    monkeypatch.setattr(httpx, "Client", client_factory)
    with sdk.Memory(
        service_account_token="mock-service-token",
        project_id="test-project",
        base_url="https://memory.example.invalid",
    ) as memory:
        provider = AtlasAgentEngineMemoryContextProvider(
            memory, user_id="user-1", record_turns=True, include_short_term=True
        )
        client = FixtureChatClient()
        agent = Agent(client=client, context_providers=[provider])
        await agent.run(
            Message("user", ["Synthetic input"], message_id="input-1"),
            session=AgentSession(session_id="session-1"),
        )
    assert [path for path, _ in requests] == [
        "/api/v1/projects/test-project/memory/context",
        "/api/v1/projects/test-project/memory/turns",
        "/api/v1/projects/test-project/memory/turns",
    ]
    recall, user_turn, assistant_turn = (item[1] for item in requests)
    assert recall["visibility"] == "private"
    assert recall["enabled_sources"] == ["episodic", "semantic", "stm"]
    assert all(body["user_id"] == "user-1" for body in (recall, user_turn, assistant_turn))
    assert all(body["session_id"] == "session-1" for body in (recall, user_turn, assistant_turn))
    assert [user_turn["content"], assistant_turn["content"]] == [
        "Synthetic input",
        "Synthetic reply",
    ]
    assert user_turn["idempotency_key"] != assistant_turn["idempotency_key"]
    assert any(
        message.text == "Synthetic prior memory"
        and message.additional_properties["_attribution"]["source_id"] == provider.source_id
        for message in client.messages
    )
