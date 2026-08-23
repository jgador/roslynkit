#!/usr/bin/env bash
# Runs the benchmark helper and keeps Codex judge execution in Bash.
set -euo pipefail

# The C# helper owns every option, default, and validation rule. The controller
# forwards user arguments verbatim and drives Codex from the helper's control
# directive, so Bash holds no option contract of its own.

REPOSITORY_ROOT=""
BENCHMARK_PROJECT=""

# The helper owns benchmark state and retrieval; this script owns process-level orchestration.
usage() {
    cat <<'EOF'
Usage:
  scripts/benchmark.sh [options]

The C# benchmark helper is the single source of truth for options, defaults, and
validation. Run without options for the default suite, or pass helper options
such as --model, --reasoning-effort, --trials, --case, --max-results,
--index-path, --roslynkit-path, --dry-run, --resume-run-root, or
--report-run-root. See docs/benchmark.md for the full option reference.

Two controller-owned options work without building the helper:
  --clean   Remove benchmark-owned local artifacts (exclusive)
  --help    Show this help
EOF
}

die() {
    printf 'error: %s\n' "$*" >&2
    exit 2
}

has_flag() {
    local needle="$1"
    shift
    local argument
    for argument in "$@"; do
        if [[ "$argument" == "$needle" ]]; then
            return 0
        fi
    done

    return 1
}

# Cleanup is deliberately limited to fixed, benchmark-owned paths and rejects symlink traversal.
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

# Run prepared sessions serially so every prompt, judge transcript, and evaluation share one run ID.
run_sessions() {
    local run_root="$1"
    local model="$2"
    local reasoning_effort="$3"
    shift 3
    local -a run_ids=("$@")
    local run_id
    local prompt_path
    local answer_path
    local event_path
    local stderr_path
    local exit_code

    [[ -n "$model" ]] || die "The benchmark helper did not return a model for the run."
    [[ -n "$reasoning_effort" ]] || die "The benchmark helper did not return a reasoning effort for the run."

    if ((${#run_ids[@]} > 0)); then
        [[ -d "$run_root/answers" && -d "$run_root/events" && -d "$run_root/stderr" ]] \
            || die "The benchmark helper did not create the required artifact directories."
        if ! command -v codex >/dev/null 2>&1; then
            die "Codex executable was not found on PATH; install Codex or resume after it is available."
        fi

        # Host-injected thread state is not a supported codex exec input. Keep every
        # judge ephemeral and independent from the controller's conversation.
        unset CODEX_THREAD_ID

        for run_id in "${run_ids[@]}"; do
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
                --model "$model" \
                --config "model_reasoning_effort=\"$reasoning_effort\"" \
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
        done
    fi

    benchmark_helper report --run-root "$run_root"
}

# Parse the helper's control directive and execute only the Codex-owned work.
run_from_control() {
    local control
    control="$(benchmark_helper prepare "$@")"

    local action=""
    local run_root=""
    local model=""
    local reasoning_effort=""
    local -a sessions=()
    local line

    while IFS= read -r line || [[ -n "$line" ]]; do
        line="${line%$'\r'}"
        [[ -n "$line" ]] || continue
        case "$line" in
            action=*) action="${line#action=}" ;;
            run-root=*) run_root="${line#run-root=}" ;;
            model=*) model="${line#model=}" ;;
            reasoning-effort=*) reasoning_effort="${line#reasoning-effort=}" ;;
            session=*) sessions+=("${line#session=}") ;;
            *) die "The benchmark helper produced an unknown control line: '$line'." ;;
        esac
    done <<<"$control"

    case "$action" in
        dry-run | report)
            return 0
            ;;
        run)
            [[ -n "$run_root" ]] || die "The benchmark helper did not return a run root."
            is_absolute_path "$run_root" \
                || die "The benchmark helper did not return an absolute run root."
            run_root="$(to_shell_path "$run_root")"
            [[ -d "$run_root" ]] || die "The benchmark helper returned a missing run root: '$run_root'."
            # Bash 3.2 rejects an empty array expansion under `set -u`, and a fully
            # completed resume returns no session lines, so forward ids only when present.
            if ((${#sessions[@]} > 0)); then
                run_sessions "$run_root" "$model" "$reasoning_effort" "${sessions[@]}"
            else
                run_sessions "$run_root" "$model" "$reasoning_effort"
            fi
            ;;
        "")
            die "The benchmark helper did not return a control action."
            ;;
        *)
            die "The benchmark helper produced an unknown control action: '$action'."
            ;;
    esac
}

# Handle build-free controller modes before building the helper or starting a judge session.
main() {
    local script_directory

    script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
    REPOSITORY_ROOT="$(cd -- "$script_directory/.." && pwd -P)"
    BENCHMARK_PROJECT="$REPOSITORY_ROOT/tests/Integration/Benchmarking/RoslynKit.Benchmarking.csproj"
    cd -- "$REPOSITORY_ROOT"

    # Only standalone --help/-h is answered without building the helper. Any other
    # combination is forwarded so the helper validates it, which keeps the
    # controller from silently honoring help while ignoring invalid modifiers such
    # as `--help --bogus` or bypassing `--clean` exclusivity in `--clean --help`.
    if (($# == 1)) && [[ "$1" == "--help" || "$1" == "-h" ]]; then
        usage
        return 0
    fi

    if has_flag --clean "$@"; then
        if (($# != 1)); then
            die "--clean cannot be combined with other options."
        fi

        clean_benchmark_artifacts
        return 0
    fi

    [[ -f "$BENCHMARK_PROJECT" ]] || die "The benchmark helper project was not found: '$BENCHMARK_PROJECT'."
    build_helper

    run_from_control "$@"
}

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    main "$@"
fi
