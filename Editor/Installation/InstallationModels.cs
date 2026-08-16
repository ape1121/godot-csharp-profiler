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

public sealed record FileChange(
    string RelativePath,
    byte[]? OriginalBytes,
    byte[]? NewBytes,
    string UnifiedDiff);

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
