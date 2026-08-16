[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [ValidateSet('Install', 'Remove')][string]$Action = 'Install',
    [string]$Project = '',
    [switch]$EnableAutomatic
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($EnableAutomatic) {
    throw @'
setup.ps1 installs sampling dependencies only. It intentionally does not duplicate the automatic instrumentation installer.
In Godot, enable Godot C# Profiler, open its Automatic mode, choose Install, review the ProjectInstaller preview, and apply it. Then clean/build and restart Godot and the game.
'@
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
if (-not $Project) {
    $projects = @(Get-ChildItem -LiteralPath $root -Filter *.csproj -File)
    if ($projects.Count -ne 1) { throw 'Exactly one top-level .csproj is required; pass -Project explicitly.' }
    $Project = $projects[0].FullName
}
$Project = [IO.Path]::GetFullPath($Project)
if ([IO.Path]::GetDirectoryName($Project) -ne $root) { throw 'The project must be top-level in the Godot project.' }
if (-not (Test-Path -LiteralPath $Project -PathType Leaf)) { throw "Project does not exist: $Project" }

$props = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'GodotCSharpProfiler.Dependencies.props'))
if (-not (Test-Path -LiteralPath $props -PathType Leaf)) { throw "Dependency props do not exist: $props" }

# A deliberately narrow, ownership-marked textual edit keeps every unknown project
# element, comment, encoding, and newline byte unchanged. This script never edits
# NuGet.Config or the shipped props and never installs automatic instrumentation.
$label = 'GodotCSharpProfilerSamplingDependencies'
$begin = "<!-- $label BEGIN -->"
$end = "<!-- $label END -->"
$original = [IO.File]::ReadAllBytes($Project)
$encoding = [Text.UTF8Encoding]::new($false, $true)
try { $text = $encoding.GetString($original) }
catch { throw 'The project must be UTF-8 so setup can preserve it safely.' }
if ($text -notmatch '<Project(?:\s|>)' -or $text -notmatch '</Project\s*>') { throw 'An SDK-style XML project is required.' }
if (($text.Contains($begin)) -xor ($text.Contains($end))) { throw 'Refusing a project with an incomplete profiler ownership marker.' }

$newText = $text
if ($Action -eq 'Install') {
    if (-not $text.Contains($begin)) {
        $newline = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
        $escapedProps = [Security.SecurityElement]::Escape($props.Replace('\', '/'))
        $block = "  $begin$newline  <Import Project=`"$escapedProps`" Label=`"$label`" />$newline  $end$newline"
        $close = [Text.RegularExpressions.Regex]::Match($text, '</Project\s*>', [Text.RegularExpressions.RegexOptions]::RightToLeft)
        $newText = $text.Insert($close.Index, $block)
    }
} elseif ($text.Contains($begin)) {
    $pattern = '(?m)^[ \t]*' + [Regex]::Escape($begin) + '\r?\n[\s\S]*?^[ \t]*' + [Regex]::Escape($end) + '(?:\r?\n)?'
    $newText = [Regex]::Replace($text, $pattern, '', 1)
}

if ($newText -eq $text) {
    Write-Host "$Action complete; no owned sampling changes were needed."
    return
}
if (-not $PSCmdlet.ShouldProcess($Project, "$Action owned profiler sampling dependency import")) {
    Write-Host 'Preview complete; no files were changed.'
    return
}

$replacement = $encoding.GetBytes($newText)
$temp = Join-Path ([IO.Path]::GetDirectoryName($Project)) ('.' + [IO.Path]::GetFileName($Project) + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    [IO.File]::WriteAllBytes($temp, $replacement)
    [xml]$null = [IO.File]::ReadAllText($temp, $encoding)
    [IO.File]::Move($temp, $Project, $true)
} catch {
    Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    # Move is the sole commit point. If anything unexpectedly changed the target,
    # restore its exact pre-transaction bytes before reporting failure.
    if (-not [Linq.Enumerable]::SequenceEqual([byte[]]$original, [byte[]][IO.File]::ReadAllBytes($Project))) {
        [IO.File]::WriteAllBytes($Project, $original)
    }
    throw
}
Write-Host "$Action complete. Run 'dotnet restore', rebuild, and restart Godot."
