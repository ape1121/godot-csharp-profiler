[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$OutputDirectory = 'artifacts/release',
    [string]$PowerShellExecutable,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$out = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory)) }
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { throw 'dotnet prerequisite missing.' }
if (-not $SkipTests) {
    if (-not $PowerShellExecutable) { $PowerShellExecutable = (Get-Process -Id $PID).Path }
    if (-not $PowerShellExecutable -or -not (Test-Path -LiteralPath $PowerShellExecutable -PathType Leaf)) { throw 'PowerShell prerequisite missing: pass -PowerShellExecutable PATH.' }
    $PowerShellExecutable = [IO.Path]::GetFullPath($PowerShellExecutable)
}
$work = Join-Path ([IO.Path]::GetTempPath()) ("godot-csharp-profiler-release." + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $arguments = @('run', '--project', (Join-Path $repo 'scripts/GodotCSharpProfiler.ReleaseBuilder/GodotCSharpProfiler.ReleaseBuilder.csproj'), '-c', 'Release', '--', '--repository', $repo, '--output', $out, '--workspace', $work, '--version', $Version, '--dotnet', $dotnet)
    if ($SkipTests) { $arguments += '--skip-tests' } else { $arguments += @('--powershell', $PowerShellExecutable) }
    & $dotnet @arguments
    if ($LASTEXITCODE) { throw "Release builder exited $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $work) { Remove-Item -Recurse -Force -LiteralPath $work -ErrorAction SilentlyContinue }
}
