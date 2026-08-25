# Benchmark

This opt-in benchmark compares two retrieval conditions for the same repository question: bounded raw-text excerpts and RoslynKit text-only search evidence. A Codex judge receives only controller-supplied retrieval text and reports an answer with JSON Lines (JSONL) token accounting. Retrieval happens outside the measured judge turn.

The public entrypoint is [scripts/benchmark.sh](../scripts/benchmark.sh), the Bash controller:

```bash
bash ./scripts/benchmark.sh [options]
```

The benchmark requires Bash. On Windows, run it from Windows Subsystem for Linux (WSL) or Git Bash, with `codex` and the intended credentials available in that environment. The controller inherits `CODEX_HOME`; WSL therefore uses its WSL-local Codex configuration, provider selection, and credentials.

Benchmark sessions can incur model costs. Start sessions only after an explicit user request. Start with a dry run, which validates options and the case catalog and prints planned work without building RoslynKit or starting a Codex session:

```bash
bash ./scripts/benchmark.sh --dry-run --trials 1 --case default
```

The benchmark reads ten case definitions from [tests/Integration/Benchmarking/cases.json](../tests/Integration/Benchmarking/cases.json). The catalog owns the default selector: six `isDefault: true` code-search cases run from simple to complex—`search-option-parsing`, `search-query-tokenization`, `text-only-workspace`, `search-corpus-building`, `search-result-ranking`, and `search-command-flow`. Four optional navigation and stress cases—`daemon-disconnect`, `workspace-generation`, `stale-search-index`, and `symbol-comments`—run only through `--case all` or their individual ID. Each case defines an ID, query, intent, and required evidence groups. Catalog validation rejects unknown or malformed properties and evidence paths that do not identify repository files.

## Options

- `--model <model>` and `--reasoning-effort <effort>` select the Codex judge configuration. The default model is the balanced `gpt-5.6-terra`.
- `--trials <count>` sets the number of paired trials per selected case.
- `--case <id|default|all>` selects one case, the six-case default code-search suite, or all ten cases; `--case-id` remains a compatibility alias.
- `--max-results <count>` bounds retrieval evidence.
- `--index-path <path>` selects the Git-ignored text-only search index.
- `--roslynkit-path <path>` selects a built RoslynKit apphost.
- `--dry-run` validates and prints planned work without starting a Codex session.
- `--resume-run-root <path>` continues only unfinished case, condition, and trial tuples from one prior run.
- `--report-run-root <path>` rebuilds reports from one prior run without starting a Codex session.
- `--clean` removes every entry under [artifacts/](../artifacts/) except [artifacts/.gitkeep](../artifacts/.gitkeep), without building the helper or starting a Codex session.
- `--help` prints the available options.

`--clean` is exclusive and permanently removes every entry under [artifacts/](../artifacts/), including hidden entries, except [artifacts/.gitkeep](../artifacts/.gitkeep). It refuses a symlinked artifacts root, but removes artifact symlinks without following their targets. Any generated build, index, test, report, or commit-context artifact must be recreated after cleanup.

## Controller boundary

The C# benchmark helper is the single source of truth for every option, default, and validation rule. The Bash controller does not parse measured-run options; it forwards user arguments verbatim to the helper and drives only the paid judge turn. This keeps the two layers from drifting: adding or changing a helper option requires no controller change. The helper has four operations:

- `prepare` parses and validates all options, selects the mode, and prints a control directive on standard output. For a measured run it validates the catalog, creates `run.json`, builds RoslynKit when required, refreshes the text-only index, and lists the scheduled sessions. For `--dry-run` and `--report-run-root` it does the requested work, writes human-readable text to standard error, and emits a non-run directive.
- `prepare-session` retrieves raw-text or RoslynKit evidence and writes the evidence and judge-prompt files for one scheduled session.
- `evaluate-session` parses the judge JSONL, validates evidence and usage, persists the session result, and updates the run state.
- `report` derives `runs.csv` and `summary.md` from the persisted run state.

