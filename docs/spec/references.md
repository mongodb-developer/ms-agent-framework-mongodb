# References

## Primary references

### Microsoft Agent Framework

- Repository: <https://github.com/microsoft/agent-framework>
- Neo4j GraphRAG integration documentation:
  <https://learn.microsoft.com/en-us/agent-framework/integrations/neo4j-graphrag>
- Neo4j Memory integration documentation:
  <https://learn.microsoft.com/en-us/agent-framework/integrations/neo4j-memory>
- .NET `TextSearchProvider` reference implementation in the Agent Framework repository:
  `dotnet/src/Microsoft.Agents.AI/TextSearchProvider.cs`
- Python `HistoryProvider` and `SessionStore` reference implementations:
  `python/packages/core/agent_framework/_sessions.py`
- Python workflow `CheckpointStorage` reference implementation:
  `python/packages/core/agent_framework/_workflows/_checkpoint.py`
- .NET first-party JSON checkpoint-store pattern:
  `dotnet/src/Microsoft.Agents.AI.CosmosNoSql/CosmosCheckpointStore.cs`
- Python Azure AI Search context provider reference implementation:
  `python/packages/azure-ai-search/agent_framework_azure_ai_search/_context_provider.py`

### Neo4j integration model

- Neo4j Agent Framework GraphRAG provider: <https://github.com/neo4j-labs/neo4j-maf-provider>
- Neo4j Agent Memory: <https://github.com/neo4j-labs/agent-memory>

Neo4j is a structural reference for separating Memory from RAG. MongoDB will use one external repository because its
modules share MongoDB-specific infrastructure and are expected to have one ownership and release model.

### MongoDB

- `$vectorSearch` aggregation stage:
  <https://www.mongodb.com/docs/vector-search/query/aggregation-stages/vector-search-stage/>
- Vector Search index definition:
  <https://www.mongodb.com/docs/vector-search/vector-search-type/>
- `$search` aggregation stage:
  <https://www.mongodb.com/docs/search/query/aggregation-stages/search/>
- `$rankFusion` aggregation stage:
  <https://www.mongodb.com/docs/manual/reference/operator/aggregation/rankFusion/>
- PyMongo Search index management:
  <https://www.mongodb.com/docs/languages/python/pymongo-driver/current/indexes/clustered-search-index/>
- MongoDB .NET/C# Driver Search index management:
  <https://www.mongodb.com/docs/drivers/csharp/current/indexes/search-indexes/>
- MongoDB .NET/C# Driver pipeline-stage builders:
  <https://mongodb.github.io/mongo-csharp-driver/3.5.0/api/MongoDB.Driver/MongoDB.Driver.PipelineStageDefinitionBuilder.html>
- MongoDB LangChain integration documentation: <https://www.mongodb.com/docs/atlas/ai-integrations/langchain/>
- MongoDB LangChain package API documentation: <https://langchain-mongodb.readthedocs.io/en/latest/>
- MongoDB LangGraph integration documentation: <https://www.mongodb.com/docs/atlas/ai-integrations/langgraph/>

MongoDB documentation URLs and capability requirements must be revalidated during implementation because Search,
Vector Search, aggregation stages, drivers, and deployment compatibility evolve independently of this specification.
