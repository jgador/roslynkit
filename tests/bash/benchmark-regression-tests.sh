#!/usr/bin/env bash
# Regression coverage for the Bash Codex benchmark controller and Python support module.

set -euo pipefail

export PYTHONDONTWRITEBYTECODE=1

readonly test_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly repo_root="$(cd -- "${test_dir}/../.." && pwd)"
readonly runner_path="${repo_root}/scripts/benchmark-codex.sh"
readonly support_path="${repo_root}/scripts/benchmark_codex_support.py"

fail() {
    printf 'Benchmark regression failure: %s\n' "$*" >&2
    exit 1
}

assert_contains() {
    local value="$1"
    local expected="$2"
    local message="$3"
    [[ "${value}" == *"${expected}"* ]] || fail "${message}"
}

resolve_python() {
    if command -v python3 >/dev/null 2>&1; then
        command -v python3
        return 0
    fi

    if command -v python >/dev/null 2>&1; then
        command -v python
        return 0
    fi

    fail 'Python 3 is required for benchmark regression tests.'
}

readonly python_executable="$(resolve_python)"
readonly temp_root="$(mktemp -d "${TMPDIR:-/tmp}/roslynkit-benchmark-regression.XXXXXX")"
mkdir -p "${repo_root}/artifacts/codex-benchmark"
readonly report_cli_root="$(mktemp -d "${repo_root}/artifacts/codex-benchmark/benchmark-regression.XXXXXX")"
readonly legacy_report_cli_root="$(mktemp -d "${repo_root}/artifacts/codex-benchmark/benchmark-legacy-regression.XXXXXX")"

cleanup() {
    rm -rf -- "${temp_root}" "${report_cli_root}" "${legacy_report_cli_root}"
}

trap cleanup EXIT

[[ -f "${runner_path}" ]] || fail "Benchmark runner was not found: ${runner_path}"
[[ -f "${support_path}" ]] || fail "Benchmark support module was not found: ${support_path}"
bash -n "${runner_path}"

help_output="$(bash "${runner_path}" --help)"
assert_contains "${help_output}" '--model' 'The Bash runner did not document --model.'
assert_contains "${help_output}" '--reasoning-effort' 'The Bash runner did not document --reasoning-effort.'
assert_contains "${help_output}" '--roslynkit-path' 'The Bash runner did not document --roslynkit-path.'
assert_contains "${help_output}" '--dry-run' 'The Bash runner did not document --dry-run.'

zero_argument_byte_count="$(RUNNER_PATH="${runner_path}" bash -c '
    source "${RUNNER_PATH}"
    normalize_path_options | wc -c
')"
[[ "${zero_argument_byte_count//[[:space:]]/}" == '0' ]] || fail 'Zero-argument normalization emitted a spurious empty positional argument.'

mkdir -p "${temp_root}/unsupported-python"
printf '%s\n' '#!/bin/sh' 'exit 1' > "${temp_root}/unsupported-python/python3"
printf '%s\n' '#!/bin/sh' 'exit 1' > "${temp_root}/unsupported-python/python"
chmod 755 "${temp_root}/unsupported-python/python3" "${temp_root}/unsupported-python/python"
if unsupported_python_output="$(RUNNER_PATH="${runner_path}" TEST_PYTHON_BIN="${temp_root}/unsupported-python" bash -c '
    source "${RUNNER_PATH}"
    PATH="${TEST_PYTHON_BIN}"
    resolve_python
' 2>&1)"; then
    fail 'The Bash runner accepted only unsupported Python interpreters.'
fi
assert_contains "${unsupported_python_output}" 'Python 3.10 or later is required' 'The Bash runner did not explain its unsupported-Python rejection.'

path_normalization_output="$(RUNNER_PATH="${runner_path}" bash -c '
    source "${RUNNER_PATH}"
    cygpath() { printf "converted:%s\\n" "$3"; }
    while IFS= read -r -d "" value; do
        printf "<%s>\\n" "${value}"
    done < <(normalize_path_options --index-path /c/roslynkit/index.db --roslynkit-path /c/roslynkit/bin/roslynkit --report-run-root="C:\\reports\\run" --internal-tool-probe-path /c/roslynkit/probe.json)
