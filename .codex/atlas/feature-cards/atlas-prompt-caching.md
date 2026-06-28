# Atlas Prompt Caching

## Purpose

Reuse a stable Atlas prefix across repeated Responses API queries so repeated Atlas routing work is cheaper and faster without bloating the reusable prompt with volatile data.

## Task keywords

- atlas prompt caching
- prompt_cache_key
- prompt cache retention
- cached tokens
- repeated atlas queries
- responses api

## Entrypoints

- `tests/RoslynKit.AtlasPromptCacheProbe/Program.cs`
- `tests/RoslynKit.AtlasPromptCacheProbe/AtlasPromptProbe.cs`
- `.codex/atlas/USAGE.md`

## Important symbols

- `AtlasPromptProbeRunner.RunAsync`
- `AtlasPromptProbeRunner.BuildStablePrefix`
- `AtlasPromptProbeRunner.BuildDynamicSuffix`
- `AtlasPromptProbeRunner.LoadSelectedIndexes`
- `AtlasPromptProbeDefaults.GetPromptCacheKey`

## Important files

- `tests/RoslynKit.AtlasPromptCacheProbe/Program.cs`
- `tests/RoslynKit.AtlasPromptCacheProbe/AtlasPromptProbe.cs`
- `.codex/atlas/USAGE.md`
- `.codex/atlas/indexes/README.md`

## Nearest tests

- `tests/RoslynKit.Tests/AtlasPromptCacheProbeTests.cs`

## Build/test commands

- `dotnet test .\tests\RoslynKit.Tests\RoslynKit.Tests.csproj`
- `dotnet run --project .\tests\RoslynKit.AtlasPromptCacheProbe`

## Invariants

- The cached prefix is the lane config plus durable Atlas markdown, not volatile generated-index metadata or raw source dumps.
- `route.ps1` remains the deterministic route source for task-specific narrowing.
- Prompt-cache keys stay lane-stable unless the reusable prefix contract changes.

## Common pitfalls

- Putting `generatedAtUtc`, machine-local tool paths, or full source dumps into the reusable prefix.
- Treating selected index rows as source truth instead of routing hints.
- Using the probe for one-off Atlas questions where the repeated prefix is unlikely to matter.

## Do not read first

- `artifacts/`
- `TestResults/`
- full `.codex/atlas/indexes/symbol-index.json`

## Last verified

`2026-06-28`
