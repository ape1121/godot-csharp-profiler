#nullable enable
using Apeworks.GodotCSharpProfiler.Editor.Modes;
using Apeworks.GodotCSharpProfiler.Protocol;

namespace Apeworks.GodotCSharpProfiler.Editor.Integration;

/// <summary>Godot-free dock behavior. The view is a dumb renderer and never owns capture state.</summary>
public sealed class ProfilerDockController
{
    private const int MaximumPersistedResultRows = 128;
    private const int MaximumPersistedTimelineRowsPerPoint = 1;

    private readonly IProfilerDockView view;
    private readonly IProfilerCommandTransport transport;
    private readonly IAutomaticInstaller? installer;
    private readonly IProfilerOutput? output;
    private readonly Action<ProfilerDockReloadState>? reloadStateChanged;
    private readonly ModeUiController modes = new();
    private CaptureSnapshot snapshot = DisconnectedSnapshot();
    private ProfilerResults results = ProfilerResults.Empty;
    private CaptureTimeline timeline = CaptureTimeline.Empty;
    private ProfilerTerminalCapture? lastTerminalCapture;
    private string target = "No target";
    private string? statusOverride;
    private string installerStatus = "Automatic installation: not previewed";
    private string installerPreviewDiff = "";
    private string? currentPreviewToken;
    private InstallerGate installerGate = InstallerGate.Ready;
    private bool waitingForTarget;

    public ProfilerDockController(IProfilerDockView view, IProfilerCommandTransport transport,
        IAutomaticInstaller? installer, IProfilerOutput? output = null,
        ProfilerDockReloadState? initialState = null,
        Action<ProfilerDockReloadState>? reloadStateChanged = null)
    {
        this.view = view ?? throw new ArgumentNullException(nameof(view));
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.installer = installer;
        this.output = output;
        this.reloadStateChanged = reloadStateChanged;
        if (TryNormalizeReloadState(initialState, out var normalizedInitialState))
        {
            modes.Restore(normalizedInitialState.Configuration);
            lastTerminalCapture = normalizedInitialState.TerminalCapture;
            results = lastTerminalCapture?.Results ?? ProfilerResults.Empty;
            timeline = lastTerminalCapture?.Timeline ?? CaptureTimeline.Empty;
            if (lastTerminalCapture is not null)
                snapshot = DisconnectedSnapshot() with
                {
                    Completeness = lastTerminalCapture.Completeness,
                    PartialReason = lastTerminalCapture.PartialReason,
                    Quality = lastTerminalCapture.Quality
                };
        }
        Render();
    }

    public ModeConfiguration Configuration => modes.Configuration;

    public ProfilerDockReloadState CreateReloadSnapshot() => new(
        ProfilerDockReloadState.CurrentSchemaVersion, modes.Configuration.Normalize(),
        BoundTerminalCapture(lastTerminalCapture));

    public static bool TryNormalizeReloadState(ProfilerDockReloadState? value,
        out ProfilerDockReloadState normalized)
    {
        normalized = null!;
        if (value?.SchemaVersion != ProfilerDockReloadState.CurrentSchemaVersion)
            return false;
        try
        {
            if (!TryBoundConfiguration(value.Configuration, out var configuration))
                return false;
            var terminal = BoundTerminalCapture(value.TerminalCapture);
            if (value.TerminalCapture is not null && terminal is null)
                return false;
            normalized = new ProfilerDockReloadState(
                ProfilerDockReloadState.CurrentSchemaVersion, configuration, terminal);
            return true;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
                                      OverflowException or NullReferenceException)
        {
            normalized = null!;
            return false;
        }
    }

    public void UpdateSnapshot(CaptureSnapshot value, string targetDescription)
    {
        snapshot = value;
        target = SafeText(targetDescription, 160, "Unknown target");
        if (value.State is CaptureState.Starting or CaptureState.Capturing)
            waitingForTarget = false;
        statusOverride = waitingForTarget ? "Waiting for target capabilities — capture will start automatically." : null;
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
        waitingForTarget = false;
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
        PersistReloadState();
    }

    public void SelectManualOnly()
    {
        modes.SelectManualOnly();
        Render();
        PersistReloadState();
    }

    public void SetManualOverlay(bool included)
    {
        modes.SetManualOverlay(included);
        Render();
        PersistReloadState();
    }

