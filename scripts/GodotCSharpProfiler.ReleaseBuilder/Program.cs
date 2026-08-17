using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

return await ReleaseBuilder.RunAsync(args);

static class ReleaseBuilder
{
    static readonly UTF8Encoding Utf8 = new(false);
    static readonly string[] Required = ["plugin.cfg", "README.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "licenses/FodyHelpers-LICENSE.txt", "licenses/Mono.Cecil-LICENSE.txt", "icon.svg", "icon.png", ".gitignore", "Compatibility/GlobalUsings.cs", "Runtime/CsProfiler.cs", "Editor/CsProfilerPlugin.cs", "assets/setup.ps1", "assets/dependencies.json", "assets/GodotCSharpProfiler.Dependencies.props"];
    static readonly string[] RequiredPackageEntries = ["README.md", "THIRD-PARTY-NOTICES.md", "licenses/FodyHelpers-LICENSE.txt", "licenses/Mono.Cecil-LICENSE.txt", "weaver/GodotCSharpProfiler.Fody.dll", "weaver/FodyHelpers.dll", "weaver/Mono.Cecil.dll", "weaver/Mono.Cecil.Pdb.dll", "weaver/Mono.Cecil.Rocks.dll"];
    static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".godot", "spikes", "tests", "src", "docs", ".git", "TestResults" };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            await BuildAsync(options);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    static async Task BuildAsync(Options options)
    {
        ValidateVersion(options.Version);
        var addonSource = Path.Combine(options.Repository, "addons", "godot_csharp_profiler");
        var stage = Path.Combine(options.Workspace, "stage");
        var addon = Path.Combine(stage, "addons", "godot_csharp_profiler");
        Directory.CreateDirectory(addon);
        CopyAddon(addonSource, addon);
        TransformStage(addon, options.Version);

        var feed = Path.Combine(addon, "assets", "nuget");
        Directory.CreateDirectory(feed);
        var targets = Path.Combine(options.Workspace, "GodotCSharpProfiler.PackageReadme.targets");
        var readme = EscapeXml(Path.Combine(addon, "README.md"));
        File.WriteAllText(targets, $"<Project><PropertyGroup><PackageReadmeFile>README.md</PackageReadmeFile></PropertyGroup><ItemGroup><None Include=\"{readme}\" Pack=\"true\" PackagePath=\"/\" /></ItemGroup></Project>\n", Utf8);
        var project = Path.Combine(options.Repository, "src", "GodotCSharpProfiler.Fody", "GodotCSharpProfiler.Fody.csproj");
        var packObj = Path.Combine(options.Workspace, "pack-obj") + Path.DirectorySeparatorChar;
        var packBin = Path.Combine(options.Workspace, "pack-bin") + Path.DirectorySeparatorChar;
        await RunAsync(options.DotNet, ["pack", project, "-c", "Release", $"-p:Version={options.Version}", "-p:NoWarn=NU5100%3BNU5128", $"-p:DirectoryBuildTargetsPath={targets}", $"-p:BaseIntermediateOutputPath={packObj}", $"-p:BaseOutputPath={packBin}", "-p:ContinuousIntegrationBuild=true", $"-p:PathMap={options.Repository}=/_/%2C{options.Workspace}=/_work/", "-o", feed], options.Repository, "Fody package build");

        var nupkg = Path.Combine(feed, $"GodotCSharpProfiler.Fody.{options.Version}.nupkg");
        if (!File.Exists(nupkg)) throw new InvalidDataException($"Expected package was not produced: {nupkg}");
        NormalizePackage(nupkg);
        ValidateStage(stage, addon);

        var archiveName = $"godot-csharp-profiler-{options.Version}.zip";
        var archiveCandidate = Path.Combine(options.Workspace, archiveName);
        CanonicalZip.Write(archiveCandidate, EnumerateFiles(stage).Select(path => new ZipItem(Relative(stage, path), File.ReadAllBytes(path))));
        if (Environment.GetEnvironmentVariable("GCP_RELEASE_TEST_FAIL_AFTER_ZIP") == "1")
            throw new InvalidOperationException("Forced post-build validation failure.");

        if (!options.SkipTests)
        {
            if (string.IsNullOrWhiteSpace(options.PowerShell)) throw new InvalidOperationException("PowerShell is required unless validation is skipped.");
            var validationProject = Path.Combine(options.Repository, "tests", "GodotCSharpProfiler.Packaging.Tests", "GodotCSharpProfiler.Packaging.Tests.csproj");
            await RunAsync(options.DotNet, ["run", "--project", validationProject, "-c", "Release", "--", "--archive", archiveCandidate, "--version", options.Version, "--powershell", options.PowerShell], options.Repository, "Packaging validation");
        }

        var bytes = new FileInfo(archiveCandidate).Length;
        var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archiveCandidate))).ToLowerInvariant();
        var sumsCandidate = Path.Combine(options.Workspace, "SHA256SUMS");
        File.WriteAllText(sumsCandidate, $"{digest}  {archiveName}\n", Encoding.ASCII);
        var jsonCandidate = Path.Combine(options.Workspace, "release.json");
        var metadata = new JsonObject { ["version"] = options.Version, ["file"] = archiveName, ["bytes"] = bytes, ["sha256"] = digest };
        File.WriteAllText(jsonCandidate, metadata.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", Utf8);
        Publish(options.Output, options.Workspace, [(archiveCandidate, archiveName), (sumsCandidate, "SHA256SUMS"), (jsonCandidate, "release.json")]);
        Console.WriteLine($"Artifact: {Path.Combine(options.Output, archiveName)}\nBytes: {bytes}\nSHA-256: {digest}");
    }

    static void CopyAddon(string source, string destination)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        foreach (var path in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"Symlinks are forbidden in release input: {path}");
            var relative = Relative(source, path);
            if (relative.Split('/').Any(Forbidden.Contains)) continue;
            if (relative.EndsWith(".uid", StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith(".import", StringComparison.OrdinalIgnoreCase) ||
                relative.EndsWith(".translation", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(path)) Directory.CreateDirectory(target);
            else { Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(path, target); }
        }
    }

    static void TransformStage(string addon, string version)
    {
        Transform(Path.Combine(addon, "Runtime", "Sampling", "ManagedSamplingSession.cs"), text => "#if GODOT_CSHARP_PROFILER_SAMPLING\n" + NormalizeLf(text).TrimEnd('\n') + "\n#endif\n");
        ReplaceExactly(Path.Combine(addon, "plugin.cfg"), "version=\"0.1.0\"", $"version=\"{version}\"");
        ReplaceExactly(Path.Combine(addon, "Editor", "Installation", "ProjectInstaller.cs"), "ProfilerFodyVersion = \"0.1.0\"", $"ProfilerFodyVersion = \"{version}\"");
        var manifestPath = Path.Combine(addon, "assets", "dependencies.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject() ?? throw new InvalidDataException("Invalid dependencies.json.");
        manifest["bootstrapPackage"]!["version"] = version;
        manifest["automatic"]!["packages"]!["GodotCSharpProfiler.Fody"] = version;
        File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", Utf8);
    }

    static void NormalizePackage(string path)
    {
        List<ZipItem> items = [];
        string? oldCore = null;
        using (var archive = ZipFile.OpenRead(path))
        {
            foreach (var entry in archive.Entries)
            {
                ValidateZipName(entry.FullName);
                if (entry.FullName.EndsWith('/')) continue;
                using var stream = entry.Open(); using var memory = new MemoryStream(); stream.CopyTo(memory);
                if (entry.FullName.StartsWith("package/services/metadata/core-properties/", StringComparison.Ordinal))
                {
                    if (oldCore is not null) throw new InvalidDataException("Package contains multiple core-properties entries.");
                    oldCore = entry.FullName;
                }
                items.Add(new(entry.FullName, memory.ToArray()));
            }
        }
        if (oldCore is null) throw new InvalidDataException("Package core-properties entry is missing.");
        foreach (var required in RequiredPackageEntries)
            if (!items.Any(item => string.Equals(item.Name, required, StringComparison.Ordinal))) throw new InvalidDataException($"Package is missing required embedded dependency notice or binary: {required}");
        const string fixedCore = "package/services/metadata/core-properties/godot-csharp-profiler.psmdcp";
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var name = item.Name == oldCore ? fixedCore : item.Name;
            var data = item.Data;
            if (item.Name == "_rels/.rels")
            {
                var text = Utf8.GetString(data).Replace(oldCore, fixedCore, StringComparison.Ordinal);
                text = Regex.Replace(text, "(Type=\"http://schemas.microsoft.com/packaging/2010/07/manifest\"[^>]*Id=\")[^\"]+\"", "$1RMANIFEST\"");
                text = Regex.Replace(text, "(Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\"[^>]*Id=\")[^\"]+\"", "$1RMETADATA\"");
                data = Utf8.GetBytes(text);
            }
            else if (item.Name == oldCore)
            {
                var text = Utf8.GetString(data);
                text = Regex.Replace(text, "(<dcterms:(?:created|modified)[^>]*>)[^<]*(</dcterms:(?:created|modified)>)", "$12000-01-01T00:00:00Z$2");
                data = Utf8.GetBytes(text);
            }
            items[index] = new(name, data);
        }
        var temporary = path + ".canonical";
        CanonicalZip.Write(temporary, items);
        File.Move(temporary, path, true);
    }

    static void ValidateStage(string stage, string addon)
    {
        foreach (var required in Required)
            if (!File.Exists(Path.Combine(addon, required.Replace('/', Path.DirectorySeparatorChar)))) throw new InvalidDataException($"Missing required file: {required}");
        foreach (var path in Directory.EnumerateFileSystemEntries(stage, "*", SearchOption.AllDirectories))
            if (Relative(stage, path).Split('/').Any(Forbidden.Contains)) throw new InvalidDataException($"Forbidden archive content: {path}");
        if (!File.ReadAllText(Path.Combine(addon, "plugin.cfg")).Contains("script=\"Editor/CsProfilerPlugin.cs\"", StringComparison.Ordinal)) throw new InvalidDataException("Invalid plugin.cfg script path.");
        ValidateNotices(addon, "addon");
        using var package = ZipFile.OpenRead(Directory.EnumerateFiles(Path.Combine(addon, "assets", "nuget"), "GodotCSharpProfiler.Fody.*.nupkg").Single());
        foreach (var required in RequiredPackageEntries)
            if (package.GetEntry(required) is null) throw new InvalidDataException($"Nested package is missing required entry: {required}");
        ValidateNotices(ReadPackageText(package, "THIRD-PARTY-NOTICES.md"), ReadPackageText(package, "licenses/FodyHelpers-LICENSE.txt"), ReadPackageText(package, "licenses/Mono.Cecil-LICENSE.txt"), "nested package");
    }

    static void ValidateNotices(string addon, string container)
    {
        var notices = File.ReadAllText(Path.Combine(addon, "THIRD-PARTY-NOTICES.md"));
        var fodyLicense = File.ReadAllText(Path.Combine(addon, "licenses", "FodyHelpers-LICENSE.txt"));
        var cecilLicense = File.ReadAllText(Path.Combine(addon, "licenses", "Mono.Cecil-LICENSE.txt"));
        ValidateNotices(notices, fodyLicense, cecilLicense, container);
    }

    static void ValidateNotices(string notices, string fodyLicense, string cecilLicense, string container)
    {
        if (!notices.Contains("FodyHelpers 6.9.3", StringComparison.Ordinal) || !notices.Contains("Mono.Cecil 0.11.6", StringComparison.Ordinal) || !notices.Contains("FodyHelpers.dll", StringComparison.Ordinal) || !notices.Contains("Mono.Cecil.Pdb.dll", StringComparison.Ordinal) || !notices.Contains("Mono.Cecil.Rocks.dll", StringComparison.Ordinal)) throw new InvalidDataException($"{container} third-party notices are incomplete.");
        if (!fodyLicense.Contains("Copyright (c) The Fody Team and contributors", StringComparison.Ordinal) || !cecilLicense.Contains("Copyright (c) 2008 - 2015 Jb Evain", StringComparison.Ordinal) || !cecilLicense.Contains("Copyright (c) 2008 - 2011 Novell, Inc.", StringComparison.Ordinal)) throw new InvalidDataException($"{container} third-party copyright notices are incomplete.");
        const string permission = "Permission is hereby granted, free of charge, to any person obtaining";
        const string warranty = "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND";
        if (!fodyLicense.Contains(permission, StringComparison.Ordinal) || !fodyLicense.Contains(warranty, StringComparison.Ordinal) || !cecilLicense.Contains(permission, StringComparison.Ordinal) || !cecilLicense.Contains(warranty, StringComparison.Ordinal)) throw new InvalidDataException($"{container} third-party MIT license text is incomplete.");
    }

    static string ReadPackageText(ZipArchive package, string name)
    {
        using var reader = new StreamReader(package.GetEntry(name)?.Open() ?? throw new InvalidDataException($"Nested package is missing required entry: {name}"));
        return reader.ReadToEnd();
    }

    static void Publish(string output, string workspace, (string Candidate, string Name)[] files)
    {
        Directory.CreateDirectory(output);
        var transaction = Path.Combine(workspace, "publication"); Directory.CreateDirectory(transaction);
        var installed = new List<(string Final, string? Backup)>();
        try
        {
            foreach (var (candidate, name) in files)
            {
                var final = Path.Combine(output, name);
                string? backup = null;
                if (File.Exists(final)) { backup = Path.Combine(transaction, name + ".backup"); File.Move(final, backup); }
                try { File.Copy(candidate, final, false); installed.Add((final, backup)); }
                catch { if (backup is not null) File.Move(backup, final); throw; }
            }
            foreach (var (_, backup) in installed) if (backup is not null) File.Delete(backup);
        }
        catch
        {
            foreach (var (final, backup) in installed.AsEnumerable().Reverse())
            {
                File.Delete(final);
                if (backup is not null && File.Exists(backup)) File.Move(backup, final);
            }
            throw;
        }
    }

    static IEnumerable<string> EnumerateFiles(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal);
    static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    static string NormalizeLf(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');
    static string EscapeXml(string value) => value.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
    static void Transform(string path, Func<string, string> transform) => File.WriteAllText(path, transform(File.ReadAllText(path)), Utf8);
    static void ReplaceExactly(string path, string oldValue, string newValue)
    {
        var text = File.ReadAllText(path); var first = text.IndexOf(oldValue, StringComparison.Ordinal);
        if (first < 0 || text.IndexOf(oldValue, first + oldValue.Length, StringComparison.Ordinal) >= 0) throw new InvalidDataException($"Expected exactly one release token in {path}.");
        File.WriteAllText(path, NormalizeLf(text.Replace(oldValue, newValue, StringComparison.Ordinal)), Utf8);
    }
    static void ValidateVersion(string version) { if (!Regex.IsMatch(version, "^[0-9]+\\.[0-9]+\\.[0-9]+(?:[-.][0-9A-Za-z.-]+)?$")) throw new ArgumentException("Invalid version."); }
    static void ValidateZipName(string name) { if (string.IsNullOrEmpty(name) || name.StartsWith('/') || name.Contains('\\') || name.Split('/').Any(part => part is "." or "..")) throw new InvalidDataException($"Unsafe ZIP entry: {name}"); }

    static async Task RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, string purpose)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException($"{purpose} exited {process.ExitCode}.");
    }

    sealed record Options(string Repository, string Output, string Workspace, string Version, string DotNet, string? PowerShell, bool SkipTests)
    {
        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal); var skip = false;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--skip-tests") { skip = true; continue; }
                if (!args[i].StartsWith("--", StringComparison.Ordinal) || ++i == args.Length) throw new ArgumentException("Invalid release builder arguments.");
                values.Add(args[i - 1], args[i]);
            }
            string Get(string name) => values.TryGetValue(name, out var value) ? Path.GetFullPath(value) : throw new ArgumentException($"Missing {name}.");
            if (!values.TryGetValue("--version", out var version)) throw new ArgumentException("Missing --version.");
            values.TryGetValue("--powershell", out var powershell);
            return new(Get("--repository"), Get("--output"), Get("--workspace"), version, Get("--dotnet"), powershell is null ? null : Path.GetFullPath(powershell), skip);
        }
    }
}

