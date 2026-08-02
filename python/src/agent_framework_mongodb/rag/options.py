"""Public RAG option contracts."""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum
from math import isfinite
from re import fullmatch
from typing import cast

from .._shared.embeddings import validate_dimensions
from .._shared.field_paths import validate_field_path
from ..errors import MongoDBConfigurationError
from .filters import AndFilter, MongoDBFilter


class MongoDBSearchMode(str, Enum):
    """Supported MongoDB retrieval modes."""

    VECTOR_ANN = "vector_ann"
    VECTOR_ENN = "vector_enn"
    FULL_TEXT = "full_text"
    HYBRID_RRF = "hybrid_rrf"


def _mode(value: object) -> MongoDBSearchMode:
    try:
        return value if isinstance(value, MongoDBSearchMode) else MongoDBSearchMode(value)
    except (TypeError, ValueError) as exc:
        raise MongoDBConfigurationError(
            "mode must be vector_ann, vector_enn, full_text, or hybrid_rrf."
        ) from exc


def _bounded_int(value: object, name: str, *, maximum: int) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 1 or value > maximum:
        raise MongoDBConfigurationError(f"{name} must be an integer from 1 through {maximum}.")
    return value


def _name(value: object, name: str, *, required: bool) -> str | None:
    if value is None:
        if required:
            raise MongoDBConfigurationError(f"{name} is required for the selected search mode.")
        return None
    if not isinstance(value, str) or not fullmatch(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,127}", value):
        raise MongoDBConfigurationError(
            f"{name} must be 1-128 letters, digits, dots, underscores, or hyphens."
        )
    return value


def _paths(
    values: object,
    name: str,
    *,
    allow_empty: bool,
) -> tuple[str, ...]:
    if not isinstance(values, (list, tuple)):
        raise MongoDBConfigurationError(f"{name} must be an explicit list or tuple.")
    if not values and not allow_empty:
        raise MongoDBConfigurationError(f"{name} must contain at least one field path.")
    result: list[str] = []
    seen: set[str] = set()
    for value in cast(list[object] | tuple[object, ...], values):
        if not isinstance(value, str):
            raise MongoDBConfigurationError(f"{name} values must be field path strings.")
        path = validate_field_path(value, option_name=name)
        if path not in seen:
            seen.add(path)
            result.append(path)
    return tuple(result)


