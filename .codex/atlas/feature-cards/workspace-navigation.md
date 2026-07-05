# Workspace And Navigation

## Purpose

Load targets, resolve documents and positions, and answer Roslyn-backed navigation commands.

## Task keywords

- workspace load
- document selection
- symbol search
- navigation commands
- generated documents

## Entrypoints

- `src/RoslynKit/RoslynCommandExecutor.cs`
- `src/RoslynKit/RoslynWorkspaceLoader.cs`
- `src/RoslynKit/PositionResolver.cs`
- `src/RoslynKit/RoslynSymbolResolver.cs`
- `src/RoslynKit/RoslynDocumentFilters.cs`
- `src/RoslynKit/RoslynSymbolSearch.cs`
- `src/RoslynKit/RoslynSignatureHelpService.cs`

## Important symbols

- `RoslynCommandExecutor.ExecuteAsync`
- `RoslynWorkspaceLoader.LoadAsync`
- `RoslynWorkspaceLoader.FindTextDocumentAsync`
- `PositionResolver.GetPositionAsync`
- `PositionResolver.ToDocumentRange`
- `RoslynSymbolResolver.ResolveAsync`
- `RoslynSymbolSearch.EnumerateSourceSymbols`
- `RoslynSignatureHelpService.GetSignatureHelpAsync`

## Important files

- `src/RoslynKit/RoslynCommandExecutor.cs`
- `src/RoslynKit/RoslynWorkspaceLoader.cs`
- `src/RoslynKit/PositionResolver.cs`
- `src/RoslynKit/RoslynSymbolResolver.cs`
- `src/RoslynKit/RoslynDocumentFilters.cs`
- `src/RoslynKit/RoslynSymbolSearch.cs`
- `src/RoslynKit/RoslynSignatureHelpService.cs`

## Nearest tests

- `tests/RoslynKit.Tests/CommandExecutionTests.cs`
- `tests/RoslynKit.Tests/SymbolsCommandTests.cs`
- `tests/RoslynKit.Tests/CliOutputTests.cs`
- `tests/RoslynKit.WorkspaceGraphDump/Program.cs`

## Build/test commands

- `dotnet test .\tests\RoslynKit.Tests\RoslynKit.Tests.csproj`
- `dotnet run --project .\tests\RoslynKit.WorkspaceGraphDump -- .\RoslynKit.slnx`

## Invariants

- `--target` is explicit.
- Document selectors are `--file` or `--document-key`.
- Positions are one-based.
- `definition`, `references`, and `implementations` accept `--symbol` (doc-comment ID or qualified name) as an alternative to the position selector; `symbol-source` is `--symbol`-only.
- Every `SymbolItem` payload carries `symbolId` (documentation-comment ID) for identity chaining.
- Prefer symbol and line-range reads over full-file reads.
- Do not build or read a repo-wide symbol index; compose live RoslynKit commands from task-sized seeds.

## Common pitfalls

- Skipping `workspace` when file context is ambiguous.
- Reading full files instead of exact ranges.
- Ignoring tests before implementation.
- Treating generated Atlas metadata as semantic truth instead of using RoslynKit live queries.

## Do not read first

- `docs/dotnet-tool-release.md`
- `scripts/prepare-roslynkit-package.ps1`
- `artifacts/`

## Last verified

`2026-07-05`
