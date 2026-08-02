---
name: roslynkit-dev
description: Use the side-by-side prerelease RoslynKit dev tool first for C#, TypeScript, and JavaScript semantic inspection; for literal search, prose, unsupported files, or workspace-load failures, let Codex CLI choose the terminal-native fallback for the current platform.
---

# RoslynKit

## Hard Limits (read first)

These override every other section when they conflict. They exist because reading whole files and re-reading files is the dominant token cost.

- Never read a whole supported source file when a position-based command (`quick-info`, `definition`, `references`) answers the question.
- Never read the same file twice. Capture needed context the first time.
- Resolve positions with RoslynKit instead of falling back to `Read`/grep on supported source once the file is loaded.
- Prefer single-target commands (`definition`, `quick-info`, `type-definition`) over `symbols`; `symbols` returns verbose arrays. When `symbols` or `references` are required, always cap output with the smallest useful `--max-results` (often `--max-results 1` to confirm one known declaration).
- Stop once enough evidence is available. Do not gather extra members, siblings, or confirmation reads.

Use this skill for ordinary C#, TypeScript, and JavaScript semantic inspection when the side-by-side prerelease RoslynKit dev tool is already installed with `--tool-path`.

## Routing Rule

- Use RoslynKit first for C#, TypeScript, and JavaScript semantic inspection.
- For literal text search, comments or prose, unsupported files, or RoslynKit workspace-load failures, let Codex CLI choose the terminal-native fallback for the current platform.

Do not default to `Get-Content`, `Select-String`, or grep-style file reads for questions that are really about supported-language declarations, symbol structure, definitions, references, implementations, types, signatures, or generated documents.

## Intent-Based Symbol Discovery

Use `search` when an English-oriented question describes a code responsibility but no declared symbol name is known. The command requires both `--target` and `--index-path`; use a Git-ignored repository-local database such as `./artifacts/roslynkit.db`.

```powershell
& $roslynkitDev search --target .\SomeSolution.slnx --index-path .\artifacts\roslynkit.db --query "how does workspace daemon reload after source changes"
```

`search` validates and refreshes the index automatically. Use `index` to prepare the index deliberately, and add `--rebuild` only when the selected target partition must be recreated.

```powershell
& $roslynkitDev index --target .\SomeSolution.slnx --index-path .\artifacts\roslynkit.db
```

Search ranking is heuristic. Inspect several top results and compare excerpts, symbol kinds, identities, and locations before selecting a navigation target; do not assume rank 1 is correct. Follow up through existing commands with a returned `id:` or `loc:` value. RoslynKit has no standard-input pipeline for search hits; the coding agent selects the appropriate next command.

Search requires projects with one target framework and repository-local physical project and non-generated source paths; missing paths, external projects, and external linked non-generated source paths are rejected. It skips generated source documents, including source-generated documents, paths below `bin` or `obj`, and sources with standard generated-code markers injected from extracted NuGet packages outside the worktree. Use `--project`, `--kind`, or `--max-results` only when a narrower target, symbol kind, or result limit is needed. In the RoslynKit repository, the generated [.agents/skills/roslynkit/references/commands.md](../roslynkit/references/commands.md) file contains the exact options.

## Selector Choice

`definition`, `references`, and `implementations` accept either `--symbol <selector>` or a position (`--file` plus `--line --column`), never both. Pick by the evidence already available:

- Known declared name (type, method, property, field, event): use `--symbol` directly. Do not run `symbols` first just to obtain line and column numbers.
- Known position from prior RoslynKit output (a reference location, a declaration location, a diagnostic) or from the user's cursor: use the position selector for the next hop at that spot.
- The target has no global name (local variable, parameter, lambda parameter): position mode is the only way to address it.
- `signature-help` and mid-expression `quick-info` are always position-based: they answer questions about a spot in the code, not about a declaration.

For C#, `--symbol` takes a documentation-comment ID from RoslynKit `id:` output or a qualified name such as `SomeNamespace.SomeType.SomeMethod`. Prefix meanings for C# documentation-comment IDs are defined in [.agents/skills/roslynkit/references/output.md](../roslynkit/references/output.md). TypeScript and JavaScript use opaque `ts:` selectors emitted in `id:` output; pass them back unchanged and do not invent a C# documentation-comment ID. An ambiguous C# qualified name fails with candidate IDs; retry with the exact ID.

Hard rule: coordinates must come from tool output, a diagnostic, or the user. If reading or searching a file would be required to find a line number, use `--symbol` instead.

When a coordinate usage error includes a `hint:` line, do not retry the same `--line`/`--column`. Use the valid range in `hint:` and pick a new coordinate with `document-lines` or `document-symbols` before retrying semantic commands.

Chain by identity when possible: symbol bullets carry a backend-specific selector as `id:`. Pass that value straight to the next `--symbol` command. After editing a file, cached coordinates are stale; C# documentation IDs generally remain valid, while TypeScript `ts:` selectors should be regenerated from fresh output.

