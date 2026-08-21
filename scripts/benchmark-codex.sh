#!/usr/bin/env bash
# Runs the Codex search benchmark through the portable Python support module.

set -euo pipefail

readonly SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly SUPPORT_SCRIPT="${SCRIPT_DIR}/benchmark_codex_support.py"
readonly PRICING_VERIFIED_DATE='2026-08-21'
readonly REQUESTED_DISABLED_FEATURES=(
    apps browser_use browser_use_external browser_use_full_cdp_access computer_use
    external_agent_memory_import goals hooks image_generation in_app_browser memories
    multi_agent multi_agent_v2 plugin_sharing plugins remote_plugin shell_snapshot
    skill_mcp_dependency_install skill_search standalone_web_search unified_exec
    workspace_dependencies
)

usage() {
    cat <<'EOF'
Usage: benchmark-codex.sh [options]

Options:
  --model VALUE                     Codex model (default: gpt-5.6-sol)
  --reasoning-effort VALUE          Reasoning effort (default: high)
  --trials NUMBER                   Trials per case and condition (default: 1)
  --case-id VALUE                   A benchmark case ID or all (default: all)
  --index-path PATH                 Repository-local artifacts database path
                                    (default: ./artifacts/roslynkit.db)
  --report-run-root PATH            Rebuild reports for an existing Bash-runner run
  --dry-run                         Print planned prompts and commands only
  --internal-tool-probe-path PATH   Internal preflight mode; not for normal use
  --help                            Show this help
EOF
}

resolve_python() {
    local candidate
    for candidate in python3 python; do
        if ! command -v "${candidate}" >/dev/null 2>&1; then
            continue
        fi

        local candidate_path
        candidate_path="$(command -v "${candidate}")"
        if "${candidate_path}" -c 'import sys; raise SystemExit(0 if sys.version_info >= (3, 10) else 1)' >/dev/null 2>&1; then
            printf '%s\n' "${candidate_path}"
            return 0
        fi
    done

    printf '%s\n' 'Python 3.10 or later is required for the Codex benchmark runner.' >&2
    return 1
}

