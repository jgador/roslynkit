# RoslynKit Command Reference

This reference lists command names, usage strings, and options exposed by the installed RoslynKit CLI. For emitted `id:` values and documentation-comment ID prefix meanings, see [references/output.md](output.md). Agent routing guidance remains in [SKILL.md](../SKILL.md).

## Commands

- `version`: Print the installed RoslynKit version.
- `init`: Scaffold the RoslynKit coding-agent skill bundle into the current Git repository.
- `workspace`: List projects and repository-relevant documents in the inferred repository or explicit target.
- `diagnostics`: Return source compiler diagnostics for the loaded target.
- `index`: Build or refresh the repository-local search and semantic catalog.
- `search`: Search the repository-local C# catalog using English-oriented text matching and ranking.
- `symbols`: Search source declarations by symbol name.
- `document-text`: Read the full text of one resolved document.
- `document-lines`: Read a bounded one-based line range from one resolved document.
- `document-symbols`: List declared symbols in one source or source-generated C# document.
- `definition`: Resolve a symbol selector or the symbol at a one-based line and column to source definitions.
- `type-definition`: Resolve the type of the symbol at a one-based line and column to source definitions.
- `references`: Find source references for a symbol selector or the symbol at a one-based line and column.
- `implementations`: Find implementations for a symbol selector or the symbol at a one-based line and column.
- `symbol-context`: Return the local syntax node, resolved symbol, ordinary comments, and bounded semantic context.
- `quick-info`: Return Roslyn quick info for the symbol at a one-based line and column.
- `signature-help`: Return Roslyn signature help for the position at a one-based line and column.
- `symbol-source`: Return the full declaration source text for a symbol selector.

## `version`

Print the installed RoslynKit version.

### Usage

```powershell
roslynkit version
roslynkit --version
```

### Options

No options.

## `init`

Scaffold the RoslynKit coding-agent skill bundle into the current Git repository.

### Usage

```powershell
roslynkit init [--agent <codex|claude|copilot|all>] [--overwrite]
```

### Options

- `--agent` `<agent>`: agent target: codex, claude, copilot, or all
- `--overwrite`: replace existing scaffolded skill files when content differs

## `workspace`

List projects and repository-relevant documents in the inferred repository or explicit target.

### Usage

```powershell
roslynkit workspace [--target <solution.slnx|solution.sln|solution.slnf|project.csproj|repository>] [--include-generated] [--include-additional] [--include-analyzer-config]
```

### Options

- `--target` / `-t` `<target>`: optional solution, project, or repository-directory scope; defaults to the nearest repository
- `--include-generated`: include source-generated and generated source documents
- `--include-additional`: include additional files
- `--include-analyzer-config`: include analyzer config documents such as .editorconfig

## `diagnostics`

Return source compiler diagnostics for the loaded target.

### Usage

```powershell
roslynkit diagnostics [--target <target>] [--max-results <n>] [--include-hidden] [--include-generated]
```

### Options

- `--target` / `-t` `<target>`: optional solution, project, or repository-directory scope; defaults to the nearest repository
- `--max-results` `<n>`: maximum results to return
- `--include-hidden`: include hidden diagnostics
- `--include-generated`: include diagnostics from generated, bin, and obj documents

## `index`

Build or refresh the repository-local search and semantic catalog.

### Usage

```powershell
roslynkit index [--target <target>] [--index-path <path>] [--rebuild] [--text-only]
```

### Options

- `--target` / `-t` `<target>`: optional solution, project, or repository-directory scope; defaults to the nearest repository
- `--index-path` `<path>`: optional SQLite database override; defaults to .roslynkit/roslynkit.db
- `--rebuild`: discard the selected partition before indexing
- `--text-only`: index repository C# source in-process without loading MSBuild

## `search`

Search the repository-local C# catalog using English-oriented text matching and ranking.

### Usage

```powershell
roslynkit search --query <text> [--target <target>] [--index-path <path>] [--project <path>] [--kind <kind>] [--max-results <n>] [--text-only] [--compact] [--balanced]
```

### Options

- `--target` / `-t` `<target>`: optional solution, project, or repository-directory scope; defaults to the nearest repository
- `--index-path` `<path>`: optional SQLite database override; defaults to .roslynkit/roslynkit.db
- `--query` / `-q` `<text>` (required): English-oriented text to search for
- `--project` `<path>`: limit search to one project file within the loaded target
- `--kind` `<kind>`: filter symbols by kind: namespace, type, member, method, property, field, event, class, interface, struct, enum, delegate
- `--max-results` `<n>`: maximum results to return (default: 20)
- `--text-only`: search repository C# source in-process without loading MSBuild
- `--compact`: emit concise ranked evidence with repository-relative locations
- `--balanced`: reserve half of bounded results for focused test declarations when both source and tests match

## `symbols`

Search source declarations by symbol name.

### Usage

