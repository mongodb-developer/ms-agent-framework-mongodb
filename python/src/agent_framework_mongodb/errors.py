"""Stable public error categories for MongoDB integrations."""


class MongoDBIntegrationError(Exception):
    """Base exception for errors raised by this integration."""


class MongoDBConfigurationError(MongoDBIntegrationError, ValueError):
    """Raised when integration configuration is invalid."""


class MongoDBEmbeddingError(MongoDBIntegrationError):
    """Raised when embedding generation or validation fails."""


class MongoDBEmbeddingGenerationError(MongoDBEmbeddingError):
    """Raised when an embedding generator fails operationally."""


class MongoDBCapabilityError(MongoDBIntegrationError):
    """Raised when a required MongoDB capability is unavailable."""


class MongoDBMappingError(MongoDBIntegrationError):
    """Raised when a MongoDB document cannot be mapped safely."""


class MongoDBAuthorizationError(MongoDBIntegrationError):
    """Raised when MongoDB authentication or authorization fails."""


class MongoDBIndexError(MongoDBIntegrationError):
    """Base exception for Search index failures."""


class MongoDBIndexMissingError(MongoDBIndexError):
    """Raised when a required named index does not exist."""


class MongoDBIndexMismatchError(MongoDBIndexError):
    """Raised when an index definition is incompatible."""


class MongoDBIndexNotReadyError(MongoDBIndexError):
    """Raised when an index exists but is not queryable."""


class MongoDBRetrievalError(MongoDBIntegrationError):
    """Raised when a direct MongoDB read operation fails."""


class MongoDBTransientRetrievalError(MongoDBRetrievalError):
    """Raised when a MongoDB read fails for a documented transient reason."""


class MongoDBPersistenceError(MongoDBIntegrationError):
    """Raised when a direct MongoDB write operation fails."""


class MongoDBTransientPersistenceError(MongoDBPersistenceError):
    """Raised when a MongoDB write fails for a documented transient reason."""


class MongoDBTimeoutError(MongoDBIntegrationError, TimeoutError):
    """Raised when a configured provider operation deadline expires."""
