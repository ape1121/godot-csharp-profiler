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
    foreach (var required in new[] { "plugin.cfg", "README.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "licenses/FodyHelpers-LICENSE.txt", "licenses/Mono.Cecil-LICENSE.txt", "icon.svg", "icon.png", ".gitignore", "Compatibility/GlobalUsings.cs", "Runtime/CsProfiler.cs", "Editor/CsProfilerPlugin.cs", "assets/setup.ps1", "assets/dependencies.json", "assets/GodotCSharpProfiler.Dependencies.props" })
        Assert(names.Contains(root + required, StringComparer.Ordinal), $"Missing {required}.");
    Assert(!names.Any(n => Regex.IsMatch(n, @"(^|/)(bin|obj|\.godot|spikes|tests|src|docs|\.git)(/|$)", RegexOptions.IgnoreCase)), "Development content leaked into archive.");
    Assert(!names.Any(n => n.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)), "Project files must not be archived.");
    Assert(!names.Any(n => n.EndsWith(".uid", StringComparison.OrdinalIgnoreCase) ||
                           n.EndsWith(".import", StringComparison.OrdinalIgnoreCase) ||
                           n.EndsWith(".translation", StringComparison.OrdinalIgnoreCase)),
        "Godot-generated metadata must not be archived.");
    var plugin = Read(zip, root + "plugin.cfg");
    Assert(plugin.Contains("script=\"Editor/CsProfilerPlugin.cs\"", StringComparison.Ordinal), "Invalid plugin script path.");
    Assert(plugin.Contains($"version=\"{version}\"", StringComparison.Ordinal), "Exact release plugin version is missing.");
    var sampling = Read(zip, root + "Runtime/Sampling/ManagedSamplingSession.cs");
    Assert(sampling.StartsWith("#if GODOT_CSHARP_PROFILER_SAMPLING\n", StringComparison.Ordinal), "Raw addon must gate external sampling references.");
    var epoch = Read(zip, root + "Runtime/Sampling/ManagedSamplingTraceEpoch.cs");
    Assert(epoch.StartsWith("#if GODOT_CSHARP_PROFILER_SAMPLING\n", StringComparison.Ordinal), "Raw addon must gate external EventPipe epoch references.");
    var manifest = Read(zip, root + "assets/dependencies.json");
    Assert(manifest.Contains("0.2.661903") && manifest.Contains("3.2.5"), "Exact sampling versions are absent.");
    var setup = Read(zip, root + "assets/setup.ps1");
    Assert(!setup.Contains("PackageReference", StringComparison.Ordinal) && !setup.Contains("FodyWeavers", StringComparison.Ordinal), "setup.ps1 must not duplicate automatic instrumentation installation.");
    Assert(setup.Contains("ProjectInstaller", StringComparison.Ordinal), "Automatic setup must direct users to the tested ProjectInstaller contract.");
    Assert(setup.Contains("ReparsePoint", StringComparison.Ordinal) && setup.Contains("LinkType", StringComparison.Ordinal), "setup.ps1 must reject symlink/reparse-point paths.");
    AssertThirdPartyNotices(
        Read(zip, root + "THIRD-PARTY-NOTICES.md"),
        Read(zip, root + "licenses/FodyHelpers-LICENSE.txt"),
        Read(zip, root + "licenses/Mono.Cecil-LICENSE.txt"),
        "addon");
    var nupkg = zip.Entries.SingleOrDefault(e => e.FullName == root + $"assets/nuget/GodotCSharpProfiler.Fody.{version}.nupkg");
    Assert(nupkg is not null && nupkg.Length > 10_000, "Fody nupkg is absent or implausibly small.");
    using (var package = new ZipArchive(nupkg!.Open(), ZipArchiveMode.Read, leaveOpen: false))
    {
        Assert(package.GetEntry("README.md") is not null, "Fody nupkg README is missing.");
        AssertThirdPartyNotices(
            Read(package, "THIRD-PARTY-NOTICES.md"),
            Read(package, "licenses/FodyHelpers-LICENSE.txt"),
            Read(package, "licenses/Mono.Cecil-LICENSE.txt"),
            "Fody nupkg");
        foreach (var binary in new[] { "FodyHelpers.dll", "Mono.Cecil.dll", "Mono.Cecil.Pdb.dll", "Mono.Cecil.Rocks.dll" })
            Assert(package.GetEntry("weaver/" + binary) is not null, $"Fody nupkg is missing embedded {binary}.");
    }
    Assert(new FileInfo(path).Length < 25 * 1024 * 1024, "Archive exceeds 25 MiB safety budget.");
}

