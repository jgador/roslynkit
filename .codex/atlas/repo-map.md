# Repo Map

## Shape

- Solution: `RoslynKit.slnx`
- Source project: `src/RoslynKit/RoslynKit.csproj`
- Test project: `tests/RoslynKit.Tests/RoslynKit.Tests.csproj`
- Test-side utility: `tests/RoslynKit.WorkspaceGraphDump/RoslynKit.WorkspaceGraphDump.csproj`
- Docs: `docs/`
- Scripts: `scripts/`
- Agent assets: `.agents/skills/`, `.codex/agents/`, `.codex/atlas/`

## Entrypoints

- CLI entrypoint: `src/RoslynKit/Program.cs`
- Top-level runtime and output flow: `src/RoslynKit/CliApplication.cs`
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

## Conventions

- .NET 10 CLI-first repo with deterministic markdown-first command output.
- `roslynkit-dev` is the repo-default semantic inspection route.
- Prefer tests before implementation when available.
- Prefer symbol and line-range reads over full-file reads.
- Atlas carries durable architecture and routing facts only; use RoslynKit live commands for task-sized semantic graph hops.
- `global.json` pins the SDK and testing platform; `Directory.Packages.props` centralizes package versions.

## Runtime Flow

- `Program.Main` creates `CliApplication` and calls `RunAsync`.
- `CliApplication.RunAsync` parses arguments, dispatches help/version, calls `RoslynCommandExecutor.ExecuteAsync`, and renders results through `MarkdownProjection`.
- `CliParser` binds command tokens to `BuiltinCommandRegistry` metadata and validates selector/option combinations.
- `RoslynCommandExecutor` loads workspaces, resolves documents or symbols, invokes Roslyn APIs, and returns result models.
- `MarkdownProjection` is the only command output renderer.

## Live Navigation

- Use `symbols` for filtered declaration discovery.
- Use `document-symbols` for one known C# document outline.
- Use `definition`, `references`, `implementations`, `type-definition`, and `quick-info` for bounded semantic hops from a seed.
- Use `document-lines` or `symbol-source` after the exact file or symbol is known.
- Do not use Atlas markdown as a symbol graph.

## Likely Domains

- command routing and help/version
- parser and option validation
- workspace loading and document selection
- symbol search and document symbols
- navigation commands: definition, references, implementations, quick-info, type-definition, signature-help
- markdown output rendering and result models
- packaging, install, and release flow
- agent and skill routing

## Ignore First

- `artifacts/`
- `TestResults/`
- `.vs/`
- `Visual Studio 18/`
- `bin/`
- `obj/`
- `*.nupkg`