')"
assert_contains "${path_normalization_output}" '<converted:/c/roslynkit/index.db>' 'Git-Bash index-path normalization did not use cygpath.'
assert_contains "${path_normalization_output}" '<converted:/c/roslynkit/bin/roslynkit>' 'Git-Bash RoslynKit-path normalization did not use cygpath.'
assert_contains "${path_normalization_output}" '<--report-run-root=converted:C:\reports\run>' 'Git-Bash equals-form report-path normalization did not use cygpath.'
assert_contains "${path_normalization_output}" '<converted:/c/roslynkit/probe.json>' 'Git-Bash probe-path normalization did not use cygpath.'

if failed_cygpath_output="$(RUNNER_PATH="${runner_path}" /bin/bash -c '
    source "${RUNNER_PATH}"
    cygpath() { return 55; }
    normalize_path_options --index-path /c/roslynkit/index.db
' 2>&1)"; then
    fail 'A failed cygpath conversion was accepted during path normalization.'
fi

mkdir -p "${temp_root}/codex-home"
: > "${temp_root}/codex-home/config.toml"

mkdir -p "${temp_root}/mock-bin"
parser_codex_marker="${temp_root}/parser-codex-invoked.txt"
printf '%s\n' '#!/bin/sh' 'printf invoked > "${PARSER_CODEX_MARKER}"' 'exit 1' > "${temp_root}/mock-bin/codex"
chmod 755 "${temp_root}/mock-bin/codex"
if malformed_option_output="$(PARSER_CODEX_MARKER="${parser_codex_marker}" PATH="${temp_root}/mock-bin:${PATH}" CODEX_HOME="${temp_root}/codex-home" /bin/bash "${runner_path}" --index-path --dry-run 2>&1)"; then
    fail 'A value-taking option accepted --dry-run as its value.'
fi
assert_contains "${malformed_option_output}" '--index-path requires a value' 'The malformed value-taking option did not report its missing value.'
[[ ! -e "${parser_codex_marker}" ]] || fail 'A malformed value-taking option invoked codex.'

git_bash_feature_capture="${temp_root}/git-bash-feature-environment.txt"
if git_bash_non_dry_output="$(RUNNER_PATH="${runner_path}" TEST_GIT_BASH_HOME="${temp_root}/codex-home" TEST_FEATURE_CAPTURE="${git_bash_feature_capture}" CODEX_HOME='C:\Original Codex Home' CODEX_THREAD_ID='benchmark-thread' /bin/bash -c '
    source "${RUNNER_PATH}"
    uname() { printf "%s\n" "MINGW64_NT"; }
    cygpath() {
        case "$1" in
            -u) printf "%s\n" "${TEST_GIT_BASH_HOME}" ;;
            -w) printf "%s\n" "C:\\Converted\\Codex Home" ;;
        esac
    }
    run_support() {
        case "$1" in
            normalize-index-path) printf "%s\n" "./artifacts/roslynkit.db" ;;
            case-list) printf "daemon-disconnect\0mock prompt\0" ;;
            *) printf "unexpected support command: %s\n" "$1" >&2; return 99 ;;
        esac
    }
    codex() {
        if [[ "$1" == features && "$2" == list ]]; then
            printf "CODEX_HOME=%s\nCODEX_THREAD_ID=%s\n" "${CODEX_HOME:-}" "${CODEX_THREAD_ID-<unset>}" > "${TEST_FEATURE_CAPTURE}"
            return 78
        fi
        return 79
    }
    main --case-id daemon-disconnect
' 2>&1)"; then
    fail 'The mocked non-dry Git-Bash setup unexpectedly continued past codex features list.'
fi
[[ -s "${git_bash_feature_capture}" ]] || fail 'The mocked non-dry Git-Bash setup did not invoke codex features list.'
git_bash_feature_environment="$(< "${git_bash_feature_capture}")"
assert_contains "${git_bash_feature_environment}" 'CODEX_HOME=C:\Converted\Codex Home' 'codex features list did not receive the converted Git-Bash CODEX_HOME.'
assert_contains "${git_bash_feature_environment}" 'CODEX_THREAD_ID=<unset>' 'codex features list received CODEX_THREAD_ID during a non-dry benchmark setup.'

dry_run_output="$(CODEX_HOME="${temp_root}/codex-home" CODEX_THREAD_ID='benchmark-regression-thread' bash "${runner_path}" --dry-run --trials 1 --case-id daemon-disconnect)"
assert_contains "${dry_run_output}" "bash -lc 'cat .agents/skills/benchmark/SKILL.md'" 'The dry-run prompt did not use a Bash-native benchmark skill read.'
assert_contains "${dry_run_output}" "bash -lc 'cat .agents/skills/roslynkit/SKILL.md" 'The dry-run prompt did not use Bash-native RoslynKit context reads.'
assert_contains "${dry_run_output}" '--disable unified_exec' 'The dry run did not disable unified_exec.'

