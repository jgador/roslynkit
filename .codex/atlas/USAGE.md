# Atlas Usage

Use Atlas before broad source reading.

```text
Use atlas-router to map this bug before reading source.

Spawn atlas-csharp-mapper and atlas-test-mapper in parallel, then summarize read order.

Before editing, use the Atlas and read only exact symbol slices.

Run .\.codex\atlas\scripts\route.ps1 "fix command execution bug" before opening source files.

For repeated Atlas queries with the same lane, run:
dotnet run --project .\tests\RoslynKit.AtlasPromptCacheProbe

Override the default task or lane when you need to:
dotnet run --project .\tests\RoslynKit.AtlasPromptCacheProbe -- --task "fix command execution bug" --lane router

Refresh deterministic Atlas indexes with .\.codex\atlas\scripts\rebuild-atlas.ps1.
```

- Markdown is for agent and human navigation.
- Feature cards are the hand-maintained routing layer for `route.ps1`.
- JSON is for deterministic scripts.
- Prompt caching is for repeated Atlas API requests that reuse the same durable prefix. Keep the lane config, `repo-map.md`, `test-index.md`, and concise feature cards in that prefix, and put task-specific route output and selected index slices at the end.
- Check the probe utility output for `usage.cachedTokens` before assuming the cache is helping.
- Avoid prompt caching for one-off Atlas questions, tiny prompts, or payloads that stuff volatile index metadata or raw source into the reusable prefix.
- Raw source remains the source of truth.
- Keep the Atlas compact.
