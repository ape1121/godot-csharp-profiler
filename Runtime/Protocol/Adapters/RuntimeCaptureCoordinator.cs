#nullable enable
using Apeworks.GodotCSharpProfiler.Protocol;

namespace Apeworks.GodotCSharpProfiler.Runtime.Protocol.Adapters;

/// <summary>Owns one protocol-v1 runtime lease and orchestrates a backend without transport assumptions.</summary>
public sealed class RuntimeCaptureCoordinator : IDisposable
{
    private readonly string _runtimeToken;
    private readonly IRuntimeCaptureTransport _transport;
    private readonly IRuntimeCaptureBackend _backend;
    private readonly CaptureProtocolParser _parser = new();
    private RuntimeCaptureConfiguration? _configuration;
    private string? _owner;
    private long _generation;
    private long _sequence;
    private QualityCounters _quality;
    private bool _connected;
    private bool _disposed;

    public RuntimeCaptureCoordinator(string runtimeToken, IRuntimeCaptureTransport transport, IRuntimeCaptureBackend backend)
    {
        if (string.IsNullOrWhiteSpace(runtimeToken) || runtimeToken.Length > ProtocolLimits.MaxTokenCharacters)
            throw new ArgumentException("Runtime token is invalid.", nameof(runtimeToken));
        _runtimeToken = runtimeToken;
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        ValidateCapabilities(backend.Capabilities);
    }

    public bool Connected => _connected;
    public bool Capturing => _owner is not null && _backend.IsActive;
    public long Generation => _generation;
    public long Sequence => _sequence;
    public string? LeaseOwner => _owner;

    public void Connect()
    {
        ThrowIfDisposed();
        if (_connected) return;
        _connected = true;
        Announce();
    }

    public void Announce()
    {
        ThrowIfDisposed();
        if (!_connected) return;
        Send(new HelloMessage(ProtocolVersion.Major, ProtocolVersion.Minor, _runtimeToken, "runtime", ProtocolLimits.MaxBatchBytes));
        var c = _backend.Capabilities;
        Send(new CapabilitiesMessage(ProtocolVersion.Major, ProtocolVersion.Minor, _runtimeToken, 0, c.Modes,
            c.SamplingIntervalRuntimeConfigurable, c.EffectiveSamplingIntervalNanoseconds,
            c.MaxMethods, ProtocolLimits.MaxBatchBytes, ProtocolLimits.MaxDepth));
    }

    /// <summary>Processes exactly one untrusted command. Any malformed/stale/gap/duplicate command is inert.</summary>
    public bool Receive(object? payload, string owner)
    {
        ThrowIfDisposed();
        if (!_connected || string.IsNullOrWhiteSpace(owner) ||
            !StrictWireAdapter.TryConvert(payload, out var wire) || wire is null ||
            !_parser.TryParse(wire, out var message, out _) || message is null ||
            !string.Equals(message.RuntimeToken, _runtimeToken, StringComparison.Ordinal)) return false;
        return message switch
        {
            ConfigureMessage configure => Configure(configure),
            StartMessage start => Start(start, owner),
            StopMessage stop => Stop(stop, owner),
            _ => false
        };
    }

    public void Flush()
    {
        ThrowIfDisposed();
        if (!_connected || _owner is null || !_backend.IsActive || _configuration is null) return;
        EmitBatches(_backend.Drain());
    }

    public void Disconnect()
    {
        if (_disposed || !_connected) return;
        if (_owner is not null) StopOwned(sendTerminal: false, PartialReason.Disconnected);
        _connected = false;
        _configuration = null;
        _generation = _sequence = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Disconnect();
        _backend.Dispose();
        _disposed = true;
    }

