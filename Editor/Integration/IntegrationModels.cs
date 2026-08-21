#nullable enable
using Apeworks.GodotCSharpProfiler.Editor.Modes;
using Apeworks.GodotCSharpProfiler.Protocol;
using Apeworks.GodotCSharpProfiler.Runtime.Protocol.Adapters;

namespace Apeworks.GodotCSharpProfiler.Editor.Integration;

public enum ProfilerCommand { Start, Stop, CancelPending }
public enum ExportFormat { LosslessJson, VisibleCsv, ChromeTrace }
public enum OrphanRecoveryAction { None, WaitForNegotiation, ResetOrphan, StartFresh, RestartTargetRequired }

public static class OrphanRecoveryPolicy
{
    public static OrphanRecoveryAction Decide(bool selected, bool runtimeMatches, bool identityCapturing,
        bool resetSupported, long identityGeneration, CaptureSnapshot endpoint, long resetCompletedGeneration,
        bool explicitStartIntent)
    {
        if (!selected) return OrphanRecoveryAction.None;
        if (endpoint.LeaseOwner is not null && endpoint.State is
            CaptureState.Starting or CaptureState.Capturing or CaptureState.Stopping)
            return OrphanRecoveryAction.None;
        var orphanResetPending = endpoint.State == CaptureState.Stopping &&
                                 endpoint.LeaseOwner is null && endpoint.Fingerprint is null;
        if (orphanResetPending) return OrphanRecoveryAction.None;
        var mayStart = endpoint.State is CaptureState.Ready or CaptureState.Complete or CaptureState.Partial;
        var capabilitiesReady = endpoint.SupportedModes != CaptureModes.None && endpoint.CapabilityMaxMethods > 0;
        if (identityCapturing)
        {
            // A locally accepted terminal state is newer authority than an observational Ready packet
            // emitted while this same generation was still running.
            if (endpoint.State is CaptureState.Complete or CaptureState.Partial &&
                endpoint.Generation >= identityGeneration)
                return explicitStartIntent && runtimeMatches && capabilitiesReady
                    ? OrphanRecoveryAction.StartFresh
                    : OrphanRecoveryAction.None;
            if (identityGeneration < 1)
                return OrphanRecoveryAction.RestartTargetRequired;
            // Ready.Capturing describes generation G at discovery time. Once G was reset, that
            // observation is stale and must never trigger another reset or handshake loop.
            if (resetCompletedGeneration == identityGeneration)
                return explicitStartIntent && runtimeMatches && mayStart && capabilitiesReady
                    ? OrphanRecoveryAction.StartFresh
                    : explicitStartIntent ? OrphanRecoveryAction.WaitForNegotiation : OrphanRecoveryAction.None;
            if (!runtimeMatches || endpoint.State != CaptureState.Ready ||
                endpoint.Generation != identityGeneration)
                return OrphanRecoveryAction.WaitForNegotiation;
            if (!resetSupported)
                return OrphanRecoveryAction.RestartTargetRequired;
            return OrphanRecoveryAction.ResetOrphan;
        }
        if (!explicitStartIntent) return OrphanRecoveryAction.None;
        if (!runtimeMatches || !mayStart || !capabilitiesReady) return OrphanRecoveryAction.WaitForNegotiation;
        return OrphanRecoveryAction.StartFresh;
    }
}
public enum InstallerGate { Ready, PackageUnavailable, NeedsBuild, NeedsRestart, Stale, NoMatches, Error }

public sealed record ResultRow(string Name, long Samples, double EstimatedStackFrameShare, long Calls,
    double ObservedWallTimeMilliseconds, double AverageWallTimeMilliseconds,
    double LargestBatchAverageWallTimeMilliseconds);
public sealed record SourceResultGroup(CaptureSource Source, IReadOnlyList<ResultRow> Rows);
public sealed record ProfilerResults(IReadOnlyList<SourceResultGroup> Groups, long Truncated)
{
    public static ProfilerResults Empty { get; } = new(Array.Empty<SourceResultGroup>(), 0);
    public bool HasResults => Groups.Any(group => group.Rows.Count != 0);
}

/// <summary>
/// A bounded recent-batch timeline. Values are depth-weighted stack-frame observations for sampling
/// and nanoseconds for exact spans.
/// </summary>
public sealed record CaptureTimelinePoint(
    long Sequence,
    CaptureSource Source,
    long Value,
    long Observations,
    IReadOnlyList<ResultRow> Rows,
    BatchFlushFrame? FlushFrame = null);
