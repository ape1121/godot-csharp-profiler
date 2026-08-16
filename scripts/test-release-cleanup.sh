#!/usr/bin/env bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
powershell="${POWERSHELL_EXE:-}"
[[ -n "$powershell" ]] || [[ ! -x /tmp/gcp-pwsh-tool/pwsh ]] || powershell=/tmp/gcp-pwsh-tool/pwsh
[[ -n "$powershell" ]] || powershell="$(command -v pwsh || true)"
[[ -n "$powershell" && -x "$powershell" ]] || { echo "PowerShell prerequisite missing." >&2; exit 2; }
version=0.1.0-wrapper-equality
root="$repo/artifacts/release-regression"
rm -rf -- "$root"; mkdir -p "$root"
cleanup() { rm -rf -- "$root"; }
trap cleanup EXIT

"$repo/scripts/build-release.sh" "$version" "$root/bash-one" --powershell "$powershell"
"$repo/scripts/build-release.sh" "$version" "$root/bash-two" --skip-tests
"$powershell" -NoProfile -File "$repo/scripts/build-release.ps1" -Version "$version" -OutputDirectory "$root/pwsh-one" -SkipTests
"$powershell" -NoProfile -File "$repo/scripts/build-release.ps1" -Version "$version" -OutputDirectory "$root/pwsh-two" -SkipTests
archive="godot-csharp-profiler-$version.zip"
for file in "$archive" SHA256SUMS release.json; do
    for candidate in bash-two pwsh-one pwsh-two; do cmp "$root/bash-one/$file" "$root/$candidate/$file"; done
done

assert_no_claim() {
    local out="$1"
    [[ ! -e "$out/$archive" && ! -e "$out/SHA256SUMS" && ! -e "$out/release.json" ]] || { echo "Forced failure published artifacts: $out" >&2; exit 1; }
    [[ -z "$(find "$out" -type f \( -name '*.tmp' -o -name '*.cs' \) -print -quit 2>/dev/null)" ]] || { echo "Forced failure leaked staging: $out" >&2; exit 1; }
}
if GCP_RELEASE_TEST_FAIL_AFTER_ZIP=1 "$repo/scripts/build-release.sh" "$version" "$root/fail-bash" --skip-tests; then echo "Bash forced failure succeeded." >&2; exit 1; fi
assert_no_claim "$root/fail-bash"
if env GCP_RELEASE_TEST_FAIL_AFTER_ZIP=1 "$powershell" -NoProfile -File "$repo/scripts/build-release.ps1" -Version "$version" -OutputDirectory "$root/fail-pwsh" -SkipTests; then echo "PowerShell forced failure succeeded." >&2; exit 1; fi
assert_no_claim "$root/fail-pwsh"

mkdir "$root/preserve"; cp "$root/bash-one/"* "$root/preserve/"
if GCP_RELEASE_TEST_FAIL_AFTER_ZIP=1 "$repo/scripts/build-release.sh" "$version" "$root/preserve" --skip-tests; then echo "Replacement forced failure succeeded." >&2; exit 1; fi
for file in "$archive" SHA256SUMS release.json; do cmp "$root/bash-one/$file" "$root/preserve/$file"; done

dotnet build "$repo/GodotCSharpProfiler.csproj" -c Release --nologo
printf 'Cross-wrapper equality, repeatability, raw/add/remove validation, failure cleanup, prior-output preservation, and root build passed.\nSHA-256: '
cut -d' ' -f1 "$root/bash-one/SHA256SUMS"
