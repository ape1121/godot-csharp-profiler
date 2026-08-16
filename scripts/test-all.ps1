$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE) { throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE" }
}

# Release-required production gates. Keep this explicit: some harnesses are executables,
# and dotnet test on an executable project can succeed without executing its assertions.
$testProjects = @(
    'tests/GodotCSharpProfiler.Sampling.Tests/GodotCSharpProfiler.Sampling.Tests.csproj',
    'tests/GodotCSharpProfiler.Protocol.Tests/GodotCSharpProfiler.Protocol.Tests.csproj',
    'tests/GodotCSharpProfiler.RuntimeIntegration.Tests/GodotCSharpProfiler.RuntimeIntegration.Tests.csproj',
    'tests/GodotCSharpProfiler.EditorIntegration.Tests/GodotCSharpProfiler.EditorIntegration.Tests.csproj',
    'tests/GodotCSharpProfiler.ModeUi.Tests/GodotCSharpProfiler.ModeUi.Tests.csproj',
    'tests/GodotCSharpProfiler.EndToEnd.Tests/GodotCSharpProfiler.EndToEnd.Tests.csproj',
    'tests/GodotCSharpProfiler.Installation.Tests/GodotCSharpProfiler.Installation.Tests.csproj'
)
$runProjects = @(
    'tests/GodotCSharpProfiler.Tests/GodotCSharpProfiler.Tests.csproj',
    'tests/GodotCSharpProfiler.Instrumentation.Tests/GodotCSharpProfiler.Instrumentation.Tests.csproj'
)

Push-Location $root
try {
    foreach ($configuration in @('Debug', 'ExportDebug', 'Release')) {
        Invoke-Dotnet @('build', 'GodotCSharpProfiler.csproj', '-c', $configuration, '-v', 'minimal')
    }
    foreach ($project in $testProjects) {
        Invoke-Dotnet @('test', $project, '-c', 'Release', '--logger', 'console;verbosity=minimal')
    }
    foreach ($project in $runProjects) {
        Invoke-Dotnet @('run', '--project', $project, '-c', 'Release')
    }
}
finally { Pop-Location }

Write-Host 'PASS: all release-required managed gates completed.'
Write-Host 'Harmony and the standalone Cecil spike remain proof-only experiments and are intentionally not release gates.'
