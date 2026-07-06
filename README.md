# RoslynKit

RoslynKit is an unofficial Roslyn-powered C# code intelligence CLI for terminal workflows and coding agents.

It exposes Git-style subcommands for Roslyn/MSBuild-backed inspection. Commands return deterministic markdown-flavored text on stdout, while `version` and top-level `--version` print a plain-text version line.

RoslynKit is deliberately not an MCP server, not an LSP client, and not a background daemon. Its first job is read-only C# intelligence: inspect, navigate, understand, and verify code without changing source files or project state.

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

For local package feeds and side-by-side prerelease development installs, see `docs/dev-install.md` and `docs/dotnet-tool-release.md`.

## What It Does

- Load `.slnx`, `.sln`, and `.csproj` targets through MSBuildWorkspace.
- List workspace documents, including generated, additional, and analyzer-config documents when requested.
- Search C# declarations with `symbols` and inspect one file with `document-symbols`.
- Navigate with `definition`, `type-definition`, `references`, and `implementations`.
- Inspect call sites with `quick-info` and `signature-help`.
- Read resolved source through `document-lines`, `document-text`, and `symbol-source`.
- Report compiler diagnostics with `diagnostics`.

## Quick Start

```powershell
roslynkit workspace --target .\MySolution.slnx
roslynkit diagnostics --target .\MySolution.slnx
roslynkit symbols --target .\MySolution.slnx --query MyService --exact --kind class
roslynkit definition --target .\MySolution.slnx --symbol MyApp.MyService
roslynkit references --target .\MySolution.slnx --symbol MyApp.MyService.Execute --max-results 20
roslynkit document-lines --target .\MySolution.slnx --file .\src\MyApp\Service.cs --start-line 40 --end-line 52
roslynkit symbol-source --target .\MySolution.slnx --symbol "M:MyApp.MyService.Execute(System.String)"
```

Targets can be `.slnx`, `.sln`, or `.csproj` files. Source positions are one-based. `version` and top-level `--version` do not require `--target`.

## Output

Successful command results use a compact markdown-flavored format:

```markdown
command: symbols
query: `MyService`
returned: 2/2
truncated: false

- kind: NamedType name: `MyApp.MyService` loc: `src/MyApp/MyService.cs:8:14-8:23` id: `T:MyApp.MyService`
  documentation: Runs application work for the current request.
```

Failures print plain text and exit non-zero:

```text
error: usage
message: Missing required option '--target'.
```

Exit codes are `0` for success, `2` for usage errors, `130` for cancellation, and `1` for other failures. See `docs/markdown-output-format.md` for the complete output contract.

## Selecting Documents

Document-oriented commands accept `--target` plus `--file <path>`. Relative `--file` values resolve from the current working directory; absolute paths are accepted unchanged.

Use `workspace` first when the same file appears in multiple project contexts, when a project targets multiple frameworks, or when you need generated, additional, or analyzer-config documents. If one path maps to multiple documents, retry with `--project <path>`, `--tfm <framework>`, or `--document-kind <source|sourceGenerated|additional|analyzerConfig>` from the usage error.

`document-text` can read source, source-generated, additional, and analyzer-config documents. `document-lines` is better when a small line window is enough.

## Selecting Symbols

`definition`, `references`, and `implementations` accept either a position selector (`--file` with `--line` and `--column`) or `--symbol <selector>`.

The `--symbol` selector can be a Roslyn documentation-comment ID such as `T:MyApp.MyService` or `M:MyApp.MyService.Execute(System.String)`, or a qualified symbol name such as `MyApp.MyService.Execute`. If a qualified name is ambiguous, RoslynKit fails with candidate documentation-comment IDs so the exact symbol can be retried.

Symbol bullets include an `id:` field when Roslyn can provide a documentation-comment ID. That ID can be passed directly to later symbol-based commands and is more stable than cached line and column coordinates.

## Command Reference

The exact runtime command reference is generated from `BuiltinCommandRegistry`:

- `docs/agents/roslynkit-command-reference.md`: checked-in command names, usage strings, and options.
- `roslynkit help`: runtime command overview.
- `roslynkit help <command>`: runtime help for one command.

The generated reference is the source to use when command syntax matters.

## Documentation

- `docs/dev-install.md`: side-by-side prerelease development install.
- `docs/dotnet-tool-release.md`: maintainer packaging and release workflow.
- `docs/markdown-output-format.md`: command output contract.
- `docs/agents/roslynkit-command-reference.md`: generated runtime command reference.
- `docs/roslyn-lsp-commands.md`: Roslyn language-server inventory and RoslynKit planning coverage.
- `docs/token-efficiency-benchmark.md`: manual Codex token-efficiency benchmark procedure.
- `docs/agents/README.md`: coding-agent operational docs.

## Non-Goals

- No MCP transport.
- No LSP transport.
- No background daemon.
- No editor-specific protocol coupling.
- No source mutation by default; future edit-producing features should emit proposed changes before any apply mode exists.
