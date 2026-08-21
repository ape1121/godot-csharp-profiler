#nullable enable
namespace Apeworks.GodotCSharpProfiler.Protocol;

public readonly record struct CaptureSnapshot(CaptureState State, string? RuntimeToken, long Generation,
    long Sequence, string? Fingerprint, string? LeaseOwner, CaptureModes Modes,
    CaptureSource Source, CaptureCompleteness Completeness, PartialReason PartialReason,
    QualityCounters Quality, CaptureModes SupportedModes, bool SamplingIntervalRuntimeConfigurable,
    long EffectiveSamplingIntervalNanoseconds, int CapabilityMaxMethods);

/// <summary>Deterministic capture lifecycle. Rejected input leaves its snapshot unchanged.</summary>
public sealed class CaptureStateMachine
{
    public CaptureState State { get; private set; } = CaptureState.Disconnected;
    public string? RuntimeToken { get; private set; }
    public long Generation { get; private set; }
    public long Sequence { get; private set; }
    public string? Fingerprint { get; private set; }
    public string? LeaseOwner { get; private set; }
    public CaptureModes Modes { get; private set; }
    public CaptureSource Source { get; private set; }
    public CaptureCompleteness Completeness { get; private set; }
    public PartialReason PartialReason { get; private set; }
    public QualityCounters Quality { get; private set; }
    public CaptureModes SupportedModes { get; private set; }
    public bool SamplingIntervalRuntimeConfigurable { get; private set; }
    public long EffectiveSamplingIntervalNanoseconds { get; private set; }
    public int CapabilityMaxMethods { get; private set; }

    public CaptureSnapshot Snapshot => new(State, RuntimeToken, Generation, Sequence, Fingerprint,
        LeaseOwner, Modes, Source, Completeness, PartialReason, Quality, SupportedModes,
        SamplingIntervalRuntimeConfigurable, EffectiveSamplingIntervalNanoseconds, CapabilityMaxMethods);

    public bool Connect()
    {
        if (State != CaptureState.Disconnected) return false;
        State = CaptureState.Negotiating;
        return true;
    }

    public void Disconnect()
    {
        State = CaptureState.Disconnected;
        RuntimeToken = null;
        Generation = 0;
        Sequence = 0;
        Fingerprint = null;
        LeaseOwner = null;
        Modes = CaptureModes.None;
        Source = default;
        Completeness = default;
        PartialReason = default;
        Quality = default;
        SupportedModes = CaptureModes.None;
        SamplingIntervalRuntimeConfigurable = false;
        EffectiveSamplingIntervalNanoseconds = 0;
        CapabilityMaxMethods = 0;
    }

    public bool AcceptHello(HelloMessage message)
    {
        if (State != CaptureState.Negotiating || !Compatible(message) || string.IsNullOrWhiteSpace(message.RuntimeToken))
            return false;
        RuntimeToken = message.RuntimeToken;
        State = CaptureState.Ready;
        return true;
    }

    public bool AcceptCapabilities(CapabilitiesMessage message)
    {
        if (State != CaptureState.Ready || !MatchesRuntime(message) || message.Generation < Generation ||
            !ValidAvailableModes(message.Modes) || message.MaxMethods < 1 ||
            !ValidCapabilityInterval(message)) return false;
        Generation = message.Generation;
        SupportedModes = message.Modes;
        SamplingIntervalRuntimeConfigurable = message.SamplingIntervalRuntimeConfigurable;
        EffectiveSamplingIntervalNanoseconds = message.EffectiveSamplingIntervalNanoseconds;
        CapabilityMaxMethods = message.MaxMethods;
        return true;
    }

    public bool Configure(ConfigureMessage message)
    {
        if (State is not (CaptureState.Ready or CaptureState.Complete or CaptureState.Partial) ||
            !MatchesRuntime(message) || message.Generation <= Generation || SupportedModes == CaptureModes.None ||
            !ValidModes(message.Modes) || (message.Modes & ~SupportedModes) != 0 ||
            message.MaxMethods < 1 || message.MaxMethods > CapabilityMaxMethods ||
            !ValidRequestedInterval(message) || !ValidFingerprint(message.Fingerprint)) return false;
        Generation = message.Generation;
        Sequence = 0;
        Fingerprint = message.Fingerprint;
        Modes = message.Modes;
        Completeness = CaptureCompleteness.InProgress;
        PartialReason = PartialReason.None;
        Quality = QualityCounters.Zero;
        State = CaptureState.Ready;
        return true;
    }

    public bool Start(StartMessage message, string owner)
    {
        if (LeaseOwner is not null && !string.Equals(LeaseOwner, owner, StringComparison.Ordinal))
        {
            State = CaptureState.Busy;
            return false;
        }
        if (State != CaptureState.Ready || string.IsNullOrWhiteSpace(owner) || !MatchesCapture(message)) return false;
        LeaseOwner = owner;
        State = CaptureState.Starting;
        return true;
    }

    public bool Stop(StopMessage message, string owner)
    {
        if (!string.Equals(LeaseOwner, owner, StringComparison.Ordinal))
        {
            if (LeaseOwner is not null) State = CaptureState.Busy;
            return false;
        }
        // Stop is sequence-tolerant (generation/fingerprint/owner scoped) and does not consume a
        // sequence slot, so in-flight batches stay contiguous for the receiving editor.
        if (State != CaptureState.Capturing || !MatchesCapture(message))
            return false;
        State = CaptureState.Stopping;
        return true;
    }