if CODEX_HOME="${temp_root}/codex-home" bash "${runner_path}" --dry-run --case-id unknown >"${temp_root}/unknown-case.txt" 2>&1; then
    fail 'The Bash runner accepted an unknown benchmark case.'
fi

probe_path="${temp_root}/tool-probe.json"
bash "${runner_path}" --internal-tool-probe-path "${probe_path}"
[[ -s "${probe_path}" ]] || fail 'The Bash runner did not write its hidden tool-probe artifact.'

mkdir -p "${temp_root}/isolated-tool"
isolated_roslynkit_path="${temp_root}/isolated-tool/roslynkit"
printf '%s\n' '#!/usr/bin/env bash' 'printf "roslynkit version isolated-test\\n"' > "${isolated_roslynkit_path}"
chmod 755 "${isolated_roslynkit_path}"
isolated_probe_path="${temp_root}/isolated-tool-probe.json"
bash "${runner_path}" --internal-tool-probe-path "${isolated_probe_path}" --roslynkit-path "${isolated_roslynkit_path}"
[[ -s "${isolated_probe_path}" ]] || fail 'The Bash runner did not write a RoslynKit-path tool-probe artifact.'
isolated_dry_run_output="$(CODEX_HOME="${temp_root}/codex-home" bash "${runner_path}" --dry-run --trials 1 --case-id daemon-disconnect --roslynkit-path "${isolated_roslynkit_path}")"
assert_contains "${isolated_dry_run_output}" "${isolated_roslynkit_path}" 'The dry run did not identify the isolated RoslynKit executable.'

"${python_executable}" - "${repo_root}" "${temp_root}" "${probe_path}" "${isolated_probe_path}" "${isolated_roslynkit_path}" "${report_cli_root}" "${legacy_report_cli_root}" <<'PY'
import hashlib
import json
import sys
from pathlib import Path

repo_root = Path(sys.argv[1])
temp_root = Path(sys.argv[2])
probe_path = Path(sys.argv[3])
isolated_probe_path = Path(sys.argv[4])
isolated_roslynkit_path = Path(sys.argv[5])
report_cli_root = Path(sys.argv[6])
legacy_report_cli_root = Path(sys.argv[7])
sys.path.insert(0, str(repo_root / "scripts"))

import benchmark_codex_support as support


def check(condition, message):
    if not condition:
        raise AssertionError(message)


def command_event(event_type, event_id, command, status="completed", exit_code=0):
    return {
        "type": event_type,
        "item": {
            "id": event_id,
            "type": "command_execution",
            "name": None,
            "command": command,
            "status": status,
            "exit_code": exit_code,
        },
        "payload": None,
    }


cases = support.get_case_data(repo_root)
case_by_id = {case["id"]: case for case in cases}
check(set(case_by_id) == {"daemon-disconnect", "workspace-generation", "stale-search-index"}, "Benchmark cases changed unexpectedly.")
check("completed command failures" in case_by_id["daemon-disconnect"]["prompt"], "The daemon task omitted completed-failure correctness.")
check("maximum clean-reader capacity" in case_by_id["workspace-generation"]["prompt"], "The workspace task omitted reader-capacity correctness.")
check("exact quiet-period retry timing" in case_by_id["workspace-generation"]["prompt"], "The workspace task omitted retry-timing correctness.")
check("state recapture" in case_by_id["stale-search-index"]["prompt"], "The search task omitted state recapture correctness.")

check(support.resolve_benchmark_index_path(repo_root, "artifacts/benchmark-test.db") == "./artifacts/benchmark-test.db", "A valid custom benchmark index path was not normalized.")
try:
    support.resolve_benchmark_index_path(repo_root, "../outside.db")
except support.BenchmarkError:
    pass
else:
    raise AssertionError("An index path outside artifacts was accepted.")

prompt = support.new_condition_prompt("roslynkit", "Benchmark regression prompt.", "./artifacts/benchmark-test.db")
check("bash -lc 'cat .agents/skills/benchmark/SKILL.md'" in prompt, "The measured prompt did not use cat for the benchmark skill.")
check("bash -lc 'cat .agents/skills/roslynkit/SKILL.md" in prompt, "The measured prompt did not use cat for RoslynKit context.")
check("--index-path ./artifacts/benchmark-test.db" in prompt, "The measured prompt did not use the selected custom index path.")
check("Use at most 8 RoslynKit invocations total" in prompt, "The measured prompt omitted the RoslynKit invocation ceiling.")
check("Never run a fourth search" in prompt, "The measured prompt omitted the bounded search rule.")

