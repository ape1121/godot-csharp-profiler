#nullable enable
using Apeworks.GodotCSharpProfiler.Editor.Installation;
using Apeworks.GodotCSharpProfiler.Editor.Modes;
using System.Security.Cryptography;
using System.Text;

namespace Apeworks.GodotCSharpProfiler.Editor.Integration;

/// <summary>UI-safe preview/apply adapter. Only previews produced by this instance can be applied.</summary>
public sealed class ProjectInstallerAdapter : IAutomaticInstaller
{
    private readonly string projectRoot;
    private readonly PackageSourcePlan packageSources;
    private readonly Func<ProjectInstaller, InstallationPreview> previewFactory;
    private readonly Func<ProjectInstaller, InstallationPreview, InstallationResult> applyFactory;
    private readonly Func<bool> packageAvailable;
    private ProjectInstaller? applyingInstaller;
    private InstallationPreview? preview;
    private string? token;

    public ProjectInstallerAdapter(ProjectInstaller installer, Func<bool>? packageAvailable = null)
        : this("", PackageSourcePlan.Empty, _ => installer.PreviewInstall(), (_, value) => installer.Apply(value),
            packageAvailable)
    {
        applyingInstaller = installer ?? throw new ArgumentNullException(nameof(installer));
    }

    public ProjectInstallerAdapter(string projectRoot, string profilerPackagePath,
        Func<bool>? packageAvailable = null)
        : this(projectRoot, new PackageSourcePlan([Path.GetFullPath(profilerPackagePath)]),
            installer => installer.PreviewInstall(),
            (installer, value) => installer.Apply(value), packageAvailable)
    {
    }

    private ProjectInstallerAdapter(string projectRoot, PackageSourcePlan packageSources,
        Func<ProjectInstaller, InstallationPreview> previewFactory,
        Func<ProjectInstaller, InstallationPreview, InstallationResult> applyFactory,
        Func<bool>? packageAvailable)
    {
        this.projectRoot = projectRoot;
        this.packageSources = packageSources;
        this.previewFactory = previewFactory;
        this.applyFactory = applyFactory;
        this.packageAvailable = packageAvailable ?? (() => true);
    }

    public InstallerPreviewResult Preview(AutomaticSettings automatic)
        => PreviewCore(automatic, uninstall: false);

    public InstallerPreviewResult PreviewUninstall()
        => PreviewCore(null, uninstall: true);

    private InstallerPreviewResult PreviewCore(AutomaticSettings? automatic, bool uninstall)
    {
        preview = null;
        token = null;
        if (!uninstall && !packageAvailable())
            return new InstallerPreviewResult(InstallerGate.PackageUnavailable, null, "", 0);
        try
        {
            var installer = string.IsNullOrEmpty(projectRoot)
                ? applyingInstaller!
                : new ProjectInstaller(projectRoot, packageSources: packageSources,
                    settings: automatic is null ? null : SettingsFor(automatic));
            var candidate = uninstall ? installer.PreviewUninstall() : previewFactory(installer);
            var candidateToken = Token(candidate);
            applyingInstaller = installer;
            preview = candidate;
            token = candidateToken;
            return new InstallerPreviewResult(InstallerGate.Ready, candidateToken,
                string.Join("\n", candidate.Changes.Select(change => change.UnifiedDiff)),
                candidate.Changes.Count);
        }
        catch (InstallationRefusedException error) when (
            error.Message.Contains("unavailable", StringComparison.OrdinalIgnoreCase))
        {
            return new InstallerPreviewResult(InstallerGate.PackageUnavailable, null, "", 0);
        }
    }

    public InstallerApplyResult Apply(string previewToken)
    {
        if (preview is null || token is null || applyingInstaller is null ||
            !string.Equals(token, previewToken, StringComparison.Ordinal))
            throw new InstallationRefusedException("A current matching preview is required before Apply.");
        var applying = preview;
        preview = null;
        token = null;
        var result = applyFactory(applyingInstaller, applying);
        applyingInstaller = null;
        return new InstallerApplyResult(GateFor(result.RebuildRequired, result.RestartRequired),
            result.Changed, result.RebuildRequired, result.RestartRequired);
    }

    public static InstallerGate GateFor(bool rebuildRequired, bool restartRequired) =>
        restartRequired ? InstallerGate.NeedsRestart
        : rebuildRequired ? InstallerGate.NeedsBuild
        : InstallerGate.Ready;

    private static InstrumentationSettings SettingsFor(AutomaticSettings automatic) => new(
        MaximumMethods: automatic.MaxMethods,
        Rules:
        [
            .. Rules("include", automatic.IncludePatterns),
            .. Rules("exclude", automatic.ExcludePatterns)
        ]);

    private static IEnumerable<InstrumentationRule> Rules(string action, string patterns) =>
        (patterns ?? "").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(pattern => new InstrumentationRule(action, "all", pattern));

    private static string Token(InstallationPreview value)
    {
        var canonical = new StringBuilder(value.InstallationId.ToString("D"));
        foreach (var change in value.Changes)
            canonical.Append('|').Append(change.RelativePath).Append('|').Append(change.UnifiedDiff);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }
}
