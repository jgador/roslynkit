---
name: roslynkit-csharp
description: Use RoslynKit first for ordinary C# semantic inspection in repos where the repo-local wrapper is available; for literal search, prose, non-C# files, or RoslynKit workspace-load failures, let Codex CLI choose the terminal-native fallback for the current platform.
---

# RoslynKit CSharp

Use this skill for ordinary C# semantic inspection in a repo where RoslynKit is available.

## Routing Rule

- Use RoslynKit first for C# semantic inspection.
- For literal text search, comments or prose, non-C# files, or RoslynKit workspace-load failures, let Codex CLI choose the terminal-native fallback for the current platform.

Do not default to `Get-Content`, `Select-String`, or grep-style file reads for questions that are really about C# declarations, symbol structure, definitions, references, implementations, types, signatures, or generated documents.

## Wrapper

Use the repo-local wrapper:

```powershell
pwsh .\.agents\skills\roslynkit-csharp\scripts\invoke-roslynkit-csharp.ps1 <args>
```

When the skill lives inside the RoslynKit repo, the wrapper runs the local `src\RoslynKit\RoslynKit.csproj` so the skill follows the current checkout. In repos that only carry the skill files, it falls back to an installed `roslynkit` command.

Use `-Path` for file-backed operations. `pwsh` reserves `-File` for script invocation, so the wrapper exposes `-Path` and translates it to RoslynKit's `--file` option.

The wrapper resolves the nearest target from the current working tree in this order:

1. nearest `.slnx`
2. nearest `.sln`
3. nearest `.csproj`

It always passes `--target` explicitly.

## When To Start With `workspace`

Run `workspace` first when any of these are true:

- you need a generated document key;
- the same file may appear in multiple target-framework or project contexts;
- you need to inspect additional files or analyzer config documents.

Example:

```powershell
pwsh .\.agents\skills\roslynkit-csharp\scripts\invoke-roslynkit-csharp.ps1 `
  -Operation workspace `
  -IncludeGenerated `
  -IncludeAdditional `
  -IncludeAnalyzerConfig
```

## Agent-Facing Operations

### Declaration lookup

```powershell
pwsh .\.agents\skills\roslynkit-csharp\scripts\invoke-roslynkit-csharp.ps1 `
  -Operation declaration-lookup `
  -Query CliApplication `
  -Exact `
  -Kind class
```

### File structure

```powershell
pwsh .\.agents\skills\roslynkit-csharp\scripts\invoke-roslynkit-csharp.ps1 `
  -Operation file-structure `
  -Path .\src\RoslynKit\CliApplication.cs
```

### Method or class body reads

```powershell
pwsh .\.agents\skills\roslynkit-csharp\scripts\invoke-roslynkit-csharp.ps1 `
  -Operation body-read `
  -Path .\src\RoslynKit\RoslynCommandExecutor.cs `
  -StartLine 26 `
  -EndLine 41
```

### Definition

```powershell
pwsh .\.agents\skills\roslynkit-csharp\scripts\invoke-roslynkit-csharp.ps1 `
  -Operation definition `
  -Path .\src\RoslynKit\Program.cs `
  -Line 9 `
  -Column 20
```

### References

```powershell
pwsh .\.agents\skills\roslynkit-csharp\scripts\invoke-roslynkit-csharp.ps1 `
  -Operation references `
  -Path .\src\RoslynKit\Program.cs `
  -Line 9 `
  -Column 20 `
  -MaxResults 50
```

### Implementations

```powershell
pwsh .\.agents\skills\roslynkit-csharp\scripts\invoke-roslynkit-csharp.ps1 `
  -Operation implementations `
  -Path .\SomeFile.cs `
  -Line 12 `
  -Column 9 `
  -MaxResults 20
```

### Quick info

```powershell
pwsh .\.agents\skills\roslynkit-csharp\scripts\invoke-roslynkit-csharp.ps1 `
  -Operation quick-info `
  -Path .\src\RoslynKit\Program.cs `
  -Line 9 `
  -Column 20
```

### Type definition

```powershell
pwsh .\.agents\skills\roslynkit-csharp\scripts\invoke-roslynkit-csharp.ps1 `
  -Operation type-definition `
  -Path .\SomeFile.cs `
  -Line 18 `
  -Column 13
```

### Signature help

```powershell
pwsh .\.agents\skills\roslynkit-csharp\scripts\invoke-roslynkit-csharp.ps1 `
  -Operation signature-help `
  -Path .\SomeFile.cs `
  -Line 24 `
  -Column 17
```

### Generated-document reads

1. Run `workspace --include-generated`.
2. Copy the `documentKey` for the source-generated document.
3. Read it with `generated-document-read`.

```powershell
pwsh .\.agents\skills\roslynkit-csharp\scripts\invoke-roslynkit-csharp.ps1 `
  -Operation generated-document-read `
  -DocumentKey doc_ABC123
```

## Fallbacks

- Do not prescribe a specific fallback command in this skill.
- If the task is literal search, prose inspection, a non-C# file, or a RoslynKit workspace-load failure, state that RoslynKit is not the right tool for that step and let Codex CLI choose the terminal-native fallback for the current platform, such as PowerShell on Windows or the default shell on macOS and Linux.
