#nullable enable
using Apeworks.GodotCSharpProfiler.Editor.Modes;
using Apeworks.GodotCSharpProfiler.Protocol;

namespace Apeworks.GodotCSharpProfiler.Editor.Integration;

/// <summary>Godot-free dock behavior. The view is a dumb renderer and never owns capture state.</summary>
public sealed class ProfilerDockController
{
    private readonly IProfilerDockView view;
    private readonly IProfilerCommandTransport transport;
    private readonly IAutomaticInstaller? installer;
    private readonly IProfilerOutput? output;
    private readonly ModeUiController modes = new();
    private CaptureSnapshot snapshot = DisconnectedSnapshot();
    private ProfilerResults results = ProfilerResults.Empty;
    private string target = "No target";
    private string? statusOverride;
    private string installerStatus = "Automatic installation: not previewed";
    private string installerPreviewDiff = "";
    private string? currentPreviewToken;
    private InstallerGate installerGate = InstallerGate.Ready;

    public ProfilerDockController(IProfilerDockView view, IProfilerCommandTransport transport,
        IAutomaticInstaller? installer, IProfilerOutput? output = null)
    {
        this.view = view ?? throw new ArgumentNullException(nameof(view));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.installer = installer;
        this.output = output;
        Render();
    }

    public ModeConfiguration Configuration => modes.Configuration;

    public void UpdateSnapshot(CaptureSnapshot value, string targetDescription)
    {
        snapshot = value;
        target = SafeText(targetDescription, 160, "Unknown target");
        statusOverride = null;
        Render();
    }

    public void Disconnected(string status)
    {
        // Results intentionally remain editor-owned and visible for post-mortem inspection.
        snapshot = snapshot with
        {
            State = CaptureState.Disconnected,
            RuntimeToken = null,
            SupportedModes = CaptureModes.None
        };
        statusOverride = SafeText(status, 160, "Target disconnected");
        Render();
    }

    public void ReportStatus(string status)
    {
        statusOverride = SafeText(status, 160, "Profiler message rejected");
        Render();
    }

    public void SelectMode(PrimaryMode mode)
    {
        modes.SelectPrimary(mode);
        Render();
    }

    public void SelectManualOnly()
    {
        modes.SelectManualOnly();
        Render();
    }

    public void SetManualOverlay(bool included)
    {
        modes.SetManualOverlay(included);
        Render();
    }

    public void UpdateSampling(SamplingSettings settings)
    {
        modes.UpdateSampling(settings);
        Render();
    }

    public void UpdateAutomatic(AutomaticSettings settings)
    {
        modes.UpdateAutomatic(settings);
        InvalidatePreview();
        Render();
    }

    public void UpdateManual(ManualSettings settings)
    {
        modes.UpdateManual(settings);
        Render();
    }

    private void InvalidatePreview()
    {
        currentPreviewToken = null;
        installerPreviewDiff = "";
        installerGate = InstallerGate.Ready;
        installerStatus = "Preview required after automatic settings changed";
    }

    public void Start()
    {
        var presentation = Presentation();
        if (presentation.Commands.Start.Enabled)
            transport.Send(ProfilerCommand.Start, modes.Configuration);
    }

    public void Stop()
    {
        if (Presentation().Commands.Stop.Enabled)
            transport.Send(ProfilerCommand.Stop, modes.Configuration);
    }

    public void Clear()
    {
        if (!Presentation().Commands.Clear.Enabled)
            return;
        results = ProfilerResults.Empty;
        Render();
    }

    public void ReplaceResults(ProfilerResults value)
    {
        results = value ?? ProfilerResults.Empty;
        Render();
    }

    public void Copy(ExportFormat format)
    {
        if (Presentation().Commands.Copy.Enabled)
            output?.Copy(format, results);
    }

    public void Export(ExportFormat format)
    {
        if (Presentation().Commands.Export.Enabled)
            output?.Export(format, results);
    }

