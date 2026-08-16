using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Apeworks.GodotCSharpProfiler.Editor.Installation;

public sealed class ProjectInstaller
{
    public const string FodyVersion = "6.8.2";
    public const string ProfilerFodyVersion = "1.0.0";
    public const string ConfigurationRelativePath = "addons/godot_csharp_profiler/instrumentation.json";
    public const string OwnershipElementName = "GodotCSharpProfilerInstallation";

    private const string WeaversPath = "FodyWeavers.xml";
    private readonly string root;
    private readonly string projectPath;

    public ProjectInstaller(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var supplied = Path.GetFullPath(projectRoot);
        root = TrimEndingSeparator(Path.GetFullPath(supplied));
        if (!Directory.Exists(root) || !string.Equals(root, TrimEndingSeparator(supplied), PathComparison))
            throw new InstallationRefusedException("The project root must be an existing canonical directory.");
        EnsureNoSymlink(root);
        projectPath = DiscoverProject(root);
    }

    public static string DiscoverProject(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var root = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(root)) throw new InstallationRefusedException("Project directory does not exist.");
        var projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly).ToArray();
        if (projects.Length != 1) throw new InstallationRefusedException("Exactly one top-level SDK-style .csproj is required.");
        EnsureSafeFile(root, projects[0], mustExist: true, requireWritable: false);
        try
        {
            var document = LoadXml(File.ReadAllBytes(projects[0]));
            if (document.Root?.Name.LocalName != "Project" || document.Root.Attribute("Sdk") is null)
                throw new InstallationRefusedException("The selected project is not SDK-style.");
        }
        catch (InstallationRefusedException) { throw; }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            throw new InstallationRefusedException("The selected project is malformed or unreadable.", ex);
        }
        return Path.GetFullPath(projects[0]);
    }

    public InstallationPreview PreviewInstall()
    {
        ValidateWritableInputs();
        var projectBytes = File.ReadAllBytes(projectPath);
        var project = LoadXml(projectBytes);
        var existingId = ReadOwnedInstallationId(project);
        var id = existingId ?? Guid.NewGuid();
        var changes = new List<FileChange>();

        AddPackageReferenceIfMissing(project, "Fody", FodyVersion, id);
        AddPackageReferenceIfMissing(project, "GodotCSharpProfiler.Fody", ProfilerFodyVersion, id);
        AddChange(changes, Relative(projectPath), projectBytes, SaveXml(project, projectBytes));

        var weavers = SafePath(WeaversPath);
        var weaverBytes = File.Exists(weavers) ? File.ReadAllBytes(weavers) : null;
        var weaverDocument = weaverBytes is null
            ? new XDocument(new XElement("Weavers"))
            : LoadXml(weaverBytes);
        if (weaverDocument.Root?.Name.LocalName != "Weavers") throw new InstallationRefusedException("FodyWeavers.xml must have a Weavers root.");
        var profilerWeavers = weaverDocument.Root.Elements().Where(e => e.Name.LocalName == "GodotCSharpProfiler").ToArray();
        if (profilerWeavers.Any(e => !string.Equals((string?)e.Attribute("Owner"), id.ToString("D"), StringComparison.OrdinalIgnoreCase)))
            throw new InstallationRefusedException("A GodotCSharpProfiler weaver not owned by this installer already exists.");
        if (profilerWeavers.Length == 0) weaverDocument.Root.Add(new XElement("GodotCSharpProfiler", new XAttribute("Owner", id.ToString("D"))));
        AddChange(changes, WeaversPath, weaverBytes, SaveXml(weaverDocument, weaverBytes));

        var configPath = SafePath(ConfigurationRelativePath);
        var configBytes = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
        if (configBytes is not null)
        {
            var owner = ReadConfigOwner(configBytes);
            if (owner != id) throw new InstallationRefusedException("Instrumentation configuration is not owned by this installation.");
        }
        var desiredConfig = CreateConfiguration(id);
        AddChange(changes, ConfigurationRelativePath, configBytes, desiredConfig);
        return new InstallationPreview(InstallationOperation.Install, id, projectPath, changes);
    }

    public InstallationPreview PreviewUninstall()
    {
        ValidateWritableInputs();
        var projectBytes = File.ReadAllBytes(projectPath);
        var project = LoadXml(projectBytes);
        var id = ReadOwnedInstallationId(project);
        if (id is null) return new InstallationPreview(InstallationOperation.Uninstall, Guid.Empty, projectPath, Array.Empty<FileChange>());
        var changes = new List<FileChange>();
        foreach (var reference in project.Descendants().Where(e => e.Name.LocalName == "PackageReference").ToArray())
        {
            var marker = reference.Elements().FirstOrDefault(e => e.Name.LocalName == OwnershipElementName);
            if (marker?.Value == id.Value.ToString("D")) reference.Remove();
        }
        foreach (var group in project.Descendants().Where(e => e.Name.LocalName == "ItemGroup" && !e.Elements().Any()).ToArray()) group.Remove();
        AddChange(changes, Relative(projectPath), projectBytes, SaveXml(project, projectBytes));

        var weavers = SafePath(WeaversPath);
        if (File.Exists(weavers))
        {
            var bytes = File.ReadAllBytes(weavers);
            var document = LoadXml(bytes);
            var owned = document.Root?.Elements().Where(e => e.Name.LocalName == "GodotCSharpProfiler" && (string?)e.Attribute("Owner") == id.Value.ToString("D")).ToArray() ?? [];
            foreach (var element in owned) element.Remove();
            var meaningfulNodes = document.Root?.Nodes().Any(n => n is XElement or XComment) == true;
            AddChange(changes, WeaversPath, bytes, meaningfulNodes ? SaveXml(document, bytes) : null);
        }

        var config = SafePath(ConfigurationRelativePath);
        if (File.Exists(config))
        {
            var bytes = File.ReadAllBytes(config);
            if (ReadConfigOwner(bytes) == id) AddChange(changes, ConfigurationRelativePath, bytes, null);
        }
        return new InstallationPreview(InstallationOperation.Uninstall, id.Value, projectPath, changes);
    }

    public InstallationResult Apply(InstallationPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!string.Equals(Path.GetFullPath(preview.ProjectPath), projectPath, PathComparison)) throw new InstallationRefusedException("Preview belongs to another project.");
        foreach (var change in preview.Changes)
        {
            var path = SafePath(change.RelativePath);
            var current = File.Exists(path) ? File.ReadAllBytes(path) : null;
            if (!BytesEqual(current, change.OriginalBytes)) throw new InstallationRefusedException($"{change.RelativePath} changed after preview.");
            EnsureSafeFile(root, path, change.OriginalBytes is not null, requireWritable: change.OriginalBytes is not null);
        }
        var applied = new List<FileChange>();
        try
        {
            foreach (var change in preview.Changes)
            {
                WriteAtomic(SafePath(change.RelativePath), change.NewBytes);
                applied.Add(change);
            }
        }
        catch (Exception ex)
        {
            try { foreach (var change in applied.AsEnumerable().Reverse()) WriteAtomic(SafePath(change.RelativePath), change.OriginalBytes); }
            catch (Exception rollback) { throw new AggregateException("Installation failed and rollback was incomplete.", ex, rollback); }
            throw new InstallationRefusedException("Installation failed; all completed writes were rolled back.", ex);
        }
        var changed = preview.Changes.Count != 0;
        return new InstallationResult(preview.Operation, preview.InstallationId, changed, changed, changed, changed);
    }

    private void ValidateWritableInputs()
    {
        EnsureSafeFile(root, projectPath, true, true);
        foreach (var relative in new[] { WeaversPath, ConfigurationRelativePath })
        {
            var path = SafePath(relative);
            if (File.Exists(path)) EnsureSafeFile(root, path, true, true);
        }
    }

    private static void AddPackageReferenceIfMissing(XDocument document, string package, string version, Guid id)
    {
        var ownedReference = document.Descendants().FirstOrDefault(e =>
            e.Name.LocalName == "PackageReference" &&
            string.Equals((string?)e.Attribute("Include"), package, StringComparison.OrdinalIgnoreCase) &&
            e.Elements().Any(marker => marker.Name.LocalName == OwnershipElementName));
        if (ownedReference is not null) return;
        // Borrowed references are deliberately left semantically untouched; the separately
        // marked exact reference is the installer's responsibility.
        var ns = document.Root!.Name.Namespace;
        var group = new XElement(ns + "ItemGroup",
            new XElement(ns + "PackageReference",
                new XAttribute("Include", package),
                new XAttribute("Version", version),
                new XElement(ns + "PrivateAssets", "all"),
                new XElement(ns + OwnershipElementName, id.ToString("D"))));
        document.Root.Add(group);
    }

    private static Guid? ReadOwnedInstallationId(XDocument project)
    {
        var values = project.Descendants().Where(e => e.Name.LocalName == OwnershipElementName).Select(e => e.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (values.Length == 0) return null;
        if (values.Length != 1 || !Guid.TryParse(values[0], out var id)) throw new InstallationRefusedException("Project ownership markers are inconsistent.");
        return id;
    }

    private static byte[] CreateConfiguration(Guid id)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "godot-csharp-profiler.instrumentation/v1");
            writer.WriteString("owner", id);
            writer.WriteStartObject("filters");
            writer.WriteStartArray("include"); writer.WriteStringValue("**/*.dll"); writer.WriteEndArray();
            writer.WriteStartArray("exclude"); writer.WriteStringValue("Godot*.dll"); writer.WriteStringValue("GodotCSharpProfiler*.dll"); writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteStartObject("limits"); writer.WriteNumber("maxMethods", 10000); writer.WriteNumber("maxLabelLength", 256); writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return stream.ToArray().Concat(new byte[] { (byte)'\n' }).ToArray();
    }

    private static Guid ReadConfigOwner(byte[] bytes)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            return Guid.TryParse(json.RootElement.GetProperty("owner").GetString(), out var owner) ? owner : Guid.Empty;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InstallationRefusedException("Instrumentation configuration is malformed.", ex);
        }
    }

    private static XDocument LoadXml(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return XDocument.Load(stream, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            throw new InstallationRefusedException("XML input is malformed.", ex);
        }
    }
    private static byte[] SaveXml(XDocument document, byte[]? original)
    {
        var encoding = DetectEncoding(original);
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = encoding, Indent = false, OmitXmlDeclaration = document.Declaration is null, NewLineHandling = NewLineHandling.None };
        using (var writer = XmlWriter.Create(stream, settings)) document.Save(writer);
        return stream.ToArray();
    }

    private static Encoding DetectEncoding(byte[]? bytes) => bytes is { Length: >= 2 } && bytes[0] == 0xff && bytes[1] == 0xfe ? new UnicodeEncoding(false, true) : new UTF8Encoding(bytes is { Length: >= 3 } && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf);

    private static void AddChange(List<FileChange> changes, string relative, byte[]? before, byte[]? after)
    {
        if (BytesEqual(before, after)) return;
        changes.Add(new FileChange(relative, before, after, CreateDiff(relative, before, after)));
    }

    private static string CreateDiff(string path, byte[]? before, byte[]? after)
    {
        var oldText = before is null ? "" : Encoding.UTF8.GetString(before);
        var newText = after is null ? "" : Encoding.UTF8.GetString(after);
        return $"--- a/{path}\n+++ b/{path}\n@@ content @@\n-{oldText.Replace("\n", "\n-")}\n+{newText.Replace("\n", "\n+")}";
    }

    private string SafePath(string relative)
    {
        if (Path.IsPathRooted(relative)) throw new InstallationRefusedException("Rooted paths are not allowed.");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison)) throw new InstallationRefusedException("Path escapes the project root.");
        var cursor = full;
        while (!string.Equals(cursor, root, PathComparison))
        {
            if (File.Exists(cursor) || Directory.Exists(cursor)) EnsureNoSymlink(cursor);
            cursor = Path.GetDirectoryName(cursor) ?? root;
        }
        return full;
    }

    private static void EnsureSafeFile(string root, string path, bool mustExist, bool requireWritable)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(TrimEndingSeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, PathComparison)) throw new InstallationRefusedException("Path escapes project root.");
        if (mustExist && !File.Exists(full)) throw new InstallationRefusedException("Expected file does not exist.");
        if (!File.Exists(full)) return;
        EnsureNoSymlink(full);
        if (requireWritable && (File.GetAttributes(full) & FileAttributes.ReadOnly) != 0) throw new InstallationRefusedException("Input file is read-only.");
        if (requireWritable && !OperatingSystem.IsWindows() && (File.GetUnixFileMode(full) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0) throw new InstallationRefusedException("Input file is read-only.");
    }

    private static void EnsureNoSymlink(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null)
            throw new InstallationRefusedException("Symbolic links and reparse points are not allowed.");
    }

    private static void WriteAtomic(string path, byte[]? bytes)
    {
        if (bytes is null) { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string Relative(string path) => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
    private static bool BytesEqual(byte[]? left, byte[]? right) => left is null ? right is null : right is not null && CryptographicOperations.FixedTimeEquals(left, right);
    private static string TrimEndingSeparator(string value) => value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
