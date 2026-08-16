$ErrorActionPreference = 'Stop'
$projects = Get-ChildItem "$PSScriptRoot/../tests" -Filter '*.csproj' -Recurse | Sort-Object FullName
foreach ($project in $projects) {
    dotnet test $project.FullName -c Release
    if ($LASTEXITCODE) { throw "Tests failed: $($project.FullName)" }
}
