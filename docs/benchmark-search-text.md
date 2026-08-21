# Search-Text Token Benchmark

This focused benchmark measures whether RoslynKit can reduce the input tokens needed for an LLM to select relevant C# production and test declarations. It compares bounded plain-text search excerpts with one compact RoslynKit search result. The LLM is only the judge: retrieval happens before the measured turn, and a valid judge turn uses no tools.

This workflow is intended for constrained hosts such as ChatGPT Work sandboxes where the workspace daemon and `MSBuildWorkspace` build host cannot create local sockets. RoslynKit is invoked directly through `dotnet`, and `--text-only` constructs an in-process `AdhocWorkspace` instead of contacting the daemon or evaluating MSBuild.

## Product workflow

Prepare a repository-local, Git-ignored text-only partition:

```bash
dotnet run --project ./src/RoslynKit --no-build -- \
  index \
  --target ./RoslynKit.slnx \
  --index-path ./artifacts/roslynkit-text.db \
  --text-only
```

Retrieve the bounded evidence that an LLM should judge:

```bash
dotnet run --project ./src/RoslynKit --no-build -- \
  search \
  --target ./RoslynKit.slnx \
  --index-path ./artifacts/roslynkit-text.db \
  --query "daemon transport disconnect buffered response standalone retry" \
  --max-results 10 \
  --text-only \
  --compact \
  --balanced
```

The three workflow flags have separate responsibilities:

- `--text-only` scans repository C# files into a separate text-only index partition without daemon or MSBuild startup. It excludes `.git`, `.vs`, `artifacts`, `bin`, `obj`, `node_modules`, and `TestResults`. It cannot be combined with `--project`.
- `--compact` emits only the returned/total count, rank, kind, display name, repository-relative location, and bounded excerpt. It intentionally omits navigation IDs, index metadata, and excerpt provenance.
- `--balanced` over-fetches ranked matches and reserves half of the bounded result set for test paths when both production and test declarations match. Unused capacity is filled from the original ranking.

Use this mode when the immediate task is to judge ranked search evidence. Use the normal MSBuild-backed search output when the next step must chain an `id:` into semantic navigation.

## Run the paired benchmark

Dry-run one case without starting a model turn:

```bash
bash ./scripts/benchmark-search-text.sh \
  --dry-run \
  --trials 1 \
  --case-id daemon-disconnect
```

Run the acceptance benchmark:

```bash
bash ./scripts/benchmark-search-text.sh \
  --model gpt-5.6-sol \
  --reasoning-effort high \
  --trials 3 \
  --case-id all
```

The controller restores and builds only `src/RoslynKit/RoslynKit.csproj`, prepares one text-only index, and alternates condition order by trial. Every RoslynKit retrieval is exactly one direct `dotnet run ... search --text-only --compact --balanced` command executed by the controller. Judge turns receive the resulting text in their prompt and are told not to use tools.

The plain-text condition ranks C# files independently in `src/RoslynKit` and `tests/RoslynKit.Tests` by distinct query-term coverage, selects at most eight files per scope, and includes at most eight high-signal matching-line anchors per file with three lines of context. This is deliberately bounded, auditable text retrieval rather than an unconstrained source-reading agent.

The runner copies only the active Codex `auth.json` into a private temporary Codex home, ignores user configuration, disables optional features, and removes the temporary home at process exit. Model, reasoning effort, prompts, retrieval text, JSONL events, answers, stderr, token accounting, and paired reports are retained below:

```text
artifacts/search-text-benchmark/<timestamp>/
```

If the outer execution channel is interrupted, resume only missing sessions:

```bash
bash ./scripts/benchmark-search-text.sh \
  --model gpt-5.6-sol \
  --reasoning-effort high \
  --trials 3 \
  --case-id all \
  --resume-run-root ./artifacts/search-text-benchmark/<timestamp>
```

Use `--report-run-root` instead to rebuild reports without starting model turns.

## Acceptance and accounting

A pair is comparable only when both judge turns:

- exit successfully and contain one terminal `turn.completed.usage` event;
- use zero tools;
- return a non-empty answer; and
- cite every required production/test evidence group from [benchmarks/search-text-cases.json](../benchmarks/search-text-cases.json).

Input-token savings for a comparable pair are:

```text
100 * (raw input tokens - RoslynKit input tokens) / raw input tokens
```

The acceptance rule is strict: every scheduled pair must be comparable and save at least 20%. A favorable median cannot hide an invalid, incorrect, or sub-threshold pair. Token totals come only from each measured turn's terminal Codex JSONL usage event. Index preparation and controller retrieval are intentionally outside the measured LLM turn.

## Verified result

On 2026-08-22, GPT-5.6 Sol at high reasoning effort completed three trials across four cases:

| Metric | Result |
| --- | ---: |
| Correct, valid pairs | 12/12 |
| Correct judge turns | 24/24 |
| Judge tool calls | 0 |
| Minimum input-token savings | 49.42% |
| Median input-token savings | 51.99% |
| Maximum input-token savings | 54.90% |

The raw event evidence is intentionally Git-ignored. Re-run the command above after retrieval, prompt, model, or repository changes instead of treating this snapshot as permanent performance data.
