"""Verify the Agent Framework hooks against the optional SDK boundary."""

from __future__ import annotations

import asyncio
import logging
import sys
from threading import Event
from types import ModuleType, SimpleNamespace
from unittest.mock import Mock, create_autospec

import pytest
from agent_framework import (
    AgentResponse,
    AgentSession,
    Content,
    Message,
    SessionContext,
    SupportsAgentRun,
)

from agent_framework_mongodb.atlas_agent_engine_memory import AtlasAgentEngineMemoryContextProvider
from agent_framework_mongodb.errors import MongoDBAuthorizationError, MongoDBConfigurationError


class ConnectionFailure(Exception):
    pass


class AuthorizationFailure(Exception):
    pass


class OtherSDKError(Exception):
    pass


@pytest.fixture
def memory(monkeypatch: pytest.MonkeyPatch) -> Mock:
    sdk = ModuleType("agent_engine_sdk_memory")
    client = Mock()
    sdk.__dict__.update(Memory=Mock, MemoryRequestContext=SimpleNamespace)
    for name in (
        "MemoryClientError",
        "MemoryIdentityError",
        "MemoryNotSupportedError",
        "MemoryNotProvisionedError",
        "MemoryBadRequestError",
        "MemoryServerError",
        "MemoryAPIError",
    ):
        sdk.__dict__[name] = OtherSDKError
    sdk.__dict__.update(
        MemoryAuthError=AuthorizationFailure, MemoryConnectionError=ConnectionFailure
    )
    monkeypatch.setitem(sys.modules, sdk.__name__, sdk)
    return client


async def test_scoped_recall_is_attributed_to_the_managed_provider(memory: Mock) -> None:
    memory.build_context.return_value = SimpleNamespace(
        selected_memories=[SimpleNamespace(content="Prefers tea")]
    )
    provider = AtlasAgentEngineMemoryContextProvider(memory, user_id="customer-1")
    session = AgentSession(session_id="current")
    context = SessionContext(
        session_id=session.session_id, input_messages=[Message("user", ["What should I drink?"])]
    )
    await provider.before_run(
        agent=create_autospec(SupportsAgentRun), session=session, context=context, state={}
    )
    memory.build_context.assert_called_once_with(
        query="What should I drink?",
        user_id="customer-1",
        session_id=None,
        visibility="private",
        enabled_sources={"semantic", "episodic"},
        max_tokens=2048,
        include_memories=True,
    )
    recalled = context.get_messages(sources={provider.source_id})
    assert [message.text for message in recalled] == ["Prefers tea"]
    assert recalled[0].additional_properties["_attribution"]["source_id"] == provider.source_id


async def test_recording_is_opt_in_and_replays_only_the_same_scoped_keys(memory: Mock) -> None:
    session = AgentSession(session_id="current")
    current = Message("user", ["New input"], message_id="message-1")
    context = SessionContext(
        session_id=session.session_id,
        input_messages=[current],
        context_messages={
            "mongodb-history": [Message("user", ["Prior conversation"])],
            "mongodb-memory": [Message("assistant", ["Recalled direct memory"])],
        },
    )
    context._response = AgentResponse(  # pyright: ignore[reportPrivateUsage]
        messages=[
            Message("assistant", [Content.from_function_call("tool-1", "lookup")]),
            Message("tool", [Content.from_function_result("tool-1", result="tool output")]),
            Message("assistant", ["New response"]),
        ],
        response_id="response-1",
    )
    agent = create_autospec(SupportsAgentRun)
    await AtlasAgentEngineMemoryContextProvider(memory, user_id="customer-1").after_run(
        agent=agent, session=session, context=context, state={}
    )
    memory.bind.assert_not_called()
    bound = memory.bind.return_value
    bound.record_turn.return_value = SimpleNamespace(acknowledged=True)
    provider = AtlasAgentEngineMemoryContextProvider(
        memory, user_id="customer-1", record_turns=True
    )
    for _ in range(2):
        await provider.after_run(agent=agent, session=session, context=context, state={})
    assert memory.bind.call_args.args[0] == SimpleNamespace(
        user_id="customer-1", session_id="current"
    )
    turns = [call.kwargs for call in bound.record_turn.call_args_list]
    assert [(turn["role"], turn["content"]) for turn in turns[:2]] == [
        ("user", "New input"),
        ("assistant", "New response"),
    ]
    assert turns[:2] == turns[2:]
    assert turns[0]["idempotency_key"] != turns[1]["idempotency_key"]
    isolated = AtlasAgentEngineMemoryContextProvider(memory, user_id="other", record_turns=True)
    await isolated.after_run(agent=agent, session=session, context=context, state={})
    other_key = bound.record_turn.call_args_list[4].kwargs["idempotency_key"]
    assert other_key != turns[0]["idempotency_key"]


async def test_short_term_recall_requires_and_forwards_highest_session_scope(memory: Mock) -> None:
    provider = AtlasAgentEngineMemoryContextProvider(
        memory, user_id="customer-1", include_short_term=True
    )
    session = AgentSession(session_id="current")
    agent = create_autospec(SupportsAgentRun)
    context = SessionContext(input_messages=[Message("user", ["query"])])
    with pytest.raises(MongoDBConfigurationError, match="session_id"):
        await provider.before_run(agent=agent, session=session, context=context, state={})
    memory.build_context.assert_not_called()
    context.session_id = "current"
    memory.build_context.return_value = SimpleNamespace(selected_memories=[])
    await provider.before_run(agent=agent, session=session, context=context, state={})
    assert memory.build_context.call_args.kwargs["session_id"] == "current"
    assert "stm" in memory.build_context.call_args.kwargs["enabled_sources"]
    assert not context.get_messages()


async def test_operational_recall_fails_open_with_redacted_logs_but_auth_surfaces(
    memory: Mock, caplog: pytest.LogCaptureFixture
) -> None:
    provider = AtlasAgentEngineMemoryContextProvider(memory, user_id="private-user")
    context = SessionContext(input_messages=[Message("user", ["private message"])])
    session = AgentSession()
    agent = create_autospec(SupportsAgentRun)
    caplog.set_level(logging.INFO, logger="agent_framework_mongodb")
    memory.build_context.side_effect = ConnectionFailure("private transport response")
    await provider.before_run(agent=agent, session=session, context=context, state={})
    assert not context.get_messages()
    assert "private" not in caplog.text
    assert "failed" in caplog.text
    memory.build_context.side_effect = AuthorizationFailure("credential")
    with pytest.raises(MongoDBAuthorizationError):
        await provider.before_run(agent=agent, session=session, context=context, state={})
    assert "credential" not in caplog.text


async def test_cancellation_is_propagated_without_blocking_event_loop(memory: Mock) -> None:
    started = asyncio.Event()
    release = Event()
    loop = asyncio.get_running_loop()

    def wait_for_release(**_: object) -> SimpleNamespace:
        loop.call_soon_threadsafe(started.set)
        assert release.wait(timeout=5)
        return SimpleNamespace(selected_memories=[])

    memory.build_context.side_effect = wait_for_release
    provider = AtlasAgentEngineMemoryContextProvider(memory, user_id="customer-1")
    task = asyncio.create_task(
        provider.before_run(
            agent=create_autospec(SupportsAgentRun),
            session=AgentSession(),
            context=SessionContext(input_messages=[Message("user", ["query"])]),
            state={},
        )
    )
    try:
        await asyncio.wait_for(started.wait(), timeout=2)
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task
    finally:
        release.set()
