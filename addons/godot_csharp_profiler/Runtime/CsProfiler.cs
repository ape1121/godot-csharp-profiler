using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Apeworks.GodotCSharpProfiler;

public static class CsProfiler
{
    public const int MaximumRetainedNodes = 4096;
    public const int MaximumLabelLength = 128;
    public const string WorkerThreadsRootName = "Worker Threads";

    private const int MaximumSnapshotDepth = 32;
    private const int MaximumFunctionNames = 4096;

    public sealed class SampleNode
    {
        public string Name;
        public SampleNode Parent;
        public readonly Dictionary<string, SampleNode> Children = new(StringComparer.Ordinal);
        public long Calls;
        public long TotalTicks;
    }

    private sealed class WorkerSample
    {
        public long Calls;
        public long Ticks;
    }

    internal sealed class ScopeState
    {
        internal long Generation;
        internal long StartTimestamp;
        internal SampleNode Node;
        internal ScopeState Parent;
        internal string WorkerName;
        internal int Disposed;
    }

    public sealed class FrameSnapshot
    {
        public static readonly FrameSnapshot Empty = new()
        {
            Names = Array.Empty<string>(),
            Depths = Array.Empty<int>(),
            Calls = Array.Empty<long>(),
            TotalUsec = Array.Empty<long>()
        };

        // Parallel arrays describing the frame's call tree in pre-order; Depths reconstructs the
        // hierarchy. Self time is derived by the consumer (total minus direct children).
        public string[] Names;
        public int[] Depths;
        public long[] Calls;
        public long[] TotalUsec;
        public long CsTotalUsec;
        public long DroppedScopes;
        public long TruncatedLabels;
    }

    public sealed class CaptureLease : IDisposable
    {
        private readonly long _token;

        internal CaptureLease(long token, string owner)
        {
            _token = token;
            Owner = owner;
        }

        public string Owner { get; }
        public bool IsActive => OwnsCapture(_token);
        public FrameSnapshot FlushFrame() => TryFlushFrame(_token);
        public bool Stop() => TryStopCapture(_token);
        public void Dispose() => Stop();
    }

    private static readonly object CaptureLock = new();
    private static volatile bool _active;
    private static int _mainThreadId = -1;
    private static long _nextCaptureToken;
    private static long _captureToken;
    private static string _captureOwner = "";
    private static SampleNode _root = new() { Name = "Frame" };
    private static ScopeState _currentScope;
    private static ConcurrentDictionary<string, WorkerSample> _workerSamples =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(string File, string Member), string> FunctionNames =
        new();
    private static int _retainedNodeCount;
    private static long _droppedScopes;
    private static long _truncatedLabels;

    public static bool Active => _active;

    public static string CaptureOwner
    {
        get
        {
            lock (CaptureLock)
                return _captureOwner;
        }
    }

    public static int RetainedNodeCount
    {
        get
        {
            lock (CaptureLock)
                return _retainedNodeCount;
        }
    }

    public static bool TryStartCapture(string owner, out CaptureLease lease)
    {
        lease = null;
        if (string.IsNullOrWhiteSpace(owner))
            return false;
        lock (CaptureLock)
        {
            if (_active)
                return false;
            Reset();
            _mainThreadId = Environment.CurrentManagedThreadId;
            _captureOwner = owner.Trim();
            _captureToken = Interlocked.Increment(ref _nextCaptureToken);
            _active = true;
            lease = new CaptureLease(_captureToken, _captureOwner);
            return true;
        }
    }

    private static bool OwnsCapture(long token)
    {
        lock (CaptureLock)
            return _active && token != 0 && token == _captureToken;
    }

    private static bool TryStopCapture(long token)
    {
        lock (CaptureLock)
        {
            if (!_active || token == 0 || token != _captureToken)
                return false;
            _active = false;
            _captureToken = 0;
            _captureOwner = "";
            _mainThreadId = -1;
            Reset();
            return true;
        }
    }

    private static void Reset()
    {
        _root = new SampleNode { Name = "Frame" };
        _currentScope = null;
        _workerSamples = new ConcurrentDictionary<string, WorkerSample>(StringComparer.Ordinal);
        _retainedNodeCount = 0;
        _droppedScopes = 0;
        _truncatedLabels = 0;
    }

    public static ProfileScope Scope(string name)
    {
        if (!_active)
            return default;
        return BeginScope(NormalizeLabel(name ?? "(null)"));
    }

    public static ProfileScope Fn(
        [CallerFilePath] string filePath = "",
        [CallerMemberName] string memberName = "")
    {
        if (!_active)
            return default;

        var key = (filePath ?? "", memberName ?? "");
        string name;
        if (!FunctionNames.TryGetValue(key, out name))
        {
            name = NormalizeLabel($"{Path.GetFileNameWithoutExtension(key.Item1)}.{key.Item2}");
            if (FunctionNames.Count < MaximumFunctionNames)
                FunctionNames.TryAdd(key, name);
        }
        return BeginScope(name);
    }

    private static string NormalizeLabel(string name)
    {
        if (name.Length <= MaximumLabelLength)
            return name;
        Interlocked.Increment(ref _truncatedLabels);
        return name[..MaximumLabelLength];
    }

    private static ProfileScope BeginScope(string name)
    {
        lock (CaptureLock)
        {
            if (!_active)
                return default;

            var threadId = Environment.CurrentManagedThreadId;
            var state = new ScopeState
            {
                Generation = _captureToken,
                StartTimestamp = Stopwatch.GetTimestamp()
            };

            if (threadId != _mainThreadId)
            {
                state.WorkerName = name;
                return new ProfileScope(state);
            }

            var parent = _currentScope?.Node ?? _root;
            if (!parent.Children.TryGetValue(name, out var node))
            {
                if (_retainedNodeCount >= MaximumRetainedNodes)
                {
                    _droppedScopes++;
                    return default;
                }
                node = new SampleNode { Name = name, Parent = parent };
                parent.Children.Add(name, node);
                _retainedNodeCount++;
            }

            state.Node = node;
            state.Parent = _currentScope;
            _currentScope = state;
            return new ProfileScope(state);
        }
    }

