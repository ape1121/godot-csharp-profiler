using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

var archiveArg = Array.IndexOf(args, "--archive");
if (archiveArg < 0 || archiveArg + 1 == args.Length) throw new ArgumentException("Use --archive <zip> --version <version> [--powershell <path>].");
var path = Path.GetFullPath(args[archiveArg + 1]);
var versionArg = Array.IndexOf(args, "--version");
if (versionArg < 0 || versionArg + 1 == args.Length) throw new ArgumentException("Use --version <version>.");
var version = args[versionArg + 1];
if (!File.Exists(path)) throw new FileNotFoundException(path);

var powershellArg = Array.IndexOf(args, "--powershell");
if (powershellArg >= 0 && powershellArg + 1 == args.Length)
    throw new ArgumentException("Use --powershell <path>.");
var requestedPowerShell = powershellArg >= 0
    ? args[powershellArg + 1]
    : Environment.GetEnvironmentVariable("POWERSHELL_EXE");
var shell = ResolveExecutable(requestedPowerShell, OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh")
    ?? throw new InvalidOperationException("PowerShell prerequisite missing: install pwsh, set POWERSHELL_EXE, or pass --powershell <path>.");

using (var zip = ZipFile.OpenRead(path))
{
    var names = zip.Entries.Select(e => e.FullName).ToArray();
    const string root = "addons/godot_csharp_profiler/";
    Assert(names.Length > 10, "Archive is unexpectedly empty.");
    Assert(names.All(n => n.StartsWith(root, StringComparison.Ordinal)), "Every entry must be rooted at addons/godot_csharp_profiler.");
    foreach (var required in new[] { "plugin.cfg", "README.md", "LICENSE", "icon.svg", "Compatibility/GlobalUsings.cs", "Runtime/CsProfiler.cs", "Editor/CsProfilerPlugin.cs", "assets/setup.ps1", "assets/dependencies.json", "assets/GodotCSharpProfiler.Dependencies.props" })
        Assert(names.Contains(root + required, StringComparer.Ordinal), $"Missing {required}.");
    Assert(!names.Any(n => Regex.IsMatch(n, @"(^|/)(bin|obj|\.godot|spikes|tests|src|docs|\.git)(/|$)", RegexOptions.IgnoreCase)), "Development content leaked into archive.");
    Assert(!names.Any(n => n.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)), "Project files must not be archived.");
    var plugin = Read(zip, root + "plugin.cfg");
    Assert(plugin.Contains("script=\"Editor/CsProfilerPlugin.cs\"", StringComparison.Ordinal), "Invalid plugin script path.");
    Assert(plugin.Contains($"version=\"{version}\"", StringComparison.Ordinal), "Exact release plugin version is missing.");
    var sampling = Read(zip, root + "Runtime/Sampling/ManagedSamplingSession.cs");
    Assert(sampling.StartsWith("#if GODOT_CSHARP_PROFILER_SAMPLING\n", StringComparison.Ordinal), "Raw addon must gate external sampling references.");
    var manifest = Read(zip, root + "assets/dependencies.json");
    Assert(manifest.Contains("0.2.661903") && manifest.Contains("3.2.5"), "Exact sampling versions are absent.");
    var setup = Read(zip, root + "assets/setup.ps1");
    Assert(!setup.Contains("PackageReference", StringComparison.Ordinal) && !setup.Contains("FodyWeavers", StringComparison.Ordinal), "setup.ps1 must not duplicate automatic instrumentation installation.");
    Assert(setup.Contains("ProjectInstaller", StringComparison.Ordinal), "Automatic setup must direct users to the tested ProjectInstaller contract.");
    var nupkg = zip.Entries.SingleOrDefault(e => e.FullName == root + $"assets/nuget/GodotCSharpProfiler.Fody.{version}.nupkg");
    Assert(nupkg is not null && nupkg.Length > 10_000, "Fody nupkg is absent or implausibly small.");
    using (var package = new ZipArchive(nupkg!.Open(), ZipArchiveMode.Read, leaveOpen: false))
        Assert(package.GetEntry("README.md") is not null, "Fody nupkg README is missing.");
    Assert(new FileInfo(path).Length < 25 * 1024 * 1024, "Archive exceeds 25 MiB safety budget.");
}

foreach (var implicitUsings in new[] { "enable", "disable" })
    TestExtractedProject(path, shell, implicitUsings);
Console.WriteLine($"Validated canonical archive and raw/add/remove clean-project matrix (ImplicitUsings enable/disable), {new FileInfo(path).Length} bytes: {path}");

