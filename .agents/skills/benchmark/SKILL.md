---
name: benchmark
description: Run reproducible repository benchmarks through checked-in focused runners, inspect validity and correctness evidence, and compare measured conditions. Use when the user explicitly invokes $benchmark or asks to run, dry-run, design, or interpret a repository benchmark.
---

# Benchmark

Use this skill as the generic benchmark entry point. Keep benchmark-specific mechanics in focused runners and keep manual procedures outside agent-autoloaded documentation.

## Roles

The outer controller prepares and evaluates a benchmark. A measured Codex session investigates one supplied case. The two roles have different allowed context and must not be combined.

Measured sessions must follow only [Measured-session behavior](#measured-session-behavior). In particular, a measured session must not launch this skill's runner, inspect the controller-only links below, or recursively start a benchmark.

## Outer controller workflow

1. Read the canonical procedure for the requested benchmark.
2. Dry-run the focused runner before a measured invocation.
3. Confirm that the dry run uses the repository worktree directly, preserves the requested condition difference, and leaves paid measurement disabled.
4. Run a measured benchmark only when the user explicitly requests execution. A request to design, review, or add cases does not authorize a paid Codex run.
5. Inspect validity flags and answer correctness before comparing token or timing results.
6. Report the exact command, artifact directory, content-manifest status, invalid runs, and the primary comparison.

## Codex Search Benchmark: controller only

Read [docs/benchmark-codex.md](../../../docs/benchmark-codex.md), then use [scripts/benchmark-codex.ps1](../../../scripts/benchmark-codex.ps1). Case prompts and controller-only review criteria live in [benchmarks/codex-cases.json](../../../benchmarks/codex-cases.json); measured prompts and controller validation must keep that file forbidden to measured Codex sessions.

Dry-run with full parameter names:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\benchmark-codex.ps1 -DryRun -Trials 1
```

For measured runs, pass the requested `-Model` and `-ReasoningEffort` explicitly. Keep the active workstation `CODEX_HOME`, inherited environment, global `rg` and `roslynkit` versions, and executable locations stable for the entire comparison. The runner intentionally uses `--dangerously-bypass-approvals-and-sandbox`, `shell_environment_policy.inherit="all"`, and `--cd <repository-root>` so workstation-global tools run directly against the repository worktree. Run it only after explicit benchmark authorization in an environment where unsandboxed host access and full environment exposure are acceptable; Codex intends the bypass flag for externally sandboxed environments.

Prepare the one shared repository-local `artifacts/roslynkit.db` index once before timed sessions. Reuse that index for every case and trial. Preserve the runner's unmeasured preflight exactly: one Codex session runs `rg --version; roslynkit --version`, and the controller validates the recorded output and exit status before measurement starts.

For the RoslynKit condition, confirm that the dry-run prompt requires the stable RoslynKit skill and its command and output references, followed by serial, bounded RoslynKit searches. After measurement, inspect `events/` and `commands/` for the controller-validated RoslynKit invocation. Treat command overlap or a cold-start timeout as an invalid run; do not compare or retry it inside the same measured session.

Treat lower token use or elapsed time as meaningful only when both conditions produce correct answers and the run is valid. Keep raw events and answers available for audit; do not expose controller-only review criteria in child prompts.

## Measured-session behavior

This section applies only to a Codex process launched by the benchmark controller for one case and condition.

1. As the first command, read this [benchmark skill](SKILL.md) with `Get-Content -Raw` before source investigation. Do not follow the controller-only links or instructions above.
2. Do not launch [scripts/benchmark-codex.ps1](../../../scripts/benchmark-codex.ps1), dry-run a benchmark, create a benchmark, or inspect controller reports.
3. Do not read [AGENTS.md](../../../AGENTS.md), [.codex/atlas/repo-map.md](../../../.codex/atlas/repo-map.md), [docs/benchmark-codex.md](../../../docs/benchmark-codex.md), [benchmarks/codex-cases.json](../../../benchmarks/codex-cases.json), prior artifact directories, private review criteria, or any other skill. The sole exception is the RoslynKit material explicitly required for the `roslynkit` condition below.
4. For `raw-codex`, use ordinary local shell and text inspection only. Do not read [.agents/skills/roslynkit/SKILL.md](../roslynkit/SKILL.md), its references, or any RoslynKit development skill, and do not invoke `roslynkit`, `roslynkit-dev`, or `dotnet run` for RoslynKit.
5. For `roslynkit`, read [.agents/skills/roslynkit/SKILL.md](../roslynkit/SKILL.md), [.agents/skills/roslynkit/references/commands.md](../roslynkit/references/commands.md), and [.agents/skills/roslynkit/references/output.md](../roslynkit/references/output.md) with `Get-Content -Raw` before invoking the global `roslynkit` command. Use `--target .\RoslynKit.slnx`; pass `--index-path .\artifacts\roslynkit.db` to `search`; run RoslynKit commands serially; and begin intent discovery with one bounded search of at most five results.
6. Do not edit files or change Git state. Do not run builds, restores, tests, web or network requests, browsers, or subagents. Treat a declined command or a nonzero exit code as a failed session.
7. Return concise source-and-test evidence for the supplied question. Do not inspect prior run answers, events, commands, reviews, or other artifacts.
