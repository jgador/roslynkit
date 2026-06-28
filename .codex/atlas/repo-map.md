# Repo Map

## Shape

- Solution: `RoslynKit.slnx`
- Source project: `src/RoslynKit/RoslynKit.csproj`
- Test project: `tests/RoslynKit.Tests/RoslynKit.Tests.csproj`
- Test-side utilities: `tests/RoslynKit.WorkspaceGraphDump/RoslynKit.WorkspaceGraphDump.csproj` and `tests/RoslynKit.AtlasPromptCacheProbe/RoslynKit.AtlasPromptCacheProbe.csproj`
- Docs: `docs/`
- Scripts: `scripts/`
- Agent assets: `.agents/skills/`, `.codex/agents/`, `.codex/atlas/`

## Entrypoints

- CLI entrypoint: `src/RoslynKit/Program.cs`
- Top-level runtime and envelope flow: `src/RoslynKit/CliApplication.cs`
- CLI parsing: `src/RoslynKit/CliParser.cs`
- Command registry: `src/RoslynKit/BuiltinCommandRegistry.cs`
- Command execution: `src/RoslynKit/RoslynCommandExecutor.cs`
- Workspace and document resolution: `src/RoslynKit/RoslynWorkspaceLoader.cs`

## Build And Test

- Restore: `dotnet restore .\RoslynKit.slnx`
- Build: `dotnet build .\RoslynKit.slnx --tl:off --nologo "-clp:ErrorsOnly;NoSummary"`
- Test: `dotnet test .\RoslynKit.slnx`
- Run locally: `dotnet run --project .\src\RoslynKit -- help`
- Pack: `dotnet pack .\src\RoslynKit\RoslynKit.csproj`
- Atlas prompt-cache probe: `dotnet run --project .\tests\RoslynKit.AtlasPromptCacheProbe`

## Conventions

- .NET 10 CLI-first repo with deterministic JSON-first command output.
- `roslynkit-dev` is the repo-default semantic inspection route.
- Prefer tests before implementation when available.
- Prefer symbol and line-range reads over full-file reads.
- `global.json` pins the SDK and testing platform; `Directory.Packages.props` centralizes package versions.

## Likely Domains

- command routing and help/version
- parser and option validation
- workspace loading and document selection
- symbol search and document symbols
- navigation commands: definition, references, implementations, quick-info, type-definition, signature-help
- JSON envelopes and result models
- packaging, install, and release flow
- agent and skill routing
- Atlas prompt caching and repeated route probes

## Ignore First

- `artifacts/`
- `TestResults/`
- `.vs/`
- `Visual Studio 18/`
- `bin/`
- `obj/`
- `.synapse/graph.json`
- `.synapse/memories.md`
- `.synapse/synapse-memory.json`
- `*.nupkg`
