#nullable enable
using Apeworks.GodotCSharpProfiler.Editor.Installation;
using System.Security.Cryptography;
using System.Text;

namespace Apeworks.GodotCSharpProfiler.Editor.Integration;

/// <summary>UI-safe preview/apply adapter. Only previews produced by this instance can be applied.</summary>
public sealed class ProjectInstallerAdapter : IAutomaticInstaller
{
    private readonly ProjectInstaller installer;
    private readonly Func<bool> packageAvailable;
    private InstallationPreview? preview;
    private string? token;

    public ProjectInstallerAdapter(ProjectInstaller installer, Func<bool>? packageAvailable = null)
    {
        this.installer = installer ?? throw new ArgumentNullException(nameof(installer));
        this.packageAvailable = packageAvailable ?? (() => true);
    }

    public InstallerPreviewResult Preview()
    {
        preview = null;
        token = null;
        if (!packageAvailable())
            return new InstallerPreviewResult(InstallerGate.PackageUnavailable, null, "", 0);
        try
        {
            var candidate = installer.PreviewInstall();
            var candidateToken = Token(candidate);
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
        if (preview is null || token is null ||
            !string.Equals(token, previewToken, StringComparison.Ordinal))
            throw new InstallationRefusedException("A current matching preview is required before Apply.");
        var applying = preview;
        preview = null;
        token = null;
        var result = installer.Apply(applying);
        return new InstallerApplyResult(result.Changed ? InstallerGate.NeedsBuild : InstallerGate.Ready,
            result.Changed, result.RebuildRequired, result.RestartRequired);
    }

    private static string Token(InstallationPreview value)
    {
        var canonical = new StringBuilder(value.InstallationId.ToString("D"));
        foreach (var change in value.Changes)
            canonical.Append('|').Append(change.RelativePath).Append('|').Append(change.UnifiedDiff);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())))
            .ToLowerInvariant();
    }
}
