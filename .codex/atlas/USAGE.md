# Atlas Usage

Use Atlas before broad source reading.

```text
Use atlas-router to map this bug before reading source.

Use scout for bounded file discovery when the target files are unclear.

Use atlas-csharp-mapper or RoslynKit live queries after candidate files or symbols are known.

Use atlas-test-mapper after the source/domain scope is known.

Run .\.codex\atlas\scripts\route.ps1 "fix command execution bug" before opening source files.

Refresh deterministic Atlas indexes with .\.codex\atlas\scripts\rebuild-atlas.ps1.
```

- Markdown is for agent and human navigation.
- Feature cards are the hand-maintained routing layer for `route.ps1`.
- JSON indexes are file/project/test metadata for deterministic scripts, not semantic context.
- Atlas does not store repo-wide symbol inventories.
- Use RoslynKit live commands (`symbols`, `document-symbols`, `definition`, `references`, `implementations`, `quick-info`, `type-definition`, `signature-help`, `document-lines`, and `symbol-source`) for task-sized semantic hops.
- Do not full-read generated JSON as a substitute for RoslynKit semantic navigation.
- Raw source remains the source of truth.
- Keep the Atlas compact.
