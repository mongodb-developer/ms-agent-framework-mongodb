# ms-agent-framework-mongodb

MongoDB providers for Microsoft Agent Framework in Python and .NET.

Choose **Memory** for scoped semantic conversation recall, **Chat History** for an
exact ordered transcript, **RAG** for read-only authoritative knowledge retrieval,
**Session Store** for complete agent sessions, and **Workflow Checkpoint Store** for
resumable workflow state and lineage. Applications may combine these deliberately;
none substitutes for another.

## Python package

The canonical distribution is `agent-framework-mongodb` and the import root is
`agent_framework_mongodb`:

```powershell
python -m pip install agent-framework-mongodb
```

No package has been published from this repository yet. Until publishing
ownership is confirmed, build and install the reviewed artifact from
[`python`](python/README.md); do not depend on an unverified registry project
with the same name.

| Capability | Choose it for | Python sample |
| --- | --- | --- |
| Memory | scoped semantic recall from prior conversation | [Memory quickstart](python/samples/memory_quickstart.py) |
| Chat History | exact ordered replay of supported messages | [History quickstart](python/samples/history_quickstart.py) |
| RAG | read-only retrieval from pre-ingested knowledge | [Vector](python/samples/rag_vector_quickstart.py), [full text](python/samples/rag_full_text_quickstart.py), [hybrid](python/samples/rag_hybrid_quickstart.py) |
| Session Store | complete Agent Framework session snapshots | [Session persistence](python/samples/session_persistence.py) |
| Workflow Checkpoint Store | resumable workflow state and lineage | [Checkpoint resume](python/samples/workflow_checkpoint_resume.py) |

Implementation-owned Python scenarios are also available for
[parent-document RAG](python/samples/rag_parent_document.py),
[on-demand retrieval](python/samples/on_demand_retrieval_tool.py),
[workflow retrieval](python/samples/workflow_retrieval.py),
[Memory with RAG](python/samples/memory_and_rag.py),
[structured metadata](python/samples/structured_metadata_retrieval.py), and the
[bounded document loader](python/samples/document_loader.py). They use local
model-free fixtures where a model client would otherwise require an
owner-selected provider.

## .NET package

The canonical NuGet package and namespace are both `MongoDB.AgentFramework`:

```powershell
dotnet add package MongoDB.AgentFramework --prerelease
```

No package has been published from this repository yet. Until publishing
ownership is confirmed, build and reference the reviewed artifact described in
the [`.NET` package guide](dotnet/README.md); do not depend on an unverified
registry project with the same name.

| Capability | Choose it for | .NET sample |
| --- | --- | --- |
| Memory | scoped semantic recall from prior conversation | [Memory quickstart](dotnet/samples/MemoryQuickstart/Program.cs) |
| Chat History | exact ordered replay of supported messages | [History quickstart](dotnet/samples/HistoryQuickstart/Program.cs) |
| RAG | read-only retrieval from pre-ingested knowledge | [RAG quickstart](dotnet/samples/RAGQuickstart/Program.cs) |
| Session Store | complete Agent Framework session snapshots | [Session persistence](dotnet/samples/SessionPersistenceQuickstart/Program.cs) |
| Workflow Checkpoint Store | resumable workflow state and lineage | [Checkpoint resume](dotnet/samples/WorkflowCheckpointResumeQuickstart/Program.cs) |

Implementation-owned .NET scenarios are also available for
[parent-document RAG](dotnet/samples/ParentDocumentRAGQuickstart/Program.cs),
[on-demand retrieval](dotnet/samples/OnDemandRetrievalTool/Program.cs),
[workflow retrieval](dotnet/samples/WorkflowRetrieval/Program.cs),
[Memory with RAG](dotnet/samples/MemoryAndRAG/Program.cs),
[structured metadata](dotnet/samples/StructuredMetadataRetrieval/Program.cs), and the
[bounded document loader](dotnet/samples/MongoDBDocumentLoader/Program.cs).

## Configuration and safety

Samples use `MONGODB_URI`, `MONGODB_DATABASE`, and feature-specific collection,
scope, and index variables documented in the
[Python samples guide](python/samples/README.md) and
[.NET samples guide](dotnet/samples/README.md). They validate setup before
network access and contain no credentials. Use separate least-privilege runtime,
index-provisioning, and sample-ingestion identities. Runtime RAG is read-only;
it does not ingest documents or accept model-generated BSON, filters, field
names, index names, or pipelines.

MongoDB Search, Vector Search, and native hybrid RRF require a compatible
deployment and pre-created indexes. Credentialed compatibility evidence is not
available in this repository yet, so publication remains blocked. See the
[Python compatibility evidence](docs/development/release/python-packaging.md),
[.NET packaging evidence](docs/development/release/dotnet-packaging-release.md),
[Python release checklist](docs/release/python-release-checklist.md), and
[.NET release operations guide](docs/development/release/dotnet-release-operations.md).
Maintainers use the
[Python release runbook](docs/release/python-release.md) and
[.NET release operations guide](docs/development/release/dotnet-release-operations.md);
the documented local rehearsals never publish.

## Development

This repository is maintained under [`mongo/ms-agent-framework-mongodb`](https://github.com/mongo/ms-agent-framework-mongodb). See [implementation specifications](docs/spec/README.md), the [implementation map](docs/spec/implementation-map.md), [architectural decisions](docs/decisions/README.md), and [contribution requirements](CONTRIBUTING.md).

To build, install, and test both packages without publishing, follow the
[local build, installation, and testing guide](docs/development/operations/local-build-install-test.md).

Implemented provider guides:

- [Python Chat History](docs/development/history/python-history.md)
- [.NET Chat History](docs/development/history/dotnet-history.md)
- [.NET Session Store](docs/development/persistence/dotnet-session-store.md)

Python and .NET quickstarts and the explicitly sample-only, write-capable
ingestion demonstrations are documented in the
[`python` package guide](python/README.md) and
[`.NET` package guide](dotnet/README.md). Runtime RAG retrieval remains
read-only and must use credentials separate from ingestion and index
provisioning.

## Warranty

The Software is provided as Open Source. This software is provided “as is” and any express or implied warranties, including, but not limited to, the implied warranties of merchantability and fitness for a particular purpose are disclaimed. In no event shall the owner or contributors be liable for any direct, indirect, incidental, special, exemplary, or consequential damages (including, but not limited to, procurement of substitute goods or services; loss of use, data, or profits; or business interruption) however caused and on any theory of liability, whether in contract, strict liability, or tort (including negligence or otherwise) arising in any way out of the use of this software, even if advised of the possibility of such damage.

## Legal

Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions and limitations under the License.

## Trademarks

This project may contain trademarks or logos for projects, products, or services.  Any use of third-party trademarks or logos are subject to those third-party's policies.
