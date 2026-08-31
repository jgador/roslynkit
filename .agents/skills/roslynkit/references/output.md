# Markdown Output Format

This document is the output contract for RoslynKit. Every command writes this token-saving markdown-flavored text to stdout; there is no JSON output.

The output stays close to GitHub Flavored Markdown, but it uses only the smallest useful subset for coding-agent consumption.

## Goals

- Keep output compact enough for coding-agent context windows.
- Preserve exact source text in fenced code blocks.
- Keep output deterministic: stable ordering, stable labels, stable locations, and stable section order.
- Keep failures machine-detectable through exit codes instead of output parsing.

## Supported Markdown

Output uses only these constructs:

- blank lines between logical sections;
- plain key-value lines, such as `command: symbols`;
- compact bullets for repeated items;
- inline code spans for paths, symbols, command names, options, IDs, and short code fragments;
- fenced code blocks for source text and longer display text.

Do not use headings in CLI output. The command name is a key-value line:

```markdown
command: symbols
query: `MyService`
returned: 2/2
truncated: false
```

Do not use bold, italic, strikethrough, tables, links, blockquotes, task lists, raw HTML, images, diagrams, alerts, emoji, or footnotes in CLI output.

## Location Format

Locations always render as one-based column ranges:

```text
path:startLine:startColumn-endLine:endColumn
```

Examples:

```text
src/MyApp/MyService.cs:8:14-8:23
src/MyApp/MyService.cs:8:1-24:2
```

For a point selector, the range uses matching start and end positions:

```text
src/MyApp/Program.cs:10:20-10:20
```

## Format Contract

On success, the command writes a compact markdown fragment to stdout and exits `0`. On failure, it writes a plain-text error to stdout and exits non-zero:

```text
error: usage
message: Missing required option '--query'.
```

All failures include `error:` and `message:`. Usage errors with a deterministic retry path may include a third `hint:` line:

```text
error: usage
message: Line 70 is outside the document range 1..13.
hint: Retry with --line between 1 and 13, or run document-lines to inspect valid source lines before choosing --line/--column.
```

Exit codes: `0` success, `2` usage error, `130` canceled, `1` any other failure. The `error:` value is `usage`, `canceled`, or the exception type name. A zero exit code means stdout is command output; a non-zero exit code means stdout is the plain-text error.

Success output starts with `command: <name>` followed by command-specific key-value lines, then a blank line before bullets or fences:

```markdown
command: <command>
selector: `<location-or-symbol>`

<payload>
```

For repeated items, output uses compact bullets:

```markdown
- kind: NamedType name: `MyApp.MyService` loc: `src/MyApp/MyService.cs:8:14-8:23` id: `T:MyApp.MyService`
  documentation: Runs application work for the current request.
- kind: Method name: `MyApp.MyService.Execute` loc: `src/MyApp/MyService.cs:12:17-12:24` id: `M:MyApp.MyService.Execute(System.String)`
```

`name:` carries the fully qualified display name, and `id:` carries the documentation-comment ID that chains into `--symbol` when Roslyn can provide one. For `symbols`, `document-symbols`, `definition`, `type-definition`, and `implementations`, a non-empty XML summary may render as an indented `documentation:` continuation line below the symbol bullet. `references` renders documentation once in the command header for the searched symbol. When a symbol has more than one declaration (partial types), extra `- decl:` bullets follow with one location each.

### Documentation-Comment ID Prefixes

This is the canonical prefix legend for RoslynKit `id:` values and `--symbol` selectors that use Roslyn documentation-comment IDs:

- `N:` namespace
- `T:` type, including class, struct, interface, enum, delegate, or record
- `M:` method-like member, including method, constructor, operator, or conversion; constructors use `#ctor`
- `P:` property or indexer
- `F:` field
- `E:` event

Treat the complete `id:` value as opaque when chaining it into `--symbol`; keep the prefix, containing type, member name, and signature suffix exactly as emitted.

For source text, output uses fenced code blocks with a fence longer than any backtick run inside the payload:

````markdown
loc: `src/MyApp/MyService.cs:8:1-24:2`
```csharp
public sealed class MyService
{
    public void Execute(string value)
    {
    }
}
```
````

For non-C# text documents, the fence info string is `text`.

When workspace loading produced diagnostics, every command appends them at the end:

```markdown
workspace-diagnostics: 1
- severity: Warning message: `Project skipped because ...`
```

## Command Shapes

