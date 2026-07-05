# Codex Token Efficiency Benchmark

This benchmark measures whether RoslynKit's markdown CLI output is token efficient for Codex compared with a baseline that uses native shell/text inspection.

The benchmark uses actual Codex accounting. Each trial starts paired `codex --model gpt-5.5 --dangerously-bypass-approvals-and-sandbox exec --json` sessions and extracts the final cumulative `input_tokens` from the latest supported usage record in the Codex JSONL output or persisted rollout file: either `turn.completed.usage` or a `token_count` event.

## What It Compares

- Baseline arm: native repo inspection with `rg`, `Get-Content`, `Select-String`, and ordinary PowerShell only.
- RoslynKit arm: the side-by-side `roslynkit-dev` executable plus one required read of `.agents/skills/roslynkit-dev/SKILL.md` before C# inspection.

Both arms run under the same Codex full-access mode so MSBuild/Roslyn workspace loading can create temporary files if needed. The prompts still forbid repository edits. Both arms also forbid Codex memory, prior session artifacts, repo-local memory/cache tools, Atlas files/tools, subagents, and web search. Benchmark artifacts are written under ignored `artifacts/token-efficiency/<timestamp>/`.

## Run

Dry-run the planned invocations without starting Codex sessions:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\measure-codex-token-efficiency.ps1 -DryRun -Trials 1 -BenchmarkSet fixture
```

Run the default one-pair dogfood benchmark:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\measure-codex-token-efficiency.ps1
```

Run the complex navigation A/B explicitly:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\measure-codex-token-efficiency.ps1 -BenchmarkSet repo -CaseId repo-references-flow -Trials 1 -Model gpt-5.5
```

Use a specific model, RoslynKit executable, or dev skill file:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\measure-codex-token-efficiency.ps1 -Model gpt-5.4 -RoslynKitPath "$HOME\.roslynkit\tools\roslynkit-dev\roslynkit.exe"
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\measure-codex-token-efficiency.ps1 -RoslynKitSkillPath ".\.agents\skills\roslynkit-dev\SKILL.md"
```

## Benchmark Cases

- `fixture-symbol`: explain `FixtureApp.Consumer.Run` and identify the concrete `IMessageSource` implementation in `tests/FixtureWorkspace/App/App.csproj`.
- `repo-dispatch`: identify where `RoslynCommandExecutor.ExecuteAsync` dispatches `symbol-source`.
- `repo-references`: find callers or references of `RoslynKit.PositionResolver.GetPositionAsync`.
- `repo-references-flow`: trace `references --symbol RoslynKit.PositionResolver.GetPositionAsync` from command registration/parsing through execution, symbol/document resolution, markdown rendering, and relevant test coverage.

`repo-references-flow` also records `artifacts/token-efficiency/20260704-120341` as its fixed reference baseline: 642575 baseline input tokens and 93199 uncached input tokens. The same-run Savings table is still useful for judging run-to-run variance, but the Reference Baseline Targets table reports whether the RoslynKit arm beats that saved target.

Trial order alternates by pair: baseline then RoslynKit for odd trials, RoslynKit then baseline for even trials.

## Outputs

Each run writes:

- `runs.csv`: one row per case, arm, and trial.
- `summary.md`: medians, means, and savings percentages.
- `violations.md`: runs where Codex used a forbidden command for that arm or hit semantic command failures.
- `answers/*.md`: final Codex answers for correctness review.
- `commands/*.txt`: shell commands issued by Codex.
- `events/*.jsonl`: `codex exec --json` event stream.
- `stderr/*.txt`: Codex progress and diagnostics.

## Metrics

Primary comparison:

```text
roslynkit_savings_pct =
  100 * (baseline_median_input_tokens - roslynkit_median_input_tokens)
      / baseline_median_input_tokens
```

Also compare uncached input:

```text
uncached_input_tokens = input_tokens - cached_input_tokens
```

Treat a run as invalid when:

- `codex exec` exits non-zero.
- no token accounting is found.
- the baseline arm uses RoslynKit.
- either arm reads Codex memory or prior-session artifacts such as `.codex/memories`, `.codex/sessions`, `.codex/archived_sessions`, `history.jsonl`, `MEMORY.md`, `rollout_summaries`, or `rollout-*.jsonl`.
- either arm uses repo-local memory/cache tools or generated repo-local memory/cache directories.
- either arm uses Atlas files or tools such as `.codex/atlas`, `atlas-router`, `atlas-csharp-mapper`, or Atlas scripts.
- either arm uses subagents such as `scout`, `explorer`, or `worker`.
- the RoslynKit arm uses text/source inspection commands such as `rg`, `Get-Content`, or `Select-String`, except for the exact `.agents/skills/roslynkit-dev/SKILL.md` read required by the prompt.
- the RoslynKit arm does not issue a RoslynKit command.
- a RoslynKit semantic command exits non-zero or reports a workspace/remote invocation error.
- the final answer reports blocked or incomplete semantic evidence.

## Interpreting Results

Use medians first. Individual Codex runs can vary by planning choices, session context, and whether Codex chooses extra validation commands.

Check `answers/*.md` before trusting token savings. A lower-token run is not useful if it answered the wrong question or skipped necessary evidence.

If the RoslynKit arm wins on uncached input tokens but not total input tokens, the command output is likely compact, but prompt or cached context dominates the session. If it loses on both, inspect `commands/*.txt` and the RoslynKit output chosen by Codex; the prompt may need to steer toward `document-lines`, `references --symbol`, or smaller `--max-results` values. Use `symbol-source` only when a full declaration body is required.
