---
name: benchmark
description: Run reproducible repository benchmarks through checked-in focused runners, inspect validity and correctness evidence, and compare measured conditions. Use when the user explicitly invokes $benchmark or asks to run, dry-run, design, or interpret a repository benchmark.
---

# Benchmark

Use this skill as the generic benchmark entry point. Keep benchmark-specific mechanics in focused `scripts/benchmark-*.ps1` runners and keep manual procedures outside agent-autoloaded documentation.

## Workflow

1. Read the canonical procedure for the requested benchmark.
2. Dry-run the focused runner before a measured invocation.
3. Confirm that the dry-run preserves the benchmark's isolation and changes only the factor being compared.
4. Run a measured benchmark only when the user explicitly requests execution. A request to design, review, or add cases does not authorize a paid Codex run.
5. Inspect validity flags and answer correctness before comparing token or timing results.
6. Report the exact command, artifact directory, invalid runs, and the primary comparison.

## Codex Search Benchmark

Read [docs/benchmark-codex.md](../../../docs/benchmark-codex.md), then use [scripts/benchmark-codex.ps1](../../../scripts/benchmark-codex.ps1). Case prompts and controller-only review criteria live in [benchmarks/codex-cases.json](../../../benchmarks/codex-cases.json); never place that file in the repository snapshot visible to the measured Codex process.

Dry-run with full parameter names:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\benchmark-codex.ps1 -DryRun -Trials 1
```

For measured runs, pass the requested `-Model` and `-ReasoningEffort` explicitly. Keep the active workstation `CODEX_HOME`, inherited environment, global `rg` and `roslynkit` versions, and executable locations stable for the entire comparison. The runner intentionally uses `--dangerously-bypass-approvals-and-sandbox`, `shell_environment_policy.inherit="all"`, and `--cd <snapshot>` so workstation-global tools run without staged copies. Run it only after explicit benchmark authorization in an environment where unsandboxed host access and full environment exposure are acceptable; Codex intends the bypass flag for externally sandboxed environments. Do not weaken its sanitized snapshots, memory, skill, plugin, or ephemeral-session controls.

Treat lower token use or elapsed time as meaningful only when both conditions produce correct answers and the run is valid. Keep raw events and answers available for audit; do not expose controller-only review criteria in child prompts.
