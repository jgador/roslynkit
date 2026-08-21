#!/usr/bin/env bash

set -euo pipefail

export PYTHONDONTWRITEBYTECODE=1

readonly script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
python_executable=''
for candidate in python3 python; do
    if command -v "${candidate}" >/dev/null 2>&1 \
        && "${candidate}" -c 'import sys; raise SystemExit(0 if sys.version_info >= (3, 10) else 1)' >/dev/null 2>&1; then
        python_executable="$(command -v "${candidate}")"
        break
    fi
done

if [[ -z "${python_executable}" ]]; then
    printf '%s\n' 'error: Python 3.10 or later is required.' >&2
    exit 1
fi

exec "${python_executable}" "${script_dir}/benchmark_search_text.py" "$@"
