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
- A `.cs` file may still be inspected with RoslynKit when the primary task is semantic C# analysis or source-range inspection. Returned ranges may include comments, but comment text is not itself a RoslynKit search target.

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
roslynkit workspace --target .\RoslynKit.slnx --include-generated --include-additional --include-analyzer-config
```

## Agent-Facing Operations

### Declaration lookup

```powershell
roslynkit symbols --target .\RoslynKit.slnx --query CliApplication --exact --kind class
```

### File structure

```powershell
roslynkit document-symbols --target .\RoslynKit.slnx --file .\src\RoslynKit\CliApplication.cs
```

### Method or class body reads

```powershell
roslynkit document-text --target .\RoslynKit.slnx --file .\src\RoslynKit\RoslynCommandExecutor.cs --start-line 26 --end-line 41
```

### Definition

```powershell
roslynkit definition --target .\RoslynKit.slnx --file .\src\RoslynKit\Program.cs --line 10 --column 20
```

### References

```powershell
roslynkit references --target .\RoslynKit.slnx --file .\src\RoslynKit\Program.cs --line 10 --column 20 --max-results 50
```

### Implementations

```powershell
roslynkit implementations --target .\SomeProject.csproj --file .\SomeFile.cs --line 12 --column 9 --max-results 20
```

### Quick info

```powershell
roslynkit quick-info --target .\RoslynKit.slnx --file .\src\RoslynKit\Program.cs --line 10 --column 20
```

### Type definition

```powershell
roslynkit type-definition --target .\SomeProject.csproj --file .\SomeFile.cs --line 18 --column 13
```

### Signature help

```powershell
roslynkit signature-help --target .\SomeProject.csproj --file .\SomeFile.cs --line 24 --column 17
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
