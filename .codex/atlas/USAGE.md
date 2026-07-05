# Atlas Usage

Use Atlas before broad source reading.

```text
Use atlas-router to map this bug before reading source.

Spawn atlas-csharp-mapper and atlas-test-mapper in parallel, then summarize read order.

Before editing, use the Atlas and read only exact symbol slices.

Run .\.codex\atlas\scripts\route.ps1 "fix command execution bug" before opening source files.

Refresh deterministic Atlas indexes with .\.codex\atlas\scripts\rebuild-atlas.ps1.
```

- Markdown is for agent and human navigation.
- Feature cards are the hand-maintained routing layer for `route.ps1`.
- JSON is for deterministic scripts.
- Raw source remains the source of truth.
- Keep the Atlas compact.
