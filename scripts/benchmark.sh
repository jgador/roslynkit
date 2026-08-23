#!/usr/bin/env bash
# Runs the benchmark helper and keeps Codex judge execution in Bash.
set -euo pipefail

# The C# helper owns every option, default, and validation rule. The controller
# forwards user arguments verbatim and drives Codex from the helper's control
# directive, so Bash holds no option contract of its own.

REPOSITORY_ROOT=""
BENCHMARK_PROJECT=""

# Print the small amount of usage text that belongs to this Bash controller. The
# C# helper owns all benchmark options, so this deliberately lists only the two
# options that work without building the helper.
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
  --clean   Remove every artifact except artifacts/.gitkeep (exclusive)
  --help    Show this help
EOF
}

# Print a consistent error message to standard error, then stop with exit code 2
# (the conventional exit code for invalid command-line usage or unsafe input).
die() {
    printf 'error: %s\n' "$*" >&2
    exit 2
}

# Return success when the remaining command-line arguments contain the requested
# flag. Bash uses a zero exit code for true, which lets callers use this directly
# in an `if` condition without producing output.
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

# Remove every artifact entry except .gitkeep. Cleanup is intentionally strict:
# it refuses a redirected root so the `rm -rf` below can never traverse outside
# this repository's own artifacts directory.
clean_benchmark_artifacts() {
    local artifacts_root="$REPOSITORY_ROOT/artifacts"
    local physical_artifacts_root
    local removed=0
    local artifact_entry

    if [[ -L "$artifacts_root" ]]; then
        die "Refusing to clean through symlinked artifacts root: '$artifacts_root'."
    fi

    if [[ -e "$artifacts_root" && ! -d "$artifacts_root" ]]; then
        die "Refusing to clean a non-directory artifacts root: '$artifacts_root'."
    fi

    if [[ -d "$artifacts_root" ]]; then
        # `-L` recognizes ordinary symbolic links on Unix. Git Bash on Windows
        # can represent a redirected directory as a reparse point instead, for
        # which `-L` is false. Resolving the directory physically catches both
        # forms before `find` enumerates entries or `rm -rf` removes anything.
        physical_artifacts_root="$(cd -- "$artifacts_root" && pwd -P)"
        if [[ "$physical_artifacts_root" != "$artifacts_root" ]]; then
            die "Refusing to clean through symlinked artifacts root: '$artifacts_root'."
        fi

        while IFS= read -r -d '' artifact_entry; do
            rm -rf -- "$artifact_entry"
            printf 'Removed: %s\n' "${artifact_entry#"$REPOSITORY_ROOT/"}"
            removed=$((removed + 1))
        done < <(find "$artifacts_root" -mindepth 1 -maxdepth 1 ! -name '.gitkeep' -print0)
    fi

    if ((removed == 0)); then
        printf 'Benchmark artifacts already clean.\n'
    else
        printf 'Benchmark artifacts cleaned.\n'
    fi
}

# Build the C# helper once for commands that need benchmark preparation,
# retrieval, evaluation, or reporting. Cleanup and help bypass this function.
build_helper() {
    dotnet build "$BENCHMARK_PROJECT" \
        --configuration Release \
        --tl:off \
        --nologo \
        "-clp:ErrorsOnly;NoSummary"
}

# Invoke the already-built C# helper. Keeping this in one function means every
# controller operation uses the same project, configuration, and argument pass-through.
benchmark_helper() {
    dotnet run --project "$BENCHMARK_PROJECT" \
        --configuration Release \
        --no-build \
        -- "$@"
}

# Return success only for an absolute Unix path or an absolute Windows drive path.
# The helper may run on either platform, so later file operations must not accept
# a relative path that could escape the intended benchmark run directory.
is_absolute_path() {
    local path="$1"

    [[ "$path" == /* || "$path" =~ ^[A-Za-z]:[\\/] ]]
}

# Convert a Windows drive path to the slash-based form used by Git Bash when
# `cygpath` is available. On Unix and already-compatible paths, print the input
# unchanged so callers can use one cross-platform code path.
to_shell_path() {
    local path="$1"

    if command -v cygpath >/dev/null 2>&1 && [[ "$path" =~ ^[A-Za-z]:[\\/] ]]; then
        cygpath --unix "$path"
        return
    fi

    printf '%s\n' "$path"
}

# Accept only simple helper-provided run IDs before using them as artifact file
# names. This blocks path separators and other characters that could redirect a
# prompt, answer, event, or error log outside the selected run directory.
require_safe_run_id() {
    local run_id="$1"

    if [[ ! "$run_id" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]]; then
        die "The benchmark helper produced an unsafe run id: '$run_id'."
    fi
}

# Run prepared judge sessions serially. Each session has one run ID shared by its
# prompt, answer, event transcript, error log, and evaluator result; serial work
# keeps those artifacts deterministic and avoids overlapping paid Codex sessions.
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

# Ask the helper to prepare a command, parse its machine-readable control
# directive, and perform only the Bash-owned Codex work. The helper owns option
# parsing and benchmark state; Bash owns the actual judge process invocation.
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

# Establish repository paths, then route the top-level command. Help and cleanup
# return before a build or a Codex session; every other request is validated by
# the C# helper before this controller starts any paid judge work.
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
