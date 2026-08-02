# Agent Framework MongoDB for Python

MongoDB integrations for Microsoft Agent Framework.

## Memory quickstart

The Memory provider performs scoped semantic conversation recall. It does not
replace exact Chat History or authoritative RAG.

```python
memory = MongoDBMemoryContextProvider(
    embedding_generator,
    connection_string=os.environ["MONGODB_URI"],
    database_name=os.environ["MONGODB_DATABASE"],
    collection_name=os.environ["MONGODB_MEMORY_COLLECTION"],
    vector_dimensions=1536,
    application_id="my-app",
    user_id="user-123",
)
await memory.ensure_vector_search_index(wait_until_ready=True)
```

Run `samples\memory_quickstart.py` after setting `MONGODB_URI`,
`MONGODB_DATABASE`, and `MONGODB_MEMORY_COLLECTION`. Replace its deterministic
demonstration generator with a production embedding generator whose dimensions
match the configured index. Runtime operations never provision indexes
implicitly. The sample deletes only its scoped fixture messages; collection
cleanup remains an administrator decision.

## Chat History quickstart

Chat History preserves the exact ordered transcript for one authorized session. It
does not perform semantic recall or store complete Agent Framework session state.

```python
history = MongoDBHistoryProvider(
    collection,
    options=MongoDBHistoryProviderOptions(
        application_id="my-app",
        agent_id="my-agent",
        session_id="session-123",
    ),
)
await history.ensure_indexes()
```

Run `samples\history_quickstart.py` after setting `MONGODB_URI`,
`MONGODB_DATABASE`, `MONGODB_HISTORY_COLLECTION`,
`MONGODB_HISTORY_APPLICATION_ID`, `MONGODB_HISTORY_AGENT_ID`, and
`MONGODB_HISTORY_SESSION_ID`. Index creation and session clearing are explicit.