foreach (var implicitUsings in new[] { "enable", "disable" })
    TestExtractedProject(path, shell, implicitUsings);
if (OperatingSystem.IsLinux()) TestLinuxSymlinkRejections(path, shell);
Console.WriteLine($"Validated canonical archive, retained third-party notices, raw/add/remove clean-project matrix (ImplicitUsings enable/disable), and Linux project/root/props/parent-chain symlink rejection when applicable; {new FileInfo(path).Length} bytes: {path}");

static void AssertThirdPartyNotices(string notices, string fodyLicense, string cecilLicense, string container)
{
    Assert(notices.Contains("FodyHelpers 6.9.3", StringComparison.Ordinal), $"{container} notices omit FodyHelpers 6.9.3.");
    Assert(notices.Contains("Mono.Cecil 0.11.6", StringComparison.Ordinal), $"{container} notices omit Mono.Cecil 0.11.6.");
    Assert(notices.Contains("FodyHelpers.dll", StringComparison.Ordinal), $"{container} notices do not identify the embedded FodyHelpers binary.");
    Assert(notices.Contains("Mono.Cecil.Pdb.dll", StringComparison.Ordinal) && notices.Contains("Mono.Cecil.Rocks.dll", StringComparison.Ordinal), $"{container} notices do not identify all embedded Mono.Cecil binaries.");
    Assert(fodyLicense.Contains("Copyright (c) The Fody Team and contributors", StringComparison.Ordinal), $"{container} FodyHelpers copyright notice is incomplete.");
    Assert(cecilLicense.Contains("Copyright (c) 2008 - 2015 Jb Evain", StringComparison.Ordinal) && cecilLicense.Contains("Copyright (c) 2008 - 2011 Novell, Inc.", StringComparison.Ordinal), $"{container} Mono.Cecil copyright notices are incomplete.");
    const string permission = "Permission is hereby granted, free of charge, to any person obtaining";
    const string warranty = "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND";
    Assert(fodyLicense.Contains(permission, StringComparison.Ordinal) && fodyLicense.Contains(warranty, StringComparison.Ordinal), $"{container} FodyHelpers MIT license text is incomplete.");
    Assert(cecilLicense.Contains(permission, StringComparison.Ordinal) && cecilLicense.Contains(warranty, StringComparison.Ordinal), $"{container} Mono.Cecil MIT license text is incomplete.");
}

static void TestLinuxSymlinkRejections(string archive, string shell)
{
    TestRejectedSymlinkFixture(archive, shell, "selected project", (root, setup, project) =>
    {
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "outside.csproj");
        File.WriteAllText(outside, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n", new UTF8Encoding(false));
        File.Delete(project);
        File.CreateSymbolicLink(project, outside);
        return (setup, project, outside);
    });
    TestRejectedSymlinkFixture(archive, shell, "dependency props", (root, setup, project) =>
    {
        var props = Path.Combine(root, "addons", "godot_csharp_profiler", "assets", "GodotCSharpProfiler.Dependencies.props");
        var outside = Path.Combine(Path.GetDirectoryName(root)!, "outside.props");
        File.Move(props, outside);
        File.CreateSymbolicLink(props, outside);
        return (setup, project, project);
    });
    TestRejectedSymlinkFixture(archive, shell, "project root", (root, setup, project) =>
    {
        var link = Path.Combine(Path.GetDirectoryName(root)!, "linked-root");
        Directory.CreateSymbolicLink(link, root);
        return (
            Path.Combine(link, "addons", "godot_csharp_profiler", "assets", "setup.ps1"),
            Path.Combine(link, Path.GetFileName(project)),
            project);
    });
    TestRejectedSymlinkFixture(archive, shell, "unsafe parent chain", (root, setup, project) =>
    {
        var parent = Path.GetDirectoryName(root)!;
        var realParent = Path.Combine(parent, "real-parent");
        Directory.CreateDirectory(realParent);
        var movedRoot = Path.Combine(realParent, "project");
        Directory.Move(root, movedRoot);
        var linkedParent = Path.Combine(parent, "linked-parent");
        Directory.CreateSymbolicLink(linkedParent, realParent);
        return (
            Path.Combine(linkedParent, "project", "addons", "godot_csharp_profiler", "assets", "setup.ps1"),
            Path.Combine(linkedParent, "project", Path.GetFileName(project)),
            Path.Combine(movedRoot, Path.GetFileName(project)));
    });
}

