using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Apeworks.GodotCSharpProfiler.Editor.Installation;

public sealed class ProjectInstaller
{
    public const string FodyVersion = "6.9.3";
    public const string ProfilerFodyVersion = "0.1.0-dev";
    public const string OwnershipElementName = "GodotCSharpProfilerInstallation";
    public const string ReferenceOwnershipElementName = "GodotCSharpProfilerOwned";

    private const string WeaversPath = "FodyWeavers.xml";
    private readonly string root;
    private readonly string projectPath;
    private readonly IPackageAvailabilityChecker packageAvailability;
    private readonly PackageSourcePlan packageSources;
    private readonly InstrumentationSettings settings;

    public ProjectInstaller(
        string projectRoot,
        IPackageAvailabilityChecker? packageAvailability = null,
        PackageSourcePlan? packageSources = null,
        InstrumentationSettings? settings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        var supplied = Path.GetFullPath(projectRoot);
        root = TrimEndingSeparator(Path.GetFullPath(supplied));
        if (!Directory.Exists(root) || !string.Equals(root, TrimEndingSeparator(supplied), PathComparison))
            throw new InstallationRefusedException("The project root must be an existing canonical directory.");
        EnsureNoSymlink(root);
        projectPath = DiscoverProject(root);
        this.packageAvailability = packageAvailability ?? new LocalPackageAvailabilityChecker();
        this.packageSources = packageSources ?? PackageSourcePlan.Empty;
        var requested = settings ?? new InstrumentationSettings();
        this.settings = requested with { ProjectRoot = requested.ProjectRoot ?? root.Replace('\\', '/') };
        ValidateSettings(this.settings);
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

        EnsureCompatiblePackageReference(project, "Fody", FodyVersion, id);
        EnsureCompatiblePackageReference(project, "GodotCSharpProfiler.Fody", ProfilerFodyVersion, id);
        EnsureProjectOwnershipMarker(project, id);
        AddChange(changes, Relative(projectPath), projectBytes, SaveXml(project, projectBytes));

        var weaversPath = SafePath(WeaversPath);
        var weaverBytes = File.Exists(weaversPath) ? File.ReadAllBytes(weaversPath) : null;
        var weavers = weaverBytes is null ? new XDocument(new XElement("Weavers")) : LoadXml(weaverBytes);
        if (weavers.Root?.Name.LocalName != "Weavers") throw new InstallationRefusedException("FodyWeavers.xml must have a Weavers root.");
        var existing = weavers.Root.Elements().Where(e => e.Name.LocalName == "GodotCSharpProfiler").ToArray();
        if (existing.Length > 1 || existing.Any(e => !string.Equals((string?)e.Attribute("Owner"), id.ToString("D"), StringComparison.OrdinalIgnoreCase)))
            throw new InstallationRefusedException("A foreign GodotCSharpProfiler weaver already exists.");
        var desired = CreateWeaverElement(id);
        if (existing.Length == 0) weavers.Root.Add(desired); else existing[0].ReplaceWith(desired);
        AddChange(changes, WeaversPath, weaverBytes, SaveXml(weavers, weaverBytes));

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
            var owned = reference.Elements().Any(e => e.Name.LocalName == ReferenceOwnershipElementName &&
                string.Equals(e.Value, id.Value.ToString("D"), StringComparison.OrdinalIgnoreCase));
            if (owned) reference.Remove();
        }
        foreach (var marker in project.Descendants().Where(e => e.Name.LocalName == OwnershipElementName &&
                     string.Equals(e.Value, id.Value.ToString("D"), StringComparison.OrdinalIgnoreCase)).ToArray()) marker.Remove();
        RemoveEmptyGroups(project);
        AddChange(changes, Relative(projectPath), projectBytes, SaveXml(project, projectBytes));

