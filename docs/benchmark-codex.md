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
| `-KeepSnapshot` | Preserve the temporary clean-room snapshots for inspection; copied authentication is still deleted. |

For example, inspect a complete planned invocation without running a benchmark:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\benchmark-codex.ps1 -Model gpt-5.6-luna -ReasoningEffort high -Trials 1 -CaseId daemon-disconnect -DryRun
```

Use full parameter names in benchmark commands. Omit `-DryRun` only after an explicit request to execute the benchmark.

The runner always resolves the global `roslynkit` application from `PATH`. It does not use or accept the side-by-side `roslynkit-dev` installation.

A one-trial run with `-CaseId all` starts six measured Codex sessions: three cases across two conditions. Runtime therefore includes six independent investigations in addition to snapshot preparation and one short, unmeasured RoslynKit preflight.

## Clean-room contract

The runner creates separate sanitized, one-commit temporary snapshots for the two conditions. Sanitization removes repository instructions, skills, Atlas data, agent-maintenance guidance, benchmark files, and prior Git history. Both snapshots receive dependency restore outputs before timing starts. Only the RoslynKit snapshot receives a Git-ignored search index and a snapshot-local copy of the global RoslynKit executable and its package payload. Keeping the executable inside the read-only snapshot avoids external-path command-policy exceptions on Windows. No generated or user command rules are installed in either condition.

Before measured work begins, one isolated Codex preflight must run the snapshot-local RoslynKit version command successfully with the same sandbox, temporary home, authentication seed, and model-provider seed used by measured sessions. A failed preflight aborts before any measured session. During measurement, any declined command or nonzero command exit invalidates the current session, writes its evidence, and stops the benchmark before another session starts. This fail-fast behavior prevents a broken tool or command policy from consuming the rest of a full benchmark run.

The preparation daemon is stopped before timing, and the RoslynKit daemon is stopped after every Codex session so trials do not share an in-memory workspace. Expected SQLite write-ahead logging (WAL) and shared-memory sidecars for that prepared index are excluded from snapshot-mutation checks; tracked and other untracked changes still invalidate a run. Every condition and trial receives a separate temporary `CODEX_HOME`; it is seeded with `auth.json` when that file is available and an allowlisted configuration for the selected model provider when required. The provider seed contains only transport and model-compatibility fields; approval, sandbox, search, memory, skill, plugin, and other user settings are not copied. Token refreshes stay in the temporary authentication seed between trials and never update the workstation copy. Copied authentication and provider configuration are deleted even when `-KeepSnapshot` retains the repository snapshots. Memory and optional features are disabled, and each Codex session is ephemeral. The sessions use a read-only sandbox with approval set to `never`.

Accounting reads only Codex `stdout` JSON Lines (JSONL) events. The run does not use rollout files, prior sessions, or memory as a token source. Artifacts are written to `artifacts/codex-benchmark/<timestamp>/`.

Each completed artifact directory contains `runs.csv`, `summary.md`, controller-only `review.md`, per-run `answers/`, `commands/`, `events/`, and `stderr/` evidence, plus the unmeasured `preflight/` evidence. A preflight failure can leave only the preflight evidence.

## Review results

Compare total input tokens, uncached input tokens, and duration for each condition. Review the answer for every run manually before treating a token reduction as meaningful; a cheaper incomplete or incorrect answer is not a benchmark win.

The clean room has limits:

- Copying `auth.json`, allowlisted model-provider fields, and the global RoslynKit package into an ignored snapshot directory supplies connectivity and the intended tool only; it does not copy memory or session context.
- The runner does not claim operating-system-level read isolation.
- Any declined or nonzero command invalidates the affected run and stops the remaining benchmark sessions.
