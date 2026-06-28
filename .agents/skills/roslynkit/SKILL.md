---
name: roslynkit
description: Use the stable global RoslynKit tool first for ordinary C# semantic inspection; for literal search, prose, non-C# files, or RoslynKit workspace-load failures, let Codex CLI choose the terminal-native fallback for the current platform.
---

# RoslynKit

Use this skill for ordinary C# semantic inspection when the stable global `roslynkit` command is available.

## Routing Rule

- Use RoslynKit first for C# semantic inspection.
- For literal text search, comments or prose, non-C# files, or RoslynKit workspace-load failures, let Codex CLI choose the terminal-native fallback for the current platform.

Do not default to `Get-Content`, `Select-String`, or grep-style file reads for questions that are really about C# declarations, symbol structure, definitions, references, implementations, types, signatures, or generated documents.

## Default File Scope

RoslynKit is C#-only by default.

- For any RoslynKit command that accepts `--file`, always pass a path that ends in `.cs`.
- Treat `.cs` as the default and required file scope unless the user explicitly asks for generated-document inspection that uses `--document-key` instead of `--file`.
- Do not run RoslynKit with `--file` values that point to `.md`, `.json`, `.xml`, `.yml`, `.yaml`, `.props`, `.targets`, `.editorconfig`, `.sln`, `.slnx`, `.csproj`, or other non-C# files.
- If the task is prose inspection, comment wording, XML documentation wording, TODO scanning, or literal text matching, let Codex CLI choose the terminal-native fallback even when the text appears inside a `.cs` file.
- A `.cs` file may still be inspected with RoslynKit when the primary task is semantic C# analysis or source inspection. Returned ranges may include comments, but comment text is not itself a RoslynKit search target. Because `document-text` now returns whole documents only, prefer `quick-info`, `document-symbols`, and targeted cross-references first. Use `document-text` only when a full document read is justified after the symbol or file is already resolved.

## Command

Use the stable global command:

```powershell
roslynkit <command> --target <solution.slnx|solution.sln|project.csproj>
```

Always pass `--target` explicitly. RoslynKit does not infer a solution or project path for you.
When a command accepts `--file`, pass a `.cs` path by default.

## When To Start With `workspace`

Run `workspace` first when any of these are true:

- you need a generated document key;
- the same file may appear in multiple target-framework or project contexts;
- you need to inspect additional files or analyzer config documents.

Example:

```powershell
roslynkit workspace --target .\SomeSolution.slnx --include-generated --include-additional --include-analyzer-config
```

## Cursor Choice

When a line contains more than one semantic target, choose the cursor deliberately before you jump:

- Prefer the most flow-bearing symbol on the current line.
- For chained expressions such as `new SomeType(...).RunAsync(args)`, probe the rightmost invoked method or property first with `quick-info`, then use `definition` on that same position if the jump still looks useful.
- Treat the constructor token or enclosing type name as an opt-in target only when object construction or type identity is the actual question.
- If the question changes to "what is this type?", resolve the class with `symbols --exact --kind class` and then use `quick-info` at the class declaration before reading the file body.

## Cheap-First Semantic Workflow

When the task is a semantic C# question, prefer this order and stop as soon as you have enough evidence:

1. Pick the most flow-bearing symbol on the current line before you jump. For chained invocations, start with the rightmost invoked method or property, not the constructor or enclosing type, unless construction is the question.
2. Resolve the exact symbol or declaration first with `symbols`, `definition`, `references`, or `implementations`, depending on the question.
3. Use `quick-info` at the resolved location before reading source text when you need signature, type, or documentation context.
4. If source text is still necessary, use `document-text` only when a full document read is justified after the symbol or file is already resolved.
5. If only a small literal snippet or comment block is needed after semantic resolution, let Codex CLI choose the terminal-native fallback instead of pulling the whole document through RoslynKit.
6. Read a method or class body only when symbol locations, quick info, document structure, and targeted cross-references are still insufficient.

## Token Discipline

- Do not read an entire `.cs` file through `document-text` by default.
- Do not read a whole class body by default.
- Do not start with `document-symbols` unless you already know the file and need local structure to choose a member or range.
- Prefer exact `symbols --exact --kind <kind>` or position-based commands over broad pattern searches when the likely symbol is already known.
- Prefer `definition` plus `quick-info` over `document-symbols` or `document-text` when the next useful hop is already on the current line.
- Keep `references` narrow with the smallest useful `--max-results` when the goal is routing or nearest-test discovery.
- Once you have enough evidence, stop instead of gathering extra member bodies or sibling declarations.

## Agent-Facing Operations

### Follow the active call

```powershell
roslynkit quick-info --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs --line 18 --column 27
roslynkit definition --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs --line 18 --column 27
```

### Find a named type

```powershell
roslynkit symbols --target .\SomeSolution.slnx --query SomeType --exact --kind class
```

### Inspect type context

```powershell
roslynkit quick-info --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs --line 11 --column 21
```

### List file members

```powershell
roslynkit document-symbols --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs
```

### Read a resolved document

```powershell
roslynkit document-text --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs
```

### Jump to a definition

```powershell
roslynkit definition --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs --line 18 --column 27
```

### Find references

```powershell
roslynkit references --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs --line 32 --column 17 --max-results 3
```

### Find implementations

```powershell
roslynkit implementations --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs --line 12 --column 9 --max-results 20
```

### Read quick info

```powershell
roslynkit quick-info --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs --line 18 --column 27
```

### Jump to a type definition

```powershell
roslynkit type-definition --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs --line 18 --column 13
```

### Inspect call-site signature help

```powershell
roslynkit signature-help --target .\SomeSolution.slnx --file .\src\SomeProject\SomeFile.cs --line 24 --column 17
```

### Generated-document reads

This is the routine exception to the `.cs` `--file` default.

1. Run `workspace --include-generated`.
2. Copy the `documentKey` for the source-generated document.
3. Read it with `document-text --document-key`.

```powershell
roslynkit document-text --target .\SomeProject.csproj --document-key doc_ABC123
```

## Fallbacks

- Do not prescribe a specific fallback command in this skill.
- If the task is literal search, prose inspection, a non-C# file, or a RoslynKit workspace-load failure, state that RoslynKit is not the right tool for that step and let Codex CLI choose the terminal-native fallback for the current platform, such as PowerShell on Windows or the default shell on macOS and Linux.
