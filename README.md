# RoslynKit

RoslynKit is an unofficial Roslyn-powered C# code intelligence CLI for coding agents and terminal workflows.

It is deliberately not an MCP server and not an LSP client. The CLI exposes Git-style subcommands for Roslyn/MSBuild-backed C# inspection. Commands return deterministic markdown-flavored text on stdout, built to minimize token cost for coding agents, while `version` and top-level `--version` print a plain-text version line.

RoslynKit prioritizes read-only Roslyn intelligence: inspect, navigate, understand, and verify C# code without changing source files or project state.

This guide assumes `roslynkit` is already installed and available on `PATH`. For the side-by-side prerelease dev install, see `docs/dev-install.md`.

## Goals

- Use Roslyn APIs directly instead of shelling out to an editor, language server, or IDE.
- Prioritize read-only code intelligence before edit-producing refactor or formatting features.
- Keep commands deterministic and scriptable for agents and terminal workflows.
- Emit one deterministic markdown-flavored text shape for every command result: key-value header lines, labeled compact bullets, inline code spans, and fenced code blocks for source text. Failures print a plain-text error and exit non-zero.
- Support solution-level and project-level inspection with stable sorting and one-based source positions.

## Commands

Commands write markdown-flavored text described by `docs/markdown-output-format.md`. A successful result starts with key-value header lines and lists repeated items as compact bullets:

```markdown
command: symbols
query: `MyService`
returned: 2/2
truncated: false

- kind: NamedType name: `MyApp.MyService` loc: `src/MyApp/MyService.cs:8:14-8:23` id: `T:MyApp.MyService`
  documentation: Runs application work for the current request.
```

A failed result prints plain-text lines and the process exits non-zero:

```text
error: usage
message: Missing required option '--target'.
```

All failures include `error:` and `message:`. Usage errors with a deterministic retry path may add a `hint:` line:

```text
error: usage
message: Line 70 is outside the document range 1..13.
hint: Retry with --line between 1 and 13, or run document-lines to inspect valid source lines before choosing --line/--column.
```

Exit codes: `0` success, `2` usage error, `130` canceled, `1` any other failure. The `error:` value is `usage`, `canceled`, or the exception type name.

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
roslynkit document-text --target .\MySolution.slnx --file .\src\MyApp\Service.cs
roslynkit document-lines --target .\MySolution.slnx --file .\src\MyApp\Service.cs --start-line 40 --end-line 52
roslynkit definition --target .\MySolution.slnx --file .\src\MyApp\Program.cs --line 10 --column 20
roslynkit definition --target .\MySolution.slnx --symbol MyApp.MyService
roslynkit references --target .\MySolution.slnx --file .\src\MyApp\Program.cs --line 10 --column 20
roslynkit references --target .\MySolution.slnx --symbol MyApp.MyService.Execute
roslynkit implementations --target .\src\MyApp\MyApp.csproj --file .\src\MyApp\Service.cs --line 23 --column 23
roslynkit implementations --target .\MySolution.slnx --symbol MyApp.IMyService
roslynkit quick-info --target .\MySolution.slnx --file .\src\MyApp\Program.cs --line 10 --column 20
roslynkit type-definition --target .\src\MyApp\MyApp.csproj --file .\src\MyApp\Service.cs --line 27 --column 22
roslynkit signature-help --target .\MySolution.slnx --file .\src\MyApp\Program.cs --line 42 --column 17
roslynkit symbol-source --target .\MySolution.slnx --symbol "M:MyApp.MyService.Execute(System.String)"
roslynkit document-text --target .\src\MyApp\MyApp.csproj --file .\obj\Debug\net10.0\Generated.g.cs --document-kind sourceGenerated
```

Targets can be `.slnx`, `.sln`, or `.csproj` files. Source positions are one-based. `version` and top-level `--version` do not require `--target`.

`workspace` defaults to repo-relevant source documents only. Add `--include-generated`, `--include-additional`, and `--include-analyzer-config` when you need source-generated files, `AdditionalFiles`, or analyzer config documents. Document rows include the owning project path, target framework, document kind, and document path. Paths under the loaded root render relative; files outside the loaded root render as absolute paths.

For document-oriented commands such as `document-symbols`, `document-text`, `document-lines`, `definition`, `references`, `implementations`, `quick-info`, `type-definition`, and `signature-help`, pass `--target` plus `--file <path>`. Relative `--file` values resolve from the current working directory; absolute paths are accepted unchanged. Use `workspace` first when the same file appears in multiple project contexts or when you need the path for a generated, additional, or analyzer-config document. If one path still maps to multiple documents, retry with `--project <path>`, `--tfm <framework>`, or `--document-kind <source|sourceGenerated|additional|analyzerConfig>` from the usage error. Semantic position commands operate on C# source or source-generated documents and keep line/column coordinates strict. `document-text` can read source, source-generated, additional, and analyzer-config documents and always returns the entire resolved document. Use `document-lines` when a small line window is enough; it reads from `--start-line` through the lesser of `--end-line` and EOF. If Roslyn exposes a document with no usable path, it is not addressable by document commands; use symbol-based commands when a C# declaration is available.

`definition`, `references`, and `implementations` also accept `--symbol <selector>` instead of the position selector. The selector is either a Roslyn documentation-comment ID (`T:`, `M:`, `P:`, `F:`, `E:`, or `N:` prefix, for example `M:MyApp.MyService.Execute(System.String)`) or a qualified symbol name such as `MyApp.MyService.Execute`. `--symbol` cannot be combined with `--file`, `--project`, `--tfm`, `--document-kind`, `--line`, or `--column`. When a qualified name matches several declarations (for example method overloads), the command fails with a deterministic usage error listing the candidate documentation-comment IDs so the exact one can be retried. In symbol mode the result echoes the input as `selector:`. Constructors are addressable only through `M:...#ctor(...)` documentation-comment IDs.

Every symbol bullet includes an `id:` field carrying the symbol's documentation-comment ID, so results chain by identity: take `id:` from `symbols`, `definition`, or `references` output and pass it straight to the next `--symbol` command without copying line and column numbers. The ID stays valid after files are edited; cached line numbers do not. Symbol bullets from `symbols`, `document-symbols`, `definition`, `type-definition`, and `implementations` may include an indented `documentation:` line when the resolved symbol has a non-empty XML summary.

`symbol-source` takes `--target` plus `--symbol` and returns the full declaration source text for the resolved symbol: one entry per declaring block with its document descriptor, true block range, and text. Partial types return every declaring block, including source-generated ones. The text covers the declaration node itself (attributes included); leading XML documentation comments are trivia and are not included.

The markdown output is the only format; there is no `--format` option. Locations render as one-based `path:line:column-endLine:endColumn` ranges, and `symbol-source`, `document-text`, `document-lines`, and `quick-info` payloads stay verbatim inside fenced code blocks — real newlines and quotes, no string escaping. A zero exit code means stdout is command output; a non-zero exit code means stdout is the plain-text error. See `docs/markdown-output-format.md` for the full per-command output contract.

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
- `docs/markdown-output-format.md`: the markdown output contract for all CLI command results.
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