    public void UpdateSampling(SamplingSettings settings)
    {
        modes.UpdateSampling(settings);
        Render();
        PersistReloadState();
    }

    public void UpdateAutomatic(AutomaticSettings settings)
    {
        modes.UpdateAutomatic(settings);
        InvalidatePreview();
        Render();
        PersistReloadState();
    }

    public void UpdateManual(ManualSettings settings)
    {
        modes.UpdateManual(settings);
        Render();
        PersistReloadState();
    }

    private void InvalidatePreview()
    {
        currentPreviewToken = null;
        installerPreviewDiff = "";
        installerGate = InstallerGate.Ready;
        installerStatus = "Preview required after automatic settings changed";
    }

        /// <summary>The production button path: records one normalized intent even before a target exists.</summary>
    public bool RequestStart()
    {
        if (waitingForTarget) return false;
        var presentation = Presentation();
        if (presentation.Commands.Start.Enabled)
        {
            transport.Send(ProfilerCommand.Start, modes.Configuration.Normalize());
            return true;
        }
        var orphanResetPending = snapshot.State == CaptureState.Stopping &&
                                 snapshot.LeaseOwner is null && snapshot.Fingerprint is null;
        if (snapshot.State is CaptureState.Disconnected or CaptureState.Negotiating || orphanResetPending)
        {
            waitingForTarget = true;
            statusOverride = orphanResetPending
                ? "Resetting the pre-rebuild capture — a fresh capture will start automatically."
                : "Waiting for target capabilities — capture will start automatically.";
            transport.Send(ProfilerCommand.Start, modes.Configuration.Normalize());
            Render();
            return true;
        }
        statusOverride = presentation.Commands.Start.Reason ?? "Selected mode cannot start.";
        Render();
        return false;
    }

    public bool Start() => RequestStart();

    public bool Stop()
    {
        if (waitingForTarget)
        {
            waitingForTarget = false;
            transport.Send(ProfilerCommand.CancelPending, modes.Configuration.Normalize());
            statusOverride = "Pending capture cancelled.";
            Render();
            return true;
        }
        if (!Presentation().Commands.Stop.Enabled) return false;
        transport.Send(ProfilerCommand.Stop, modes.Configuration.Normalize());
        return true;
    }

        public void Clear()
    {
        if (waitingForTarget)
        {
            waitingForTarget = false;
            transport.Send(ProfilerCommand.CancelPending, modes.Configuration.Normalize());
            statusOverride = "Pending capture cancelled.";
        }
        results = ProfilerResults.Empty;
        timeline = CaptureTimeline.Empty;
        lastTerminalCapture = null;
        Render();
        PersistReloadState();
    }

    public void ReplaceResults(ProfilerResults value)
    {
        results = BoundResults(value) ?? ProfilerResults.Empty;
        Render();
    }

    public void ReplaceTerminalCapture(ProfilerTerminalCapture value)
    {
        lastTerminalCapture = BoundTerminalCapture(value);
        if (lastTerminalCapture is null) return;
        results = lastTerminalCapture.Results;
        timeline = lastTerminalCapture.Timeline;
        snapshot = snapshot with
        {
            Completeness = lastTerminalCapture.Completeness,
            PartialReason = lastTerminalCapture.PartialReason,
            Quality = lastTerminalCapture.Quality
        };
        Render();
        PersistReloadState();
    }

    private void PersistReloadState() => reloadStateChanged?.Invoke(CreateReloadSnapshot());

    public void UpdateTimeline(CaptureTimeline value)
    {
        timeline = BoundTimeline(value) ?? CaptureTimeline.Empty;
        Render();
    }

