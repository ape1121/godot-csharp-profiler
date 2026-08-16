#nullable enable
using Apeworks.GodotCSharpProfiler.Editor.Modes;
using Apeworks.GodotCSharpProfiler.Protocol;
using Apeworks.GodotCSharpProfiler.Runtime.Protocol.Adapters;

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

public sealed class EditorCaptureCoordinator
{
    private readonly string owner;
    private readonly Action<WireMap> send;
    private readonly CaptureProtocolParser parser = new();
    private readonly Dictionary<CaptureSource, Dictionary<long, Aggregate>> pending = new();
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
    public ProfilerResults CompletedResults { get; private set; } = ProfilerResults.Empty;
    public event Action<CaptureSnapshot>? SnapshotChanged;
    public event Action<ProfilerResults>? CompletedResultsChanged;
    public event Action<string>? Rejected;

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
            ErrorMessage value => AcceptError(value),
            _ => false
        };
        if (!accepted) return Reject("Profiler protocol packet rejected (stale, duplicate, gap, or incompatible identity).");
        if (message is ErrorMessage error) Rejected?.Invoke(ProfilerDockController.SafeText(error.Message, 160, "Runtime capture error"));
        if (message is StateMessage terminal && terminal.State is CaptureState.Complete or CaptureState.Partial) CommitResults();
        SnapshotChanged?.Invoke(snapshot);
        return true;
    }

    public bool Start(ModeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (snapshot.State is not (CaptureState.Ready or CaptureState.Complete or CaptureState.Partial) ||
            snapshot.RuntimeToken is null || snapshot.SupportedModes == CaptureModes.None) return false;
        var normalized = configuration.Normalize();
        var generation = checked(snapshot.Generation + 1);
        var interval = (normalized.Modes & CaptureModes.Sampling) != 0 ? normalized.Sampling.RequestedIntervalNanoseconds : 0;
        var maxMethods = Math.Min(normalized.Automatic.MaxMethods, snapshot.CapabilityMaxMethods);
        if (normalized.Modes == CaptureModes.None || (normalized.Modes & ~snapshot.SupportedModes) != 0 ||
            maxMethods < 1 || (interval != 0 && !snapshot.SamplingIntervalRuntimeConfigurable)) return false;
        snapshot = snapshot with { Generation = generation, Sequence = 0, Fingerprint = normalized.Fingerprint,
            LeaseOwner = owner, Modes = normalized.Modes, State = CaptureState.Starting,
            Completeness = CaptureCompleteness.InProgress, PartialReason = PartialReason.None, Quality = QualityCounters.Zero };
        pending.Clear();
        send(StrictWireAdapter.Serialize(new ConfigureMessage(ProtocolVersion.Major, ProtocolVersion.Minor,
            snapshot.RuntimeToken, generation, normalized.Fingerprint, normalized.Modes, interval, maxMethods)));
        send(StrictWireAdapter.Serialize(new StartMessage(ProtocolVersion.Major, ProtocolVersion.Minor,
            snapshot.RuntimeToken, generation, normalized.Fingerprint)));
        SnapshotChanged?.Invoke(snapshot);
        return true;
    }

    public bool Stop()
    {
        if (snapshot.State is not (CaptureState.Starting or CaptureState.Capturing) ||
            snapshot.RuntimeToken is null || snapshot.Fingerprint is null || snapshot.LeaseOwner != owner) return false;
        var sequence = checked(snapshot.Sequence + 1);
        send(StrictWireAdapter.Serialize(new StopMessage(ProtocolVersion.Major, ProtocolVersion.Minor,
            snapshot.RuntimeToken, snapshot.Generation, sequence, snapshot.Fingerprint)));
        snapshot = snapshot with { State = CaptureState.Stopping, Sequence = sequence };
        SnapshotChanged?.Invoke(snapshot);
        return true;
    }

    public void Disconnect()
    {
        snapshot = snapshot with { State = CaptureState.Disconnected, RuntimeToken = null, Generation = 0,
            Sequence = 0, Fingerprint = null, LeaseOwner = null, Modes = CaptureModes.None,
            SupportedModes = CaptureModes.None, SamplingIntervalRuntimeConfigurable = false,
            EffectiveSamplingIntervalNanoseconds = 0, CapabilityMaxMethods = 0 };
        pending.Clear();
        SnapshotChanged?.Invoke(snapshot);
    }

    private bool AcceptHello(HelloMessage message)
    {
        if (snapshot.State != CaptureState.Negotiating || message.Major != ProtocolVersion.Major) return false;
        snapshot = snapshot with { State = CaptureState.Ready, RuntimeToken = message.RuntimeToken };
        return true;
    }

    private bool AcceptCapabilities(CapabilitiesMessage message)
    {
        if (snapshot.State != CaptureState.Ready || !MatchesRuntime(message) || message.Generation != 0) return false;
        snapshot = snapshot with { SupportedModes = message.Modes,
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
        if (!pending.TryGetValue(message.Source, out var methods)) pending[message.Source] = methods = new();
        try
        {
            foreach (var sample in message.Methods)
            {
                methods.TryGetValue(sample.MethodId, out var current);
                var maximum = sample.Calls == 0 ? 0 : sample.Value / (double)sample.Calls;
                methods[sample.MethodId] = new Aggregate(checked(current.Value + sample.Value),
                    checked(current.Calls + sample.Calls), Math.Max(current.Maximum, maximum));
            }
        }
        catch (OverflowException) { return false; }
        snapshot = snapshot with { Sequence = message.Sequence, Source = message.Source, Quality = quality };
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
        snapshot = snapshot with { State = message.State, Sequence = message.Sequence, Source = message.Source,
            Completeness = message.Completeness, PartialReason = message.PartialReason, Quality = message.Quality,
            LeaseOwner = message.State is CaptureState.Complete or CaptureState.Partial or CaptureState.Error ? null : snapshot.LeaseOwner };
        return true;
    }

    private bool AcceptError(ErrorMessage message)
    {
        if (!MatchesRuntime(message) || message.Generation != snapshot.Generation || message.Sequence != snapshot.Sequence + 1) return false;
        snapshot = snapshot with { Sequence = message.Sequence, State = message.Fatal ? CaptureState.Error : snapshot.State,
            LeaseOwner = message.Fatal ? null : snapshot.LeaseOwner };
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
                    ? new ResultRow($"Method {pair.Key}", value.Value, total == 0 ? 0 : value.Value * 100.0 / total, 0, 0, 0, 0)
                    : new ResultRow($"Method {pair.Key}", 0, 0, value.Calls, value.Value / 1_000_000.0,
                        value.Calls == 0 ? 0 : value.Value / 1_000_000.0 / value.Calls, value.Maximum / 1_000_000.0);
            }).ToArray();
            groups.Add(new SourceResultGroup(source, rows));
        }
        var quality = snapshot.Quality;
        CompletedResults = new ProfilerResults(groups, checked(quality.Dropped + quality.Overflowed));
        CompletedResultsChanged?.Invoke(CompletedResults);
        pending.Clear();
    }

    private readonly record struct Aggregate(long Value, long Calls, double Maximum);
}
