#!/usr/bin/env bash
set -euo pipefail
version="${1:?usage: scripts/build-release.sh VERSION [OUTPUT_DIRECTORY]}"
out="${2:-artifacts/release}"
case "$version" in *[!0-9A-Za-z.-]*|'' ) echo "invalid version" >&2; exit 2;; esac
repo="$(cd "$(dirname "$0")/.." && pwd)"; out="$repo/$out"; stage="$out/stage"
rm -rf "$stage"; mkdir -p "$stage/addons"; cp -R "$repo/addons/godot_csharp_profiler" "$stage/addons/"
addon="$stage/addons/godot_csharp_profiler"; mkdir -p "$addon/assets/nuget"
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
pack_targets="$stage/GodotCSharpProfiler.PackageReadme.targets"
cat > "$pack_targets" <<EOF
<Project><PropertyGroup><PackageReadmeFile>README.md</PackageReadmeFile></PropertyGroup><ItemGroup><None Include="$addon/README.md" Pack="true" PackagePath="/" /></ItemGroup></Project>
EOF
dotnet pack "$repo/src/GodotCSharpProfiler.Fody/GodotCSharpProfiler.Fody.csproj" -c Release -p:Version="$version" -p:NoWarn=NU5100%3BNU5128 -p:DirectoryBuildTargetsPath="$pack_targets" -o "$addon/assets/nuget"
rm "$pack_targets"
python3 - "$addon/assets/nuget/GodotCSharpProfiler.Fody.$version.nupkg" <<'PY'
from pathlib import Path
import sys, zipfile
p=Path(sys.argv[1])
with zipfile.ZipFile(p) as z:
    raw=[(i.filename,z.read(i)) for i in z.infolist()]
old=next(name for name,_ in raw if name.startswith('package/services/metadata/core-properties/'))
fixed='package/services/metadata/core-properties/godot-csharp-profiler.psmdcp'
entries=[]
for name,data in raw:
    if name==old: name=fixed
    if name=='_rels/.rels':
        import re
        text=data.decode(); text=re.sub(r'(Type="http://schemas.microsoft.com/packaging/2010/07/manifest"[^>]*Id=")[^"]+"', r'\1RMANIFEST"', text); text=re.sub(r'(Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties"[^>]*Id=")[^"]+"', r'\1RMETADATA"', text); text=re.sub(r'Target="/package/services/metadata/core-properties/[^"]+"', f'Target="/{fixed}"', text); data=text.encode()
    entries.append((name,data))
with zipfile.ZipFile(p,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
    for name,data in sorted(entries):
        i=zipfile.ZipInfo(name,(2000,1,1,0,0,0)); i.compress_type=zipfile.ZIP_DEFLATED; i.external_attr=0o100644<<16
        z.writestr(i,data,compresslevel=9)
PY
python3 - "$stage" "$out" "$version" <<'PY'
from pathlib import Path
import hashlib, json, re, sys, zipfile
stage,out,version=map(Path,sys.argv[1:]); out.mkdir(parents=True,exist_ok=True)
root=stage/'addons/godot_csharp_profiler'; required=['plugin.cfg','README.md','LICENSE','icon.svg','Compatibility/GlobalUsings.cs','Runtime/CsProfiler.cs','Editor/CsProfilerPlugin.cs','assets/setup.ps1','assets/dependencies.json','assets/GodotCSharpProfiler.Dependencies.props']
for rel in required:
    if not (root/rel).is_file(): raise SystemExit(f'missing required file: {rel}')
for p in stage.rglob('*'):
    if any(x.lower() in {'bin','obj','.godot','spikes','tests','src','docs','.git','testresults'} for x in p.relative_to(stage).parts): raise SystemExit(f'forbidden content: {p}')
if 'script="Editor/CsProfilerPlugin.cs"' not in (root/'plugin.cfg').read_text(): raise SystemExit('invalid plugin.cfg script path')
zip_path=out/f'godot-csharp-profiler-{version}.zip'
with zipfile.ZipFile(zip_path,'w',zipfile.ZIP_DEFLATED,compresslevel=9) as z:
    for p in sorted(x for x in stage.rglob('*') if x.is_file()):
        info=zipfile.ZipInfo(p.relative_to(stage).as_posix(),(2000,1,1,0,0,0)); info.compress_type=zipfile.ZIP_DEFLATED; info.external_attr=0o100644<<16
        z.writestr(info,p.read_bytes(),compresslevel=9)
digest=hashlib.sha256(zip_path.read_bytes()).hexdigest(); size=zip_path.stat().st_size
(out/'SHA256SUMS').write_text(f'{digest}  {zip_path.name}\n',encoding='ascii')
(out/'release.json').write_text(json.dumps({'version':str(version),'file':zip_path.name,'bytes':size,'sha256':digest},indent=2)+'\n')
print(f'Artifact: {zip_path}\nBytes: {size}\nSHA-256: {digest}')
PY
dotnet run --project "$repo/tests/GodotCSharpProfiler.Packaging.Tests/GodotCSharpProfiler.Packaging.Tests.csproj" -c Release -- --archive "$out/godot-csharp-profiler-$version.zip"

rm -rf "$stage"