## Default File Scope

RoslynKit selects its backend from `--target`.

- `.slnx`, `.sln`, and `.csproj` select Roslyn and accept `.cs` source. `tsconfig.json` and `jsconfig.json` select native-preview and accept `.ts`, `.tsx`, `.mts`, `.cts`, `.js`, `.jsx`, `.mjs`, and `.cjs` source.
- Generated C# documents are selected by the generated `path` emitted from `workspace --include-generated`. Native-preview currently reports physical configured source documents only.
- Do not use semantic commands for `.md`, generic `.json`, `.xml`, `.yml`, `.yaml`, `.props`, `.targets`, or other unsupported files.
- If the task is prose inspection, comment wording, TODO scanning, or literal text matching, let Codex CLI choose the terminal-native fallback even when the text appears inside a supported source file.
- A supported source file may be inspected when the primary task is semantic analysis or source inspection. Prefer position-based commands, `quick-info`, targeted cross-references, and `document-lines` before `document-text`.

## Command

Resolve the installed dev command once, then invoke it directly:

```powershell
$roslynkitDev = Join-Path (Join-Path (Join-Path $HOME ".roslynkit") "tools") "roslynkit-dev"
$roslynkitDev = Join-Path $roslynkitDev ($(if ($IsWindows) { "roslynkit.exe" } else { "roslynkit" }))
& $roslynkitDev <command> --target <solution.slnx|solution.sln|project.csproj|tsconfig.json|jsconfig.json>
```

Always pass `--target` explicitly. RoslynKit does not infer a target path automatically.
When a command accepts `--file`, use a source extension supported by the selected backend.
Use `& $roslynkitDev help <command>` for exact runtime syntax and options. In the RoslynKit repository, [.agents/skills/roslynkit/references/commands.md](../roslynkit/references/commands.md) is the generated checked-in command reference.

The dev tool install and update workflow is documented separately in [docs/dev-install.md](../../../docs/dev-install.md).

## When To Start With `workspace`

Run `workspace` first when any of these are true:

- a generated document path is needed;
- the same file may appear in multiple target-framework or project contexts;
- additional files or analyzer config documents need inspection.

Example:

```powershell
$roslynkitDev = Join-Path (Join-Path (Join-Path $HOME ".roslynkit") "tools") "roslynkit-dev"
$roslynkitDev = Join-Path $roslynkitDev ($(if ($IsWindows) { "roslynkit.exe" } else { "roslynkit" }))
& $roslynkitDev workspace --target .\SomeSolution.slnx --include-generated --include-additional --include-analyzer-config
```

If a document command reports multiple document contexts for the same path, retry with the concrete context from the error hint. Use `--project <path>` for linked files, `--tfm <framework>` for multi-targeted projects, and `--document-kind <source|sourceGenerated|additional|analyzerConfig>` only when the same path still maps to multiple document kinds.

## Cursor Choice

This section applies only to position mode (see Selector Choice).

When a line contains more than one semantic target, choose the cursor deliberately before jumping:

- Prefer the most flow-bearing symbol on the current line.
- If the current `.cs` file and cursor are already known, start with a position-based command on that location before reaching for `symbols`.
- For chained expressions such as `new SomeType(...).RunAsync(args)`, probe the rightmost invoked method or property first with `quick-info`, then use `definition` on that same position if the jump still looks useful.
- Treat the constructor token or enclosing type name as an opt-in target only when object construction or type identity is the actual question.
- If the question changes to "what is this type?", resolve the class with `symbols --exact --kind class` and then use `quick-info` at the class declaration before reading the file body.
- Keep semantic cursor coordinates strict for `definition`, `quick-info`, `signature-help`, `type-definition`, `references`, and `implementations`; do not overshoot line or column bounds or retry an out-of-range coordinate for those commands.

## Cheap-First Semantic Workflow

When the task is a supported-language semantic question, prefer this order and stop once enough evidence is available:

1. If the declaration name is known, run `definition`, `references`, or `implementations` with `--symbol`, or `symbol-source` for the declaration body, in one command.
2. If the current `.cs` file and cursor are already known, pick the most flow-bearing symbol on the current line and start with a position-based `definition`, `references`, `implementations`, or `quick-info` on that location. For chained invocations, start with the rightmost invoked method or property, not the constructor or enclosing type, unless construction is the question.
3. Use `symbols` only for discovery: fuzzy name search, kind-filtered listing, or checking whether a declaration exists. Do not use it to convert a known name into coordinates.
4. Use `quick-info` at the resolved position or location before reading source text when signature, type, or documentation context is needed.
5. If only a small source snippet is needed after semantic resolution, use `document-lines` with the smallest useful inclusive line range instead of pulling the whole document through RoslynKit.
6. Use `document-symbols` only when the file is already known and local structure is still needed to choose a member or range.
7. Use `document-text` only when a full document read is still justified after the symbol or file is already resolved.
8. Use `symbol-source` when exactly one declaration body is needed; read larger source regions only when symbol locations, quick info, document structure, and targeted cross-references are still insufficient.

