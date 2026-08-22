# Benchmark

This opt-in benchmark compares two retrieval conditions for the same repository question: bounded raw-text excerpts and RoslynKit text-only search evidence. A Codex judge receives only the controller-supplied retrieval text, performs no tool calls, and reports an answer with JSON Lines (JSONL) token accounting. Retrieval happens outside the measured judge turn.

Run the native .NET 10 harness from the repository root:

```bash
dotnet run --project ./tests/Integration/Benchmarking/RoslynKit.Benchmarking.csproj -- [options]
```

Benchmark sessions can incur model costs. Run sessions only when a user explicitly requests them. Start with a dry run, which validates the options and case catalog and prints planned work without building RoslynKit or starting a Codex session:

```bash
dotnet run --project ./tests/Integration/Benchmarking/RoslynKit.Benchmarking.csproj -- \
  --dry-run --trials 1 --case daemon-disconnect
```

The harness reads its six case definitions from [tests/Integration/Benchmarking/cases.json](../tests/Integration/Benchmarking/cases.json). Each case defines an ID, query, intent, and required evidence groups. Catalog validation rejects unknown or malformed properties and evidence paths that do not identify repository files.

## Options

- `--model <model>` and `--reasoning-effort <effort>` select the Codex judge configuration.
- `--trials <count>` sets the number of paired trials per selected case.
- `--case <id|all>` selects a case; `--case-id` remains a compatibility alias.
- `--max-results <count>` bounds retrieval evidence.
- `--index-path <path>` selects the Git-ignored text-only search index.
- `--roslynkit-path <path>` selects a built RoslynKit apphost.
- `--dry-run` validates and prints planned work without starting a Codex session.
- `--resume-run-root <path>` continues only missing case, condition, and trial tuples from one prior run.
- `--report-run-root <path>` rebuilds reports from one prior run without starting a Codex session.

When `--roslynkit-path` is omitted, the harness builds RoslynKit in Release configuration and invokes `artifacts/bin/RoslynKit/release/RoslynKit[.exe]` directly. It uses that apphost for `index --text-only` and `search --text-only --compact --balanced`; it does not use the workspace daemon, PowerShell, Python, or `dotnet run` for retrieval.

The raw-text condition tokenizes alphanumeric and underscore query terms of at least three characters. It ranks C# files independently in `src/RoslynKit` and `tests/RoslynKit.Tests` by distinct-term coverage, total occurrences, and path. Each scope contributes at most eight files; each file contributes at most eight matching anchors, three lines of surrounding context, and 300 characters per rendered line. Build-output and infrastructure directories are excluded. The RoslynKit condition uses one bounded compact balanced search over the same repository.

Condition order alternates by trial. A one-trial all-cases run starts 12 judge turns: two conditions for each of six cases. A three-trial acceptance run starts 36 judge turns.

Each judge inherits the active host's `CODEX_HOME`, including its host-local `config.toml`, selected model provider, and credential source; Windows Subsystem for Linux (WSL) runs use WSL-local configuration. Each process clears `CODEX_THREAD_ID`, ignores project instructions and command rules, disables tool-related features, and runs `codex exec --json --ephemeral` directly. A session is rejected if it calls a tool, lacks exactly one terminal usage event, has an empty answer, exits unsuccessfully, or misses required evidence.

Input-token savings are `100 * (raw input - RoslynKit input) / raw input`. A pair is accepted only when both sessions are valid and correct and RoslynKit saves at least 20%. Every scheduled pair must meet that threshold; a passing median alone is insufficient.

## Artifacts and reports

Each new run writes one canonical typed document to `artifacts/benchmark/<timestamp>/run.json`. The harness derives `runs.csv` and `summary.md` from that document. Resume and report-only modes hydrate the same document, so historical Bash and Python benchmark artifacts are intentionally unsupported.

Resume uses the cases and configuration stored in `run.json` and starts only missing case, condition, and trial tuples. Report-only mode starts no child process and can regenerate the derived files even if the checked-in catalog has changed.
