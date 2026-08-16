#!/usr/bin/env bash
set -euo pipefail

usage() {
    echo "usage: scripts/build-release.sh VERSION [OUTPUT_DIRECTORY] [--powershell PATH]" >&2
}

[[ $# -ge 1 ]] || { usage; exit 2; }
version="$1"
shift
out_arg="artifacts/release"
powershell="${POWERSHELL_EXE:-}"
if [[ $# -gt 0 && "$1" != "--powershell" ]]; then
    out_arg="$1"
    shift
fi
while [[ $# -gt 0 ]]; do
    case "$1" in
        --powershell)
            [[ $# -ge 2 ]] || { usage; exit 2; }
            powershell="$2"
            shift 2
            ;;
        *) usage; exit 2 ;;
    esac
done
case "$version" in *[!0-9A-Za-z.-]*|'') echo "invalid version" >&2; exit 2 ;; esac

# The clean-project matrix executes setup.ps1. Check this before creating any
# staging directory or claimed artifact.
if [[ -z "$powershell" ]]; then
    powershell="$(command -v pwsh || true)"
elif [[ "$powershell" != */* ]]; then
    powershell="$(command -v "$powershell" || true)"
fi
if [[ -z "$powershell" || ! -f "$powershell" || ! -x "$powershell" ]]; then
    echo "PowerShell prerequisite missing: install pwsh, set POWERSHELL_EXE, or pass --powershell PATH." >&2
    exit 2
fi

repo="$(cd "$(dirname "$0")/.." && pwd)"
out="$repo/$out_arg"
work=""
zip_tmp=""
sums_tmp=""
json_tmp=""
final_zip=""
final_sums=""
final_json=""
published=0
cleanup() {
    local status=$?
    trap - EXIT HUP INT TERM
    [[ -z "$work" ]] || rm -rf -- "$work"
    [[ -z "$zip_tmp" ]] || rm -f -- "$zip_tmp"
    [[ -z "$sums_tmp" ]] || rm -f -- "$sums_tmp"
    [[ -z "$json_tmp" ]] || rm -f -- "$json_tmp"
    if [[ "$published" != "1" ]]; then
        [[ -z "$final_zip" ]] || rm -f -- "$final_zip"
        [[ -z "$final_sums" ]] || rm -f -- "$final_sums"
        [[ -z "$final_json" ]] || rm -f -- "$final_json"
    fi
    exit "$status"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

work="$(mktemp -d "${TMPDIR:-/tmp}/godot-csharp-profiler-release.XXXXXXXX")"
stage="$work/stage"
mkdir -p "$stage/addons"
cp -R "$repo/addons/godot_csharp_profiler" "$stage/addons/"
addon="$stage/addons/godot_csharp_profiler"
mkdir -p "$addon/assets/nuget"
python3 - "$addon" "$version" <<'PY'
from pathlib import Path
import json, re, sys
addon, version = Path(sys.argv[1]), sys.argv[2]
p = addon/'Runtime/Sampling/ManagedSamplingSession.cs'; p.write_text('#if GODOT_CSHARP_PROFILER_SAMPLING\n'+p.read_text()+'\n#endif\n')
p = addon/'plugin.cfg'; p.write_text(re.sub(r'version="[^"]+"', f'version="{version}"', p.read_text()))
p = addon/'Editor/Installation/ProjectInstaller.cs'; p.write_text(p.read_text().replace('ProfilerFodyVersion = "0.1.0-dev"', f'ProfilerFodyVersion = "{version}"'))
p = addon/'assets/dependencies.json'; data=json.loads(p.read_text()); data['bootstrapPackage']['version']=version; data['automatic']['packages']['GodotCSharpProfiler.Fody']=version; p.write_text(json.dumps(data, indent=2)+'\n')
p = addon/'assets/setup.ps1'; p.write_text(p.read_text().replace("@('GodotCSharpProfiler.Fody','0.1.0-dev')", f"@('GodotCSharpProfiler.Fody','{version}')"))
PY
pack_targets="$work/GodotCSharpProfiler.PackageReadme.targets"
cat > "$pack_targets" <<EOF
<Project><PropertyGroup><PackageReadmeFile>README.md</PackageReadmeFile></PropertyGroup><ItemGroup><None Include="$addon/README.md" Pack="true" PackagePath="/" /></ItemGroup></Project>
EOF
dotnet pack "$repo/src/GodotCSharpProfiler.Fody/GodotCSharpProfiler.Fody.csproj" -c Release -p:Version="$version" -p:NoWarn=NU5100%3BNU5128 -p:DirectoryBuildTargetsPath="$pack_targets" -o "$addon/assets/nuget"
python3 - "$addon/assets/nuget/GodotCSharpProfiler.Fody.$version.nupkg" <<'PY'
from pathlib import Path
import re, sys, zipfile
p=Path(sys.argv[1])
with zipfile.ZipFile(p) as z:
    raw=[(i.filename,z.read(i)) for i in z.infolist()]
old=next(name for name,_ in raw if name.startswith('package/services/metadata/core-properties/'))
fixed='package/services/metadata/core-properties/godot-csharp-profiler.psmdcp'
entries=[]
for name,data in raw:
    if name==old: name=fixed
    if name=='_rels/.rels':
        text=data.decode(); text=re.sub(r'(Type="http://schemas.microsoft.com/packaging/2010/07/manifest"[^>]*Id=")[^"]+"', r'\1RMANIFEST"', text); text=re.sub(r'(Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties"[^>]*Id=")[^"]+"', r'\1RMETADATA"', text); text=re.sub(r'Target="/package/services/metadata/core-properties/[^"]+"', f'Target="/{fixed}"', text); data=text.encode()
    entries.append((name,data))
with zipfile.ZipFile(p,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
    for name,data in sorted(entries):
        i=zipfile.ZipInfo(name,(2000,1,1,0,0,0)); i.compress_type=zipfile.ZIP_DEFLATED; i.external_attr=0o100644<<16
        z.writestr(i,data,compresslevel=9)
PY

mkdir -p "$out"
final_zip="$out/godot-csharp-profiler-$version.zip"
final_sums="$out/SHA256SUMS"
final_json="$out/release.json"
rm -f -- "$final_zip" "$final_sums" "$final_json"
zip_tmp="$(mktemp "$out/.godot-csharp-profiler-$version.zip.XXXXXXXX.tmp")"
sums_tmp="$(mktemp "$out/.SHA256SUMS.XXXXXXXX.tmp")"
json_tmp="$(mktemp "$out/.release.json.XXXXXXXX.tmp")"
python3 - "$stage" "$zip_tmp" <<'PY'
from pathlib import Path
import sys, zipfile
stage,zip_path=map(Path,sys.argv[1:])
root=stage/'addons/godot_csharp_profiler'; required=['plugin.cfg','README.md','LICENSE','icon.svg','Compatibility/GlobalUsings.cs','Runtime/CsProfiler.cs','Editor/CsProfilerPlugin.cs','assets/setup.ps1','assets/dependencies.json','assets/GodotCSharpProfiler.Dependencies.props']
for rel in required:
    if not (root/rel).is_file(): raise SystemExit(f'missing required file: {rel}')
for p in stage.rglob('*'):
    if any(x.lower() in {'bin','obj','.godot','spikes','tests','src','docs','.git','testresults'} for x in p.relative_to(stage).parts): raise SystemExit(f'forbidden content: {p}')
if 'script="Editor/CsProfilerPlugin.cs"' not in (root/'plugin.cfg').read_text(): raise SystemExit('invalid plugin.cfg script path')
with zipfile.ZipFile(zip_path,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
    for p in sorted(x for x in stage.rglob('*') if x.is_file()):
        info=zipfile.ZipInfo(p.relative_to(stage).as_posix(),(2000,1,1,0,0,0)); info.compress_type=zipfile.ZIP_DEFLATED; info.external_attr=0o100644<<16
        z.writestr(info,p.read_bytes(),compresslevel=9)
PY

if [[ "${GCP_RELEASE_TEST_FAIL_AFTER_ZIP:-}" == "1" ]]; then
    echo "Forced post-build validation failure." >&2
    exit 1
fi
dotnet run --project "$repo/tests/GodotCSharpProfiler.Packaging.Tests/GodotCSharpProfiler.Packaging.Tests.csproj" -c Release -- --archive "$zip_tmp" --powershell "$powershell"
read -r digest size < <(python3 - "$zip_tmp" <<'PY'
from pathlib import Path
import hashlib, sys
p=Path(sys.argv[1]); print(hashlib.sha256(p.read_bytes()).hexdigest(), p.stat().st_size)
PY
)
printf '%s  %s\n' "$digest" "$(basename "$final_zip")" > "$sums_tmp"
python3 - "$json_tmp" "$version" "$(basename "$final_zip")" "$size" "$digest" <<'PY'
from pathlib import Path
import json, sys
path,version,name,size,digest=sys.argv[1:]
Path(path).write_text(json.dumps({'version':version,'file':name,'bytes':int(size),'sha256':digest},indent=2)+'\n')
PY
mv -f -- "$zip_tmp" "$final_zip"; zip_tmp=""
mv -f -- "$sums_tmp" "$final_sums"; sums_tmp=""
mv -f -- "$json_tmp" "$final_json"; json_tmp=""
published=1
printf 'Artifact: %s\nBytes: %s\nSHA-256: %s\nPowerShell: %s\n' "$final_zip" "$size" "$digest" "$powershell"
