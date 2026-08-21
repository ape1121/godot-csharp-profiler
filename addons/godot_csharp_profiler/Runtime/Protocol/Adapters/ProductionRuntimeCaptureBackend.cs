#if !PROTOCOL_TESTS
#nullable enable
using System.Diagnostics;
using System.Reflection;
using Apeworks.GodotCSharpProfiler.Instrumentation;
using Apeworks.GodotCSharpProfiler.Protocol;
using Apeworks.GodotCSharpProfiler.Runtime.Sampling;

namespace Apeworks.GodotCSharpProfiler.Runtime.Protocol.Adapters;

/// <summary>Production adapter over sampling, woven instrumentation, and manual scopes.</summary>
public sealed class ProductionRuntimeCaptureBackend : IRuntimeCaptureBackend
{
    private static ProductionRuntimeCaptureBackend? s_automaticOwner;
    private readonly Func<SamplingOptions, IManagedSamplingLease>? _samplingFactory;
    private readonly Func<string, IManualCaptureLease?> _manualFactory;
    private readonly object _lifecycleGate = new();
    private Task<RuntimeCaptureStopResult>? _stopTask;
    private RuntimeSourceBatch? _pendingSamplingStopBatch;
    private RuntimeSourceBatch? _pendingAutomaticStopBatch;
    private readonly Dictionary<string, long> _manualIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _samplingIds = new(StringComparer.Ordinal);
    private IManagedSamplingLease? _sampler;
    private IManualCaptureLease? _manualLease;
    private RuntimeSourceBatch? _pendingManualStopBatch;
    private bool _pendingStopDataIncomplete;
    private CaptureModes _modes;
    private int _maxMethods;
    private long _nextManualId = 1;
    private long _nextSamplingId = 1;
    private string _manualLabelPrefix = string.Empty;
    private InstrumentationManifest? _manifest;
    private bool _automatic;
    private bool _starting;
    private bool _disposed;

    public ProductionRuntimeCaptureBackend() : this(CreateSamplingFactory(), FindManifest(), null) { }

    internal ProductionRuntimeCaptureBackend(Func<SamplingOptions, IManagedSamplingLease>? samplingFactory,
        InstrumentationManifest? manifest,
        Func<string, IManualCaptureLease?>? manualFactory = null)
    {
        _samplingFactory = samplingFactory;
        _manualFactory = manualFactory ?? AcquireManualLease;
        _manifest = manifest;
        var modes = CaptureModes.ManualScopes;
        if (samplingFactory is not null) modes |= CaptureModes.Sampling;
        if (manifest is not null) modes |= CaptureModes.AutomaticInstrumentation;
        Capabilities = new RuntimeBackendCapabilities(modes, false, ReadEffectiveInterval(),
            Math.Min(ProtocolLimits.MaxConfiguredMethods, InstrumentationRecorder.MaximumMethods),
            samplingFactory is null ? "Sampling unavailable in this build/runtime." : "Fixed process-start interval; effective interval may be unknown.",
            manifest is null ? "No valid instrumentation manifest loaded for this build." : $"Manifest {manifest.ConfigHash} loaded.");
    }

    public RuntimeBackendCapabilities Capabilities { get; }
    public bool IsActive
    {
        get
        {
            lock (_lifecycleGate)
                return HasActiveBackend();
        }
    }

