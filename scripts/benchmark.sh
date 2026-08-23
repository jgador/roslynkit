#!/usr/bin/env bash
# Runs the benchmark helper and keeps Codex judge execution in Bash.
set -euo pipefail

readonly DEFAULT_MODEL="gpt-5.6-terra"
readonly DEFAULT_REASONING_EFFORT="high"
readonly DEFAULT_TRIALS="1"
readonly DEFAULT_CASE="all"
readonly DEFAULT_MAX_RESULTS="10"
readonly DEFAULT_INDEX_PATH="./artifacts/roslynkit-text.db"

MODEL="$DEFAULT_MODEL"
REASONING_EFFORT="$DEFAULT_REASONING_EFFORT"
TRIALS="$DEFAULT_TRIALS"
CASE_ID="$DEFAULT_CASE"
MAX_RESULTS="$DEFAULT_MAX_RESULTS"
INDEX_PATH="$DEFAULT_INDEX_PATH"
ROSLYNKIT_PATH=""
DRY_RUN=false
RESUME_RUN_ROOT=""
REPORT_RUN_ROOT=""
CLEAN=false
SHOW_HELP=false
REPOSITORY_ROOT=""
BENCHMARK_PROJECT=""
SEEN_OPTIONS=""

usage() {
    cat <<'EOF'
Usage:
  scripts/benchmark.sh [options]

Options:
  --model <id>                 Codex model (default: gpt-5.6-terra)
  --reasoning-effort <level>   Codex reasoning effort (default: high)
  --trials <1-100>             Trials per selected case (default: 1)
  --case <id|all>              Select one case or all cases (default: all)
  --case-id <id|all>           Compatibility alias for --case
  --max-results <2-50>         Maximum RoslynKit results (default: 10)
  --index-path <path>          Database directly below ./artifacts/
  --roslynkit-path <path>      Use an existing RoslynKit apphost
  --dry-run                    Print the schedule without starting Codex
  --resume-run-root <path>     Resume unfinished sessions from one run document
  --report-run-root <path>     Regenerate CSV and Markdown from one run document
  --clean                      Remove benchmark-owned local artifacts
  --help                       Show this help
EOF
}

die() {
    printf 'error: %s\n' "$*" >&2
    exit 2
}

require_value() {
    local option="$1"
    local value="${2-}"

    if [[ -z "$value" || "$value" == --* ]]; then
        die "$option requires a value."
    fi
}

mark_once() {
    local canonical_name="$1"
    local option="$2"

    if [[ " $SEEN_OPTIONS " == *" $canonical_name "* ]]; then
        die "Option '$option' was specified more than once."
    fi

    SEEN_OPTIONS+="$canonical_name "
}

parse_options() {
    SEEN_OPTIONS=""

    while (($# > 0)); do
        case "$1" in
            --help|-h)
                mark_once "help" "$1"
                SHOW_HELP=true
                shift
                ;;
            --model)
                mark_once "model" "$1"
                require_value "$1" "${2-}"
                MODEL="$2"
                shift 2
                ;;
            --reasoning-effort)
                mark_once "reasoning-effort" "$1"
                require_value "$1" "${2-}"
                REASONING_EFFORT="$2"
                shift 2
                ;;
            --trials)
                mark_once "trials" "$1"
                require_value "$1" "${2-}"
                TRIALS="$2"
                shift 2
                ;;
            --case|--case-id)
                mark_once "case" "$1"
                require_value "$1" "${2-}"
                CASE_ID="$2"
                shift 2
                ;;
            --max-results)
                mark_once "max-results" "$1"
                require_value "$1" "${2-}"
                MAX_RESULTS="$2"
                shift 2
                ;;
            --index-path)
                mark_once "index-path" "$1"
                require_value "$1" "${2-}"
                INDEX_PATH="$2"
                shift 2
                ;;
            --roslynkit-path)
                mark_once "roslynkit-path" "$1"
                require_value "$1" "${2-}"
                ROSLYNKIT_PATH="$2"
                shift 2
                ;;
            --dry-run)
                mark_once "dry-run" "$1"
                DRY_RUN=true
                shift
                ;;
            --resume-run-root)
                mark_once "resume-run-root" "$1"
                require_value "$1" "${2-}"
                RESUME_RUN_ROOT="$2"
                shift 2
                ;;
            --report-run-root)
                mark_once "report-run-root" "$1"
                require_value "$1" "${2-}"
                REPORT_RUN_ROOT="$2"
                shift 2
                ;;
            --clean)
                mark_once "clean" "$1"
                CLEAN=true
                shift
                ;;
            *)
                die "Unknown benchmark option: '$1'."
                ;;
        esac
    done

    if [[ -n "$RESUME_RUN_ROOT" && -n "$REPORT_RUN_ROOT" ]]; then
        die "--resume-run-root and --report-run-root are mutually exclusive."
    fi

    if [[ "$DRY_RUN" == true && ( -n "$RESUME_RUN_ROOT" || -n "$REPORT_RUN_ROOT" ) ]]; then
        die "--dry-run cannot be combined with resume or report mode."
    fi

    if [[ "$CLEAN" == true && "$SEEN_OPTIONS" != "clean " ]]; then
        die "--clean cannot be combined with other options."
    fi
}