    public bool AcceptBatch(BatchMessage message)
    {
        if (State != CaptureState.Capturing || !MatchesCapture(message) || message.Sequence != Sequence + 1 ||
            !ValidSource(message.Source, message.ExactCalls, message.CpuTime)) return false;
        QualityCounters next;
        try { next = Quality.Add(message.Quality); }
        catch (OverflowException) { return false; }
        Sequence = message.Sequence;
        Source = message.Source;
        Quality = next;
        return true;
    }

    public bool AcceptState(StateMessage message)
    {
        if (State is not (CaptureState.Starting or CaptureState.Capturing or CaptureState.Stopping) ||
            !MatchesCapture(message) || message.Sequence != Sequence + 1 || !AllowedRemoteTransition(State, message.State) ||
            !ValidCompletion(message.State, message.Completeness, message.PartialReason)) return false;
        Sequence = message.Sequence;
        State = message.State;
        Source = message.Source;
        Completeness = message.Completeness;
        PartialReason = message.PartialReason;
        Quality = new QualityCounters(Math.Max(Quality.Observed, message.Quality.Observed),
            Math.Max(Quality.Dropped, message.Quality.Dropped),
            Math.Max(Quality.Overflowed, message.Quality.Overflowed),
            Math.Max(Quality.Invalid, message.Quality.Invalid));
        if (State is CaptureState.Complete or CaptureState.Partial or CaptureState.Error) LeaseOwner = null;
        return true;
    }

    public bool AcceptError(ErrorMessage message)
    {
        if (!MatchesCapture(message) || message.Sequence != Sequence + 1) return false;
        Sequence = message.Sequence;
        if (message.Fatal)
        {
            State = CaptureState.Error;
            LeaseOwner = null;
        }
        return true;
    }

    private bool MatchesCapture(ProtocolMessage message) => MatchesRuntime(message) && message switch
    {
        StartMessage start => start.Generation == Generation && start.Fingerprint == Fingerprint,
        StopMessage stop => stop.Generation == Generation && stop.Fingerprint == Fingerprint,
        BatchMessage batch => batch.Generation == Generation && batch.Fingerprint == Fingerprint,
        StateMessage state => state.Generation == Generation && state.Fingerprint == Fingerprint,
        ErrorMessage error => error.Generation == Generation,
        _ => false
    };

    private bool MatchesRuntime(ProtocolMessage message) => Compatible(message) &&
        string.Equals(message.RuntimeToken, RuntimeToken, StringComparison.Ordinal);

    private static bool Compatible(ProtocolMessage message) => message.Major == ProtocolVersion.Major &&
        message.Minor >= 0 && message.Minor <= ProtocolVersion.Minor;

    private static bool ValidAvailableModes(CaptureModes modes) =>
        modes != CaptureModes.None && (modes & ~((CaptureModes)7)) == 0;

    private static bool ValidModes(CaptureModes modes) =>
        ValidAvailableModes(modes) &&
        !((modes & CaptureModes.Sampling) != 0 && (modes & CaptureModes.AutomaticInstrumentation) != 0);

    private static bool ValidFingerprint(string value) => value is not null &&
        value.Length == ProtocolLimits.FingerprintCharacters && value.All(Uri.IsHexDigit);

    private static bool ValidCapabilityInterval(CapabilitiesMessage message)
    {
        var sampling = (message.Modes & CaptureModes.Sampling) != 0;
        var interval = message.EffectiveSamplingIntervalNanoseconds;
        return sampling
            ? interval == 0 || interval is >= ProtocolLimits.MinSamplingIntervalNanoseconds
                and <= ProtocolLimits.MaxSamplingIntervalNanoseconds
            : !message.SamplingIntervalRuntimeConfigurable && interval == 0;
    }

    private bool ValidRequestedInterval(ConfigureMessage message)
    {
        var interval = message.RequestedSamplingIntervalNanoseconds;
        if ((message.Modes & CaptureModes.Sampling) == 0) return interval == 0;
        if (interval == 0) return true;
        return SamplingIntervalRuntimeConfigurable &&
            interval is >= ProtocolLimits.MinSamplingIntervalNanoseconds
                and <= ProtocolLimits.MaxSamplingIntervalNanoseconds;
    }

    private static bool ValidSource(CaptureSource source, bool exactCalls, bool cpuTime) => source switch
    {
        CaptureSource.Sampling => !exactCalls && !cpuTime,
        CaptureSource.AutomaticSpans or CaptureSource.ManualSpans => exactCalls && !cpuTime,
        _ => false
    };

    private static bool AllowedRemoteTransition(CaptureState current, CaptureState next) => (current, next) switch
    {
        (CaptureState.Starting, CaptureState.Capturing) => true,
        (CaptureState.Starting, CaptureState.Error) => true,
        (CaptureState.Capturing, CaptureState.Capturing) => true,
        (CaptureState.Capturing, CaptureState.Error) => true,
        (CaptureState.Stopping, CaptureState.Complete) => true,
        (CaptureState.Stopping, CaptureState.Partial) => true,
        (CaptureState.Stopping, CaptureState.Error) => true,
        _ => false
    };

    private static bool ValidCompletion(CaptureState state, CaptureCompleteness completeness, PartialReason reason) =>
        state switch
        {
            CaptureState.Complete => completeness == CaptureCompleteness.Complete && reason == PartialReason.None,
            CaptureState.Partial => completeness == CaptureCompleteness.Partial && reason != PartialReason.None,
            CaptureState.Capturing => completeness == CaptureCompleteness.InProgress && reason == PartialReason.None,
            CaptureState.Error => completeness != CaptureCompleteness.Complete,
            _ => false
        };
}