benchmark_skill_path = ".agents/skills/benchmark/SKILL.md"
check(support.test_command_reads_context_path("/bin/bash -lc 'cat .agents/skills/benchmark/SKILL.md'", benchmark_skill_path), "A full Bash cat context read was not recognized.")
for truncated_read in [
    "/bin/bash -lc 'head -n 5 .agents/skills/benchmark/SKILL.md'",
    "/bin/bash -lc 'tail -n 5 .agents/skills/benchmark/SKILL.md'",
    "/bin/bash -lc 'sed -n 1,5p .agents/skills/benchmark/SKILL.md'",
    "/bin/bash -lc 'cat .agents/skills/benchmark/SKILL.md | head -n 5'",
    "/bin/bash -lc 'cat .agents/skills/benchmark/SKILL.md > /tmp/benchmark-skill.txt'",
    "/bin/bash -lc 'cat < .agents/skills/benchmark/SKILL.md'",
    "/bin/bash -lc 'cat <(head -n 5 .agents/skills/benchmark/SKILL.md)'",
    "/bin/bash -lc 'cat < <(head -n 5 .agents/skills/benchmark/SKILL.md)'",
    "/bin/bash -lc 'cat \"$(head -n 5 .agents/skills/benchmark/SKILL.md)\"'",
    "/bin/bash -lc 'cat \"${P:-.agents/skills/benchmark/SKILL.md}\"'",
]:
    check(not support.test_command_reads_context_path(truncated_read, benchmark_skill_path), f"A truncated context read was accepted: {truncated_read}")

usage_event = {
    "type": "turn.completed",
    "usage": {
        "input_tokens": 348872,
        "cached_input_tokens": 293385,
        "cache_write_input_tokens": 55454,
        "output_tokens": 3783,
        "reasoning_output_tokens": 2183,
    },
}
accounting = support.get_token_accounting([usage_event])
check(not accounting["issues"], f"Valid terminal token accounting was rejected: {accounting['issues']}")
check(accounting["usage"]["uncached_input_tokens"] == 55487, "Non-cached input was not derived from total minus cached input.")
check(accounting["usage"]["regular_uncached_input_tokens"] == 33, "Regular uncached input did not exclude cache-write tokens.")
check(not accounting["request_usage_available"], "Completed-turn aggregate usage was mistaken for per-request usage.")

api_named_accounting = support.get_token_accounting([{
    "type": "turn.completed",
    "usage": {
        "input_tokens": 100,
        "cached_input_tokens": 40,
        "cache_write_tokens": 50,
        "output_tokens": 10,
        "reasoning_output_tokens": 4,
    },
}])
check(api_named_accounting["usage"]["cache_write_input_tokens"] == 50, "The API cache_write_tokens alias was not normalized.")
check(api_named_accounting["usage"]["regular_uncached_input_tokens"] == 10, "The API cache_write_tokens alias produced an incorrect regular-input count.")
ambiguous_accounting = support.get_token_accounting([usage_event, usage_event])
check(any("2 terminal usage events" in issue for issue in ambiguous_accounting["issues"]), "Multiple terminal usage events were accepted as one ephemeral turn.")

terra_short_cost = support.get_gpt56_cost_projection(accounting["usage"], "gpt-5.6-terra")
terra_long_cost = support.get_gpt56_cost_projection(accounting["usage"], "gpt-5.6-terra", "long")
check(terra_short_cost["total_cost_usd"] == 0.242774, "Terra short-context projection was incorrect.")
check(terra_long_cost["total_cost_usd"] == 0.46285, "Terra all-long-context projection was incorrect.")
check(terra_short_cost["status"] == "complete", "Known cache-write accounting was marked incomplete.")

malformed_event_path = temp_root / "events.jsonl"
malformed_event_path.write_text('{"type":"turn.started"}\n{\n', encoding="utf-8")
malformed_log = support.read_event_log(malformed_event_path)
check(len(malformed_log["events"]) == 1, "The valid JSONL event next to a malformed event was lost.")
check(any("line 2" in issue for issue in malformed_log["issues"]), "Malformed JSONL did not retain its line number.")