    public InstallerPreviewResult? PreviewAutomaticInstall()
        => PreviewAutomaticOperation(() => installer!.Preview(modes.Configuration.Automatic), "Installation");

    public InstallerPreviewResult? PreviewAutomaticUninstall()
        => PreviewAutomaticOperation(() => installer!.PreviewUninstall(), "Uninstall");

    private InstallerPreviewResult? PreviewAutomaticOperation(Func<InstallerPreviewResult> operation,
        string operationName)
    {
        currentPreviewToken = null;
        installerPreviewDiff = "";
        if (installer is null)
        {
            installerGate = InstallerGate.PackageUnavailable;
            installerStatus = "Package unavailable: installer is not configured";
            Render();
            return null;
        }
        try
        {
            var preview = operation();
            installerGate = preview.Gate;
            installerPreviewDiff = SafeText(preview.Diff, 16_384, "");
            installerStatus = GateStatus(preview.Gate, preview.ChangeCount);
            if (preview.Gate != InstallerGate.PackageUnavailable &&
                !string.IsNullOrWhiteSpace(preview.Token))
                currentPreviewToken = preview.Token;
            Render();
            return currentPreviewToken is null ? null : preview;
        }
        catch (Exception error)
        {
            installerGate = InstallerGate.Error;
            installerStatus = operationName + " preview failed: " + SafeText(error.Message, 120, "unknown error");
            Render();
            return null;
        }
    }

    public bool ApplyAutomaticInstall(string previewToken, bool confirmed)
    {
        if (!confirmed || installer is null || currentPreviewToken is null ||
            !string.Equals(previewToken, currentPreviewToken, StringComparison.Ordinal))
            return false;
        currentPreviewToken = null; // A preview is single-use, including failed apply attempts.
        try
        {
            var result = installer.Apply(previewToken);
            installerGate = result.Gate;
            installerPreviewDiff = "";
            installerStatus = GateStatus(result.Gate, result.Changed ? 1 : 0);
            Render();
            return result.Changed;
        }
        catch (Exception error)
        {
            installerGate = InstallerGate.Error;
            installerStatus = "Installation apply failed: " + SafeText(error.Message, 120, "unknown error");
            Render();
            return false;
        }
    }

    private ModePresentation Presentation()
    {
        var sources = results.Groups.Select(group => group.Source).Distinct().ToArray();
        if (sources.Length == 0)
            sources = [snapshot.Source];
        var automatic = InstallerAutomaticFacts();
        var presentationSnapshot = snapshot;
        if (snapshot.State == CaptureState.Disconnected && results.HasResults)
        {
            presentationSnapshot = snapshot with
            {
                Completeness = snapshot.Completeness == CaptureCompleteness.Partial
                    ? CaptureCompleteness.Partial
                    : CaptureCompleteness.Complete,
                PartialReason = snapshot.Completeness == CaptureCompleteness.Partial
                    ? PartialReason.Disconnected
                    : snapshot.PartialReason
            };
        }
        return ModePresenter.Present(ModePresentationInput.FromSnapshot(presentationSnapshot,
            modes.Configuration, results.HasResults, sources, results.Truncated, automatic));
    }

    private AutomaticFacts InstallerAutomaticFacts()
    {
        var status = installerGate switch
        {
            InstallerGate.NeedsBuild => AutomaticBuildStatus.NeedsBuild,
            InstallerGate.NeedsRestart => AutomaticBuildStatus.NeedsRestart,
            InstallerGate.Stale => AutomaticBuildStatus.StaleBuild,
            InstallerGate.NoMatches => AutomaticBuildStatus.NoMatches,
            _ => AutomaticBuildStatus.Ready
        };
        return new AutomaticFacts(status, 0, 0, 0);
    }

