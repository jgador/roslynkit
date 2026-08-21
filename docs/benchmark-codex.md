# Codex Token-Efficiency Benchmark

This opt-in cost-efficiency benchmark compares a raw Codex investigation with a RoslynKit-guided investigation of the same repository question. Run it only when a user explicitly requests a benchmark; normal repository work must not start benchmark sessions.

The runner is [scripts/benchmark-codex.sh](../scripts/benchmark-codex.sh). Internally, it uses a Python 3.10 or later standard-library helper for structured JSON and JSON Lines (JSONL) processing. The standalone, explicitly invoked workflow is [.agents/skills/benchmark/SKILL.md](../.agents/skills/benchmark/SKILL.md); it is not part of the `roslynkit init` bundle.

The primary outcome is estimated GPT-5.6 Sol cost per correct answer. Token counts explain the result, but a lower token total is not a win when the answer is incomplete or incorrect. Terra and Luna projections show what the measured token profile would cost at the lower-priced tiers; they do not predict how those models would navigate the task. Separate model runs are required for behavioral comparisons.

## Cases

Each case uses the exact user prompt below. The two conditions, `raw-codex` and `roslynkit`, run each selected case independently. Condition order alternates by trial: raw Codex first on odd trials and RoslynKit first on even trials.

Each prompt explicitly requests every behavior dimension used by its private correctness rubric. The rubric can name the expected implementation and focused tests, but it must not add a hidden task requirement that neither condition was asked to answer.

### `daemon-disconnect`

```text
Find out what happens when the background workspace process disconnects after it starts returning a response. Explain why partial stdout or stderr cannot leak, exactly when the command runs locally instead, and how cancellation, completed command failures, and unexpected exceptions differ from recoverable infrastructure failures. Trace the source and focused tests. Don't just summarize docs or change files.
```

### `workspace-generation`

```text
Find out how the repo keeps code-navigation requests on a consistent compiler view when tracked files change while reads are still running. Explain the maximum clean-reader capacity, pending-reload priority, what waits, when the old view is discarded, the exact quiet-period retry timing, and what happens if files change again during the retry load. Show the source and focused tests. Don't change files.
```

### `stale-search-index`

```text
Why can an English-oriented code search return older results instead of waiting for an index update? Contrast an absent or unusable partition with a coherent stale partition, then trace nonblocking writer acquisition, state recapture, fresh-versus-stale fingerprint reporting, and transaction visibility so it is clear whether partial updates can become visible. Show the implementation and focused tests. Don't change files.
```

### `symbol-context-search`

```text
Starting only from this intent, find the RoslynKit code that extracts ordinary C# comments associated with a C# declaration and classifies each comment as leading, body, or trailing. Use actual repository search and navigation; do not assume declaration names or source files in advance. Trace the evidence path from ranked search results through a navigable symbol identity into bounded symbol context, then follow relevant connected syntax nodes or helpers to definitive source evidence. Explain search-result identity and excerpt provenance, trivia collection, supported comment kinds, nested-declaration ownership, normalization, location de-duplication, placement classification, descendant bounds, truncation, and focused test coverage. Cite precise production source and tests. Do not change files or Git state, build, restore, run tests, optimize code, or propose implementation changes. Do not merely summarize documentation.
```

## Run

Use [scripts/benchmark-codex.sh](../scripts/benchmark-codex.sh) directly or invoke the explicit `$benchmark` skill. The parameters are:

| Parameter | Purpose |
| --- | --- |
| `--model` | Codex model used for both conditions; defaults to `gpt-5.6-sol`. |
| `--reasoning-effort` | Codex reasoning effort used for both conditions. |
| `--trials` | Number of trials for each selected case and condition. |
| `--case-id` | One case ID, or `all` for every case. |
| `--index-path` | One repository-local SQLite database below `./artifacts/`; defaults to `./artifacts/roslynkit.db`. |
| `--roslynkit-path` | An executable named `roslynkit` or `roslynkit.exe` used instead of the `roslynkit` command from `PATH`; use it to pin a source-built, isolated tool. |
| `--report-run-root` | Rebuild reports for a completed Bash-runner artifact root after updating its structured correctness review; starts no Codex session. |
| `--dry-run` | Print the planned invocations without starting Codex. |

