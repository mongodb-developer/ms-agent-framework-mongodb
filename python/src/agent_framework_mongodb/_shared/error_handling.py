"""Inward-only PyMongo exception classification without message inspection."""

from __future__ import annotations

from collections.abc import Mapping
from typing import Literal, cast

from pymongo.errors import (
    ConfigurationError,
    ConnectionFailure,
    ExecutionTimeout,
    InvalidName,
    NetworkTimeout,
    OperationFailure,
    PyMongoError,
    ServerSelectionTimeoutError,
    WTimeoutError,
)

from ..errors import (
    MongoDBAuthorizationError,
    MongoDBCapabilityError,
    MongoDBConfigurationError,
    MongoDBIndexMismatchError,
    MongoDBIndexMissingError,
    MongoDBIndexNotReadyError,
    MongoDBIntegrationError,
    MongoDBPersistenceError,
    MongoDBRetrievalError,
    MongoDBTimeoutError,
    MongoDBTransientPersistenceError,
    MongoDBTransientRetrievalError,
)

OperationKind = Literal["retrieval", "persistence"]

_AUTHORIZATION_CODES = frozenset({13, 18})
_AUTHORIZATION_NAMES = frozenset({"Unauthorized", "AuthenticationFailed"})
_CAPABILITY_CODES = frozenset({59, 303, 40324})
_CAPABILITY_NAMES = frozenset({"CommandNotFound", "Location303", "Location40324"})
_CONFIGURATION_CODES = frozenset({2, 9, 14, 72})
_CONFIGURATION_NAMES = frozenset({"BadValue", "FailedToParse", "InvalidOptions", "TypeMismatch"})
_INDEX_MISSING_NAMES = frozenset({"IndexNotFound", "SearchIndexNotFound"})
_INDEX_MISMATCH_NAMES = frozenset({"IndexOptionsConflict", "IndexKeySpecsConflict"})
_INDEX_NOT_READY_NAMES = frozenset({"SearchIndexNotReady", "IndexBuildAlreadyInProgress"})
_TRANSIENT_CODES = frozenset(
    {
        6,
        7,
        89,
        91,
        189,
        262,
        9001,
        10107,
        11600,
        11601,
        11602,
        13435,
        13436,
    }
)
_TRANSIENT_NAMES = frozenset(
    {
        "HostUnreachable",
        "HostNotFound",
        "NetworkTimeout",
        "ShutdownInProgress",
        "PrimarySteppedDown",
        "NotWritablePrimary",
        "Interrupted",
        "InterruptedAtShutdown",
        "InterruptedDueToReplStateChange",
        "NotPrimaryNoSecondaryOk",
        "NotPrimaryOrSecondary",
    }
)


def translate_pymongo_error(
    error: PyMongoError,
    operation: OperationKind,
    *,
    feature: str,
) -> MongoDBIntegrationError:
    """Translate all PyMongo failures by structured type/code/label only."""
    code, code_name = _structured_identity(error)
    label = _feature_label(feature)
    if isinstance(error, (ConfigurationError, InvalidName)):
        return MongoDBConfigurationError(f"MongoDB rejected the configured {label} operation.")
    if code in _AUTHORIZATION_CODES or code_name in _AUTHORIZATION_NAMES:
        return MongoDBAuthorizationError("MongoDB authentication or authorization failed.")
    if code == 27 or code_name in _INDEX_MISSING_NAMES:
        return MongoDBIndexMissingError(f"The required MongoDB {label} index is missing.")
    if code in {85, 86} or code_name in _INDEX_MISMATCH_NAMES:
        return MongoDBIndexMismatchError(
            f"The configured MongoDB {label} index definition does not match."
        )
    if code_name in _INDEX_NOT_READY_NAMES:
        return MongoDBIndexNotReadyError(f"The required MongoDB {label} index is not ready.")
    if code in _CAPABILITY_CODES or code_name in _CAPABILITY_NAMES:
        return MongoDBCapabilityError(f"The required MongoDB {label} capability is unavailable.")
    if code in _CONFIGURATION_CODES or code_name in _CONFIGURATION_NAMES:
        return MongoDBConfigurationError(f"MongoDB rejected the configured {label} operation.")
    if (
        isinstance(
            error,
            (ExecutionTimeout, NetworkTimeout, ServerSelectionTimeoutError, WTimeoutError),
        )
        or code == 50
    ):
        return MongoDBTimeoutError(f"MongoDB {label} operation timed out.")
    transient = (
        isinstance(error, ConnectionFailure)
        or code in _TRANSIENT_CODES
        or code_name in _TRANSIENT_NAMES
        or error.has_error_label("RetryableReadError")
        or error.has_error_label("RetryableWriteError")
    )
    if operation == "retrieval":
        if transient:
            return MongoDBTransientRetrievalError(f"MongoDB {label} retrieval failed transiently.")
        return MongoDBRetrievalError(f"MongoDB {label} retrieval failed.")
    if transient:
        return MongoDBTransientPersistenceError(f"MongoDB {label} persistence failed transiently.")
    return MongoDBPersistenceError(f"MongoDB {label} persistence failed.")


def _structured_identity(error: PyMongoError) -> tuple[int | None, str | None]:
    if not isinstance(error, OperationFailure):
        return None, None
    details: Mapping[str, object] = (
        cast(Mapping[str, object], error.details)
        if isinstance(error.details, Mapping)
        else cast(Mapping[str, object], {})
    )
    raw_name = details.get("codeName")
    return error.code, raw_name if isinstance(raw_name, str) else None


def _feature_label(feature: str) -> str:
    return {
        "memory": "Memory",
        "history": "History",
        "rag": "RAG",
        "indexing": "Search",
        "session_store": "Session Store",
        "checkpoint_store": "Workflow Checkpoint",
    }[feature]
