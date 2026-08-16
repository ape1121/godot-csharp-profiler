#nullable enable
namespace Apeworks.GodotCSharpProfiler.Runtime.Sampling;

public enum SampleIntervalConfigurationScope
{
    ProcessStartup
}

/// <summary>Describes controls the managed EventPipe backend can actually apply.</summary>
public sealed record SamplingCapabilities(
    SampleIntervalConfigurationScope SampleIntervalScope,
    bool SupportsPerSessionSampleInterval,
    bool SupportsRuntimeSampleIntervalChanges,
    bool CanReportEffectiveSampleInterval,
    string SampleIntervalRuntimeSetting)
{
    internal static SamplingCapabilities Detect() => new(
        SampleIntervalConfigurationScope.ProcessStartup,
        SupportsPerSessionSampleInterval: false,
        SupportsRuntimeSampleIntervalChanges: false,
        CanReportEffectiveSampleInterval: false,
        "DOTNET_EventPipeSamplingRate (or legacy COMPlus_EventPipeSamplingRate), " +
        "in nanoseconds, before process startup; the runtime does not expose the effective value");
}
