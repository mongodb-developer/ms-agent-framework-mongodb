"""Persist and replay one exact, authorized Agent Framework conversation."""

from __future__ import annotations

import asyncio
import os

from agent_framework import Content, Message

from agent_framework_mongodb import MongoDBHistoryProvider, MongoDBHistoryProviderOptions


def required_environment(name: str) -> str:
    value = os.getenv(name)
    if not value:
        raise RuntimeError(f"{name} is required; set it before running this sample.")
    return value


async def main() -> None:
    provider = MongoDBHistoryProvider(
        options=MongoDBHistoryProviderOptions(
            application_id=required_environment("MONGODB_HISTORY_APPLICATION_ID"),
            agent_id=required_environment("MONGODB_HISTORY_AGENT_ID"),
            session_id=required_environment("MONGODB_HISTORY_SESSION_ID"),
            max_messages=20,
        ),
        connection_string=required_environment("MONGODB_URI"),
        database_name=required_environment("MONGODB_DATABASE"),
        collection_name=required_environment("MONGODB_HISTORY_COLLECTION"),
    )
    try:
        await provider.ensure_indexes()
        await provider.save_messages(
            None,
            [
                Message("user", ["What is the weather?"], message_id="sample-input"),
                Message(
                    "assistant",
                    [
                        Content(
                            type="function_call",
                            call_id="sample-call",
                            name="weather",
                            arguments={"city": "London"},
                        )
                    ],
                    message_id="sample-tool-call",
                ),
                Message(
                    "tool",
                    [
                        Content(
                            type="function_result",
                            call_id="sample-call",
                            result={"temperature": 19},
                        )
                    ],
                    message_id="sample-tool-result",
                ),
            ],
        )
        for message in await provider.get_messages(None):
            print(f"{message.role}: {message.text or message.contents[0].type}")
        if os.getenv("MONGODB_HISTORY_CLEAR", "").lower() == "true":
            print(f"Cleared {await provider.clear_messages()} messages.")
    finally:
        await provider.close()


if __name__ == "__main__":
    asyncio.run(main())