```powershell
roslynkit symbols --query <text> [--target <target>] [--max-results <n>] [--case-sensitive] [--exact] [--kind <kind>]
```

### Options

- `--target` / `-t` `<target>`: optional solution, project, or repository-directory scope; defaults to the nearest repository
- `--query` / `-q` `<text>` (required): symbol name text to search for
- `--max-results` `<n>`: maximum results to return
- `--case-sensitive`: match query text case-sensitively
- `--exact`: match the declaration name exactly
- `--kind` `<kind>`: filter symbols by kind: namespace, type, member, method, property, field, event, class, interface, struct, enum, delegate

## `document-text`

Read the full text of one resolved document.

### Usage

```powershell
roslynkit document-text --file <path> [--target <target>] [--project <path>] [--tfm <framework>] [--document-kind <kind>]
```

### Options

- `--target` / `-t` `<target>`: optional solution, project, or repository-directory scope; defaults to the nearest repository
- `--file` / `-f` `<path>`: document file path in the loaded target
- `--project` `<path>`: owning project file path when a document path is ambiguous
- `--tfm` `<framework>`: target framework when a document path is ambiguous across project contexts
- `--document-kind` `<kind>`: document kind when a path maps to source, sourceGenerated, additional, or analyzerConfig

## `document-lines`

Read a bounded one-based line range from one resolved document.

### Usage

```powershell
roslynkit document-lines --file <path> --start-line <n> --end-line <n> [--target <target>] [--project <path>] [--tfm <framework>] [--document-kind <kind>]
```

### Options

- `--target` / `-t` `<target>`: optional solution, project, or repository-directory scope; defaults to the nearest repository
- `--file` / `-f` `<path>`: document file path in the loaded target
- `--project` `<path>`: owning project file path when a document path is ambiguous
- `--tfm` `<framework>`: target framework when a document path is ambiguous across project contexts
- `--document-kind` `<kind>`: document kind when a path maps to source, sourceGenerated, additional, or analyzerConfig
- `--start-line` `<n>` (required): one-based first document line
- `--end-line` `<n>` (required): one-based last document line

## `document-symbols`

List declared symbols in one source or source-generated C# document.

### Usage

```powershell
roslynkit document-symbols --file <path> [--target <target>] [--project <path>] [--tfm <framework>] [--document-kind <kind>]
```

### Options

- `--target` / `-t` `<target>`: optional solution, project, or repository-directory scope; defaults to the nearest repository
- `--file` / `-f` `<path>`: document file path in the loaded target
- `--project` `<path>`: owning project file path when a document path is ambiguous
- `--tfm` `<framework>`: target framework when a document path is ambiguous across project contexts
- `--document-kind` `<kind>`: document kind when a path maps to source, sourceGenerated, additional, or analyzerConfig

## `definition`

Resolve a symbol selector or the symbol at a one-based line and column to source definitions.

### Usage

```powershell
roslynkit definition --file <path> --line <n> --column <n> [--target <target>] [--project <path>] [--tfm <framework>] [--document-kind <kind>]
roslynkit definition --symbol <selector> [--target <target>]
```

### Options

- `--target` / `-t` `<target>`: optional solution, project, or repository-directory scope; defaults to the nearest repository
- `--file` / `-f` `<path>`: document file path in the loaded target
- `--project` `<path>`: owning project file path when a document path is ambiguous
- `--tfm` `<framework>`: target framework when a document path is ambiguous across project contexts
- `--document-kind` `<kind>`: document kind when a path maps to source, sourceGenerated, additional, or analyzerConfig
- `--line` `<n>`: one-based source line
- `--column` `<n>`: one-based source column
- `--symbol` `<selector>`: documentation-comment ID or qualified symbol name

## `type-definition`

Resolve the type of the symbol at a one-based line and column to source definitions.

### Usage

```powershell
roslynkit type-definition --file <path> --line <n> --column <n> [--target <target>] [--project <path>] [--tfm <framework>] [--document-kind <kind>]
```

### Options

- `--target` / `-t` `<target>`: optional solution, project, or repository-directory scope; defaults to the nearest repository
- `--file` / `-f` `<path>`: document file path in the loaded target
- `--project` `<path>`: owning project file path when a document path is ambiguous
- `--tfm` `<framework>`: target framework when a document path is ambiguous across project contexts
- `--document-kind` `<kind>`: document kind when a path maps to source, sourceGenerated, additional, or analyzerConfig
- `--line` `<n>` (required): one-based source line
- `--column` `<n>` (required): one-based source column

## `references`

Find source references for a symbol selector or the symbol at a one-based line and column.

### Usage

```powershell
roslynkit references --file <path> --line <n> --column <n> [--target <target>] [--project <path>] [--tfm <framework>] [--document-kind <kind>] [--max-results <n>]
roslynkit references --symbol <selector> [--target <target>] [--max-results <n>]
```

### Options