normalize_git_bash_path() {
    local value="$1"
    if command -v cygpath >/dev/null 2>&1 && [[ "${value}" =~ ^[A-Za-z]:[\\/] || "${value}" == \\* || "${value}" == /* ]]; then
        cygpath -w -- "${value}" || return 1
        return 0
    fi

    printf '%s\n' "${value}"
}

normalize_path_options() {
    local -a input_arguments=("$@")
    local -a normalized_arguments=()
    local argument
    local next_is_path=false

    for argument in "${input_arguments[@]}"; do
        if [[ "${next_is_path}" == true ]]; then
            normalized_arguments+=("$(normalize_git_bash_path "${argument}")")
            next_is_path=false
            continue
        fi

        case "${argument}" in
            --index-path|--report-run-root|--internal-tool-probe-path)
                normalized_arguments+=("${argument}")
                next_is_path=true
                ;;
            --index-path=*|--report-run-root=*|--internal-tool-probe-path=*)
                normalized_arguments+=("${argument%%=*}=$(normalize_git_bash_path "${argument#*=}")")
                ;;
            *)
                normalized_arguments+=("${argument}")
                ;;
        esac
    done

    if ((${#normalized_arguments[@]} > 0)); then
        printf '%s\0' "${normalized_arguments[@]}"
    fi
}

fail() {
    printf 'error: %s\n' "$*" >&2
    exit 1
}

run_support() {
    if [[ -n "${native_codex_home:-}" ]]; then
        CODEX_HOME="${native_codex_home}" "${python_executable}" "${SUPPORT_SCRIPT}" "$@"
        return 0
    fi

    "${python_executable}" "${SUPPORT_SCRIPT}" "$@"
}

resolve_repo_root() {
    local root
    root="$(git rev-parse --show-toplevel 2>/dev/null)" || fail 'Run the benchmark from a Git worktree.'
    [[ -n "${root}" ]] || fail 'Run the benchmark from a Git worktree.'
    repo_root="$(cd -- "${root}" && pwd -P)"
}

load_cases() {
    case_ids=()
    case_prompts=()
    local discovered_id
    local discovered_prompt
    while IFS= read -r -d '' discovered_id && IFS= read -r -d '' discovered_prompt; do
        case_ids+=("${discovered_id}")
        case_prompts+=("${discovered_prompt}")
    done < <(run_support case-list --repo-root "${repo_root}" --case-id "${case_id_option}")
    ((${#case_ids[@]} > 0)) || fail "No benchmark case matches --case-id '${case_id_option}'."
}

render_prompt() {
    run_support render-prompt --condition "$1" --index-path "${benchmark_index_path}" --prompt "$2"
}

set_host_codex_home() {
    local configured_codex_home="${CODEX_HOME:-${HOME}/.codex}"
    local shell_codex_home="${configured_codex_home}"
    local host_shell
    host_shell="$(uname -s 2>/dev/null || true)"
    if [[ "${host_shell}" =~ ^(MINGW|MSYS) ]] && command -v cygpath >/dev/null 2>&1; then
        shell_codex_home="$(cygpath -u -- "${configured_codex_home}")"
    fi
    [[ -d "${shell_codex_home}" ]] || fail "The active host CODEX_HOME directory was not found: '${configured_codex_home}'."
    active_codex_config_path="$(cd -- "${shell_codex_home}" && pwd -P)/config.toml"
    [[ -f "${active_codex_config_path}" ]] || fail "The active host Codex configuration was not found: '${active_codex_config_path}'."
    native_codex_home="${active_codex_config_path%/config.toml}"
    if [[ "${host_shell}" =~ ^(MINGW|MSYS) ]] && command -v cygpath >/dev/null 2>&1; then
        native_codex_home="$(cygpath -w -- "${native_codex_home}")"
    fi
}

build_disabled_features() {
    disabled_features=()
    if [[ "${dry_run}" == true ]]; then
        disabled_features=("${REQUESTED_DISABLED_FEATURES[@]}")
        return 0
    fi

    local feature_output
    feature_output="$(invoke_codex_cli features list)" || fail 'The installed Codex CLI could not enumerate features for benchmark isolation.'
    local -a available_features=()
    local feature
    while IFS= read -r feature _; do
        [[ -n "${feature}" ]] && available_features+=("${feature}")
    done <<< "${feature_output}"

    local requested
    for requested in "${REQUESTED_DISABLED_FEATURES[@]}"; do
        if [[ "${requested}" == unified_exec ]]; then
            disabled_features+=("${requested}")
            continue
        fi
        for feature in "${available_features[@]}"; do
            if [[ "${requested}" == "${feature}" ]]; then
                disabled_features+=("${requested}")
                break
            fi
        done
    done
}

invoke_codex_cli() {
    local prior_codex_home="${CODEX_HOME-}"
    local had_codex_home=false
    [[ -n "${CODEX_HOME+x}" ]] && had_codex_home=true
    export CODEX_HOME="${native_codex_home}"
    local exit_code
    if codex "$@"; then
        exit_code=0
    else
        exit_code=$?
    fi
    if [[ "${had_codex_home}" == true ]]; then
        export CODEX_HOME="${prior_codex_home}"
    else
        unset CODEX_HOME
    fi
    return "${exit_code}"
}

build_codex_arguments() {
    local prompt="$1"
    local answer_path="$2"
    codex_arguments=(
        exec --dangerously-bypass-approvals-and-sandbox --config "model_reasoning_effort=\"${reasoning_effort}\""
        --config project_doc_max_bytes=0 --config memories.use_memories=false --config memories.generate_memories=false
        --config 'shell_environment_policy.inherit="all"' --model "${model}" --ephemeral --json --color never
        --cd "${repo_root}" --output-last-message "${answer_path}"
    )
    local feature
    for feature in "${disabled_features[@]}"; do
        codex_arguments+=(--disable "${feature}")
    done
    codex_arguments+=("${prompt}")
}

print_codex_command() {
    printf 'codex '
    printf '%q ' "${codex_arguments[@]}"
    printf '\n'
}

check_manifest_unchanged() {
    local phase="$1"
    local changes
    changes="$(run_support manifest-changes --repo-root "${repo_root}" --baseline "${manifest_path}")"
    [[ "${changes}" == '[]' ]] || fail "Repository content changed ${phase}: ${changes}"
}

stop_roslynkit_daemon() {
    [[ -n "${resolved_roslynkit_path:-}" && -f "${repo_root:-}/RoslynKit.slnx" ]] || return 0
    local stop_output
    if ! stop_output="$("${resolved_roslynkit_path}" daemon stop --target ./RoslynKit.slnx 2>&1)"; then
        printf "warning: RoslynKit daemon cleanup failed for '%s': %s\n" "${repo_root}" "${stop_output}" >&2
        return 0
    fi
    [[ "${stop_output}" == *'state: not-running'* ]] && return 0
    local attempt
    for ((attempt = 0; attempt < 20; attempt += 1)); do
        local status_output
        if status_output="$("${resolved_roslynkit_path}" daemon status --target ./RoslynKit.slnx 2>&1)" && [[ "${status_output}" == *'state: not-running'* ]]; then
            return 0
        fi
        sleep 0.25
    done
    printf "warning: RoslynKit daemon did not stop within five seconds for '%s'.\n" "${repo_root}" >&2
}

run_codex_session() {
    local event_path="$1"
    local stderr_path="$2"
    local started_at
    started_at="$(run_support monotonic)"
    if invoke_codex_cli "${codex_arguments[@]}" >"${event_path}" 2>"${stderr_path}"; then
        session_exit_code=0
    else
        session_exit_code=$?
    fi
    session_duration_seconds="$(run_support elapsed --started-at "${started_at}")"
}

run_preflight() {
    local preflight_root="${run_root}/preflight"
    local answer_path="${preflight_root}/answer.md"
    local event_path="${preflight_root}/events.jsonl"
    local stderr_path="${preflight_root}/stderr.txt"
    local commands_path="${preflight_root}/commands.txt"
    local probe_path="${preflight_root}/tool-probe.json"
    mkdir -p -- "${preflight_root}"
    local probe_relative_path="./${probe_path#"${repo_root}/"}"
    local preflight_command
    printf -v preflight_command 'bash ./scripts/benchmark-codex.sh --internal-tool-probe-path %q' "${probe_relative_path}"
    local prompt="Run exactly this one shell command once and do not run any other command:

${preflight_command}

Then reply with exactly: tool probe complete"
    build_codex_arguments "${prompt}" "${answer_path}"
    run_codex_session "${event_path}" "${stderr_path}"
    if ((session_exit_code != 0)); then
        fail "Benchmark preflight failed before measured sessions. Inspect '${preflight_root}'."
    fi
    if ! run_support validate-preflight --event-path "${event_path}" --probe-path "${probe_path}" --commands-path "${commands_path}"; then
        fail "Benchmark preflight failed before measured sessions. Inspect '${preflight_root}'."
    fi
    check_manifest_unchanged 'during benchmark preflight'
    resolved_roslynkit_path="$(run_support probe-path --probe-path "${probe_path}")"
    printf 'Benchmark preflight passed: %s\n' "${preflight_root}"
}

run_measured_case() {
    local current_case_id="$1"
    local current_case_prompt="$2"
    local condition="$3"
    local trial="$4"
    local run_id="${current_case_id}-${condition}-trial${trial}"
    local answer_path="${run_root}/answers/${run_id}.md"
    local event_path="${run_root}/events/${run_id}.jsonl"
    local stderr_path="${run_root}/stderr/${run_id}.txt"
    local commands_path="${run_root}/commands/${run_id}.txt"
    local row_path="${run_root}/${run_id}.json"
    check_manifest_unchanged "before '${run_id}'"
    local prompt
    prompt="$(render_prompt "${condition}" "${current_case_prompt}")"
    build_codex_arguments "${prompt}" "${answer_path}"
    run_codex_session "${event_path}" "${stderr_path}"
    run_support evaluate-run --case-id "${current_case_id}" --condition "${condition}" --trial "${trial}" \
        --repo-root "${repo_root}" --manifest-path "${manifest_path}" --answer-path "${answer_path}" \
        --event-path "${event_path}" --stderr-path "${stderr_path}" --commands-path "${commands_path}" \
        --roslynkit-path "${resolved_roslynkit_path}" --index-path "${benchmark_index_path}" --model "${model}" \
        --reasoning-effort "${reasoning_effort}" --exit-code "${session_exit_code}" \
        --duration-seconds "${session_duration_seconds}" --output "${row_path}"
    run_support append-run --run-root "${run_root}" --row-path "${row_path}"
    run_support write-reports --run-root "${run_root}" --cases-path "${repo_root}/benchmarks/codex-cases.json"
    check_manifest_unchanged "during '${current_case_id}/${condition}/trial${trial}'"
    if grep -q '"valid": false' "${row_path}"; then
        printf "warning: Recorded invalid session '%s/%s/trial%s' and continuing.\n" "${current_case_id}" "${condition}" "${trial}" >&2
    fi
    rm -- "${row_path}"
}

write_dry_run() {
    printf 'Active Codex config: %s\n' "${active_codex_config_path}"
    printf '%s\n' "Environment: the current host's CODEX_HOME is used directly; benchmark-specific command-line overrides remain in effect."
    printf '%s\n' 'Execution: child sessions bypass approvals and sandboxing, inherit the full host environment, disable unified_exec, and use the repository root as the --cd working root.'
    printf "RoslynKit condition: the global 'roslynkit' command is resolved from the inherited host PATH; the prepared search index is %s relative to the repository root.\n" "${benchmark_index_path}"
    printf '%s\n' 'Preflight: one unmeasured child runs the controller hidden tool-probe mode through Bash and writes structured host, path, output, and exit-code evidence.'
    printf '%s\n' 'Comparison: compare raw Codex with RoslynKit only inside the same run and host; do not compare duration across hosts or with runs made before unified_exec was disabled.'
    printf '%s\n' 'Validity: an invalid measured session is recorded and excluded from comparison, then the remaining scheduled sessions continue without retry. Preparation, preflight, and nonignored repository content changes stop the controller.'
    printf 'Cost: reports project GPT-5.6 Sol, Terra, and Luna Standard API prices verified %s; correctness-gated savings remain empty until review-results.json is completed.\n' "${PRICING_VERIFIED_DATE}"
    printf '%s\n' 'Long context: Codex exec JSONL exposes completed-turn aggregate usage, so request-level 272K threshold counts and exact long-context cost remain unknown.'
    printf '%s\n' 'Repository integrity: a content manifest is captured before preflight and validated after preflight, preparation, and every measured session; ignored artifacts do not affect it.'
    printf '\n'
    local trial condition case_index prompt
    for ((trial = 1; trial <= trials; trial += 1)); do
        local -a conditions=(raw-codex roslynkit)
        ((trial % 2 == 0)) && conditions=(roslynkit raw-codex)
        for case_index in "${!case_ids[@]}"; do
            for condition in "${conditions[@]}"; do
                prompt="$(render_prompt "${condition}" "${case_prompts[case_index]}")"
                build_codex_arguments "${prompt}" '<artifacts-answer-path>'
                printf '[%s] %s trial %s\n' "${case_ids[case_index]}" "${condition}" "${trial}"
                print_codex_command
                printf 'Prompt:\n%s\n\n' "${prompt}"
            done
        done
    done
}

main() {
    model='gpt-5.6-sol'
    reasoning_effort='high'
    trials=1
    case_id_option='all'
    index_path_option='./artifacts/roslynkit.db'
    report_run_root=''
    dry_run=false
    internal_tool_probe_path=''
    local -a normalized_arguments=("$@")
    local index=0
    while ((index < ${#normalized_arguments[@]})); do
        argument="${normalized_arguments[index]}"
        case "${argument}" in
            --help|-h)
                usage
                return 0
                ;;
            --dry-run)
                dry_run=true
                ;;
            --model|--reasoning-effort|--trials|--case-id|--index-path|--report-run-root|--internal-tool-probe-path)
                ((index + 1 < ${#normalized_arguments[@]})) || fail "Option ${argument} requires a value."
                index=$((index + 1))
                [[ "${normalized_arguments[index]}" != -* ]] || fail "Option ${argument} requires a value."
                case "${argument}" in
                    --model) model="${normalized_arguments[index]}" ;;
                    --reasoning-effort) reasoning_effort="${normalized_arguments[index]}" ;;
                    --trials) trials="${normalized_arguments[index]}" ;;
                    --case-id) case_id_option="${normalized_arguments[index]}" ;;
                    --index-path) index_path_option="$(normalize_git_bash_path "${normalized_arguments[index]}")" || fail 'Could not normalize the --index-path value.' ;;
                    --report-run-root) report_run_root="$(normalize_git_bash_path "${normalized_arguments[index]}")" || fail 'Could not normalize the --report-run-root value.' ;;
                    --internal-tool-probe-path) internal_tool_probe_path="$(normalize_git_bash_path "${normalized_arguments[index]}")" || fail 'Could not normalize the --internal-tool-probe-path value.' ;;
                esac
                ;;
            --model=*|--reasoning-effort=*|--trials=*|--case-id=*|--index-path=*|--report-run-root=*|--internal-tool-probe-path=*)
                local option_name="${argument%%=*}"
                local option_value="${argument#*=}"
                case "${option_name}" in
                    --model) model="${option_value}" ;;
                    --reasoning-effort) reasoning_effort="${option_value}" ;;
                    --trials) trials="${option_value}" ;;
                    --case-id) case_id_option="${option_value}" ;;
                    --index-path) index_path_option="$(normalize_git_bash_path "${option_value}")" || fail 'Could not normalize the --index-path value.' ;;
                    --report-run-root) report_run_root="$(normalize_git_bash_path "${option_value}")" || fail 'Could not normalize the --report-run-root value.' ;;
                    --internal-tool-probe-path) internal_tool_probe_path="$(normalize_git_bash_path "${option_value}")" || fail 'Could not normalize the --internal-tool-probe-path value.' ;;
                esac
                ;;
            *)
                fail "Unknown option: ${argument}"
                ;;
        esac
        index=$((index + 1))
    done
    [[ -n "${model}" ]] || fail '--model must not be empty.'
    [[ -n "${reasoning_effort}" ]] || fail '--reasoning-effort must not be empty.'
    [[ "${trials}" =~ ^[0-9]+$ ]] && ((trials >= 1 && trials <= 100)) || fail '--trials must be an integer from 1 through 100.'
    [[ -f "${SUPPORT_SCRIPT}" ]] || fail "Benchmark support module was not found: ${SUPPORT_SCRIPT}"
    python_executable="$(resolve_python)" || exit 1
    if [[ -n "${internal_tool_probe_path}" ]]; then
        run_support internal-tool-probe --output "${internal_tool_probe_path}"
        return 0
    fi
    resolve_repo_root
    cd -- "${repo_root}"
    if [[ -n "${report_run_root}" ]]; then
        [[ "${dry_run}" == false ]] || fail '--report-run-root cannot be combined with --dry-run.'
        local refreshed_root
        refreshed_root="$(run_support report --repo-root "${repo_root}" --run-root "${report_run_root}")"
        printf 'Benchmark reports refreshed: %s\n' "${refreshed_root}"
        return 0
    fi
    benchmark_index_path="$(run_support normalize-index-path --repo-root "${repo_root}" --index-path "${index_path_option}")"
    load_cases
    set_host_codex_home
    if [[ "${dry_run}" == true ]]; then
        build_disabled_features
        write_dry_run
        return 0
    fi
    command -v codex >/dev/null 2>&1 || fail "The installed 'codex' executable is required."
    unset CODEX_THREAD_ID
    build_disabled_features
    run_root="${repo_root}/artifacts/codex-benchmark/$(date +%Y%m%d-%H%M%S)"
    mkdir -p -- "${run_root}/answers" "${run_root}/events" "${run_root}/stderr" "${run_root}/commands"
    printf '[]\n' > "${run_root}/runs.json"
    manifest_path="${run_root}/repository-manifest.json"
    resolved_roslynkit_path=''
    trap 'stop_roslynkit_daemon' EXIT
    dotnet restore "${repo_root}/RoslynKit.slnx" --nologo --verbosity quiet || fail 'Repository restore failed before measured runs.'
    run_support manifest --repo-root "${repo_root}" --output "${manifest_path}"
    run_preflight
    printf 'Benchmark host preflight completed. Timing comparisons are valid only within this run.\n'
    "${resolved_roslynkit_path}" index --target ./RoslynKit.slnx --index-path "${benchmark_index_path}" || fail 'RoslynKit index preparation failed before measured runs.'
    stop_roslynkit_daemon
    check_manifest_unchanged 'during benchmark preparation'
    local trial condition case_index
    for ((trial = 1; trial <= trials; trial += 1)); do
        local -a conditions=(raw-codex roslynkit)
        ((trial % 2 == 0)) && conditions=(roslynkit raw-codex)
        for case_index in "${!case_ids[@]}"; do
            for condition in "${conditions[@]}"; do
                run_measured_case "${case_ids[case_index]}" "${case_prompts[case_index]}" "${condition}" "${trial}"
                [[ "${condition}" != roslynkit ]] || stop_roslynkit_daemon
            done
        done
    done
    local invalid_count
    invalid_count="$(grep -c '"valid": false' "${run_root}/runs.json" || true)"
    if ((invalid_count > 0)); then
        printf "warning: Benchmark completed with %s invalid measured session(s). Review '%s/summary.md'; comparisons use valid rows only.\n" "${invalid_count}" "${run_root}" >&2
    fi
    printf 'Benchmark complete: %s\n' "${run_root}"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    main "$@"
fi
