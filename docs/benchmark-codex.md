# Codex Token-Efficiency Benchmark

This opt-in token-efficiency benchmark compares a raw Codex investigation with a RoslynKit-guided investigation of the same repository question. Run it only when a user explicitly requests a benchmark; normal repository work must not start benchmark sessions.

The runner is [scripts/benchmark-codex.ps1](../scripts/benchmark-codex.ps1). The standalone, explicitly invoked workflow is [.agents/skills/benchmark/SKILL.md](../.agents/skills/benchmark/SKILL.md); it is not part of the `roslynkit init` bundle.

## Cases

Each case uses the exact user prompt below. The two conditions, `raw-codex` and `roslynkit`, run each selected case independently.
Condition order alternates by trial: raw Codex first on odd trials and RoslynKit first on even trials.

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
| `-Model` | Codex model used for both conditions; defaults to `gpt-5.6-luna`. |
| `-ReasoningEffort` | Codex reasoning effort used for both conditions. |
| `-Trials` | Number of trials for each selected case and condition. |
| `-CaseId` | One case ID, or `all` for every case. |
| `-DryRun` | Print the planned invocations without starting Codex. |
| `-KeepSnapshot` | Preserve the temporary clean-room snapshots for inspection; the workstation Codex home is never copied or deleted. |

For example, inspect a complete planned invocation without running a benchmark:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\benchmark-codex.ps1 -Model gpt-5.6-luna -ReasoningEffort high -Trials 1 -CaseId daemon-disconnect -DryRun
```

Use full parameter names in benchmark commands. Omit `-DryRun` only after an explicit request to execute the benchmark.

The runner resolves the global `roslynkit` and `rg` applications from `PATH`. It does not use or accept the side-by-side `roslynkit-dev` installation.

A one-trial run with `-CaseId all` starts six measured Codex sessions: three cases across two conditions. Runtime therefore includes six independent investigations in addition to snapshot preparation and one short, unmeasured tool preflight.

## Clean-room contract

The runner creates separate sanitized, one-commit temporary snapshots for the two conditions. Sanitization removes repository instructions, skills, Atlas data, agent-maintenance guidance, benchmark files, and prior Git history. Both snapshots receive dependency restore outputs and a snapshot-local copy of the resolved `rg` application before timing starts. The runner prepends that common tool directory to the child process environment so raw and RoslynKit sessions have the same text-search command available. Only the RoslynKit snapshot receives a Git-ignored search index and a snapshot-local copy of the global RoslynKit executable and its package payload. The RoslynKit condition invokes that staged executable as `roslynkit` through the temporary child `PATH`, matching how the staged `rg` executable is discovered without exposing snapshot-specific absolute paths. Keeping measured tools inside the isolated snapshots avoids external-path command-policy exceptions on Windows. The runner does not install command rules; existing rules from the active workstation Codex home remain in effect.

Before measured work begins, one isolated Codex preflight must resolve snapshot-local ripgrep and RoslynKit from the temporary child `PATH`, then run both version checks successfully with the same `workspace-write` sandbox and active workstation Codex configuration used by measured sessions. A failed preflight aborts before any measured session. During measurement, any declined command or nonzero command exit invalidates the current session, writes its evidence, and stops the benchmark before another session starts. This fail-fast behavior prevents a broken tool or command policy from consuming the rest of a full benchmark run.

The preparation daemon is stopped before timing, and the RoslynKit daemon is stopped after every Codex session so trials do not share an in-memory workspace. Expected SQLite write-ahead logging (WAL) and shared-memory sidecars for that prepared index are excluded from snapshot-mutation checks; tracked and other untracked changes still invalidate a run. Every condition and trial uses the active workstation `CODEX_HOME` directly, including its complete `config.toml`, selected model provider, command rules, and credential source. The runner does not copy, filter, or delete workstation authentication or configuration; Codex itself may update its normal state under that home. Environment variables referenced by the selected provider are inherited by the Codex process. Benchmark-specific command-line arguments still override the model, reasoning effort, approval, sandbox, memory, optional-feature, and session-persistence settings needed for a controlled comparison. Each session remains ephemeral and clears the parent thread identifier. The sessions use a `workspace-write` sandbox with approval set to `never` so inspection commands and the snapshot-local RoslynKit executable can run non-interactively. Prompts still prohibit source changes, and the controller invalidates any session that changes the snapshot beyond the expected search-index sidecars.

Accounting reads only Codex `stdout` JSON Lines (JSONL) events. The run does not use rollout files, prior sessions, or memory as a token source. Artifacts are written to `artifacts/codex-benchmark/<timestamp>/`.

Each completed artifact directory contains `runs.csv`, `summary.md`, controller-only `review.md`, per-run `answers/`, `commands/`, `events/`, and `stderr/` evidence, plus the unmeasured `preflight/` evidence. A preflight failure can leave only the preflight evidence.

## Review results

Compare total input tokens, uncached input tokens, and duration for each condition. Review the answer for every run manually before treating a token reduction as meaningful; a cheaper incomplete or incorrect answer is not a benchmark win.

The clean room has limits:

- The live workstation Codex home supplies its current authentication, complete configuration, and command rules. Results are comparable only while that external state and its referenced environment variables remain stable across all conditions and trials.
- The `workspace-write` sandbox permits changes inside each disposable snapshot; the controller detects unexpected snapshot changes and invalidates the session rather than claiming operating-system-level read isolation.
- Any declined or nonzero command invalidates the affected run and stops the remaining benchmark sessions.
