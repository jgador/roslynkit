#!/usr/bin/env bash
# Exercises the Bash controller with local fake dotnet and Codex commands.
set -euo pipefail

readonly REPOSITORY_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd -P)"
readonly CONTROLLER="$REPOSITORY_ROOT/scripts/benchmark.sh"
readonly ORIGINAL_PATH="$PATH"
TEST_ROOT="$(mktemp -d)"
REAL_RUN_ROOT=""
readonly REAL_INDEX_PATH="$REPOSITORY_ROOT/artifacts/benchmark-protocol-$$.db"

cleanup() {
    rm -rf -- "$TEST_ROOT"
    rm -f -- "$REAL_INDEX_PATH" "$REAL_INDEX_PATH-shm" "$REAL_INDEX_PATH-wal"
    if [[ -n "$REAL_RUN_ROOT" && "$REAL_RUN_ROOT" == "$REPOSITORY_ROOT/artifacts/benchmark/"* ]]; then
        rm -rf -- "$REAL_RUN_ROOT"
    fi
}

trap cleanup EXIT

fail() {
    printf 'FAIL: %s\n' "$*" >&2
    exit 1
}

assert_contains() {
    local needle="$1"
    local path="$2"

    grep -F -- "$needle" "$path" >/dev/null || fail "Expected '$needle' in '$path'."
}

assert_not_contains() {
    local needle="$1"
    local path="$2"

    if grep -F -- "$needle" "$path" >/dev/null; then
        fail "Did not expect '$needle' in '$path'."
    fi
}

mkdir -p "$TEST_ROOT/bin"

cat >"$TEST_ROOT/bin/dotnet" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

{
    printf 'dotnet'
    printf ' <%s>' "$@"
    printf ' cwd=<%s>\n' "$PWD"
} >>"$BENCHMARK_TEST_LOG"

if [[ "$1" == "build" ]]; then
    exit 0
fi

[[ "$1" == "run" ]] || exit 70
shift
while (($# > 0)) && [[ "$1" != "--" ]]; do
    shift
done
[[ "${1-}" == "--" ]] || exit 71
shift

command_name="${1-}"
shift
case "$command_name" in
    prepare)
        if [[ " $* " == *" --dry-run "* ]]; then
            printf 'dry-run plan\n'
            exit 0
        fi

        case "${BENCHMARK_TEST_MODE-normal}" in
            resume-pending)
                run_root="$BENCHMARK_TEST_ROOT/resume pending"
                schedule='resumed-raw-1'
                model='resume-model'
                reasoning_effort='low'
                ;;
            resume-empty)
                run_root="$BENCHMARK_TEST_ROOT/resume empty"
                schedule=''
                model='completed-model'
                reasoning_effort='minimal'
                ;;
            *)
                run_root="$BENCHMARK_TEST_ROOT/run space"
                schedule=$'case-raw-1\ncase-roslynkit-1'
                model='test-model'
                reasoning_effort='high'
                ;;
        esac
        mkdir -p "$run_root/answers" "$run_root/events" "$run_root/stderr" "$run_root/prompts"
        if [[ -n "$schedule" ]]; then
            printf '%s\n' "$schedule" >"$run_root/schedule.txt"
        else
            : >"$run_root/schedule.txt"
        fi
        printf '%s\n' "$model" >"$run_root/model.txt"
        printf '%s\n' "$reasoning_effort" >"$run_root/reasoning-effort.txt"
        printf '%s\n' "$run_root"
        ;;
    prepare-session)
        run_root=""
        run_id=""
        while (($# > 0)); do
            case "$1" in
                --run-root)
                    run_root="$2"
                    shift 2
                    ;;
                --run-id)
                    run_id="$2"
                    shift 2
                    ;;
                *)
                    exit 72
                    ;;
            esac
        done
        printf 'prompt for %s\n' "$run_id" >"$run_root/prompts/$run_id.md"
        printf '%s\n' "$run_root/prompts/$run_id.md"
        ;;
    evaluate-session|report)
        {
            printf 'helper %s' "$command_name"
            printf ' <%s>' "$@"
            printf '\n'
        } >>"$BENCHMARK_TEST_LOG"
        ;;
    *)
        exit 73
        ;;
esac
EOF

cat >"$TEST_ROOT/bin/codex" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