probe = json.loads(probe_path.read_text(encoding="utf-8"))
check(probe.get("schema_version") == 1, "The hidden tool-probe artifact had an invalid schema version.")
check(probe.get("host_kind") == support.get_benchmark_host_kind(), "The hidden tool-probe artifact had invalid host metadata.")
for tool_name in ("ripgrep", "roslynkit"):
    check({"resolved_path", "output", "version_output", "executable_sha256", "exit_code"}.issubset(probe[tool_name]), f"The hidden {tool_name} probe omitted required fields.")

isolated_probe = json.loads(isolated_probe_path.read_text(encoding="utf-8"))
isolated_tool = isolated_probe["roslynkit"]
check(Path(isolated_tool["resolved_path"]) == isolated_roslynkit_path.resolve(), "The tool probe did not use the selected isolated RoslynKit executable.")
check(isolated_tool["version_output"] == "roslynkit version isolated-test", "The tool probe did not record the selected RoslynKit version output.")
check(isolated_tool["executable_sha256"] == hashlib.sha256(isolated_roslynkit_path.read_bytes()).hexdigest(), "The tool probe did not record the selected RoslynKit SHA-256.")

valid_probe = {
    "schema_version": 1,
    "host_kind": support.get_benchmark_host_kind(),
    "ripgrep": {"resolved_path": sys.executable, "output": "ripgrep 15.2.0", "version_output": "ripgrep 15.2.0", "executable_sha256": hashlib.sha256(Path(sys.executable).read_bytes()).hexdigest(), "exit_code": 0},
    "roslynkit": {"resolved_path": sys.executable, "output": "roslynkit version 0.2.0", "version_output": "roslynkit version 0.2.0", "executable_sha256": hashlib.sha256(Path(sys.executable).read_bytes()).hexdigest(), "exit_code": 0},
}
check(not support.get_tool_probe_validation_issues(valid_probe), "A valid structured tool probe was rejected.")
missing_probe = dict(valid_probe, ripgrep=None)
check(any("ripgrep probe was missing" in issue for issue in support.get_tool_probe_validation_issues(missing_probe)), "A missing tool probe was accepted.")
nonzero_probe = dict(valid_probe, ripgrep={"resolved_path": sys.executable, "output": "ripgrep 15.2.0", "exit_code": 7})
check(any("ripgrep exit code was not zero" in issue for issue in support.get_tool_probe_validation_issues(nonzero_probe)), "A nonzero individual tool exit was accepted.")

invalid_probe_path = temp_root / "invalid-probe.json"
invalid_probe_path.write_text("{", encoding="utf-8")
for invalid_path, expected in [(invalid_probe_path, "not valid JSON"), (temp_root / "missing-probe.json", "was not written")]:
    try:
        support.read_validated_tool_probe(invalid_path)
    except support.BenchmarkError as error:
        check(expected in str(error), f"Unexpected invalid-probe error: {error}")
    else:
        raise AssertionError(f"An invalid tool probe was accepted: {invalid_path}")

raw_skill_read = "/bin/bash -lc 'cat .agents/skills/benchmark/SKILL.md'"
roslynkit_context_read = "/bin/bash -lc 'cat .agents/skills/roslynkit/SKILL.md .agents/skills/roslynkit/references/commands.md .agents/skills/roslynkit/references/output.md'"
roslynkit_search = 'timeout 120s roslynkit search --target ./RoslynKit.slnx --index-path ./artifacts/roslynkit.db --query "tracked files change reload workspace" --max-results 10'
roslynkit_source = "timeout 120s roslynkit symbol-source --target ./RoslynKit.slnx --symbol 'M:RoslynKit.WorkspaceDaemonSession.BeginReload'"

observed_commands = support.get_commands([
    command_event("item.completed", "command-1", raw_skill_read),
    {
        "type": "response_item",
        "payload": {
            "type": "function_call",
            "name": "shell_command",
            "arguments": json.dumps({"command": roslynkit_search}),
        },
    },
    {"type": "response_item", "payload": {"type": "function_call", "name": "other", "arguments": "{}"}},
])
check(observed_commands == [raw_skill_read, roslynkit_search], "Command extraction did not retain the supported command-event shapes.")

