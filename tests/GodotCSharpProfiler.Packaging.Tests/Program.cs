using System.IO.Compression;
using System.Text.RegularExpressions;

var archiveArg = Array.IndexOf(args, "--archive");
if (archiveArg < 0 || archiveArg + 1 == args.Length) throw new ArgumentException("Use --archive <zip>.");
var path = Path.GetFullPath(args[archiveArg + 1]);
if (!File.Exists(path)) throw new FileNotFoundException(path);
using var zip = ZipFile.OpenRead(path);
var names = zip.Entries.Select(e => e.FullName).ToArray();
var root = "addons/godot_csharp_profiler/";
Assert(names.Length > 10, "Archive is unexpectedly empty.");
Assert(names.All(n => n.StartsWith(root, StringComparison.Ordinal)), "Every entry must be rooted at addons/godot_csharp_profiler.");
foreach (var required in new[] { "plugin.cfg", "README.md", "LICENSE", "icon.svg", "Runtime/CsProfiler.cs", "Editor/CsProfilerPlugin.cs", "assets/setup.ps1", "assets/dependencies.json" })
    Assert(names.Contains(root + required, StringComparer.Ordinal), $"Missing {required}.");
Assert(!names.Any(n => Regex.IsMatch(n, @"(^|/)(bin|obj|\.godot|spikes|tests|src|docs|\.git)(/|$)", RegexOptions.IgnoreCase)), "Development content leaked into archive.");
Assert(!names.Any(n => n.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)), "Project files must not be archived.");
var plugin = Read(root + "plugin.cfg");
Assert(plugin.Contains("script=\"Editor/CsProfilerPlugin.cs\"", StringComparison.Ordinal), "Invalid plugin script path.");
Assert(Regex.IsMatch(plugin, "version=\\\"\\d+\\.\\d+\\.\\d+"), "Release plugin version is missing.");
var sampling = Read(root + "Runtime/Sampling/ManagedSamplingSession.cs");
Assert(sampling.StartsWith("#if GODOT_CSHARP_PROFILER_SAMPLING\n", StringComparison.Ordinal), "Raw addon must gate external sampling references.");
var manifest = Read(root + "assets/dependencies.json");
Assert(manifest.Contains("0.2.661903") && manifest.Contains("3.2.5"), "Exact sampling versions are absent.");
var nupkg = zip.Entries.SingleOrDefault(e => e.FullName.StartsWith(root + "assets/nuget/GodotCSharpProfiler.Fody.", StringComparison.Ordinal) && e.FullName.EndsWith(".nupkg", StringComparison.Ordinal));
Assert(nupkg is not null && nupkg.Length > 10_000, "Fody nupkg is absent or implausibly small.");
Assert(new FileInfo(path).Length < 25 * 1024 * 1024, "Archive exceeds 25 MiB safety budget.");
Console.WriteLine($"Validated {names.Length} files, {new FileInfo(path).Length} bytes: {path}");
return;

string Read(string name)
{
    var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"Missing {name}");
    using var reader = new StreamReader(entry.Open());
    return reader.ReadToEnd();
}
static void Assert(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
