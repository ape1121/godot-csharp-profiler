using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Apeworks.GodotCSharpProfiler.Instrumentation;

/// <summary>Allocation-free-when-inactive, bounded recorder called by woven methods.</summary>
public static class InstrumentationRecorder
{
    public readonly struct Token
    {
        internal Token(long generation, int methodId, long started, int depth)
        { Generation = generation; MethodId = methodId; Started = started; Depth = depth; }
        internal long Generation { get; }
        internal int MethodId { get; }
        internal long Started { get; }
        internal int Depth { get; }
        public bool IsValid => Generation != 0;
    }

    public sealed class Sample
    {
        internal Sample(int methodId, long calls, long ticks)
        { MethodId = methodId; Calls = calls; TotalTicks = ticks; }
        public int MethodId { get; }
        public long Calls { get; }
        public long TotalTicks { get; }
    }

    public sealed class Snapshot
    {
        internal Snapshot(Sample[] samples, long generation, long dropped, long truncated, long forcedClosed)
        { Samples = Array.AsReadOnly(samples); Generation = generation; Dropped = dropped; Truncated = truncated; ForcedClosed = forcedClosed; }
        public IReadOnlyList<Sample> Samples { get; }
        public long Generation { get; }
        public long Dropped { get; }
        public long Truncated { get; }
        public long ForcedClosed { get; }
    }

    private struct Frame { internal long Generation; internal int MethodId; internal long Started; }
    private sealed class ThreadStack { internal readonly Frame[] Frames = new Frame[MaximumDepth]; internal int Count; }
    private struct Aggregate { internal long Calls; internal long Ticks; }

    public const int MaximumMethods = 16384;
    public const int MaximumSamples = MaximumMethods;
    public const int MaximumDepth = 1024;
    private static readonly object Gate = new();
    private static readonly Aggregate[] Aggregates = new Aggregate[MaximumMethods];
    [ThreadStatic] private static ThreadStack? _stack;
    private static long _generation;
    private static int _active;
    private static long _dropped;
    private static long _truncated;
    private static long _forcedClosed;

    public static bool Active => Volatile.Read(ref _active) != 0;

    public static long StartCapture()
    {
        lock (Gate)
        {
            Array.Clear(Aggregates, 0, Aggregates.Length);
            _dropped = _truncated = _forcedClosed = 0;
            var generation = unchecked(++_generation);
            if (generation == 0) generation = ++_generation;
            Volatile.Write(ref _active, 1);
            return generation;
        }
    }

    public static Token Enter(int methodId)
    {
        // This branch is the complete inactive path: no lock, clock, TLS initialization, or allocation.
        if (Volatile.Read(ref _active) == 0) return default;
        var generation = Volatile.Read(ref _generation);
        if ((uint)methodId >= MaximumMethods) { Interlocked.Increment(ref _dropped); return default; }
        var stack = _stack ??= new ThreadStack();
        if (stack.Count == MaximumDepth) { Interlocked.Increment(ref _dropped); return default; }
        var started = Stopwatch.GetTimestamp();
        var depth = stack.Count++;
        stack.Frames[depth] = new Frame { Generation = generation, MethodId = methodId, Started = started };
        return new Token(generation, methodId, started, depth);
    }

    public static void Exit(Token token)
    {
        if (!token.IsValid) return;
        var stack = _stack;
        if (stack is null || stack.Count != token.Depth + 1)
        { Interlocked.Increment(ref _forcedClosed); return; }
        var frame = stack.Frames[token.Depth];
        stack.Count--;
        if (frame.Generation != token.Generation || frame.MethodId != token.MethodId || frame.Started != token.Started)
        { Interlocked.Increment(ref _forcedClosed); return; }
        if (Volatile.Read(ref _active) == 0 || token.Generation != Volatile.Read(ref _generation)) return;
        var elapsed = Math.Max(0, Stopwatch.GetTimestamp() - token.Started);
        lock (Gate)
        {
            if (_active == 0 || token.Generation != _generation) return;
            Aggregates[token.MethodId].Calls++;
            Aggregates[token.MethodId].Ticks += elapsed;
        }
    }

    public static Snapshot StopCapture()
    {
        lock (Gate)
        {
            Volatile.Write(ref _active, 0);
            var stack = _stack;
            if (stack is not null) { _forcedClosed += stack.Count; stack.Count = 0; }
            var samples = new List<Sample>();
            for (var id = 0; id < Aggregates.Length; id++)
            {
                var value = Aggregates[id];
                if (value.Calls == 0) continue;
                if (samples.Count == MaximumSamples) { _truncated++; continue; }
                samples.Add(new Sample(id, value.Calls, value.Ticks));
            }
            return new Snapshot(samples.ToArray(), _generation, _dropped, _truncated, _forcedClosed);
        }
    }
}
