---
name: benchmark
description: Run or inspect the opt-in native raw-text versus RoslynKit text-only token benchmark. Use only when the user explicitly invokes $benchmark or requests this benchmark.
---

# Benchmark

Read [docs/benchmark.md](../../../docs/benchmark.md) for the canonical procedure and invoke:

```bash
dotnet run --project ./tests/Integration/Benchmarking/RoslynKit.Benchmarking.csproj -- [options]
```

Run `--dry-run` before a measured invocation. A paid Codex session requires an explicit user request; requests to design, review, or modify the benchmark do not authorize one.

Use `--case <id|all>` to select cases (`--case-id` is a compatibility alias), `--resume-run-root` to continue a run, and `--report-run-root` to regenerate reports without model sessions. The harness retrieves evidence directly with the RoslynKit apphost, and only a tool-free judge receives that evidence. Inspect `run.json`, `runs.csv`, and `summary.md` before comparing token results.