    private bool Configure(ConfigureMessage message)
    {
        if (_owner is not null || message.Generation <= _generation ||
            (message.Modes & ~_backend.Capabilities.Modes) != 0 || !ValidModes(message.Modes) ||
            message.MaxMethods > _backend.Capabilities.MaxMethods || !ValidInterval(message)) return false;
        _configuration = new RuntimeCaptureConfiguration(message.Generation, message.Fingerprint,
            message.Modes, message.RequestedSamplingIntervalNanoseconds, message.MaxMethods,
            message.SamplingIncludeAssemblies, message.SamplingExcludeAssemblies, message.ManualLabelPrefix);
        _generation = message.Generation;
        _sequence = 0;
        _quality = QualityCounters.Zero;
        return true;
    }

    private bool Start(StartMessage message, string owner)
    {
        if (_configuration is null || message.Generation != _generation ||
            !string.Equals(message.Fingerprint, _configuration.Fingerprint, StringComparison.Ordinal)) return false;
        // Start is accepted exactly once. Retries/duplicates are inert rather than mutating a lease.
        if (_owner is not null) return false;
        if (!_backend.TryStart(_configuration, owner, out var error))
        {
            SendError(1, error ?? "Capture backend rejected start.", fatal: true);
            return false;
        }
        _owner = owner;
        SendState(CaptureState.Capturing, CaptureCompleteness.InProgress, PartialReason.None, SourceForState(), QualityCounters.Zero);
        return true;
    }

    private bool Stop(StopMessage message, string owner)
    {
        if (_configuration is null || _owner is null || !string.Equals(_owner, owner, StringComparison.Ordinal) ||
            message.Generation != _generation || message.Sequence != _sequence + 1 ||
            !string.Equals(message.Fingerprint, _configuration.Fingerprint, StringComparison.Ordinal)) return false;
        // The stop command consumes its sequence; emitted final batches continue after it.
        _sequence = message.Sequence;
        StopOwned(sendTerminal: true, PartialReason.None);
        return true;
    }

    private void StopOwned(bool sendTerminal, PartialReason reason)
    {
        IReadOnlyList<RuntimeSourceBatch> final;
        try { final = _backend.Stop(); }
        catch (Exception exception)
        {
            final = Array.Empty<RuntimeSourceBatch>();
            if (sendTerminal) SendError(2, exception.Message, fatal: true);
        }
        if (sendTerminal)
        {
            EmitBatches(final);
            var completeness = reason == PartialReason.None ? CaptureCompleteness.Complete : CaptureCompleteness.Partial;
            var state = reason == PartialReason.None ? CaptureState.Complete : CaptureState.Partial;
            SendState(state, completeness, reason, SourceForState(), _quality);
        }
        _owner = null;
    }

    private void EmitBatches(IReadOnlyList<RuntimeSourceBatch>? batches)
    {
        if (batches is null || _configuration is null) return;
        foreach (var batch in batches)
        {
            if (!ValidBatch(batch)) continue;
            QualityCounters nextQuality;
            try { nextQuality = _quality.Add(batch.Quality); }
            catch (OverflowException) { continue; }
            var offset = 0;
            var firstChunk = true;
            do
            {
                var remaining = batch.Methods.Count - offset;
                var count = Math.Min(remaining, Math.Min(_configuration.MaxMethods, ProtocolLimits.MaxMethodsPerBatch));
                if (remaining == 0) count = 0;
                while (count > 0 && !Fits(batch, offset, count)) count /= 2;
                if (remaining > 0 && count == 0) break;
                var methods = batch.Methods.Skip(offset).Take(count).ToArray();
                Send(new BatchMessage(ProtocolVersion.Major, ProtocolVersion.Minor, _runtimeToken, _generation, ++_sequence,
                    _configuration.Fingerprint, batch.Source, batch.ExactCalls, batch.CpuTime,
                    firstChunk ? batch.Quality : QualityCounters.Zero, methods));
                firstChunk = false;
                offset += count;
                if (remaining == 0) break;
            } while (offset < batch.Methods.Count);
            if (!firstChunk) _quality = nextQuality;
        }
    }

