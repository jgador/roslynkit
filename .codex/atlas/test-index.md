# Test Index

## Test Projects

- `tests/RoslynKit.Tests/` is the main xUnit v3 plus Microsoft Testing Platform suite.
- `tests/RoslynKit.WorkspaceGraphDump/` is a console utility in the test tree, not a test project.
- `tests/RoslynKit.AtlasPromptCacheProbe/` is a console utility in the test tree for repeated Atlas Responses API probes with prompt caching.
- `tests/FixtureWorkspace/App/` is fixture input for command execution tests.

## Naming

- Flat `*Tests.cs` files map to focused domains.
- `TestPaths.cs` is the shared path and fixture helper.
- Test methods use behavior-style names.

## Common Commands

- Full suite: `dotnet test .\RoslynKit.slnx`
- Main test project: `dotnet test .\tests\RoslynKit.Tests\RoslynKit.Tests.csproj`
- Explicit live Codex token benchmark: `dotnet test .\tests\RoslynKit.Tests\RoslynKit.Tests.csproj -- --explicit only --filter-method RoslynKit.Tests.CodexExecTokenComparisonTests.RoslynkitDevPrompt_UsesFewerInputTokens_ThanShellOnlyCodexExec`
- Workspace graph utility: `dotnet run --project .\tests\RoslynKit.WorkspaceGraphDump -- .\RoslynKit.slnx`
- Atlas prompt-cache probe: `dotnet run --project .\tests\RoslynKit.AtlasPromptCacheProbe`

## Source To Test Map

- command parsing and option validation -> `tests/RoslynKit.Tests/CliParserTests.cs`
- command execution and navigation flows -> `tests/RoslynKit.Tests/CommandExecutionTests.cs`
- live Codex shell-only versus RoslynKit-first token benchmark -> `tests/RoslynKit.Tests/CodexExecTokenComparisonTests.cs`
- Atlas prompt-cache probe helpers and request shaping -> `tests/RoslynKit.Tests/AtlasPromptCacheProbeTests.cs`
- JSON envelopes, help, and version output -> `tests/RoslynKit.Tests/EnvelopeTests.cs`
- symbol search behavior -> `tests/RoslynKit.Tests/SymbolsCommandTests.cs`
- repo and fixture path helpers -> `tests/RoslynKit.Tests/TestPaths.cs`
