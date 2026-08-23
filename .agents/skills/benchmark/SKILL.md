---
name: benchmark
description: Run, inspect, analyze, resume, report, or clean the opt-in raw-text versus RoslynKit text-only token benchmark. Use only when the user explicitly invokes $benchmark or requests this benchmark.
---

# Benchmark

Treat the text after `$benchmark` as a forgiving command-like request. Accept obvious aliases, natural modifiers such as `3 trials` or `case daemon-disconnect`, and the controller's exact options. Do not silently ignore unknown or conflicting modifiers. A bare `$benchmark` behaves as `help`.

Read [docs/benchmark.md](../../../docs/benchmark.md) before measured or state-changing work. The C# benchmark helper is the single source of truth for every option, default, and validation rule; [scripts/benchmark.sh](../../../scripts/benchmark.sh) is the entrypoint that forwards those options and runs the judge:

```bash
bash ./scripts/benchmark.sh [options]
```

## Fast commands

- `help`, `?`: show these presets, current defaults from `--help`, current case IDs from [tests/Integration/Benchmarking/cases.json](../../../tests/Integration/Benchmarking/cases.json), identify the default code-search suite versus optional navigation and stress cases, and give a few examples. Start nothing.
- `cases`: list case IDs and their one-line intent, grouped as default code-search or optional navigation and stress cases. Start nothing.
- `doctor`: check Bash, .NET, Codex, controller syntax, and required files without starting a model session.
- `plan`, `dry`, `dry-run [preset or modifiers]`: run only the resolved `--dry-run` command. This builds the benchmark helper but does not build RoslynKit, create the index, or start a model session.
- `default`, `run`: use `gpt-5.6-terra`, reasoning effort `high`, one trial, `--case default`, ten results, and the default index. The default selector runs the six catalog-marked code-search cases, ordered from simple to complex: `search-option-parsing`, `search-query-tokenization`, `text-only-workspace`, `search-corpus-building`, `search-result-ranking`, and `search-command-flow`. Run the matching dry run and then the measured command.
- `smoke`, `quick [case]`: run one trial for one case. When no case is supplied, select the first and easiest default case, `search-option-parsing`, and state that choice. Run the matching dry run first.
- `case <id> [modifiers]`: run one trial for that case unless another trial count is supplied. Run the matching dry run first.
- `acceptance [modifiers]`: run `--case default` with three trials unless overridden. Run the matching dry run first.
- `resume <run-root|latest>`: inspect the stored configuration and resume only unfinished sessions. Resume does not support `--dry-run`.
- `report <run-root|latest>`: regenerate reports without starting a model session.
- `analyze <run-root|latest>`: inspect the persisted reports and session failures without starting another model session.
- `compare <run-root> <run-root>`: compare configuration, validity, correctness, pair acceptance, and token savings without starting model sessions.
- `status`, `history`: summarize available run roots and completion state without starting model sessions.
- `clean`, `reset`: run `bash ./scripts/benchmark.sh --clean`. This permanently removes every entry under [artifacts/](../../../artifacts/) except [artifacts/.gitkeep](../../../artifacts/.gitkeep), including unrelated and hidden artifacts. Either word is explicit deletion authorization, so list that exact scope and proceed without another confirmation.

`latest` must be resolved before the requested action: sort immediate `artifacts/benchmark/*/run.json` candidates by parent-directory name, newest first, and select the first run accepted by the current helper's report-only validation. Inspection commands use the same helper-validated set and derive completion from persisted session state. Never pass the word `latest` to the controller.

The catalog owns case selection: `--case default` selects its six `isDefault: true` code-search cases, while `--case all` also includes the four optional `daemon-disconnect`, `workspace-generation`, `stale-search-index`, and `symbol-comments` cases. Friendly modifiers map to `--model`, `--reasoning-effort`, `--trials`, `--case`, `--max-results`, `--index-path`, and `--roslynkit-path`. Accept at most one action preset and one case selector, and reject duplicate or conflicting presets, selectors, or values with a compact usage correction. Exact or friendly configuration modifiers without another preset behave as `run`. Print the resolved configuration and planned judge-turn count before a measured run, but do not request confirmation again.

`default`, `run`, `smoke`, `quick`, `case`, `acceptance`, `resume`, and configuration-only invocations explicitly authorize their required paid Codex sessions. Help, planning, inspection, reporting, cleanup, benchmark design, and benchmark modification do not authorize a paid session. If paid intent is unclear, show the closest safe command instead of guessing.

## Result analysis

The Bash controller launches the configured `codex exec` judge for each scheduled condition. Do not launch a separate judge. After a measured or resumed run, inspect `run.json`, `runs.csv`, `summary.md`, and relevant invalid-session artifacts, then return:

- the run root, resolved configuration, and completed judge-turn count;
- valid and correct session counts plus accepted-pair rate;
- raw-text versus RoslynKit input tokens, median and per-case savings, and the 20% threshold failures;
- invalid sessions, correctness failures, and notable outliers;
- a concise conclusion and the next most useful command.

The Bash controller forwards user options to the helper, schedules the judge loop from the helper's control directive, and makes the direct `codex exec` calls. It keeps only build-free `--clean` and `--help`, clears host-injected `CODEX_THREAD_ID`, uses `--ephemeral`, and retains `CODEX_HOME`. The C# helper owns option parsing, defaults, validation, catalog validation, retrieval, JSON Lines (JSONL) evaluation, persisted run state, and reports; it does not launch Codex. Judge prompts prohibit tool calls, and the evaluator rejects tool events; `--sandbox read-only` does not mean tools are disabled.
