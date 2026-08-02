"""Public RAG provider seams before search-mode execution is installed."""

from __future__ import annotations

from typing import ClassVar

from agent_framework import ContextProvider

from ..errors import MongoDBCapabilityError, MongoDBConfigurationError
from .options import MongoDBRAGProviderOptions, MongoDBRAGSearchOptions
from .result import MongoDBRAGResult


class MongoDBRAGProvider:
    """Direct read-only RAG contract shared by later search-mode implementations."""

    def __init__(self, options: MongoDBRAGProviderOptions) -> None:
        self.options = options

    async def search(
        self,
        query: str,
        *,
        options: MongoDBRAGSearchOptions | None = None,
    ) -> list[MongoDBRAGResult]:
        """Search directly; execution is supplied by a mode implementation slice."""
        del options
        if not query.strip():
            raise MongoDBConfigurationError("query must not be empty.")
        raise MongoDBCapabilityError(
            f"{self.options.mode.value} search execution is not installed; "
            "install the corresponding RAG mode implementation."
        )


class MongoDBRAGContextProvider(ContextProvider):
    """Agent Framework adapter contract over a direct MongoDB RAG provider."""

    DEFAULT_SOURCE_ID: ClassVar[str] = "mongodb-rag"

    def __init__(
        self,
        provider: MongoDBRAGProvider,
        *,
        source_id: str = DEFAULT_SOURCE_ID,
    ) -> None:
        if not source_id.strip():
            raise MongoDBConfigurationError("source_id must not be empty.")
        super().__init__(source_id.strip())
        self.provider = provider
