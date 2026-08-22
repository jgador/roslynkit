---
name: benchmark
description: Run or inspect the opt-in Bash-controlled raw-text versus RoslynKit text-only token benchmark. Use only when the user explicitly invokes $benchmark or requests this benchmark.
---

# Benchmark

Read [docs/benchmark.md](../../../docs/benchmark.md) for the canonical procedure and invoke:

```bash
bash ./scripts/benchmark.sh [options]
```

Run `--dry-run` before a measured invocation. A paid Codex session requires an explicit user request; requests to design, review, or modify the benchmark do not authorize one.

The Bash controller owns options, scheduling, and direct `codex exec` calls. It clears host-injected `CODEX_THREAD_ID`, uses `--ephemeral`, and retains `CODEX_HOME`. The C# helper owns catalog validation, retrieval, JSON Lines (JSONL) evaluation, persisted run state, and reports; it does not launch Codex. Judge prompts prohibit tool calls, and the evaluator rejects tool events; `--sandbox read-only` does not mean tools are disabled.

Use `--case <id|all>` to select cases (`--case-id` is a compatibility alias), `--resume-run-root` to continue only unfinished sessions, and `--report-run-root` to regenerate reports without model sessions. The helper retrieves evidence directly with the RoslynKit apphost. Inspect `run.json`, `runs.csv`, and `summary.md` before comparing token results.