quoted_wrapper_text = "/bin/bash -lc 'printf \"%s\\n\" \"pwsh -NoProfile -Command; cmd.exe /c\"'"
check(not support.test_forbidden_context_surface("raw-codex", quoted_wrapper_text, False, repo_root), "Quoted PowerShell or cmd text was treated as a wrapper invocation.")
for shell_wrapper in [
    'pwsh -NoProfile -Command "printf ok"',
    'powershell.exe -NoProfile -Command "printf ok"',
    'cmd.exe /c "echo ok"',
    'command pwsh -NoProfile -Command "printf ok"',
    'nice -n 5 cmd.exe /c "echo ok"',
    'bash -c -- \'pwsh -NoProfile -Command "printf ok"\'',
    'exec pwsh -NoProfile -Command "printf ok"',
    'nohup cmd.exe /c "echo ok"',
    'stdbuf -oL pwsh -NoProfile -Command "printf ok"',
    'time pwsh -NoProfile -Command "printf ok"',
    'setsid cmd.exe /c "echo ok"',
]:
    check(support.test_forbidden_context_surface("raw-codex", shell_wrapper, False, repo_root), f"A real disallowed shell wrapper was accepted: {shell_wrapper}")

for quoted_roslynkit in [
    '"roslynkit search"',
    'printf "%s\\n" "roslynkit search"',
    "/bin/bash -lc 'printf \"%s\\n\" \"roslynkit search\"'",
]:
    check(not support.test_roslynkit_invocation(quoted_roslynkit, "roslynkit"), f"Quoted RoslynKit text was treated as an invocation: {quoted_roslynkit}")
for prefixed_roslynkit in [
    "command roslynkit version",
    "nice -n 5 roslynkit version",
    "bash -c -- 'roslynkit version'",
    "exec roslynkit version",
    "nohup roslynkit version",
    "stdbuf -oL roslynkit version",
    "time roslynkit version",
    "setsid roslynkit version",
]:
    check(support.test_roslynkit_invocation(prefixed_roslynkit, "roslynkit"), f"A launcher prefix hid a RoslynKit invocation: {prefixed_roslynkit}")
for prefixed_root_search in [
    "command rg -n reload .",
    "exec rg -n reload .",
    "nohup rg -n reload .",
    "stdbuf -oL rg -n reload .",
    "time rg -n reload .",
    "setsid rg -n reload .",
]:
    check(support.test_repository_root_recursive_search(prefixed_root_search, repo_root), f"A launcher-prefixed repository-root search was accepted: {prefixed_root_search}")
check(not support.test_roslynkit_invocation("command -v roslynkit", "roslynkit"), "A command -v RoslynKit query was treated as execution.")
check(not support.test_forbidden_context_surface("raw-codex", "command -V pwsh", False, repo_root), "A command -V PowerShell query was treated as wrapper execution.")

check(support.get_compliance_issues("raw-codex", [raw_skill_read, 'rg -n "reload|snapshot" --glob "*.cs" src/RoslynKit tests/RoslynKit.Tests'], [command_event("item.completed", "raw-1", raw_skill_read), command_event("item.completed", "raw-2", 'rg -n "reload|snapshot" --glob "*.cs" src/RoslynKit tests/RoslynKit.Tests')], [], "roslynkit", repo_root) == [], "A valid raw Bash sequence failed compliance.")
check(support.get_compliance_issues("roslynkit", [raw_skill_read, roslynkit_context_read, roslynkit_search, roslynkit_source], [command_event("item.completed", "rk-1", raw_skill_read), command_event("item.completed", "rk-2", roslynkit_context_read), command_event("item.completed", "rk-3", roslynkit_search), command_event("item.completed", "rk-4", roslynkit_source)], [], "roslynkit", repo_root) == [], "A valid RoslynKit Bash sequence failed compliance.")
root_search_issues = support.get_compliance_issues("raw-codex", ["rg -n reload"], [], [], "roslynkit", repo_root)
check(any("forbidden context surface" in issue for issue in root_search_issues), "An implicit repository-root search was accepted.")
power_shell_issues = support.get_compliance_issues("raw-codex", ['pwsh -NoProfile -Command "Get-Content .agents/skills/benchmark/SKILL.md"'], [], [], "roslynkit", repo_root)
check(any("PowerShell" in issue or "forbidden" in issue for issue in power_shell_issues), "A PowerShell wrapper was accepted in a Bash-only benchmark.")
at_limit_issues = support.get_compliance_issues("roslynkit", [raw_skill_read, roslynkit_context_read, *([roslynkit_search] * 8)], [], [], "roslynkit", repo_root)
check(not any("maximum is 8" in issue for issue in at_limit_issues), "Eight RoslynKit invocations exceeded the hard ceiling.")
over_limit_issues = support.get_compliance_issues("roslynkit", [raw_skill_read, roslynkit_context_read, *([roslynkit_search] * 9)], [], [], "roslynkit", repo_root)
check(any("used 9 invocations; maximum is 8" in issue for issue in over_limit_issues), "Nine RoslynKit invocations did not fail compliance.")
nine_identical_events = [command_event("item.completed", f"rk-{index}", roslynkit_search) for index in range(1, 10)]
nine_identical_commands = support.get_commands(nine_identical_events)
check(len(nine_identical_commands) == 9, "Nine identical RoslynKit command events were collapsed during command extraction.")
check(support.get_roslynkit_invocation_count(nine_identical_commands, "roslynkit") == 9, "Nine identical RoslynKit commands did not count as nine invocations.")
nine_event_issues = support.get_compliance_issues("roslynkit", [raw_skill_read, roslynkit_context_read, *nine_identical_commands], nine_identical_events, [], "roslynkit", repo_root)
check(any("used 9 invocations; maximum is 8" in issue for issue in nine_event_issues), "Nine identical RoslynKit command events did not exceed the invocation ceiling.")

