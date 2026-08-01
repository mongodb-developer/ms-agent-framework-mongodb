"""Stable public error categories for MongoDB integrations."""


class MongoDBIntegrationError(Exception):
    """Base exception for errors raised by this integration."""


class MongoDBConfigurationError(MongoDBIntegrationError, ValueError):
    """Raised when integration configuration is invalid."""


class MongoDBEmbeddingError(MongoDBIntegrationError):
    """Raised when embedding generation or validation fails."""


class MongoDBCapabilityError(MongoDBIntegrationError):
    """Raised when a required MongoDB capability is unavailable."""


class MongoDBMappingError(MongoDBIntegrationError):
    """Raised when a MongoDB document cannot be mapped safely."""
