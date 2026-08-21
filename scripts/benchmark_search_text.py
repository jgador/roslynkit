#!/usr/bin/env python3
"""Runs the focused raw-versus-RoslynKit search-text token benchmark."""

from __future__ import annotations

import argparse
import csv
import json
import os
from pathlib import Path
import re
import shlex
import shutil
import statistics
import subprocess
import sys
import tempfile
import time
from typing import Any

import benchmark_codex_support as support


CONDITIONS = ("raw-text", "roslynkit-search")
RAW_FILES_PER_SCOPE = 8
RAW_ANCHORS_PER_FILE = 8
RAW_CONTEXT_LINES = 3
DISABLED_FEATURES = (
    "apps",
    "browser_use",
    "memories",
    "multi_agent",
    "plugins",
    "standalone_web_search",
    "unified_exec",
)


def fail(message: str) -> None:
    raise SystemExit(f"error: {message}")


def load_cases(repo_root: Path, case_id: str) -> list[dict[str, Any]]:
    path = repo_root / "benchmarks" / "search-text-cases.json"
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        fail(f"Could not read search-text benchmark cases: {error}")
    cases = data.get("cases") if isinstance(data, dict) else None
    if not isinstance(cases, list) or not cases:
        fail("Search-text benchmark cases must contain one or more cases.")
    required_keys = {"id", "intent", "query", "requiredEvidenceGroups"}
    if any(not isinstance(case, dict) or not required_keys.issubset(case) for case in cases):
        fail("Every search-text benchmark case must contain id, intent, query, and requiredEvidenceGroups.")
    ids = [case["id"] for case in cases]
    if len(ids) != len(set(ids)):
        fail("Search-text benchmark case IDs must be unique.")
    if case_id == "all":
        return cases
    selected = [case for case in cases if case["id"] == case_id]
    if not selected:
        fail(f"Unknown search-text benchmark case: {case_id}")
    return selected


def resolve_run_root(repo_root: Path, value: str) -> Path:
    candidate = Path(value)
    if not candidate.is_absolute():
        candidate = repo_root / candidate
    candidate = candidate.resolve()
    allowed_parent = (repo_root / "artifacts" / "search-text-benchmark").resolve()
    if candidate.parent != allowed_parent or not candidate.is_dir():
        fail("The run root must identify one existing run below artifacts/search-text-benchmark.")
    return candidate


def search_command(case: dict[str, Any], index_path: str, max_results: int) -> list[str]:
    return [
        "dotnet",
        "run",
        "--project",
        "./src/RoslynKit",
        "--no-build",
        "--",
        "search",
        "--target",
        "./RoslynKit.slnx",
        "--index-path",
        index_path,
        "--query",
        str(case["query"]),
        "--max-results",
        str(max_results),
        "--text-only",
        "--compact",
        "--balanced",
    ]


def render_prompt(condition: str, case: dict[str, Any], evidence: str) -> str:
    shared = [
        f"Search-retrieval benchmark condition: {condition}.",
        "Do not use tools or outside knowledge. Judge only the supplied search text.",
        "Return at most six declarations as `path:line — declaration — relevance`; include production and focused test evidence.",
        "Cover the orchestration entry point, supporting implementation, and focused tests when those distinct roles appear in the intent.",
        "Choose only relevant evidence and stop when those roles are covered.",
    ]
    if condition not in CONDITIONS:
        fail(f"Unsupported condition: {condition}")
    shared.extend(["", f"Intent: {case['intent']}", "", "Search text:", evidence])
    return "\n".join(shared)


def query_tokens(query: str) -> tuple[str, ...]:
    return tuple(dict.fromkeys(token.lower() for token in re.findall(r"[A-Za-z0-9_]+", query) if len(token) >= 3))