    private static bool TryBoundConfiguration(ModeConfiguration? value,
        out ModeConfiguration normalized)
    {
        normalized = ModeConfiguration.Default;
        if (value?.Sampling is null || value.Automatic is null || value.Manual is null)
            return false;
        normalized = value.Normalize();
        if (!Enum.IsDefined(normalized.Primary) || normalized.Sampling.RequestedIntervalNanoseconds < ProtocolLimits.MinSamplingIntervalNanoseconds ||
            normalized.Sampling.RequestedIntervalNanoseconds > ProtocolLimits.MaxSamplingIntervalNanoseconds ||
            normalized.Automatic.MaxMethods < 1 || normalized.Automatic.MaxMethods > ProtocolLimits.MaxConfiguredMethods ||
            !ValidText(normalized.Sampling.IncludeAssemblies, ProtocolLimits.MaxConfigurationListCharacters) ||
            !ValidText(normalized.Sampling.ExcludeAssemblies, ProtocolLimits.MaxConfigurationListCharacters) ||
            !ValidText(normalized.Automatic.IncludePatterns, ProtocolLimits.MaxConfigurationListCharacters) ||
            !ValidText(normalized.Automatic.ExcludePatterns, ProtocolLimits.MaxConfigurationListCharacters) ||
            !ValidText(normalized.Manual.LabelPrefix, ProtocolLimits.MaxManualLabelPrefixCharacters))
            return false;
        return true;
    }

    private static ModeConfiguration BoundConfiguration(ModeConfiguration? value) =>
        TryBoundConfiguration(value, out var normalized) ? normalized : ModeConfiguration.Default;

    private static bool ValidText(string? value, int maximum) => value is not null && value.Length <= maximum &&
        !value.Any(char.IsControl);

    private static ProfilerTerminalCapture? BoundTerminalCapture(ProfilerTerminalCapture? value)
    {
        if (value is null || value.Completeness is not (CaptureCompleteness.Complete or CaptureCompleteness.Partial) ||
            value.Completeness == CaptureCompleteness.Complete && value.PartialReason != PartialReason.None ||
            value.Completeness == CaptureCompleteness.Partial && value.PartialReason == PartialReason.None ||
            !ValidQuality(value.Quality)) return null;
        var results = BoundResults(value.Results, out var omittedResultRows);
        var timeline = BoundTimeline(value.Timeline, out var omittedTimelineRows);
        if (results is null || timeline is null) return null;
        long truncated;
        try { truncated = checked(results.Truncated + omittedResultRows + omittedTimelineRows); }
        catch (OverflowException) { return null; }
        results = results with { Truncated = truncated };
        return new ProfilerTerminalCapture(results, timeline, value.Completeness,
            value.PartialReason, value.Quality);
    }

    private static ProfilerResults? BoundResults(ProfilerResults? value) =>
        BoundResults(value, ProtocolLimits.MaxMethodsPerBatch, out _);

    private static ProfilerResults? BoundResults(ProfilerResults? value, out long omittedRows) =>
        BoundResults(value, MaximumPersistedResultRows, out omittedRows);

    private static ProfilerResults? BoundResults(ProfilerResults? value, int maximumRows, out long omittedRows)
    {
        omittedRows = 0;
        if (value?.Groups is null || value.Truncated < 0) return null;
        var groups = new List<SourceResultGroup>();
        var seen = new HashSet<CaptureSource>();
        var remaining = maximumRows;
        foreach (var group in value.Groups.Take(3))
        {
            if (group?.Rows is null || !Enum.IsDefined(group.Source) || !seen.Add(group.Source)) return null;
            var rows = group.Rows.Take(Math.Min(group.Rows.Count, remaining)).ToArray();
            if (rows.Any(row => !ValidResultRow(row, group.Source))) return null;
            groups.Add(new SourceResultGroup(group.Source, rows));
            omittedRows = checked(omittedRows + group.Rows.Count - rows.Length);
            remaining -= rows.Length;
        }
        omittedRows = checked(omittedRows + value.Groups.Skip(3).Sum(group => group?.Rows?.Count ?? 0));
        return new ProfilerResults(groups, value.Truncated);
    }

    private static bool ValidResultRow(ResultRow? row, CaptureSource source) => row is not null &&
        !string.IsNullOrWhiteSpace(row.Name) && row.Name.Length <= ProtocolLimits.MaxMethodLabelCharacters &&
        !row.Name.Any(char.IsControl) && row.Samples >= 0 && row.Calls >= 0 &&
        double.IsFinite(row.EstimatedStackFrameShare) && row.EstimatedStackFrameShare >= 0 &&
        double.IsFinite(row.ObservedWallTimeMilliseconds) && row.ObservedWallTimeMilliseconds >= 0 &&
        double.IsFinite(row.AverageWallTimeMilliseconds) && row.AverageWallTimeMilliseconds >= 0 &&
        double.IsFinite(row.LargestBatchAverageWallTimeMilliseconds) && row.LargestBatchAverageWallTimeMilliseconds >= 0 &&
        (source == CaptureSource.Sampling
            ? row.Calls == 0 && row.ObservedWallTimeMilliseconds == 0 && row.AverageWallTimeMilliseconds == 0 &&
              row.LargestBatchAverageWallTimeMilliseconds == 0
            : row.Samples == 0 && row.EstimatedStackFrameShare == 0);

