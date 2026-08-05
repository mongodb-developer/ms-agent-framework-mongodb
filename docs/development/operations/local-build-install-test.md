# Local build, installation, and testing

This guide builds and consumes the Python and .NET packages entirely from the
local checkout. None of these commands creates a Git tag, pushes to GitHub, or
publishes to PyPI or NuGet.org.

The package identities are:

- Python distribution: `agent-framework-mongodb`
- Python import: `agent_framework_mongodb`
- NuGet package and .NET namespace: `MongoDB.AgentFramework`

Replace `<repository-root>` in the examples with the absolute path to this
checkout.

## Prerequisites

- Git checkout of this repository
- Internet access to restore third-party dependencies
- Python 3.10 or later
- .NET 10 SDK
- PowerShell 7 (`pwsh`) for the complete .NET package verification scripts
- .NET 8, 9, and 10 runtimes to execute the .NET package smoke test against
  every target framework shipped by the package

The build and credential-free tests do not require MongoDB credentials.
MongoDB-backed samples and integration tests require the deployment,
credentials, collections, and indexes documented in the
[Python samples guide](../../../python/samples/README.md) and
[.NET samples guide](../../../dotnet/samples/README.md).

## Python

Run these commands from the repository's `python` directory.

### Create a development environment

```powershell
cd <repository-root>\python
py -3.10 -m venv .venv
.\.venv\Scripts\Activate.ps1
python -m pip install --upgrade pip
python -m pip install -e ".[dev]"
```

The editable installation is intended for development. Changes under `src`
become available without rebuilding a wheel.

### Run credential-free tests

```powershell
python -m pytest -q
```

Credentialed integration tests skip cleanly when their required configuration
is absent.

### Build local artifacts

```powershell
python -m build
```

This creates one wheel and one source distribution under `python\dist`.

### Install and smoke-test the wheel

Use a separate virtual environment so the test cannot resolve the editable
source checkout:

```powershell
deactivate
py -3.10 -m venv .venv-consumer
.\.venv-consumer\Scripts\Activate.ps1
$wheel = Get-ChildItem .\dist\*.whl | Select-Object -First 1
python -m pip install $wheel.FullName
python -c "import agent_framework_mongodb; print('Python package installed successfully')"
```

### Run the complete non-publishing rehearsal

The rehearsal runs quality checks and tests, builds and validates both package
formats, and installs each artifact in an isolated environment:

```powershell
cd <repository-root>\python
.\.venv\Scripts\Activate.ps1
python scripts\rehearse_release.py
```

Validated packages, checksums, coverage, JUnit, JSON, Markdown, and environment
reports are written under `python\dist\rehearsal`. The rehearsal contains no
upload operation. See
[Python packaging, compatibility, and release evidence](../release/python-packaging.md)
for the checks it performs.

## .NET

Run these commands from the repository's `dotnet` directory unless a command
states otherwise.

### Restore, build, and test

```powershell
cd <repository-root>\dotnet
dotnet restore MongoDB.AgentFramework.slnx
dotnet build MongoDB.AgentFramework.slnx --configuration Release --no-restore
dotnet test MongoDB.AgentFramework.slnx --configuration Release --no-build
```

These tests are credential-free. Tests requiring an external MongoDB
deployment skip when the required configuration is absent.

### Build the local NuGet package

```powershell
dotnet pack .\src\MongoDB.AgentFramework\MongoDB.AgentFramework.csproj `
  --configuration Release `
  --no-restore
```

The package and symbol package are written under
`dotnet\artifacts\packages`. The current package version is defined by the
`Version` property in
`dotnet\src\MongoDB.AgentFramework\MongoDB.AgentFramework.csproj`; use that
value in the consumer command below.

### Install the package in a local application

Create a `NuGet.Config` beside the consuming project. Keep NuGet.org available
for third-party dependencies while forcing `MongoDB.AgentFramework` to resolve
from the local artifact directory:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-mongodb-agent-framework"
         value="<repository-root>\dotnet\artifacts\packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local-mongodb-agent-framework">
      <package pattern="MongoDB.AgentFramework" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
```

Install the current local package, then build and run the application:

```powershell
dotnet add package MongoDB.AgentFramework --version 0.1.0-preview.1
dotnet build
dotnet run
```

If the project version changes, replace `0.1.0-preview.1` with the value in the
package project. Do not add `--source` to the `dotnet add package` command:
`NuGet.Config` intentionally supplies both the local package and NuGet.org
dependency sources.

### Verify the packed package

The repository's package verifier checks metadata, contents, reproducibility,
and installation and execution from an isolated local NuGet feed:

```powershell
pwsh .\scripts\verify-package.ps1 -Configuration Release
```

### Run the complete non-publishing rehearsal

From the repository root:

```powershell
cd <repository-root>
pwsh .\dotnet\scripts\invoke-release-rehearsal.ps1 -Configuration Release
```

Reports and checksums are written under
`dotnet\artifacts\release-rehearsal`. The script performs no tag creation,
Git push, or NuGet publication. See
[.NET release operations](../release/dotnet-release-operations.md) for the
checks it performs.

## Run MongoDB-backed samples

Set `MONGODB_URI`, `MONGODB_DATABASE`, and the feature-specific variables in
the applicable samples guide. Never commit connection strings.

For example, after configuring Chat History:

```powershell
# Python
cd <repository-root>\python
.\.venv-consumer\Scripts\Activate.ps1
python .\samples\history_quickstart.py

# .NET
cd <repository-root>\dotnet
dotnet run --project .\samples\HistoryQuickstart
```

MongoDB Search and Vector Search samples require compatible deployments,
pre-created indexes, and the documented least-privilege identities. Runtime
RAG samples are read-only.