### `workspace`

Workspace metadata renders as key-value lines with project and document bullets. Project summary bullets use the project display name. Document bullets use the owning project path, target framework when available, document kind, and document path. Paths under the loaded root render relative; paths outside the loaded root render absolute.

```markdown
command: workspace
documents: 1

- project: `MyApp` tfm: `net10.0` documents: 42
- project: `src/MyApp/MyApp.csproj` tfm: `net10.0` kind: source path: `src/MyApp/Program.cs`
```

### `init`

`init` scaffolds the embedded RoslynKit skill bundle into the current Git repository. The command requires a `.git` directory or file in the current directory. Existing files are preserved when content is identical, rejected when content differs, and replaced only when `--overwrite` is supplied.

```markdown
command: init
agent: `codex`
root: `C:\repo\MyApp`
files: 3

- created: `.agents/skills/roslynkit/SKILL.md` agent: `codex`
- unchanged: `.agents/skills/roslynkit/references/commands.md` agent: `codex`
- overwritten: `.agents/skills/roslynkit/references/output.md` agent: `codex`
```

Agent values map only the outer target folder. The scaffolded bundle-relative files remain the same:

- `codex` -> `.agents/skills/roslynkit/`
- `claude` -> `.claude/skills/roslynkit/`
- `copilot` -> `.github/skills/roslynkit/`

### `index`

`index` prepares or refreshes a partition in the repository-local SQLite Full-Text Search 5 (FTS5) database. Without `--target`, RoslynKit finds the nearest standard Git repository, discovers all tracked or unignored `.csproj` files, and stores the catalog at `.roslynkit/roslynkit.db`. An explicit `--target` narrows the scope to a solution, solution filter, project, or repository directory; an explicit `--index-path` is an advanced override that must be inside the repository and ignored by Git. One database can contain partitions for multiple scopes in that repository. SQLite persists project paths and declaration source paths relative to the repository, then reconstructs public locations from the resolved repository root.

`index` waits for a stable workspace before reporting success. `--rebuild` recreates the selected partition. The generated `.roslynkit/.gitignore` covers the database and its write-ahead logging (WAL) sidecars. Multi-targeted projects, missing physical project or non-generated source paths, external projects, and external linked non-generated source files are rejected. Generated source documents are skipped, including source-generated documents, generated paths below `bin` or `obj`, and sources with standard generated-code markers injected from extracted NuGet packages outside the worktree.

```markdown
command: index
scope: repository
repository: `C:\repo\MyApp`
index-path: `C:\repo\MyApp\.roslynkit\roslynkit.db`
index-state: fresh
symbols: 124
rebuilt: false
```

`symbols:` is the number of indexed declarations in the selected partition. An implicit repository scope renders `scope: repository` and `repository:`; an explicit scope renders `target:`. A successful explicit index reports `index-state: fresh`. `rebuilt:` is `true` when `--rebuild` recreated the selected partition and `false` when the command refreshed it normally.

### `search`

`search` finds C# declarations from an English-oriented query. It infers the repository scope and default catalog unless those values are explicitly overridden. It validates the selected partition and refreshes stale data automatically. The first request waits for indexing; when a prior coherent index exists, a concurrent refresh can return that data with `index-state: stale`.

```markdown
command: search
scope: repository
repository: `C:\repo\MyApp`
index-path: `C:\repo\MyApp\.roslynkit\roslynkit.db`
query: `where is configuration validated during startup`
index-state: fresh
returned: 2/2
truncated: false

- rank: 1 kind: method name: `MyApp.ConfigurationValidator.Validate` loc: `C:\repo\MyApp\src\MyApp\ConfigurationValidator.cs:24:5-36:6` id: `M:MyApp.ConfigurationValidator.Validate(MyApp.Configuration)`
  excerpt: `Validates configuration before startup.`
  excerpt-source: documentation
- rank: 2 kind: class name: `MyApp.ConfigurationValidator` loc: `C:\repo\MyApp\src\MyApp\ConfigurationValidator.cs:8:14-38:2` id: `T:MyApp.ConfigurationValidator`
```

Results are ordered by the internal Best Matching 25 (BM25) ranking but do not expose raw scores. `rank:` starts at one. `excerpt:` is optional and is a bounded source-derived excerpt with normalized whitespace; it is never generated or paraphrased. Whenever `excerpt:` is present, `excerpt-source:` immediately follows it and is one of `documentation`, `comment`, `signature`, or `body`. `id:` and `loc:` are navigation inputs for an agent-selected follow-up command, not a standard-input pipeline. Rank and excerpt provenance are routing metadata, so agents compare several excerpts, kinds, identities, and locations before choosing a next navigation target and verify the selected route with source evidence.