def raw_text_evidence(repo_root: Path, case: dict[str, Any]) -> str:
    tokens = query_tokens(str(case["query"]))
    sections = [
        "Plain-text baseline: files ranked by distinct query terms, then bounded matching-line context.",
    ]
    for scope in (Path("src/RoslynKit"), Path("tests/RoslynKit.Tests")):
        ranked: list[tuple[int, int, Path, list[str]]] = []
        for path in (repo_root / scope).rglob("*.cs"):
            lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
            normalized = "\n".join(lines).lower()
            counts = [normalized.count(token) for token in tokens]
            distinct = sum(count > 0 for count in counts)
            if distinct:
                ranked.append((distinct, sum(counts), path, lines))
        ranked.sort(key=lambda item: (-item[0], -item[1], item[2].as_posix()))

        sections.extend(["", f"## {scope.as_posix()}"])
        for _, _, path, lines in ranked[:RAW_FILES_PER_SCOPE]:
            anchors: list[tuple[int, int, int]] = []
            for line_index, line in enumerate(lines):
                normalized_line = line.lower()
                counts = [normalized_line.count(token) for token in tokens]
                distinct = sum(count > 0 for count in counts)
                if distinct:
                    anchors.append((distinct, sum(counts), line_index))
            anchors.sort(key=lambda item: (-item[0], -item[1], item[2]))
            selected_lines: set[int] = set()
            for _, _, line_index in anchors[:RAW_ANCHORS_PER_FILE]:
                start = max(0, line_index - RAW_CONTEXT_LINES)
                end = min(len(lines), line_index + RAW_CONTEXT_LINES + 1)
                selected_lines.update(range(start, end))

            relative = path.relative_to(repo_root).as_posix()
            sections.extend(["", f"### {relative}"])
            for line_index in sorted(selected_lines):
                sections.append(f"{relative}:{line_index + 1}: {lines[line_index][:300]}")
    return "\n".join(sections)


def roslynkit_search_evidence(
    repo_root: Path,
    environment: dict[str, str],
    case: dict[str, Any],
    index_path: str,
    max_results: int,
) -> tuple[str, str]:
    command = search_command(case, index_path, max_results)
    completed = subprocess.run(
        command,
        cwd=repo_root,
        env=environment,
        check=False,
        capture_output=True,
        text=True,
    )
    if completed.returncode != 0:
        fail(f"Direct RoslynKit search failed ({completed.returncode}): {completed.stderr.strip()}")
    if not completed.stdout.startswith("results:"):
        fail("Direct RoslynKit search did not return compact ranked search text.")
    return completed.stdout.rstrip(), shlex.join(command)


def retrieve_evidence(
    condition: str,
    repo_root: Path,
    environment: dict[str, str],
    case: dict[str, Any],
    index_path: str,
    max_results: int,
) -> tuple[str, str]:
    if condition == "raw-text":
        return raw_text_evidence(repo_root, case), "controller plain-text ranked excerpt search"
    if condition == "roslynkit-search":
        return roslynkit_search_evidence(repo_root, environment, case, index_path, max_results)
    fail(f"Unsupported condition: {condition}")


def run_checked(command: list[str], repo_root: Path, environment: dict[str, str]) -> None:
    completed = subprocess.run(command, cwd=repo_root, env=environment, check=False)
    if completed.returncode != 0:
        fail(f"Preparation command failed ({completed.returncode}): {shlex.join(command)}")


def build_codex_command(
    codex_path: str,
    repo_root: Path,
    model: str,
    reasoning_effort: str,
    answer_path: Path,
    prompt: str,
) -> list[str]:
    command = [
        codex_path,
        "exec",
        "--ignore-user-config",
        "--approve-for-me",
        "--config",
        f'model_reasoning_effort="{reasoning_effort}"',
        "--config",
        "project_doc_max_bytes=0",
        "--config",
        "memories.use_memories=false",
        "--config",
        "memories.generate_memories=false",
        "--config",
        'shell_environment_policy.inherit="all"',
        "--model",
        model,
        "--ephemeral",
        "--json",
        "--color",
        "never",
        "--cd",
        str(repo_root),
        "--output-last-message",
        str(answer_path),
    ]
    for feature in DISABLED_FEATURES:
        command.extend(("--disable", feature))
    command.append(prompt)
    return command


