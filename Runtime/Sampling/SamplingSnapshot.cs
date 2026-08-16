#nullable enable
using System;
using System.Collections.Generic;

namespace Apeworks.GodotCSharpProfiler.Runtime.Sampling;

public readonly record struct SamplingFrame(string AssemblyName, string MethodName);

public sealed record SampledMethod(int Id, string AssemblyName, string Label, long SampleCount);

public sealed record SampledStack(IReadOnlyList<int> MethodIds, long SampleCount);

public sealed record SamplingCounters(
    long SamplesReceived,
    long SamplesAccepted,
    long DroppedSamples,
    long DroppedMethods,
    long DroppedStacks,
    long TruncatedFrames,
    long TruncatedLabels,
    long IgnoredThreadSamples,
    long FilteredFrames);

/// <summary>An immutable, transport-friendly batch of samples accumulated since the last reset.</summary>
public sealed record SamplingSnapshot(
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    IReadOnlyList<SampledMethod> Methods,
    IReadOnlyList<SampledStack> Stacks,
    SamplingCounters Counters)
{
    public static SamplingSnapshot Empty(DateTimeOffset timestamp) => new(
        timestamp,
        timestamp,
        Array.Empty<SampledMethod>(),
        Array.Empty<SampledStack>(),
        new SamplingCounters(0, 0, 0, 0, 0, 0, 0, 0, 0));
}
