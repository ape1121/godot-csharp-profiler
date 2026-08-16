#!/usr/bin/env bash
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
powershell="${POWERSHELL_EXE:-$(command -v pwsh || true)}"
if [[ -z "$powershell" || ! -x "$powershell" ]]; then
    echo "PowerShell prerequisite missing: install pwsh or set POWERSHELL_EXE before running release cleanup tests." >&2
    exit 2
fi

assert_clean() {
    local out="$1"
    if [[ -e "$out/godot-csharp-profiler-0.1.0-cleanup-test.zip" || -e "$out/SHA256SUMS" || -e "$out/release.json" ]]; then
        echo "Forced release failure published a claimed artifact: $out" >&2
        exit 1
    fi
    if [[ -d "$out/stage" ]] || [[ -n "$(find "$out" -type f \( -name '*.cs' -o -name '*.tmp' \) -print -quit 2>/dev/null)" ]]; then
        echo "Forced release failure left staged source or temporary files: $out" >&2
        exit 1
    fi
}

bash_out="$repo/artifacts/cleanup-regression-bash"
ps_out="$repo/artifacts/cleanup-regression-powershell"
rm -rf -- "$bash_out" "$ps_out"

if GCP_RELEASE_TEST_FAIL_AFTER_ZIP=1 "$repo/scripts/build-release.sh" 0.1.0-cleanup-test artifacts/cleanup-regression-bash --powershell "$powershell"; then
    echo "Bash release unexpectedly succeeded during forced failure." >&2
    exit 1
fi
assert_clean "$bash_out"

env GCP_RELEASE_TEST_FAIL_AFTER_ZIP=1 "$powershell" -NoProfile -File "$repo/scripts/build-release.ps1" -Version 0.1.0-cleanup-test -OutputDirectory artifacts/cleanup-regression-powershell -SkipTests && {
    echo "PowerShell release unexpectedly succeeded during forced failure." >&2
    exit 1
}
assert_clean "$ps_out"

# This is deliberately immediate: recursive SDK compile would expose leaked staged .cs.
dotnet build "$repo/GodotCSharpProfiler.csproj" -c Release --nologo
rm -rf -- "$bash_out" "$ps_out"
echo "Release forced-failure cleanup and immediate root addon build passed."