def isolate_codex_home(environment: dict[str, str]) -> tempfile.TemporaryDirectory[str]:
    active_home = Path(environment.get("CODEX_HOME", Path.home() / ".codex")).expanduser().resolve()
    auth_path = active_home / "auth.json"
    if not auth_path.is_file():
        fail(f"The active Codex authentication file was not found: {auth_path}")

    temporary_home = tempfile.TemporaryDirectory(prefix="roslynkit-search-benchmark-")
    destination = Path(temporary_home.name) / "auth.json"
    shutil.copyfile(auth_path, destination)
    destination.chmod(0o600)
    environment["CODEX_HOME"] = temporary_home.name
    return temporary_home


def answer_covers_case(answer: str, case: dict[str, Any]) -> tuple[bool, list[list[str]]]:
    missing: list[list[str]] = []
    normalized_answer = answer.replace("\\", "/").lower()
    for group in case["requiredEvidenceGroups"]:
        candidates = [str(candidate).replace("\\", "/") for candidate in group]
        if not any(candidate.lower() in normalized_answer for candidate in candidates):
            missing.append(candidates)
    return not missing, missing


def evaluate_run(
    case: dict[str, Any],
    condition: str,
    trial: int,
    event_path: Path,
    answer_path: Path,
    evidence_path: Path,
    stderr_path: Path,
    exit_code: int,
    duration_seconds: float,
    model: str,
    reasoning_effort: str,
    retrieval_command: str,
) -> dict[str, Any]:
    events = support.read_events(event_path)
    accounting = support.get_token_accounting(events)
    commands = support.get_commands(events)
    answer = answer_path.read_text(encoding="utf-8") if answer_path.is_file() else ""
    correct, missing_evidence = answer_covers_case(answer, case)
    issues = list(accounting["issues"])
    if exit_code != 0:
        issues.append(f"codex exited with {exit_code}")
    if not answer.strip():
        issues.append("answer was empty")
    if commands:
        issues.append("LLM judge used tools instead of judging only the supplied search text")

    usage = accounting["usage"] or {}
    short_cost = support.get_gpt56_cost_projection(usage if usage else None, model, "short")
    return {
        "case_id": case["id"],
        "condition": condition,
        "trial": trial,
        "model": model,
        "reasoning_effort": reasoning_effort,
        "valid": not issues,
        "correct": correct,
        "missing_evidence": missing_evidence,
        "issues": issues,
        "command_count": len(commands),
        "retrieval_command": retrieval_command,
        "retrieval_bytes": evidence_path.stat().st_size,
        "duration_seconds": round(duration_seconds, 4),
        "input_tokens": usage.get("input_tokens"),
        "cached_input_tokens": usage.get("cached_input_tokens"),
        "cache_write_input_tokens": usage.get("cache_write_input_tokens"),
        "regular_uncached_input_tokens": usage.get("regular_uncached_input_tokens"),
        "output_tokens": usage.get("output_tokens"),
        "reasoning_output_tokens": usage.get("reasoning_output_tokens"),
        "projected_short_cost_usd": short_cost,
        "answer_path": str(answer_path),
        "evidence_path": str(evidence_path),
        "event_path": str(event_path),
        "stderr_path": str(stderr_path),
    }


def median(values: list[float | int]) -> float | None:
    return statistics.median(values) if values else None


