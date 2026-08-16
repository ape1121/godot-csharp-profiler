#nullable enable
using System;
using System.Collections.Generic;

namespace Apeworks.GodotCSharpProfiler.Runtime.Sampling;

public sealed class SamplingOptions
{
    public int MaxUniqueMethods { get; init; } = 4_096;
    public int MaxUniqueStacks { get; init; } = 8_192;
    public int MaxStackDepth { get; init; } = 128;
    public int MaxLabelLength { get; init; } = 256;

    /// <summary>
    /// Maximum lifetime of one in-memory TraceLog epoch. The EventPipe session is renewed at this
    /// interval so TraceEvent cannot retain an unbounded full-session index.
    /// </summary>
    public TimeSpan TraceRetentionDuration { get; init; } = TimeSpan.FromSeconds(30);

    public int CircularBufferSizeMegabytes { get; init; } = 32;
    public IReadOnlyList<string> IncludeAssemblyPrefixes { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludeAssemblyPrefixes { get; init; } = Array.Empty<string>();

    internal SamplingOptions ValidateAndCopy()
    {
        if (MaxUniqueMethods <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxUniqueMethods));
        if (MaxUniqueStacks <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxUniqueStacks));
        if (MaxStackDepth <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxStackDepth));
        if (MaxLabelLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxLabelLength));
        if (CircularBufferSizeMegabytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(CircularBufferSizeMegabytes));
        if (TraceRetentionDuration < TimeSpan.FromSeconds(1) ||
            TraceRetentionDuration > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(
                nameof(TraceRetentionDuration),
                "Trace retention must be between one second and ten minutes.");
        }

        return new SamplingOptions
        {
            MaxUniqueMethods = MaxUniqueMethods,
            MaxUniqueStacks = MaxUniqueStacks,
            MaxStackDepth = MaxStackDepth,
            MaxLabelLength = MaxLabelLength,
            CircularBufferSizeMegabytes = CircularBufferSizeMegabytes,
            TraceRetentionDuration = TraceRetentionDuration,
            IncludeAssemblyPrefixes = CopyPrefixes(IncludeAssemblyPrefixes, nameof(IncludeAssemblyPrefixes)),
            ExcludeAssemblyPrefixes = CopyPrefixes(ExcludeAssemblyPrefixes, nameof(ExcludeAssemblyPrefixes))
        };
    }

    private static string[] CopyPrefixes(IReadOnlyList<string>? prefixes, string parameterName)
    {
        if (prefixes is null)
            throw new ArgumentNullException(parameterName);

        var result = new string[prefixes.Count];
        for (var index = 0; index < result.Length; index++)
        {
            var prefix = prefixes[index];
            if (string.IsNullOrWhiteSpace(prefix))
                throw new ArgumentException("Assembly prefixes cannot be empty.", parameterName);
            result[index] = prefix.Trim();
        }
        return result;
    }
}
