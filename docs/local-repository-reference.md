# Local Repository Reference

This document maps the local repositories that RoslynKit should use as first-pass references for Roslyn features, CLI architecture, .NET tool conventions, and C# language-server wiring. Prefer these local checkouts before remote GitHub inspection so searches stay fast, reproducible, and grounded in the code available on this machine.

## Repository Map

| Repository | Local path | Use it for | Avoid using it for |
| --- | --- | --- | --- |
| EF Core | `C:\repo\GitHub\efcore` | Official .NET CLI tooling conventions, `dotnet ef` packaging, command tree shape, command option registration, project/startup resolution. | Roslyn feature behavior. |
| Git | `C:\repo\GitHub\git` | Simple subcommand dispatch, builtin command table style, option parsing conventions, help/usage behavior. | .NET implementation style. |
| Roslyn | `C:\repo\GitHub\roslyn` | Roslyn APIs, workspace loading, symbol search, definitions, references, diagnostics, source generators, language-server handlers. | RoslynKit CLI design unless the question is feature behavior. |
| VS Code C# | `C:\repo\GitHub\vscode-csharp` | How the VS Code C# extension locates, launches, configures, and integrates the Roslyn language server. | The Roslyn server implementation itself. That lives in the Roslyn repo. |

## Default Search Order

1. Search this repo first for RoslynKit decisions and current command contracts.
2. Search `C:\repo\GitHub\roslyn` for Roslyn feature semantics and test coverage.
3. Search `C:\repo\GitHub\vscode-csharp` for language-server launch/client behavior.
4. Search `C:\repo\GitHub\efcore` for .NET CLI/tool packaging and command architecture.
5. Search `C:\repo\GitHub\git` for simple CLI command dispatch and option/help conventions.

Use `rg` from the target repo root. Prefer exact symbols or file names over broad terms like `server`, `command`, or `symbol`.

## EF Core

Use EF Core as the local reference for official .NET tool conventions and the `dotnet ef` command architecture.

High-signal paths:

- `C:\repo\GitHub\efcore\src\dotnet-ef\dotnet-ef.csproj` - .NET tool packaging, `PackAsTool`, platform shims, linked command/parser sources, and packaging of the inner `ef` tool.
- `C:\repo\GitHub\efcore\src\dotnet-ef\Program.cs` - `dotnet ef` entrypoint and top-level `CommandLineApplication` setup.
- `C:\repo\GitHub\efcore\src\dotnet-ef\RootCommand.cs` - top-level `dotnet ef` orchestration, help routing, project resolution, config loading, and re-exec flow.
- `C:\repo\GitHub\efcore\src\dotnet-ef\ProjectOptions.cs` - canonical project/startup/framework/runtime/build flags.
- `C:\repo\GitHub\efcore\src\dotnet-ef\Project.cs` - project metadata extraction and `dotnet build` invocation.
- `C:\repo\GitHub\efcore\src\dotnet-ef\Exe.cs` - process-launch helper for build and re-exec behavior.
- `C:\repo\GitHub\efcore\src\ef\CommandLineUtils\CommandLineApplication.cs` - custom command parser, help/version behavior, response files, argument separators, remaining arguments, and application arguments.
- `C:\repo\GitHub\efcore\src\ef\Commands\CommandBase.cs` - shared command execution setup and validation flow.
- `C:\repo\GitHub\efcore\src\ef\Commands\ProjectCommandBase.cs` - shared project/startup/design-time option plumbing.
- `C:\repo\GitHub\efcore\src\ef\Commands\RootCommand.cs` - root EF command tree: `database`, `dbcontext`, and `migrations`.
- `C:\repo\GitHub\efcore\Directory.Build.props` and `C:\repo\GitHub\efcore\src\Directory.Build.props` - build, analyzer, nullable, deterministic build, and packaging conventions.
- `C:\repo\GitHub\efcore\.editorconfig` and `C:\repo\GitHub\efcore\src\.editorconfig` - formatting and analyzer policy.

Useful searches:

```powershell
rg -n "PackAsTool|ToolCommandName|PackAsToolShimRuntimeIdentifiers" C:\repo\GitHub\efcore\src\dotnet-ef
rg -n "CommandLineApplication|HelpOption|VersionOption|OnExecute|Validate" C:\repo\GitHub\efcore\src\ef
rg -n "--project|--startup-project|--framework|--configuration|--no-build" C:\repo\GitHub\efcore\src\dotnet-ef C:\repo\GitHub\efcore\src\ef
```

RoslynKit takeaways:

- EF Core is useful for command classes, shared base-command behavior, and .NET tool packaging.
- `src\dotnet-ef` is a launcher/orchestration layer; `src\ef` contains the reusable command tree and parser model.
- EF Core uses `Cli` in C# identifiers such as `Microsoft.DotNet.Cli.CommandLine`; reserve `CLI` for prose.

## Git