def paired_rows(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    grouped: dict[tuple[str, int], dict[str, dict[str, Any]]] = {}
    for row in rows:
        grouped.setdefault((row["case_id"], row["trial"]), {})[row["condition"]] = row
    pairs: list[dict[str, Any]] = []
    for (case_id, trial), conditions in sorted(grouped.items()):
        raw = conditions.get("raw-text")
        roslynkit = conditions.get("roslynkit-search")
        comparable = bool(
            raw
            and roslynkit
            and raw["valid"]
            and raw["correct"]
            and roslynkit["valid"]
            and roslynkit["correct"]
            and raw["input_tokens"]
            and roslynkit["input_tokens"] is not None
        )
        savings = None
        if comparable:
            savings = 100.0 * (raw["input_tokens"] - roslynkit["input_tokens"]) / raw["input_tokens"]
        pairs.append(
            {
                "case_id": case_id,
                "trial": trial,
                "comparable": comparable,
                "raw_input_tokens": raw["input_tokens"] if raw else None,
                "roslynkit_input_tokens": roslynkit["input_tokens"] if roslynkit else None,
                "input_token_savings_pct": round(savings, 4) if savings is not None else None,
            }
        )
    return pairs


def format_value(value: Any) -> str:
    if value is None:
        return "-"
    if isinstance(value, float):
        return f"{value:.2f}"
    return str(value)


def write_reports(run_root: Path, rows: list[dict[str, Any]]) -> None:
    pairs = paired_rows(rows)
    comparable_savings = [pair["input_token_savings_pct"] for pair in pairs if pair["comparable"]]
    consistent = bool(pairs) and len(comparable_savings) == len(pairs) and all(value >= 20.0 for value in comparable_savings)
    (run_root / "runs.json").write_text(json.dumps(rows, indent=2) + "\n", encoding="utf-8")
    (run_root / "pairs.json").write_text(json.dumps(pairs, indent=2) + "\n", encoding="utf-8")

    fieldnames = list(rows[0]) if rows else []
    with (run_root / "runs.csv").open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=fieldnames)
        writer.writeheader()
        for row in rows:
            writer.writerow(row)

    lines = [
        "# Search-text benchmark",
        "",
        f"Model: {rows[0]['model'] if rows else '-'}",
        f"Reasoning effort: {rows[0]['reasoning_effort'] if rows else '-'}",
        f"LLM judgments: {len(rows)}",
        f"Comparable pairs: {len(comparable_savings)}/{len(pairs)}",
        f"Minimum input-token savings: {format_value(min(comparable_savings) if comparable_savings else None)}%",
        f"Median input-token savings: {format_value(median(comparable_savings))}%",
        f"Maximum input-token savings: {format_value(max(comparable_savings) if comparable_savings else None)}%",
        f"Every scheduled pair was valid, correct, and saved at least 20%: {'yes' if consistent else 'no'}",
        "",
        "| Case | Trial | Raw input | RoslynKit input | Savings | Comparable |",
        "| --- | ---: | ---: | ---: | ---: | --- |",
    ]
    for pair in pairs:
        savings = pair["input_token_savings_pct"]
        lines.append(
            f"| {pair['case_id']} | {pair['trial']} | {format_value(pair['raw_input_tokens'])} | "
            f"{format_value(pair['roslynkit_input_tokens'])} | "
            f"{format_value(savings)}{'%' if savings is not None else ''} | "
            f"{'yes' if pair['comparable'] else 'no'} |"
        )
    lines.extend(
        [
            "",
            "A pair is comparable only when both sessions are operationally valid and contain every required production/test evidence group.",
        ]
    )
    (run_root / "summary.md").write_text("\n".join(lines) + "\n", encoding="utf-8")


def prepare_repository(repo_root: Path, environment: dict[str, str], index_path: str) -> None:
    run_checked(
        ["dotnet", "restore", "./src/RoslynKit/RoslynKit.csproj", "--disable-parallel", "--nologo", "--verbosity", "quiet"],
        repo_root,
        environment,
    )
    run_checked(
        ["dotnet", "build", "./src/RoslynKit/RoslynKit.csproj", "--no-restore", "--nologo", "--tl:off", "-clp:ErrorsOnly;NoSummary"],
        repo_root,
        environment,
    )
    run_checked(
        [
            "dotnet",
            "run",
            "--project",
            "./src/RoslynKit",
            "--no-build",
            "--",
            "index",
            "--target",
            "./RoslynKit.slnx",
            "--index-path",
            index_path,
            "--text-only",
        ],
        repo_root,
        environment,
    )