if [[ -n "${CODEX_THREAD_ID+x}" ]]; then
    printf 'CODEX_THREAD_ID was retained\n' >&2
    exit 74
fi

{
    printf 'codex'
    printf ' <%s>' "$@"
    printf '\n'
} >>"$BENCHMARK_TEST_LOG"

answer_path=""
while (($# > 0)); do
    case "$1" in
        --output-last-message)
            answer_path="$2"
            shift 2
            ;;
        *)
            shift
            ;;
    esac
done

[[ -n "$answer_path" ]] || exit 75
IFS= read -r prompt
printf 'answer for %s\n' "$prompt" >"$answer_path"
printf '{"type":"fake"}\n'
exit 17
EOF

chmod +x "$TEST_ROOT/bin/dotnet" "$TEST_ROOT/bin/codex"

export BENCHMARK_TEST_ROOT="$TEST_ROOT"
export BENCHMARK_TEST_LOG="$TEST_ROOT/command.log"
export CODEX_THREAD_ID="host-thread"
export PATH="$TEST_ROOT/bin:$PATH"

(
    cd -- "$TEST_ROOT"
    bash "$CONTROLLER" \
        --model test-model \
        --reasoning-effort high \
        --trials 1 \
        --case sample \
        --max-results 10 \
        --index-path ./artifacts/test.db \
        >"$TEST_ROOT/normal.stdout" \
        2>"$TEST_ROOT/normal.stderr"
)

assert_contains 'dotnet <build>' "$BENCHMARK_TEST_LOG"
assert_contains "cwd=<$REPOSITORY_ROOT>" "$BENCHMARK_TEST_LOG"
assert_contains 'helper evaluate-session <--run-root> <' "$BENCHMARK_TEST_LOG"
assert_contains '<--exit-code> <17>' "$BENCHMARK_TEST_LOG"
assert_contains 'helper report <--run-root>' "$BENCHMARK_TEST_LOG"
assert_contains 'codex <exec> <--json> <--ephemeral> <--ignore-rules> <--sandbox> <read-only> <--model> <test-model> <--config> <model_reasoning_effort="high">' "$BENCHMARK_TEST_LOG"
assert_contains '<--cd> <' "$BENCHMARK_TEST_LOG"
assert_contains '<--output-last-message> <' "$BENCHMARK_TEST_LOG"
assert_contains ' <->' "$BENCHMARK_TEST_LOG"
assert_contains 'answer for prompt for case-raw-1' "$TEST_ROOT/run space/answers/case-raw-1.md"
assert_contains '{"type":"fake"}' "$TEST_ROOT/run space/events/case-raw-1.jsonl"

export BENCHMARK_TEST_MODE=resume-pending
: >"$BENCHMARK_TEST_LOG"
bash "$CONTROLLER" \
    --resume-run-root "$TEST_ROOT/resume pending" \
    >"$TEST_ROOT/resume-pending.stdout" \
    2>"$TEST_ROOT/resume-pending.stderr"
assert_contains 'codex <exec> <--json> <--ephemeral> <--ignore-rules> <--sandbox> <read-only> <--model> <resume-model> <--config> <model_reasoning_effort="low">' "$BENCHMARK_TEST_LOG"
unset BENCHMARK_TEST_MODE

: >"$BENCHMARK_TEST_LOG"
bash "$CONTROLLER" --dry-run >"$TEST_ROOT/dry-run.stdout"
assert_contains 'dry-run plan' "$TEST_ROOT/dry-run.stdout"
assert_not_contains 'codex <exec>' "$BENCHMARK_TEST_LOG"
assert_contains ' <prepare> ' "$BENCHMARK_TEST_LOG"
assert_contains '<--model> <gpt-5.6-terra>' "$BENCHMARK_TEST_LOG"

: >"$BENCHMARK_TEST_LOG"
bash "$CONTROLLER" --report-run-root "$TEST_ROOT/run space" >"$TEST_ROOT/report.stdout"
assert_contains 'helper report <--run-root> <' "$BENCHMARK_TEST_LOG"
assert_not_contains ' <prepare> ' "$BENCHMARK_TEST_LOG"
assert_not_contains 'codex <exec>' "$BENCHMARK_TEST_LOG"