clean_benchmark_artifacts() {
    local artifacts_root="$REPOSITORY_ROOT/artifacts"
    local removed=0
    local target
    local protocol_database
    local -a targets=(
        "$artifacts_root/benchmark"
        "$artifacts_root/benchmark-integration"
        "$artifacts_root/roslynkit-text.db"
        "$artifacts_root/roslynkit-text.db-shm"
        "$artifacts_root/roslynkit-text.db-wal"
        "$artifacts_root/bin/RoslynKit.Benchmarking"
        "$artifacts_root/obj/RoslynKit.Benchmarking"
        "$artifacts_root/bin/RoslynKit.Benchmarking.Tests"
        "$artifacts_root/obj/RoslynKit.Benchmarking.Tests"
        "$artifacts_root/bin/RoslynKit/release"
        "$artifacts_root/obj/RoslynKit/release"
    )

    if [[ -L "$artifacts_root" ]]; then
        die "Refusing to clean through symlinked artifacts root: '$artifacts_root'."
    fi

    for target in "${targets[@]}"; do
        require_no_symlink_components "$artifacts_root" "$target"
    done

    for target in "${targets[@]}"; do
        if [[ -e "$target" || -L "$target" ]]; then
            rm -rf -- "$target"
            printf 'Removed: %s\n' "${target#"$REPOSITORY_ROOT/"}"
            removed=$((removed + 1))
        fi
    done

    if [[ -d "$artifacts_root" ]]; then
        while IFS= read -r -d '' protocol_database; do
            rm -f -- "$protocol_database"
            printf 'Removed: %s\n' "${protocol_database#"$REPOSITORY_ROOT/"}"
            removed=$((removed + 1))
        done < <(
            find "$artifacts_root" -maxdepth 1 -type f \
                \( -name 'benchmark-protocol-*.db' \
                -o -name 'benchmark-protocol-*.db-shm' \
                -o -name 'benchmark-protocol-*.db-wal' \) \
                -print0
        )
    fi

    if ((removed == 0)); then
        printf 'Benchmark artifacts already clean.\n'
    else
        printf 'Benchmark artifacts cleaned.\n'
    fi
}

require_no_symlink_components() {
    local root="$1"
    local path="$2"
    local relative_path="${path#"$root/"}"
    local current_path="$root"
    local component
    local -a components

    if [[ "$relative_path" == "$path" ]]; then
        die "Refusing to clean a path outside the artifacts root: '$path'."
    fi

    IFS='/' read -r -a components <<<"$relative_path"
    for component in "${components[@]}"; do
        current_path="$current_path/$component"
        if [[ -L "$current_path" ]]; then
            die "Refusing to clean through symlinked artifact path: '$current_path'."
        fi
    done
}

build_helper() {
    dotnet build "$BENCHMARK_PROJECT" \
        --configuration Release \
        --tl:off \
        --nologo \
        "-clp:ErrorsOnly;NoSummary"
}

benchmark_helper() {
    dotnet run --project "$BENCHMARK_PROJECT" \
        --configuration Release \
        --no-build \
        -- "$@"
}