- `--target` / `-t` `<target>`: optional solution, project, or repository-directory scope; defaults to the nearest repository
- `--file` / `-f` `<path>`: document file path in the loaded target
- `--project` `<path>`: owning project file path when a document path is ambiguous
- `--tfm` `<framework>`: target framework when a document path is ambiguous across project contexts
- `--document-kind` `<kind>`: document kind when a path maps to source, sourceGenerated, additional, or analyzerConfig
- `--line` `<n>`: one-based source line
- `--column` `<n>`: one-based source column
- `--symbol` `<selector>`: documentation-comment ID or qualified symbol name
- `--max-results` `<n>`: maximum results to return

## `implementations`

Find implementations for a symbol selector or the symbol at a one-based line and column.

### Usage

```powershell
roslynkit implementations --file <path> --line <n> --column <n> [--target <target>] [--project <path>] [--tfm <framework>] [--document-kind <kind>] [--max-results <n>]
roslynkit implementations --symbol <selector> [--target <target>] [--max-results <n>]
```

### Options

- `--target` / `-t` `<target>`: optional solution, project, or repository-directory scope; defaults to the nearest repository
- `--file` / `-f` `<path>`: document file path in the loaded target
- `--project` `<path>`: owning project file path when a document path is ambiguous
- `--tfm` `<framework>`: target framework when a document path is ambiguous across project contexts
- `--document-kind` `<kind>`: document kind when a path maps to source, sourceGenerated, additional, or analyzerConfig
- `--line` `<n>`: one-based source line
- `--column` `<n>`: one-based source column
- `--symbol` `<selector>`: documentation-comment ID or qualified symbol name
- `--max-results` `<n>`: maximum results to return

## `symbol-context`

Return the local syntax node, resolved symbol, ordinary comments, and bounded semantic context.

### Usage

```powershell
roslynkit symbol-context --file <path> --line <n> --column <n> [--target <target>] [--project <path>] [--tfm <framework>] [--document-kind <kind>] [--max-results <n>] [--max-comments <n>]
roslynkit symbol-context --symbol <selector> [--target <target>] [--max-results <n>] [--max-comments <n>]
```

### Options

- `--target` / `-t` `<target>`: optional solution, project, or repository-directory scope; defaults to the nearest repository
- `--file` / `-f` `<path>`: document file path in the loaded target
- `--project` `<path>`: owning project file path when a document path is ambiguous
- `--tfm` `<framework>`: target framework when a document path is ambiguous across project contexts
- `--document-kind` `<kind>`: document kind when a path maps to source, sourceGenerated, additional, or analyzerConfig
- `--line` `<n>`: one-based source line
- `--column` `<n>`: one-based source column
- `--symbol` `<selector>`: documentation-comment ID or qualified symbol name
- `--max-results` `<n>`: maximum semantic descendants to return (default: 20)
- `--max-comments` `<n>`: maximum ordinary comments to return (default: 3)

## `quick-info`

Return Roslyn quick info for the symbol at a one-based line and column.

### Usage

```powershell
roslynkit quick-info --file <path> --line <n> --column <n> [--target <target>] [--project <path>] [--tfm <framework>] [--document-kind <kind>]
```

### Options

- `--target` / `-t` `<target>`: optional solution, project, or repository-directory scope; defaults to the nearest repository
- `--file` / `-f` `<path>`: document file path in the loaded target
- `--project` `<path>`: owning project file path when a document path is ambiguous
- `--tfm` `<framework>`: target framework when a document path is ambiguous across project contexts
- `--document-kind` `<kind>`: document kind when a path maps to source, sourceGenerated, additional, or analyzerConfig
- `--line` `<n>` (required): one-based source line
- `--column` `<n>` (required): one-based source column

## `signature-help`

Return Roslyn signature help for the position at a one-based line and column.

### Usage

```powershell
roslynkit signature-help --file <path> --line <n> --column <n> [--target <target>] [--project <path>] [--tfm <framework>] [--document-kind <kind>]
```

### Options

- `--target` / `-t` `<target>`: optional solution, project, or repository-directory scope; defaults to the nearest repository
- `--file` / `-f` `<path>`: document file path in the loaded target
- `--project` `<path>`: owning project file path when a document path is ambiguous
- `--tfm` `<framework>`: target framework when a document path is ambiguous across project contexts
- `--document-kind` `<kind>`: document kind when a path maps to source, sourceGenerated, additional, or analyzerConfig
- `--line` `<n>` (required): one-based source line
- `--column` `<n>` (required): one-based source column

## `symbol-source`

Return the full declaration source text for a symbol selector.

### Usage

```powershell
roslynkit symbol-source --symbol <selector> [--target <target>]
```

### Options

- `--target` / `-t` `<target>`: optional solution, project, or repository-directory scope; defaults to the nearest repository
- `--symbol` `<selector>` (required): documentation-comment ID or qualified symbol name