The same semantic partition stores exact symbol metadata, declaration spans, structured comments, project references, and containment, inheritance, interface implementation, and override relationships. A fresh catalog can answer exact `symbols`, symbol-based `definition`, `symbol-source`, and `implementations` without loading an MSBuild workspace. The first bounded exact `references` request runs Roslyn and stores the result for an identical later invocation.

With `--compact`, search emits a judgment-only shape with repository-relative locations:

```markdown
results: 2/17
- 1 method `MyApp.ConfigurationValidator.Validate` `src/MyApp/ConfigurationValidator.cs:24:5-36:6`
  `Validates configuration before startup.`
- 2 method `MyApp.Tests.ConfigurationValidatorTests.RejectsMissingEndpoint` `tests/MyApp.Tests/ConfigurationValidatorTests.cs:18:17-18:39`
```

Compact output omits the command header, target and index metadata, stale/fresh state, truncation flag, symbol IDs, and excerpt provenance. Use it when an LLM will judge bounded search evidence directly, not when the next command must chain through `id:`. `--balanced` changes only bounded selection: when both source and test paths match, half of the result capacity is reserved for tests and unused capacity is filled from the original ranking.

### `symbol-context`

`symbol-context` resolves one selector into a local syntax node and its semantic symbol. It accepts exactly one of an emitted documentation-comment ID or qualified symbol selector, or a source position. The header begins with `command:` and `selector:`, followed by the selected-node record and resolved symbol record. A syntax node is source structure, such as `InvocationExpression` or `MethodDeclaration`; a symbol is its compiler-resolved identity. The command re-resolves syntax from the selector for every invocation and does not expose persistent syntax-node IDs.

```markdown
command: symbol-context
selector: `M:MyApp.Validator.Validate(MyApp.Configuration)`
selected-node: kind: MethodDeclaration loc: `src/MyApp/Validator.cs:12:5-30:6` name: `MyApp.Validator.Validate` id: `M:MyApp.Validator.Validate(MyApp.Configuration)`
symbol: `M:MyApp.Validator.Validate(MyApp.Configuration)` name: `MyApp.Validator.Validate`
documentation: Validates configuration before startup.
alternate-declarations: 0
ancestors: 1
- kind: ClassDeclaration loc: `src/MyApp/Validator.cs:5:1-45:2` name: `MyApp.Validator` id: `T:MyApp.Validator`
descendants: 1/3
descendants-truncated: true
- relation: invocation depth: 2 kind: InvocationExpression loc: `src/MyApp/Validator.cs:20:9-20:34` target: `MyApp.Configuration.Normalize` target-id: `M:MyApp.Configuration.Normalize`
comments: 1/2
comments-truncated: true
- placement: leading style: line loc: `src/MyApp/Validator.cs:10:1-10:44` text: `Startup validation boundary.`
```

`selected-node:` has `kind:` and `loc:`, plus optional `name:` and `id:` when the syntax node resolves to a symbol. `symbol:` is the exact `id:` when Roslyn can create one, otherwise the display name; an ID-bearing symbol adds `name:` with its display name. Optional `documentation:` is the plain-text XML summary. `alternate-declarations:` and `ancestors:` always carry counts; alternate declarations are `- loc:` bullets, and ancestors are nearest-first `- kind: ... loc:` bullets with the same optional `name:` and `id:` fields.

Descendants cover declaration, invocation, construction, and member-reference syntax. `descendants:` and `comments:` each use `returned/total` counts and are immediately followed by their respective `*-truncated: true|false` line. Descendant bullets carry `relation:`, `depth:`, `kind:`, and `loc:`, with optional `target:` and `target-id:` when the node resolves to a navigable symbol. Comment bullets carry `placement:` (`leading`, `body`, or `trailing`), `style:` (`line` or `block`), `loc:`, and normalized `text:`. `--max-results` defaults to `25` descendants, and `--max-comments` defaults to `3` comments. An absent optional symbol ID, target ID, or documentation summary is omitted rather than rendered as an empty field.

