"""MongoDB client construction and immutable ownership tracking."""

from __future__ import annotations

from collections.abc import Awaitable, Callable
from inspect import isawaitable
from types import TracebackType
from typing import Protocol, cast

from pymongo import AsyncMongoClient

from ..errors import MongoDBConfigurationError


class _ClosableClient(Protocol):
    def close(self) -> None | Awaitable[None]: ...


class MongoClientHandle:
    """Retain a MongoDB client and whether this package owns its lifetime."""

    def __init__(self, client: _ClosableClient, *, owns_client: bool) -> None:
        self._client = client
        self._owns_client = owns_client
        self._closed = False

    @classmethod
    def from_uri(
        cls,
        uri: str,
        *,
        client_factory: Callable[[str], _ClosableClient] | None = None,
    ) -> MongoClientHandle:
        if not uri.strip():
            raise MongoDBConfigurationError("MongoDB connection URI must not be empty.")

        factory = client_factory or cast(Callable[[str], _ClosableClient], AsyncMongoClient)
        return MongoClientHandle(factory(uri), owns_client=True)

    @classmethod
    def from_client(cls, client: _ClosableClient) -> MongoClientHandle:
        return MongoClientHandle(client, owns_client=False)

    @property
    def client(self) -> _ClosableClient:
        return self._client

    @property
    def owns_client(self) -> bool:
        return self._owns_client

    async def close(self) -> None:
        if not self._owns_client or self._closed:
            return

        self._closed = True
        close_result = self._client.close()
        if isawaitable(close_result):
            await close_result

    async def __aenter__(self) -> MongoClientHandle:
        return self

    async def __aexit__(
        self,
        exc_type: type[BaseException] | None,
        exc_value: BaseException | None,
        traceback: TracebackType | None,
    ) -> None:
        await self.close()
