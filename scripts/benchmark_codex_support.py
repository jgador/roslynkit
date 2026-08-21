#!/usr/bin/env python3
"""Portable support for the Codex search benchmark controller.

The Bash runner owns the public command line.  This module deliberately uses
only the Python standard library so Git Bash, WSL, Linux, and macOS share the
same accounting and report implementation.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import os
import re
import shlex
import shutil
import statistics
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any, Iterable


ROSLYNKIT_SHELL_TIMEOUT_MILLISECONDS = 120000
MAXIMUM_ROSLYNKIT_INVOCATIONS = 8
LONG_CONTEXT_INPUT_TOKEN_THRESHOLD = 272000
PRICING_SOURCE = "https://developers.openai.com/api/docs/pricing"
PRICING_VERIFIED_DATE = "2026-08-21"
PRICING: dict[str, dict[str, float]] = {
    "gpt-5.6-sol": {
        "short_input": 5.00, "short_cached_input": 0.50,
        "short_cache_write": 6.25, "short_output": 30.00,
        "long_input": 10.00, "long_cached_input": 1.00,
        "long_cache_write": 12.50, "long_output": 45.00,
    },
    "gpt-5.6-terra": {
        "short_input": 2.00, "short_cached_input": 0.20,
        "short_cache_write": 2.50, "short_output": 12.00,
        "long_input": 4.00, "long_cached_input": 0.40,
        "long_cache_write": 5.00, "long_output": 18.00,
    },
    "gpt-5.6-luna": {
        "short_input": 0.20, "short_cached_input": 0.02,
        "short_cache_write": 0.25, "short_output": 1.20,
        "long_input": 0.40, "long_cached_input": 0.04,
        "long_cache_write": 0.50, "long_output": 1.80,
    },
}


class BenchmarkError(RuntimeError):
    """Reports a deterministic controller failure without a traceback."""


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def output_path(path: Path) -> str:
    return path.as_posix()


def object_value(value: Any, name: str, default: Any = None) -> Any:
    return value.get(name, default) if isinstance(value, dict) else default


def get_case_data(repo_root: Path) -> list[dict[str, Any]]:
    path = repo_root / "benchmarks" / "codex-cases.json"
    if not path.is_file():
        raise BenchmarkError(f"Benchmark cases were not found at '{path}'.")
    try:
        cases = json.loads(path.read_text(encoding="utf-8")).get("cases", [])
    except json.JSONDecodeError as error:
        raise BenchmarkError(f"Benchmark cases were not valid JSON: '{path}'.") from error
    if (
        not isinstance(cases, list)
        or not cases
        or any(not isinstance(case, dict) or not case.get("id") or not case.get("prompt") for case in cases)
    ):
        raise BenchmarkError("Benchmark case data must contain one or more named prompts.")
    case_ids = [case["id"] for case in cases]
    if len(case_ids) != len(set(case_ids)):
        raise BenchmarkError("Benchmark case IDs must be unique.")
    return cases


def get_selected_cases(cases: list[dict[str, Any]], selected_case_id: str) -> list[dict[str, Any]]:
    if selected_case_id == "all":
        return cases
    selected = [case for case in cases if case["id"] == selected_case_id]
    if len(selected) != 1:
        raise BenchmarkError(f"No benchmark case matches --case-id '{selected_case_id}'.")
    return selected


def resolve_benchmark_index_path(repo_root: Path, path: str) -> str:
    normalized = path.replace("\\", "/")
    if not normalized.startswith("./"):
        normalized = f"./{normalized}"
    if not re.fullmatch(r"\./artifacts/[A-Za-z0-9._-]+\.db", normalized):
        raise BenchmarkError("--index-path must be one repository-local database file below ./artifacts/.")
    candidate = (repo_root / normalized[2:]).resolve()
    artifacts_root = (repo_root / "artifacts").resolve()
    if candidate.parent != artifacts_root:
        raise BenchmarkError("--index-path must remain below the repository artifacts directory.")
    return normalized


def resolve_benchmark_report_run_root(repo_root: Path, path: str) -> Path:
    if not path or not path.strip():
        raise BenchmarkError("--report-run-root must not be empty.")
    candidate = Path(path)
    full_path = candidate.resolve() if candidate.is_absolute() else (repo_root / candidate).resolve()
    artifacts_root = (repo_root / "artifacts" / "codex-benchmark").resolve()
    if full_path.parent != artifacts_root or not (full_path / "runs.json").is_file():
        if full_path.parent != artifacts_root:
            raise BenchmarkError(
                "--report-run-root must identify one run below the repository artifacts/codex-benchmark directory."
            )
        raise BenchmarkError(f"--report-run-root does not contain runs.json: {full_path}")
    return full_path


def get_required_context_paths(condition: str) -> list[str]:
    paths = [".agents/skills/benchmark/SKILL.md"]
    if condition == "roslynkit":
        paths.extend([
            ".agents/skills/roslynkit/SKILL.md",
            ".agents/skills/roslynkit/references/commands.md",
            ".agents/skills/roslynkit/references/output.md",
        ])
    return paths


def new_condition_prompt(condition: str, prompt: str, index_path: str = "./artifacts/roslynkit.db") -> str:
    rules = [
        f"Inspection-only benchmark condition: {condition}.",
        "As the first command, run exactly: bash -lc 'cat .agents/skills/benchmark/SKILL.md'. This reads the benchmark skill before investigating code.",
        "The measured shell is Bash-compatible. Use Bash-native commands only; PowerShell and cmd wrappers are not permitted.",
        "Do not edit files or change Git state.",
        "Do not run builds, restores, tests, or other commands that write caches; inspect test source instead.",
        "Do not use web search, browsers, network requests, or subagents. Do not inspect memory, prior-session files, Atlas, CODEX_HOME, .codex, AGENTS.md, or agent context not explicitly permitted here.",
        "Do not inspect the benchmark controller, private benchmark data, prior benchmark artifacts, or benchmark procedure documentation.",
        "Do not run repository-root recursive searches. Scope every recursive or literal search to explicit permitted source or test paths.",
        "Do not use rg --files; name known source or test paths explicitly and use bounded literal searches only.",
        "Use only simple inspection commands that do not modify the repository and are expected to succeed. A declined command or nonzero exit code invalidates the run.",
        "Return concise source-and-test evidence; do not change files.",
    ]
    if condition == "raw-codex":
        rules.extend([
            "Do not read .agents/skills/roslynkit or any file below it.",
            "Use ordinary local shell and text inspection only. Do not invoke RoslynKit, roslynkit-dev, or dotnet run for RoslynKit.",
            "Use only the known source root src/RoslynKit and test root tests/RoslynKit.Tests. Do not use find, enumerate directories, or probe speculative paths such as test or tests. For rg searches, scope to those known directories instead of listing guessed filenames; only read a specific file after a prior command emitted that path.",
        ])
    elif condition == "roslynkit":
        rules.extend([
            "Then run exactly: bash -lc 'cat .agents/skills/roslynkit/SKILL.md; cat .agents/skills/roslynkit/references/commands.md; cat .agents/skills/roslynkit/references/output.md'. This reads the stable skill and its command and output references before invoking RoslynKit.",
            "Invoke the global RoslynKit from PATH as roslynkit for code investigation.",
            f"Pass --target ./RoslynKit.slnx to RoslynKit. The prepared repository-local search index is {index_path}; pass --index-path {index_path} to search.",
            f"Set timeout_ms to {ROSLYNKIT_SHELL_TIMEOUT_MILLISECONDS} on every shell tool call that invokes RoslynKit; the shell tool's default deadline is too short for a cold workspace command.",
            "Run only one RoslynKit command at a time and wait for it to finish before starting another. Do not use concurrent tool calls, background jobs, or parallel pipelines for RoslynKit.",
            f"Use at most {MAXIMUM_ROSLYNKIT_INVOCATIONS} RoslynKit invocations total, including search and source or test reads.",
            "Start intent discovery with one narrow roslynkit search query and --max-results 10. If it returns no useful method or location, run one refined search with --max-results 10 and add --kind method when appropriate.",
            "Only if the refined results still lack a reliable jump target, run one third and final search with --max-results 20; use --max-results 50 instead only when the earlier rankings show many plausible near-ties. Never run a fourth search. Prefer bounded source slices over whole-file output.",
            "Before investigating, turn every requested behavior, numeric limit, timing rule, and failure or reuse branch into an evidence checklist. Do not answer until each clause is supported by an emitted implementation or focused-test location.",
            "Treat every id: selector as opaque and copy it verbatim. When an id contains shell-sensitive characters, pass it as one single-quoted --symbol value or use its returned loc with a bounded document-lines call; never reconstruct or rewrite the id.",
            "Never guess or shorten a RoslynKit selector. For definition, references, implementations, and symbol-source, use an exact N:, T:, M:, P:, F:, or E: id emitted by a successful RoslynKit command; if no exact id is available, search first or use the returned loc with a bounded file slice. Never substitute the adjacent display name for an emitted id.",
        ])
    else:
        raise BenchmarkError(f"Unsupported benchmark condition: {condition}")
    return "\n".join([*rules, "", prompt])


def read_event_log(path: Path) -> dict[str, Any]:
    events: list[dict[str, Any]] = []
    issues: list[str] = []
    if not path.is_file():
        return {"events": events, "issues": ["event log was not written"]}
    for line_number, line in enumerate(path.read_text(encoding="utf-8", errors="replace").splitlines(), start=1):
        if not line.strip():
            continue
        try:
            value = json.loads(line)
        except json.JSONDecodeError:
            issues.append(f"event log line {line_number} was not valid JSON")
            continue
        if isinstance(value, dict):
            events.append(value)
        else:
            issues.append(f"event log line {line_number} was not a JSON object")
    return {"events": events, "issues": issues}


def read_events(path: Path) -> list[dict[str, Any]]:
    return read_event_log(path)["events"]


def convert_to_token_count(usage: dict[str, Any], name: str, required: bool, issues: list[str]) -> int | None:
    value = usage.get(name)
    if value is None:
        if required:
            issues.append(f"usage omitted {name}")
        return None
    if isinstance(value, bool) or not re.fullmatch(r"\d+", str(value)):
        issues.append(f"usage field {name} was not a nonnegative integer")
        return None
    return int(value)


def get_token_accounting(events: Iterable[dict[str, Any]]) -> dict[str, Any]:
    event_list = list(events)
    issues: list[str] = []
    terminal_events = [event for event in event_list if event.get("type") == "turn.completed" and isinstance(event.get("usage"), dict)]
    legacy_events = [
        event for event in event_list
        if object_value(event.get("payload"), "type") == "token_count"
        and object_value(object_value(event.get("payload"), "info"), "total_token_usage") is not None
    ]
    usage: dict[str, Any] | None = None
    usage_source: str | None = None
    if len(terminal_events) == 1:
        usage = terminal_events[0]["usage"]
        usage_source = "turn.completed.usage"
    elif len(terminal_events) > 1:
        issues.append(f"event log contained {len(terminal_events)} terminal usage events for one ephemeral Codex exec turn")
    elif legacy_events:
        usage = object_value(object_value(legacy_events[-1].get("payload"), "info"), "total_token_usage")
        usage_source = "token_count.info.total_token_usage"
    else:
        issues.append("event log did not contain terminal token accounting")
    result: dict[str, Any] = {
        "usage": None, "issues": issues, "usage_source": usage_source,
        "usage_scope": "completed_turn_aggregate", "terminal_usage_event_count": len(terminal_events),
        "request_usage_available": False, "max_request_input_tokens": None,
        "requests_over_long_context_threshold": None,
    }
    if not isinstance(usage, dict):
        return result
    input_tokens = convert_to_token_count(usage, "input_tokens", True, issues)
    cached_input_tokens = convert_to_token_count(usage, "cached_input_tokens", True, issues)
    cache_write_input_tokens = convert_to_token_count(usage, "cache_write_input_tokens", False, issues)
    alternate_cache_write_tokens = convert_to_token_count(usage, "cache_write_tokens", False, issues)
    if cache_write_input_tokens is None:
        cache_write_input_tokens = alternate_cache_write_tokens
    elif alternate_cache_write_tokens is not None and alternate_cache_write_tokens != cache_write_input_tokens:
        issues.append("usage cache-write token aliases disagreed")
    output_tokens = convert_to_token_count(usage, "output_tokens", True, issues)
    reasoning_output_tokens = convert_to_token_count(usage, "reasoning_output_tokens", True, issues)
    uncached_input_tokens = None
    regular_uncached_input_tokens = None
    if input_tokens is not None and cached_input_tokens is not None:
        if cached_input_tokens > input_tokens:
            issues.append("cached_input_tokens exceeded input_tokens")
        else:
            uncached_input_tokens = input_tokens - cached_input_tokens
            if cache_write_input_tokens is not None:
                if cache_write_input_tokens > uncached_input_tokens:
                    issues.append("cache_write_input_tokens exceeded non-cached input tokens")
                else:
                    regular_uncached_input_tokens = uncached_input_tokens - cache_write_input_tokens
    result["usage"] = {
        "input_tokens": input_tokens,
        "cached_input_tokens": cached_input_tokens,
        "cache_write_input_tokens": cache_write_input_tokens,
        "uncached_input_tokens": uncached_input_tokens,
        "regular_uncached_input_tokens": regular_uncached_input_tokens,
        "output_tokens": output_tokens,
        "reasoning_output_tokens": reasoning_output_tokens,
    }
    return result


def get_token_usage(events: Iterable[dict[str, Any]]) -> dict[str, Any] | None:
    return get_token_accounting(events)["usage"]


def get_gpt56_pricing(model: str) -> dict[str, Any] | None:
    resolved_model = "gpt-5.6-sol" if model == "gpt-5.6" else model
    rates = PRICING.get(resolved_model)
    return {"model": resolved_model, "rates": rates} if rates is not None else None


def get_gpt56_cost_projection(
    usage: dict[str, Any] | None,
    model: str,
    context_class: str = "short",
) -> dict[str, Any] | None:
    pricing = get_gpt56_pricing(model)
    if usage is None or pricing is None or context_class not in {"short", "long"}:
        return None
    regular = usage.get("regular_uncached_input_tokens")
    uncached = usage.get("uncached_input_tokens")
    cached = usage.get("cached_input_tokens")
    cache_write = usage.get("cache_write_input_tokens")
    output = usage.get("output_tokens")
    if cached is None or output is None or (regular is None and uncached is None):
        return None
    rates = pricing["rates"]
    cache_write_known = cache_write is not None and regular is not None
    ordinary_tokens = regular if cache_write_known else uncached
    assert ordinary_tokens is not None
    ordinary_cost = ordinary_tokens / 1_000_000 * rates[f"{context_class}_input"]
    cached_cost = cached / 1_000_000 * rates[f"{context_class}_cached_input"]
    cache_write_cost = cache_write / 1_000_000 * rates[f"{context_class}_cache_write"] if cache_write_known else None
    output_cost = output / 1_000_000 * rates[f"{context_class}_output"]
    total = ordinary_cost + cached_cost + output_cost + (cache_write_cost or 0)
    return {
        "model": pricing["model"], "context_class": context_class,
        "regular_uncached_input_cost_usd": round(ordinary_cost, 9),
        "cached_input_cost_usd": round(cached_cost, 9),
        "cache_write_cost_usd": round(cache_write_cost, 9) if cache_write_cost is not None else None,
        "output_cost_usd": round(output_cost, 9), "total_cost_usd": round(total, 9),
        "status": "complete" if cache_write_known else "excluding_cache_write_uplift",
    }


def get_commands(events: Iterable[dict[str, Any]]) -> list[str]:
    """Returns one command per execution while retaining separate equal commands."""
    commands: list[str] = []
    command_execution_ids: set[str] = set()
    for event in events:
        item = object_value(event, "item", {})
        command = object_value(item, "command")
        if object_value(item, "type") == "command_execution" and isinstance(command, str) and command.strip():
            item_id = object_value(item, "id")
            if isinstance(item_id, str) and item_id:
                if item_id in command_execution_ids:
                    continue
                command_execution_ids.add(item_id)
            commands.append(command)
    for event in events:
        payload = object_value(event, "payload", {})
        if (
            event.get("type") == "response_item"
            and object_value(payload, "type") == "function_call"
            and object_value(payload, "name") in {"shell_command", "exec_command", "shell"}
        ):
            legacy_id = object_value(event, "id") or object_value(payload, "id") or object_value(object_value(event, "item", {}), "id")
            if isinstance(legacy_id, str) and legacy_id in command_execution_ids:
                continue
            try:
                arguments = json.loads(object_value(payload, "arguments", "{}"))
            except (TypeError, json.JSONDecodeError):
                continue
            command = object_value(arguments, "command")
            if isinstance(command, str) and command.strip():
                commands.append(command)
    return commands


def remove_command_envelope_quotes(value: str) -> str:
    trimmed = value.strip()
    if len(trimmed) < 2 or trimmed[0] not in {"'", '"'} or trimmed[-1] != trimmed[0]:
        return trimmed
    payload = trimmed[1:-1]
    return payload.replace("\\\"", '"') if trimmed[0] == '"' else payload.replace("''", "'")


def shell_tokens(command: str) -> list[str] | None:
    try:
        return shlex.split(command, posix=True)
    except ValueError:
        return None


def get_timeout_command_payload(arguments: list[str]) -> str | None:
    index = 0
    options_with_values = {"-k", "--kill-after", "-s", "--signal"}
    while index < len(arguments):
        word = arguments[index]
        if word == "--":
            index += 1
            break
        if word.startswith("-"):
            index += 2 if word in options_with_values else 1
            continue
        if not re.fullmatch(r"(?:\d+(?:\.\d+)?|\.\d+)[smhd]?", word) or index + 1 >= len(arguments):
            return None
        return " ".join(shlex.quote(value) for value in arguments[index + 1:])
    return None


def resolve_command_tokens(tokens: list[str]) -> tuple[str | None, list[str]]:
    """Resolves an executable after ordinary shell launcher prefixes."""
    index = 0
    while index < len(tokens) and re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*=.*", tokens[index]):
        index += 1
    while index < len(tokens):
        executable = Path(tokens[index]).name.lower()
        arguments = tokens[index + 1:]
        if executable == "env":
            index += 1
            while index < len(tokens):
                token = tokens[index]
                if token == "--":
                    index += 1
                    break
                if token in {"-u", "--unset"}:
                    index += 2
                    continue
                if token.startswith("-") or re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*=.*", token):
                    index += 1
                    continue
                break
            continue
        if executable in {"command", "command.exe"}:
            index += 1
            while index < len(tokens) and tokens[index].startswith("-"):
                token = tokens[index]
                if token == "--":
                    index += 1
                    break
                if token in {"-v", "-V"} or re.fullmatch(r"-[A-Za-z]*[vV][A-Za-z]*", token):
                    return None, []
                index += 1
            continue
        if executable in {"exec", "exec.exe"}:
            index += 1
            while index < len(tokens):
                token = tokens[index]
                if token == "--":
                    index += 1
                    break
                if token == "-a":
                    index += 2
                    continue
                if token.startswith("-"):
                    index += 1
                    continue
                break
            continue
        if executable in {"nohup", "nohup.exe"}:
            index += 1
            while index < len(tokens) and tokens[index].startswith("-"):
                token = tokens[index]
                if token == "--":
                    index += 1
                    break
                if token in {"--help", "--version"}:
                    return None, []
                index += 1
            continue
        if executable in {"stdbuf", "stdbuf.exe"}:
            index += 1
            while index < len(tokens):
                token = tokens[index]
                if token == "--":
                    index += 1
                    break
                if token in {"--help", "--version"}:
                    return None, []
                if token in {"-i", "-o", "-e", "--input", "--output", "--error"}:
                    index += 2
                    continue
                if token.startswith("--input=") or token.startswith("--output=") or token.startswith("--error="):
                    index += 1
                    continue
                if token.startswith("-"):
                    index += 1
                    continue
                break
            continue
        if executable in {"time", "time.exe"}:
            index += 1
            while index < len(tokens):
                token = tokens[index]
                if token == "--":
                    index += 1
                    break
                if token in {"--help", "--version"}:
                    return None, []
                if token in {"-o", "--output"}:
                    index += 2
                    continue
                if token.startswith("--output=") or token in {"-a", "--append", "-p", "-v", "--verbose"}:
                    index += 1
                    continue
                if token.startswith("-"):
                    index += 1
                    continue
                break
            continue
        if executable in {"setsid", "setsid.exe"}:
            index += 1
            while index < len(tokens) and tokens[index].startswith("-"):
                token = tokens[index]
                if token == "--":
                    index += 1
                    break
                if token in {"--help", "--version"}:
                    return None, []
                index += 1
            continue
        if executable in {"nice", "nice.exe"}:
            index += 1
            while index < len(tokens):
                token = tokens[index]
                if token == "--":
                    index += 1
                    break
                if token in {"-n", "--adjustment"}:
                    index += 2
                    continue
                if token.startswith("--adjustment=") or re.fullmatch(r"-[0-9]+", token):
                    index += 1
                    continue
                break
            continue
        return executable, arguments
    return None, []


def get_shell_envelope_payload(command: str) -> str | None:
    tokens = shell_tokens(command)
    if not tokens:
        return None
    executable, arguments = resolve_command_tokens(tokens)
    if executable is None:
        return None
    if executable in {"bash", "bash.exe", "sh", "sh.exe", "zsh", "zsh.exe"}:
        for index, argument in enumerate(arguments):
            if argument in {"-c", "-lc"} and index + 1 < len(arguments):
                payload_index = index + 1
                if arguments[payload_index] == "--":
                    payload_index += 1
                return arguments[payload_index] if payload_index < len(arguments) else None
        return None
    if executable in {"timeout", "timeout.exe"}:
        return get_timeout_command_payload(arguments)
    return None


def get_normalized_command_payloads(command: str) -> list[str]:
    payloads: list[str] = []
    current = command.strip()
    for _ in range(9):
        if not current:
            break
        if current not in payloads:
            payloads.append(current)
        next_payload = get_shell_envelope_payload(current)
        if not next_payload or next_payload.strip() == current:
            break
        current = next_payload.strip()
    return payloads


def split_shell_segments(command: str) -> list[str]:
    """Split ordinary shell chains while intentionally keeping quoted text opaque."""
    segments: list[str] = []
    quote: str | None = None
    escaped = False
    start = 0
    index = 0
    while index < len(command):
        character = command[index]
        if escaped:
            escaped = False
        elif character == "\\" and quote != "'":
            escaped = True
        elif quote:
            if character == quote:
                quote = None
        elif character in {"'", '"'}:
            quote = character
        elif character in {";", "|", "\n", "\r"}:
            segments.append(command[start:index].strip())
            if character == "|" and index + 1 < len(command) and command[index + 1] == "|":
                index += 1
            start = index + 1
        elif character == "&" and index + 1 < len(command) and command[index + 1] == "&":
            segments.append(command[start:index].strip())
            index += 1
            start = index + 1
        index += 1
    segments.append(command[start:].strip())
    return [segment for segment in segments if segment]


def has_unquoted_pipeline_or_redirection(command: str) -> bool:
    """Rejects context reads that transform or redirect a complete file stream."""
    quote: str | None = None
    escaped = False
    for index, character in enumerate(command):
        if escaped:
            escaped = False
        elif character == "\\" and quote != "'":
            escaped = True
        elif quote:
            if character == quote:
                quote = None
            elif quote == '"' and (character == "`" or (character == "$" and index + 1 < len(command) and command[index + 1] in {"(", "{"})):
                return True
        elif character in {"'", '"'}:
            quote = character
        elif character in {"|", ">", "<", "`"} or (character == "$" and index + 1 < len(command) and command[index + 1] in {"(", "{"}):
            return True
    return False


def test_roslynkit_core_invocation(command: str, resolved_roslynkit_path: str) -> bool:
    for segment in split_shell_segments(command):
        tokens = shell_tokens(segment)
        if not tokens:
            continue
        command_file, arguments = resolve_command_tokens(tokens)
        if command_file is None:
            continue
        candidates = {"roslynkit", "roslynkit.exe", "roslynkit-dev", "roslynkit-dev.exe"}
        resolved_name = Path(resolved_roslynkit_path.strip("\"'")).name
        candidates.add(resolved_name)
        if os.name == "nt":
            if command_file.lower() in {candidate.lower() for candidate in candidates}:
                return True
        elif command_file in candidates:
            return True
        if command_file in {"dotnet", "dotnet.exe"} and "run" in arguments:
            project_match = re.search(r"--project(?:=|\s+)(?:['\"]?)[^\s'\"]*src[\\/]RoslynKit(?:[\\/]|['\"]|\s)", segment)
            if project_match:
                return True
    return False


def test_roslynkit_invocation(command: str, resolved_roslynkit_path: str) -> bool:
    return any(test_roslynkit_core_invocation(payload, resolved_roslynkit_path) for payload in get_normalized_command_payloads(command))


def get_roslynkit_invocation_count(commands: Iterable[str], resolved_roslynkit_path: str) -> int:
    return sum(test_roslynkit_invocation(command, resolved_roslynkit_path) for command in commands)


def get_pattern_search_scope_arguments(
    arguments: list[str],
    options_with_values: set[str],
    pattern_options: set[str],
    modes_without_pattern: set[str] | None = None,
) -> list[str]:
    positionals: list[str] = []
    pattern_provided = False
    mode_without_pattern = False
    end_of_options = False
    index = 0
    while index < len(arguments):
        argument = arguments[index]
        if not end_of_options and argument == "--":
            end_of_options = True
            index += 1
            continue
        if not end_of_options and argument.startswith("-") and argument != "-":
            option_name = argument.split("=", 1)[0]
            has_inline_value = "=" in argument
            has_attached_pattern = any(
                re.fullmatch(r"-[A-Za-z]", option) is not None
                and argument.lower().startswith(option.lower()) and len(argument) > len(option)
                for option in pattern_options
            )
            if option_name in pattern_options or has_attached_pattern:
                pattern_provided = True
            if modes_without_pattern and option_name in modes_without_pattern:
                mode_without_pattern = True
            if not has_inline_value and option_name in options_with_values and index + 1 < len(arguments):
                index += 1
            index += 1
            continue
        positionals.append(argument)
        index += 1
    if pattern_provided or mode_without_pattern:
        return positionals
    return positionals[1:] if len(positionals) > 1 else []


def test_is_repository_root_scope(scope: str, repo_root: Path | None) -> bool:
    normalized = remove_command_envelope_quotes(scope).strip().replace("\\", "/")
    if normalized in {".", "./", "$PWD", "${PWD}"}:
        return True
    if repo_root is None:
        return False
    if normalized.startswith("FileSystem::"):
        normalized = normalized[len("FileSystem::"):]
    if normalized.startswith("~/"):
        normalized = str(Path.home() / normalized[2:])
    elif normalized == "~":
        normalized = str(Path.home())
    elif normalized.startswith("$HOME/"):
        normalized = str(Path.home() / normalized[6:])
    elif normalized == "$HOME":
        normalized = str(Path.home())
    if "$" in normalized or "`" in normalized:
        return False
    try:
        candidate = Path(normalized)
        candidate = candidate.resolve() if candidate.is_absolute() else (repo_root / candidate).resolve()
    except (OSError, RuntimeError):
        return False
    if os.name == "nt":
        return os.path.normcase(str(candidate)) == os.path.normcase(str(repo_root.resolve()))
    return candidate == repo_root.resolve()


def test_scopes_use_repository_root(scopes: Iterable[str], repo_root: Path | None) -> bool:
    scope_list = [scope for scope in scopes if scope.strip()]
    return not scope_list or any(test_is_repository_root_scope(scope, repo_root) for scope in scope_list)


def test_repository_root_recursive_search(command: str, repo_root: Path | None = None) -> bool:
    for payload in get_normalized_command_payloads(command):
        for segment in split_shell_segments(payload):
            tokens = shell_tokens(segment)
            if not tokens:
                continue
            executable, arguments = resolve_command_tokens(tokens)
            if executable is None:
                continue
            if executable in {"rg", "rg.exe", "ripgrep", "ripgrep.exe"}:
                if any(argument in {"--help", "-h", "--version", "-V", "--type-list", "--pcre2-version"} for argument in arguments):
                    continue
                scopes = get_pattern_search_scope_arguments(
                    arguments,
                    {"-A", "--after-context", "-B", "--before-context", "-C", "--context", "--colors", "--context-separator", "--dfa-size-limit", "-E", "--encoding", "--engine", "-e", "--regexp", "-f", "--file", "--field-context-separator", "--field-match-separator", "-g", "--glob", "--iglob", "--ignore-file", "-j", "--threads", "-M", "--max-columns", "-m", "--max-count", "--max-depth", "--max-filesize", "--path-separator", "--pre", "--pre-glob", "-r", "--replace", "--regex-size-limit", "--sort", "--sortr", "-t", "--type", "-T", "--type-not", "--type-add", "--type-clear"},
                    {"-e", "--regexp", "-f", "--file"}, {"--files"},
                )
                if test_scopes_use_repository_root(scopes, repo_root):
                    return True
            elif executable in {"grep", "grep.exe", "egrep", "egrep.exe", "fgrep", "fgrep.exe"} and any(
                argument == "--recursive" or re.fullmatch(r"-[^-]*[rR][^-]*", argument) for argument in arguments
            ):
                scopes = get_pattern_search_scope_arguments(
                    arguments,
                    {"-A", "--after-context", "-B", "--before-context", "-C", "--context", "-D", "--devices", "-d", "--directories", "-e", "--regexp", "-f", "--file", "--exclude", "--exclude-from", "--exclude-dir", "--group-separator", "--include", "-m", "--max-count"},
                    {"-e", "--regexp", "-f", "--file"},
                )
                if test_scopes_use_repository_root(scopes, repo_root):
                    return True
            elif executable in {"fd", "fd.exe", "fdfind", "fdfind.exe"}:
                if any(argument in {"--help", "-h", "--version", "-V"} for argument in arguments):
                    continue
                scopes = get_pattern_search_scope_arguments(
                    arguments,
                    {"-d", "--max-depth", "--min-depth", "--exact-depth", "-E", "--exclude", "-e", "--extension", "-g", "--glob", "-j", "--threads", "--max-buffer-time", "--path-separator", "--search-path", "-t", "--type"}, set(),
                )
                if test_scopes_use_repository_root(scopes, repo_root):
                    return True
            elif executable == "find" and not any(argument in {"--help", "--version"} for argument in arguments):
                scopes = []
                for argument in arguments:
                    if argument.startswith("-") or argument.startswith("!") or argument.startswith("("):
                        break
                    scopes.append(argument)
                if test_scopes_use_repository_root(scopes, repo_root):
                    return True
    return False


def test_command_references_context_path(command: str, context_path: str) -> bool:
    path_pattern = re.escape(context_path).replace("/", r"[\\/]")
    return re.search(rf"(?i)(?<![A-Za-z0-9_.-])(?:\.[\\/])?{path_pattern}(?![A-Za-z0-9_.\\/-])", command) is not None


def test_command_reads_context_path(command: str, context_path: str) -> bool:
    for payload in get_normalized_command_payloads(command):
        if not test_command_references_context_path(payload, context_path):
            continue
        if has_unquoted_pipeline_or_redirection(payload):
            continue
        for segment in split_shell_segments(payload):
            tokens = shell_tokens(segment)
            if not tokens or Path(tokens[0]).name.lower() not in {"cat", "cat.exe"}:
                continue
            if any(test_command_references_context_path(argument, context_path) for argument in tokens[1:]):
                return True
    return False


def test_concurrent_roslynkit_invocations(events: Iterable[dict[str, Any]], resolved_roslynkit_path: str) -> bool:
    active_commands: set[str] = set()
    for event in events:
        item = object_value(event, "item", {})
        if (
            event.get("type") not in {"item.started", "item.completed"}
            or object_value(item, "type") != "command_execution"
            or not test_roslynkit_invocation(str(object_value(item, "command", "")), resolved_roslynkit_path)
        ):
            continue
        item_id = str(object_value(item, "id", ""))
        if event["type"] == "item.started":
            if active_commands:
                return True
            if item_id:
                active_commands.add(item_id)
        elif item_id:
            active_commands.discard(item_id)
    return False


def command_executable(tokens: list[str]) -> str | None:
    """Returns the invoked executable after ordinary shell launcher prefixes."""
    executable, _ = resolve_command_tokens(tokens)
    return executable


def test_disallowed_shell_wrapper(command: str) -> bool:
    """Recognizes a real PowerShell or cmd command without scanning quoted text."""
    disallowed = {"pwsh", "pwsh.exe", "powershell", "powershell.exe", "cmd", "cmd.exe"}
    for payload in get_normalized_command_payloads(command):
        for segment in split_shell_segments(payload):
            tokens = shell_tokens(segment)
            if tokens and command_executable(tokens) in disallowed:
                return True
    return False


def test_forbidden_context_surface(
    condition: str,
    command: str,
    uses_roslynkit: bool,
    repo_root: Path | None = None,
    allowed_index_path: str = "./artifacts/roslynkit.db",
) -> bool:
    if test_repository_root_recursive_search(command, repo_root):
        return True
    normalized = command.replace("\\", "/")
    if test_disallowed_shell_wrapper(command):
        return True
    without_negative_globs = re.sub(r"![^\s]+", "", normalized)
    if re.search(
        r"(?i)(CODEX_HOME|\.codex|\.claude(?:/|$)|\.github/(?:skills(?:/|$)|copilot-instructions\.md)|AGENTS\.md|CLAUDE\.md|MEMORY\.md|rollout-|history\.jsonl|atlas-(?:csharp|doc|test)-mapper|benchmarks/|scripts/benchmark-codex(?:\.sh)?|artifacts/codex-benchmark(?:/|$)|docs/agents(?:/|$)|docs/local-repository-reference\.md|benchmark-codex|codex-cases\.json|token-efficiency-benchmark)",
        without_negative_globs,
    ):
        return True
    remaining = without_negative_globs
    for allowed_path in get_required_context_paths(condition):
        pattern = re.escape(allowed_path).replace("/", r"[\\/]")
        remaining = re.sub(rf"(?i)(?<![A-Za-z0-9_.-])(?:\.[\\/])?{pattern}(?![A-Za-z0-9_.\\/-])", "", remaining)
    if condition == "roslynkit" and uses_roslynkit:
        index_pattern = re.escape(allowed_index_path).replace("/", r"[\\/]")
        remaining = re.sub(rf"(?i)--index-path(?:\s+|=)['\"]?{index_pattern}['\"]?", "", remaining)
    return re.search(r"(?i)\.agents(?:/|$)|(?:^|[^A-Za-z0-9_.-])artifacts/|AGENTS\.md", remaining) is not None


def get_compliance_issues(
    condition: str,
    commands: Iterable[str],
    events: Iterable[dict[str, Any]],
    repository_changes: Iterable[str],
    resolved_roslynkit_path: str,
    repo_root: Path | None = None,
    allowed_index_path: str = "./artifacts/roslynkit.db",
) -> list[str]:
    command_list = list(commands)
    event_list = list(events)
    issues: list[str] = []
    roslynkit_invocation_count = get_roslynkit_invocation_count(command_list, resolved_roslynkit_path)
    observed_roslynkit = roslynkit_invocation_count > 0
    for command in command_list:
        uses_roslynkit = test_roslynkit_invocation(command, resolved_roslynkit_path)
        if condition == "raw-codex" and uses_roslynkit:
            issues.append(f"raw-codex invoked RoslynKit: {command}")
        if re.search(r"(?i)\b(?:curl|wget)\b|https?://", command):
            issues.append(f"used web or network access: {command}")
        if test_forbidden_context_surface(condition, command, uses_roslynkit, repo_root, allowed_index_path):
            issues.append(f"used forbidden context surface: {command}")
        if re.search(
            r"(?i)\b(?:apply_patch|tee|install|rm|mv|cp|touch|mkdir|truncate|dd)\b|>\s*[^&]|git\s+(?:add|commit|checkout|switch|reset|restore|clean|stash)\b|\b(?:dotnet|msbuild)\s+(?:build|test|restore|pack|run)\b",
            command,
        ):
            issues.append(f"attempted an edit: {command}")
    for event in event_list:
        item = object_value(event, "item", {})
        payload = object_value(event, "payload", {})
        event_surface = " ".join(
            str(value) for value in [event.get("type"), object_value(item, "type"), object_value(item, "name"), object_value(payload, "name")]
            if value is not None
        )
        if re.search(r"(?i)(web_search|browser|computer|mcp|atlas|scout|explorer|worker|subagent|multi_agent|spawn_agent|memory)", event_surface):
            issues.append(f"used forbidden event surface: {event_surface}")
        if (
            event.get("type") == "item.completed"
            and object_value(item, "type") == "command_execution"
            and (object_value(item, "status") != "completed" or object_value(item, "exit_code") != 0)
        ):
            command = re.sub(r"\s+", " ", str(object_value(item, "command", ""))).strip()
            if len(command) > 240:
                command = f"{command[:237]}..."
            issues.append(
                f"command failed (status={object_value(item, 'status')}, exit={object_value(item, 'exit_code')}): {command}"
            )
    if condition == "roslynkit" and not observed_roslynkit:
        issues.append("RoslynKit condition did not invoke RoslynKit")
    if condition == "roslynkit" and roslynkit_invocation_count > MAXIMUM_ROSLYNKIT_INVOCATIONS:
        issues.append(
            f"RoslynKit condition used {roslynkit_invocation_count} invocations; maximum is {MAXIMUM_ROSLYNKIT_INVOCATIONS}"
        )
    if condition == "roslynkit" and test_concurrent_roslynkit_invocations(event_list, resolved_roslynkit_path):
        issues.append("RoslynKit commands overlapped; run one invocation at a time")
    required_read_indices: dict[str, int] = {}
    for required_path in get_required_context_paths(condition):
        read_index = next(
            (index for index, command in enumerate(command_list) if test_command_reads_context_path(command, required_path)),
            -1,
        )
        required_read_indices[required_path] = read_index
        if read_index < 0:
            issues.append(f"did not read required context: {required_path}")
    benchmark_skill_path = ".agents/skills/benchmark/SKILL.md"
    if required_read_indices.get(benchmark_skill_path, -1) > 0:
        issues.append("benchmark skill was not read by the first command")
    if condition == "roslynkit":
        first_roslynkit_index = next(
            (index for index, command in enumerate(command_list) if test_roslynkit_invocation(command, resolved_roslynkit_path)),
            -1,
        )
        if first_roslynkit_index >= 0:
            for required_path, read_index in required_read_indices.items():
                if read_index >= first_roslynkit_index:
                    issues.append(f"required context was not read before RoslynKit invocation: {required_path}")
    if not command_list:
        issues.append("run recorded no inspection commands")
    changes = list(repository_changes)
    if changes:
        issues.append(f"repository content changed: {'; '.join(changes)}")
    return list(dict.fromkeys(issues))


def test_non_empty_file(path: Path) -> bool:
    return path.is_file() and bool(path.read_text(encoding="utf-8", errors="replace").strip())


def get_benchmark_host_kind() -> str:
    if sys.platform == "win32":
        return "windows-git-bash"
    is_wsl = bool(os.environ.get("WSL_DISTRO_NAME") or os.environ.get("WSL_INTEROP"))
    osrelease = Path("/proc/sys/kernel/osrelease")
    if not is_wsl and osrelease.is_file():
        is_wsl = bool(re.search(r"microsoft|wsl", osrelease.read_text(encoding="utf-8", errors="ignore"), re.IGNORECASE))
    if is_wsl:
        if os.environ.get("TERM_PROGRAM", "").lower() == "vscode" or os.environ.get("VSCODE_IPC_HOOK_CLI") or os.environ.get("VSCODE_GIT_IPC_HANDLE"):
            return "wsl-vscode-remote"
        return "wsl"
    if sys.platform.startswith("linux"):
        return "linux"
    if sys.platform == "darwin":
        return "macos"
    return "unknown"


def invoke_tool_version_probe(command_name: str, executable_path: str | None = None) -> dict[str, Any]:
    resolved_path = executable_path or shutil.which(command_name)
    if resolved_path is None:
        return {"resolved_path": None, "output": f"The '{command_name}' application was not found on PATH.", "exit_code": 127}
    try:
        resolved_path = str(Path(resolved_path).resolve())
        completed = subprocess.run([resolved_path, "--version"], capture_output=True, text=True, check=False)
        output = "\n".join(part for part in [completed.stdout.strip(), completed.stderr.strip()] if part)
        return {
            "resolved_path": resolved_path,
            "output": output,
            "version_output": output,
            "executable_sha256": hashlib.sha256(Path(resolved_path).read_bytes()).hexdigest(),
            "exit_code": completed.returncode,
        }
    except OSError as error:
        return {"resolved_path": str(Path(resolved_path).resolve()), "output": str(error), "exit_code": 126}


def write_internal_tool_probe(output: Path, roslynkit_path: str | None = None) -> None:
    output = output.resolve()
    if output.parent == output:
        raise BenchmarkError("The internal tool-probe path must have a parent directory.")
    output.parent.mkdir(parents=True, exist_ok=True)
    probe = {
        "schema_version": 1, "generated_at_utc": utc_now(), "host_kind": get_benchmark_host_kind(),
        "ripgrep": invoke_tool_version_probe("rg"), "roslynkit": invoke_tool_version_probe("roslynkit", roslynkit_path),
    }
    output.write_text(json.dumps(probe, indent=2) + "\n", encoding="utf-8")


def get_tool_probe_validation_issues(probe: Any) -> list[str]:
    issues: list[str] = []
    if not isinstance(probe, dict):
        return ["tool probe was missing"]
    if probe.get("schema_version") != 1:
        issues.append("schema_version was not 1")
    host_kind = probe.get("host_kind")
    valid_hosts = {"windows-git-bash", "wsl", "wsl-vscode-remote", "linux", "macos", "unknown"}
    if host_kind not in valid_hosts:
        issues.append("host_kind was missing or unsupported")
    elif host_kind != get_benchmark_host_kind():
        issues.append("host_kind did not match the controller host")
    for name, pattern in [("ripgrep", r"(?im)^(?:ripgrep|rg)\s+\d"), ("roslynkit", r"(?i)roslynkit version")]:
        tool = probe.get(name)
        if not isinstance(tool, dict):
            issues.append(f"{name} probe was missing")
            continue
        resolved_path = tool.get("resolved_path")
        output = tool.get("output")
        version_output = tool.get("version_output")
        executable_sha256 = tool.get("executable_sha256")
        exit_code = tool.get("exit_code")
        if not isinstance(resolved_path, str) or not Path(resolved_path).is_file():
            issues.append(f"{name} resolved path was missing" if not resolved_path else f"{name} resolved path was not a file")
        if isinstance(exit_code, bool) or not isinstance(exit_code, int) or exit_code != 0:
            issues.append(f"{name} exit code was not zero")
        if not isinstance(output, str) or not re.search(pattern, output):
            issues.append(f"{name} version output was invalid")
        if version_output != output:
            issues.append(f"{name} version output record was missing or did not match")
        if not isinstance(executable_sha256, str) or not re.fullmatch(r"[0-9a-f]{64}", executable_sha256):
            issues.append(f"{name} executable SHA-256 was missing or invalid")
    return issues


def read_validated_tool_probe(path: Path) -> dict[str, Any]:
    if not path.is_file():
        raise BenchmarkError(f"The child tool-probe artifact was not written: '{path}'.")
    try:
        probe = json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        raise BenchmarkError(f"The child tool-probe artifact was not valid JSON: '{path}'.") from error
    issues = get_tool_probe_validation_issues(probe)
    if issues:
        raise BenchmarkError(f"The child tool-probe artifact was invalid: {'; '.join(issues)}.")
    return probe


def test_single_successful_command_event(events: Iterable[dict[str, Any]]) -> bool:
    completed_commands = [
        event for event in events
        if event.get("type") == "item.completed" and object_value(event.get("item"), "type") == "command_execution"
    ]
    return (
        len(completed_commands) == 1
        and object_value(completed_commands[0].get("item"), "status") == "completed"
        and object_value(completed_commands[0].get("item"), "exit_code") == 0
    )


def get_repository_content_manifest(repo_root: Path) -> list[dict[str, Any]]:
    completed = subprocess.run(
        ["git", "-C", str(repo_root), "ls-files", "--cached", "--others", "--exclude-standard"],
        capture_output=True,
        text=True,
        check=False,
    )
    if completed.returncode != 0:
        raise BenchmarkError("Could not list nonignored repository files for the content manifest.")
    entries: list[dict[str, Any]] = []
    for relative_path in completed.stdout.splitlines():
        full_path = repo_root / relative_path
        exists = full_path.is_file()
        entries.append({
            "path": relative_path.replace("\\", "/"), "exists": exists,
            "sha256": hashlib.sha256(full_path.read_bytes()).hexdigest() if exists else None,
        })
    return sorted(entries, key=lambda entry: entry["path"])


def repository_manifest_records(entries: Iterable[dict[str, Any]]) -> set[str]:
    return {f"{entry.get('path')}|{entry.get('exists')}|{entry.get('sha256')}" for entry in entries}


def get_repository_content_changes(repo_root: Path, baseline: Iterable[dict[str, Any]]) -> list[str]:
    baseline_records = repository_manifest_records(baseline)
    current_records = repository_manifest_records(get_repository_content_manifest(repo_root))
    return sorted(baseline_records.symmetric_difference(current_records))


def get_median(values: Iterable[Any]) -> float | None:
    filtered = [float(value) for value in values if value is not None]
    return statistics.median(filtered) if filtered else None


def format_metric(value: Any) -> str:
    return "" if value is None else f"{float(value):.2f}".rstrip("0").rstrip(".")


def format_currency(value: Any) -> str:
    return "" if value is None else f"${float(value):.6f}"


def format_percent(value: Any) -> str:
    return "" if value is None else f"{format_metric(value)}%"


def get_savings_percent(raw: Any, roslynkit: Any) -> float | None:
    if raw is None or roslynkit is None or raw <= 0:
        return None
    return 100.0 * (raw - roslynkit) / raw


def sync_review_results(run_root: Path, rows: list[dict[str, Any]], cases: list[dict[str, Any]]) -> list[dict[str, Any]]:
    path = run_root / "review-results.json"
    existing_by_run_id: dict[str, dict[str, Any]] = {}
    if path.is_file():
        try:
            document = json.loads(path.read_text(encoding="utf-8"))
        except json.JSONDecodeError as error:
            raise BenchmarkError(f"Review results were not valid JSON: {path}") from error
        if document.get("schema_version") != 1:
            raise BenchmarkError(f"Review results must use schema_version 1: {path}")
        existing_by_run_id = {
            entry.get("run_id"): entry for entry in document.get("runs", [])
            if isinstance(entry, dict) and entry.get("run_id")
        }
    cases_by_id = {case["id"]: case for case in cases}
    entries: list[dict[str, Any]] = []
    statuses = {"pass", "fail", "not_evaluated"}
    for row in rows:
        run_id = row.get("run_id") or f"{row.get('case_id')}-{row.get('condition')}-trial{row.get('trial')}"
        case = cases_by_id.get(row.get("case_id"))
        if case is None:
            raise BenchmarkError(f"Review row references an unknown benchmark case: {row.get('case_id')}")
        existing = existing_by_run_id.get(run_id, {})
        existing_criteria = {
            criterion.get("id"): criterion for criterion in existing.get("criteria", [])
            if isinstance(criterion, dict) and criterion.get("id")
        }
        criteria = []
        for index, text in enumerate(case.get("manualReviewCriteria", []), start=1):
            criterion_id = f"criterion-{index}"
            prior = existing_criteria.get(criterion_id, {})
            status = prior.get("status") or "not_evaluated"
            if status not in statuses:
                raise BenchmarkError(f"Review criterion '{run_id}/{criterion_id}' had unsupported status '{status}'.")
            criteria.append({"id": criterion_id, "text": text, "status": status, "evidence": prior.get("evidence", "")})
        overall_status = existing.get("overall_status") or "not_evaluated"
        if overall_status not in statuses:
            raise BenchmarkError(f"Review '{run_id}' had unsupported overall_status '{overall_status}'.")
        if overall_status == "pass" and any(criterion["status"] != "pass" for criterion in criteria):
            raise BenchmarkError(f"Review '{run_id}' cannot pass until every criterion passes.")
        entries.append({
            "run_id": run_id, "case_id": row.get("case_id"), "condition": row.get("condition"), "trial": row.get("trial"),
            "overall_status": overall_status, "reviewer": existing.get("reviewer", ""),
            "reviewed_at_utc": existing.get("reviewed_at_utc"), "notes": existing.get("notes", ""), "criteria": criteria,
        })
    document = {
        "schema_version": 1,
        "instructions": "Set every criterion and overall_status to pass or fail. Cost-per-correct-answer comparisons use only operationally valid runs with overall_status=pass.",
        "runs": entries,
    }
    path.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    return entries


def median_for(rows: Iterable[dict[str, Any]], name: str) -> float | None:
    return get_median(row.get(name) for row in rows)


def write_reports(run_root: Path, rows: list[dict[str, Any]], cases: list[dict[str, Any]]) -> None:
    run_root.mkdir(parents=True, exist_ok=True)
    field_names = list(dict.fromkeys(key for row in rows for key in row))
    with (run_root / "runs.csv").open("w", newline="", encoding="utf-8") as stream:
        writer = csv.DictWriter(stream, fieldnames=field_names)
        writer.writeheader()
        writer.writerows(rows)
    (run_root / "runs.json").write_text(json.dumps(rows, indent=2) + "\n", encoding="utf-8")
    review_results = sync_review_results(run_root, rows, cases)
    review_by_run_id = {result["run_id"]: result for result in review_results}
    summary = [
        "# Codex Benchmark", "",
        f"GPT-5.6 cost values are Standard API short-context projections using prices verified {PRICING_VERIFIED_DATE} from [{PRICING_SOURCE}]({PRICING_SOURCE}). They are not a claim about the active Codex account's bill.", "",
        "`codex exec --json` exposes one cumulative completed-turn total, not usage for each underlying model request. Request-level 272K threshold metrics and exact long-context cost are therefore unavailable in this runner version.", "",
        "## By Case And Condition", "",
        "| Case | Condition | Valid | Correct | Pending review | Median input | Cached | Cache write | Regular uncached | Output | Reasoning output | Cache rate | Turns | Tool calls | RoslynKit calls | Duration (s) |",
        "| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
    ]
    group_keys = sorted({(row.get("case_id"), row.get("condition")) for row in rows})
    valid_rows_all = [row for row in rows if row.get("valid") and row.get("input_tokens") is not None]
    for case_id, condition in group_keys:
        group_rows = [row for row in rows if row.get("case_id") == case_id and row.get("condition") == condition]
        valid_rows = [row for row in group_rows if row.get("valid") and row.get("input_tokens") is not None]
        correct_rows = [row for row in valid_rows if review_by_run_id.get(row.get("run_id"), {}).get("overall_status") == "pass"]
        pending_rows = [row for row in valid_rows if review_by_run_id.get(row.get("run_id"), {}).get("overall_status") == "not_evaluated"]
        summary.append(
            "| {case_id} | {condition} | {valid} | {correct} | {pending} | {input} | {cached} | {cache_write} | {regular} | {output} | {reasoning} | {cache_rate} | {turns} | {tool_calls} | {roslynkit_calls} | {duration} |".format(
                case_id=case_id, condition=condition, valid=len(valid_rows), correct=len(correct_rows), pending=len(pending_rows),
                input=format_metric(median_for(valid_rows, "input_tokens")), cached=format_metric(median_for(valid_rows, "cached_input_tokens")),
                cache_write=format_metric(median_for(valid_rows, "cache_write_input_tokens")), regular=format_metric(median_for(valid_rows, "regular_uncached_input_tokens")),
                output=format_metric(median_for(valid_rows, "output_tokens")), reasoning=format_metric(median_for(valid_rows, "reasoning_output_tokens")),
                cache_rate=format_percent(median_for(valid_rows, "cache_hit_rate_pct")), turns=format_metric(median_for(valid_rows, "model_turn_count")),
                tool_calls=format_metric(median_for(valid_rows, "tool_call_count")), roslynkit_calls=format_metric(median_for(valid_rows, "roslynkit_invocation_count")),
                duration=format_metric(median_for(valid_rows, "duration_seconds")),
            )
        )
    reviewed_correct = [
        row for row in valid_rows_all if review_by_run_id.get(row.get("run_id"), {}).get("overall_status") == "pass"
    ]
    summary.extend([
        "", "## Cost Per Correct Answer", "",
        "Only operationally valid runs marked `pass` in `review-results.json` appear here.", "",
        "| Case | Model | Raw correct | RoslynKit correct | Raw median projected cost | RoslynKit median projected cost | Cost savings | Raw short/all-long range | RoslynKit short/all-long range |",
        "| --- | --- | ---: | ---: | ---: | ---: | ---: | --- | --- |",
    ])
    for case in cases:
        case_id = case["id"]
        models = list(dict.fromkeys(row.get("model") for row in rows if row.get("case_id") == case_id))
        for model in models:
            raw = [row for row in reviewed_correct if row.get("case_id") == case_id and row.get("condition") == "raw-codex" and row.get("model") == model]
            roslynkit = [row for row in reviewed_correct if row.get("case_id") == case_id and row.get("condition") == "roslynkit" and row.get("model") == model]
            raw_cost = median_for(raw, "selected_model_short_context_cost_usd")
            roslynkit_cost = median_for(roslynkit, "selected_model_short_context_cost_usd")
            raw_long = median_for(raw, "selected_model_all_long_context_cost_usd")
            roslynkit_long = median_for(roslynkit, "selected_model_all_long_context_cost_usd")
            summary.append(
                f"| {case_id} | {model} | {len(raw)} | {len(roslynkit)} | {format_currency(raw_cost)} | {format_currency(roslynkit_cost)} | {format_percent(get_savings_percent(raw_cost, roslynkit_cost))} | {format_currency(raw_cost)}–{format_currency(raw_long)} | {format_currency(roslynkit_cost)}–{format_currency(roslynkit_long)} |"
            )
    summary.extend([
        "", "## Token Savings For Correct Answers", "",
        "| Case | Model | Input | Cached input | Cache writes | Regular uncached input | Output |",
        "| --- | --- | ---: | ---: | ---: | ---: | ---: |",
    ])
    for case in cases:
        case_id = case["id"]
        models = list(dict.fromkeys(row.get("model") for row in rows if row.get("case_id") == case_id))
        for model in models:
            raw = [row for row in reviewed_correct if row.get("case_id") == case_id and row.get("condition") == "raw-codex" and row.get("model") == model]
            roslynkit = [row for row in reviewed_correct if row.get("case_id") == case_id and row.get("condition") == "roslynkit" and row.get("model") == model]
            savings = [
                format_percent(get_savings_percent(median_for(raw, name), median_for(roslynkit, name)))
                for name in ["input_tokens", "cached_input_tokens", "cache_write_input_tokens", "regular_uncached_input_tokens", "output_tokens"]
            ]
            summary.append(f"| {case_id} | {model} | {' | '.join(savings)} |")
    summary.extend([
        "", "## GPT-5.6 Standard Cost Projections For Correct Runs", "",
        "These projections apply each model's price to the measured token profile; they do not predict how another model would navigate the task.", "",
        "| Case | Condition | Executed model | Sol | Terra | Luna |",
        "| --- | --- | --- | ---: | ---: | ---: |",
    ])
    cost_groups = sorted({(row.get("case_id"), row.get("condition"), row.get("model")) for row in reviewed_correct})
    for case_id, condition, model in cost_groups:
        group = [row for row in reviewed_correct if (row.get("case_id"), row.get("condition"), row.get("model")) == (case_id, condition, model)]
        summary.append(
            f"| {case_id} | {condition} | {model} | {format_currency(median_for(group, 'sol_short_context_cost_usd'))} | {format_currency(median_for(group, 'terra_short_context_cost_usd'))} | {format_currency(median_for(group, 'luna_short_context_cost_usd'))} |"
        )
    if not cost_groups:
        summary.append("| — | — | — | — | — | — |")
    summary.extend([
        "", "## Sol Standard Cost Breakdown For Correct Runs", "",
        "| Case | Condition | Executed model | Regular uncached input | Cached input | Cache writes | Output | Total |",
        "| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |",
    ])
    for case_id, condition, model in cost_groups:
        group = [row for row in reviewed_correct if (row.get("case_id"), row.get("condition"), row.get("model")) == (case_id, condition, model)]
        summary.append(
            f"| {case_id} | {condition} | {model} | {format_currency(median_for(group, 'sol_regular_uncached_input_cost_usd'))} | {format_currency(median_for(group, 'sol_cached_input_cost_usd'))} | {format_currency(median_for(group, 'sol_cache_write_cost_usd'))} | {format_currency(median_for(group, 'sol_output_cost_usd'))} | {format_currency(median_for(group, 'sol_short_context_cost_usd'))} |"
        )
    if not cost_groups:
        summary.append("| — | — | — | — | — | — | — | — |")
    invalid = [row for row in rows if not row.get("valid")]
    summary.extend(["", "## Invalid Runs", ""])
    if not invalid:
        summary.append("None.")
    else:
        summary.extend(["| Case | Condition | Trial | Exit | Issues |", "| --- | --- | ---: | ---: | --- |"])
        summary.extend(
            f"| {row.get('case_id')} | {row.get('condition')} | {row.get('trial')} | {row.get('exit_code')} | {str(row.get('issues', '')).replace('|', '/')} |"
            for row in invalid
        )
    (run_root / "summary.md").write_text("\n".join(summary) + "\n", encoding="utf-8")
    review = [
        "# Manual Review", "",
        "Record criterion results and the overall result in `review-results.json`, then rerun the report-only command shown below. These criteria are never included in child prompts.", "",
        "```bash", f"bash ./scripts/benchmark-codex.sh --report-run-root '{output_path(run_root)}'", "```",
    ]
    for case in cases:
        review.extend(["", f"## {case['id']}", ""])
        review.extend(f"- {criterion}" for criterion in case.get("manualReviewCriteria", []))
        for row in [row for row in rows if row.get("case_id") == case["id"]]:
            status = review_by_run_id.get(row.get("run_id"), {}).get("overall_status", "not_evaluated")
            review.append(f"- {row.get('condition')} trial {row.get('trial')}: {row.get('answer_path')} (valid: {row.get('valid')}; review: {status})")
    (run_root / "review.md").write_text("\n".join(review) + "\n", encoding="utf-8")


def evaluate_benchmark_run(
    case_id: str,
    condition: str,
    trial: int,
    repo_root: Path,
    repository_manifest: list[dict[str, Any]],
    answer_path: Path,
    event_path: Path,
    stderr_path: Path,
    commands_path: Path,
    resolved_roslynkit_path: str,
    index_path: str,
    model: str,
    reasoning_effort: str,
    exit_code: int,
    duration_seconds: float,
) -> dict[str, Any]:
    """Evaluates one Bash-executed Codex session without starting a process."""
    event_log = read_event_log(event_path)
    events = event_log["events"]
    commands = get_commands(events)
    commands_path.write_text("\n".join(commands) + ("\n" if commands else ""), encoding="utf-8")
    accounting = get_token_accounting(events)
    usage = accounting["usage"]
    repository_changes = get_repository_content_changes(repo_root, repository_manifest)
    issues = get_compliance_issues(
        condition, commands, events, repository_changes, resolved_roslynkit_path, repo_root, index_path
    ) + event_log["issues"] + accounting["issues"]
    if not test_non_empty_file(answer_path):
        issues.append("no final answer was written")
    issues = list(dict.fromkeys(issues))
    usage = usage or {}
    input_tokens = usage.get("input_tokens")
    cost_projections = {
        name: get_gpt56_cost_projection(usage if usage else None, name, "short")
        for name in ["gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna"]
    }
    selected_short_cost = get_gpt56_cost_projection(usage if usage else None, model, "short")
    selected_long_cost = get_gpt56_cost_projection(usage if usage else None, model, "long")
    run_id = f"{case_id}-{condition}-trial{trial}"
    return {
        "runner": "bash", "timestamp_utc": utc_now(), "run_id": run_id, "case_id": case_id, "condition": condition, "trial": trial,
        "model": model, "reasoning_effort": reasoning_effort,
        "valid": exit_code == 0 and input_tokens is not None and not issues,
        "exit_code": exit_code, "duration_seconds": duration_seconds,
        "input_tokens": input_tokens, "cached_input_tokens": usage.get("cached_input_tokens"),
        "cache_write_input_tokens": usage.get("cache_write_input_tokens"), "uncached_input_tokens": usage.get("uncached_input_tokens"),
        "regular_uncached_input_tokens": usage.get("regular_uncached_input_tokens"), "output_tokens": usage.get("output_tokens"),
        "reasoning_output_tokens": usage.get("reasoning_output_tokens"),
        "cache_hit_rate_pct": round(100.0 * usage["cached_input_tokens"] / input_tokens, 4)
        if input_tokens and usage.get("cached_input_tokens") is not None else None,
        "model_turn_count": sum(event.get("type") == "turn.started" for event in events),
        "tool_call_count": len(commands), "command_count": len(commands),
        "roslynkit_invocation_count": get_roslynkit_invocation_count(commands, resolved_roslynkit_path),
        "usage_source": accounting["usage_source"], "usage_scope": accounting["usage_scope"],
        "request_usage_available": accounting["request_usage_available"],
        "max_request_input_tokens": accounting["max_request_input_tokens"],
        "requests_over_272k": accounting["requests_over_long_context_threshold"],
        "long_context_pricing_status": "unknown_request_level_usage_unavailable",
        "selected_model_short_context_cost_usd": object_value(selected_short_cost, "total_cost_usd"),
        "selected_model_all_long_context_cost_usd": object_value(selected_long_cost, "total_cost_usd"),
        "cost_projection_status": object_value(selected_short_cost, "status"),
        "sol_short_context_cost_usd": object_value(cost_projections["gpt-5.6-sol"], "total_cost_usd"),
        "terra_short_context_cost_usd": object_value(cost_projections["gpt-5.6-terra"], "total_cost_usd"),
        "luna_short_context_cost_usd": object_value(cost_projections["gpt-5.6-luna"], "total_cost_usd"),
        "sol_regular_uncached_input_cost_usd": object_value(cost_projections["gpt-5.6-sol"], "regular_uncached_input_cost_usd"),
        "sol_cached_input_cost_usd": object_value(cost_projections["gpt-5.6-sol"], "cached_input_cost_usd"),
        "sol_cache_write_cost_usd": object_value(cost_projections["gpt-5.6-sol"], "cache_write_cost_usd"),
        "sol_output_cost_usd": object_value(cost_projections["gpt-5.6-sol"], "output_cost_usd"),
        "pricing_source": PRICING_SOURCE, "pricing_verified_date": PRICING_VERIFIED_DATE,
        "issues": " | ".join(issues), "answer_path": output_path(answer_path), "events_path": output_path(event_path),
        "events_sha256": hashlib.sha256(event_path.read_bytes()).hexdigest() if event_path.is_file() else None,
        "stderr_path": output_path(stderr_path), "commands_path": output_path(commands_path),
    }


def internal_tool_probe_main(arguments: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="benchmark_codex_support.py internal-tool-probe")
    parser.add_argument("--output", required=True)
    parser.add_argument("--roslynkit-path")
    options = parser.parse_args(arguments)
    write_internal_tool_probe(Path(options.output), options.roslynkit_path)
    return 0


def write_reports_main(arguments: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="benchmark_codex_support.py write-reports")
    parser.add_argument("--run-root", required=True)
    parser.add_argument("--cases-path", required=True)
    options = parser.parse_args(arguments)
    run_root = Path(options.run_root).resolve()
    try:
        rows = json.loads((run_root / "runs.json").read_text(encoding="utf-8"))
        cases = json.loads(Path(options.cases_path).read_text(encoding="utf-8")).get("cases", [])
    except json.JSONDecodeError as error:
        raise BenchmarkError("Report input was not valid JSON.") from error
    if not isinstance(rows, list) or not isinstance(cases, list):
        raise BenchmarkError("Report input had an unsupported shape.")
    row_case_ids = {row.get("case_id") for row in rows if isinstance(row, dict)}
    write_reports(run_root, rows, [case for case in cases if case.get("id") in row_case_ids])
    return 0


def validate_event_log_main(arguments: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="benchmark_codex_support.py validate-event-log")
    parser.add_argument("--event-path", required=True)
    parser.add_argument("--condition", required=True, choices=["raw-codex", "roslynkit"])
    parser.add_argument("--repo-root", required=True)
    parser.add_argument("--roslynkit-path", required=True)
    parser.add_argument("--index-path", default="./artifacts/roslynkit.db")
    options = parser.parse_args(arguments)
    event_log = read_event_log(Path(options.event_path))
    commands = get_commands(event_log["events"])
    result = {
        "commands": commands,
        "issues": get_compliance_issues(
            options.condition, commands, event_log["events"], [], options.roslynkit_path,
            Path(options.repo_root).resolve(), options.index_path,
        ) + event_log["issues"],
        "accounting": get_token_accounting(event_log["events"]),
    }
    print(json.dumps(result, indent=2))
    return 0


def case_list_main(arguments: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="benchmark_codex_support.py case-list")
    parser.add_argument("--repo-root", required=True)
    parser.add_argument("--case-id", default="all")
    options = parser.parse_args(arguments)
    cases = get_selected_cases(get_case_data(Path(options.repo_root).resolve()), options.case_id)
    for case in cases:
        sys.stdout.buffer.write(case["id"].encode("utf-8") + b"\0" + case["prompt"].encode("utf-8") + b"\0")
    return 0


def render_prompt_main(arguments: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="benchmark_codex_support.py render-prompt")
    parser.add_argument("--condition", required=True, choices=["raw-codex", "roslynkit"])
    parser.add_argument("--index-path", default="./artifacts/roslynkit.db")
    parser.add_argument("--prompt", required=True)
    options = parser.parse_args(arguments)
    print(new_condition_prompt(options.condition, options.prompt, options.index_path))
    return 0


def normalize_index_path_main(arguments: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="benchmark_codex_support.py normalize-index-path")
    parser.add_argument("--repo-root", required=True)
    parser.add_argument("--index-path", required=True)
    options = parser.parse_args(arguments)
    print(resolve_benchmark_index_path(Path(options.repo_root).resolve(), options.index_path))
    return 0


def manifest_main(arguments: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="benchmark_codex_support.py manifest")
    parser.add_argument("--repo-root", required=True)
    parser.add_argument("--output", required=True)
    options = parser.parse_args(arguments)
    Path(options.output).write_text(
        json.dumps(get_repository_content_manifest(Path(options.repo_root).resolve()), indent=2) + "\n",
        encoding="utf-8",
    )
    return 0


def manifest_changes_main(arguments: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="benchmark_codex_support.py manifest-changes")
    parser.add_argument("--repo-root", required=True)
    parser.add_argument("--baseline", required=True)
    options = parser.parse_args(arguments)
    try:
        baseline = json.loads(Path(options.baseline).read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        raise BenchmarkError(f"Repository manifest was not valid JSON: {options.baseline}") from error
    if not isinstance(baseline, list):
        raise BenchmarkError(f"Repository manifest was not an array: {options.baseline}")
    print(json.dumps(get_repository_content_changes(Path(options.repo_root).resolve(), baseline)))
    return 0


def validate_preflight_main(arguments: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="benchmark_codex_support.py validate-preflight")
    parser.add_argument("--event-path", required=True)
    parser.add_argument("--probe-path", required=True)
    parser.add_argument("--commands-path", required=True)
    options = parser.parse_args(arguments)
    event_log = read_event_log(Path(options.event_path))
    commands = get_commands(event_log["events"])
    Path(options.commands_path).write_text("\n".join(commands) + ("\n" if commands else ""), encoding="utf-8")
    if event_log["issues"] or not test_single_successful_command_event(event_log["events"]):
        raise BenchmarkError("Benchmark preflight child event log was invalid.")
    read_validated_tool_probe(Path(options.probe_path))
    return 0


def probe_path_main(arguments: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="benchmark_codex_support.py probe-path")
    parser.add_argument("--probe-path", required=True)
    options = parser.parse_args(arguments)
    probe = read_validated_tool_probe(Path(options.probe_path))
    print(probe["roslynkit"]["resolved_path"])
    return 0


def monotonic_main(arguments: list[str]) -> int:
    if arguments:
        raise BenchmarkError("monotonic does not accept arguments.")
    print(f"{time.monotonic():.9f}")
    return 0


def elapsed_main(arguments: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="benchmark_codex_support.py elapsed")
    parser.add_argument("--started-at", required=True, type=float)
    options = parser.parse_args(arguments)
    print(f"{max(0.0, time.monotonic() - options.started_at):.3f}")
    return 0


def evaluate_run_main(arguments: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="benchmark_codex_support.py evaluate-run")
    parser.add_argument("--case-id", required=True)
    parser.add_argument("--condition", required=True, choices=["raw-codex", "roslynkit"])
    parser.add_argument("--trial", required=True, type=int)
    parser.add_argument("--repo-root", required=True)
    parser.add_argument("--manifest-path", required=True)
    parser.add_argument("--answer-path", required=True)
    parser.add_argument("--event-path", required=True)
    parser.add_argument("--stderr-path", required=True)
    parser.add_argument("--commands-path", required=True)
    parser.add_argument("--roslynkit-path", required=True)
    parser.add_argument("--index-path", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--reasoning-effort", required=True)
    parser.add_argument("--exit-code", required=True, type=int)
    parser.add_argument("--duration-seconds", required=True, type=float)
    parser.add_argument("--output", required=True)
    options = parser.parse_args(arguments)
    try:
        manifest = json.loads(Path(options.manifest_path).read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        raise BenchmarkError(f"Repository manifest was not valid JSON: {options.manifest_path}") from error
    row = evaluate_benchmark_run(
        options.case_id, options.condition, options.trial, Path(options.repo_root).resolve(), manifest,
        Path(options.answer_path), Path(options.event_path), Path(options.stderr_path), Path(options.commands_path),
        options.roslynkit_path, options.index_path, options.model, options.reasoning_effort,
        options.exit_code, options.duration_seconds,
    )
    Path(options.output).write_text(json.dumps(row, indent=2) + "\n", encoding="utf-8")
    return 0


def append_run_main(arguments: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="benchmark_codex_support.py append-run")
    parser.add_argument("--run-root", required=True)
    parser.add_argument("--row-path", required=True)
    options = parser.parse_args(arguments)
    runs_path = Path(options.run_root) / "runs.json"
    try:
        rows = json.loads(runs_path.read_text(encoding="utf-8"))
        row = json.loads(Path(options.row_path).read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        raise BenchmarkError("Benchmark run data was not valid JSON.") from error
    if not isinstance(rows, list) or not isinstance(row, dict):
        raise BenchmarkError("Benchmark run data had an unsupported shape.")
    rows.append(row)
    runs_path.write_text(json.dumps(rows, indent=2) + "\n", encoding="utf-8")
    return 0


def report_main(arguments: list[str]) -> int:
    parser = argparse.ArgumentParser(prog="benchmark_codex_support.py report")
    parser.add_argument("--repo-root", required=True)
    parser.add_argument("--run-root", required=True)
    options = parser.parse_args(arguments)
    repo_root = Path(options.repo_root).resolve()
    run_root = resolve_benchmark_report_run_root(repo_root, options.run_root)
    try:
        rows = json.loads((run_root / "runs.json").read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        raise BenchmarkError(f"Report run data was not valid JSON: {run_root / 'runs.json'}") from error
    if not isinstance(rows, list):
        raise BenchmarkError(f"Report run data was not an array: {run_root / 'runs.json'}")
    if not rows or any(not isinstance(row, dict) or row.get("runner") != "bash" for row in rows):
        raise BenchmarkError("--report-run-root accepts only Bash-runner artifacts with runner='bash'.")
    all_cases = get_case_data(repo_root)
    case_ids = {row.get("case_id") for row in rows if isinstance(row, dict)}
    write_reports(run_root, rows, [case for case in all_cases if case["id"] in case_ids])
    print(output_path(run_root))
    return 0


def main(arguments: list[str]) -> int:
    if not arguments:
        raise BenchmarkError("A benchmark support command is required.")
    command, *remaining = arguments
    if command == "internal-tool-probe":
        return internal_tool_probe_main(remaining)
    if command == "write-reports":
        return write_reports_main(remaining)
    if command == "validate-event-log":
        return validate_event_log_main(remaining)
    if command == "case-list":
        return case_list_main(remaining)
    if command == "render-prompt":
        return render_prompt_main(remaining)
    if command == "normalize-index-path":
        return normalize_index_path_main(remaining)
    if command == "manifest":
        return manifest_main(remaining)
    if command == "manifest-changes":
        return manifest_changes_main(remaining)
    if command == "validate-preflight":
        return validate_preflight_main(remaining)
    if command == "probe-path":
        return probe_path_main(remaining)
    if command == "monotonic":
        return monotonic_main(remaining)
    if command == "elapsed":
        return elapsed_main(remaining)
    if command == "evaluate-run":
        return evaluate_run_main(remaining)
    if command == "append-run":
        return append_run_main(remaining)
    if command == "report":
        return report_main(remaining)
    raise BenchmarkError(f"Unknown support command: {command}")


if __name__ == "__main__":
    try:
        raise SystemExit(main(sys.argv[1:]))
    except BenchmarkError as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1)