readonly record struct ZipItem(string Name, byte[] Data);

static class CanonicalZip
{
    const uint LocalSignature = 0x04034b50, CentralSignature = 0x02014b50, EndSignature = 0x06054b50;
    const ushort Utf8Flag = 0x0800, Stored = 0, DosTime = 0, DosDate = 0x2821;
    public static void Write(string path, IEnumerable<ZipItem> source)
    {
        var items = source.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
        if (items.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != items.Length) throw new InvalidDataException("Duplicate ZIP entry.");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None); using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        var records = new List<(ZipItem Item, byte[] Name, uint Crc, uint Offset)>();
        foreach (var item in items)
        {
            Validate(item.Name); var name = Encoding.UTF8.GetBytes(item.Name);
            if (name.Length > ushort.MaxValue || item.Data.LongLength > uint.MaxValue || stream.Position > uint.MaxValue) throw new InvalidDataException("ZIP32 limit exceeded.");
            var crc = ComputeCrc32(item.Data); var offset = (uint)stream.Position;
            writer.Write(LocalSignature); writer.Write((ushort)20); writer.Write(Utf8Flag); writer.Write(Stored); writer.Write(DosTime); writer.Write(DosDate); writer.Write(crc); writer.Write((uint)item.Data.Length); writer.Write((uint)item.Data.Length); writer.Write((ushort)name.Length); writer.Write((ushort)0); writer.Write(name); writer.Write(item.Data);
            records.Add((item, name, crc, offset));
        }
        if (records.Count > ushort.MaxValue || stream.Position > uint.MaxValue) throw new InvalidDataException("ZIP32 limit exceeded.");
        var centralOffset = (uint)stream.Position;
        foreach (var record in records)
        {
            writer.Write(CentralSignature); writer.Write((ushort)20); writer.Write((ushort)20); writer.Write(Utf8Flag); writer.Write(Stored); writer.Write(DosTime); writer.Write(DosDate); writer.Write(record.Crc); writer.Write((uint)record.Item.Data.Length); writer.Write((uint)record.Item.Data.Length); writer.Write((ushort)record.Name.Length); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((uint)0); writer.Write(record.Offset); writer.Write(record.Name);
        }
        var centralSize = checked((uint)stream.Position - centralOffset);
        writer.Write(EndSignature); writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)records.Count); writer.Write((ushort)records.Count); writer.Write(centralSize); writer.Write(centralOffset); writer.Write((ushort)0);
    }
    static uint ComputeCrc32(byte[] data)
    {
        uint crc = 0xffffffff;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
    static void Validate(string name) { if (string.IsNullOrEmpty(name) || name.StartsWith('/') || name.EndsWith('/') || name.Contains('\\') || name.Split('/').Any(part => part is "" or "." or "..")) throw new InvalidDataException($"Unsafe ZIP entry: {name}"); }
}
