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
    private string? _resetReceiptOwner;
    private long _resetReceiptGeneration;
    private string? _resetReceiptRequestId;
    private Task<RuntimeCaptureStopResult>? _pendingStopTask;
    private PendingStop? _pendingStop;
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
    public bool Capturing => _owner is not null && (_pendingStop is not null || _backend.IsActive);
    public long Generation => _generation;
    public long Sequence => _sequence;
    public string? LeaseOwner => _owner;
    public event Action<long, long>? BatchEmitted;

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
        Send(new CapabilitiesMessage(ProtocolVersion.Major, ProtocolVersion.Minor, _runtimeToken, _generation, c.Modes,
            c.SamplingIntervalRuntimeConfigurable, c.EffectiveSamplingIntervalNanoseconds,
            c.MaxMethods, ProtocolLimits.MaxBatchBytes, ProtocolLimits.MaxDepth));
    }

    /// <summary>Processes exactly one untrusted command. Any malformed/stale/gap/duplicate command is inert.</summary>
    public bool Receive(object? payload, string owner)
    {
        ThrowIfDisposed();
        var completedReset = _pendingStop is { IsReset: true } completingReset &&
            _pendingStopTask?.IsCompletedSuccessfully == true && !_backend.IsActive
            ? completingReset : null;
        PollPendingStop();
        if (!_connected || string.IsNullOrWhiteSpace(owner) ||
            !StrictWireAdapter.TryConvert(payload, out var wire) || wire is null ||
            !_parser.TryParse(wire, out var message, out _) || message is null ||
            !string.Equals(message.RuntimeToken, _runtimeToken, StringComparison.Ordinal)) return false;
        if (completedReset is not null && message is ResetMessage completedMessage &&
            completedReset.Generation == completedMessage.Generation &&
            string.Equals(completedReset.Owner, owner, StringComparison.Ordinal) &&
            string.Equals(completedReset.RequestId, completedMessage.RequestId, StringComparison.Ordinal))
            return true;
        return message switch
        {
            ConfigureMessage configure => Configure(configure),
            StartMessage start => Start(start, owner),
            StopMessage stop => Stop(stop, owner),
            ResetMessage reset => Reset(reset, owner),
            _ => false
        };
    }

    public void Flush()
    {
        ThrowIfDisposed();
        PollPendingStop();
        if (!_connected || _pendingStop is not null || _owner is null || !_backend.IsActive || _configuration is null) return;
        try { EmitBatches(_backend.Drain()); }
        catch (Exception exception)
        {
            SendError(3, exception.Message, fatal: true);
            BeginStop(new PendingStop(true, PartialReason.RuntimeError,
                _owner!, _generation, null));
        }
    }

    public void Disconnect()
    {
        if (_disposed || !_connected) return;
        _connected = false;
        ClearResetReceipt();
        if (_owner is not null)
        {
            if (_pendingStop is null)
                BeginStop(new PendingStop(false, PartialReason.Disconnected,
                    _owner, _generation, null));
            return;
        }
        ClearInactiveState();
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
        ClearResetReceipt();
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
            message.Generation != _generation ||
            !string.Equals(message.Fingerprint, _configuration.Fingerprint, StringComparison.Ordinal)) return false;
        // Stop is lag-tolerant: batches emitted after the editor sent the command make its sequence
        // view stale, so any sequence in [2, _sequence + 1] is a valid stop for this capture (the
        // editor has at least seen the Capturing state, sequence 1). Sequences ahead of the stream
        // are still rejected, and Stop does not consume a slot; final batches continue the stream.
        if (message.Sequence < 2 || message.Sequence > _sequence + 1) return false;
        return BeginStop(new PendingStop(true, PartialReason.None, owner, _generation, null));
    }

    private bool Reset(ResetMessage message, string owner)
    {
        if (_resetReceiptRequestId is not null)
        {
            if (_resetReceiptGeneration != message.Generation ||
                !string.Equals(_resetReceiptOwner, owner, StringComparison.Ordinal) ||
                !string.Equals(_resetReceiptRequestId, message.RequestId, StringComparison.Ordinal)) return false;
            SendResetAck(message.Generation, message.RequestId);
            return true;
        }
        if (_pendingStop is { } pending)
        {
            var exact = pending.IsReset && pending.Generation == message.Generation &&
                string.Equals(pending.Owner, owner, StringComparison.Ordinal) &&
                string.Equals(pending.RequestId, message.RequestId, StringComparison.Ordinal);
            if (!exact || _pendingStopTask is not null) return exact;
            return BeginStop(pending, resume: true);
        }
        if (_owner is null || !string.Equals(_owner, owner, StringComparison.Ordinal) ||
            message.Generation != _generation) return false;
        return BeginStop(new PendingStop(false, PartialReason.None, owner,
            message.Generation, message.RequestId));
    }

    private bool BeginStop(PendingStop pending, bool resume = false)
    {
        if (_pendingStop is not null && !resume) return false;
        _pendingStop = pending;
        try { _pendingStopTask = _backend.StopAsync(); }
        catch (Exception exception)
        {
            if (_backend.IsActive)
            {
                _pendingStopTask = null;
                if (!pending.IsReset) _pendingStop = null;
                if (pending.SendTerminal) SendError(2, exception.Message, fatal: true);
                return false;
            }
            if (pending.SendTerminal) SendError(2, exception.Message, fatal: true);
            _pendingStopTask = Task.FromResult(new RuntimeCaptureStopResult(
                Array.Empty<RuntimeSourceBatch>(), true));
        }
        return PollPendingStop() ?? true;
    }

    private bool? PollPendingStop()
    {
        var pending = _pendingStop;
        var task = _pendingStopTask;
        if (pending is null || task is null || !task.IsCompleted) return null;
        _pendingStop = null;
        _pendingStopTask = null;

        RuntimeCaptureStopResult result;
        try { result = task.GetAwaiter().GetResult(); }
        catch (Exception exception)
        {
            if (_backend.IsActive)
            {
                if (pending.IsReset) _pendingStop = pending;
                if (pending.SendTerminal) SendError(2, exception.Message, fatal: true);
                return false;
            }
            if (pending.SendTerminal) SendError(2, exception.Message, fatal: true);
            result = new RuntimeCaptureStopResult(Array.Empty<RuntimeSourceBatch>(), true);
        }
        if (_backend.IsActive)
        {
            if (pending.IsReset) _pendingStop = pending;
            if (pending.SendTerminal) SendError(2, "Capture backend remained active after stop.", fatal: true);
            return false;
        }

        if (pending.IsReset)
        {
            _configuration = null;
            _sequence = 0;
            _quality = QualityCounters.Zero;
            _resetReceiptOwner = pending.Owner;
            _resetReceiptGeneration = pending.Generation;
            _resetReceiptRequestId = pending.RequestId;
            _owner = null;
            SendResetAck(pending.Generation, pending.RequestId!);
            return true;
        }

        if (pending.SendTerminal)
        {
            EmitBatches(result.Batches);
            var reason = result.DataIncomplete && pending.Reason == PartialReason.None
                ? PartialReason.RuntimeError : pending.Reason;
            var completeness = reason == PartialReason.None
                ? CaptureCompleteness.Complete : CaptureCompleteness.Partial;
            var state = reason == PartialReason.None ? CaptureState.Complete : CaptureState.Partial;
            SendState(state, completeness, reason, SourceForState(), _quality);
        }
        _owner = null;
        if (!_connected) ClearInactiveState();
        return true;
    }

    private void SendResetAck(long generation, string requestId) =>
        Send(new ResetAckMessage(ProtocolVersion.Major, ProtocolVersion.Minor, _runtimeToken,
            generation, requestId));

    private void ClearResetReceipt()
    {
        _resetReceiptOwner = null;
        _resetReceiptGeneration = 0;
        _resetReceiptRequestId = null;
    }

    private void ClearInactiveState()
    {
        if (_owner is not null || _backend.IsActive) return;
        _configuration = null;
        _generation = _sequence = 0;
        _quality = QualityCounters.Zero;
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
                BatchEmitted?.Invoke(_generation, _sequence);
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

    private sealed record PendingStop(bool SendTerminal, PartialReason Reason, string Owner,
        long Generation, string? RequestId)
    {
        public bool IsReset => RequestId is not null;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
