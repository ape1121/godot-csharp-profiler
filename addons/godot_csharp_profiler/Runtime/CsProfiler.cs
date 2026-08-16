using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

public static class CsProfiler
{
    private const int MaxSnapshotNodes = 4096;
    private const int MaxSnapshotDepth = 32;

    public sealed class SampleNode
    {
        public string Name;
        public SampleNode Parent;
        public readonly Dictionary<string, SampleNode> Children = new(StringComparer.Ordinal);
        public long Calls;
        public long TotalTicks;
        internal long StartTimestamp;
    }

    private sealed class WorkerSample
    {
        public long Calls;
        public long Ticks;
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
        // Main-thread scoped time only; worker samples are visible in the tree but excluded so
        // the frame graph compares like with like against the engine's frame time.
        public long CsTotalUsec;
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

        public bool Stop()
        {
            var stopped = TryStopCapture(_token);
            return stopped;
        }

        public void Dispose() => Stop();
    }

    public const string WorkerThreadsRootName = "Worker Threads";

    private static readonly object CaptureLock = new();
    private static volatile bool _active;
    private static int _mainThreadId = -1;
    private static long _nextCaptureToken;
    private static long _captureToken;
    private static string _captureOwner = "";
    private static SampleNode _root = new() { Name = "Frame" };
    private static SampleNode _current;
    private static ConcurrentDictionary<string, WorkerSample> _workerSamples =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<(string File, string Member), string> _functionNames =
        new();

    public static bool Active => _active;
    public static string CaptureOwner
    {
        get
        {
            lock (CaptureLock)
                return _captureOwner;
        }
    }

    // Capture mutation is lease-scoped: only the successful starter may flush or stop it. A
    // competing sampler/editor start fails closed and receives no handle that could consume or
    // reset another owner's frame data.
    public static bool TryStartCapture(string owner, out CaptureLease lease)
    {
        lease = null;
        if (string.IsNullOrWhiteSpace(owner))
            return false;
        lock (CaptureLock)
        {
            if (_active)
                return false;
            _mainThreadId = System.Environment.CurrentManagedThreadId;
            Reset();
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
            Reset();
            return true;
        }
    }

    private static void Reset()
    {
        _root = new SampleNode { Name = "Frame" };
        _current = _root;
        _workerSamples = new ConcurrentDictionary<string, WorkerSample>(StringComparer.Ordinal);
    }

    public static ProfileScope Scope(string name)
    {
        if (!_active)
            return default;
        return BeginScope(name ?? "(null)");
    }

    // Auto-named scope: "NpcActor.Internals._PhysicsProcess" becomes "NpcActor.Internals._PhysicsProcess"
    // from the file name plus member, which stays correct across partial classes. The composed
    // string is cached per call site so the hot path does not allocate.
    public static ProfileScope Fn(
        [CallerFilePath] string filePath = "",
        [CallerMemberName] string memberName = "")
    {
        if (!_active)
            return default;
        var name = _functionNames.GetOrAdd((filePath, memberName),
            static key => ResolveFunctionName(key.File, key.Member));
        return BeginScope(name);
    }

    private static string ResolveFunctionName(string filePath, string memberName) =>
        $"{System.IO.Path.GetFileNameWithoutExtension(filePath)}.{memberName}";

    private static ProfileScope BeginScope(string name)
    {
        var threadId = System.Environment.CurrentManagedThreadId;
        if (threadId != _mainThreadId)
            return new ProfileScope(name, Stopwatch.GetTimestamp(), threadId);

        var parent = _current ?? _root;
        if (!parent.Children.TryGetValue(name, out var node))
        {
            node = new SampleNode { Name = name, Parent = parent };
            parent.Children[name] = node;
        }
        node.Calls++;
        node.StartTimestamp = Stopwatch.GetTimestamp();
        _current = node;
        return new ProfileScope(node, threadId);
    }