def _weight(value: object, name: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise MongoDBConfigurationError(f"{name} must be a finite non-negative number.")
    result = float(value)
    if not isfinite(result) or result < 0:
        raise MongoDBConfigurationError(f"{name} must be a finite non-negative number.")
    return result


def _filter(value: object) -> MongoDBFilter | None:
    if value is not None and not isinstance(value, MongoDBFilter):
        raise TypeError(
            "filter must be a typed MongoDBFilter; raw dictionaries/BSON are forbidden."
        )
    return value


def _boolean(value: object, name: str) -> bool:
    if not isinstance(value, bool):
        raise MongoDBConfigurationError(f"{name} must be a boolean.")
    return value


@dataclass(frozen=True, slots=True)
class MongoDBRAGParentOptions:
    """Bounded same-database parent-document hydration contract."""

    collection_name: str | None = None
    parent_id_field: str = "parent_id"
    parent_document_id_field: str = "_id"
    parent_text_field: str = "content"
    max_parents: int = 10
    max_parent_text_length: int = 50_000
    max_lookup_fan_out: int = 20
    max_context_tokens: int = 8_000

    def __post_init__(self) -> None:
        if self.collection_name is not None:
            if "." in self.collection_name:
                raise MongoDBConfigurationError(
                    "parent collection_name must be a same-database collection name."
                )
            object.__setattr__(
                self,
                "collection_name",
                _name(self.collection_name, "parent collection_name", required=True),
            )
        for name in ("parent_id_field", "parent_document_id_field", "parent_text_field"):
            object.__setattr__(
                self,
                name,
                validate_field_path(getattr(self, name), option_name=name),
            )
        object.__setattr__(
            self,
            "max_parents",
            _bounded_int(self.max_parents, "max_parents", maximum=100),
        )
        object.__setattr__(
            self,
            "max_parent_text_length",
            _bounded_int(
                self.max_parent_text_length,
                "max_parent_text_length",
                maximum=1_000_000,
            ),
        )
        object.__setattr__(
            self,
            "max_lookup_fan_out",
            _bounded_int(self.max_lookup_fan_out, "max_lookup_fan_out", maximum=100),
        )
        object.__setattr__(
            self,
            "max_context_tokens",
            _bounded_int(self.max_context_tokens, "max_context_tokens", maximum=100_000),
        )


@dataclass(frozen=True, slots=True)
class MongoDBRAGSearchOptions:
    """Per-call bounds and typed relevance filter for direct search."""

    top_k: int | None = None
    num_candidates: int | None = None
    filter: MongoDBFilter | None = None
    include_score_details: bool | None = None

    def __post_init__(self) -> None:
        if self.top_k is not None:
            _bounded_int(self.top_k, "top_k", maximum=100)
        if self.num_candidates is not None:
            _bounded_int(self.num_candidates, "num_candidates", maximum=10_000)
        object.__setattr__(self, "filter", _filter(self.filter))
        if self.include_score_details is not None:
            _boolean(self.include_score_details, "include_score_details")


@dataclass(frozen=True, slots=True)
class MongoDBRAGProviderOptions:
    """Immutable provider-owned RAG mapping, security, and mode options."""

    mode: MongoDBSearchMode = MongoDBSearchMode.VECTOR_ANN
    vector_dimensions: int | None = None
    vector_index_name: str | None = None
    search_index_name: str | None = None
    id_field: str = "_id"
    text_fields: tuple[str, ...] | list[str] = ("content",)
    vector_field: str = "embedding"
    source_name_field: str | None = "source.name"
    source_url_field: str | None = "source.url"
    metadata_fields: tuple[str, ...] | list[str] = ()
    top_k: int = 5
    num_candidates: int | None = None
    filter: MongoDBFilter | None = None
    vector_weight: float = 1.0
    text_weight: float = 1.0
    include_score_details: bool = False
    parent: MongoDBRAGParentOptions | None = None

    def __post_init__(self) -> None:
        mode = _mode(self.mode)
        object.__setattr__(self, "mode", mode)
        object.__setattr__(self, "top_k", _bounded_int(self.top_k, "top_k", maximum=100))
        object.__setattr__(
            self,
            "text_fields",
            _paths(self.text_fields, "text_fields", allow_empty=False),
        )
        object.__setattr__(
            self,
            "metadata_fields",
            _paths(self.metadata_fields, "metadata_fields", allow_empty=True),
        )
        object.__setattr__(
            self, "id_field", validate_field_path(self.id_field, option_name="id_field")
        )
        object.__setattr__(
            self,
            "vector_field",
            validate_field_path(self.vector_field, option_name="vector_field"),
        )
        for name in ("source_name_field", "source_url_field"):
            value = getattr(self, name)
            if value is not None:
                object.__setattr__(self, name, validate_field_path(value, option_name=name))
        object.__setattr__(self, "filter", _filter(self.filter))
        _boolean(self.include_score_details, "include_score_details")

        vector_mode = mode in (
            MongoDBSearchMode.VECTOR_ANN,
            MongoDBSearchMode.VECTOR_ENN,
            MongoDBSearchMode.HYBRID_RRF,
        )
        search_mode = mode in (MongoDBSearchMode.FULL_TEXT, MongoDBSearchMode.HYBRID_RRF)
        object.__setattr__(
            self,
            "vector_index_name",
            _name(self.vector_index_name, "vector_index_name", required=vector_mode),
        )
        object.__setattr__(
            self,
            "search_index_name",
            _name(self.search_index_name, "search_index_name", required=search_mode),
        )
        if not vector_mode and self.vector_dimensions is not None:
            raise MongoDBConfigurationError("vector_dimensions is forbidden in full_text mode.")
        if vector_mode:
            if self.vector_dimensions is None:
                raise MongoDBConfigurationError(
                    "vector_dimensions is required for vector and hybrid modes."
                )
            object.__setattr__(
                self, "vector_dimensions", validate_dimensions(self.vector_dimensions)
            )

        if mode in (MongoDBSearchMode.VECTOR_ANN, MongoDBSearchMode.HYBRID_RRF):
            candidates = 50 if self.num_candidates is None else self.num_candidates
            candidates = _bounded_int(candidates, "num_candidates", maximum=10_000)
            if candidates < self.top_k:
                raise MongoDBConfigurationError("num_candidates must be at least top_k.")
            object.__setattr__(self, "num_candidates", candidates)
        elif self.num_candidates is not None:
            raise MongoDBConfigurationError(
                "num_candidates is forbidden in vector_enn and full_text modes."
            )

        if mode in (MongoDBSearchMode.VECTOR_ANN, MongoDBSearchMode.VECTOR_ENN):
            if self.search_index_name is not None:
                raise MongoDBConfigurationError(
                    "search_index_name is forbidden in vector-only modes."
                )
        if mode is MongoDBSearchMode.FULL_TEXT and self.vector_index_name is not None:
            raise MongoDBConfigurationError("vector_index_name is forbidden in full_text mode.")
        vector_weight = _weight(self.vector_weight, "vector_weight")
        text_weight = _weight(self.text_weight, "text_weight")
        object.__setattr__(self, "vector_weight", vector_weight)
        object.__setattr__(self, "text_weight", text_weight)
        if mode is MongoDBSearchMode.HYBRID_RRF and vector_weight == text_weight == 0:
            raise MongoDBConfigurationError("at least one hybrid fusion weight must be positive.")
        if self.parent is not None and mode not in (
            MongoDBSearchMode.VECTOR_ANN,
            MongoDBSearchMode.VECTOR_ENN,
            MongoDBSearchMode.HYBRID_RRF,
        ):
            raise MongoDBConfigurationError("parent retrieval requires a vector-capable mode.")

    def normalize_search_options(
        self,
        options: MongoDBRAGSearchOptions | None = None,
    ) -> MongoDBRAGSearchOptions:
        """Resolve per-call values while retaining the mandatory provider filter."""
        options = options or MongoDBRAGSearchOptions()
        top_k = self.top_k if options.top_k is None else options.top_k
        candidates = (
            self.num_candidates if options.num_candidates is None else options.num_candidates
        )
        if self.mode in (MongoDBSearchMode.VECTOR_ENN, MongoDBSearchMode.FULL_TEXT):
            if options.num_candidates is not None:
                raise MongoDBConfigurationError(
                    "num_candidates is forbidden in vector_enn and full_text modes."
                )
            candidates = None
        elif candidates is None or candidates < top_k:
            raise MongoDBConfigurationError("num_candidates must be at least top_k.")
        effective_filter = self.filter
        if options.filter is not None:
            effective_filter = (
                options.filter
                if effective_filter is None
                else AndFilter(effective_filter, options.filter)
            )
        include_details = (
            self.include_score_details
            if options.include_score_details is None
            else options.include_score_details
        )
        return MongoDBRAGSearchOptions(
            top_k=top_k,
            num_candidates=candidates,
            filter=effective_filter,
            include_score_details=include_details,
        )