    public bool TryStart(RuntimeCaptureConfiguration configuration, string owner, out string? error)
    {
        lock (_lifecycleGate)
        {
            error = null;
            if (_disposed || _starting || HasActiveBackend() || _stopTask is { IsCompleted: false })
            {
                error = "Capture backend is busy.";
                return false;
            }
            if ((configuration.Modes & ~Capabilities.Modes) != 0)
            {
                error = "Requested backend is unavailable.";
                return false;
            }
            _stopTask = null;
            _modes = configuration.Modes;
            _maxMethods = configuration.MaxMethods;
            _manualIds.Clear();
            _samplingIds.Clear();
            _pendingSamplingStopBatch = null;
            _pendingAutomaticStopBatch = null;
            _pendingManualStopBatch = null;
            _pendingStopDataIncomplete = false;
            _nextManualId = _nextSamplingId = 1;
            _manualLabelPrefix = configuration.ManualLabelPrefix;
            _starting = true;
            try
            {
                if ((_modes & CaptureModes.AutomaticInstrumentation) != 0)
                {
                    if (Interlocked.CompareExchange(ref s_automaticOwner, this, null) is not null)
                        throw new InvalidOperationException("Automatic instrumentation recorder is owned by another capture.");
                    try { InstrumentationRecorder.StartCapture(); _automatic = true; }
                    catch { Interlocked.CompareExchange(ref s_automaticOwner, null, this); throw; }
                    ThrowIfStartInterrupted();
                }
                if ((_modes & CaptureModes.ManualScopes) != 0)
                {
                    _manualLease = _manualFactory(owner);
                    if (_manualLease is null)
                        throw new InvalidOperationException("Manual profiler lease is owned by another capture.");
                    ThrowIfStartInterrupted();
                }
                if ((_modes & CaptureModes.Sampling) != 0)
                {
                    _sampler = _samplingFactory!(new SamplingOptions
                    {
                        MaxUniqueMethods = _maxMethods,
                        IncludeAssemblyPrefixes = SplitList(configuration.SamplingIncludeAssemblies),
                        ExcludeAssemblyPrefixes = SplitList(configuration.SamplingExcludeAssemblies)
                    });
                    ThrowIfStartInterrupted();
                    _sampler.Start();
                    ThrowIfStartInterrupted();
                }
                return true;
            }
            catch (Exception exception)
            {
                error = exception.Message;
                ObserveFault(EnsureStopOperation());
                return false;
            }
            finally { _starting = false; }
        }
    }

    public IReadOnlyList<RuntimeSourceBatch> Drain()
    {
        lock (_lifecycleGate)
        {
            if (_starting || !HasActiveBackend() || _stopTask is not null)
                return Array.Empty<RuntimeSourceBatch>();
            var result = new List<RuntimeSourceBatch>(2);
            if (_sampler is not null)
            {
                if (_sampler.Fault is not null)
                    throw new InvalidOperationException("Managed sampling stopped unexpectedly.", _sampler.Fault);
                var sampling = SamplingBatch(_sampler.Snapshot(reset: true));
                if (HasData(sampling)) result.Add(sampling);
            }
            if (_manualLease?.IsActive == true)
            {
                var manual = ManualBatch(_manualLease.FlushFrame());
                if (HasData(manual)) result.Add(manual);
            }
            return result;
        }
    }

    public Task<RuntimeCaptureStopResult> StopAsync()
    {
        lock (_lifecycleGate)
            return EnsureStopOperation();
    }

    // Publish a stable task before any backend callback can synchronously reenter lifecycle APIs.
    private Task<RuntimeCaptureStopResult> EnsureStopOperation()
    {
        if (_stopTask is { IsCompleted: false } || _stopTask?.IsCompletedSuccessfully == true)
            return _stopTask;
        var completion = new TaskCompletionSource<RuntimeCaptureStopResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _stopTask = completion.Task;
        _ = CompleteStopOperationAsync(completion);
        return _stopTask;
    }

