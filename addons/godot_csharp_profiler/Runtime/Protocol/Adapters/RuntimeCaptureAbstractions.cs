#nullable enable
using Apeworks.GodotCSharpProfiler.Protocol;

namespace Apeworks.GodotCSharpProfiler.Runtime.Protocol.Adapters;

/// <summary>Transport used by the runtime coordinator. Implementations must not retain mutable payloads.</summary>
public interface IRuntimeCaptureTransport
{
    void Send(WireMap message);
}

/// <summary>Truthful capabilities of one runtime backend.</summary>
public sealed record RuntimeBackendCapabilities(
    CaptureModes Modes,
    bool SamplingIntervalRuntimeConfigurable,
    long EffectiveSamplingIntervalNanoseconds,
    int MaxMethods,
    string SamplingStatus,
    string AutomaticInstrumentationStatus);

public sealed record RuntimeCaptureConfiguration(
    long Generation,
    string Fingerprint,
    CaptureModes Modes,
    long RequestedSamplingIntervalNanoseconds,
    int MaxMethods,
    string SamplingIncludeAssemblies,
    string SamplingExcludeAssemblies,
    string ManualLabelPrefix)
{
    public RuntimeCaptureConfiguration(long generation, string fingerprint, CaptureModes modes,
        long requestedSamplingIntervalNanoseconds, int maxMethods)
        : this(generation, fingerprint, modes, requestedSamplingIntervalNanoseconds, maxMethods,
            string.Empty, string.Empty, string.Empty) { }
}

/// <summary>A source-specific batch. Sources are deliberately never combined.</summary>
public sealed record RuntimeSourceBatch(
    CaptureSource Source,
    bool ExactCalls,
    bool CpuTime,
    QualityCounters Quality,
    IReadOnlyList<MethodSample> Methods);

/// <summary>Backend-neutral capture operations. A backend must make Stop idempotent.</summary>
public interface IRuntimeCaptureBackend : IDisposable
{
    RuntimeBackendCapabilities Capabilities { get; }
    bool IsActive { get; }
    bool TryStart(RuntimeCaptureConfiguration configuration, string owner, out string? error);
    IReadOnlyList<RuntimeSourceBatch> Drain();
    IReadOnlyList<RuntimeSourceBatch> Stop();
}
