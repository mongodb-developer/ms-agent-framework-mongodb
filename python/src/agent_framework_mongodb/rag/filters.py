"""Typed, operator-limited public RAG filter expressions."""

from __future__ import annotations

from dataclasses import dataclass, field
from datetime import datetime
from math import isfinite
from typing import ClassVar

from .._shared.field_paths import validate_field_path
from ..errors import MongoDBConfigurationError

FilterScalar = str | int | float | bool | datetime | None
RangeScalar = int | float | datetime


@dataclass(frozen=True, slots=True)
class MongoDBFilter:
    """Base type for application-owned mandatory RAG filters."""

    MAX_DEPTH: ClassVar[int] = 8
    MAX_CHILDREN: ClassVar[int] = 20
    MAX_VALUES: ClassVar[int] = 100

    def __post_init__(self) -> None:
        if type(self) is MongoDBFilter:
            raise MongoDBConfigurationError("MongoDBFilter must be a concrete filter expression.")

    @property
    def depth(self) -> int:
        """Return the expression nesting depth."""
        return 1


def _field(value: object) -> str:
    if not isinstance(value, str):
        raise MongoDBConfigurationError("filter field must be a string.")
    return validate_field_path(value, option_name="filter field")


def _scalar(value: object) -> FilterScalar:
    if not isinstance(value, (str, int, float, bool, datetime)) and value is not None:
        raise MongoDBConfigurationError("filter value must be a BSON scalar, not raw BSON.")
    if isinstance(value, float) and not isfinite(value):
        raise MongoDBConfigurationError("numeric filter value must be finite.")
    if isinstance(value, datetime) and value.tzinfo is None:
        raise MongoDBConfigurationError("datetime filter value must include a timezone.")
    return value


def _range_scalar(value: object) -> RangeScalar:
    if isinstance(value, bool) or not isinstance(value, (int, float, datetime)):
        raise MongoDBConfigurationError("range filter value must be numeric or datetime.")
    result = _scalar(value)
    if result is None or isinstance(result, (str, bool)):
        raise MongoDBConfigurationError("range filter value must be numeric or datetime.")
    return result


@dataclass(frozen=True, slots=True)
class EqualFilter(MongoDBFilter):
    """Match a field equal to one scalar value."""

    field: str
    value: FilterScalar

    def __post_init__(self) -> None:
        object.__setattr__(self, "field", _field(self.field))
        object.__setattr__(self, "value", _scalar(self.value))


@dataclass(frozen=True, slots=True)
class NotEqualFilter(MongoDBFilter):
    """Match a field unequal to one scalar value."""

    field: str
    value: FilterScalar

    def __post_init__(self) -> None:
        object.__setattr__(self, "field", _field(self.field))
        object.__setattr__(self, "value", _scalar(self.value))


@dataclass(frozen=True, slots=True)
class InFilter(MongoDBFilter):
    """Match a field contained in a bounded scalar set."""

    field: str
    values: tuple[FilterScalar, ...]

    def __post_init__(self) -> None:
        object.__setattr__(self, "field", _field(self.field))
        values = tuple(_scalar(value) for value in self.values)
        if not values:
            raise MongoDBConfigurationError("membership filter requires at least one value.")
        if len(values) > self.MAX_VALUES:
            raise MongoDBConfigurationError(
                f"membership filter accepts at most {self.MAX_VALUES} values."
            )
        object.__setattr__(self, "values", values)


@dataclass(frozen=True, slots=True)
class NotInFilter(InFilter):
    """Match a field not contained in a bounded scalar set."""


@dataclass(frozen=True, slots=True)
class _RangeFilter(MongoDBFilter):
    field: str
    value: RangeScalar

    def __post_init__(self) -> None:
        object.__setattr__(self, "field", _field(self.field))
        object.__setattr__(self, "value", _range_scalar(self.value))


@dataclass(frozen=True, slots=True)
class GreaterThanFilter(_RangeFilter):
    """Match values greater than the configured bound."""


@dataclass(frozen=True, slots=True)
class GreaterThanOrEqualFilter(_RangeFilter):
    """Match values greater than or equal to the configured bound."""


@dataclass(frozen=True, slots=True)
class LessThanFilter(_RangeFilter):
    """Match values less than the configured bound."""


@dataclass(frozen=True, slots=True)
class LessThanOrEqualFilter(_RangeFilter):
    """Match values less than or equal to the configured bound."""


@dataclass(frozen=True, slots=True, init=False)
class _BooleanFilter(MongoDBFilter):
    filters: tuple[MongoDBFilter, ...] = field(default_factory=tuple)

    def __init__(self, *filters: MongoDBFilter) -> None:
        values = tuple(filters)
        if len(values) < 2:
            raise MongoDBConfigurationError("boolean filter requires at least two child filters.")
        if len(values) > self.MAX_CHILDREN:
            raise MongoDBConfigurationError(
                f"boolean filter accepts at most {self.MAX_CHILDREN} child filters."
            )
        depth = 1 + max(value.depth for value in values)
        if depth > self.MAX_DEPTH:
            raise MongoDBConfigurationError(
                f"filter nesting depth must not exceed {self.MAX_DEPTH}."
            )
        object.__setattr__(self, "filters", values)

    @property
    def depth(self) -> int:
        """Return the expression nesting depth."""
        return 1 + max(value.depth for value in self.filters)


class AndFilter(_BooleanFilter):
    """Require all child filters."""


class OrFilter(_BooleanFilter):
    """Require at least one child filter."""