static void TestExtractedProject(string archive, string shell, string implicitUsings)
{
    var root = Path.Combine(Path.GetTempPath(), "gcp-package-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        ZipFile.ExtractToDirectory(archive, root);
        var project = Path.Combine(root, "Synthetic.csproj");
        var projectText = $"""
            <Project Sdk="Godot.NET.Sdk/4.7.1">
              <!-- unknown-content-sentinel -->
              <PropertyGroup Condition="'$(Configuration)' != 'Never'">
                <TargetFramework>net8.0</TargetFramework>
                <ImplicitUsings>{implicitUsings}</ImplicitUsings>
                <Nullable>disable</Nullable>
                <CustomUnknownProperty>preserve me</CustomUnknownProperty>
              </PropertyGroup>
              <Target Name="UnknownTarget" BeforeTargets="BeforeBuild"><Message Text="preserved" Importance="low" /></Target>
            </Project>
            """;
        File.WriteAllText(project, projectText, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, "project.godot"), "[application]\nconfig/name=\"Packaging test\"\n[dotnet]\nproject/assembly_name=\"Synthetic\"\n", new UTF8Encoding(false));
        var config = Path.Combine(root, "NuGet.Config");
        var configBytes = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration><packageSources><add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" /></packageSources><config><add key=\"unknown\" value=\"keep\" /></config></configuration>\n");
        File.WriteAllBytes(config, configBytes);
        var originalProject = File.ReadAllBytes(project);
        var props = Path.Combine(root, "addons", "godot_csharp_profiler", "assets", "GodotCSharpProfiler.Dependencies.props");
        var originalProps = File.ReadAllBytes(props);

        Run("dotnet", $"build \"{project}\" --nologo", root, $"build before setup ({implicitUsings})");
        var setup = Path.Combine(root, "addons", "godot_csharp_profiler", "assets", "setup.ps1");
        Run(shell, $"-NoProfile -File \"{setup}\" -Project \"{project}\" -WhatIf", root, "setup preview");
        Assert(BytesEqual(originalProject, File.ReadAllBytes(project)), "WhatIf changed the project.");
        Assert(BytesEqual(configBytes, File.ReadAllBytes(config)), "WhatIf changed NuGet.Config.");

        Run(shell, $"-NoProfile -File \"{setup}\" -Project \"{project}\"", root, "sampling install");
        var installed = File.ReadAllText(project);
        Assert(installed.Contains("GodotCSharpProfilerSamplingDependencies BEGIN", StringComparison.Ordinal), "Owned sampling import was not installed.");
        Assert(installed.Contains("unknown-content-sentinel", StringComparison.Ordinal) && installed.Contains("CustomUnknownProperty", StringComparison.Ordinal) && installed.Contains("UnknownTarget", StringComparison.Ordinal), "Unknown project content was not preserved.");
        Assert(BytesEqual(configBytes, File.ReadAllBytes(config)), "Sampling install changed existing NuGet.Config.");
        Run("dotnet", $"build \"{project}\" -c Debug --nologo", root, $"Debug build after dependency setup ({implicitUsings})");
        Assert(File.Exists(Path.Combine(root, ".godot", "mono", "temp", "bin", "Debug", "GodotSharpEditor.dll")),
            "Setup did not copy GodotSharpEditor.dll required by the Debug game process.");

        Run(shell, $"-NoProfile -File \"{setup}\" -Project \"{project}\" -EnableAutomatic", root, "automatic redirect", expectedExitCode: 1);
        Assert(File.ReadAllText(project) == installed, "Rejected automatic request changed the project.");
        Assert(BytesEqual(configBytes, File.ReadAllBytes(config)), "Rejected automatic request changed NuGet.Config.");

        Run(shell, $"-NoProfile -File \"{setup}\" -Action Remove -Project \"{project}\"", root, "sampling remove");
        Assert(BytesEqual(originalProject, File.ReadAllBytes(project)), "Remove did not byte-restore the original project.");
        Assert(BytesEqual(configBytes, File.ReadAllBytes(config)), "Remove changed existing NuGet.Config.");
        Assert(BytesEqual(originalProps, File.ReadAllBytes(props)), "Setup changed the shipped dependency props.");
        Run("dotnet", $"build \"{project}\" --nologo", root, $"build after remove ({implicitUsings})");
        var leftovers = File.ReadAllText(project) + File.ReadAllText(config);
        Assert(!leftovers.Contains("GodotCSharpProfilerSamplingDependencies", StringComparison.Ordinal), "Owned import was orphaned.");
        Assert(!leftovers.Contains("GodotCSharpProfilerLocal", StringComparison.Ordinal), "A local package source was orphaned.");
        Assert(!File.Exists(project + ".gcp.tmp") && !Directory.EnumerateFiles(root, ".Synthetic.csproj.*.tmp").Any(), "Setup left transaction files.");
    }
    finally { Directory.Delete(root, recursive: true); }
}

static void Run(string file, string arguments, string workingDirectory, string purpose, int expectedExitCode = 0)
{
    using var process = Process.Start(new ProcessStartInfo(file, arguments) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })
        ?? throw new InvalidOperationException($"Could not start {file}.");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    Task.WaitAll(stdout, stderr);
    if (process.ExitCode != expectedExitCode)
        throw new InvalidOperationException($"{purpose} exited {process.ExitCode}, expected {expectedExitCode}.\n{stdout.Result}\n{stderr.Result}");
}

static string? ResolveExecutable(string? requested, string defaultName)
{
    if (!string.IsNullOrWhiteSpace(requested))
    {
        if (requested.Contains(Path.DirectorySeparatorChar) || requested.Contains(Path.AltDirectorySeparatorChar))
        {
            var fullPath = Path.GetFullPath(requested);
            return File.Exists(fullPath) ? fullPath : null;
        }
        return FindOnPath(requested);
    }
    return FindOnPath(defaultName);
}

static string? FindOnPath(string name)
{
    foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
    {
        var candidate = Path.Combine(directory, name);
        if (File.Exists(candidate)) return Path.GetFullPath(candidate);
    }
    return null;
}

static string Read(ZipArchive zip, string name)
{
    var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"Missing {name}");
    using var reader = new StreamReader(entry.Open());
    return reader.ReadToEnd();
}
static bool BytesEqual(byte[] left, byte[] right) => CryptographicOperations.FixedTimeEquals(left, right);
static void Assert(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
