# RoslynKit

RoslynKit is an unofficial Roslyn-powered C# code intelligence CLI for coding agents and terminal workflows.

It is deliberately not an MCP server and not an LSP client. The CLI loads C# solutions or projects through Roslyn/MSBuild APIs and returns deterministic JSON on stdout.

## Goals

- Use Roslyn APIs directly instead of shelling out to an editor, language server, or IDE.
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
dotnet run --project .\src\RoslynKit -- diagnostics --target .\RoslynKit.slnx
dotnet run --project .\src\RoslynKit -- symbols --target .\RoslynKit.slnx --query CliApplication
dotnet run --project .\src\RoslynKit -- document-symbols --target .\RoslynKit.slnx --file .\src\RoslynKit\CliApplication.cs
dotnet run --project .\src\RoslynKit -- definition --target .\RoslynKit.slnx --file .\src\RoslynKit\Program.cs --line 7 --column 20
dotnet run --project .\src\RoslynKit -- references --target .\RoslynKit.slnx --file .\src\RoslynKit\Program.cs --line 7 --column 20
```

Targets can be `.slnx`, `.sln`, or `.csproj` files. Source positions are one-based.

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
dotnet run --project .\src\RoslynKit -- symbols --help
```

## Tool Packaging

The project is configured as a .NET tool:

```powershell
dotnet pack .\src\RoslynKit\RoslynKit.csproj
dotnet tool install --global --add-source .\src\RoslynKit\bin\Release RoslynKit
roslynkit help
```

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
