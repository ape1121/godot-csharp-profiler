using System.Text;
using System.Text.Json;
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
    public void Preview_is_non_mutating_and_apply_installs_owned_pinned_integration()
    {
        using var fixture = Fixture.Create("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <!-- protected -->\n  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>\n</Project>\n");
        var before = fixture.Bytes("Game.csproj");
        var installer = new ProjectInstaller(fixture.Root);

        var preview = installer.PreviewInstall();

        Assert.NotEmpty(preview.Changes);
        Assert.Contains(preview.Changes, c => c.RelativePath == "Game.csproj" && c.UnifiedDiff.Contains("PackageReference"));
        Assert.Equal(before, fixture.Bytes("Game.csproj"));
        Assert.False(File.Exists(fixture.Path(ProjectInstaller.ConfigurationRelativePath)));

        var result = installer.Apply(preview);
        Assert.True(result.CleanRequired);
        Assert.True(result.RebuildRequired);
        Assert.True(result.RestartRequired);

        var project = XDocument.Load(fixture.Path("Game.csproj"), LoadOptions.PreserveWhitespace);
        AssertOwnedReference(project, "Fody", ProjectInstaller.FodyVersion, preview.InstallationId);
        AssertOwnedReference(project, "GodotCSharpProfiler.Fody", ProjectInstaller.ProfilerFodyVersion, preview.InstallationId);
        Assert.Contains("<!-- protected -->", File.ReadAllText(fixture.Path("Game.csproj")));

        var weavers = XDocument.Load(fixture.Path("FodyWeavers.xml"), LoadOptions.PreserveWhitespace);
        Assert.Equal(preview.InstallationId.ToString("D"), weavers.Root!.Element("GodotCSharpProfiler")!.Attribute("Owner")!.Value);
        using var config = JsonDocument.Parse(File.ReadAllText(fixture.Path(ProjectInstaller.ConfigurationRelativePath)));
        Assert.Equal(preview.InstallationId.ToString("D"), config.RootElement.GetProperty("owner").GetString());
        Assert.InRange(config.RootElement.GetProperty("limits").GetProperty("maxMethods").GetInt32(), 1, 100_000);
    }

    [Fact]
    public void Install_is_idempotent_and_uninstall_removes_only_owned_entries()
    {
        using var fixture = Fixture.Create("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <PackageReference Include=\"Fody\" Version=\"6.7.0\"><PrivateAssets>compile</PrivateAssets></PackageReference>\n  </ItemGroup>\n</Project>\n");
        fixture.Write("FodyWeavers.xml", "<Weavers>\n  <!-- keep -->\n  <Costura />\n</Weavers>\n");
        var installer = new ProjectInstaller(fixture.Root);
        installer.Apply(installer.PreviewInstall());
        var once = fixture.Snapshot();

        var second = installer.PreviewInstall();
        Assert.Empty(second.Changes);
        installer.Apply(second);
        AssertSnapshotsEqual(once, fixture.Snapshot());

        var uninstall = installer.PreviewUninstall();
        Assert.NotEmpty(uninstall.Changes);
        var result = installer.Apply(uninstall);
        Assert.True(result.CleanRequired && result.RebuildRequired && result.RestartRequired);

        var text = File.ReadAllText(fixture.Path("Game.csproj"));
        Assert.Contains("Version=\"6.7.0\"", text);
        Assert.Contains("<PrivateAssets>compile</PrivateAssets>", text);
        Assert.DoesNotContain("GodotCSharpProfiler.Fody", text);
        Assert.Equal("<Weavers>\n  <!-- keep -->\n  <Costura />\n</Weavers>\n", File.ReadAllText(fixture.Path("FodyWeavers.xml")));
        Assert.False(File.Exists(fixture.Path(ProjectInstaller.ConfigurationRelativePath)));
    }

    [Fact]
    public void Refusals_and_stale_previews_leave_every_file_byte_identical()
    {
        using var fixture = Fixture.Create();
        var installer = new ProjectInstaller(fixture.Root);
        var preview = installer.PreviewInstall();
        fixture.Write("Game.csproj", File.ReadAllText(fixture.Path("Game.csproj")) + "<!-- changed -->");
        var before = fixture.Snapshot();
        Assert.Throws<InstallationRefusedException>(() => installer.Apply(preview));
        AssertSnapshotsEqual(before, fixture.Snapshot());

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(fixture.Path("Game.csproj"), UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            before = fixture.Snapshot();
            Assert.Throws<InstallationRefusedException>(() => installer.PreviewInstall());
            AssertSnapshotsEqual(before, fixture.Snapshot());
        }
    }

    [Fact]
    public void Rejects_path_traversal_and_symlink_escape()
    {
        using var fixture = Fixture.Create();
        Assert.Throws<InstallationRefusedException>(() => new ProjectInstaller(fixture.Path("..")));

        if (!OperatingSystem.IsWindows())
        {
            using var outside = Fixture.Create();
            fixture.Delete("Game.csproj");
            File.CreateSymbolicLink(fixture.Path("Game.csproj"), outside.Path("Game.csproj"));
            Assert.Throws<InstallationRefusedException>(() => new ProjectInstaller(fixture.Root).PreviewInstall());
        }
    }

    [Fact]
    public void Apply_rejects_preview_traversal_and_rolls_back_partial_atomic_writes()
    {
        using var fixture = Fixture.Create();
        var installer = new ProjectInstaller(fixture.Root);
        var baseline = fixture.Bytes("Game.csproj");
        var traversal = new InstallationPreview(InstallationOperation.Install, Guid.NewGuid(), fixture.Path("Game.csproj"),
            [new FileChange("../escape", null, Encoding.UTF8.GetBytes("bad"), "diff")]);
        Assert.Throws<InstallationRefusedException>(() => installer.Apply(traversal));

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
    public void Malformed_weavers_and_foreign_config_are_never_modified()
    {
        using var fixture = Fixture.Create();
        fixture.Write("FodyWeavers.xml", "<Weavers>");
        var before = fixture.Snapshot();
        Assert.Throws<InstallationRefusedException>(() => new ProjectInstaller(fixture.Root).PreviewInstall());
        AssertSnapshotsEqual(before, fixture.Snapshot());

        fixture.Delete("FodyWeavers.xml");
        fixture.Write(ProjectInstaller.ConfigurationRelativePath, "{\"owner\":\"someone-else\"}");
        before = fixture.Snapshot();
        Assert.Throws<InstallationRefusedException>(() => new ProjectInstaller(fixture.Root).PreviewInstall());
        AssertSnapshotsEqual(before, fixture.Snapshot());
    }

    [Fact]
    public void Manual_addon_deletion_contract_cleanly_rebuilds_without_recorder_calls()
    {
        using var fixture = Fixture.Create("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n</Project>\n");
        fixture.Write("Player.cs", "public sealed class Player { public int Tick() => 42; }");
        fixture.Write("addons/godot_csharp_profiler/Runtime/Recorder.cs", "namespace AddonOnly; public static class Recorder { public static void Enter() { } }");
        var installer = new ProjectInstaller(fixture.Root);
        installer.Apply(installer.PreviewInstall());
        installer.Apply(installer.PreviewUninstall());
        Directory.Delete(fixture.Path("addons/godot_csharp_profiler"), recursive: true);

        Assert.DoesNotContain("GodotCSharpProfiler", File.ReadAllText(fixture.Path("Game.csproj")));
        Assert.DoesNotContain("Recorder", File.ReadAllText(fixture.Path("Player.cs")));
        Assert.False(File.Exists(fixture.Path("FodyWeavers.xml")));
        var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("dotnet", "build --nologo")
        {
            WorkingDirectory = fixture.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = build.StandardOutput.ReadToEnd() + build.StandardError.ReadToEnd();
        build.WaitForExit();
        Assert.True(build.ExitCode == 0, output);
    }

    private static void AssertOwnedReference(XDocument project, string include, string version, Guid owner)
    {
        var reference = project.Descendants("PackageReference").Single(e => (string?)e.Attribute("Include") == include && (string?)e.Attribute("Version") == version);
        Assert.Equal("all", reference.Element("PrivateAssets")?.Value);
        Assert.Equal(owner.ToString("D"), reference.Element(ProjectInstaller.OwnershipElementName)?.Value);
    }

    private static void AssertSnapshotsEqual(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach (var pair in expected) Assert.Equal(pair.Value, actual[pair.Key]);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; }
        private Fixture(string project) { Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gcsp-installer-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); Write("Game.csproj", project); }
        public static Fixture Create(string project = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>\n</Project>\n") => new(project);
        public string Path(string relative) => System.IO.Path.Combine(Root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        public void Write(string relative, string text) { var path = Path(relative); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!); File.WriteAllText(path, text, new UTF8Encoding(false)); }
        public byte[] Bytes(string relative) => File.ReadAllBytes(Path(relative));
        public void Delete(string relative) { var path = Path(relative); if (File.Exists(path)) File.Delete(path); }
        public Dictionary<string, byte[]> Snapshot() => Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).ToDictionary(p => System.IO.Path.GetRelativePath(Root, p), File.ReadAllBytes);
        public void Dispose() { try { foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.SetAttributes(path, FileAttributes.Normal); Directory.Delete(Root, true); } catch { } }
    }
}
