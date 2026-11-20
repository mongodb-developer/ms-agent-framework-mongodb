"""Credentialed adapter round trip; use only a disposable Atlas Agent Engine project."""

from __future__ import annotations

import asyncio
import os
from unittest.mock import create_autospec
from uuid import uuid4

import pytest
from agent_framework import AgentResponse, AgentSession, Message, SessionContext, SupportsAgentRun

from agent_framework_mongodb.atlas_agent_engine_memory import AtlasAgentEngineMemoryContextProvider

pytestmark = pytest.mark.integration_atlas_agent_engine


async def test_hosted_turn_round_trip_is_private_and_session_scoped() -> None:
    project_id = os.getenv("ATLAS_AGENT_ENGINE_TEST_PROJECT_ID")
    token = os.getenv("AGENTIC_MEMORY_SERVICE_ACCOUNT_TOKEN")
    if not project_id or not token or os.getenv("ATLAS_AGENT_ENGINE_TEST_DISPOSABLE") != "1":
        pytest.skip("Requires an explicitly disposable, provisioned test project and token.")
    sdk = pytest.importorskip("agent_engine_sdk_memory")
    prefix = f"maf-atlas-test-{uuid4().hex}"
    agent = create_autospec(SupportsAgentRun)
    owner_session = AgentSession(session_id=f"{prefix}-session")
    other_session = AgentSession(session_id=f"{prefix}-other-session")
    marker = f"Synthetic preference for tea, reference {prefix}"
    with sdk.Memory(project_id=project_id, service_account_token=token) as sdk_memory:
        writer = AtlasAgentEngineMemoryContextProvider(
            sdk_memory,
            user_id=f"{prefix}-owner",
            record_turns=True,
            persistence_fail_fast=True,
            include_short_term=True,
        )
        other_user = AtlasAgentEngineMemoryContextProvider(
            sdk_memory, user_id=f"{prefix}-other", include_short_term=True
        )
        context = SessionContext(
            session_id=owner_session.session_id,
            input_messages=[Message("user", [marker], message_id=f"{prefix}-input")],
        )
        context._response = AgentResponse(  # pyright: ignore[reportPrivateUsage]
            messages=[Message("assistant", ["Synthetic acknowledgement"], message_id=prefix)]
        )
        for _ in range(2):
            await writer.after_run(agent=agent, session=owner_session, context=context, state={})

        async def recall(
            provider: AtlasAgentEngineMemoryContextProvider, session: AgentSession
        ) -> str:
            query = SessionContext(
                session_id=session.session_id, input_messages=[Message("user", [marker])]
            )
            await provider.before_run(agent=agent, session=session, context=query, state={})
            return "\n".join(message.text for message in query.get_messages())

        for _ in range(15):
            if marker in await recall(writer, owner_session):
                break
            await asyncio.sleep(2)
        else:
            pytest.fail("The hosted adapter did not recall its synthetic turn.")
        assert marker not in await recall(other_user, owner_session)
        assert marker not in await recall(writer, other_session)