overlapping_events = [
    command_event("item.started", "rk-1", roslynkit_search, "in_progress", None),
    command_event("item.started", "rk-2", roslynkit_source, "in_progress", None),
]
overlapping_issues = support.get_compliance_issues("roslynkit", [raw_skill_read, roslynkit_context_read, roslynkit_search, roslynkit_source], overlapping_events, [], "roslynkit", repo_root)
check(any("overlapped" in issue for issue in overlapping_issues), "Overlapping Bash-wrapped RoslynKit commands were not detected.")
serial_events = [
    command_event("item.started", "rk-1", roslynkit_search, "in_progress", None),
    command_event("item.completed", "rk-1", roslynkit_search),
    command_event("item.started", "rk-2", roslynkit_source, "in_progress", None),
    command_event("item.completed", "rk-2", roslynkit_source),
]
check(not support.test_concurrent_roslynkit_invocations(serial_events, "roslynkit"), "Serial Bash-wrapped RoslynKit commands were classified as overlapping.")

successful_preflight_events = [command_event("item.completed", "probe-1", "bash ./scripts/benchmark-codex.sh --internal-tool-probe-path ./artifacts/probe.json")]
failed_preflight_events = [command_event("item.completed", "probe-1", "bash ./scripts/benchmark-codex.sh --internal-tool-probe-path ./artifacts/probe.json", "failed", 1)]
check(support.test_single_successful_command_event(successful_preflight_events), "One successful child probe event was rejected.")
check(not support.test_single_successful_command_event(failed_preflight_events), "A failed child probe event was accepted.")
check(not support.test_single_successful_command_event([]), "A missing child probe event was accepted.")
check(not support.test_single_successful_command_event(successful_preflight_events * 2), "Multiple child probe events were accepted.")