Use Git as the local reference for simple command dispatch and terse subcommand architecture.

High-signal paths:

- `C:\repo\GitHub\git\git.c` - builtin dispatch type, `commands[]` table, `get_builtin()`, and `git cmd --help` routing.
- `C:\repo\GitHub\git\builtin.h` - builtin command contribution contract and setup flags such as `RUN_SETUP`, `NEED_WORK_TREE`, and `NO_PARSEOPT`.
- `C:\repo\GitHub\git\parse-options.h` - option parser API, usage helpers, option grouping, and subcommand support.
- `C:\repo\GitHub\git\command-list.txt` - categorized command list used by help/listing behavior.
- `C:\repo\GitHub\git\help.c` - shared help/listing and unknown-command handling.
- `C:\repo\GitHub\git\builtin\help.c` - concrete `git help` subcommand, usage text, and help option parsing.
- `C:\repo\GitHub\git\Documentation\gitcli.adoc` - CLI style guide for option placement, `-h`, `--help-all`, and stuck-form options.
- `C:\repo\GitHub\git\Documentation\technical\api-parse-options.adoc` - parse-options behavior and usage conventions.
- `C:\repo\GitHub\git\Documentation\git-help.adoc` and `C:\repo\GitHub\git\Documentation\git.adoc` - user-facing help and top-level command syntax.

Useful searches:

```powershell
rg -n "struct cmd_struct|commands\\[\\]|get_builtin|--help" C:\repo\GitHub\git\git.c
rg -n "RUN_SETUP|NEED_WORK_TREE|NO_PARSEOPT|command-list" C:\repo\GitHub\git\builtin.h
rg -n "parse_options|usage_with_options|OPT_SUBCOMMAND|OPT_GROUP" C:\repo\GitHub\git\parse-options.h
```

RoslynKit takeaways:

- Keep builtin registration obvious and discoverable.
- Keep command help and option metadata close to command registration.
- Use Git as style guidance, not as a C# architecture template.

## Roslyn

Use Roslyn as the source of truth for Roslyn feature behavior and tests.

High-signal paths:

- `C:\repo\GitHub\roslyn\src\Workspaces\Core\Portable\Workspace\Workspace.cs` - core workspace model.
- `C:\repo\GitHub\roslyn\src\Workspaces\Core\Portable\Workspace\Workspace_SourceGeneration.cs` - source-generator lifecycle and solution updates.
- `C:\repo\GitHub\roslyn\src\Workspaces\Core\Portable\Workspace\SourceGeneratorExecution.cs` - source-generator execution preferences.
- `C:\repo\GitHub\roslyn\src\Workspaces\MSBuild\Core\MSBuild\MSBuildWorkspace.cs` - `MSBuildWorkspace.Create`, `OpenSolutionAsync`, and `OpenProjectAsync`.
- `C:\repo\GitHub\roslyn\src\Workspaces\Core\Portable\FindSymbols\SymbolFinder.cs` - public symbol lookup and source-definition APIs.
- `C:\repo\GitHub\roslyn\src\Workspaces\Core\Portable\FindSymbols\SymbolFinder_FindReferences_Current.cs` - current find-references implementation path.
- `C:\repo\GitHub\roslyn\src\Features\Core\Portable\FindUsages` - feature-layer find references, implementations, and definition item models.
- `C:\repo\GitHub\roslyn\src\Features\Core\Portable\GoToDefinition` - go-to-definition services and helpers.
- `C:\repo\GitHub\roslyn\src\Features\Core\Portable\Diagnostics` - diagnostic analyzer service and diagnostic pipeline.
- `C:\repo\GitHub\roslyn\src\LanguageServer\Protocol\Handler` - LSP handler implementations for definitions, references, symbols, diagnostics, and source generators.
- `C:\repo\GitHub\roslyn\src\LanguageServer\Protocol\Protocol` - protocol DTO shapes for definitions, references, symbols, and diagnostics.
- `C:\repo\GitHub\roslyn\src\LanguageServer\Microsoft.CodeAnalysis.LanguageServer` - Roslyn language-server process bootstrap.
- `C:\repo\GitHub\roslyn\src\LanguageServer\ProtocolUnitTests` - protocol-level behavior tests.
- `C:\repo\GitHub\roslyn\src\EditorFeatures\Test` - editor-facing symbol finder, references, and diagnostics tests.
- `C:\repo\GitHub\roslyn\src\Workspaces\MSBuild\Test` - `MSBuildWorkspace` integration coverage.

Useful searches:

