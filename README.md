# RoslynKit

RoslynKit is an independent Roslyn-powered command-line tool plus the [.agents/skills/roslynkit/SKILL.md](.agents/skills/roslynkit/SKILL.md) workflow that helps coding agents navigate C# solutions through Roslyn instead of relying only on grep-style text search.

Install the CLI, point it at a `.slnx`, `.sln`, or `.csproj` file, and it can answer source questions that normally require an IDE:

- What projects and source files does this solution load?
- Where is this class or method defined?
- What references this symbol?
- What implementations exist for this interface or method?
- What type, signature, or XML documentation is available at this call site?
- What compiler diagnostics does Roslyn report for the loaded code?

The CLI loads .NET projects with MSBuild and asks Roslyn for source information. Roslyn is the official .NET compiler platform for C# and Visual Basic; RoslynKit currently focuses on C# inspection.

RoslynKit prints stable terminal output so people can read it, copy it into issues, or use it in scripts.

## Install

Install the global .NET tool from NuGet.org:

```powershell
dotnet tool install --global roslynkit
roslynkit version
```

Update an existing install:

```powershell
dotnet tool update --global roslynkit
```

For local package feeds and side-by-side prerelease development installs, see [docs/dev-install.md](docs/dev-install.md). For maintainer packaging and release steps, see [docs/dotnet-tool-release.md](docs/dotnet-tool-release.md).

## Common Tasks

| Command | Use it to |
| --- | --- |
| `workspace` | See which projects and documents load. |
| `diagnostics` | Check compiler diagnostics. |
| `symbols` | Find C# declarations by name. |
| `document-symbols` | List declarations inside one file. |
| `definition` | Jump from a symbol or cursor position to its definition. |
| `type-definition` | Jump from a cursor position to the definition of its type. |
| `references` | Find uses of a class, method, property, field, or other symbol. |
| `implementations` | Find implementations of an interface, abstract member, or overridable member. |
| `quick-info` | Show type, signature, and documentation at a cursor position. |
| `signature-help` | Show overload information for a method call. |
| `document-lines`, `document-text`, `symbol-source` | Read source from the loaded workspace. |

For exact command syntax, use `roslynkit help`, `roslynkit help <command>`, or [docs/agents/roslynkit-command-reference.md](docs/agents/roslynkit-command-reference.md).

## Quick Start

Start by confirming RoslynKit can load your solution or project:

```powershell
roslynkit workspace --target .\MySolution.slnx
roslynkit diagnostics --target .\MySolution.slnx
```

Find a declaration by name, then reuse the returned symbol ID for more precise navigation:

```powershell
roslynkit symbols --target .\MySolution.slnx --query MyService --exact --kind class
roslynkit definition --target .\MySolution.slnx --symbol "T:MyApp.MyService"
roslynkit references --target .\MySolution.slnx --symbol "M:MyApp.MyService.Execute(System.String)" --max-results 20
roslynkit symbol-source --target .\MySolution.slnx --symbol "M:MyApp.MyService.Execute(System.String)"
```

Read a small source window from the workspace Roslyn loaded:

```powershell
roslynkit document-lines --target .\MySolution.slnx --file .\src\MyApp\Service.cs --start-line 40 --end-line 52
```

Targets can be `.slnx`, `.sln`, or `.csproj` files. Source positions are one-based, matching editor line and column numbers.

## CLI Plus Skill Files

RoslynKit is a normal CLI process. It is not an MCP server, an LSP client, or a background daemon.

If you want an AI coding tool to use RoslynKit, pair the CLI with a skill file that teaches the tool which commands to run. The stable skill lives at [.agents/skills/roslynkit/SKILL.md](.agents/skills/roslynkit/SKILL.md), and the repo-local development skill lives at [.agents/skills/roslynkit-dev/SKILL.md](.agents/skills/roslynkit-dev/SKILL.md). The integration model is still just command-line execution: install `roslynkit`, then run `roslynkit <command> ...`.

## Selecting Documents

Document commands accept `--target` plus `--file <path>`.

Relative `--file` values resolve from the current working directory. Absolute paths are accepted unchanged.

Use `workspace` first when the same file appears in multiple project contexts, when a project targets multiple frameworks, or when you need generated, additional, or analyzer-config documents. If one path maps to multiple documents, retry with `--project <path>`, `--tfm <framework>`, or `--document-kind <source|sourceGenerated|additional|analyzerConfig>` from the usage error.

Use `document-lines` when you only need a small source range. Use `document-text` when you need the full resolved document, including source-generated, additional, or analyzer-config documents.

## Selecting Symbols

`definition`, `references`, and `implementations` accept either a cursor-style selector or a symbol selector:

```powershell
roslynkit definition --target .\MySolution.slnx --file .\src\MyApp\Service.cs --line 42 --column 18
roslynkit definition --target .\MySolution.slnx --symbol "M:MyApp.MyService.Execute(System.String)"
```

The `--symbol` selector can be a Roslyn documentation-comment ID emitted as `id:` in command output, such as `T:MyApp.MyService` or `M:MyApp.MyService.Execute(System.String)`, or a qualified symbol name such as `MyApp.MyService.Execute`. Prefix meanings are defined in [docs/markdown-output-format.md](docs/markdown-output-format.md).

If a qualified name is ambiguous, RoslynKit fails with candidate documentation-comment IDs so you can rerun the command with the exact symbol. Symbol IDs are more stable than saved line and column coordinates when files are changing.

## Output

Successful commands print compact markdown-flavored text:

```markdown
command: symbols
query: `MyService`
returned: 2/2
truncated: false

- kind: NamedType name: `MyApp.MyService` loc: `src/MyApp/MyService.cs:8:14-8:23` id: `T:MyApp.MyService`
  documentation: Runs application work for the current request.
```

Failures print a short error block and exit non-zero:

```text
error: usage
message: Missing required option '--target'.
```

Exit codes are `0` for success, `2` for usage errors, `130` for cancellation, and `1` for other failures. See [docs/markdown-output-format.md](docs/markdown-output-format.md) for the complete output contract.

## Documentation

- [docs/agents/roslynkit-command-reference.md](docs/agents/roslynkit-command-reference.md): generated command names, usage strings, and options.
- [docs/markdown-output-format.md](docs/markdown-output-format.md): command output contract.
- [docs/dev-install.md](docs/dev-install.md): side-by-side prerelease development install.
- [docs/dotnet-tool-release.md](docs/dotnet-tool-release.md): maintainer packaging and release workflow.
- [docs/roslyn-lsp-commands.md](docs/roslyn-lsp-commands.md): Roslyn language-server inventory and RoslynKit planning coverage.
- [docs/token-efficiency-benchmark.md](docs/token-efficiency-benchmark.md): manual token-efficiency benchmark procedure.
- [docs/agents/README.md](docs/agents/README.md): operational docs for people maintaining RoslynKit skill files and AI-tool guidance.

## Non-Goals

- No MCP transport.
- No LSP transport.
- No background daemon.
- No editor-specific protocol coupling.
- No source mutation by default. If edit-producing features are added later, they should return deterministic proposed edits before any apply mode exists.