    internal static void EndMainThreadScope(SampleNode node)
    {
        if (!_active || node == null ||
            System.Environment.CurrentManagedThreadId != _mainThreadId)
            return;
        var current = _current;
        while (current != null && current != node)
            current = current.Parent;
        if (current == null)
            return;
        node.TotalTicks += Stopwatch.GetTimestamp() - node.StartTimestamp;
        _current = node.Parent ?? _root;
    }

    internal static void EndWorkerScope(string name, long startTimestamp)
    {
        if (!_active || startTimestamp <= 0)
            return;
        var elapsed = Stopwatch.GetTimestamp() - startTimestamp;
        var sample = _workerSamples.GetOrAdd(name, static _ => new WorkerSample());
        Interlocked.Increment(ref sample.Calls);
        Interlocked.Add(ref sample.Ticks, elapsed);
    }

    // Only the capture lease can call this destructive operation. It emits every node touched this
    // frame and zeroes its counters in place; the tree persists to avoid steady-state churn.
    private static FrameSnapshot TryFlushFrame(long token)
    {
        lock (CaptureLock)
        {
            if (!_active || token == 0 || token != _captureToken ||
                System.Environment.CurrentManagedThreadId != _mainThreadId)
                return FrameSnapshot.Empty;

            var names = new List<string>();
            var depths = new List<int>();
            var calls = new List<long>();
            var totalUsec = new List<long>();
            long csTotalTicks = 0;

            var stack = new Stack<(SampleNode Node, int Depth)>();
            foreach (var child in _root.Children.Values)
                stack.Push((child, 0));
            while (stack.Count > 0 && names.Count < MaxSnapshotNodes)
            {
                var (node, depth) = stack.Pop();
                if (node.Calls == 0)
                    continue;
                names.Add(node.Name);
                depths.Add(depth);
                calls.Add(node.Calls);
                totalUsec.Add(TicksToUsec(node.TotalTicks));
                if (depth == 0)
                    csTotalTicks += node.TotalTicks;
                node.Calls = 0;
                node.TotalTicks = 0;
                if (depth + 1 < MaxSnapshotDepth)
                {
                    foreach (var child in node.Children.Values)
                        stack.Push((child, depth + 1));
                }
            }

            AppendWorkerSamples(names, depths, calls, totalUsec);

            return new FrameSnapshot
            {
                Names = names.ToArray(),
                Depths = depths.ToArray(),
                Calls = calls.ToArray(),
                TotalUsec = totalUsec.ToArray(),
                CsTotalUsec = TicksToUsec(csTotalTicks)
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
            var sampleCalls = Interlocked.Exchange(ref pair.Value.Calls, 0);
            var sampleTicks = Interlocked.Exchange(ref pair.Value.Ticks, 0);
            if (sampleCalls > 0)
                workerRows.Add((pair.Key, sampleCalls, sampleTicks));
        }
        if (workerRows.Count == 0 || names.Count >= MaxSnapshotNodes)
            return;

        names.Add(WorkerThreadsRootName);
        depths.Add(0);
        calls.Add(workerRows.Sum(row => row.Calls));
        totalUsec.Add(workerRows.Sum(row => TicksToUsec(row.Ticks)));
        foreach (var row in workerRows.OrderBy(row => row.Name, StringComparer.Ordinal))
        {
            if (names.Count >= MaxSnapshotNodes)
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
        private readonly SampleNode _node;
        private readonly string _workerName;
        private readonly long _workerStart;
        private readonly int _threadId;

        internal ProfileScope(SampleNode node, int threadId)
        {
            _node = node;
            _workerName = null;
            _workerStart = 0;
            _threadId = threadId;
        }

        internal ProfileScope(string workerName, long workerStart, int threadId)
        {
            _node = null;
            _workerName = workerName;
            _workerStart = workerStart;
            _threadId = threadId;
        }

        public void Dispose()
        {
            if (_threadId == 0 || _threadId != System.Environment.CurrentManagedThreadId)
                return;
            if (_node != null)
                EndMainThreadScope(_node);
            else if (_workerName != null)
                EndWorkerScope(_workerName, _workerStart);
        }
    }
}