    private bool Fits(RuntimeSourceBatch batch, int offset, int count)
    {
        var message = new BatchMessage(ProtocolVersion.Major, ProtocolVersion.Minor, _runtimeToken, _generation, _sequence + 1,
            _configuration!.Fingerprint, batch.Source, batch.ExactCalls, batch.CpuTime,
            QualityCounters.Zero, batch.Methods.Skip(offset).Take(count).ToArray());
        return StrictWireAdapter.MeasureBytes(StrictWireAdapter.Serialize(message)) <= ProtocolLimits.MaxBatchBytes;
    }

    private bool ValidBatch(RuntimeSourceBatch batch)
    {
        if ((_configuration!.Modes & ModeFor(batch.Source)) == 0 || batch.Methods is null ||
            batch.Quality.Observed < 0 || batch.Quality.Dropped < 0 || batch.Quality.Overflowed < 0 || batch.Quality.Invalid < 0)
            return false;
        if (batch.Source == CaptureSource.Sampling ? batch.ExactCalls || batch.CpuTime : !batch.ExactCalls || batch.CpuTime)
            return false;
        return batch.Methods.All(method => method.MethodId >= 0 && method.Value >= 0 && method.Calls >= 0 &&
            !string.IsNullOrEmpty(method.Label) && method.Label.Length <= ProtocolLimits.MaxMethodLabelCharacters &&
            !method.Label.Any(char.IsControl));
    }

    private void SendState(CaptureState state, CaptureCompleteness completeness, PartialReason reason,
        CaptureSource source, QualityCounters quality) => Send(new StateMessage(ProtocolVersion.Major, ProtocolVersion.Minor, _runtimeToken,
        _generation, ++_sequence, _configuration!.Fingerprint, state, source, completeness, reason, quality));

    private void SendError(int code, string message, bool fatal) => Send(new ErrorMessage(ProtocolVersion.Major, ProtocolVersion.Minor, _runtimeToken,
        _generation, ++_sequence, code, BoundError(message), fatal));

    private void Send(ProtocolMessage message) => _transport.Send(StrictWireAdapter.Serialize(message));

    private CaptureSource SourceForState() => (_configuration!.Modes & CaptureModes.Sampling) != 0
        ? CaptureSource.Sampling
        : (_configuration.Modes & CaptureModes.AutomaticInstrumentation) != 0
            ? CaptureSource.AutomaticSpans : CaptureSource.ManualSpans;

    private bool ValidInterval(ConfigureMessage message)
    {
        if ((message.Modes & CaptureModes.Sampling) == 0) return message.RequestedSamplingIntervalNanoseconds == 0;
        if (message.RequestedSamplingIntervalNanoseconds == 0) return true;
        return _backend.Capabilities.SamplingIntervalRuntimeConfigurable;
    }

    private static bool ValidModes(CaptureModes modes) => modes != CaptureModes.None &&
        (modes & ~(CaptureModes.Sampling | CaptureModes.AutomaticInstrumentation | CaptureModes.ManualScopes)) == 0 &&
        !modes.HasFlag(CaptureModes.Sampling | CaptureModes.AutomaticInstrumentation);

    private static CaptureModes ModeFor(CaptureSource source) => source switch
    {
        CaptureSource.Sampling => CaptureModes.Sampling,
        CaptureSource.AutomaticSpans => CaptureModes.AutomaticInstrumentation,
        CaptureSource.ManualSpans => CaptureModes.ManualScopes,
        _ => CaptureModes.None
    };

    private static void ValidateCapabilities(RuntimeBackendCapabilities value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Modes == CaptureModes.None || value.MaxMethods < 1 || value.MaxMethods > ProtocolLimits.MaxConfiguredMethods ||
            (value.Modes & CaptureModes.Sampling) == 0 && (value.SamplingIntervalRuntimeConfigurable || value.EffectiveSamplingIntervalNanoseconds != 0))
            throw new ArgumentException("Backend capabilities are invalid.");
    }

    private static string BoundError(string value)
    {
        value = string.IsNullOrWhiteSpace(value) ? "Runtime capture error." : new string(value.Where(c => !char.IsControl(c)).ToArray());
        return value[..Math.Min(value.Length, ProtocolLimits.MaxErrorCharacters)];
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