    private void Render()
    {
        var presentation = Presentation();
        var config = modes.Configuration;
        var groups = results.Groups.Select(group => new ResultGroupViewState(
            SourceTitle(group.Source), group.Source, Columns(group.Source), group.Rows)).ToArray();
        view.Render(new ProfilerDockViewState(
            target,
            statusOverride ?? StateStatus(snapshot.State),
            [
                Segment("Sampling", config.Primary == PrimaryMode.Sampling, presentation.Modes.Sampling),
                Segment("Automatic", config.Primary == PrimaryMode.AutomaticInstrumentation, presentation.Modes.Automatic),
                Segment("Manual", config.Primary == PrimaryMode.None, presentation.Modes.Manual)
            ],
            Segment("Include Manual", config.IncludeManual, presentation.Modes.Manual),
            new CommandViewState(presentation.Commands.Start.Enabled, presentation.Commands.Stop.Enabled,
                presentation.Commands.Clear.Enabled, presentation.Commands.Copy.Enabled,
                presentation.Commands.Export.Enabled),
            $"{presentation.Overhead} overhead · Sampling interval: {presentation.Sampling.Interval.Display}",
            installerStatus,
            installerPreviewDiff,
            presentation.Quality.Banner,
            presentation.ResultsVisible,
            groups));
    }

    private static ToggleViewState Segment(string label, bool selected, Availability availability) =>
        new(label, selected, availability.Enabled,
            string.Join(" ", new[] { availability.Reason, availability.Remediation }
                .Where(value => !string.IsNullOrWhiteSpace(value))));

    private static IReadOnlyList<string> Columns(CaptureSource source) => source switch
    {
        CaptureSource.Sampling => ["Name", "Samples", "Estimated CPU %"],
        CaptureSource.AutomaticSpans or CaptureSource.ManualSpans =>
            ["Name", "Wall time", "Calls", "Average wall time", "Maximum wall time"],
        _ => ["Name"]
    };

    private static string SourceTitle(CaptureSource source) => source switch
    {
        CaptureSource.Sampling => "Sampling (estimated)",
        CaptureSource.AutomaticSpans => "Automatic instrumentation (exact)",
        CaptureSource.ManualSpans => "Manual scopes (exact)",
        _ => "Unknown source"
    };

    private static string StateStatus(CaptureState state) => state switch
    {
        CaptureState.Disconnected => "Disconnected",
        CaptureState.Negotiating => "Negotiating capabilities",
        CaptureState.Ready => "Ready",
        CaptureState.Starting => "Starting",
        CaptureState.Capturing => "Capturing",
        CaptureState.Stopping => "Stopping",
        CaptureState.Complete => "Complete",
        CaptureState.Partial => "Partial",
        CaptureState.Busy => "Target busy",
        CaptureState.Error => "Target error",
        _ => "Unknown"
    };

    private static string GateStatus(InstallerGate gate, int changes) => gate switch
    {
        InstallerGate.PackageUnavailable => "Package unavailable",
        InstallerGate.NeedsBuild => "Needs build",
        InstallerGate.NeedsRestart => "Needs restart",
        InstallerGate.Stale => "Stale build",
        InstallerGate.NoMatches => "No matches",
        InstallerGate.Error => "Installer error",
        _ => $"Preview ready: {changes} file change(s); confirm Apply"
    };

    public static string SafeText(string? value, int maximumCharacters, string fallback)
    {
        var source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var buffer = new char[Math.Min(source.Length, maximumCharacters)];
        var count = 0;
        foreach (var character in source)
        {
            if (count == buffer.Length) break;
            buffer[count++] = char.IsControl(character) ? ' ' : character;
        }
        var safe = new string(buffer, 0, count).Trim();
        return safe.Length == 0 ? fallback : safe;
    }

    private static CaptureSnapshot DisconnectedSnapshot() => new(CaptureState.Disconnected, null, 0, 0,
        null, null, CaptureModes.None, CaptureSource.Sampling, CaptureCompleteness.Complete,
        PartialReason.None, QualityCounters.Zero, CaptureModes.None, false, 0, 1);
}
