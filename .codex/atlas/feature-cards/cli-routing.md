# CLI Routing

## Purpose

Route command text from `Program.Main` through parse, help/version, and command execution.

## Task keywords

- command execution
- command routing
- help and version
- usage and error output
- parser validation

## Entrypoints

- `src/RoslynKit/Program.cs`
- `src/RoslynKit/CliApplication.cs`
- `src/RoslynKit/CliParser.cs`
- `src/RoslynKit/BuiltinCommandRegistry.cs`
- `src/RoslynKit/RoslynCommandExecutor.cs`
- `docs/markdown-output-format.md`

## Important symbols

- `Program.Main`
- `CliApplication.RunAsync`
- `CliParser.Parse`
- `BuiltinCommandRegistry.Commands`
- `RoslynCommandExecutor.ExecuteAsync`
- `RoslynCommandExecutor.DefinitionAsync`

## Important files

- `src/RoslynKit/Program.cs`
- `src/RoslynKit/CliApplication.cs`
- `src/RoslynKit/CliParser.cs`
- `src/RoslynKit/BuiltinCommandRegistry.cs`
- `src/RoslynKit/RoslynCommandExecutor.cs`

## Nearest tests

- `tests/RoslynKit.Tests/CliParserTests.cs`
- `tests/RoslynKit.Tests/CommandExecutionTests.cs`
- `tests/RoslynKit.Tests/CliOutputTests.cs`
- `tests/RoslynKit.Tests/MarkdownFormatTests.cs`

## Build/test commands

- `dotnet test .\tests\RoslynKit.Tests\RoslynKit.Tests.csproj`
- `dotnet run --project .\src\RoslynKit -- help`

## Invariants

- Top-level `--version` rewrites to `version`.
- One builtin registry owns command metadata.
- Commands emit markdown-flavored text only (`MarkdownProjection`); there is no `--format` option and passing it is a usage error.
- Failures write a plain-text error (`error:` code, `message:` text, and optional usage-error `hint:` text) to stdout with exit codes 2 (usage), 130 (canceled), or 1 (internal); success is exit 0.
- `definition`, `references`, and `implementations` validate `--symbol` and the position selector as mutually exclusive in `CliParser.ValidateSymbolOrPositionSelector`.
- `document-lines` rejects reversed ranges and `--start-line` beyond EOF, but caps an oversized `--end-line` to the document EOF and reports the actual returned range.

## Common pitfalls

- Changing parser rules without updating registry, help, and tests.
- Reading deep executor code before checking parser behavior and nearby tests.
- Jumping to the `CliApplication` constructor token in `Program.Main` when the routing question is really about the flow into `RunAsync`.

## Cursor choices

- For chained expressions in runtime routing traces, jump to the rightmost invoked method first.
- In `Program.Main`, prefer the `RunAsync` token over the `CliApplication` constructor token when tracing command flow.
- If the question changes from flow to type identity, switch to the `CliApplication` class declaration and its XML summary instead of the constructor.

## Do not read first

- `artifacts/`
- `TestResults/`
- `Visual Studio 18/`

## Last verified

`2026-07-05`