For example, inspect a complete planned invocation without running a benchmark:

```bash
bash ./scripts/benchmark-codex.sh --model gpt-5.6-sol --reasoning-effort high --trials 1 --case-id daemon-disconnect --dry-run
```

Use complete GNU-style option names in benchmark commands. Omit `--dry-run` only after an explicit request to execute the benchmark.

The runner supports Git Bash on Windows, Windows Subsystem for Linux (WSL), VS Code Remote–WSL, Linux, and macOS. It requires Bash and Python 3.10 or later, resolves `rg` and, by default, global `roslynkit` applications from the current host's `PATH`, and does not use or accept the side-by-side `roslynkit-dev` installation. `--roslynkit-path` prepends an isolated executable's directory to the child `PATH`, so the same pinned executable supplies the preflight, index preparation, and measured RoslynKit condition without changing the global installation. Git Bash runs use Windows-local tools and configuration; WSL runs use WSL-local `PATH` and `CODEX_HOME`; Unix hosts use their local environment. The controller does not cross a host boundary to reuse another environment's installations.

A one-trial run with `--case-id all` starts eight measured Codex sessions: four cases across two conditions. Runtime therefore includes eight independent investigations, one shared search-index preparation, and one short, unmeasured tool preflight.

## Direct-worktree contract

Each child `codex exec` uses `--cd <repository-root>` and investigates the active repository worktree directly. Both conditions use that same root and resolve `rg` from the inherited host `PATH`; the RoslynKit condition resolves global `roslynkit` from that `PATH` unless `--roslynkit-path` selects an isolated executable. Every child disables `unified_exec`, which gives Git Bash and Unix hosts the same shell-tool interface and preserves the per-call `timeout_ms` behavior required by RoslynKit commands. The runner restores repository dependencies once, prepares one Git-ignored shared search index below `artifacts/` before timed work, and reuses that index for every selected case and trial. RoslynKit receives `./RoslynKit.slnx` and the selected portable repository-relative index path; the default is `./artifacts/roslynkit.db`. The runner does not copy executable or package payload files and does not install command rules; existing rules from the active host's Codex home remain in effect.

Before preflight, the controller captures a content manifest for the worktree. The manifest records the initial content state and therefore permits an initially dirty worktree. After preflight, after search-index preparation, and after every measured session, the controller compares tracked and non-ignored content with that manifest; a later content change records the affected session as invalid and stops the controller because subsequent measurements would not share the same baseline. Git-ignored files, including the shared index and benchmark evidence, are excluded from that comparison. Do not start a benchmark while other work is changing the repository.

Before measured work begins, one unmeasured Codex preflight invokes the hidden `--internal-tool-probe-path` mode of [scripts/benchmark-codex.sh](../scripts/benchmark-codex.sh) through Bash. That child resolves `rg` and `roslynkit` from its inherited `PATH`, or uses the selected `--roslynkit-path`, runs each tool's version command separately, and writes `preflight/tool-probe.json` with the host classification, resolved paths, version output, executable SHA-256 hashes, and individual exit codes. The controller requires exactly one successful child command event and validates the structured artifact; unreliable command `aggregated_output` and wording in the model-written final response do not determine validity. A missing tool, missing artifact field, malformed artifact, or nonzero individual exit aborts before any measured session. During measurement, a declined command, nonzero command exit, compliance failure, missing answer, or missing accounting marks only the current session invalid. The controller writes its evidence and continues the remaining scheduled sessions without retry so other cases and conditions still produce evidence. Reports retain invalid rows and calculate comparisons only from valid rows.

