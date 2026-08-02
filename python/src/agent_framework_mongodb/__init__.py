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
    "MongoDBMemoryContextProvider",
    "MongoDBPersistenceError",
    "MongoDBRetrievalError",
    "MongoDBTimeoutError",
    "MongoDBTransientPersistenceError",
    "MongoDBTransientRetrievalError",
    "MemoryMetadata",
    "MemoryMetadataPage",
]