report_root = temp_root / "report"
report_cases = [{"id": "cost-case", "manualReviewCriteria": ["Answer is correct."]}]
raw_sol = support.get_gpt56_cost_projection(accounting["usage"], "gpt-5.6-sol")
raw_luna = support.get_gpt56_cost_projection(accounting["usage"], "gpt-5.6-luna")
raw_row = {
    "run_id": "cost-case-raw-codex-trial1", "case_id": "cost-case", "condition": "raw-codex", "trial": 1,
    "model": "gpt-5.6-terra", "valid": True, "exit_code": 0, "duration_seconds": 10,
    "input_tokens": 348872, "cached_input_tokens": 293385, "cache_write_input_tokens": 55454,
    "regular_uncached_input_tokens": 33, "output_tokens": 3783, "reasoning_output_tokens": 2183,
    "cache_hit_rate_pct": 84.0996, "model_turn_count": 1, "tool_call_count": 5, "roslynkit_invocation_count": 0,
    "selected_model_short_context_cost_usd": terra_short_cost["total_cost_usd"],
    "selected_model_all_long_context_cost_usd": terra_long_cost["total_cost_usd"],
    "sol_short_context_cost_usd": raw_sol["total_cost_usd"], "terra_short_context_cost_usd": terra_short_cost["total_cost_usd"], "luna_short_context_cost_usd": raw_luna["total_cost_usd"],
    "sol_regular_uncached_input_cost_usd": raw_sol["regular_uncached_input_cost_usd"], "sol_cached_input_cost_usd": raw_sol["cached_input_cost_usd"],
    "sol_cache_write_cost_usd": raw_sol["cache_write_cost_usd"], "sol_output_cost_usd": raw_sol["output_cost_usd"],
    "issues": "", "answer_path": "raw.md",
}
roslyn_usage = {
    "input_tokens": 176512, "cached_input_tokens": 150782, "cache_write_input_tokens": 25703,
    "uncached_input_tokens": 25730, "regular_uncached_input_tokens": 27, "output_tokens": 2000,
    "reasoning_output_tokens": 1000,
}
roslyn_terra_short = support.get_gpt56_cost_projection(roslyn_usage, "gpt-5.6-terra")
roslyn_terra_long = support.get_gpt56_cost_projection(roslyn_usage, "gpt-5.6-terra", "long")
roslyn_sol = support.get_gpt56_cost_projection(roslyn_usage, "gpt-5.6-sol")
roslyn_luna = support.get_gpt56_cost_projection(roslyn_usage, "gpt-5.6-luna")
roslyn_row = {
    "run_id": "cost-case-roslynkit-trial1", "case_id": "cost-case", "condition": "roslynkit", "trial": 1,
    "model": "gpt-5.6-terra", "valid": True, "exit_code": 0, "duration_seconds": 12,
    "input_tokens": 176512, "cached_input_tokens": 150782, "cache_write_input_tokens": 25703,
    "regular_uncached_input_tokens": 27, "output_tokens": 2000, "reasoning_output_tokens": 1000,
    "cache_hit_rate_pct": 85.422, "model_turn_count": 1, "tool_call_count": 3, "roslynkit_invocation_count": 3,
    "selected_model_short_context_cost_usd": roslyn_terra_short["total_cost_usd"],
    "selected_model_all_long_context_cost_usd": roslyn_terra_long["total_cost_usd"],
    "sol_short_context_cost_usd": roslyn_sol["total_cost_usd"], "terra_short_context_cost_usd": roslyn_terra_short["total_cost_usd"], "luna_short_context_cost_usd": roslyn_luna["total_cost_usd"],
    "sol_regular_uncached_input_cost_usd": roslyn_sol["regular_uncached_input_cost_usd"], "sol_cached_input_cost_usd": roslyn_sol["cached_input_cost_usd"],
    "sol_cache_write_cost_usd": roslyn_sol["cache_write_cost_usd"], "sol_output_cost_usd": roslyn_sol["output_cost_usd"],
    "issues": "", "answer_path": "roslynkit.md",
}
support.write_reports(report_root, [raw_row, roslyn_row], report_cases)
pending_summary = (report_root / "summary.md").read_text(encoding="utf-8")
check("Only operationally valid runs marked `pass`" in pending_summary, "The report did not state its correctness gate.")
review_path = report_root / "review-results.json"
review_document = json.loads(review_path.read_text(encoding="utf-8"))
for run_review in review_document["runs"]:
    run_review["overall_status"] = "pass"
    for criterion in run_review["criteria"]:
        criterion["status"] = "pass"
review_path.write_text(json.dumps(review_document), encoding="utf-8")
support.write_reports(report_root, [raw_row, roslyn_row], report_cases)
reviewed_summary = (report_root / "summary.md").read_text(encoding="utf-8")
check("$0.242774" in reviewed_summary, "The reviewed-correct Terra cost was not reported.")
check("GPT-5.6 Standard Cost Projections For Correct Runs" in reviewed_summary, "Cross-tier cost projections were not reported.")
check("| RoslynKit calls |" in reviewed_summary, "The report did not separate RoslynKit invocations from all tool calls.")

report_cli_rows = [
    dict(raw_row, run_id="daemon-disconnect-raw-codex-trial1", case_id="daemon-disconnect", runner="bash"),
    dict(roslyn_row, run_id="daemon-disconnect-roslynkit-trial1", case_id="daemon-disconnect", runner="bash"),
]
support.write_reports(report_cli_root, report_cli_rows, [case_by_id["daemon-disconnect"]])
(legacy_report_cli_root / "runs.json").write_text(json.dumps([dict(report_cli_rows[0], runner=None)]), encoding="utf-8")

print("Bash benchmark Python regression checks passed.")
PY

report_only_output="$(bash "${runner_path}" --report-run-root "${report_cli_root}")"
assert_contains "${report_only_output}" 'Benchmark reports refreshed:' 'The report-only controller branch did not refresh a generated artifact.'
if legacy_report_output="$(bash "${runner_path}" --report-run-root "${legacy_report_cli_root}" 2>&1)"; then
    fail 'The report-only controller accepted an unmarked legacy artifact.'
fi
assert_contains "${legacy_report_output}" "--report-run-root accepts only Bash-runner artifacts with runner='bash'." 'The report-only controller did not explain its legacy-artifact rejection.'

printf '%s\n' 'Bash benchmark regression tests passed.'
