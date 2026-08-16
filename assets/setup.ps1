[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Install', 'Remove')][string]$Action = 'Install',
    [string]$Project = '',
    [switch]$EnableAutomatic
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
if (-not $Project) {
    $projects = @(Get-ChildItem -LiteralPath $root -Filter *.csproj -File)
    if ($projects.Count -ne 1) { throw 'Exactly one top-level .csproj is required; pass -Project explicitly.' }
    $Project = $projects[0].FullName
}
$Project = [IO.Path]::GetFullPath($Project)
if ([IO.Path]::GetDirectoryName($Project) -ne $root) { throw 'The project must be top-level in the Godot project.' }
[xml]$xml = [IO.File]::ReadAllText($Project)
if ($xml.Project.Sdk -eq $null) { throw 'An SDK-style project is required.' }
$label = 'GodotCSharpProfilerDependencies'
$owned = @($xml.Project.Import | Where-Object Label -eq $label)
$feed = (Join-Path $PSScriptRoot 'nuget').Replace('\','/')
$props = (Join-Path $PSScriptRoot 'GodotCSharpProfiler.Dependencies.props').Replace('\','/')
if ($Action -eq 'Install') {
    if ($owned.Count -eq 0) {
        $import = $xml.CreateElement('Import', $xml.Project.NamespaceURI)
        $import.SetAttribute('Project', $props)
        $import.SetAttribute('Label', $label)
        [void]$xml.Project.AppendChild($import)
    }
    if ($EnableAutomatic) {
        $group = $xml.CreateElement('ItemGroup', $xml.Project.NamespaceURI); $group.SetAttribute('Label', $label)
        foreach ($spec in @(@('Fody','6.9.3'), @('GodotCSharpProfiler.Fody','0.1.0-dev'))) {
            $reference = $xml.CreateElement('PackageReference', $xml.Project.NamespaceURI)
            $reference.SetAttribute('Include', $spec[0]); $reference.SetAttribute('Version', $spec[1]); $reference.SetAttribute('PrivateAssets','all')
            [void]$group.AppendChild($reference)
        }
        [void]$xml.Project.AppendChild($group)
    }
    $config = Join-Path $root 'NuGet.Config'
    if ($EnableAutomatic -and -not (Test-Path $config)) {
        $configText = "<?xml version=`"1.0`" encoding=`"utf-8`"?>`n<configuration><packageSources><add key=`"nuget.org`" value=`"https://api.nuget.org/v3/index.json`" /><add key=`"GodotCSharpProfilerLocal`" value=`"$feed`" /></packageSources></configuration>`n"
        [IO.File]::WriteAllText("$config.tmp", $configText, [Text.UTF8Encoding]::new($false)); Move-Item "$config.tmp" $config
    }
} else {
    foreach ($node in @($xml.Project.ChildNodes | Where-Object { $_.Attributes['Label']?.Value -eq $label })) { [void]$xml.Project.RemoveChild($node) }
    $config = Join-Path $root 'NuGet.Config'
    if (Test-Path $config) {
        $text = [IO.File]::ReadAllText($config)
        if ($text -match 'GodotCSharpProfilerLocal') { Write-Warning 'NuGet.Config names the addon feed; remove that source/file manually after checking other entries.' }
    }
}
$settings = [Xml.XmlWriterSettings]::new(); $settings.Indent = $true; $settings.Encoding = [Text.UTF8Encoding]::new($false)
$temp = "$Project.gcp.tmp"
$writer = [Xml.XmlWriter]::Create($temp, $settings); try { $xml.Save($writer) } finally { $writer.Dispose() }
if ($PSCmdlet.ShouldProcess($Project, "$Action profiler dependencies")) { Move-Item -Force $temp $Project } else { Remove-Item $temp }
Write-Host "$Action complete. Run 'dotnet restore', rebuild, and restart Godot."
