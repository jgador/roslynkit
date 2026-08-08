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
| `-KeepSnapshot` | Retain the temporary clean-room snapshots; defaults to true. Pass `-KeepSnapshot:$false` to delete their shared temporary root after the run. The workstation Codex home is never copied or deleted. |

For example, inspect a complete planned invocation without running a benchmark:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\benchmark-codex.ps1 -Model gpt-5.6-luna -ReasoningEffort high -Trials 1 -CaseId daemon-disconnect -DryRun
```

Use full parameter names in benchmark commands. Omit `-DryRun` only after an explicit request to execute the benchmark.

The runner resolves the global `roslynkit` and `rg` applications from the workstation `PATH`. It does not copy either executable into a benchmark snapshot, and it does not use or accept the side-by-side `roslynkit-dev` installation.

A one-trial run with `-CaseId all` starts six measured Codex sessions: three cases across two conditions. Runtime therefore includes six independent investigations in addition to snapshot preparation and one short, unmeasured tool preflight.

## Clean-room contract

The runner creates separate sanitized, one-commit temporary snapshots for the two conditions. Sanitization removes repository instructions, skills, Atlas data, agent-maintenance guidance, benchmark files, and prior Git history. Both snapshots receive dependency restore outputs before timing starts. Each child `codex exec` uses `--cd <snapshot>` so relative source paths and generated benchmark data stay rooted in that condition's snapshot. Only the RoslynKit snapshot receives a prepared, Git-ignored search index at `artifacts/roslynkit.db`, relative to that snapshot working root. Both conditions resolve `rg` from the inherited workstation `PATH`; the RoslynKit condition also resolves the global `roslynkit` command from that `PATH`. The runner does not copy executable or package payload files and does not install command rules; existing rules from the active workstation Codex home remain in effect.

Measured runs print the temporary root plus the raw and RoslynKit snapshot paths before preparation begins. Both snapshots are retained by default, and the runner prints their locations again during final cleanup even when preparation, preflight, or measurement fails. This supports inspection of the exact source tree, generated restore outputs, and RoslynKit database used by the run. Delete retained snapshots manually after inspection, or pass `-KeepSnapshot:$false` when automatic deletion of the managed temporary root is explicitly wanted. Dry runs create no snapshot directories and state that fact instead of reporting a real location.

Before measured work begins, one isolated Codex preflight must run global `rg --version` and `roslynkit --version` successfully with the same full-access execution mode and active workstation Codex configuration used by measured sessions. A failed preflight aborts before any measured session. During measurement, any declined command or nonzero command exit invalidates the current session, writes its evidence, and stops the benchmark before another session starts. This fail-fast behavior prevents a broken tool or command policy from consuming the rest of a full benchmark run.

The RoslynKit condition starts intent discovery with one narrow `roslynkit search` query capped at five results, then refines only when necessary. RoslynKit commands must run serially: each invocation must finish before another starts, without concurrent tool calls, background jobs, or parallel pipelines. Independent cold commands can otherwise contend while loading the same solution even though every process receives the same `artifacts/roslynkit.db` path. The controller recognizes direct and PowerShell-wrapped RoslynKit command events and invalidates observable overlapping invocations. A timeout remains a failed command and is not retried inside the measured session because a retry would change the timing condition.

The preparation daemon is stopped before timing, and the RoslynKit daemon is stopped after every Codex session so trials do not share an in-memory workspace. Expected SQLite write-ahead logging (WAL) and shared-memory sidecars for that prepared index are excluded from snapshot-mutation checks; tracked and other untracked changes still invalidate a run. Every condition and trial uses the active workstation `CODEX_HOME` directly, including its complete `config.toml`, selected model provider, command rules, and credential source. The runner does not copy, filter, or delete workstation authentication or configuration; Codex itself may update its normal state under that home. Child commands use `shell_environment_policy.inherit="all"`, so the complete parent environment, including `PATH` and provider-related variables, is available to commands. Benchmark-specific command-line arguments still override the model, reasoning effort, memory, optional-feature, and session-persistence settings needed for a controlled comparison. Each session remains ephemeral and clears the parent thread identifier.

Every preflight and measured session uses `--dangerously-bypass-approvals-and-sandbox`. This is equivalent to approval `never` with unsandboxed command execution and is required here so the workstation-global executables can run without being copied. The Codex CLI labels this flag extremely dangerous and intends it only for externally sandboxed environments. Running this benchmark directly on a workstation accepts host-wide access as an explicit operator risk. Prompts still prohibit source changes, and the controller invalidates changes inside the snapshot beyond expected search-index sidecars, but it cannot prevent or audit reads and writes elsewhere on the host.

Accounting reads only Codex `stdout` JSON Lines (JSONL) events. The run does not use rollout files, prior sessions, or memory as a token source. Persistent run evidence is written to `artifacts/codex-benchmark/<timestamp>/`; retained clean-room snapshots remain under the separately printed operating-system temporary directory.

Each completed artifact directory contains `runs.csv`, `summary.md`, controller-only `review.md`, per-run `answers/`, `commands/`, `events/`, and `stderr/` evidence, plus the unmeasured `preflight/` evidence. A preflight failure can leave only the preflight evidence.

## Review results

Compare total input tokens, uncached input tokens, and duration for each condition. Review the answer for every run manually before treating a token reduction as meaningful; a cheaper incomplete or incorrect answer is not a benchmark win.

The clean room has limits:

- The live workstation Codex home supplies its current authentication, complete configuration, and command rules. Results are comparable only while that external state and its referenced environment variables remain stable across all conditions and trials.
- Full environment inheritance exposes parent environment values to child commands. Run the benchmark only when those values and both global tool versions are appropriate to expose to an unsandboxed Codex session.
- The approval and sandbox bypass permits host-level access. The controller detects unexpected changes inside each disposable snapshot but provides no operating-system isolation or host-wide mutation audit.
- Any declined or nonzero command invalidates the affected run and stops the remaining benchmark sessions.
- RoslynKit command overlap invalidates the affected run. Review `events/` and `commands/` when a RoslynKit timeout occurs; a shared index path does not make concurrent solution loads one process or one workspace instance.