if bash "$CONTROLLER" --case sample --case-id sample >/dev/null 2>"$TEST_ROOT/duplicate.stderr"; then
    fail 'Expected duplicate case aliases to fail.'
fi
assert_contains 'specified more than once' "$TEST_ROOT/duplicate.stderr"

rm -- "$TEST_ROOT/bin/codex"
export PATH="$TEST_ROOT/bin:/usr/bin:/bin"

: >"$BENCHMARK_TEST_LOG"
if bash "$CONTROLLER" --case sample >/dev/null 2>"$TEST_ROOT/missing-codex.stderr"; then
    fail 'Expected a non-empty schedule to require Codex.'
fi
assert_contains 'Codex executable was not found on PATH' "$TEST_ROOT/missing-codex.stderr"
assert_not_contains 'helper evaluate-session' "$BENCHMARK_TEST_LOG"

: >"$BENCHMARK_TEST_LOG"
export BENCHMARK_TEST_MODE=resume-empty
bash "$CONTROLLER" --resume-run-root "$TEST_ROOT/resume empty" >"$TEST_ROOT/empty-resume.stdout"
assert_contains ' <prepare> ' "$BENCHMARK_TEST_LOG"
assert_contains 'helper report <--run-root> <' "$BENCHMARK_TEST_LOG"
assert_not_contains ' <prepare-session> ' "$BENCHMARK_TEST_LOG"
unset BENCHMARK_TEST_MODE

mkdir -p "$TEST_ROOT/real-helper-bin"
cat >"$TEST_ROOT/real-helper-bin/codex" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

[[ -z "${CODEX_THREAD_ID+x}" ]] || exit 74
answer_path=""
while (($# > 0)); do
    case "$1" in
        --output-last-message)
            answer_path="$2"
            shift 2
            ;;
        *)
            shift
            ;;
    esac
done

[[ -n "$answer_path" ]] || exit 75
cat >/dev/null
printf 'src/RoslynKit/DaemonClient.cs:1\n' >"$answer_path"
printf '{"type":"turn.completed","usage":{"input_tokens":100,"cached_input_tokens":0,"output_tokens":10,"reasoning_output_tokens":4}}\n'
EOF
chmod +x "$TEST_ROOT/real-helper-bin/codex"

export PATH="$TEST_ROOT/real-helper-bin:$ORIGINAL_PATH"
export CODEX_THREAD_ID="real-helper-host-thread"
real_output="$({
    cd -- "$TEST_ROOT"
    bash "$CONTROLLER" \
        --model protocol-model \
        --reasoning-effort low \
        --trials 1 \
        --case daemon-disconnect \
        --index-path "./artifacts/$(basename -- "$REAL_INDEX_PATH")"
} 2>"$TEST_ROOT/real-helper.stderr")"
while IFS= read -r line; do
    case "$line" in
        "Benchmark reports refreshed: "*) REAL_RUN_ROOT="${line#Benchmark reports refreshed: }" ;;
    esac
done <<<"$real_output"
if command -v cygpath >/dev/null 2>&1 && [[ "$REAL_RUN_ROOT" =~ ^[A-Za-z]:[\\/] ]]; then
    REAL_RUN_ROOT="$(cygpath --unix "$REAL_RUN_ROOT")"
fi

[[ -n "$REAL_RUN_ROOT" && "$REAL_RUN_ROOT" == "$REPOSITORY_ROOT/artifacts/benchmark/"* ]] \
    || fail 'The real helper protocol did not return a safe run root.'
[[ "$(grep -c '"runId"' "$REAL_RUN_ROOT/run.json")" == "2" ]] \
    || fail 'The real helper protocol did not persist both scheduled sessions.'
[[ -f "$REAL_RUN_ROOT/prompts/daemon-disconnect-raw-text-trial1.txt" ]] \
    || fail 'The real helper protocol did not create the raw-text prompt.'
[[ -f "$REAL_RUN_ROOT/prompts/daemon-disconnect-roslynkit-search-trial1.txt" ]] \
    || fail 'The real helper protocol did not create the RoslynKit prompt.'
[[ -f "$REAL_RUN_ROOT/runs.csv" && -f "$REAL_RUN_ROOT/summary.md" ]] \
    || fail 'The real helper protocol did not generate reports.'

printf 'benchmark Bash regression tests passed\n'
