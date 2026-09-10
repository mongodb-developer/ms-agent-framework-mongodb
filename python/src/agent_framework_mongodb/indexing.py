"""Immutable public results for explicit MongoDB index management."""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum
from typing import TypeAlias


class MongoDBIndexState(str, Enum):
    """Observable lifecycle state of a MongoDB Search index."""

    MISSING = "missing"
    BUILDING = "building"
    READY = "ready"
    READY_NOT_QUERYABLE = "ready_not_queryable"
    FAILED = "failed"
    TIMEOUT = "timeout"


@dataclass(frozen=True, slots=True)
class MongoDBVectorIndexDefinition:
    """Application-owned Vector Search definition."""

    name: str
    path: str
    dimensions: int
    similarity: str
    filter_paths: tuple[str, ...] = ()
    index_type: str = "vectorSearch"

    def document(self) -> dict[str, object]:
        """Return the structured driver definition."""
        return {
            "fields": [
                {
                    "type": "vector",
                    "path": self.path,
                    "numDimensions": self.dimensions,
                    "similarity": self.similarity,
                },
                *[{"type": "filter", "path": path} for path in self.filter_paths],
            ]
        }


@dataclass(frozen=True, slots=True)
class MongoDBSearchIndexDefinition:
    """Application-owned MongoDB Search definition."""

    name: str
    text_paths: tuple[str, ...]
    analyzer: str
    filter_fields: tuple[tuple[str, str], ...] = ()
    search_analyzer: str | None = None
    dynamic: bool = True
    index_type: str = "search"


@dataclass(frozen=True, slots=True)
class MongoDBRegularIndexDefinition:
    """Application-owned regular MongoDB index definition."""

    name: str
    keys: tuple[tuple[str, int], ...]
    expire_after_seconds: int | None = None
    collation: tuple[tuple[str, object], ...] | None = None
    index_type: str = "regular"


MongoDBIndexDefinition: TypeAlias = (
    MongoDBVectorIndexDefinition | MongoDBSearchIndexDefinition | MongoDBRegularIndexDefinition
)


@dataclass(frozen=True, slots=True)
class MongoDBIndexResult:
    """Immutable, redacted result of inspecting an index."""

    definition: MongoDBIndexDefinition
    state: MongoDBIndexState
    status: str | None
    queryable: bool
