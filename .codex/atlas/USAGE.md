# Atlas Usage

Use Atlas before broad source reading.

```text
Use atlas-router to map this bug before reading source.

Use scout for bounded file discovery when the target files are unclear.

Use atlas-csharp-mapper or RoslynKit live queries after candidate files or symbols are known.

Use atlas-test-mapper after the source/domain scope is known.

Run .\.codex\atlas\scripts\route.ps1 "fix command execution bug" before opening source files.
```

- Markdown is for agent and human navigation.
- Feature cards are the hand-maintained routing layer for `route.ps1`.
- `repo-map.md` owns durable architecture shape; `test-index.md` owns durable source-to-test routing.
- Atlas does not store current-state inventories for files, projects, tests, symbols, references, or source slices.
- Use `git ls-files`, `rg`, build/test output, and direct file inspection for current file, project, and test facts.
- Use RoslynKit live commands (`symbols`, `document-symbols`, `definition`, `references`, `implementations`, `quick-info`, `type-definition`, `signature-help`, `document-lines`, and `symbol-source`) for task-sized semantic hops.
- Use sparse XML navigation comments surfaced by RoslynKit `quick-info` as routing hints before opening broader source.
- Raw source remains the source of truth.
- Keep the Atlas compact.
