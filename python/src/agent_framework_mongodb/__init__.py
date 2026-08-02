"""MongoDB integrations for Microsoft Agent Framework."""

from .errors import (
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
)
from .memory import MemoryMetadata, MemoryMetadataPage, MongoDBMemoryContextProvider

__all__ = [
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
    "MemoryMetadata",
    "MemoryMetadataPage",
]