is_absolute_path() {
    local path="$1"

    [[ "$path" == /* || "$path" =~ ^[A-Za-z]:[\\/] ]]
}

to_shell_path() {
    local path="$1"

    if command -v cygpath >/dev/null 2>&1 && [[ "$path" =~ ^[A-Za-z]:[\\/] ]]; then
        cygpath --unix "$path"
        return
    fi

    printf '%s\n' "$path"
}

require_safe_run_id() {
    local run_id="$1"

    if [[ ! "$run_id" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]]; then
        die "The benchmark helper produced an unsafe run id: '$run_id'."
    fi
}

read_config_value() {
    local path="$1"
    local name="$2"
    local value=""
    local extra_line=""

    [[ -f "$path" ]] || die "The benchmark helper did not create '$path'."
    if ! exec 3<"$path"; then
        die "The benchmark helper created an unreadable $name value."
    fi
    if IFS= read -r value <&3; then
        :
    elif [[ -z "$value" ]]; then
        exec 3<&-
        die "The benchmark helper wrote an empty $name value."
    fi

    if IFS= read -r extra_line <&3; then
        exec 3<&-
        die "The benchmark helper wrote a multiline $name value."
    elif [[ -n "$extra_line" ]]; then
        exec 3<&-
        die "The benchmark helper wrote a multiline $name value."
    fi
    exec 3<&-

    value="${value%$'\r'}"
    if [[ -z "$value" || "$value" == *$'\r'* ]]; then
        die "The benchmark helper wrote an invalid $name value."
    fi

    printf '%s\n' "$value"
}

run_sessions() {
    local run_root="$1"
    local schedule_path="$run_root/schedule.txt"
    local run_id
    local prompt_path
    local answer_path
    local event_path
    local stderr_path
    local exit_code

    [[ -f "$schedule_path" ]] || die "The benchmark helper did not create '$schedule_path'."
    [[ -d "$run_root/answers" && -d "$run_root/events" && -d "$run_root/stderr" ]] \
        || die "The benchmark helper did not create the required artifact directories."
    MODEL="$(read_config_value "$run_root/model.txt" "model")"
    REASONING_EFFORT="$(read_config_value "$run_root/reasoning-effort.txt" "reasoning-effort")"
    if [[ -s "$schedule_path" ]] && ! command -v codex >/dev/null 2>&1; then
        die "Codex executable was not found on PATH; install Codex or resume after it is available."
    fi

    # Host-injected thread state is not a supported codex exec input. Keep every
    # judge ephemeral and independent from the controller's conversation.
    unset CODEX_THREAD_ID

    while IFS= read -r run_id || [[ -n "$run_id" ]]; do
        run_id="${run_id%$'\r'}"
        [[ -n "$run_id" ]] || die "The benchmark schedule contains an empty run id."
        require_safe_run_id "$run_id"

        prompt_path="$(benchmark_helper prepare-session --run-root "$run_root" --run-id "$run_id")"
        prompt_path="${prompt_path%$'\r'}"
        is_absolute_path "$prompt_path" \
            || die "The benchmark helper did not return an absolute prompt path for '$run_id'."
        prompt_path="$(to_shell_path "$prompt_path")"
        [[ -f "$prompt_path" ]] || die "The benchmark helper did not create '$prompt_path'."

        answer_path="$run_root/answers/$run_id.md"
        event_path="$run_root/events/$run_id.jsonl"
        stderr_path="$run_root/stderr/$run_id.txt"
        printf 'Running benchmark session: %s\n' "$run_id" >&2

        if codex exec \
            --json \
            --ephemeral \
            --ignore-rules \
            --sandbox read-only \
            --model "$MODEL" \
            --config "model_reasoning_effort=\"$REASONING_EFFORT\"" \
            --cd "$REPOSITORY_ROOT" \
            --output-last-message "$answer_path" \
            - \
            <"$prompt_path" \
            >"$event_path" \
            2>"$stderr_path"
        then
            exit_code=0
        else
            exit_code=$?
        fi

        benchmark_helper evaluate-session \
            --run-root "$run_root" \
            --run-id "$run_id" \
            --exit-code "$exit_code"
    done <"$schedule_path"

    benchmark_helper report --run-root "$run_root"
}

main() {
    local script_directory
    local run_root
    local -a prepare_arguments

    script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
    REPOSITORY_ROOT="$(cd -- "$script_directory/.." && pwd -P)"
    BENCHMARK_PROJECT="$REPOSITORY_ROOT/tests/Integration/Benchmarking/RoslynKit.Benchmarking.csproj"
    cd -- "$REPOSITORY_ROOT"

    parse_options "$@"
    if [[ "$SHOW_HELP" == true ]]; then
        usage
        return 0
    fi
    if [[ "$CLEAN" == true ]]; then
        clean_benchmark_artifacts
        return 0
    fi

    [[ -f "$BENCHMARK_PROJECT" ]] || die "The benchmark helper project was not found: '$BENCHMARK_PROJECT'."
    build_helper

    if [[ -n "$REPORT_RUN_ROOT" ]]; then
        benchmark_helper report --run-root "$REPORT_RUN_ROOT"
        return 0
    fi

    prepare_arguments=(
        prepare
        --model "$MODEL"
        --reasoning-effort "$REASONING_EFFORT"
        --trials "$TRIALS"
        --case "$CASE_ID"
        --max-results "$MAX_RESULTS"
        --index-path "$INDEX_PATH"
    )
    if [[ -n "$ROSLYNKIT_PATH" ]]; then
        prepare_arguments+=(--roslynkit-path "$ROSLYNKIT_PATH")
    fi
    if [[ "$DRY_RUN" == true ]]; then
        prepare_arguments+=(--dry-run)
        benchmark_helper "${prepare_arguments[@]}"
        return 0
    fi
    if [[ -n "$RESUME_RUN_ROOT" ]]; then
        prepare_arguments+=(--resume-run-root "$RESUME_RUN_ROOT")
    fi

    run_root="$(benchmark_helper "${prepare_arguments[@]}")"
    run_root="${run_root%$'\r'}"
    is_absolute_path "$run_root" \
        || die "The benchmark helper did not return an absolute run root."
    run_root="$(to_shell_path "$run_root")"
    [[ -d "$run_root" ]] || die "The benchmark helper returned a missing run root: '$run_root'."

    run_sessions "$run_root"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    main "$@"
fi
