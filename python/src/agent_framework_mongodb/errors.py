"""Stable public error categories for MongoDB integrations."""


class MongoDBIntegrationError(Exception):
    """Base exception for errors raised by this integration."""


class MongoDBConfigurationError(MongoDBIntegrationError, ValueError):
    """Raised when integration configuration is invalid."""