Both measured conditions read [.agents/skills/benchmark/SKILL.md](../.agents/skills/benchmark/SKILL.md) as their first command by running exactly `bash -lc 'cat .agents/skills/benchmark/SKILL.md'`, but only its measured-session section governs them. The `raw-codex` condition uses ordinary Bash and text inspection; it must not read the RoslynKit skill or invoke RoslynKit. The `roslynkit` condition must read [.agents/skills/roslynkit/SKILL.md](../.agents/skills/roslynkit/SKILL.md), [.agents/skills/roslynkit/references/commands.md](../.agents/skills/roslynkit/references/commands.md), and [.agents/skills/roslynkit/references/output.md](../.agents/skills/roslynkit/references/output.md) through `bash -lc` and `cat` before calling the global `roslynkit` command. Generated measured commands use Bash. When auditing command events, the controller normalizes Bash, `sh`, and `zsh` `-c` or `-lc` envelopes plus an optional GNU `timeout` envelope; PowerShell and `cmd` wrappers are noncompliant. Measured sessions must not read [AGENTS.md](../AGENTS.md), [.codex/atlas/repo-map.md](../.codex/atlas/repo-map.md), [scripts/benchmark-codex.sh](../scripts/benchmark-codex.sh), this procedure, benchmark data, private review criteria, prior artifacts, or any other skill. Both conditions must limit recursive and literal searches to explicit permitted source or test paths; repository-root recursive searches are forbidden because they can inspect controller-only context.

The RoslynKit condition starts intent discovery with one narrow `roslynkit search` query capped at 10 results. If that pass has no useful method or location, one refined query keeps the 10-result cap and may add `--kind method`. Only when the refined ranking still lacks a reliable jump target may one third and final search expand to 20 results, or 50 when the first two rankings show many plausible near-ties that a 20-result window may truncate; a fourth search is forbidden. The condition has a hard ceiling of eight RoslynKit invocations, which the controller audits separately from total tool calls. Every RoslynKit shell tool call sets `timeout_ms` to `120000`; the prepared SQLite index avoids rebuilding the search corpus but does not remove cold workspace and daemon startup time. RoslynKit commands must run serially: each invocation must finish before another starts, without concurrent tool calls, background jobs, or parallel pipelines. Independent cold commands can otherwise contend while loading the same solution even though every process receives the shared index path. Emitted `id:` selectors remain opaque and must not be reconstructed; pass shell-sensitive IDs as one correctly quoted `--symbol` argument or use the returned `loc:` with a bounded `document-lines` call. Before checking invocation, required skill reads, ordering, overlap, and the invocation ceiling, the controller recursively normalizes Bash, `sh`, and `zsh` `-c` or `-lc` plus GNU `timeout` command envelopes. Quoted search text remains data rather than an invocation. A timeout remains a failed command even when captured output looks complete and is not retried inside the measured session because a retry would change the timing condition.

The preparation daemon is stopped before timing, and the RoslynKit daemon is stopped after every Codex session so trials do not share an in-memory workspace. The shared index, its SQLite write-ahead logging (WAL) and shared-memory sidecars, and benchmark evidence are Git ignored and excluded from content-manifest checks; tracked and non-ignored content changes still invalidate a run. Every condition and trial uses the active host's `CODEX_HOME` directly, including its complete `config.toml`, selected model provider, command rules, and credential source. When `CODEX_HOME` is unset, the runner uses `.codex` below that host's platform user-profile directory. The runner does not copy, filter, or delete authentication or configuration; Codex itself may update its normal state under that home. Child commands use `shell_environment_policy.inherit="all"`, so the complete parent environment, including `PATH` and provider-related variables, is available to commands. Benchmark-specific command-line arguments still override the model, reasoning effort, memory, optional-feature, and session-persistence settings needed for a controlled comparison. Each session remains ephemeral and clears the parent thread identifier.

Every preflight and measured session uses `--dangerously-bypass-approvals-and-sandbox`. This is equivalent to approval `never` with unsandboxed command execution and is required here so the workstation-global executables can run directly. The Codex CLI labels this flag extremely dangerous and intends it only for externally sandboxed environments. Running this benchmark directly on a workstation accepts host-wide access as an explicit operator risk. Prompts prohibit source changes, and the content manifest detects later worktree-content changes beyond expected index files, but it cannot prevent or audit reads and writes elsewhere on the host.

Accounting reads only Codex `stdout` JSON Lines (JSONL) events. The run does not use rollout files, prior sessions, or memory as a token source. A valid current event stream has exactly one terminal `turn.completed.usage` aggregate and records input, cached input, cache-write input, output, and reasoning-output tokens. Malformed JSONL, missing terminal accounting, or multiple terminal aggregates invalidate the measured session. Persistent run evidence is written to `artifacts/codex-benchmark/<timestamp>/`.

