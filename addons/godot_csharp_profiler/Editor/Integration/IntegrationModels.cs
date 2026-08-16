#nullable enable
using Apeworks.GodotCSharpProfiler.Editor.Modes;
using Apeworks.GodotCSharpProfiler.Protocol;

namespace Apeworks.GodotCSharpProfiler.Editor.Integration;

public enum ProfilerCommand { Start, Stop }
public enum ExportFormat { LosslessJson, VisibleCsv, ChromeTrace }
public enum InstallerGate { Ready, PackageUnavailable, NeedsBuild, NeedsRestart, Stale, NoMatches, Error }

public sealed record ResultRow(string Name, long Samples, double EstimatedCpuPercentage, long Calls,
    double ObservedWallTimeMilliseconds, double AverageWallTimeMilliseconds,
    double MaximumWallTimeMilliseconds);
public sealed record SourceResultGroup(CaptureSource Source, IReadOnlyList<ResultRow> Rows);
public sealed record ProfilerResults(IReadOnlyList<SourceResultGroup> Groups, long Truncated)
{
    public static ProfilerResults Empty { get; } = new(Array.Empty<SourceResultGroup>(), 0);
    public bool HasResults => Groups.Any(group => group.Rows.Count != 0);
}

public sealed record ToggleViewState(string Label, bool Selected, bool Enabled, string Tooltip);
public sealed record CommandViewState(bool Start, bool Stop, bool Clear, bool Copy, bool Export);
public sealed record ResultGroupViewState(string Title, CaptureSource Source, IReadOnlyList<string> Columns,
    IReadOnlyList<ResultRow> Rows, bool IsCrossSourceTotal = false);
public sealed record ProfilerDockViewState(
    string Target,
    string Status,
    IReadOnlyList<ToggleViewState> ModeSegments,
    ToggleViewState ManualOverlay,
    CommandViewState Commands,
    string SettingsStatus,
    string InstallerStatus,
    string InstallerPreviewDiff,
    string QualityBanner,
    bool ResultsVisible,
    IReadOnlyList<ResultGroupViewState> ResultGroups);

public interface IProfilerDockView { void Render(ProfilerDockViewState state); }
public interface IProfilerCommandTransport
{
    void Send(ProfilerCommand command, ModeConfiguration configuration);
}
public interface IProfilerOutput
{
    void Copy(ExportFormat format, ProfilerResults results);
    void Export(ExportFormat format, ProfilerResults results);
}

public sealed record InstallerPreviewResult(InstallerGate Gate, string? Token, string Diff, int ChangeCount);
public sealed record InstallerApplyResult(InstallerGate Gate, bool Changed, bool RebuildRequired,
    bool RestartRequired);
public interface IAutomaticInstaller
{
    InstallerPreviewResult Preview(AutomaticSettings settings);
    InstallerApplyResult Apply(string previewToken);
}

public interface IProfilerPluginHost
{
    void RegisterDock();
    void RegisterDebugger();
    void UnregisterDebugger();
    void UnregisterDock();
}
public interface ICoordinatorLifetime { void RequestDispose(); }