static void TestRejectedSymlinkFixture(
    string archive,
    string shell,
    string purpose,
    Func<string, string, string, (string Setup, string Project, string ProtectedFile)> arrange)
{
    var container = Path.Combine(Path.GetTempPath(), "gcp-package-symlink-test-" + Guid.NewGuid().ToString("N"));
    var root = Path.Combine(container, "project");
    Directory.CreateDirectory(root);
    try
    {
        ZipFile.ExtractToDirectory(archive, root);
        var project = Path.Combine(root, "Synthetic.csproj");
        File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>\n", new UTF8Encoding(false));
        var setup = Path.Combine(root, "addons", "godot_csharp_profiler", "assets", "setup.ps1");
        var arranged = arrange(root, setup, project);
        var before = File.ReadAllBytes(arranged.ProtectedFile);
        Run(shell, $"-NoProfile -File \"{arranged.Setup}\" -Project \"{arranged.Project}\"", container, $"reject {purpose} symlink/reparse point", expectedExitCode: 1);
        Assert(BytesEqual(before, File.ReadAllBytes(arranged.ProtectedFile)), $"Rejected {purpose} symlink/reparse-point path changed protected bytes.");
    }
    finally { Directory.Delete(container, recursive: true); }
}

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

        RunAutomaticLifecycle(root, project);
        Assert(File.ReadAllText(project) == installed, "Automatic uninstall did not restore the setup-only project.");
        Assert(!File.Exists(Path.Combine(root, "FodyWeavers.xml")), "Automatic uninstall left FodyWeavers.xml.");

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

static void RunAutomaticLifecycle(string root, string project)
{
    var harness = Path.Combine(Path.GetTempPath(), "gcsp-package-installer-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(harness);
    try
    {
        var installer = Path.Combine(root, "addons", "godot_csharp_profiler", "Editor", "Installation", "ProjectInstaller.cs");
        var models = Path.Combine(root, "addons", "godot_csharp_profiler", "Editor", "Installation", "InstallationModels.cs");
        File.WriteAllText(Path.Combine(harness, "Harness.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
              <ItemGroup>
                <Compile Include="{EscapeXml(installer)}" Link="ProjectInstaller.cs" />
                <Compile Include="{EscapeXml(models)}" Link="InstallationModels.cs" />
              </ItemGroup>
            </Project>
            """, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(harness, "Program.cs"), """
            using Apeworks.GodotCSharpProfiler.Editor.Installation;
            using System.Diagnostics;
            var root = args[0];
            var package = Path.Combine(root, "addons", "godot_csharp_profiler", "assets", "nuget",
                $"GodotCSharpProfiler.Fody.{ProjectInstaller.ProfilerFodyVersion}.nupkg");
            var installer = new ProjectInstaller(root, packageSources: new PackageSourcePlan([package]));
            installer.Apply(installer.PreviewInstall());
            var project = ProjectInstaller.DiscoverProject(root);
            var text = File.ReadAllText(project);
            if (!text.Contains(ProjectInstaller.PackageSourceElementName) || !text.Contains(ProjectInstaller.RestoreSourcesElementName)) return 2;
            var buildInfo = new ProcessStartInfo("dotnet", $"build \"{project}\" -c Release --nologo --no-cache")
                { WorkingDirectory = root, UseShellExecute = false };
            buildInfo.Environment["NUGET_PACKAGES"] = Path.Combine(root, ".package-acceptance", "packages");
            using (var build = Process.Start(buildInfo))
            { build!.WaitForExit(); if (build.ExitCode != 0) return build.ExitCode; }
            installer.Apply(installer.PreviewUninstall());
            text = File.ReadAllText(project);
            return text.Contains("GodotCSharpProfiler.Fody") || text.Contains(ProjectInstaller.PackageSourceElementName) ||
                text.Contains(ProjectInstaller.RestoreSourcesElementName) ? 3 : 0;
            """, new UTF8Encoding(false));
        Run("dotnet", $"run --project \"{Path.Combine(harness, "Harness.csproj")}\" -c Release -- \"{root}\"", harness,
            "automatic install/build/uninstall from bundled package");
    }
    finally { Directory.Delete(harness, recursive: true); }
}

static string EscapeXml(string value) => value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

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