The controller keeps only two build-free options of its own, `--clean` and `--help`, so cleanup and usage work even when the helper cannot build. It honors each only as the sole argument; any other combination is forwarded to the helper (or, for `--clean`, rejected as non-exclusive) so invalid modifiers such as `--help --bogus` or `--clean --help` are never silently treated as help or cleanup. It recognizes exactly those two tokens; the helper rejects them as unknown, so the two layers cannot silently disagree on their meaning.

### Control directive

`prepare` prints a line-based control directive on standard output; all human-readable output goes to standard error. This is the entire controller-facing contract, so it needs no JSON parser:

- `action=run|dry-run|report` selects controller behavior; an unknown action or line stops the controller.
- for `action=run`, `run-root`, `model`, `reasoning-effort`, and zero or more `session` lines carry everything the judge loop needs.

The controller passes `model` and `reasoning-effort` straight from the directive to `codex exec`, so resumed runs use their stored configuration rather than any re-typed options. The Bash regression suite exercises the directive against the real helper so any handoff drift fails in continuous integration.

The helper may launch RoslynKit directly for retrieval, but it does not launch Bash or `codex`. The controller is deliberately Bash → C# helper plus Bash → `codex exec`; there is no Python controller and no C# → Bash → Codex nesting.

When `--roslynkit-path` is omitted, the helper builds [src/RoslynKit/RoslynKit.csproj](../src/RoslynKit/RoslynKit.csproj) in Release configuration and invokes `artifacts/bin/RoslynKit/release/RoslynKit[.exe]` directly. It uses that apphost for `index --text-only` and `search --text-only --compact --balanced`; it does not use the workspace daemon or `dotnet run` for retrieval.

For every pending session, Bash unsets the host-injected `CODEX_THREAD_ID` context before calling `codex exec`. `CODEX_THREAD_ID` is not a documented public `codex exec` input, so clearing it prevents nested judges from becoming associated with the host thread. [`codex exec --ephemeral`](https://learn.chatgpt.com/docs/non-interactive-mode) is the documented session-isolation mechanism: it avoids persisted session rollout files. The invocation retains `CODEX_HOME` and passes `--json`, `--ephemeral`, `--ignore-rules`, `--sandbox read-only`, the selected `--model`, `--config model_reasoning_effort=...`, `--cd`, `--output-last-message`, and standard input (`-`). `--json` keeps standard output as JSONL while `--output-last-message` writes the final answer to its artifact. Bash redirects judge JSONL and standard error to the session artifacts, retains the exit code, then asks the helper to evaluate the session.

`--ignore-rules` skips user and project `execpolicy` `.rules` files; it does not establish that project instructions are ignored. The judge prompt prohibits tool calls and the evaluator rejects tool events. The read-only sandbox limits filesystem effects; it does not claim that tools are disabled. A session is rejected if it calls a tool, lacks exactly one terminal usage event, has an empty answer, exits unsuccessfully, or misses required evidence.

The raw-text condition tokenizes alphanumeric and underscore query terms of at least three characters. It ranks C# files independently in `src/RoslynKit` and `tests/RoslynKit.Tests` by distinct-term coverage, total occurrences, and path. Each scope contributes at most eight files; each file contributes at most eight matching anchors, three lines of surrounding context, and 300 characters per rendered line. Build-output and infrastructure directories are excluded. The RoslynKit condition uses one bounded compact balanced search over the same repository.

Condition order alternates by trial. A one-trial default run starts 12 judge turns: two conditions for each of six code-search cases. A three-trial default acceptance run starts 36 judge turns. A one-trial all-cases run starts 20 judge turns, and a three-trial all-cases run starts 60.

Input-token savings are `100 * (raw input - RoslynKit input) / raw input`. A pair is accepted only when both sessions are valid and correct and RoslynKit saves at least 20%. Every scheduled pair must meet that threshold; a passing median alone is insufficient.

## Artifacts and reports

Each new run writes one canonical typed document to `artifacts/benchmark/<timestamp>/run.json`. The C# helper derives `runs.csv` and `summary.md` from that document. Resume and report-only modes hydrate the same document, so historical benchmark artifacts produced by earlier Bash or Python implementations are intentionally unsupported.

Resume uses the cases and configuration stored in `run.json` and schedules only unfinished case, condition, and trial tuples. Report-only mode asks the helper to generate derived files from the persisted run state; it starts no Codex session and does not use the checked-in catalog.