        var weaversPath = SafePath(WeaversPath);
        if (File.Exists(weaversPath))
        {
            var bytes = File.ReadAllBytes(weaversPath);
            var document = LoadXml(bytes);
            var owned = document.Root?.Elements().Where(e => e.Name.LocalName == "GodotCSharpProfiler" &&
                string.Equals((string?)e.Attribute("Owner"), id.Value.ToString("D"), StringComparison.OrdinalIgnoreCase)).ToArray() ?? [];
            foreach (var element in owned) element.Remove();
            var meaningfulNodes = document.Root?.Nodes().Any(n => n is XElement or XComment) == true;
            AddChange(changes, WeaversPath, bytes, meaningfulNodes ? SaveXml(document, bytes) : null);
        }
        return new InstallationPreview(InstallationOperation.Uninstall, id.Value, projectPath, changes);
    }

    public InstallationResult Apply(InstallationPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!string.Equals(Path.GetFullPath(preview.ProjectPath), projectPath, PathComparison))
            throw new InstallationRefusedException("Preview belongs to another project.");
        if (preview.Operation == InstallationOperation.Install &&
            !packageAvailability.IsAvailable("GodotCSharpProfiler.Fody", ProfilerFodyVersion, packageSources))
            throw new InstallationRefusedException($"Exact package GodotCSharpProfiler.Fody {ProfilerFodyVersion} is unavailable from the configured sources.");

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
        var weavers = SafePath(WeaversPath);
        if (File.Exists(weavers)) EnsureSafeFile(root, weavers, true, true);
    }

    private static void EnsureCompatiblePackageReference(XDocument document, string package, string version, Guid id)
    {
        var references = document.Descendants().Where(e => e.Name.LocalName == "PackageReference" &&
            string.Equals((string?)e.Attribute("Include"), package, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (references.Length > 1) throw new InstallationRefusedException($"Multiple {package} package references are ambiguous.");
        if (references.Length == 1)
        {
            var actual = (string?)references[0].Attribute("Version") ?? references[0].Elements().FirstOrDefault(e => e.Name.LocalName == "Version")?.Value;
            if (!string.Equals(actual, version, StringComparison.Ordinal))
                throw new InstallationRefusedException($"Existing {package} reference must use exact version {version}.");
            return;
        }
        var ns = document.Root!.Name.Namespace;
        document.Root.Add(new XElement(ns + "ItemGroup",
            new XElement(ns + "PackageReference",
                new XAttribute("Include", package),
                new XAttribute("Version", version),
                new XElement(ns + "PrivateAssets", "all"),
                new XElement(ns + ReferenceOwnershipElementName, id.ToString("D")))));
    }

    private static void EnsureProjectOwnershipMarker(XDocument project, Guid id)
    {
        if (ReadOwnedInstallationId(project) is not null) return;
        var ns = project.Root!.Name.Namespace;
        project.Root.Add(new XElement(ns + "PropertyGroup", new XElement(ns + OwnershipElementName, id.ToString("D"))));
    }

    private static Guid? ReadOwnedInstallationId(XDocument project)
    {
        var values = project.Descendants().Where(e => e.Name.LocalName == OwnershipElementName).Select(e => e.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (values.Length == 0) return null;
        if (values.Length != 1 || !Guid.TryParse(values[0], out var id)) throw new InstallationRefusedException("Project ownership markers are inconsistent.");
        return id;
    }

    private XElement CreateWeaverElement(Guid id)
    {
        var element = new XElement("GodotCSharpProfiler",
            new XAttribute("Owner", id.ToString("D")),
            new XAttribute("MaximumMethods", settings.MaximumMethods),
            new XAttribute("MaximumLabelLength", settings.MaximumLabelLength),
            new XAttribute("ProjectRoot", settings.ProjectRoot!));
        foreach (var rule in settings.OrderedRules)
            element.Add(new XElement("Rule", new XAttribute("Action", rule.Action), new XAttribute("Target", rule.Target), new XAttribute("Pattern", rule.Pattern)));
        return element;
    }

    private static void ValidateSettings(InstrumentationSettings settings)
    {
        if (settings.MaximumMethods is <= 0 or > 16_384 || settings.MaximumLabelLength <= 0 || string.IsNullOrWhiteSpace(settings.ProjectRoot))
            throw new InstallationRefusedException("Instrumentation limits and project root must be valid.");
        foreach (var rule in settings.OrderedRules)
        {
            if (rule.Action is not ("include" or "exclude") || rule.Target is not ("namespace" or "type" or "method" or "all") || string.IsNullOrWhiteSpace(rule.Pattern))
                throw new InstallationRefusedException("Instrumentation rules contain an unsupported action, target, or pattern.");
        }
    }

    private static void RemoveEmptyGroups(XDocument project)
    {
        foreach (var group in project.Descendants().Where(e => (e.Name.LocalName is "ItemGroup" or "PropertyGroup") && !e.Elements().Any()).ToArray()) group.Remove();
    }

    private static XDocument LoadXml(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return XDocument.Load(stream, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (XmlException ex) { throw new InstallationRefusedException("XML input is malformed.", ex); }
    }

    private static byte[] SaveXml(XDocument document, byte[]? original)
    {
        var encoding = DetectEncoding(original);
        using var stream = new MemoryStream();
        var xmlSettings = new XmlWriterSettings { Encoding = encoding, Indent = false, OmitXmlDeclaration = document.Declaration is null, NewLineHandling = NewLineHandling.None };
        using (var writer = XmlWriter.Create(stream, xmlSettings)) document.Save(writer);
        return stream.ToArray();
    }

    private static Encoding DetectEncoding(byte[]? bytes) => bytes is { Length: >= 2 } && bytes[0] == 0xff && bytes[1] == 0xfe
        ? new UnicodeEncoding(false, true)
        : new UTF8Encoding(bytes is { Length: >= 3 } && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf);

    private static void AddChange(List<FileChange> changes, string relative, byte[]? before, byte[]? after)
    {
        if (!BytesEqual(before, after)) changes.Add(new FileChange(relative, before, after, CreateDiff(relative, before, after)));
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
        if (requireWritable && !OperatingSystem.IsWindows() && (File.GetUnixFileMode(full) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0)
            throw new InstallationRefusedException("Input file is read-only.");
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