public sealed record BatchFlushFrame(long ProcessFrame, long ElapsedNanoseconds);
public sealed record CaptureTimeline(IReadOnlyList<CaptureTimelinePoint> Points)
{
    public const int MaximumPoints = 120;
    public static CaptureTimeline Empty { get; } = new(Array.Empty<CaptureTimelinePoint>());
}

/// <summary>One terminal editor-owned capture; never an in-flight lease or pending aggregate.</summary>
public sealed record ProfilerTerminalCapture(
    ProfilerResults Results,
    CaptureTimeline Timeline,
    CaptureCompleteness Completeness,
    PartialReason PartialReason,
    QualityCounters Quality);

/// <summary>Versioned, bounded editor-owned state that may survive a managed assembly reload.</summary>
public sealed record ProfilerDockReloadState(
    int SchemaVersion,
    ModeConfiguration Configuration,
    ProfilerTerminalCapture? TerminalCapture)
{
    public const int CurrentSchemaVersion = 1;
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
    IReadOnlyList<ResultGroupViewState> ResultGroups,
    CaptureTimeline Timeline,
    bool CapturePending);

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
    InstallerPreviewResult PreviewUninstall();
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

public sealed class EditorCaptureCoordinator
{
    private readonly string owner;
    private readonly Action<WireMap> send;
    private readonly CaptureProtocolParser parser = new();
    private readonly Dictionary<CaptureSource, Dictionary<long, Aggregate>> pending = new();
    private readonly List<CaptureTimelinePoint> timeline = new();
    private bool stopRequestedWhileStarting;
    private string? resetRequestId;
    private long resetGeneration;
    private int configuredMaxMethods = ProtocolLimits.MaxMethodsPerBatch;
    private CaptureSnapshot snapshot = new(CaptureState.Negotiating, null, 0, 0, null, null,
        CaptureModes.None, CaptureSource.Sampling, CaptureCompleteness.InProgress, PartialReason.None,
        QualityCounters.Zero, CaptureModes.None, false, 0, 0);

