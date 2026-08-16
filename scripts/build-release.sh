#!/usr/bin/env bash
set -euo pipefail

usage() { echo "usage: scripts/build-release.sh VERSION [OUTPUT_DIRECTORY] [--powershell PATH] [--skip-tests]" >&2; }
[[ $# -ge 1 ]] || { usage; exit 2; }
version="$1"; shift
out_arg="artifacts/release"
powershell="${POWERSHELL_EXE:-}"
skip_tests=0
if [[ $# -gt 0 && "$1" != --* ]]; then out_arg="$1"; shift; fi
while [[ $# -gt 0 ]]; do
    case "$1" in
        --powershell) [[ $# -ge 2 ]] || { usage; exit 2; }; powershell="$2"; shift 2 ;;
        --skip-tests) skip_tests=1; shift ;;
        *) usage; exit 2 ;;
    esac
done

repo="$(cd "$(dirname "$0")/.." && pwd)"
dotnet_exe="$(command -v dotnet || true)"
[[ -n "$dotnet_exe" ]] || { echo "dotnet prerequisite missing." >&2; exit 2; }
if [[ "$skip_tests" != 1 ]]; then
    if [[ -z "$powershell" && -x /tmp/gcp-pwsh-tool/pwsh ]]; then powershell=/tmp/gcp-pwsh-tool/pwsh; fi
    if [[ -z "$powershell" ]]; then powershell="$(command -v pwsh || true)"; fi
    if [[ "$powershell" != */* ]]; then powershell="$(command -v "$powershell" || true)"; fi
    [[ -n "$powershell" && -f "$powershell" && -x "$powershell" ]] || { echo "PowerShell prerequisite missing: install pwsh, set POWERSHELL_EXE, or pass --powershell PATH." >&2; exit 2; }
    powershell="$(cd "$(dirname "$powershell")" && pwd)/$(basename "$powershell")"
fi
case "$out_arg" in /*) out="$out_arg" ;; *) out="$repo/$out_arg" ;; esac
work="$(mktemp -d "${TMPDIR:-/tmp}/godot-csharp-profiler-release.XXXXXXXX")"
cleanup() { status=$?; trap - EXIT HUP INT TERM; rm -rf -- "$work"; exit "$status"; }
trap cleanup EXIT; trap 'exit 129' HUP; trap 'exit 130' INT; trap 'exit 143' TERM

args=(--repository "$repo" --output "$out" --workspace "$work" --version "$version" --dotnet "$dotnet_exe")
if [[ "$skip_tests" == 1 ]]; then args+=(--skip-tests); else args+=(--powershell "$powershell"); fi
dotnet run --project "$repo/scripts/GodotCSharpProfiler.ReleaseBuilder/GodotCSharpProfiler.ReleaseBuilder.csproj" -c Release -- "${args[@]}"