def main(arguments: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="benchmark-search-text.sh")
    parser.add_argument("--model", default="gpt-5.6-sol")
    parser.add_argument("--reasoning-effort", default="high")
    parser.add_argument("--trials", type=int, default=1)
    parser.add_argument("--case-id", default="all")
    parser.add_argument("--index-path", default="./artifacts/roslynkit-text.db")
    parser.add_argument("--max-results", type=int, default=10)
    run_root_options = parser.add_mutually_exclusive_group()
    run_root_options.add_argument("--report-run-root")
    run_root_options.add_argument("--resume-run-root")
    parser.add_argument("--dry-run", action="store_true")
    options = parser.parse_args(arguments)
    if not 1 <= options.trials <= 100:
        fail("--trials must be from 1 through 100.")
    if not 2 <= options.max_results <= 50:
        fail("--max-results must be from 2 through 50.")

    repo_root = Path(subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip()).resolve()
    cases = load_cases(repo_root, options.case_id)
    index_path = support.resolve_benchmark_index_path(repo_root, options.index_path)
    if options.report_run_root:
        run_root = resolve_run_root(repo_root, options.report_run_root)
        rows = json.loads((run_root / "runs.json").read_text(encoding="utf-8"))
        write_reports(run_root, rows)
        print(f"Search-text benchmark reports refreshed: {run_root}")
        return 0

    if options.dry_run:
        for trial in range(1, options.trials + 1):
            conditions = CONDITIONS if trial % 2 else tuple(reversed(CONDITIONS))
            for case in cases:
                for condition in conditions:
                    print(f"[{case['id']}] {condition} trial {trial}")
                    if condition == "roslynkit-search":
                        evidence = f"<output of {shlex.join(search_command(case, index_path, options.max_results))}>"
                    else:
                        evidence = "<controller-generated bounded plain-text search excerpts>"
                    print(render_prompt(condition, case, evidence))
                    print()
        return 0

    codex_path = shutil.which("codex")
    dotnet_path = shutil.which("dotnet")
    if codex_path is None:
        fail("The codex executable is required.")
    if dotnet_path is None:
        fail("The dotnet executable is required.")
    environment = os.environ.copy()
    environment.pop("CODEX_THREAD_ID", None)
    temporary_codex_home = isolate_codex_home(environment)
    prepare_repository(repo_root, environment, index_path)
    manifest = support.get_repository_content_manifest(repo_root)

    if options.resume_run_root:
        run_root = resolve_run_root(repo_root, options.resume_run_root)
        rows = json.loads((run_root / "runs.json").read_text(encoding="utf-8"))
    else:
        run_root = repo_root / "artifacts" / "search-text-benchmark" / time.strftime("%Y%m%d-%H%M%S")
        rows = []
    for child in ("answers", "events", "evidence", "stderr"):
        (run_root / child).mkdir(parents=True, exist_ok=True)
    completed_runs = {(row["case_id"], row["condition"], row["trial"]) for row in rows}
    for trial in range(1, options.trials + 1):
        conditions = CONDITIONS if trial % 2 else tuple(reversed(CONDITIONS))
        for case in cases:
            for condition in conditions:
                if (case["id"], condition, trial) in completed_runs:
                    continue
                run_id = f"{case['id']}-{condition}-trial{trial}"
                answer_path = run_root / "answers" / f"{run_id}.md"
                event_path = run_root / "events" / f"{run_id}.jsonl"
                evidence_path = run_root / "evidence" / f"{run_id}.txt"
                stderr_path = run_root / "stderr" / f"{run_id}.txt"
                evidence, retrieval_command = retrieve_evidence(
                    condition,
                    repo_root,
                    environment,
                    case,
                    index_path,
                    options.max_results,
                )
                evidence_path.write_text(evidence + "\n", encoding="utf-8")
                prompt = render_prompt(condition, case, evidence)
                command = build_codex_command(
                    codex_path,
                    repo_root,
                    options.model,
                    options.reasoning_effort,
                    answer_path,
                    prompt,
                )
                print(f"[{case['id']}] {condition} trial {trial}", flush=True)
                started = time.monotonic()
                with event_path.open("w", encoding="utf-8") as stdout, stderr_path.open("w", encoding="utf-8") as stderr:
                    completed = subprocess.run(command, cwd=repo_root, env=environment, stdout=stdout, stderr=stderr, check=False)
                rows.append(
                    evaluate_run(
                        case,
                        condition,
                        trial,
                        event_path,
                        answer_path,
                        evidence_path,
                        stderr_path,
                        completed.returncode,
                        time.monotonic() - started,
                        options.model,
                        options.reasoning_effort,
                        retrieval_command,
                    )
                )
                write_reports(run_root, rows)
                changes = support.get_repository_content_changes(repo_root, manifest)
                if changes:
                    fail(f"Repository content changed during benchmark: {changes}")
    temporary_codex_home.cleanup()
    print(f"Search-text benchmark complete: {run_root}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