    private async Task CompleteStopOperationAsync(
        TaskCompletionSource<RuntimeCaptureStopResult> completion)
    {
        await Task.Yield();
        try
        {
            completion.TrySetResult(await StopCoreAsync().ConfigureAwait(false));
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task<RuntimeCaptureStopResult> StopCoreAsync()
    {
        IManagedSamplingLease? sampler;
        Task<bool>? samplingStopTask = null;
        Exception? failure = null;
        lock (_lifecycleGate)
        {
            sampler = _sampler;
            // Stop process-global non-sampling sources before invoking the potentially blocking
            // sampling stop so every source begins teardown before EventPipe parser quiescence.
            StopNonSamplingBackends(ref failure);
            if (sampler is not null)
            {
                try { samplingStopTask = sampler.StopAsync(); }
                catch (Exception exception)
                {
                    failure ??= new InvalidOperationException("Managed sampling failed to stop.", exception);
                    _pendingStopDataIncomplete = true;
                }
            }
        }

        var samplingStopped = sampler is null;
        if (samplingStopTask is not null)
        {
            try
            {
                var dataIncomplete = await samplingStopTask.ConfigureAwait(false);
                lock (_lifecycleGate) _pendingStopDataIncomplete |= dataIncomplete;
                samplingStopped = true;
            }
            catch (Exception exception)
            {
                failure ??= new InvalidOperationException("Managed sampling failed to stop.", exception);
                lock (_lifecycleGate) _pendingStopDataIncomplete = true;
            }
        }

        lock (_lifecycleGate)
        {
            if (samplingStopped && sampler is not null && ReferenceEquals(_sampler, sampler))
                FinalizeStoppedSampler(sampler, ref failure);

            if (failure is not null && HasActiveBackend())
                throw new InvalidOperationException("One or more runtime capture backends failed to stop.", failure);

            // Once every source is inactive, teardown failures become a truthful partial result so
            // successfully captured final batches are handed off exactly once instead of discarded.
            if (failure is not null) _pendingStopDataIncomplete = true;
            var completed = new RuntimeCaptureStopResult(PendingStopBatches(), _pendingStopDataIncomplete);
            ClearPendingStopResult();
            _modes = CaptureModes.None;
            return completed;
        }
    }

    public IReadOnlyList<RuntimeSourceBatch> Stop() =>
        StopAsync().GetAwaiter().GetResult().Batches;

    private void StopNonSamplingBackends(ref Exception? failure)
    {
        if (_automatic && ReferenceEquals(Volatile.Read(ref s_automaticOwner), this))
        {
            try
            {
                var automatic = AutomaticBatch(InstrumentationRecorder.StopCapture());
                if (HasData(automatic)) _pendingAutomaticStopBatch = automatic;
            }
            catch (Exception exception)
            {
                failure ??= exception;
                _pendingStopDataIncomplete = true;
            }
            finally
            {
                Interlocked.CompareExchange(ref s_automaticOwner, null, this);
                _automatic = false;
            }
        }
        var manual = _manualLease;
        if (manual is not null)
        {
            try
            {
                if (manual.IsActive)
                {
                    var batch = ManualBatch(manual.FlushFrame());
                    if (HasData(batch))
                        _pendingManualStopBatch = _pendingManualStopBatch is null
                            ? batch
                            : MergeBatches(_pendingManualStopBatch, batch);
                }
            }
            catch (Exception exception)
            {
                failure ??= exception;
                _pendingStopDataIncomplete = true;
            }

            try
            {
                if (manual.IsActive)
                    manual.Stop();
            }
            catch (Exception exception) { failure ??= exception; }

            // A failed final flush does not keep the process recorder active when Stop succeeded,
            // but no failure may discard the only lease while the process-global capture survives.
            if (manual.IsActive)
                failure ??= new InvalidOperationException("Manual profiler remained active after stop.");
            else
                Interlocked.CompareExchange(ref _manualLease, null, manual);
        }
    }

    private void FinalizeStoppedSampler(IManagedSamplingLease sampler, ref Exception? failure)
    {
        try
        {
            var sampling = SamplingBatch(sampler.Snapshot(reset: true));
            if (HasData(sampling)) _pendingSamplingStopBatch = sampling;
        }
        catch (Exception exception)
        {
            failure ??= exception;
            _pendingStopDataIncomplete = true;
        }
        try
        {
            sampler.Dispose();
            _sampler = null;
        }
        catch (Exception exception) { failure ??= exception; }
    }

    private RuntimeSourceBatch[] PendingStopBatches()
    {
        var result = new List<RuntimeSourceBatch>(3);
        if (_pendingSamplingStopBatch is not null) result.Add(_pendingSamplingStopBatch);
        if (_pendingAutomaticStopBatch is not null) result.Add(_pendingAutomaticStopBatch);
        if (_pendingManualStopBatch is not null) result.Add(_pendingManualStopBatch);
        return result.ToArray();
    }

    private void ClearPendingStopResult()
    {
        _pendingSamplingStopBatch = null;
        _pendingAutomaticStopBatch = null;
        _pendingManualStopBatch = null;
        _pendingStopDataIncomplete = false;
    }

    private bool HasActiveBackend() => _sampler is not null || _automatic || _manualLease is not null;
    private static bool HasData(RuntimeSourceBatch batch) =>
        batch.Methods.Count > 0 || batch.Quality != QualityCounters.Zero;

    private static RuntimeSourceBatch MergeBatches(RuntimeSourceBatch retained, RuntimeSourceBatch additional)
    {
        if (retained.Source != additional.Source || retained.ExactCalls != additional.ExactCalls ||
            retained.CpuTime != additional.CpuTime)
            throw new InvalidOperationException("Only batches from the same runtime source may be accumulated.");
        var methods = retained.Methods.ToList();
        var indexes = new Dictionary<long, int>();
        for (var index = 0; index < methods.Count; index++) indexes[methods[index].MethodId] = index;
        foreach (var method in additional.Methods)
        {
            if (!indexes.TryGetValue(method.MethodId, out var index))
            {
                indexes[method.MethodId] = methods.Count;
                methods.Add(method);
                continue;
            }
            var existing = methods[index];
            methods[index] = existing with
            {
                Value = checked(existing.Value + method.Value),
                Calls = checked(existing.Calls + method.Calls)
            };
        }
        return retained with { Quality = retained.Quality.Add(additional.Quality), Methods = methods };
    }

    private void ThrowIfStartInterrupted()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ProductionRuntimeCaptureBackend),
                "Capture backend was disposed during start.");
        if (_stopTask is not null)
            throw new InvalidOperationException("Capture backend stop was requested during start.");
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    public void Dispose()
    {
        Task<RuntimeCaptureStopResult> stop;
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            stop = StopAsync();
        }
        if (!stop.IsCompletedSuccessfully) ObserveFault(stop);
    }

    private RuntimeSourceBatch SamplingBatch(SamplingSnapshot snapshot)
    {
        var methods = new List<MethodSample>();
        var overflowed = checked(snapshot.Counters.DroppedMethods + snapshot.Counters.DroppedStacks);
        foreach (var method in snapshot.Methods.Take(_maxMethods))
        {
            var label = BoundLabel(method.Label);
            var key = method.AssemblyName + "\0" + label;
            if (!_samplingIds.TryGetValue(key, out var id))
            {
                if (_samplingIds.Count >= _maxMethods) { overflowed++; continue; }
                _samplingIds[key] = id = _nextSamplingId++;
            }
            methods.Add(new MethodSample(id, label, method.SampleCount, 0));
        }
        return new(CaptureSource.Sampling, false, false,
            new QualityCounters(snapshot.Counters.SamplesAccepted, snapshot.Counters.DroppedSamples,
                overflowed, snapshot.Counters.IgnoredThreadSamples), methods);
    }

    private RuntimeSourceBatch AutomaticBatch(InstrumentationRecorder.Snapshot snapshot)
    {
        var invalid = snapshot.ForcedClosed;
        var methods = new List<MethodSample>();
        foreach (var sample in snapshot.Samples)
        {
            // Resolve every recorder ID through the loaded manifest. Unknown IDs fail closed.
            var label = _manifest?.ResolveLabel(sample.MethodId);
            if (label is null) { invalid++; continue; }
            methods.Add(new MethodSample(sample.MethodId, BoundLabel(label), TicksToNanoseconds(sample.TotalTicks), sample.Calls));
            if (methods.Count == _maxMethods) break;
        }
        return new(CaptureSource.AutomaticSpans, true, false,
            new QualityCounters(methods.Sum(method => method.Calls), snapshot.Dropped, snapshot.Truncated, invalid), methods);
    }

    private RuntimeSourceBatch ManualBatch(CsProfiler.FrameSnapshot snapshot)
    {
        var methods = new List<MethodSample>();
        var overflowed = snapshot.TruncatedLabels;
        for (var index = 0; index < snapshot.Names.Length && methods.Count < _maxMethods; index++)
        {
            var label = BoundLabel(_manualLabelPrefix + snapshot.Names[index]);
            if (!_manualIds.TryGetValue(label, out var id))
            {
                if (_manualIds.Count >= _maxMethods) { overflowed++; continue; }
                _manualIds[label] = id = _nextManualId++;
            }
            methods.Add(new MethodSample(id, label, checked(snapshot.TotalUsec[index] * 1_000), snapshot.Calls[index]));
        }
        return new(CaptureSource.ManualSpans, true, false,
            new QualityCounters(methods.Sum(method => method.Calls), snapshot.DroppedScopes, overflowed, 0), methods);
    }

    private static long TicksToNanoseconds(long ticks) => checked((long)(ticks * (1_000_000_000.0 / Stopwatch.Frequency)));

    private static string[] SplitList(string value) => string.IsNullOrEmpty(value)
        ? Array.Empty<string>() : value.Split(';', StringSplitOptions.RemoveEmptyEntries);

    private static string BoundLabel(string value)
    {
        value = new string((value ?? string.Empty).Where(character => !char.IsControl(character)).ToArray());
        if (value.Length == 0) value = "(unknown method)";
        return value[..Math.Min(value.Length, ProtocolLimits.MaxMethodLabelCharacters)];
    }

    private static InstrumentationManifest? FindManifest()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            if (InstrumentationManifest.TryRead(assembly, out var manifest)) return manifest;
        return null;
    }

    private static long ReadEffectiveInterval()
    {
        var setting = Environment.GetEnvironmentVariable("DOTNET_EventPipeSamplingRate") ??
            Environment.GetEnvironmentVariable("COMPlus_EventPipeSamplingRate");
        return long.TryParse(setting, out var value) && value >= ProtocolLimits.MinSamplingIntervalNanoseconds &&
            value <= ProtocolLimits.MaxSamplingIntervalNanoseconds ? value : 0;
    }

    private static Func<SamplingOptions, IManagedSamplingLease>? CreateSamplingFactory()
    {
#if GODOT_CSHARP_PROFILER_SAMPLING
        return options => new ManagedSamplingLease(options);
#else
        return null;
#endif
    }

    private static IManualCaptureLease? AcquireManualLease(string owner) =>
        CsProfiler.TryStartCapture(owner, out var lease) ? new ManualCaptureLease(lease) : null;

    internal interface IManualCaptureLease
    {
        bool IsActive { get; }
        CsProfiler.FrameSnapshot FlushFrame();
        bool Stop();
    }

    private sealed class ManualCaptureLease(CsProfiler.CaptureLease lease) : IManualCaptureLease
    {
        public bool IsActive => lease.IsActive;
        public CsProfiler.FrameSnapshot FlushFrame() => lease.FlushFrame();
        public bool Stop() => lease.Stop();
    }

    internal interface IManagedSamplingLease : IDisposable
    {
        Exception? Fault { get; }
        void Start();
        void Stop();
        Task<bool> StopAsync();
        SamplingSnapshot Snapshot(bool reset);
    }

#if GODOT_CSHARP_PROFILER_SAMPLING
    private sealed class ManagedSamplingLease(SamplingOptions options) : IManagedSamplingLease
    {
        private readonly ManagedSamplingSession _session = new(options);
        public Exception? Fault => _session.Fault;
        public void Start() => _session.StartAsync().GetAwaiter().GetResult();
        public void Stop() => _session.StopAsync().GetAwaiter().GetResult();
        public async Task<bool> StopAsync()
        {
            await _session.StopAsync().ConfigureAwait(false);
            return _session.StopDataIncomplete;
        }
        public SamplingSnapshot Snapshot(bool reset) => _session.GetSnapshot(reset);
        public void Dispose() => _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
#endif
}
#endif