The output is a deterministic navigation primitive, not an intent-answering planner. `id:`, `target-id:`, and `loc:` identify valid next RoslynKit selectors. Documentation and ordinary comments are routing hints, not proof; an external agent maintains intent, avoids repeated identities or locations, selects the next relationship, and stops only after focused source or test evidence satisfies the intent.

### `diagnostics`

Diagnostics render as bullets sorted deterministically. `loc:` is omitted for diagnostics without a source location.

```markdown
command: diagnostics
returned: 1/1
truncated: false

- severity: Error id: `CS1002` loc: `src/MyApp/Program.cs:12:24-12:24` message: `; expected`
```

### `symbols` And `document-symbols`

`symbols` renders counts first, then one bullet per symbol. `document-symbols` uses a `file:` line instead of `query:` and does not render counts.

```markdown
command: symbols
query: `MyService`
returned: 2/2
truncated: false

- kind: NamedType name: `MyApp.MyService` loc: `src/MyApp/MyService.cs:8:14-8:23` id: `T:MyApp.MyService`
  documentation: Runs application work for the current request.
```

### `definition`, `type-definition`, And `implementations`

The selector first, then one bullet per resolved symbol. `implementations` adds a `symbol:` line and counts.

```markdown
command: definition
selector: `src/MyApp/Program.cs:10:20-10:20`

- kind: NamedType name: `MyApp.MyService` loc: `src/MyApp/MyService.cs:8:14-8:23` id: `T:MyApp.MyService`
  documentation: Runs application work for the current request.
```

### `references`

The selector, the resolved symbol, optional documentation for the searched symbol, counts, and reference bullets. Implicit references carry `implicit: true`.

```markdown
command: references
selector: `M:MyApp.MyService.Execute(System.String)`
symbol: `M:MyApp.MyService.Execute(System.String)`
documentation: Executes the requested service operation.
returned: 25/31
truncated: true

- loc: `src/MyApp/Program.cs:42:17-42:23`
- loc: `src/MyApp/Startup.cs:18:9-18:15` implicit: true
```

### `quick-info`

Tags as a compact key-value line and longer display text in fences: the description section in a `csharp` fence, all remaining sections joined into one `text` fence.

````markdown
command: quick-info
selector: `src/MyApp/Program.cs:10:20-10:20`
range: `src/MyApp/Program.cs:10:17-10:25`
tags: `Class`, `Public`

description:
```csharp
class MyApp.MyService
```

documentation:
```text
Runs application work for the current request.
```
````

### `signature-help`

The active signature and parameter indices, then one bullet per signature label.

```markdown
command: signature-help
selector: `src/MyApp/Program.cs:42:17-42:17`
active-signature: 0
active-parameter: 1

- signature: `MyService.Execute(string value)`
```

### `document-text`, `document-lines`, And `symbol-source`

Each document or declaration renders as metadata plus a fenced code block. The source payload is not indented, wrapped, escaped, or trimmed. `document-text` adds `truncated: true` only when the read was truncated.

````markdown
command: symbol-source
symbol: `T:MyApp.MyService`

- kind: NamedType name: `MyApp.MyService` loc: `src/MyApp/MyService.cs:8:14-8:23` id: `T:MyApp.MyService`

loc: `src/MyApp/MyService.cs:8:1-24:2`
```csharp
public sealed class MyService
{
    public void Execute(string value)
    {
    }
}
```
````

`document-lines` reads from `--start-line` through the lesser of `--end-line` and EOF, then adds the actual returned line range before the fence:

````markdown
command: document-lines
path: `src/MyApp/MyService.cs`
range: `src/MyApp/MyService.cs:40:1-52:2`

```csharp
public void Execute(string value)
{
}
```
````

### `help` And `version`

`help` renders the command table in the same grammar; `help <command>` renders that command's description, usage lines, and option bullets. `version` prints a plain-text version line.

```markdown
command: symbols
description: Search source declarations by symbol name.
usage: `roslynkit symbols --query <text> [--target <target>] [--max-results <n>]`

- option: `--query` short: `-q` value: text required: true description: symbol name text to search for
```

## Escaping And Stability

- Preserve source text exactly inside fenced code blocks; fence length grows past the longest backtick run in the payload.
- Escape backticks in inline code spans only when needed to keep the code span valid.
- Avoid table-cell escaping entirely by not using tables.
- Prefer code spans over links for local paths because the CLI cannot know the GitHub repository URL.
- Do not emit relative Markdown links to source files.
- Treat line breaks as logical line separators; process output may use the host platform's newline behavior.