## Documentation Hints

- Treat `documentation:` lines in RoslynKit output as routing hints, not as proof that a route is complete.
- In `references`, a header-level `documentation:` line describes the searched symbol; it does not describe each `- loc:` usage.
- In `symbols`, `document-symbols`, `definition`, `type-definition`, and `implementations`, an indented `documentation:` line describes the symbol bullet directly above it.
- Do not run `quick-info` just to retrieve documentation already present in the current command output.
- Use documentation to choose the next semantic hop, then verify with `definition`, `references`, `symbol-source`, or a narrow `document-lines` read.

## Token Discipline

- Do not read an entire source file through `document-text` by default.
- Do not read a whole class body by default.
- Chain follow-up lookups with the `id:` value from previous output when it is present instead of re-resolving the same symbol through `symbols` or a fresh position lookup.
- Prefer `symbol-source` over `document-text` or shell reads when exactly one declaration body is needed.
- Use `symbols` for name discovery inside the loaded target, not to convert a known name into coordinates and not to inspect external Roslyn APIs or other non-declared implementation details.
- Do not start with `document-symbols` unless the file is already known and local structure is still needed to choose a member or range.
- Prefer a `definition` hop from a known call site over broad declaration search when tracing control flow.
- Prefer `definition` plus `quick-info` over `document-symbols` or `document-text` when the next useful hop is already on the current line.
- After RoslynKit resolves the relevant file and position, use `document-lines` for the smallest useful source window instead of pulling the whole document through RoslynKit.
- Prefer `document-lines` over `symbol-source` when only a few lines around a call site, switch arm, option declaration, or assertion are needed.
- Keep `references` narrow with the smallest useful `--max-results` when the goal is routing or nearest-test discovery.
- Once enough evidence is available, stop instead of gathering extra member bodies or sibling declarations.

## Agent-Facing Operations

The following examples assume `$roslynkitDev` has already been set as shown above.

### Follow the active call

```powershell
& $roslynkitDev definition --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs --line 18 --column 27
& $roslynkitDev quick-info --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs --line 18 --column 27
```

### Find a named type

```powershell
& $roslynkitDev symbols --target .\SomeSolution.slnx --query SomeType --exact --kind class
```

### Inspect type context

```powershell
& $roslynkitDev quick-info --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs --line 11 --column 21
```

### List file members

```powershell
& $roslynkitDev document-symbols --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs
```

### Read a resolved document

```powershell
& $roslynkitDev document-text --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs
```

### Read resolved source lines

```powershell
& $roslynkitDev document-lines --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs --start-line 40 --end-line 52
```

### Jump to a definition

```powershell
& $roslynkitDev definition --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs --line 18 --column 27
```

### Find references by declared name

```powershell
& $roslynkitDev references --target .\SomeSolution.slnx --symbol SomeNamespace.SomeType.SomeMethod --max-results 3
```

### Find references from a usage site

```powershell
& $roslynkitDev references --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs --line 32 --column 17 --max-results 3
```

### Find implementations by declared name

```powershell
& $roslynkitDev implementations --target .\SomeSolution.slnx --symbol SomeNamespace.ISomeService --max-results 20
```

### Find implementations from a usage site

```powershell
& $roslynkitDev implementations --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs --line 12 --column 9 --max-results 20
```

### Read a declaration body

```powershell
& $roslynkitDev symbol-source --target .\SomeSolution.slnx --symbol "M:SomeNamespace.SomeType.SomeMethod(System.String)"
```

### Read quick info

```powershell
& $roslynkitDev quick-info --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs --line 18 --column 27
```

### Jump to a type definition

```powershell
& $roslynkitDev type-definition --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs --line 18 --column 13
```

### Inspect call-site signature help

```powershell
& $roslynkitDev signature-help --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs --line 24 --column 17
```

### Generated-document reads

Use the generated path surfaced by `workspace --include-generated`.

1. Run `workspace --include-generated`.
2. Copy the generated document `path`.
3. Read it with `document-text --file`; add `--document-kind sourceGenerated` if the same path is ambiguous.

```powershell
& $roslynkitDev document-text --target .\SomeProject.csproj --file .\obj\Debug\net10.0\Generated.g.cs --document-kind sourceGenerated
```

## Fallbacks

- Do not prescribe a specific fallback command in this skill.
- If the task is literal search, prose inspection, an unsupported file, or a RoslynKit workspace-load failure, state that RoslynKit is not the right tool for that step and let Codex CLI choose the terminal-native fallback for the current platform.
