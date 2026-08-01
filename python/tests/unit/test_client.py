from collections.abc import Awaitable

import pytest

from agent_framework_mongodb import MongoDBConfigurationError
from agent_framework_mongodb._shared.client import MongoClientHandle


class FakeClient:
    def __init__(self, *, asynchronous_close: bool = False) -> None:
        self.asynchronous_close = asynchronous_close
        self.close_count = 0

    def close(self) -> None | Awaitable[None]:
        if self.asynchronous_close:
            return self._close_async()
        self.close_count += 1
        return None

    async def _close_async(self) -> None:
        self.close_count += 1


@pytest.mark.parametrize("asynchronous_close", [False, True])
async def test_provider_created_client_is_closed_once(asynchronous_close: bool) -> None:
    client = FakeClient(asynchronous_close=asynchronous_close)
    handle = MongoClientHandle.from_uri("mongodb://localhost", client_factory=lambda _: client)

    await handle.close()
    await handle.close()

    assert handle.owns_client is True
    assert client.close_count == 1


async def test_injected_client_is_never_closed() -> None:
    client = FakeClient()
    handle = MongoClientHandle.from_client(client)

    await handle.close()

    assert handle.owns_client is False
    assert client.close_count == 0


async def test_context_manager_closes_owned_client_after_failure() -> None:
    client = FakeClient()

    with pytest.raises(RuntimeError, match="operation failed"):
        async with MongoClientHandle.from_uri(
            "mongodb://localhost", client_factory=lambda _: client
        ):
            raise RuntimeError("operation failed")

    assert client.close_count == 1


def test_empty_uri_fails_before_client_creation() -> None:
    factory_called = False

    def create_client(_: str) -> FakeClient:
        nonlocal factory_called
        factory_called = True
        return FakeClient()

    with pytest.raises(MongoDBConfigurationError, match="must not be empty"):
        MongoClientHandle.from_uri("  ", client_factory=create_client)

    assert factory_called is False
