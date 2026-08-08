# Codex Token-Efficiency Benchmark

This opt-in token-efficiency benchmark compares a raw Codex investigation with a RoslynKit-guided investigation of the same repository question. Run it only when a user explicitly requests a benchmark; normal repository work must not start benchmark sessions.

The runner is [scripts/benchmark-codex.ps1](../scripts/benchmark-codex.ps1). The standalone, explicitly invoked workflow is [.agents/skills/benchmark/SKILL.md](../.agents/skills/benchmark/SKILL.md); it is not part of the `roslynkit init` bundle.

## Cases

Each case uses the exact user prompt below. The two conditions, `raw-codex` and `roslynkit`, run each selected case independently. Condition order alternates by trial: raw Codex first on odd trials and RoslynKit first on even trials.

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

For example, inspect a complete planned invocation without running a benchmark:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\benchmark-codex.ps1 -Model gpt-5.6-luna -ReasoningEffort high -Trials 1 -CaseId daemon-disconnect -DryRun
```

Use full parameter names in benchmark commands. Omit `-DryRun` only after an explicit request to execute the benchmark.

The runner resolves the global `roslynkit` and `rg` applications from the workstation `PATH`. It does not use or accept the side-by-side `roslynkit-dev` installation.

A one-trial run with `-CaseId all` starts six measured Codex sessions: three cases across two conditions. Runtime therefore includes six independent investigations, one shared search-index preparation, and one short, unmeasured tool preflight.

## Direct-worktree contract

Each child `codex exec` uses `--cd <repository-root>` and investigates the active repository worktree directly. Both conditions use that same root and resolve `rg` from the inherited workstation `PATH`; the RoslynKit condition also resolves the global `roslynkit` command from that `PATH`. The runner restores repository dependencies once, prepares one Git-ignored shared search index at `artifacts/roslynkit.db` before timed work, and reuses that index for every selected case and trial. The runner does not copy executable or package payload files and does not install command rules; existing rules from the active workstation Codex home remain in effect.

Before preflight, the controller captures a content manifest for the worktree. The manifest records the initial content state and therefore permits an initially dirty worktree. After preflight and every measured session, the controller compares tracked and non-ignored content with that manifest; a later content change records the affected session as invalid and stops the controller because subsequent measurements would not share the same baseline. Git-ignored files, including the shared index and benchmark evidence, are excluded from that comparison. Do not start a benchmark while other work is changing the repository.

Before measured work begins, one unmeasured Codex preflight runs exactly `rg --version; roslynkit --version` with the same full-access execution mode and active workstation Codex configuration used by measured sessions. The controller validates the recorded command output and exit status directly; wording in the model-written final response does not determine preflight validity. A failed preflight aborts before any measured session. During measurement, a declined command, nonzero command exit, compliance failure, missing answer, or missing accounting marks only the current session invalid. The controller writes its evidence and continues the remaining scheduled sessions without retry so other cases and conditions still produce evidence. Reports retain invalid rows and calculate comparisons only from valid rows.

Both measured conditions read [.agents/skills/benchmark/SKILL.md](../.agents/skills/benchmark/SKILL.md) with `Get-Content -Raw` as their first command, but only its measured-session section governs them. The `raw-codex` condition uses ordinary local shell and text inspection; it must not read the RoslynKit skill or invoke RoslynKit. The `roslynkit` condition must read [.agents/skills/roslynkit/SKILL.md](../.agents/skills/roslynkit/SKILL.md), [.agents/skills/roslynkit/references/commands.md](../.agents/skills/roslynkit/references/commands.md), and [.agents/skills/roslynkit/references/output.md](../.agents/skills/roslynkit/references/output.md) with `Get-Content -Raw` before calling the global `roslynkit` command. Measured sessions must not read [AGENTS.md](../AGENTS.md), [.codex/atlas/repo-map.md](../.codex/atlas/repo-map.md), [scripts/benchmark-codex.ps1](../scripts/benchmark-codex.ps1), this procedure, benchmark data, private review criteria, prior artifacts, or any other skill.

The RoslynKit condition starts intent discovery with one narrow `roslynkit search` query capped at five results, then refines only when necessary. RoslynKit commands must run serially: each invocation must finish before another starts, without concurrent tool calls, background jobs, or parallel pipelines. Independent cold commands can otherwise contend while loading the same solution even though every process receives the shared `artifacts/roslynkit.db` path. The controller recognizes direct and PowerShell-wrapped RoslynKit command events and invalidates observable overlapping invocations. A timeout remains a failed command and is not retried inside the measured session because a retry would change the timing condition.

The preparation daemon is stopped before timing, and the RoslynKit daemon is stopped after every Codex session so trials do not share an in-memory workspace. The shared index, its SQLite write-ahead logging (WAL) and shared-memory sidecars, and benchmark evidence are Git ignored and excluded from content-manifest checks; tracked and non-ignored content changes still invalidate a run. Every condition and trial uses the active workstation `CODEX_HOME` directly, including its complete `config.toml`, selected model provider, command rules, and credential source. The runner does not copy, filter, or delete workstation authentication or configuration; Codex itself may update its normal state under that home. Child commands use `shell_environment_policy.inherit="all"`, so the complete parent environment, including `PATH` and provider-related variables, is available to commands. Benchmark-specific command-line arguments still override the model, reasoning effort, memory, optional-feature, and session-persistence settings needed for a controlled comparison. Each session remains ephemeral and clears the parent thread identifier.

Every preflight and measured session uses `--dangerously-bypass-approvals-and-sandbox`. This is equivalent to approval `never` with unsandboxed command execution and is required here so the workstation-global executables can run directly. The Codex CLI labels this flag extremely dangerous and intends it only for externally sandboxed environments. Running this benchmark directly on a workstation accepts host-wide access as an explicit operator risk. Prompts prohibit source changes, and the content manifest detects later worktree-content changes beyond expected index files, but it cannot prevent or audit reads and writes elsewhere on the host.

Accounting reads only Codex `stdout` JSON Lines (JSONL) events. The run does not use rollout files, prior sessions, or memory as a token source. Persistent run evidence is written to `artifacts/codex-benchmark/<timestamp>/`.

Each completed artifact directory contains `runs.csv`, `summary.md`, controller-only `review.md`, per-run `answers/`, `commands/`, `events/`, and `stderr/` evidence, plus the unmeasured `preflight/` evidence. A preflight failure can leave only the preflight evidence.

## Review results

Compare total input tokens, uncached input tokens, and duration for each condition. Review the answer for every run manually before treating a token reduction as meaningful; a cheaper incomplete or incorrect answer is not a benchmark win.

The direct-worktree design has reduced isolation:

- The live workstation Codex home supplies its current authentication, complete configuration, and command rules. Results are comparable only while that external state and its referenced environment variables remain stable across all conditions and trials.
- Full environment inheritance exposes parent environment values to child commands. Run the benchmark only when those values and both global tool versions are appropriate to expose to an unsandboxed Codex session.
- The approval and sandbox bypass permits host-level access. The content manifest detects later repository-content changes, but provides no operating-system isolation or host-wide mutation audit.
- The measured sessions use the active worktree rather than a separate repository copy. Initial uncommitted work is allowed as a recorded baseline, but concurrent edits make results invalid and can expose worktree content to a full-access child process.
- Context restrictions are prompt and command-event audit controls, not filesystem isolation. Indirectly constructed reads can evade command-text auditing and must be considered during manual evidence review.
- Ordinary session failures are recorded as invalid and excluded from comparison; the remaining scheduled sessions continue without retry. Preparation failures, preflight failures, and repository content changes still stop the controller.
- RoslynKit command overlap invalidates the affected run. Review `events/` and `commands/` when a RoslynKit timeout occurs; a shared index path does not make concurrent solution loads one process or one workspace instance.
