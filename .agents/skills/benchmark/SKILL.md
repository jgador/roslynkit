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
3. Confirm that every behavior dimension scored by the private rubric is explicitly requested by the case prompt. The rubric may retain expected facts, files, and tests, but it must not introduce a hidden task requirement.
4. Confirm that the dry run uses the repository worktree directly, preserves the requested condition difference, and leaves paid measurement disabled.
5. Run a measured benchmark only when the user explicitly requests execution. A request to design, review, or add cases does not authorize a paid Codex run.
6. Inspect operational validity, complete the structured correctness review, and rebuild the report before comparing cost, tokens, or timing.
7. Treat estimated GPT-5.6 Sol cost per correct answer as the primary comparison. Report token-category changes, the exact command, artifact directory, content-manifest status, invalid runs, and request-level accounting limitations.

## Codex Search Benchmark: controller only

Read [docs/benchmark-codex.md](../../../docs/benchmark-codex.md), then use [scripts/benchmark-codex.ps1](../../../scripts/benchmark-codex.ps1). Case prompts and controller-only review criteria live in [benchmarks/codex-cases.json](../../../benchmarks/codex-cases.json); measured prompts and controller validation must keep that file forbidden to measured Codex sessions.

Dry-run with full parameter names:

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File ./scripts/benchmark-codex.ps1 -DryRun -Trials 1
```

For measured runs, pass the requested `-Model` and `-ReasoningEffort` explicitly. Keep the active host's `CODEX_HOME`, inherited environment, global `rg` and `roslynkit` versions, and executable locations stable for the entire comparison. Native Windows runs use Windows-local tools and configuration; native Windows Subsystem for Linux (WSL) and VS Code Remote–WSL runs use WSL-local tools, `PATH`, and `CODEX_HOME`. The runner intentionally uses `--dangerously-bypass-approvals-and-sandbox`, `shell_environment_policy.inherit="all"`, and `--cd <repository-root>` so host-global tools run directly against the repository worktree. It disables `unified_exec` for every child so Windows and WSL expose the same shell-tool interface and tool-level timeout behavior. Its RoslynKit runtime arguments use `./RoslynKit.slnx` and the controller-supplied repository-local index path after entering that repository root. Run it only after explicit benchmark authorization in an environment where unsandboxed host access and full environment exposure are acceptable; Codex intends the bypass flag for externally sandboxed environments.

Prepare the one shared repository-local index once before timed sessions and reuse it for every case and trial. The default is `./artifacts/roslynkit.db`; `-IndexPath` may select another database file directly below `./artifacts/`. Preserve the runner's unmeasured structured preflight exactly: one Codex session invokes the controller's hidden probe mode through `pwsh`; that child resolves `rg` and `roslynkit` from its inherited `PATH`, executes each version command separately, and writes `preflight/tool-probe.json` with the host kind, resolved paths, outputs, and individual exit codes. The controller requires exactly one successful child command event and validates the JSON artifact. Command `aggregated_output` and the model-written answer are never version evidence.

For the RoslynKit condition, confirm that the dry-run prompt requires the stable RoslynKit skill and its command and output references, a `timeout_ms` value of `120000` on every RoslynKit shell tool call, no more than eight RoslynKit invocations, and serial, bounded RoslynKit searches. The controller normalizes nested PowerShell, Bash/sh/zsh `-c` or `-lc`, Windows `cmd /c`, and GNU `timeout` envelopes before checking required reads, RoslynKit invocation, ordering, overlap, and the invocation ceiling. After measurement, inspect `events/` and `commands/` for the controller-validated RoslynKit count. Treat excess or overlapping invocations and cold-start timeouts as invalid even when captured output looks complete; do not compare or retry them inside the same measured session. The controller records ordinary invalid sessions and continues the remaining scheduled sessions without retry. Preparation failures, preflight failures, and repository content changes still stop the controller because later measurements would not share a valid baseline.

Treat lower projected cost, token use, or elapsed time as meaningful only when both conditions produce correct answers in the same valid run. Record criterion results in the run's `review-results.json`, then invoke [scripts/benchmark-codex.ps1](../../../scripts/benchmark-codex.ps1) with `-ReportRunRoot` to rebuild correctness-gated comparisons without starting Codex. Current `codex exec --json` usage is cumulative for the completed turn and does not expose underlying request sizes; keep the 272K request threshold and exact long-context cost marked unknown. Compare raw Codex with RoslynKit only within one host-local run; elapsed times from different hosts or from runs made before `unified_exec` was disabled are not comparable. Keep raw events and answers available for audit; do not expose controller-only review criteria in child prompts.

## Measured-session behavior

This section applies only to a Codex process launched by the benchmark controller for one case and condition.

1. As the first command, read this [benchmark skill](SKILL.md) by running exactly `pwsh -NoProfile -Command "Get-Content -Raw -LiteralPath '.agents/skills/benchmark/SKILL.md'"` before source investigation. The explicit `pwsh` wrapper is required because measured shell calls can use Bash on Windows Subsystem for Linux (WSL). Do not issue bare PowerShell cmdlets in measured-session shell commands; invoke them through `pwsh -NoProfile -Command`. Do not follow the controller-only links or instructions above.
2. Do not launch [scripts/benchmark-codex.ps1](../../../scripts/benchmark-codex.ps1), dry-run a benchmark, create a benchmark, or inspect controller reports.
3. Do not read [AGENTS.md](../../../AGENTS.md), [.codex/atlas/repo-map.md](../../../.codex/atlas/repo-map.md), [docs/benchmark-codex.md](../../../docs/benchmark-codex.md), [benchmarks/codex-cases.json](../../../benchmarks/codex-cases.json), prior artifact directories, private review criteria, or any other skill. The sole exception is the RoslynKit material explicitly required for the `roslynkit` condition below.
4. For `raw-codex`, use ordinary local shell and text inspection only. Do not read [.agents/skills/roslynkit/SKILL.md](../roslynkit/SKILL.md), its references, or any RoslynKit development skill, and do not invoke `roslynkit`, `roslynkit-dev`, or `dotnet run` for RoslynKit.
5. For `roslynkit`, read [.agents/skills/roslynkit/SKILL.md](../roslynkit/SKILL.md), [.agents/skills/roslynkit/references/commands.md](../roslynkit/references/commands.md), and [.agents/skills/roslynkit/references/output.md](../roslynkit/references/output.md) before invoking the global `roslynkit` command by running exactly `pwsh -NoProfile -Command "Get-Content -Raw -LiteralPath '.agents/skills/roslynkit/SKILL.md'; Get-Content -Raw -LiteralPath '.agents/skills/roslynkit/references/commands.md'; Get-Content -Raw -LiteralPath '.agents/skills/roslynkit/references/output.md'"`. Use `--target ./RoslynKit.slnx` and pass the controller-supplied repository-relative `--index-path` value to `search`.
6. Set `timeout_ms` to `120000` on every shell tool call that invokes RoslynKit. Run RoslynKit commands serially and begin intent discovery with one bounded search using `--max-results 10`. If it has no useful method or location, refine once at 10 results; only if that still leaves no reliable jump target, use one third and final search at 20 results, or 50 when the earlier rankings show many plausible near-ties. Never run a fourth search. Treat every emitted `id:` selector as opaque and copy it verbatim; for an ID containing PowerShell backticks, pass one single-quoted `--symbol` value or use the returned `loc:` with a bounded `document-lines` call instead of reconstructing the ID.
7. Do not edit files or change Git state. Do not run builds, restores, tests, web or network requests, browsers, or subagents. Treat a declined command or a nonzero exit code as a failed session.
8. Limit recursive and literal searches to explicit permitted source or test paths. Repository-root recursive searches are forbidden because they can inspect controller-only context.
9. Return concise source-and-test evidence for the supplied question. Do not inspect prior run answers, events, commands, reviews, or other artifacts.
