# Repository Guidelines

## Project Structure & Module Organization

RoslynKit is a .NET 10 command-line tool. Production code lives under `src/RoslynKit`, with the console entrypoint in `Program.cs`, CLI parsing in `CliParser.cs`, command metadata in `BuiltinCommandRegistry.cs`, and Roslyn execution logic in `RoslynCommandExecutor.cs`. Tests live under `tests/RoslynKit.Tests`. Repository documentation lives under `docs/`; use `docs/local-repository-reference.md` before searching remote repositories for Roslyn, Git, EF Core, or VS Code C# implementation references.

## Build, Test, and Development Commands

- `dotnet restore .\RoslynKit.slnx` restores packages for the solution.
- `dotnet build .\RoslynKit.slnx --tl:off --nologo "-clp:ErrorsOnly;NoSummary"` builds the CLI and tests with concise output.
- `dotnet test .\RoslynKit.slnx --nologo` runs the xUnit test suite.
- `dotnet run --project .\src\RoslynKit -- help` runs the CLI locally.
- `dotnet pack .\src\RoslynKit\RoslynKit.csproj` creates the .NET tool package.

## Coding Style & Naming Conventions

Follow `.editorconfig`: UTF-8, spaces, final newline, 4-space indentation for C# and PowerShell, 2-space indentation for XML/JSON/YAML. C# uses file-scoped namespaces, nullable reference types, implicit usings, latest language version, and warnings as errors. Use `Cli`, not `CLI`, in C# identifiers. Keep command output deterministic and JSON-first.

## Testing Guidelines

Tests use xUnit in `tests/RoslynKit.Tests`. Name test methods as behavior statements, for example `Parse_RejectsDuplicateOption`. Add parser/envelope tests for CLI contract changes and focused Roslyn execution tests when command behavior changes. Run `dotnet test .\RoslynKit.slnx --nologo` before publishing changes.

## Commit & Pull Request Guidelines

History currently starts with `Initial commit`; until a stricter convention exists, use short imperative commit messages such as `Add symbol search command`. Pull requests should describe the CLI behavior change, list validation commands run, and note any JSON contract changes. Link related issues when available.

## Agent-Specific Instructions

This project is CLI-first: do not turn RoslynKit into an MCP server, LSP client, background daemon, or editor-specific integration. Prefer direct Roslyn/MSBuild APIs and stable JSON stdout suitable for coding agents and terminal workflows.