    private static bool ValidQuality(QualityCounters value) => value.Observed >= 0 && value.Dropped >= 0 &&
        value.Overflowed >= 0 && value.Invalid >= 0;

    private static CaptureTimeline? BoundTimeline(CaptureTimeline? value) =>
        BoundTimeline(value, ProtocolLimits.MaxMethodsPerBatch, out _);

    private static CaptureTimeline? BoundTimeline(CaptureTimeline? value, out long omittedRows) =>
        BoundTimeline(value, MaximumPersistedTimelineRowsPerPoint, out omittedRows);

    private static CaptureTimeline? BoundTimeline(CaptureTimeline? value, int maximumRowsPerPoint,
        out long omittedRows)
    {
        omittedRows = 0;
        if (value?.Points is null) return null;
        var points = value.Points.TakeLast(CaptureTimeline.MaximumPoints).ToArray();
        long previousSequence = 0;
        var bounded = new CaptureTimelinePoint[points.Length];
        for (var index = 0; index < points.Length; index++)
        {
            var point = points[index];
            if (point?.Rows is null || point.Sequence <= previousSequence || point.Value < 0 ||
                point.Observations < 0 || !Enum.IsDefined(point.Source) ||
                point.FlushFrame is { ProcessFrame: < 0 } or { ElapsedNanoseconds: <= 0 }) return null;
            var rows = point.Rows.Take(maximumRowsPerPoint).ToArray();
            if (rows.Any(row => !ValidResultRow(row, point.Source))) return null;
            omittedRows = checked(omittedRows + point.Rows.Count - rows.Length);
            bounded[index] = point with { Rows = rows };
            previousSequence = point.Sequence;
        }
        return new CaptureTimeline(bounded);
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
        var hasOutput = results.HasResults || timeline.Points.Count > 0;
        var sources = results.Groups.Select(group => group.Source).Distinct().ToArray();
        if (sources.Length == 0)
            sources = [snapshot.Source];
        var automatic = InstallerAutomaticFacts();
        var presentationSnapshot = snapshot;
        if (snapshot.State == CaptureState.Disconnected && hasOutput)
        {
            presentationSnapshot = snapshot with
            {
                Completeness = snapshot.Completeness == CaptureCompleteness.Partial
                    ? CaptureCompleteness.Partial
                    : CaptureCompleteness.Complete,
                // Keep the terminal capture's original failure reason after disconnect/reload.
                PartialReason = snapshot.PartialReason
            };
        }
        return ModePresenter.Present(ModePresentationInput.FromSnapshot(presentationSnapshot,
            modes.Configuration, hasOutput, sources, results.Truncated, automatic));
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
            new CommandViewState(waitingForTarget ? false :
                    snapshot.State is CaptureState.Disconnected or CaptureState.Negotiating ||
                    presentation.Commands.Start.Enabled,
                waitingForTarget || presentation.Commands.Stop.Enabled,
                presentation.Commands.Clear.Enabled, presentation.Commands.Copy.Enabled,
                presentation.Commands.Export.Enabled),
            $"{presentation.Overhead} overhead · Sampling interval: {presentation.Sampling.Interval.Display}",
            installerStatus,
            installerPreviewDiff,
            presentation.Quality.Banner,
            presentation.ResultsVisible,
            groups,
            timeline,
            waitingForTarget));

    }

    private static ToggleViewState Segment(string label, bool selected, Availability availability) =>
        new(label, selected, availability.Enabled,
            string.Join(" ", new[] { availability.Reason, availability.Remediation }
                .Where(value => !string.IsNullOrWhiteSpace(value))));

    private static IReadOnlyList<string> Columns(CaptureSource source) => source switch
    {
        CaptureSource.Sampling => ["Name", "Samples", "Estimated stack-frame %"],
        CaptureSource.AutomaticSpans or CaptureSource.ManualSpans =>
            ["Name", "Wall time", "Calls", "Average wall time", "Largest batch average"],
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
