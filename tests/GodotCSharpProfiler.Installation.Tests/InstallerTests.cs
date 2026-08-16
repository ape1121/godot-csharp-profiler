using System.Text;
using System.Xml.Linq;
using Apeworks.GodotCSharpProfiler.Editor.Installation;
using Xunit;

namespace GodotCSharpProfiler.Installation.Tests;

public sealed class InstallerTests
{
    [Fact]
    public void Discovery_requires_exactly_one_well_formed_sdk_project()
    {
        using var fixture = Fixture.Create();
        Assert.Equal(fixture.Path("Game.csproj"), ProjectInstaller.DiscoverProject(fixture.Root));

        fixture.Write("Other.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Assert.Throws<InstallationRefusedException>(() => ProjectInstaller.DiscoverProject(fixture.Root));
        fixture.Delete("Other.csproj");
        fixture.Write("Game.csproj", "<Project Sdk=\"broken");
        Assert.Throws<InstallationRefusedException>(() => ProjectInstaller.DiscoverProject(fixture.Root));
    }

    [Fact]
    public void Preview_and_apply_install_exact_packages_and_authoritative_xml_without_json()
    {
        using var fixture = Fixture.Create("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <!-- protected -->\n</Project>\n");
        var installer = fixture.Installer(settings: new InstrumentationSettings(
            MaximumMethods: 4321,
            MaximumLabelLength: 123,
            ProjectRoot: "res://",
            Rules:
            [
                new InstrumentationRule("include", "namespace", "Game.*"),
                new InstrumentationRule("exclude", "method", "Game.Secret::*"),
                new InstrumentationRule("include", "type", "Game.Player"),
            ]));
        var before = fixture.Snapshot();

        var preview = installer.PreviewInstall();

        Assert.NotEmpty(preview.Changes);
        Assert.All(preview.Changes, change => Assert.False(string.IsNullOrWhiteSpace(change.UnifiedDiff)));
        AssertSnapshotsEqual(before, fixture.Snapshot());
        Assert.DoesNotContain(preview.Changes, c => c.RelativePath.EndsWith("instrumentation.json", StringComparison.OrdinalIgnoreCase));

        installer.Apply(preview);
        Assert.False(File.Exists(fixture.Path("addons/godot_csharp_profiler/instrumentation.json")));
        var project = XDocument.Load(fixture.Path("Game.csproj"));
        AssertReference(project, "Fody", "6.9.3", owned: true);
        AssertReference(project, "GodotCSharpProfiler.Fody", "0.1.0-dev", owned: true);
        Assert.Equal(preview.InstallationId.ToString("D"), project.Root!.Elements("PropertyGroup").Elements(ProjectInstaller.OwnershipElementName).Single().Value);
        Assert.Equal("addons/godot_csharp_profiler/assets/nuget", project.Descendants(ProjectInstaller.PackageSourceElementName).Single().Value);
        Assert.Equal("$(RestoreAdditionalProjectSources);$(GodotCSharpProfilerPackageSource)",
            project.Descendants(ProjectInstaller.RestoreSourcesElementName).Single().Value);

        var element = XDocument.Load(fixture.Path("FodyWeavers.xml")).Root!.Element("GodotCSharpProfiler")!;
        Assert.Equal(preview.InstallationId.ToString("D"), (string?)element.Attribute("Owner"));
        Assert.Equal("4321", (string?)element.Attribute("MaximumMethods"));
        Assert.Equal("123", (string?)element.Attribute("MaximumLabelLength"));
        Assert.Equal("res://", (string?)element.Attribute("ProjectRoot"));
        Assert.Equal(
            new[] { "include|namespace|Game.*", "exclude|method|Game.Secret::*", "include|type|Game.Player" },
            element.Elements("Rule").Select(r => $"{(string?)r.Attribute("Action")}|{(string?)r.Attribute("Target")}|{(string?)r.Attribute("Pattern")}"));
    }

    [Fact]
    public void Default_install_has_no_broad_include_rule()
    {
        using var fixture = Fixture.Create();
        var installer = fixture.Installer();
        installer.Apply(installer.PreviewInstall());
        var rules = XDocument.Load(fixture.Path("FodyWeavers.xml")).Root!.Element("GodotCSharpProfiler")!.Elements("Rule");
        Assert.DoesNotContain(rules, r => (string?)r.Attribute("Action") == "include" && (string?)r.Attribute("Target") == "all");
    }

    [Fact]
    public void Compatible_exact_references_are_borrowed_without_duplicates_and_survive_uninstall()
    {
        using var fixture = Fixture.Create("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Fody" Version="6.9.3" PrivateAssets="all" />
                <PackageReference Include="GodotCSharpProfiler.Fody"><Version>0.1.0-dev</Version></PackageReference>
              </ItemGroup>
            </Project>
            """);
        var installer = fixture.Installer();
        installer.Apply(installer.PreviewInstall());

        var installed = XDocument.Load(fixture.Path("Game.csproj"));
        Assert.Single(References(installed, "Fody"));
        Assert.Single(References(installed, "GodotCSharpProfiler.Fody"));
        AssertReference(installed, "Fody", "6.9.3", owned: false);
        AssertReference(installed, "GodotCSharpProfiler.Fody", "0.1.0-dev", owned: false);

        installer.Apply(installer.PreviewUninstall());
        var uninstalled = XDocument.Load(fixture.Path("Game.csproj"));
        Assert.Single(References(uninstalled, "Fody"));
        Assert.Single(References(uninstalled, "GodotCSharpProfiler.Fody"));
        Assert.Empty(uninstalled.Descendants(ProjectInstaller.OwnershipElementName));
        Assert.False(File.Exists(fixture.Path("FodyWeavers.xml")));
    }

    [Theory]
    [InlineData("Fody", "6.8.2")]
    [InlineData("GodotCSharpProfiler.Fody", "1.0.0")]
    public void Incompatible_reference_refuses_with_preview_byte_identical(string package, string version)
    {
        using var fixture = Fixture.Create($"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"{package}\" Version=\"{version}\" /></ItemGroup></Project>");
        var before = fixture.Snapshot();
        Assert.Throws<InstallationRefusedException>(() => fixture.Installer().PreviewInstall());
        AssertSnapshotsEqual(before, fixture.Snapshot());
    }

    [Fact]
    public void Foreign_weaver_refuses_without_changes()
    {
        using var fixture = Fixture.Create();
        fixture.Write("FodyWeavers.xml", "<Weavers><GodotCSharpProfiler MaximumMethods=\"1\" /></Weavers>");
        var before = fixture.Snapshot();
        Assert.Throws<InstallationRefusedException>(() => fixture.Installer().PreviewInstall());
        AssertSnapshotsEqual(before, fixture.Snapshot());
    }

    [Fact]
    public void Apply_refuses_when_exact_profiler_package_is_unavailable_without_changes()
    {
        using var fixture = Fixture.Create();
        var source = fixture.Path("addons/godot_csharp_profiler/assets/nuget/GodotCSharpProfiler.Fody.0.1.0-dev.nupkg");
        fixture.Write("addons/godot_csharp_profiler/assets/nuget/placeholder", "local source");
        var checker = new FakeAvailabilityChecker(false);
        var installer = fixture.Installer(checker, new PackageSourcePlan([source]));
        var preview = installer.PreviewInstall();
        var before = fixture.Snapshot();

        Assert.Throws<InstallationRefusedException>(() => installer.Apply(preview));
        AssertSnapshotsEqual(before, fixture.Snapshot());
        Assert.Equal(("GodotCSharpProfiler.Fody", "0.1.0-dev", source), checker.LastCheck);
    }

    [Fact]
    public void Install_is_idempotent_and_uninstall_removes_only_owned_entries()
    {
        using var fixture = Fixture.Create("""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><ForeignRestoreSource>foreign-feed</ForeignRestoreSource></PropertyGroup>
            </Project>
            """);
        fixture.Write("FodyWeavers.xml", "<Weavers>\n  <!-- keep -->\n  <Costura />\n</Weavers>\n");
        var installer = fixture.Installer();
        installer.Apply(installer.PreviewInstall());
        var once = fixture.Snapshot();
        Assert.Empty(installer.PreviewInstall().Changes);
        AssertSnapshotsEqual(once, fixture.Snapshot());

        installer.Apply(installer.PreviewUninstall());
        Assert.DoesNotContain("GodotCSharpProfiler", File.ReadAllText(fixture.Path("Game.csproj")));
        Assert.Contains("<ForeignRestoreSource>foreign-feed</ForeignRestoreSource>", File.ReadAllText(fixture.Path("Game.csproj")));
        Assert.DoesNotContain(ProjectInstaller.RestoreSourcesElementName, File.ReadAllText(fixture.Path("Game.csproj")));
        Assert.Equal("<Weavers>\n  <!-- keep -->\n  <Costura />\n</Weavers>\n", File.ReadAllText(fixture.Path("FodyWeavers.xml")));
    }

    [Fact]
    public void Package_source_must_be_the_exact_bundled_filename_inside_project()
    {
        using var fixture = Fixture.Create();
        using var outside = Fixture.Create();
        var outsidePackage = outside.Path("GodotCSharpProfiler.Fody.0.1.0-dev.nupkg");
        var outsideInstaller = new ProjectInstaller(fixture.Root, new FakeAvailabilityChecker(true),
            new PackageSourcePlan([outsidePackage]));
        Assert.Throws<InstallationRefusedException>(() => outsideInstaller.PreviewInstall());

        var wrongName = fixture.Path("addons/godot_csharp_profiler/assets/nuget/wrong.nupkg");
        var wrongInstaller = new ProjectInstaller(fixture.Root, new FakeAvailabilityChecker(true),
            new PackageSourcePlan([wrongName]));
        Assert.Throws<InstallationRefusedException>(() => wrongInstaller.PreviewInstall());
    }

    [Fact]
    public void Stale_preview_path_traversal_symlink_and_atomic_rollback_protections_remain()
    {
        using var fixture = Fixture.Create();
        var installer = fixture.Installer();
        var preview = installer.PreviewInstall();
        fixture.Write("Game.csproj", File.ReadAllText(fixture.Path("Game.csproj")) + "<!-- changed -->");
        var before = fixture.Snapshot();
        Assert.Throws<InstallationRefusedException>(() => installer.Apply(preview));
        AssertSnapshotsEqual(before, fixture.Snapshot());
        Assert.Throws<InstallationRefusedException>(() => new ProjectInstaller(fixture.Path(".."), new FakeAvailabilityChecker(true)));
        if (!OperatingSystem.IsWindows())
        {
            using var outside = Fixture.Create();
            using var linked = Fixture.Create();
            linked.Delete("Game.csproj");
            File.CreateSymbolicLink(linked.Path("Game.csproj"), outside.Path("Game.csproj"));
            Assert.Throws<InstallationRefusedException>(() => linked.Installer().PreviewInstall());
        }

        var baseline = fixture.Bytes("Game.csproj");
        Directory.CreateDirectory(fixture.Path("blocked"));
        var rollback = new InstallationPreview(InstallationOperation.Install, Guid.NewGuid(), fixture.Path("Game.csproj"),
        [
            new FileChange("Game.csproj", baseline, Encoding.UTF8.GetBytes("changed"), "diff"),
            new FileChange("blocked", null, Encoding.UTF8.GetBytes("cannot replace directory"), "diff"),
        ]);
        Assert.Throws<InstallationRefusedException>(() => installer.Apply(rollback));
        Assert.Equal(baseline, fixture.Bytes("Game.csproj"));
    }

    [Fact]
    public void Uninstall_after_addon_deletion_leaves_clean_build_configuration()
    {
        using var fixture = Fixture.Create("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        fixture.Write("Player.cs", "public sealed class Player { public int Tick() => 42; }");
        fixture.Write("addons/godot_csharp_profiler/Runtime/Recorder.cs", "namespace AddonOnly; public static class Recorder { }");
        var installer = fixture.Installer();
        installer.Apply(installer.PreviewInstall());
        Directory.Delete(fixture.Path("addons/godot_csharp_profiler"), recursive: true);
        installer.Apply(installer.PreviewUninstall());

        Assert.DoesNotContain("GodotCSharpProfiler", File.ReadAllText(fixture.Path("Game.csproj")));
        Assert.False(File.Exists(fixture.Path("FodyWeavers.xml")));
        var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", "build --nologo")
        {
            WorkingDirectory = fixture.Root, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        var output = build.StandardOutput.ReadToEnd() + build.StandardError.ReadToEnd();
        build.WaitForExit();
        Assert.True(build.ExitCode == 0, output);
    }

    private static IEnumerable<XElement> References(XDocument project, string package) =>
        project.Descendants().Where(e => e.Name.LocalName == "PackageReference" && string.Equals((string?)e.Attribute("Include"), package, StringComparison.OrdinalIgnoreCase));

    private static void AssertReference(XDocument project, string package, string version, bool owned)
    {
        var reference = Assert.Single(References(project, package));
        Assert.Equal(version, (string?)reference.Attribute("Version") ?? reference.Elements().SingleOrDefault(e => e.Name.LocalName == "Version")?.Value);
        Assert.Equal(owned, reference.Elements().Any(e => e.Name.LocalName == ProjectInstaller.ReferenceOwnershipElementName));
    }

    private static void AssertSnapshotsEqual(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var pair in expected) Assert.Equal(pair.Value, actual[pair.Key]);
    }

    private sealed class FakeAvailabilityChecker(bool available) : IPackageAvailabilityChecker
    {
        public (string Package, string Version, string LocalPath)? LastCheck { get; private set; }
        public bool IsAvailable(string packageId, string version, PackageSourcePlan sourcePlan)
        {
            LastCheck = (packageId, version, Assert.Single(sourcePlan.LocalPackagePaths));
            return available;
        }
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        private Fixture(string project) { Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gcsp-installer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); Write("Game.csproj", project); }
        public static Fixture Create(string project = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>\n</Project>\n") => new(project);
        public ProjectInstaller Installer(IPackageAvailabilityChecker? checker = null, PackageSourcePlan? sourcePlan = null, InstrumentationSettings? settings = null)
        {
            var path = Path("addons/godot_csharp_profiler/assets/nuget/GodotCSharpProfiler.Fody.0.1.0-dev.nupkg");
            return new ProjectInstaller(Root, checker ?? new FakeAvailabilityChecker(true), sourcePlan ?? new PackageSourcePlan([path]), settings);
        }
        public string Path(string relative) => System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        public void Write(string relative, string text) { var path = Path(relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); }
        public byte[] Bytes(string relative) => File.ReadAllBytes(Path(relative));
        public void Delete(string relative) { var path = Path(relative); if (File.Exists(path)) File.Delete(path); }
        public Dictionary<string, byte[]> Snapshot() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).ToDictionary(p => System.IO.Path.GetRelativePath(Root, p), File.ReadAllBytes);
        public void Dispose() { try { foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal); Directory.Delete(Root, true); } catch { } }
    }
}
