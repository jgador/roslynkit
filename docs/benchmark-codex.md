# Codex Token-Efficiency Benchmark

This opt-in token-efficiency benchmark compares a raw Codex investigation with a RoslynKit-guided investigation of the same repository question. Run it only when a user explicitly requests a benchmark; normal repository work must not start benchmark sessions.

The runner is [scripts/benchmark-codex.ps1](../scripts/benchmark-codex.ps1). The standalone, explicitly invoked workflow is [.agents/skills/benchmark/SKILL.md](../.agents/skills/benchmark/SKILL.md); it is not part of the `roslynkit init` bundle.

## Cases

Each case uses the exact user prompt below. The two arms, `raw-codex` and `roslynkit`, run each selected case independently.
Arm order alternates by trial: raw Codex first on odd trials and RoslynKit first on even trials.

### `daemon-disconnect`

```text
Find out what happens when the background workspace process disconnects after it starts returning a response. I want to know whether partial output can leak, when it runs locally instead, and which failures do not recover. Trace the source and tests. Don't just summarize docs or change files.
```

### `workspace-generation`

```text
Find out how the repo keeps code-navigation requests on a consistent compiler view when tracked files change while reads are still running. Explain what waits, when the old view is discarded, and what happens if files change again during reload. Show the source and tests. Don't change files.
```

### `stale-search-index`

```text
Why can an English-oriented code search return older results instead of waiting for an index update? Trace how it chooses fresh versus stale data and whether partial updates can become visible. Show the implementation and tests. Don't change files.
```

## Run

Use [scripts/benchmark-codex.ps1](../scripts/benchmark-codex.ps1) directly or invoke the explicit `$benchmark` skill. The parameters are:

| Parameter | Purpose |
| --- | --- |
| `-Model` | Codex model used for both arms. |
| `-ReasoningEffort` | Codex reasoning effort used for both arms. |
| `-Trials` | Number of trials for each selected case and arm. |
| `-CaseId` | One case ID, or `all` for every case. |
| `-RoslynKitPath` | RoslynKit executable used by the `roslynkit` arm. |
| `-DryRun` | Print the planned invocations without starting Codex. |
| `-KeepSnapshot` | Preserve the temporary clean-room snapshots for inspection; copied authentication is still deleted. |

For example, inspect a complete planned invocation without running a benchmark:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\benchmark-codex.ps1 -Model gpt-5.6-sol -ReasoningEffort high -Trials 1 -CaseId daemon-disconnect -RoslynKitPath roslynkit -DryRun
```

Use full parameter names in benchmark commands. Omit `-DryRun` only after an explicit request to execute the benchmark.

## Clean-room contract

The runner creates separate sanitized, one-commit temporary snapshots for the two arms. Sanitization removes repository instructions, skills, Atlas data, agent-maintenance guidance, benchmark files, and prior Git history. Both snapshots receive dependency restore outputs before timing starts. Only the RoslynKit snapshot receives a Git-ignored search index, so the raw Codex arm cannot discover that treatment data. The preparation daemon is stopped before timing, and the treatment daemon is stopped after every Codex session so trials do not share an in-memory workspace. Every arm and trial receives a separate temporary `CODEX_HOME`; it is seeded only with `auth.json` when that file is available. Token refreshes stay in the temporary authentication seed between trials and never update the workstation copy. Copied authentication is deleted even when `-KeepSnapshot` retains the repository snapshots. User configuration and rule files are ignored, memory and optional features are disabled, and each Codex session is ephemeral. The sessions use a read-only sandbox with approval set to `never`.

Accounting reads only Codex `stdout` JSON Lines (JSONL) events. The run does not use rollout files, prior sessions, or memory as a token source. Artifacts are written to `artifacts/codex-benchmark/<timestamp>/`.

Each artifact directory contains `runs.csv`, `summary.md`, controller-only `review.md`, and per-run `answers/`, `commands/`, `events/`, and `stderr/` evidence.

## Review results

Compare total input tokens, uncached input tokens, and duration for each arm. Review the answer for every run manually before treating a token reduction as meaningful; a cheaper incomplete or incorrect answer is not a benchmark win.

The clean room has limits:

- Copying `auth.json` supplies credentials only; it does not copy memory or session context.
- The runner does not claim operating-system-level read isolation.
- A command-policy violation invalidates the affected run.