    private static void EndScope(ScopeState state)
    {
        if (state == null || Interlocked.Exchange(ref state.Disposed, 1) != 0)
            return;

        var endTimestamp = Stopwatch.GetTimestamp();
        lock (CaptureLock)
        {
            // A scope is capture-generation owned. Reset/stop makes all outstanding handles inert.
            if (!_active || state.Generation == 0 || state.Generation != _captureToken)
                return;

            var elapsed = Math.Max(0, endTimestamp - state.StartTimestamp);
            if (state.WorkerName != null)
            {
                if (!_workerSamples.TryGetValue(state.WorkerName, out var sample))
                {
                    if (_retainedNodeCount >= MaximumRetainedNodes)
                    {
                        _droppedScopes++;
                        return;
                    }
                    sample = new WorkerSample();
                    if (_workerSamples.TryAdd(state.WorkerName, sample))
                        _retainedNodeCount++;
                    else
                        sample = _workerSamples[state.WorkerName];
                }
                sample.Calls++;
                sample.Ticks += elapsed;
                return;
            }

            if (state.Node == null)
                return;
            state.Node.Calls++;
            state.Node.TotalTicks += elapsed;

            // Disposal may resume on another thread or occur out of order. The capture lock makes
            // closure safe; only unwind states that are actually complete, preserving live children.
            if (ReferenceEquals(_currentScope, state))
            {
                _currentScope = state.Parent;
                while (_currentScope is { Disposed: not 0 })
                    _currentScope = _currentScope.Parent;
            }
        }
    }

    private static FrameSnapshot TryFlushFrame(long token)
    {
        lock (CaptureLock)
        {
            if (!_active || token == 0 || token != _captureToken ||
                Environment.CurrentManagedThreadId != _mainThreadId)
                return FrameSnapshot.Empty;

            var names = new List<string>();
            var depths = new List<int>();
            var calls = new List<long>();
            var totalUsec = new List<long>();
            long csTotalTicks = 0;

            // A path containing an open invocation is deferred intact. This prevents a frame flush
            // from publishing/resetting half a scope and preserves its eventual duration exactly.
            var activeNodes = new HashSet<SampleNode>();
            for (var scope = _currentScope; scope != null; scope = scope.Parent)
            {
                if (scope.Disposed == 0 && scope.Node != null)
                    activeNodes.Add(scope.Node);
            }

            var stack = new Stack<(SampleNode Node, int Depth)>();
            foreach (var child in _root.Children.Values)
                stack.Push((child, 0));
            while (stack.Count > 0 && names.Count < MaximumRetainedNodes)
            {
                var (node, depth) = stack.Pop();
                if (activeNodes.Contains(node))
                    continue;
                if (node.Calls > 0)
                {
                    names.Add(node.Name);
                    depths.Add(depth);
                    calls.Add(node.Calls);
                    totalUsec.Add(TicksToUsec(node.TotalTicks));
                    if (depth == 0)
                        csTotalTicks += node.TotalTicks;
                    node.Calls = 0;
                    node.TotalTicks = 0;
                }
                if (depth + 1 < MaximumSnapshotDepth)
                {
                    foreach (var child in node.Children.Values)
                        stack.Push((child, depth + 1));
                }
            }

            AppendWorkerSamples(names, depths, calls, totalUsec);
            var dropped = _droppedScopes;
            var truncated = _truncatedLabels;
            _droppedScopes = 0;
            _truncatedLabels = 0;

            return new FrameSnapshot
            {
                Names = names.ToArray(),
                Depths = depths.ToArray(),
                Calls = calls.ToArray(),
                TotalUsec = totalUsec.ToArray(),
                CsTotalUsec = TicksToUsec(csTotalTicks),
                DroppedScopes = dropped,
                TruncatedLabels = truncated
            };
        }
    }

    private static void AppendWorkerSamples(
        List<string> names,
        List<int> depths,
        List<long> calls,
        List<long> totalUsec)
    {
        var workerRows = new List<(string Name, long Calls, long Ticks)>();
        foreach (var pair in _workerSamples)
        {
            var sampleCalls = pair.Value.Calls;
            var sampleTicks = pair.Value.Ticks;
            pair.Value.Calls = 0;
            pair.Value.Ticks = 0;
            if (sampleCalls > 0)
                workerRows.Add((pair.Key, sampleCalls, sampleTicks));
        }
        if (workerRows.Count == 0 || names.Count >= MaximumRetainedNodes)
            return;

        names.Add(WorkerThreadsRootName);
        depths.Add(0);
        calls.Add(workerRows.Sum(row => row.Calls));
        totalUsec.Add(workerRows.Sum(row => TicksToUsec(row.Ticks)));
        foreach (var row in workerRows.OrderBy(row => row.Name, StringComparer.Ordinal))
        {
            if (names.Count >= MaximumRetainedNodes)
                break;
            names.Add(row.Name);
            depths.Add(1);
            calls.Add(row.Calls);
            totalUsec.Add(TicksToUsec(row.Ticks));
        }
    }

    private static long TicksToUsec(long stopwatchTicks) =>
        stopwatchTicks * 1_000_000L / Stopwatch.Frequency;

    public readonly struct ProfileScope : IDisposable
    {
        private readonly ScopeState _state;

        internal ProfileScope(ScopeState state) => _state = state;

        public void Dispose() => EndScope(_state);
    }
}
