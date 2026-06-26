---
name: roslynkit-dev
description: Use the side-by-side prerelease RoslynKit dev tool first when working on RoslynKit itself; for literal search, prose, non-C# files, or RoslynKit workspace-load failures, let Codex CLI choose the terminal-native fallback for the current platform.
---

# RoslynKit Dev

Use this skill for RoslynKit development when semantic inspection should run against the side-by-side prerelease RoslynKit dev tool that is already installed outside the repo with `--tool-path`.

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

Resolve the installed dev command once, then invoke it directly:

```powershell
$roslynkitDev = Join-Path (Join-Path (Join-Path $HOME ".roslynkit") "tools") "roslynkit-dev"
$roslynkitDev = Join-Path $roslynkitDev ($(if ($IsWindows) { "roslynkit.exe" } else { "roslynkit" }))
& $roslynkitDev <command> --target <solution.slnx|solution.sln|project.csproj>
```

Always pass `--target` explicitly. RoslynKit does not infer a solution or project path for you.
When a command accepts `--file`, pass a `.cs` path by default.

The dev tool install and update workflow is documented separately in `docs/dev-install.md`.

## When To Start With `workspace`

Run `workspace` first when any of these are true:

- you need a generated document key;
- the same file may appear in multiple target-framework or project contexts;
- you need to inspect additional files or analyzer config documents.

Example:

```powershell
$roslynkitDev = Join-Path (Join-Path (Join-Path $HOME ".roslynkit") "tools") "roslynkit-dev"
$roslynkitDev = Join-Path $roslynkitDev ($(if ($IsWindows) { "roslynkit.exe" } else { "roslynkit" }))
& $roslynkitDev workspace --target .\RoslynKit.slnx --include-generated --include-additional --include-analyzer-config
```

## Agent-Facing Operations

The following examples assume `$roslynkitDev` has already been set as shown above.

### Declaration lookup

```powershell
& $roslynkitDev symbols --target .\RoslynKit.slnx --query CliApplication --exact --kind class
```

### File structure

```powershell
& $roslynkitDev document-symbols --target .\RoslynKit.slnx --file .\src\RoslynKit\CliApplication.cs
```

### Method or class body reads

```powershell
& $roslynkitDev document-text --target .\RoslynKit.slnx --file .\src\RoslynKit\RoslynCommandExecutor.cs --start-line 26 --end-line 41
```

### Definition

```powershell
& $roslynkitDev definition --target .\RoslynKit.slnx --file .\src\RoslynKit\Program.cs --line 10 --column 20
```

### References

```powershell
& $roslynkitDev references --target .\RoslynKit.slnx --file .\src\RoslynKit\Program.cs --line 10 --column 20 --max-results 50
```

### Implementations

```powershell
& $roslynkitDev implementations --target .\SomeProject.csproj --file .\SomeFile.cs --line 12 --column 9 --max-results 20
```

### Quick info

```powershell
& $roslynkitDev quick-info --target .\RoslynKit.slnx --file .\src\RoslynKit\Program.cs --line 10 --column 20
```

### Type definition

```powershell
& $roslynkitDev type-definition --target .\SomeProject.csproj --file .\SomeFile.cs --line 18 --column 13
```

### Signature help

```powershell
& $roslynkitDev signature-help --target .\SomeProject.csproj --file .\SomeFile.cs --line 24 --column 17
```

### Generated-document reads

This is the routine exception to the `.cs` `--file` default.

1. Run `workspace --include-generated`.
2. Copy the `documentKey` for the source-generated document.
3. Read it with `document-text --document-key`.

```powershell
& $roslynkitDev document-text --target .\SomeProject.csproj --document-key doc_ABC123
```

## Fallbacks

- Do not prescribe a specific fallback command in this skill.
- If the task is literal search, prose inspection, a non-C# file, or a RoslynKit workspace-load failure, state that RoslynKit is not the right tool for that step and let Codex CLI choose the terminal-native fallback for the current platform, such as PowerShell on Windows or the default shell on macOS and Linux.
