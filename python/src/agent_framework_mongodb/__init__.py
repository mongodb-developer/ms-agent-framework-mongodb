"""MongoDB integrations for Microsoft Agent Framework."""

from .errors import (
    MongoDBAuthorizationError,
    MongoDBCapabilityError,
    MongoDBConfigurationError,
    MongoDBEmbeddingError,
    MongoDBEmbeddingGenerationError,
    MongoDBIndexError,
    MongoDBIndexMismatchError,
    MongoDBIndexMissingError,
    MongoDBIndexNotReadyError,
    MongoDBIntegrationError,
    MongoDBMappingError,
    MongoDBPersistenceError,
    MongoDBRetrievalError,
    MongoDBTimeoutError,
    MongoDBTransientPersistenceError,
    MongoDBTransientRetrievalError,
)
from .history import MongoDBHistoryProvider, MongoDBHistoryProviderOptions
from .memory import MemoryMetadata, MemoryMetadataPage, MongoDBMemoryContextProvider

__all__ = [
    "MongoDBAuthorizationError",
    "MongoDBCapabilityError",
    "MongoDBConfigurationError",
    "MongoDBEmbeddingError",
    "MongoDBEmbeddingGenerationError",
    "MongoDBIndexError",
    "MongoDBIndexMismatchError",
    "MongoDBIndexMissingError",
    "MongoDBIndexNotReadyError",
    "MongoDBIntegrationError",
    "MongoDBMappingError",
    "MongoDBHistoryProvider",
    "MongoDBHistoryProviderOptions",
    "MongoDBMemoryContextProvider",
    "MongoDBPersistenceError",
    "MongoDBRetrievalError",
    "MongoDBTimeoutError",
    "MongoDBTransientPersistenceError",
    "MongoDBTransientRetrievalError",
    "MemoryMetadata",
    "MemoryMetadataPage",
]