Each completed artifact directory contains `runs.csv`, `runs.json`, `summary.md`, controller-only `review.md` and `review-results.json`, per-run `answers/`, `commands/`, `events/`, and `stderr/` evidence, plus the unmeasured `preflight/` evidence. The preflight directory includes `tool-probe.json` as the child-observed host and tool identity record, including each executable's SHA-256 hash. A preflight failure can leave only the preflight evidence.

## Cost model

The runner stores the following Standard application programming interface (API) prices per one million tokens, verified against [OpenAI API pricing](https://developers.openai.com/api/docs/pricing) on 2026-08-21:

| Model | Short input | Short cached input | Short cache write | Short output | Long input | Long cached input | Long cache write | Long output |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| GPT-5.6 Sol | $5.00 | $0.50 | $6.25 | $30.00 | $10.00 | $1.00 | $12.50 | $45.00 |
| GPT-5.6 Terra | $2.00 | $0.20 | $2.50 | $12.00 | $4.00 | $0.40 | $5.00 | $18.00 |
| GPT-5.6 Luna | $0.20 | $0.02 | $0.25 | $1.20 | $0.40 | $0.04 | $0.50 | $1.80 |

The Codex JSONL field is `cache_write_input_tokens`; it represents the `cache_write_tokens` category in the API pricing documentation. The aggregate calculation is:

```text
regular_uncached_input = input - cached_input - cache_write_input

projected_cost =
    regular_uncached_input × input_rate
  + cached_input          × cached_input_rate
  + cache_write_input     × cache_write_rate
  + output                × output_rate
```

Reasoning-output tokens are retained as a diagnostic subset and are not billed a second time because they are already included in output tokens. These figures are projections, not a claim about the active Codex account's bill; provider pricing, service tier, regional uplift, hosted-tool charges, and account agreements can differ.

GPT-5.6 requests above 272K input tokens use long-context prices for the full qualifying request. Current `codex exec --json` output exposes one cumulative completed-turn total across the investigation, not usage for each underlying model request. The runner therefore leaves `max_request_input_tokens` and `requests_over_272k` empty, marks long-context status unknown, and reports the all-short and all-long projections as bounds. It does not compare the cumulative turn total directly with 272K.

## Review results

Operational validity and answer correctness are separate. Review every answer against the private criteria in `review.md`, set each criterion and `overall_status` in `review-results.json`, then refresh the report without starting Codex:

```bash
bash ./scripts/benchmark-codex.sh --report-run-root ./artifacts/codex-benchmark/<timestamp>
```

Cost savings and token savings use only operationally valid runs whose structured review has `overall_status` set to `pass`. Compare those results only within the same run and model. Timing comparisons across Git Bash, WSL, VS Code Remote–WSL, Linux, macOS, or separate artifact roots are invalid. Runs made before `unified_exec` was disabled are also not timing-comparable with post-fix runs.

The direct-worktree design has reduced isolation:

- The live workstation Codex home supplies its current authentication, complete configuration, and command rules. Results are comparable only while that external state and its referenced environment variables remain stable across all conditions and trials.
- Full environment inheritance exposes parent environment values to child commands. Run the benchmark only when those values and both global tool versions are appropriate to expose to an unsandboxed Codex session.
- The approval and sandbox bypass permits host-level access. The content manifest detects later repository-content changes, but provides no operating-system isolation or host-wide mutation audit.
- The measured sessions use the active worktree rather than a separate repository copy. Initial uncommitted work is allowed as a recorded baseline, but concurrent edits make results invalid and can expose worktree content to a full-access child process.
- Context restrictions are prompt and command-event audit controls, not filesystem isolation. Indirectly constructed reads can evade command-text auditing and must be considered during manual evidence review.
- Ordinary session failures are recorded as invalid and excluded from comparison; the remaining scheduled sessions continue without retry. Preparation failures, preflight failures, and repository content changes still stop the controller.
- RoslynKit command overlap invalidates the affected run. Review `events/` and `commands/` when a RoslynKit timeout occurs; a shared index path does not make concurrent solution loads one process or one workspace instance.
