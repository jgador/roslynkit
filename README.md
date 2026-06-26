# RoslynKit

RoslynKit is an unofficial Roslyn-powered C# code intelligence CLI for coding agents and terminal workflows.

It is deliberately not an MCP server and not an LSP client. The CLI exposes Git-style subcommands for Roslyn/MSBuild-backed C# inspection. Structured commands return deterministic JSON on stdout, while `version` and top-level `--version` print a plain-text version line.

RoslynKit prioritizes read-only Roslyn intelligence: inspect, navigate, understand, and verify C# code without changing source files or project state.

## Goals

- Use Roslyn APIs directly instead of shelling out to an editor, language server, or IDE.
- Prioritize read-only code intelligence before edit-producing refactor or formatting features.
- Keep commands deterministic and scriptable for agents and terminal workflows.
- Emit one JSON envelope for every structured command result, including usage and error responses. The Git-style `version` command prints plain text.
- Support solution-level and project-level inspection with stable sorting and one-based source positions.

## Commands

Most commands write a JSON envelope:

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

`version` and top-level `--version` print a plain-text version line instead:

```text
roslynkit version <informational-version>
```

The printed value comes from the assembly informational version and may include build metadata after `+`.

Available commands:

```powershell
dotnet run --project .\src\RoslynKit -- version
dotnet run --project .\src\RoslynKit -- --version
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

Targets can be `.slnx`, `.sln`, or `.csproj` files. Source positions are one-based. `version` and top-level `--version` do not require `--target`.

`workspace` defaults to repo-relevant source documents only. Add `--include-generated`, `--include-additional`, and `--include-analyzer-config` when you need source-generated files, `AdditionalFiles`, or analyzer config documents. Distinct project and target-framework contexts stay separate through `documentKey`.

For document-oriented commands such as `document-symbols`, `document-text`, `definition`, `references`, `implementations`, `quick-info`, `type-definition`, and `signature-help`, pass `--target` plus exactly one of `--file <path>` or `--document-key <id>`. Use `workspace` first when the same file appears in multiple project contexts or when you need a generated document key. Semantic position commands operate on C# source or source-generated documents; `document-text` can read source, source-generated, additional, and analyzer-config documents.

See `docs/roslyn-lsp-commands.md` for the exhaustive Roslyn language-server method inventory used to compare RoslynKit's current command set with Roslyn's broader code intelligence surface.

## Repo-Local Skills

RoslynKit keeps two checked-in agent skills:

- `.agents\skills\roslynkit\` for the stable global `roslynkit` command.
- `.agents\skills\roslynkit-dev\` for the default RoslynKit development route in this repo.

Stable skill example:

```powershell
roslynkit workspace --target .\RoslynKit.slnx
roslynkit document-text --target .\RoslynKit.slnx --file .\src\RoslynKit\RoslynCommandExecutor.cs --start-line 31 --end-line 46
roslynkit quick-info --target .\RoslynKit.slnx --file .\src\RoslynKit\Program.cs --line 10 --column 20
```

Dev skill example:

```powershell
$roslynkitDev = Join-Path (Join-Path (Join-Path $HOME ".roslynkit") "tools") "roslynkit-dev"
$roslynkitDev = Join-Path $roslynkitDev ($(if ($IsWindows) { "roslynkit.exe" } else { "roslynkit" }))
& $roslynkitDev workspace --target .\RoslynKit.slnx
& $roslynkitDev document-text --target .\RoslynKit.slnx --file .\src\RoslynKit\RoslynCommandExecutor.cs --start-line 31 --end-line 46
& $roslynkitDev quick-info --target .\RoslynKit.slnx --file .\src\RoslynKit\Program.cs --line 10 --column 20
```

`AGENTS.md` makes `.agents\skills\roslynkit-dev\` the default route for ordinary C# semantic inspection in this repo. Pass `--target` explicitly for RoslynKit's code-intelligence commands. `version` and top-level `--version` do not use a target. Use `workspace` first when you need a generated `documentKey`, multiple project contexts, or additional/analyzer-config documents. Use RoslynKit first for ordinary C# semantic inspection, then fall back to terminal-native literal search for prose, non-C# files, or RoslynKit workspace-load failures. See `docs/skill-maintenance.md` for the stable/dev update rules and `docs/dev-install.md` for the side-by-side dev install flow.

## CLI Architecture

RoslynKit follows Git's simple CLI shape:

- subcommands are registered in one builtin command table;
- top-level `--version` is rewritten to the `version` subcommand;
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

Stable global install:

```powershell
dotnet pack .\src\RoslynKit\RoslynKit.csproj -c Release -o .\artifacts\packages\roslynkit
dotnet tool install --global roslynkit --add-source .\artifacts\packages\roslynkit --version 0.1.0 --ignore-failed-sources
roslynkit version
```

If `roslynkit` is already installed globally:

```powershell
dotnet tool update --global roslynkit --add-source .\artifacts\packages\roslynkit --version 0.1.0 --ignore-failed-sources
```

Side-by-side prerelease install:

```powershell
pwsh .\scripts\install-roslynkit-dev.ps1 -Version 0.1.1-dev.1
```

That installer builds the current checkout, packs `src\RoslynKit\RoslynKit.csproj` with `/p:Version=<prerelease>` into `.\artifacts\packages\roslynkit-dev` by default, and installs or updates the side-by-side dev tool without changing `Directory.Build.props`.

For a repeatable local folder-feed setup, use the helper script:

```powershell
pwsh .\scripts\prepare-roslynkit-package.ps1
```

That script recreates `.\artifacts\packages\roslynkit`, packs the current `roslynkit.<version>.nupkg` into the stable folder feed, and prints the exact global install commands plus the self-packaging side-by-side dev install command. See `docs/dev-install.md` for the dev install flow, `docs/dotnet-tool-release.md` for the maintainer packaging workflow, and `docs/skill-maintenance.md` for the stable/dev skill split.

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
