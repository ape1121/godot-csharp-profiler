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
    private readonly Dictionary<string, long> _manualIds = new(StringComparer.Ordinal);
    private IManagedSamplingLease? _sampler;
    private CsProfiler.CaptureLease? _manualLease;
    private CaptureModes _modes;
    private int _maxMethods;
    private long _nextManualId = 1;
    private InstrumentationManifest? _manifest;
    private bool _automatic;
    private bool _disposed;

    public ProductionRuntimeCaptureBackend() : this(CreateSamplingFactory(), FindManifest()) { }

    internal ProductionRuntimeCaptureBackend(Func<SamplingOptions, IManagedSamplingLease>? samplingFactory,
        InstrumentationManifest? manifest)
    {
        _samplingFactory = samplingFactory;
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
    public bool IsActive => _sampler is not null || _automatic || _manualLease?.IsActive == true;

    public bool TryStart(RuntimeCaptureConfiguration configuration, string owner, out string? error)
    {
        error = null;
        if (_disposed || IsActive) { error = "Capture backend is busy."; return false; }
        if ((configuration.Modes & ~Capabilities.Modes) != 0) { error = "Requested backend is unavailable."; return false; }
        _modes = configuration.Modes;
        _maxMethods = configuration.MaxMethods;
        try
        {
            if ((_modes & CaptureModes.Sampling) != 0)
            {
                _sampler = _samplingFactory!(new SamplingOptions { MaxUniqueMethods = _maxMethods });
                _sampler.Start();
            }
            if ((_modes & CaptureModes.AutomaticInstrumentation) != 0)
            {
                if (Interlocked.CompareExchange(ref s_automaticOwner, this, null) is not null)
                    throw new InvalidOperationException("Automatic instrumentation recorder is owned by another capture.");
                try { InstrumentationRecorder.StartCapture(); _automatic = true; }
                catch { Interlocked.CompareExchange(ref s_automaticOwner, null, this); throw; }
            }
            if ((_modes & CaptureModes.ManualScopes) != 0)
            {
                if (!CsProfiler.TryStartCapture(owner, out _manualLease))
                    throw new InvalidOperationException("Manual profiler lease is owned by another capture.");
            }
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            try { Stop(); }
            catch (Exception cleanup) { error = $"{error} Cleanup failed: {cleanup.Message}"; }
            return false;
        }
    }

    public IReadOnlyList<RuntimeSourceBatch> Drain()
    {
        if (!IsActive) return Array.Empty<RuntimeSourceBatch>();
        var result = new List<RuntimeSourceBatch>(2);
        if (_sampler is not null) result.Add(SamplingBatch(_sampler.Snapshot(reset: true)));
        if (_manualLease?.IsActive == true) result.Add(ManualBatch(_manualLease.FlushFrame()));
        return result;
    }

    public IReadOnlyList<RuntimeSourceBatch> Stop()
    {
        var result = new List<RuntimeSourceBatch>(3);
        Exception? failure = null;
        var sampler = _sampler;
        _sampler = null;
        if (sampler is not null)
        {
            try { sampler.Stop(); result.Add(SamplingBatch(sampler.Snapshot(reset: true))); }
            catch (Exception exception) { failure = exception; }
            finally { try { sampler.Dispose(); } catch (Exception exception) { failure ??= exception; } }
        }
        if (_automatic && ReferenceEquals(Interlocked.CompareExchange(ref s_automaticOwner, null, this), this))
        {
            try { result.Add(AutomaticBatch(InstrumentationRecorder.StopCapture())); }
            catch (Exception exception) { failure ??= exception; }
            finally { _automatic = false; }
        }
        var manual = _manualLease;
        _manualLease = null;
        if (manual is not null)
        {
            try { if (manual.IsActive) result.Add(ManualBatch(manual.FlushFrame())); manual.Stop(); }
            catch (Exception exception) { failure ??= exception; }
        }
        _modes = CaptureModes.None;
        if (failure is not null) throw new InvalidOperationException("One or more runtime capture backends failed to stop.", failure);
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
    }

    private RuntimeSourceBatch SamplingBatch(SamplingSnapshot snapshot) => new(CaptureSource.Sampling, false, false,
        new QualityCounters(snapshot.Counters.SamplesAccepted, snapshot.Counters.DroppedSamples,
            snapshot.Counters.DroppedMethods + snapshot.Counters.DroppedStacks, snapshot.Counters.IgnoredThreadSamples),
        snapshot.Methods.Take(_maxMethods).Select(method => new MethodSample(method.Id, method.SampleCount, 0)).ToArray());

    private RuntimeSourceBatch AutomaticBatch(InstrumentationRecorder.Snapshot snapshot)
    {
        var invalid = snapshot.ForcedClosed;
        var methods = new List<MethodSample>();
        foreach (var sample in snapshot.Samples)
        {
            // Resolve every recorder ID through the loaded manifest. Unknown IDs fail closed.
            if (_manifest?.ResolveLabel(sample.MethodId) is null) { invalid++; continue; }
            methods.Add(new MethodSample(sample.MethodId, TicksToNanoseconds(sample.TotalTicks), sample.Calls));
            if (methods.Count == _maxMethods) break;
        }
        return new(CaptureSource.AutomaticSpans, true, false,
            new QualityCounters(methods.Sum(method => method.Calls), snapshot.Dropped, snapshot.Truncated, invalid), methods);
    }

    private RuntimeSourceBatch ManualBatch(CsProfiler.FrameSnapshot snapshot)
    {
        var methods = new List<MethodSample>();
        for (var index = 0; index < snapshot.Names.Length && methods.Count < _maxMethods; index++)
        {
            var label = snapshot.Names[index];
            if (!_manualIds.TryGetValue(label, out var id)) _manualIds[label] = id = _nextManualId++;
            methods.Add(new MethodSample(id, checked(snapshot.TotalUsec[index] * 1_000), snapshot.Calls[index]));
        }
        return new(CaptureSource.ManualSpans, true, false,
            new QualityCounters(methods.Sum(method => method.Calls), snapshot.DroppedScopes, snapshot.TruncatedLabels, 0), methods);
    }

    private static long TicksToNanoseconds(long ticks) => checked((long)(ticks * (1_000_000_000.0 / Stopwatch.Frequency)));

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

    internal interface IManagedSamplingLease : IDisposable
    {
        void Start();
        void Stop();
        SamplingSnapshot Snapshot(bool reset);
    }

#if GODOT_CSHARP_PROFILER_SAMPLING
    private sealed class ManagedSamplingLease(SamplingOptions options) : IManagedSamplingLease
    {
        private readonly ManagedSamplingSession _session = new(options);
        public void Start() => _session.StartAsync().GetAwaiter().GetResult();
        public void Stop() => _session.StopAsync().GetAwaiter().GetResult();
        public SamplingSnapshot Snapshot(bool reset) => _session.GetSnapshot(reset);
        public void Dispose() => _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
#endif
}
#endif
