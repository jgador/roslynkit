# Markdown Output Format

This document describes a token-saving Markdown output format for RoslynKit. It does not change the current formatter behavior.

RoslynKit currently accepts `--format json`, `--format compact`, and `--format text`. The existing `--format text` mode stays deterministic, line-oriented plain text for coding agents. Markdown should be added as a separate format value, such as `--format markdown`, instead of changing `text`.

Markdown output should stay close to GitHub Flavored Markdown, but it should use only the smallest useful subset for coding-agent consumption.

## Goals

- Preserve RoslynKit's JSON-first contract for automation.
- Keep `--format text` as the lowest-token plain text view.
- Keep Markdown output compact enough for coding-agent context windows.
- Preserve exact source text in fenced code blocks.
- Keep output deterministic: stable ordering, stable labels, stable locations, and stable section order.

## Supported Markdown

Markdown output should support only these constructs:

- blank lines between logical sections;
- plain key-value lines, such as `command: symbols`;
- compact bullets for repeated items;
- inline code spans for paths, symbols, command names, options, IDs, and short code fragments;
- fenced code blocks for source text, JSON examples, and longer display text.

Do not use headings in CLI output. The command name should be a key-value line:

```markdown
command: symbols
query: `MyService`
returned: 2/2
```

Do not use bold, italic, strikethrough, tables, links, blockquotes, task lists, raw HTML, images, diagrams, alerts, emoji, or footnotes in CLI output.

## Location Format

Locations should always include a start line and end line. Add columns only when the command needs column precision.

Use these forms:

```text
path:startLine-endLine
path:startLine:startColumn-endLine:endColumn
```

Examples:

```text
src/MyApp/MyService.cs:8-24
src/MyApp/MyService.cs:8:1-24:2
src/MyApp/MyService.cs:8:14-8:23
```

For a point selector, use a range with matching start and end positions:

```text
src/MyApp/Program.cs:10:20-10:20
```

Line and column numbers are one-based. Prefer the line-only form for whole declarations, whole documents, and diagnostics that do not need an exact column. Use the column form for identifier spans, cursor selectors, references, quick-info ranges, and source excerpts where the exact span matters.

## Format Contract

On success, Markdown output should write a compact Markdown fragment to stdout. On failure, it should follow the `--format text` failure rule and write the minified JSON `errors` envelope to stdout with a non-zero exit code. This keeps failures machine-detectable and prevents scripts from parsing Markdown error prose.

Use this shape:

```markdown
command: <command>
target: `<target>`
selector: `<location-or-symbol>`

<payload>
```

For repeated items, use compact bullets:

```markdown
- kind: Class name: `MyApp.MyService` loc: `src/MyApp/MyService.cs:8:14-8:23` id: `T:MyApp.MyService`
- kind: Method name: `MyApp.MyService.Execute` loc: `src/MyApp/MyService.cs:12:17-12:24` id: `M:MyApp.MyService.Execute(System.String)`
```

For source text, use fenced code blocks. Choose a fence length that is longer than any backtick run inside the payload.

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

For non-C# text documents, use `text` as the fence info string.

## Command Shapes

### `workspace`

Render workspace metadata as key-value lines and documents as bullets.

```markdown
command: workspace
target: `MySolution.slnx`
documents: 1

- project: `MyApp` tfm: `net10.0` kind: source loc: `src/MyApp/Program.cs:1-42` key: `doc_abc123`
```

Workspace diagnostics should use bullets:

```markdown
workspace-diagnostics: 1
- severity: Warning message: `Project skipped because ...`
```

### `diagnostics`

Render diagnostics as bullets sorted by severity, path, range, and diagnostic ID.

```markdown
command: diagnostics
target: `MySolution.slnx`
diagnostics: 1

- severity: Error id: `CS1002` loc: `src/MyApp/Program.cs:12:24-12:24` message: `; expected`
```

### `symbols` And `document-symbols`

Render counts first, then one bullet per symbol. Keep `symbolId` visible because it is the stable value that chains into `--symbol`.

```markdown
command: symbols
target: `MySolution.slnx`
query: `MyService`
returned: 2/2
truncated: false

- kind: Class name: `MyApp.MyService` loc: `src/MyApp/MyService.cs:8:14-8:23` id: `T:MyApp.MyService`
```

### `definition`, `type-definition`, And `implementations`

Render the selector first, then one bullet per resolved symbol.

```markdown
command: definition
target: `MySolution.slnx`
selector: `src/MyApp/Program.cs:10:20-10:20`

- kind: Class name: `MyApp.MyService` loc: `src/MyApp/MyService.cs:8:14-8:23` id: `T:MyApp.MyService`
```

### `references`

Render the symbol, counts, and reference bullets.

```markdown
command: references
target: `MySolution.slnx`
symbol: `M:MyApp.MyService.Execute(System.String)`
returned: 25/31
truncated: true

- loc: `src/MyApp/Program.cs:42:17-42:23` in: `M:MyApp.Program.Main(System.String[])` text: `service.Execute(value)`
```

### `quick-info`

Render tags as a compact key-value line and longer display text in fences.

````markdown
command: quick-info
target: `MySolution.slnx`
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

Render the active signature and one bullet per parameter.

```markdown
command: signature-help
target: `MySolution.slnx`
selector: `src/MyApp/Program.cs:42:17-42:17`
active-signature: `MyService.Execute(string value)`
active-parameter: `value`

- param: `value` doc: `Input value for the operation.`
```

### `document-text` And `symbol-source`

Render each document or declaration as metadata plus a fenced code block. Do not indent, wrap, JSON-escape, or trim the source payload.

````markdown
command: symbol-source
target: `MySolution.slnx`
symbol: `T:MyApp.MyService`

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

## Escaping And Stability

- Preserve source text exactly inside fenced code blocks.
- Escape backticks in inline code spans only when needed to keep the code span valid.
- Avoid table-cell escaping entirely by not using tables.
- Prefer code spans over links for local paths because the CLI cannot know the GitHub repository URL.
- Do not emit relative Markdown links to source files unless RoslynKit has an explicit base path and link mode.
- Use `\n` as the logical line separator in tests, while accepting platform-specific writer behavior only at the process boundary.

## Testing Requirements

When Markdown output is implemented, add focused tests for:

- parser acceptance of the new format value;
- failure output remaining a minified JSON `errors` envelope;
- line-range and column-range location rendering;
- inline code span escaping for values containing backticks;
- fenced code block rendering for source text containing backticks;
- command-specific rendering for `symbols`, `references`, `quick-info`, `document-text`, and `symbol-source`;
- README and package documentation updates for the new public option.