```powershell
rg -n "FindSourceDefinitionAsync|FindReferencesAsync|SymbolFinder" C:\repo\GitHub\roslyn\src\Workspaces
rg -n "OpenSolutionAsync|OpenProjectAsync|MSBuildWorkspace.Create" C:\repo\GitHub\roslyn\src\Workspaces\MSBuild
rg -n "textDocument/definition|textDocument/references|DocumentSymbols|WorkspaceSymbols" C:\repo\GitHub\roslyn\src\LanguageServer
rg -n "SourceGeneratedDocument|RefreshSourceGenerators|source generator" C:\repo\GitHub\roslyn\src\LanguageServer C:\repo\GitHub\roslyn\src\Workspaces
rg -n "PullDiagnostic|DocumentDiagnostic|WorkspaceDiagnostic|DiagnosticAnalyzerService" C:\repo\GitHub\roslyn\src
```

RoslynKit takeaways:

- Prefer Roslyn public APIs directly where practical: `MSBuildWorkspace`, `SymbolFinder`, semantic models, syntax roots, diagnostics, and solution/project/document models.
- Use LSP handler implementations as behavioral references, not as transport code to copy.
- Use protocol unit tests to understand edge cases and expected shapes when adding similar RoslynKit JSON commands.

## VS Code C#

Use `vscode-csharp` as the local reference for how Microsoft wires the Roslyn language server into a VS Code extension. This repo is client/launcher-side evidence, not the Roslyn server source.

High-signal paths:

- `C:\repo\GitHub\vscode-csharp\src\activateRoslyn.ts` - top-level Roslyn activation bootstrap and C# Dev Kit export probing.
- `C:\repo\GitHub\vscode-csharp\src\lsptoolshost\activate.ts` - launcher setup, server path resolution, plugin scanning, and language-status registration.
- `C:\repo\GitHub\vscode-csharp\src\lsptoolshost\server\roslynLanguageServer.ts` - executable launch construction, standalone vs C# Dev Kit activation, activation context, and server arguments.
- `C:\repo\GitHub\vscode-csharp\src\lsptoolshost\server\roslynLanguageClient.ts` - client-side crash/error handling and `LanguageClient` subclassing.
- `C:\repo\GitHub\vscode-csharp\src\utils\getCSharpDevKit.ts` - C# Dev Kit detection and `dotnet.preferCSharpExtension` opt-out.
- `C:\repo\GitHub\vscode-csharp\src\csharpDevKitExports.ts` - integration surface expected from C# Dev Kit.
- `C:\repo\GitHub\vscode-csharp\src\lsptoolshost\extensions\builtInComponents.ts` - `.roslynDevKit`, `.roslynCopilot`, `.xamlTools`, and component path overrides.
- `C:\repo\GitHub\vscode-csharp\src\lsptoolshost\extensions\roslynLanguageServerExportChannel.ts` - post-start request bridge.
- `C:\repo\GitHub\vscode-csharp\src\shared\options.ts` - canonical `dotnet.server.*` setting names.
- `C:\repo\GitHub\vscode-csharp\package.json` - contributed settings for server path, component paths, activation context, and command/menu gating.
- `C:\repo\GitHub\vscode-csharp\CONTRIBUTING.md` - local Roslyn server debugging and C# Dev Kit override guidance.
- `C:\repo\GitHub\vscode-csharp\test\lsptoolshost\integrationTests` - integration tests for definitions, references, document symbols, diagnostics, source generators, formatting, and more.

Useful searches:

```powershell
rg -n "Microsoft.CodeAnalysis.LanguageServer|\\.roslyn|--stdio|autoLoad|server.path" C:\repo\GitHub\vscode-csharp\src C:\repo\GitHub\vscode-csharp\package.json
rg -n "ms-dotnettools.csdevkit|preferCSharpExtension|roslynDevKit|activationContext" C:\repo\GitHub\vscode-csharp\src C:\repo\GitHub\vscode-csharp\package.json
rg -n "definition|references|documentSymbol|diagnostic|sourceGenerator" C:\repo\GitHub\vscode-csharp\test\lsptoolshost\integrationTests
```

RoslynKit takeaways:

- VS Code C# is useful for launch settings, extension packaging assumptions, and practical Roslyn server integration.
- It is not proof that RoslynKit should become an LSP client. RoslynKit should continue to call Roslyn APIs directly and expose deterministic JSON CLI commands.
- C# Dev Kit integration is detected through extension exports and component path configuration; treat it as client integration context, not a dependency for RoslynKit.

## RoslynKit Usage Guidance

When adding or changing a RoslynKit feature:

1. Find the closest Roslyn implementation and tests in `C:\repo\GitHub\roslyn`.
2. If the feature is already exposed through the language server, inspect the Roslyn LSP handler and protocol unit tests for expected behavior.
3. If the question is how the server is launched or configured by VS Code, inspect `C:\repo\GitHub\vscode-csharp`.
4. If the question is CLI shape, command registration, help, or option parsing, inspect `C:\repo\GitHub\git` and `C:\repo\GitHub\efcore`.
5. If the question is .NET tool packaging, inspect EF Core first.

Keep RoslynKit's product boundary explicit:

- CLI-first.
- JSON stdout for commands.
- Direct Roslyn/MSBuild APIs.
- No MCP server.
- No LSP client.
- No background daemon.

