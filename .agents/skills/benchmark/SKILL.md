---
name: benchmark
description: Run reproducible repository benchmarks through checked-in focused runners, inspect validity and correctness evidence, and compare measured conditions. Use when the user explicitly invokes $benchmark or asks to run, dry-run, design, or interpret a repository benchmark.
---

# Benchmark

Use this skill as the generic benchmark entry point. Keep benchmark-specific mechanics in focused runners and keep manual procedures outside agent-autoloaded documentation.

## Roles

The outer controller prepares and evaluates a benchmark. A measured Codex session investigates one supplied case. The two roles have different allowed context and must not be combined. The focused search-text benchmark is different: its measured LLM turns only judge controller-supplied retrieval text and do not read this skill or use tools.

Measured sessions must follow only [Measured-session behavior](#measured-session-behavior). In particular, a measured session must not launch this skill's runner, inspect the controller-only links below, or recursively start a benchmark.

## Outer controller workflow

1. Read the canonical procedure for the requested benchmark.
2. Dry-run the focused runner before a measured invocation.
3. Confirm that every behavior dimension scored by the private rubric is explicitly requested by the case prompt. The rubric may retain expected facts, files, and tests, but it must not introduce a hidden task requirement.
4. Confirm that the dry run uses the repository worktree directly, preserves the requested condition difference, and leaves paid measurement disabled.
5. Run a measured benchmark only when the user explicitly requests execution. A request to design, review, or add cases does not authorize a paid Codex run.
6. Inspect operational validity, apply the focused runner's correctness gate, and rebuild the report before comparing cost, tokens, or timing.
7. Treat estimated GPT-5.6 Sol cost per correct answer as the primary comparison. Report token-category changes, the exact command, artifact directory, content-manifest status, invalid runs, and request-level accounting limitations.

## Search-Text Token Benchmark: controller only

Read [docs/benchmark-search-text.md](../../../docs/benchmark-search-text.md), then run [scripts/benchmark-search-text.sh](../../../scripts/benchmark-search-text.sh). Cases and required production/test evidence groups live in [benchmarks/search-text-cases.json](../../../benchmarks/search-text-cases.json).

Dry-run without starting a measured model turn:

```bash
bash ./scripts/benchmark-search-text.sh --dry-run --trials 1 --case-id daemon-disconnect
```

For acceptance, pass the model and reasoning effort explicitly and use at least three trials across all cases:

```bash
bash ./scripts/benchmark-search-text.sh --model gpt-5.6-sol --reasoning-effort high --trials 3 --case-id all
```

The controller must invoke RoslynKit itself through one direct `dotnet run --project ./src/RoslynKit --no-build -- search` command with `--text-only --compact --balanced`. It supplies that output, or the bounded plain-text baseline, to a judge-only LLM turn. Any LLM tool call, missing terminal token accounting, empty answer, missing evidence group, or nonzero exit makes the run non-comparable. The benchmark passes only when every scheduled pair is comparable and saves at least 20% input tokens; a median above the threshold is insufficient.

If an outer execution channel is interrupted, use `--resume-run-root` with the original artifact root so completed sessions are retained and only missing case/condition/trial tuples run. Use `--report-run-root` to rebuild reports without starting model turns. Evidence belongs below `artifacts/search-text-benchmark/` and remains Git-ignored.

## Codex Search Benchmark: controller only

Read [docs/benchmark-codex.md](../../../docs/benchmark-codex.md), then run [scripts/benchmark-codex.sh](../../../scripts/benchmark-codex.sh). Case prompts and controller-only review criteria live in [benchmarks/codex-cases.json](../../../benchmarks/codex-cases.json); measured prompts and controller validation must keep that file forbidden to measured Codex sessions. The runner requires Bash and Python 3.10 or later; Git Bash is the supported Bash host on Windows.

Dry-run with complete GNU-style option names:

```bash
bash ./scripts/benchmark-codex.sh --dry-run --trials 1
```

For measured runs, pass the requested `--model` and `--reasoning-effort` explicitly. Keep the active host's `CODEX_HOME`, inherited environment, global `rg` and `roslynkit` versions, and executable locations stable for the entire comparison. `--roslynkit-path` may instead select an isolated executable named `roslynkit` or `roslynkit.exe`; the runner prepends its directory to child `PATH` and records its resolved path, version output, and SHA-256 hash in preflight evidence. Git Bash runs use Windows-local tools and configuration; Windows Subsystem for Linux (WSL), VS Code Remote–WSL, Linux, and macOS runs use their host-local `PATH` and `CODEX_HOME`. The runner intentionally uses `--dangerously-bypass-approvals-and-sandbox`, `shell_environment_policy.inherit="all"`, and `--cd <repository-root>` so host-global tools run directly against the repository worktree. It disables `unified_exec` for every child so supported hosts expose the same Bash shell-tool interface and tool-level timeout behavior. Its RoslynKit runtime arguments use `./RoslynKit.slnx` and the controller-supplied repository-local index path after entering that repository root. Run it only after explicit benchmark authorization in an environment where unsandboxed host access and full environment exposure are acceptable; Codex intends the bypass flag for externally sandboxed environments.

Prepare the one shared repository-local index once before timed sessions and reuse it for every case and trial. The default is `./artifacts/roslynkit.db`; `--index-path` may select another database file directly below `./artifacts/`. Preserve the runner's unmeasured structured preflight exactly: one Codex session invokes the controller's hidden probe mode through Bash; that child resolves `rg` and `roslynkit` from its inherited `PATH`, executes each version command separately, and writes `preflight/tool-probe.json` with the host kind, resolved paths, outputs, and individual exit codes. The controller requires exactly one successful child command event and validates the JSON artifact. Command `aggregated_output` and the model-written answer are never version evidence.

For the RoslynKit condition, confirm that the dry-run prompt requires the stable RoslynKit skill and its command and output references, a `timeout_ms` value of `120000` on every RoslynKit shell tool call, no more than eight RoslynKit invocations, and serial, bounded RoslynKit searches. Generated measured commands use Bash. When auditing command events, the controller normalizes Bash, `sh`, and `zsh` `-c` or `-lc` plus GNU `timeout` envelopes before checking required reads, RoslynKit invocation, ordering, overlap, and the invocation ceiling. PowerShell and `cmd` wrappers are noncompliant. After measurement, inspect `events/` and `commands/` for the controller-validated RoslynKit count. Treat excess or overlapping invocations and cold-start timeouts as invalid even when captured output looks complete; do not compare or retry them inside the same measured session. The controller records ordinary invalid sessions and continues the remaining scheduled sessions without retry. Preparation failures, preflight failures, and repository content changes still stop the controller because later measurements would not share a valid baseline.

Treat lower projected cost, token use, or elapsed time as meaningful only when both conditions produce correct answers in the same valid run. Record criterion results in the completed Bash-runner artifact root's `review-results.json`, then invoke [scripts/benchmark-codex.sh](../../../scripts/benchmark-codex.sh) with `--report-run-root` for that root to rebuild correctness-gated comparisons without starting Codex. Current `codex exec --json` usage is cumulative for the completed turn and does not expose underlying request sizes; keep the 272K request threshold and exact long-context cost marked unknown. Compare raw Codex with RoslynKit only within one host-local run; elapsed times from different hosts or from runs made before `unified_exec` was disabled are not comparable. Keep raw events and answers available for audit; do not expose controller-only review criteria in child prompts.

## Measured-session behavior

This section applies only to a Codex process launched by the benchmark controller for one case and condition.

1. As the first command, read this [benchmark skill](SKILL.md) by running exactly `bash -lc 'cat .agents/skills/benchmark/SKILL.md'` before source investigation. Do not follow the controller-only links or instructions above.
2. Do not launch [scripts/benchmark-codex.sh](../../../scripts/benchmark-codex.sh), dry-run a benchmark, create a benchmark, or inspect controller reports.
3. Do not read [AGENTS.md](../../../AGENTS.md), [.codex/atlas/repo-map.md](../../../.codex/atlas/repo-map.md), [docs/benchmark-codex.md](../../../docs/benchmark-codex.md), [benchmarks/codex-cases.json](../../../benchmarks/codex-cases.json), prior artifact directories, private review criteria, or any other skill. The sole exception is the RoslynKit material explicitly required for the `roslynkit` condition below.
4. For `raw-codex`, use ordinary Bash and text inspection only. Do not read [.agents/skills/roslynkit/SKILL.md](../roslynkit/SKILL.md), its references, or any RoslynKit development skill, and do not invoke `roslynkit`, `roslynkit-dev`, or `dotnet run` for RoslynKit.
5. For `roslynkit`, read [.agents/skills/roslynkit/SKILL.md](../roslynkit/SKILL.md), [.agents/skills/roslynkit/references/commands.md](../roslynkit/references/commands.md), and [.agents/skills/roslynkit/references/output.md](../roslynkit/references/output.md) before invoking the global `roslynkit` command by running exactly `bash -lc 'cat .agents/skills/roslynkit/SKILL.md; cat .agents/skills/roslynkit/references/commands.md; cat .agents/skills/roslynkit/references/output.md'`. Use `--target ./RoslynKit.slnx` and pass the controller-supplied repository-relative `--index-path` value to `search`.
6. Set `timeout_ms` to `120000` on every shell tool call that invokes RoslynKit. Run RoslynKit commands serially and begin intent discovery with one bounded search using `--max-results 10`. If it has no useful method or location, refine once at 10 results; only if that still leaves no reliable jump target, use one third and final search at 20 results, or 50 when the earlier rankings show many plausible near-ties. Never run a fourth search. Treat every emitted `id:` selector as opaque and copy it verbatim; pass it as one correctly quoted `--symbol` argument, or use the returned `loc:` with a bounded `document-lines` call instead of reconstructing the ID.
7. Do not edit files or change Git state. Do not run builds, restores, tests, web or network requests, browsers, or subagents. Treat a declined command or a nonzero exit code as a failed session.
8. Limit recursive and literal searches to explicit permitted source or test paths. Repository-root recursive searches are forbidden because they can inspect controller-only context.
9. Return concise source-and-test evidence for the supplied question. Do not inspect prior run answers, events, commands, reviews, or other artifacts.
