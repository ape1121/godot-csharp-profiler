#nullable enable
namespace Apeworks.GodotCSharpProfiler.Editor.Installation;

public sealed class InstallationRefusedException : InvalidOperationException
{
    public InstallationRefusedException(string message) : base(message) { }
    public InstallationRefusedException(string message, Exception innerException) : base(message, innerException) { }
}

public enum InstallationOperation
{
    Install,
    Uninstall,
}

public sealed record FileChange(string RelativePath, byte[]? OriginalBytes, byte[]? NewBytes, string UnifiedDiff);

public sealed record InstallationPreview(
    InstallationOperation Operation,
    Guid InstallationId,
    string ProjectPath,
    IReadOnlyList<FileChange> Changes);

public sealed record InstallationResult(
    InstallationOperation Operation,
    Guid InstallationId,
    bool Changed,
    bool CleanRequired,
    bool RebuildRequired,
    bool RestartRequired);

public sealed record InstrumentationRule(string Action, string Target, string Pattern);

public sealed record InstrumentationSettings(
    int MaximumMethods = 16_384,
    int MaximumLabelLength = 512,
    string? ProjectRoot = null,
    IReadOnlyList<InstrumentationRule>? Rules = null)
{
    public IReadOnlyList<InstrumentationRule> OrderedRules { get; } = Rules ?? Array.Empty<InstrumentationRule>();
}

public sealed record PackageSourcePlan(IReadOnlyList<string> LocalPackagePaths)
{
    public static PackageSourcePlan Empty { get; } = new(Array.Empty<string>());
}

public interface IPackageAvailabilityChecker
{
    bool IsAvailable(string packageId, string version, PackageSourcePlan sourcePlan);
}

public sealed class LocalPackageAvailabilityChecker : IPackageAvailabilityChecker
{
    public bool IsAvailable(string packageId, string version, PackageSourcePlan sourcePlan) =>
        sourcePlan.LocalPackagePaths.Any(path =>
            File.Exists(path) &&
            string.Equals(Path.GetFileName(path), $"{packageId}.{version}.nupkg", StringComparison.OrdinalIgnoreCase));
}
