[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')][string]$Version,
    [string]$OutputDirectory = 'artifacts/release',
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$out = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory)); $stage = Join-Path $out 'stage'
Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage, (Join-Path $stage 'addons') | Out-Null
$addonSource = Join-Path $repo 'addons/godot_csharp_profiler'; $addon = Join-Path $stage 'addons/godot_csharp_profiler'
Copy-Item -Recurse $addonSource $addon
# Sampling is opt-in so an untouched Asset Library install has no unresolved diagnostics references.
$sampling = Join-Path $addon 'Runtime/Sampling/ManagedSamplingSession.cs'
$content = [IO.File]::ReadAllText($sampling)
[IO.File]::WriteAllText($sampling, "#if GODOT_CSHARP_PROFILER_SAMPLING`n$content`n#endif`n", [Text.UTF8Encoding]::new($false))
$cfg = Join-Path $addon 'plugin.cfg'; $cfgText = [IO.File]::ReadAllText($cfg) -replace 'version="[^"]+"', "version=`"$Version`""
[IO.File]::WriteAllText($cfg, $cfgText, [Text.UTF8Encoding]::new($false))
$installer = Join-Path $addon 'Editor/Installation/ProjectInstaller.cs'
$installerText = [IO.File]::ReadAllText($installer) -replace 'ProfilerFodyVersion = "0.1.0-dev"', "ProfilerFodyVersion = `"$Version`""
[IO.File]::WriteAllText($installer, $installerText, [Text.UTF8Encoding]::new($false))
$feed = Join-Path $addon 'assets/nuget'; New-Item -ItemType Directory -Force $feed | Out-Null
$packTargets = Join-Path $stage 'GodotCSharpProfiler.PackageReadme.targets'
$readmePath = (Join-Path $addon 'README.md').Replace('&','&amp;').Replace('"','&quot;')
[IO.File]::WriteAllText($packTargets, "<Project><PropertyGroup><PackageReadmeFile>README.md</PackageReadmeFile></PropertyGroup><ItemGroup><None Include=`"$readmePath`" Pack=`"true`" PackagePath=`"/`" /></ItemGroup></Project>", [Text.UTF8Encoding]::new($false))
& dotnet pack (Join-Path $repo 'src/GodotCSharpProfiler.Fody/GodotCSharpProfiler.Fody.csproj') -c Release -p:Version=$Version -p:NoWarn=NU5100%3BNU5128 -p:DirectoryBuildTargetsPath=$packTargets -o $feed
Remove-Item $packTargets
if ($LASTEXITCODE) { throw 'Fody package build failed.' }
$nupkg = Join-Path $feed "GodotCSharpProfiler.Fody.$Version.nupkg"; $packageEntries = @()
$inputPackage = [IO.Compression.ZipFile]::OpenRead($nupkg)
try {
    $oldCore = ($inputPackage.Entries | Where-Object FullName -like 'package/services/metadata/core-properties/*').FullName
    $fixedCore = 'package/services/metadata/core-properties/godot-csharp-profiler.psmdcp'
    foreach ($item in $inputPackage.Entries) {
        $memory=[IO.MemoryStream]::new(); $source=$item.Open(); try {$source.CopyTo($memory)} finally {$source.Dispose()}; $bytes=$memory.ToArray(); $memory.Dispose()
        if ($item.FullName -eq '_rels/.rels') { $text=[Text.Encoding]::UTF8.GetString($bytes).Replace($oldCore,$fixedCore); $text=[Text.RegularExpressions.Regex]::Replace($text,'(Type="http://schemas.microsoft.com/packaging/2010/07/manifest"[^>]*Id=")[^"]+"','$1RMANIFEST"'); $text=[Text.RegularExpressions.Regex]::Replace($text,'(Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties"[^>]*Id=")[^"]+"','$1RMETADATA"'); $bytes=[Text.Encoding]::UTF8.GetBytes($text) }
        # PackageReadmeFile and README.md were supplied to dotnet pack above.
        $packageEntries += [pscustomobject]@{Name=$(if($item.FullName -eq $oldCore){$fixedCore}else{$item.FullName});Bytes=$bytes}
    }
    # Preserve the package's already-declared README entry.
} finally {$inputPackage.Dispose()}
Remove-Item $nupkg; $packageStream=[IO.File]::Open($nupkg,[IO.FileMode]::CreateNew)
try {$packageArchive=[IO.Compression.ZipArchive]::new($packageStream,[IO.Compression.ZipArchiveMode]::Create,$false); try {foreach($item in $packageEntries|Sort-Object Name){$entry=$packageArchive.CreateEntry($item.Name,[IO.Compression.CompressionLevel]::Optimal);$entry.LastWriteTime=[DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero);$target=$entry.Open();try{$target.Write($item.Bytes)}finally{$target.Dispose()}}}finally{$packageArchive.Dispose()}}finally{$packageStream.Dispose()}
if ($LASTEXITCODE) { throw 'Fody package build failed.' }
# Keep installer/source manifest versions exact for this archive without changing development sources.
$manifest = Join-Path $addon 'assets/dependencies.json'; $m = [IO.File]::ReadAllText($manifest) -replace '"version": "0.1.0-dev"', "`"version`": `"$Version`"" -replace '"GodotCSharpProfiler.Fody": "0.1.0-dev"', "`"GodotCSharpProfiler.Fody`": `"$Version`""
[IO.File]::WriteAllText($manifest, $m, [Text.UTF8Encoding]::new($false))
$required = @('plugin.cfg','README.md','LICENSE','icon.svg','Compatibility/GlobalUsings.cs','Runtime/CsProfiler.cs','Editor/CsProfilerPlugin.cs','assets/setup.ps1','assets/dependencies.json','assets/GodotCSharpProfiler.Dependencies.props')
foreach ($file in $required) { if (-not (Test-Path (Join-Path $addon $file))) { throw "Required archive file missing: $file" } }
$forbidden = Get-ChildItem -Recurse -Force $stage | Where-Object { $_.Name -in @('bin','obj','.godot','spikes','tests') -or $_.FullName -match '[\\/](docs|src|\.git|TestResults)[\\/]' }
if ($forbidden) { throw "Forbidden archive content: $($forbidden.FullName -join ', ')" }
if ([IO.File]::ReadAllText($cfg) -notmatch 'script="Editor/CsProfilerPlugin.cs"') { throw 'plugin.cfg script path is invalid.' }
$zip = Join-Path $out "godot-csharp-profiler-$Version.zip"; Remove-Item $zip -Force -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression
$stream = [IO.File]::Open($zip, [IO.FileMode]::CreateNew)
try {
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($file in Get-ChildItem -File -Recurse $stage | Sort-Object FullName) {
            $relative = [IO.Path]::GetRelativePath($stage, $file.FullName).Replace('\','/')
            $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
            $input = $file.OpenRead(); $output = $entry.Open(); try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant(); $size = (Get-Item $zip).Length
"$hash  $([IO.Path]::GetFileName($zip))" | Set-Content (Join-Path $out 'SHA256SUMS') -Encoding ascii
@{ version=$Version; file=[IO.Path]::GetFileName($zip); bytes=$size; sha256=$hash } | ConvertTo-Json | Set-Content (Join-Path $out 'release.json') -Encoding utf8
if (-not $SkipTests) { & dotnet run --project (Join-Path $repo 'tests/GodotCSharpProfiler.Packaging.Tests/GodotCSharpProfiler.Packaging.Tests.csproj') -c Release -- --archive $zip; if ($LASTEXITCODE) { throw 'Packaging tests failed.' } }
Write-Host "Artifact: $zip"; Write-Host "Bytes: $size"; Write-Host "SHA-256: $hash"
