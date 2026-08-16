#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Apeworks.GodotCSharpProfiler.Runtime.Sampling;

/// <summary>Thread-safe bounded aggregation, separate from EventPipe to permit deterministic testing.</summary>
public sealed class SamplingAggregator
{
    private sealed class MutableMethod
    {
        public required int Id;
        public required string AssemblyName;
        public required string Label;
        public long Samples;
    }

    private sealed class MutableStack
    {
        public required int[] MethodIds;
        public long Samples;
    }

    private readonly object _gate = new();
    private readonly SamplingOptions _options;
    private readonly Dictionary<string, MutableMethod> _methods = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MutableStack> _stacks = new(StringComparer.Ordinal);
    private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private long _samplesReceived;
    private long _samplesAccepted;
    private long _droppedSamples;
    private long _droppedMethods;
    private long _droppedStacks;
    private long _truncatedFrames;
    private long _truncatedLabels;
    private long _ignoredThreadSamples;
    private long _filteredFrames;

    public SamplingAggregator(SamplingOptions options)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).ValidateAndCopy();
    }

    public void AddSample(string? threadName, IReadOnlyList<SamplingFrame> frames)
    {
        if (frames is null)
            throw new ArgumentNullException(nameof(frames));

        lock (_gate)
        {
            _samplesReceived++;
            if (IsInfrastructureThread(threadName))
            {
                _ignoredThreadSamples++;
                return;
            }

            var methodIds = new List<int>(Math.Min(frames.Count, _options.MaxStackDepth));
            var retainedFrames = 0;
            foreach (var frame in frames)
            {
                if (!AssemblyIncluded(frame.AssemblyName))
                {
                    _filteredFrames++;
                    continue;
                }
                if (retainedFrames >= _options.MaxStackDepth)
                {
                    _truncatedFrames++;
                    continue;
                }
                retainedFrames++;

                var assemblyName = Truncate(frame.AssemblyName ?? "(unknown assembly)");
                var label = Truncate(frame.MethodName ?? "(unknown method)");
                var key = assemblyName + "\0" + label;
                if (!_methods.TryGetValue(key, out var method))
                {
                    if (_methods.Count >= _options.MaxUniqueMethods)
                    {
                        _droppedMethods++;
                        continue;
                    }
                    method = new MutableMethod
                    {
                        Id = _methods.Count,
                        AssemblyName = assemblyName,
                        Label = label
                    };
                    _methods.Add(key, method);
                }
                method.Samples++;
                methodIds.Add(method.Id);
            }

            if (methodIds.Count == 0)
            {
                _droppedSamples++;
                return;
            }

            var stackKey = string.Join(",", methodIds);
            if (!_stacks.TryGetValue(stackKey, out var stack))
            {
                if (_stacks.Count >= _options.MaxUniqueStacks)
                {
                    _droppedStacks++;
                    _droppedSamples++;
                    return;
                }
                stack = new MutableStack { MethodIds = methodIds.ToArray() };
                _stacks.Add(stackKey, stack);
            }
            stack.Samples++;
            _samplesAccepted++;
        }
    }

    public SamplingSnapshot GetSnapshot(bool reset = true)
    {
        lock (_gate)
        {
            var endedAt = DateTimeOffset.UtcNow;
            var methods = Array.AsReadOnly(_methods.Values
                .OrderBy(method => method.Id)
                .Select(method => new SampledMethod(method.Id, method.AssemblyName, method.Label, method.Samples))
                .ToArray());
            var stacks = Array.AsReadOnly(_stacks.Values
                .Select(stack => new SampledStack(Array.AsReadOnly((int[])stack.MethodIds.Clone()), stack.Samples))
                .ToArray());
            var counters = new SamplingCounters(
                _samplesReceived, _samplesAccepted, _droppedSamples, _droppedMethods, _droppedStacks,
                _truncatedFrames, _truncatedLabels, _ignoredThreadSamples, _filteredFrames);
            var snapshot = new SamplingSnapshot(_startedAt, endedAt, methods, stacks, counters);
            if (reset)
                Reset(endedAt);
            return snapshot;
        }
    }

    private void Reset(DateTimeOffset timestamp)
    {
        _methods.Clear();
        _stacks.Clear();
        _startedAt = timestamp;
        _samplesReceived = 0;
        _samplesAccepted = 0;
        _droppedSamples = 0;
        _droppedMethods = 0;
        _droppedStacks = 0;
        _truncatedFrames = 0;
        _truncatedLabels = 0;
        _ignoredThreadSamples = 0;
        _filteredFrames = 0;
    }

    private bool AssemblyIncluded(string? assemblyName)
    {
        assemblyName ??= "";
        var included = _options.IncludeAssemblyPrefixes.Count == 0 ||
            _options.IncludeAssemblyPrefixes.Any(prefix => assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return included && !_options.ExcludeAssemblyPrefixes.Any(
            prefix => assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private string Truncate(string value)
    {
        if (value.Length <= _options.MaxLabelLength)
            return value;
        _truncatedLabels++;
        return value[.._options.MaxLabelLength];
    }

    private static bool IsInfrastructureThread(string? threadName)
    {
        if (string.IsNullOrWhiteSpace(threadName))
            return false;
        return threadName.Contains("EventPipe", StringComparison.OrdinalIgnoreCase) ||
               threadName.Contains("SampleProfiler", StringComparison.OrdinalIgnoreCase) ||
               threadName.Contains("sampling", StringComparison.OrdinalIgnoreCase) ||
               threadName.Contains("sampler", StringComparison.OrdinalIgnoreCase) ||
               threadName.Contains("profiler", StringComparison.OrdinalIgnoreCase);
    }
}
