# Configuration

## Environment variables

Samples and integration tests should use consistent environment variables:

| Variable | Purpose |
| --- | --- |
| `MONGODB_URI` | MongoDB connection string |
| `MONGODB_DATABASE` | Database containing integration collections |
| `MONGODB_MEMORY_COLLECTION` | Memory collection |
| `MONGODB_RAG_COLLECTION` | Knowledge/RAG collection |
| `MONGODB_MEMORY_VECTOR_INDEX` | Memory Vector Search index |
| `MONGODB_RAG_VECTOR_INDEX` | RAG Vector Search index |
| `MONGODB_RAG_SEARCH_INDEX` | RAG Search index |
| `MONGODB_TEST_DATABASE` | Optional isolated integration-test database |

Chat and embedding model configuration belongs to the chosen model provider and must not be embedded into MongoDB
provider settings.

Connection strings and credentials must never be committed. Samples must fail with clear setup guidance when required
variables are absent.
