# RoslynKit

RoslynKit is an unofficial Roslyn-powered C# code intelligence CLI for coding agents and terminal workflows.

It is deliberately not an MCP server and not an LSP client. The CLI exposes Git-style subcommands for Roslyn/MSBuild-backed C# inspection. Structured commands return deterministic JSON on stdout, while `version` and top-level `--version` print a plain-text version line.

RoslynKit prioritizes read-only Roslyn intelligence: inspect, navigate, understand, and verify C# code without changing source files or project state.

This guide assumes `roslynkit` is already installed and available on `PATH`. For the side-by-side prerelease dev install, see `docs/dev-install.md`.

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
roslynkit version
roslynkit --version
roslynkit workspace --target .\MySolution.slnx
roslynkit workspace --target .\MySolution.slnx --include-generated --include-additional --include-analyzer-config
roslynkit diagnostics --target .\MySolution.slnx
roslynkit symbols --target .\MySolution.slnx --query MyType
roslynkit symbols --target .\MySolution.slnx --query MyService --exact --kind class
roslynkit document-symbols --target .\MySolution.slnx --file .\src\MyApp\Program.cs
roslynkit document-text --target .\MySolution.slnx --file .\src\MyApp\Service.cs --start-line 31 --end-line 46
roslynkit definition --target .\MySolution.slnx --file .\src\MyApp\Program.cs --line 10 --column 20
roslynkit references --target .\MySolution.slnx --file .\src\MyApp\Program.cs --line 10 --column 20
roslynkit implementations --target .\src\MyApp\MyApp.csproj --file .\src\MyApp\Service.cs --line 23 --column 23
roslynkit quick-info --target .\MySolution.slnx --file .\src\MyApp\Program.cs --line 10 --column 20
roslynkit type-definition --target .\src\MyApp\MyApp.csproj --file .\src\MyApp\Service.cs --line 27 --column 22
roslynkit signature-help --target .\MySolution.slnx --file .\src\MyApp\Program.cs --line 42 --column 17
roslynkit document-text --target .\src\MyApp\MyApp.csproj --document-key doc_ABC123
```

Targets can be `.slnx`, `.sln`, or `.csproj` files. Source positions are one-based. `version` and top-level `--version` do not require `--target`.

`workspace` defaults to repo-relevant source documents only. Add `--include-generated`, `--include-additional`, and `--include-analyzer-config` when you need source-generated files, `AdditionalFiles`, or analyzer config documents. Distinct project and target-framework contexts stay separate through `documentKey`.

For document-oriented commands such as `document-symbols`, `document-text`, `definition`, `references`, `implementations`, `quick-info`, `type-definition`, and `signature-help`, pass `--target` plus exactly one of `--file <path>` or `--document-key <id>`. Use `workspace` first when the same file appears in multiple project contexts or when you need a generated document key. Semantic position commands operate on C# source or source-generated documents; `document-text` can read source, source-generated, additional, and analyzer-config documents.

See `docs/roslyn-lsp-commands.md` for the exhaustive Roslyn language-server method inventory used to compare RoslynKit's current command set with Roslyn's broader code intelligence surface.

## Command Model

RoslynKit follows Git's simple CLI shape:

- subcommands are registered in one builtin command table;
- top-level `--version` is rewritten to the `version` subcommand;
- each subcommand owns its usage strings and option descriptors;
- one shared parser validates short options, long options, flags, required values, and command-specific help;
- command execution stays separate from argument parsing.

Examples:

```powershell
roslynkit help symbols
roslynkit symbols --target=.\MySolution.slnx --query=MyType --max=5
roslynkit symbols --target=.\MySolution.slnx --query=ExecuteAsync --exact --kind=method
roslynkit symbols --help
```

## Additional Docs

- `docs/dev-install.md`: side-by-side prerelease dev install for users who want a separate dev build.
- `docs/dotnet-tool-release.md`: maintainer packaging and release workflow.
- `docs/roslyn-lsp-commands.md`: Roslyn language-server method inventory and coverage comparison.

## Repo-Local Skills

End users working directly in the terminal can ignore this section. This repo also keeps two checked-in Codex skills that wrap the same CLI:

- `.agents\skills\roslynkit\` for the stable global `roslynkit` command.
- `.agents\skills\roslynkit-dev\` for the side-by-side prerelease dev install used in this repo.

`AGENTS.md` makes `.agents\skills\roslynkit-dev\` the default route for ordinary C# semantic inspection in this repo. See `docs/skill-maintenance.md` for the stable/dev skill split and `docs/dev-install.md` for the side-by-side dev install flow.

## Non-Goals

- No MCP transport.
- No LSP transport.
- No background daemon.
- No editor-specific protocol coupling.
- No source mutation by default; any future edit-producing feature should emit proposed changes before applying them.
