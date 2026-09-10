"""Normalized RAG result and citation mapping."""

from __future__ import annotations

from collections.abc import Mapping
from dataclasses import dataclass
from math import isfinite
from types import MappingProxyType
from typing import cast

from agent_framework import Annotation

from ..errors import MongoDBConfigurationError


def _text(value: object, name: str, *, optional: bool = False) -> str | None:
    if value is None and optional:
        return None
    if not isinstance(value, str) or not value.strip():
        raise MongoDBConfigurationError(f"result {name} must not be empty.")
    return value


def _score(value: object) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not isfinite(float(value)):
        raise MongoDBConfigurationError("result score must be a finite number.")
    return float(value)


def _mapping(value: object, name: str) -> Mapping[str, object]:
    if not isinstance(value, Mapping):
        raise MongoDBConfigurationError(f"result {name} must be a mapping.")
    return cast(Mapping[str, object], value)


@dataclass(frozen=True, slots=True)
class MongoDBRAGResult:
    """A normalized result that retains the original MongoDB document."""

    id: object
    text: str
    score: float
    metadata: Mapping[str, object]
    raw_document: Mapping[str, object]
    source_name: str | None = None
    source_url: str | None = None

    def __post_init__(self) -> None:
        if self.id is None or (isinstance(self.id, str) and not self.id.strip()):
            raise MongoDBConfigurationError("result id must not be empty.")
        object.__setattr__(self, "text", _text(self.text, "text"))
        object.__setattr__(self, "score", _score(self.score))
        metadata = _mapping(self.metadata, "metadata")
        object.__setattr__(self, "metadata", MappingProxyType(dict(metadata)))
        object.__setattr__(self, "raw_document", _mapping(self.raw_document, "raw_document"))
        object.__setattr__(
            self,
            "source_name",
            _text(self.source_name, "source_name", optional=True),
        )
        object.__setattr__(
            self,
            "source_url",
            _text(self.source_url, "source_url", optional=True),
        )

    def to_citation(self) -> Annotation:
        """Map source attribution to the public Agent Framework citation shape."""
        citation: Annotation = {
            "type": "citation",
            "snippet": self.text,
            "additional_properties": {
                "document_id": self.id,
                "score": self.score,
                "metadata": dict(self.metadata),
            },
            "raw_representation": self,
        }
        if self.source_name:
            citation["title"] = self.source_name
        if self.source_url:
            citation["url"] = self.source_url
        return citation
