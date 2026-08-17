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
    private readonly Dictionary<string, long> _samplingIds = new(StringComparer.Ordinal);
    private IManagedSamplingLease? _sampler;
    private CsProfiler.CaptureLease? _manualLease;
    private CaptureModes _modes;
    private int _maxMethods;
    private long _nextManualId = 1;
    private long _nextSamplingId = 1;
    private string _manualLabelPrefix = string.Empty;
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
        _manualIds.Clear();
        _samplingIds.Clear();
        _nextManualId = _nextSamplingId = 1;
        _manualLabelPrefix = configuration.ManualLabelPrefix;
        try
        {
            if ((_modes & CaptureModes.Sampling) != 0)
            {
                _sampler = _samplingFactory!(new SamplingOptions
                {
                    MaxUniqueMethods = _maxMethods,
                    IncludeAssemblyPrefixes = SplitList(configuration.SamplingIncludeAssemblies),
                    ExcludeAssemblyPrefixes = SplitList(configuration.SamplingExcludeAssemblies)
                });
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
        if (_sampler is not null)
        {
            if (_sampler.Fault is not null)
                throw new InvalidOperationException("Managed sampling stopped unexpectedly.", _sampler.Fault);
            var sampling = SamplingBatch(_sampler.Snapshot(reset: true));
            if (sampling.Methods.Count > 0 || sampling.Quality != QualityCounters.Zero)
                result.Add(sampling);
        }
        if (_manualLease?.IsActive == true)
        {
            var manual = ManualBatch(_manualLease.FlushFrame());
            if (manual.Methods.Count > 0 || manual.Quality != QualityCounters.Zero)
                result.Add(manual);
        }
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

    internal interface IManagedSamplingLease : IDisposable
    {
        Exception? Fault { get; }
        void Start();
        void Stop();
        SamplingSnapshot Snapshot(bool reset);
    }

#if GODOT_CSHARP_PROFILER_SAMPLING
    private sealed class ManagedSamplingLease(SamplingOptions options) : IManagedSamplingLease
    {
        private readonly ManagedSamplingSession _session = new(options);
        public Exception? Fault => _session.Fault;
        public void Start() => _session.StartAsync().GetAwaiter().GetResult();
        public void Stop() => _session.StopAsync().GetAwaiter().GetResult();
        public SamplingSnapshot Snapshot(bool reset) => _session.GetSnapshot(reset);
        public void Dispose() => _session.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
#endif
}
#endif
