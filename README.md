# RoslynKit

RoslynKit is an unofficial Roslyn-powered C# code intelligence CLI for coding agents and terminal workflows.

It is deliberately not an MCP server and not an LSP client. The CLI loads C# solutions or projects through Roslyn/MSBuild APIs and returns deterministic JSON on stdout.

RoslynKit prioritizes read-only Roslyn intelligence: inspect, navigate, understand, and verify C# code without changing source files or project state.

## Goals

- Use Roslyn APIs directly instead of shelling out to an editor, language server, or IDE.
- Prioritize read-only code intelligence before edit-producing refactor or formatting features.
- Keep commands deterministic and scriptable for agents and terminal workflows.
- Emit one JSON envelope for every command, including usage and error responses.
- Support solution-level and project-level inspection with stable sorting and one-based source positions.

## Commands

Every command writes a JSON envelope:

```json
{
  "schemaVersion": 1,
  "tool": "roslynkit",
  "command": "workspace",
  "success": true,
  "data": {},
  "errors": []
}
```

Available commands:

```powershell
dotnet run --project .\src\RoslynKit -- workspace --target .\RoslynKit.slnx
dotnet run --project .\src\RoslynKit -- workspace --target .\RoslynKit.slnx --include-generated --include-additional --include-analyzer-config
dotnet run --project .\src\RoslynKit -- diagnostics --target .\RoslynKit.slnx
dotnet run --project .\src\RoslynKit -- symbols --target .\RoslynKit.slnx --query CliApplication
dotnet run --project .\src\RoslynKit -- symbols --target .\RoslynKit.slnx --query RoslynCommandExecutor --exact --kind class
dotnet run --project .\src\RoslynKit -- document-symbols --target .\RoslynKit.slnx --file .\src\RoslynKit\CliApplication.cs
dotnet run --project .\src\RoslynKit -- document-text --target .\RoslynKit.slnx --file .\src\RoslynKit\RoslynCommandExecutor.cs --start-line 31 --end-line 46
dotnet run --project .\src\RoslynKit -- definition --target .\RoslynKit.slnx --file .\src\RoslynKit\Program.cs --line 10 --column 20
dotnet run --project .\src\RoslynKit -- references --target .\RoslynKit.slnx --file .\src\RoslynKit\Program.cs --line 10 --column 20
dotnet run --project .\src\RoslynKit -- implementations --target .\tests\FixtureWorkspace\App\App.csproj --file .\tests\FixtureWorkspace\App\Source.cs --line 23 --column 23
dotnet run --project .\src\RoslynKit -- quick-info --target .\RoslynKit.slnx --file .\src\RoslynKit\Program.cs --line 10 --column 20
dotnet run --project .\src\RoslynKit -- type-definition --target .\tests\FixtureWorkspace\App\App.csproj --file .\tests\FixtureWorkspace\App\Source.cs --line 27 --column 22
dotnet run --project .\src\RoslynKit -- signature-help --target .\RoslynKit.slnx --file .\src\RoslynKit\CliParser.cs --line 209 --column 44
dotnet run --project .\src\RoslynKit -- document-text --target .\tests\FixtureWorkspace\App\App.csproj --document-key doc_ABC123
```

Targets can be `.slnx`, `.sln`, or `.csproj` files. Source positions are one-based.

`workspace` defaults to repo-relevant source documents only. Add `--include-generated`, `--include-additional`, and `--include-analyzer-config` when you need source-generated files, `AdditionalFiles`, or analyzer config documents. Distinct project and target-framework contexts stay separate through `documentKey`.

For document-oriented commands such as `document-symbols`, `document-text`, `definition`, `references`, `implementations`, `quick-info`, `type-definition`, and `signature-help`, pass `--target` plus exactly one of `--file <path>` or `--document-key <id>`. Use `workspace` first when the same file appears in multiple project contexts or when you need a generated document key. Semantic position commands operate on C# source or source-generated documents; `document-text` can read source, source-generated, additional, and analyzer-config documents.

See `docs/roslyn-lsp-commands.md` for the exhaustive Roslyn language-server method inventory used to compare RoslynKit's current command set with Roslyn's broader code intelligence surface.

## Repo-Local Skill

RoslynKit includes a repo-local skill at `.agents\skills\roslynkit-csharp\`.

Use the wrapper directly:

```powershell
pwsh .\.agents\skills\roslynkit-csharp\scripts\roslynkit.ps1 -Operation workspace
pwsh .\.agents\skills\roslynkit-csharp\scripts\roslynkit.ps1 -Operation body-read -Path .\src\RoslynKit\RoslynCommandExecutor.cs -StartLine 31 -EndLine 46
pwsh .\.agents\skills\roslynkit-csharp\scripts\roslynkit.ps1 -Operation quick-info -Path .\src\RoslynKit\Program.cs -Line 10 -Column 20
```

The wrapper resolves the nearest `.slnx`, then `.sln`, then `.csproj`, and always passes `--target` explicitly. Use `-Path` for file-backed operations; the wrapper translates that to RoslynKit's `--file` option. It routes ordinary C# semantic inspection to RoslynKit first and leaves literal text search, prose inspection, non-C# files, and workspace-load fallbacks to Codex CLI, which selects the terminal-native tool for the current platform.

## CLI Architecture

RoslynKit follows Git's simple CLI shape:

- subcommands are registered in one builtin command table;
- each subcommand owns its usage strings and option descriptors;
- one shared parser validates short options, long options, flags, required values, and command-specific help;
- command execution stays separate from argument parsing.

Examples:

```powershell
dotnet run --project .\src\RoslynKit -- help symbols
dotnet run --project .\src\RoslynKit -- symbols --target=.\RoslynKit.slnx --query=CliApplication --max=5
dotnet run --project .\src\RoslynKit -- symbols --target=.\RoslynKit.slnx --query=ExecuteAsync --exact --kind=method
dotnet run --project .\src\RoslynKit -- symbols --help
```

## Tool Packaging

RoslynKit ships as a .NET tool package with package ID `roslynkit`.

```powershell
dotnet pack .\src\RoslynKit\RoslynKit.csproj -c Release -o .\artifacts\packages\roslynkit
dotnet tool install --global roslynkit --add-source .\artifacts\packages\roslynkit --version 0.1.0 --ignore-failed-sources
roslynkit help
```

If `roslynkit` is already installed globally:

```powershell
dotnet tool update --global roslynkit --add-source .\artifacts\packages\roslynkit --version 0.1.0 --ignore-failed-sources
```

For a repeatable local folder-feed setup, use the helper script:

```powershell
pwsh .\scripts\prepare-roslynkit-package.ps1
```

That script recreates `.\artifacts\packages\roslynkit`, packs `roslynkit.0.1.0.nupkg` into the folder feed, and prints the exact `dotnet tool install` and `dotnet tool update` commands for dogfooding the current checkout. See `docs/dotnet-tool-release.md` for the maintainer packaging and publish workflow.

## Development

This repo uses .NET 10 and pins SDK `10.0.301` in `global.json`.

```powershell
dotnet build .\RoslynKit.slnx
dotnet test .\RoslynKit.slnx
```

## Non-Goals

- No MCP transport.
- No LSP transport.
- No background daemon.
- No editor-specific protocol coupling.
- No source mutation by default; any future edit-producing feature should emit proposed changes before applying them.