    public EditorCaptureCoordinator(string owner, Action<WireMap> send)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Capture owner is required.", nameof(owner));
        this.owner = owner;
        this.send = send ?? throw new ArgumentNullException(nameof(send));
    }

    public CaptureSnapshot Snapshot => snapshot;
    public bool ResetSupported { get; private set; }
    public bool ResetPending => resetRequestId is not null;
    public long ResetCompletedGeneration { get; private set; }
    public ProfilerResults CompletedResults { get; private set; } = ProfilerResults.Empty;
    public CaptureTimeline Timeline => new(timeline.ToArray());
    public ProfilerTerminalCapture? LastTerminalCapture { get; private set; }
    public event Action<CaptureSnapshot>? SnapshotChanged;
    public event Action<ProfilerResults>? CompletedResultsChanged;
    public event Action<CaptureTimeline>? TimelineChanged;
    public event Action<ProfilerTerminalCapture>? TerminalCaptureChanged;
    public event Action<string>? Rejected;

    public bool AssociateBatchFlushFrame(long generation, long sequence, BatchFlushFrame frame)
    {
        if (generation != snapshot.Generation || sequence <= 0 || frame is null ||
            frame.ProcessFrame < 0 || frame.ElapsedNanoseconds <= 0) return false;
        var index = timeline.FindIndex(point => point.Sequence == sequence);
        if (index < 0) return false;
        if (timeline[index].FlushFrame is { } existing) return existing == frame;
        timeline[index] = timeline[index] with { FlushFrame = frame };
        TimelineChanged?.Invoke(Timeline);
        if (LastTerminalCapture is not null && snapshot.State is (CaptureState.Complete or CaptureState.Partial))
        {
            LastTerminalCapture = LastTerminalCapture with { Timeline = Timeline };
            TerminalCaptureChanged?.Invoke(LastTerminalCapture);
        }
        return true;
    }

    public bool Receive(object? payload)
    {
        var failure = ParseFailure.Malformed;
        if (!StrictWireAdapter.TryConvert(payload, out var wire) || wire is null ||
            !parser.TryParse(wire, out var message, out failure) || message is null)
            return Reject($"Profiler protocol packet rejected ({failure}).");

        var accepted = message switch
        {
            HelloMessage value => AcceptHello(value),
            CapabilitiesMessage value => AcceptCapabilities(value),
            StateMessage value => AcceptState(value),
            BatchMessage value => AcceptBatch(value),
            ResetAckMessage value => AcceptResetAck(value),
            ErrorMessage value => AcceptError(value),
            _ => false
        };
        if (!accepted) return Reject("Profiler protocol packet rejected (stale, duplicate, gap, or incompatible identity).");
        if (message is ErrorMessage error) Rejected?.Invoke(ProfilerDockController.SafeText(error.Message, 160, "Runtime capture error"));
        if (message is StateMessage terminal && terminal.State is CaptureState.Complete or CaptureState.Partial)
        {
            // Publish the terminal summary before results so persistence commits results, final timeline,
            // completeness, reason, and quality as one editor-owned terminal capture.
            SnapshotChanged?.Invoke(snapshot);
            CommitResults();
            return true;
        }
        SnapshotChanged?.Invoke(snapshot);
        return true;
    }

    public bool Start(ModeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (resetRequestId is not null ||
            snapshot.State is not (CaptureState.Ready or CaptureState.Complete or CaptureState.Partial) ||
            snapshot.RuntimeToken is null || snapshot.SupportedModes == CaptureModes.None) return false;
        var normalized = configuration.Normalize();
        var generation = checked(snapshot.Generation + 1);
        var interval = (normalized.Modes & CaptureModes.Sampling) != 0 &&
                       snapshot.SamplingIntervalRuntimeConfigurable
            ? normalized.Sampling.RequestedIntervalNanoseconds
            : 0;
        var maxMethods = Math.Min(normalized.Automatic.MaxMethods, snapshot.CapabilityMaxMethods);
        if (normalized.Modes == CaptureModes.None || (normalized.Modes & ~snapshot.SupportedModes) != 0 ||
            maxMethods < 1 ||
            !ValidConfigurationText(normalized.Sampling.IncludeAssemblies,
                ProtocolLimits.MaxConfigurationListCharacters) ||
            !ValidConfigurationText(normalized.Sampling.ExcludeAssemblies,
                ProtocolLimits.MaxConfigurationListCharacters) ||
            !ValidConfigurationText(normalized.Manual.LabelPrefix,
                ProtocolLimits.MaxManualLabelPrefixCharacters)) return false;
        snapshot = snapshot with { Generation = generation, Sequence = 0, Fingerprint = normalized.Fingerprint,
            LeaseOwner = owner, Modes = normalized.Modes, State = CaptureState.Starting,
            Completeness = CaptureCompleteness.InProgress, PartialReason = PartialReason.None, Quality = QualityCounters.Zero };
        pending.Clear();
        timeline.Clear();
        stopRequestedWhileStarting = false;
        configuredMaxMethods = maxMethods;
        TimelineChanged?.Invoke(CaptureTimeline.Empty);
        send(StrictWireAdapter.Serialize(new ConfigureMessage(ProtocolVersion.Major, ProtocolVersion.Minor,
            snapshot.RuntimeToken, generation, normalized.Fingerprint, normalized.Modes, interval, maxMethods,
            normalized.Sampling.IncludeAssemblies, normalized.Sampling.ExcludeAssemblies,
            normalized.Manual.LabelPrefix)));
        send(StrictWireAdapter.Serialize(new StartMessage(ProtocolVersion.Major, ProtocolVersion.Minor,
            snapshot.RuntimeToken, generation, normalized.Fingerprint)));
        SnapshotChanged?.Invoke(snapshot);
        return true;
    }

    public bool RequestOrphanReset(long generation, string requestId, bool resetSupported = true)
    {
        ResetSupported = resetSupported;
        if (!ResetSupported || snapshot.RuntimeToken is null || generation < 1 || snapshot.Generation != generation ||
            resetRequestId is not null || !ValidRequestId(requestId)) return false;
        resetGeneration = generation;
        resetRequestId = requestId.ToLowerInvariant();
        snapshot = snapshot with { State = CaptureState.Stopping };
        send(StrictWireAdapter.Serialize(new ResetMessage(ProtocolVersion.Major, ProtocolVersion.Minor,
            snapshot.RuntimeToken, generation, resetRequestId)));
        SnapshotChanged?.Invoke(snapshot);
        return true;
    }

    public bool RetryOrphanReset()
    {
        if (!ResetSupported || resetRequestId is null || resetGeneration < 1 ||
            snapshot.RuntimeToken is null) return false;
        send(StrictWireAdapter.Serialize(new ResetMessage(ProtocolVersion.Major, ProtocolVersion.Minor,
            snapshot.RuntimeToken, resetGeneration, resetRequestId)));
        return true;
    }

    public bool Stop()
    {
        if (snapshot.State is not (CaptureState.Starting or CaptureState.Capturing) ||
            snapshot.RuntimeToken is null || snapshot.Fingerprint is null || snapshot.LeaseOwner != owner) return false;
        if (snapshot.State == CaptureState.Starting)
        {
            stopRequestedWhileStarting = true;
            return true;
        }
        return SendStop();
    }

    private bool SendStop()
    {
        // Stop must not consume a shared-stream sequence slot: batches may be in flight, and the
        // runtime validates Stop by generation/fingerprint/owner rather than exact sequence. Keeping
        // the local sequence untouched keeps in-flight batches and the terminal state contiguous.
        // Project Stopping before Send: in-process debugger transport can deliver and apply the
        // terminal state reentrantly while the transport callback is still on this stack.
        snapshot = snapshot with { State = CaptureState.Stopping };
        SnapshotChanged?.Invoke(snapshot);
        send(StrictWireAdapter.Serialize(new StopMessage(ProtocolVersion.Major, ProtocolVersion.Minor,
            snapshot.RuntimeToken!, snapshot.Generation, checked(snapshot.Sequence + 1), snapshot.Fingerprint!)));
        return true;
    }

    public void Disconnect()
    {
        ResetSupported = false;
        resetRequestId = null;
        resetGeneration = 0;
        ResetCompletedGeneration = 0;
        snapshot = snapshot with { State = CaptureState.Disconnected, RuntimeToken = null, Generation = 0,
            Sequence = 0, Fingerprint = null, LeaseOwner = null, Modes = CaptureModes.None,
            SupportedModes = CaptureModes.None, SamplingIntervalRuntimeConfigurable = false,
            EffectiveSamplingIntervalNanoseconds = 0, CapabilityMaxMethods = 0 };
        pending.Clear();
        SnapshotChanged?.Invoke(snapshot);
    }

        private bool AcceptHello(HelloMessage message)
    {
        if (message.Major != ProtocolVersion.Major) return false;

        if (string.Equals(snapshot.RuntimeToken, message.RuntimeToken, StringComparison.Ordinal))
        {
            if (snapshot.State == CaptureState.Error)
                snapshot = snapshot with { State = CaptureState.Ready, Fingerprint = null,
                    LeaseOwner = null, Modes = CaptureModes.None };
            return snapshot.State is not (CaptureState.Disconnected or CaptureState.Negotiating);
        }

        // Debugger session ids may be reused across game reruns. A new token starts a fresh negotiation.
        // Completed editor-owned results and timeline remain visible until the next capture starts.
        snapshot = snapshot with { State = CaptureState.Ready, RuntimeToken = message.RuntimeToken,
            Generation = 0, Sequence = 0, Fingerprint = null, LeaseOwner = null, Modes = CaptureModes.None,
            SupportedModes = CaptureModes.None, SamplingIntervalRuntimeConfigurable = false,
            EffectiveSamplingIntervalNanoseconds = 0, CapabilityMaxMethods = 0 };
        pending.Clear();
        resetRequestId = null;
        resetGeneration = 0;
        ResetCompletedGeneration = 0;
        return true;
    }

        private bool AcceptCapabilities(CapabilitiesMessage message)
    {
        if (!MatchesRuntime(message) || message.Generation < snapshot.Generation) return false;
        if (snapshot.State != CaptureState.Ready)
            return message.Modes == snapshot.SupportedModes &&
                   message.SamplingIntervalRuntimeConfigurable == snapshot.SamplingIntervalRuntimeConfigurable &&
                   message.EffectiveSamplingIntervalNanoseconds == snapshot.EffectiveSamplingIntervalNanoseconds &&
                   message.MaxMethods == snapshot.CapabilityMaxMethods;
        snapshot = snapshot with { Generation = message.Generation, SupportedModes = message.Modes,
            SamplingIntervalRuntimeConfigurable = message.SamplingIntervalRuntimeConfigurable,
            EffectiveSamplingIntervalNanoseconds = message.EffectiveSamplingIntervalNanoseconds,
            CapabilityMaxMethods = message.MaxMethods };
        return true;
    }

        private bool AcceptBatch(BatchMessage message)
    {
        if (snapshot.State is not (CaptureState.Capturing or CaptureState.Stopping) || !MatchesCapture(message) ||
            message.Sequence != snapshot.Sequence + 1 || (snapshot.Modes & ModeFor(message.Source)) == 0) return false;
        QualityCounters quality;
        try { quality = snapshot.Quality.Add(message.Quality); }
        catch (OverflowException) { return false; }
        pending.TryGetValue(message.Source, out var existing);
        var methods = existing is null
            ? new Dictionary<long, Aggregate>()
            : existing.ToDictionary(pair => pair.Key, pair => pair.Value);
        try
        {
            foreach (var sample in message.Methods)
            {
                if (!methods.ContainsKey(sample.MethodId) && methods.Count >= configuredMaxMethods) return false;
                methods.TryGetValue(sample.MethodId, out var current);
                if (current.Label is not null && !string.Equals(current.Label, sample.Label, StringComparison.Ordinal))
                    return false;
                var maximum = sample.Calls == 0 ? 0 : sample.Value / (double)sample.Calls;
                methods[sample.MethodId] = new Aggregate(sample.Label, checked(current.Value + sample.Value),
                    checked(current.Calls + sample.Calls), Math.Max(current.Maximum, maximum));
            }
        }
        catch (OverflowException) { return false; }
        long batchValue;
        long observations;
        try
        {
            batchValue = message.Methods.Aggregate(0L, (sum, item) => checked(sum + item.Value));
            observations = message.Source == CaptureSource.Sampling
                ? batchValue
                : message.Methods.Aggregate(0L, (sum, item) => checked(sum + item.Calls));
        }
        catch (OverflowException) { return false; }
        pending[message.Source] = methods;
        timeline.Add(new CaptureTimelinePoint(message.Sequence, message.Source, batchValue, observations,
            BuildRows(message.Source, message.Methods)));
        if (timeline.Count > CaptureTimeline.MaximumPoints)
            timeline.RemoveRange(0, timeline.Count - CaptureTimeline.MaximumPoints);
        snapshot = snapshot with { Sequence = message.Sequence, Source = message.Source, Quality = quality };
        TimelineChanged?.Invoke(Timeline);
        return true;
    }

    private bool AcceptState(StateMessage message)
    {
        if (!MatchesCapture(message) || message.Sequence != snapshot.Sequence + 1) return false;
        var allowed = (snapshot.State, message.State) switch
        {
            (CaptureState.Starting, CaptureState.Capturing) => true,
            (CaptureState.Capturing, CaptureState.Capturing) => true,
            (CaptureState.Stopping, CaptureState.Complete or CaptureState.Partial) => true,
            (_, CaptureState.Error) when snapshot.State is CaptureState.Starting or CaptureState.Capturing or CaptureState.Stopping => true,
            _ => false
        };
        if (!allowed) return false;
        var quality = MergeTerminalQuality(snapshot.Quality, message.Quality);
        snapshot = snapshot with { State = message.State, Sequence = message.Sequence, Source = message.Source,
            Completeness = message.Completeness, PartialReason = message.PartialReason, Quality = quality,
            LeaseOwner = message.State is CaptureState.Complete or CaptureState.Partial or CaptureState.Error ? null : snapshot.LeaseOwner };
        if (message.State == CaptureState.Capturing && stopRequestedWhileStarting)
        {
            stopRequestedWhileStarting = false;
            SendStop();
        }
        return true;
    }

    private bool AcceptResetAck(ResetAckMessage message)
    {
        if (resetRequestId is null || !MatchesRuntime(message) ||
            message.Generation != resetGeneration ||
            !string.Equals(message.RequestId, resetRequestId, StringComparison.Ordinal)) return false;
        resetRequestId = null;
        resetGeneration = 0;
        ResetCompletedGeneration = message.Generation;
        stopRequestedWhileStarting = false;
        pending.Clear();
        timeline.Clear();
        snapshot = snapshot with
        {
            State = CaptureState.Ready,
            Generation = message.Generation,
            Sequence = 0,
            Fingerprint = null,
            LeaseOwner = null,
            Modes = CaptureModes.None,
            Completeness = CaptureCompleteness.InProgress,
            PartialReason = PartialReason.None,
            Quality = QualityCounters.Zero
        };
        TimelineChanged?.Invoke(CaptureTimeline.Empty);
        return true;
    }

    private bool AcceptError(ErrorMessage message)
    {
        if (!MatchesRuntime(message) || message.Generation != snapshot.Generation ||
            message.Sequence != snapshot.Sequence + 1) return false;
        if (message.Fatal && snapshot.State == CaptureState.Starting)
        {
            snapshot = snapshot with { Sequence = message.Sequence, State = CaptureState.Ready,
                Fingerprint = null, LeaseOwner = null, Modes = CaptureModes.None };
            stopRequestedWhileStarting = false;
            return true;
        }
        if (snapshot.State is not (CaptureState.Capturing or CaptureState.Stopping)) return false;
        snapshot = snapshot with
        {
            Sequence = message.Sequence,
            State = message.Fatal ? CaptureState.Stopping : CaptureState.Capturing
        };
        return true;
    }

    private bool MatchesRuntime(ProtocolMessage message) => message.Major == ProtocolVersion.Major &&
        message.Minor <= ProtocolVersion.Minor && string.Equals(message.RuntimeToken, snapshot.RuntimeToken, StringComparison.Ordinal);
    private bool MatchesCapture(ProtocolMessage message) => MatchesRuntime(message) && message switch
    {
        BatchMessage value => value.Generation == snapshot.Generation && value.Fingerprint == snapshot.Fingerprint,
        StateMessage value => value.Generation == snapshot.Generation && value.Fingerprint == snapshot.Fingerprint,
        _ => false
    };
    private static CaptureModes ModeFor(CaptureSource source) => source switch
    {
        CaptureSource.Sampling => CaptureModes.Sampling,
        CaptureSource.AutomaticSpans => CaptureModes.AutomaticInstrumentation,
        CaptureSource.ManualSpans => CaptureModes.ManualScopes,
        _ => CaptureModes.None
    };

    private bool Reject(string status) { Rejected?.Invoke(status); return false; }

    private static bool ValidConfigurationText(string value, int maximum) => value is not null &&
        value.Length <= maximum && !value.Any(char.IsControl);

    private static bool ValidRequestId(string value) => value is not null &&
        value.Length == ProtocolLimits.FingerprintCharacters && value.All(Uri.IsHexDigit);

        private static IReadOnlyList<ResultRow> BuildRows(CaptureSource source, IReadOnlyList<MethodSample> methods)
    {
        var total = source == CaptureSource.Sampling ? methods.Sum(value => value.Value) : 0;
        return methods.OrderByDescending(value => value.Value).ThenBy(value => value.MethodId).Select(value =>
            source == CaptureSource.Sampling
                ? new ResultRow(value.Label, value.Value, total == 0 ? 0 : value.Value * 100.0 / total,
                    0, 0, 0, 0)
                : new ResultRow(value.Label, 0, 0, value.Calls, value.Value / 1_000_000.0,
                    value.Calls == 0 ? 0 : value.Value / 1_000_000.0 / value.Calls,
                    value.Calls == 0 ? 0 : value.Value / 1_000_000.0 / value.Calls)).ToArray();
    }

    private void CommitResults()
    {
        var groups = new List<SourceResultGroup>();
        foreach (var source in pending.Keys.OrderBy(value => value))
        {
            var methods = pending[source];
            var total = source == CaptureSource.Sampling ? methods.Values.Sum(value => value.Value) : 0;
            var rows = methods.OrderByDescending(pair => pair.Value.Value).ThenBy(pair => pair.Key).Select(pair =>
            {
                var value = pair.Value;
                return source == CaptureSource.Sampling
                    ? new ResultRow(value.Label!, value.Value, total == 0 ? 0 : value.Value * 100.0 / total, 0, 0, 0, 0)
                    : new ResultRow(value.Label!, 0, 0, value.Calls, value.Value / 1_000_000.0,
                        value.Calls == 0 ? 0 : value.Value / 1_000_000.0 / value.Calls, value.Maximum / 1_000_000.0);
            }).ToArray();
            groups.Add(new SourceResultGroup(source, rows));
        }
        var quality = snapshot.Quality;
        CompletedResults = new ProfilerResults(groups, checked(quality.Dropped + quality.Overflowed));
        CompletedResultsChanged?.Invoke(CompletedResults);
        LastTerminalCapture = new ProfilerTerminalCapture(CompletedResults, Timeline,
            snapshot.Completeness, snapshot.PartialReason, snapshot.Quality);
        TerminalCaptureChanged?.Invoke(LastTerminalCapture);
        pending.Clear();
    }

    private static QualityCounters MergeTerminalQuality(QualityCounters accumulated, QualityCounters terminal) => new(
        Math.Max(accumulated.Observed, terminal.Observed), Math.Max(accumulated.Dropped, terminal.Dropped),
        Math.Max(accumulated.Overflowed, terminal.Overflowed), Math.Max(accumulated.Invalid, terminal.Invalid));

    private readonly record struct Aggregate(string? Label, long Value, long Calls, double Maximum);
}
