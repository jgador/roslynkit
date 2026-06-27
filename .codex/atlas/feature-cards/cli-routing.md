# CLI Routing

## Purpose

Route command text from `Program.Main` through parse, help/version, and command execution.

## Task keywords

- command execution
- command routing
- help and version
- usage and envelope
- parser validation

## Entrypoints

- `src/RoslynKit/Program.cs`
- `src/RoslynKit/CliApplication.cs`
- `src/RoslynKit/CliParser.cs`
- `src/RoslynKit/BuiltinCommandRegistry.cs`
- `src/RoslynKit/RoslynCommandExecutor.cs`

## Important symbols

- `Program.Main`
- `CliApplication.RunAsync`
- `CliParser.Parse`
- `BuiltinCommandRegistry.Commands`
- `RoslynCommandExecutor.ExecuteAsync`

## Important files

- `src/RoslynKit/Program.cs`
- `src/RoslynKit/CliApplication.cs`
- `src/RoslynKit/CliParser.cs`
- `src/RoslynKit/BuiltinCommandRegistry.cs`
- `src/RoslynKit/RoslynCommandExecutor.cs`

## Nearest tests

- `tests/RoslynKit.Tests/CliParserTests.cs`
- `tests/RoslynKit.Tests/CommandExecutionTests.cs`
- `tests/RoslynKit.Tests/EnvelopeTests.cs`

## Build/test commands

- `dotnet test .\tests\RoslynKit.Tests\RoslynKit.Tests.csproj`
- `dotnet run --project .\src\RoslynKit -- help`

## Invariants

- Top-level `--version` rewrites to `version`.
- One builtin registry owns command metadata.
- Structured commands return JSON envelopes.

## Common pitfalls

- Changing parser rules without updating registry, help, and tests.
- Reading deep executor code before checking parser behavior and nearby tests.

## Do not read first

- `artifacts/`
- `TestResults/`
- `Visual Studio 18/`

## Last verified

`2026-06-27`
