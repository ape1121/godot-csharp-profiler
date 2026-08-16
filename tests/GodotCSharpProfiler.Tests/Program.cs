using Apeworks.GodotCSharpProfiler;
using System.Collections.Concurrent;
using System.Diagnostics;

internal static class Program
{
    private static readonly (string Name, Action Test)[] Tests =
    {
        (nameof(ScopeSpanningFlushIsDeferredWithoutCorruption), ScopeSpanningFlushIsDeferredWithoutCorruption),
        (nameof(WorkerScopeFromGenerationACannotEnterB), WorkerScopeFromGenerationACannotEnterB),
        (nameof(CrossThreadDisposalCannotPoisonNesting), CrossThreadDisposalCannotPoisonNesting),
        (nameof(DynamicLabelsAndRetainedPathsAreBounded), DynamicLabelsAndRetainedPathsAreBounded),
        (nameof(MalformedReadyPayloadFailsClosed), MalformedReadyPayloadFailsClosed),
        (nameof(CaptureStopAndResetAreExact), CaptureStopAndResetAreExact),
    };

    private static int Main()
    {
        var failures = 0;
        foreach (var (name, test) in Tests)
        {
            try
            {
                EnsureStopped();
                test();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception error)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {name}: {error.Message}");
            }
            finally
            {
                EnsureStopped();
            }
        }
        Console.WriteLine($"{Tests.Length - failures}/{Tests.Length} passed");
        return failures == 0 ? 0 : 1;
    }

    private static void ScopeSpanningFlushIsDeferredWithoutCorruption()
    {
        True(CsProfiler.TryStartCapture("flush", out var lease), "capture starts");
        using var capture = lease;
        var spanning = CsProfiler.Scope("spanning");
        Thread.Sleep(2);
        var during = lease.FlushFrame();
        Equal(0, during.Names.Length, "open scope is not emitted as a zero-time/corrupt row");
        spanning.Dispose();
        using (CsProfiler.Scope("after")) Thread.Sleep(1);
        var completed = lease.FlushFrame();
        Equal(1L, Calls(completed, "spanning"), "spanning scope emitted exactly once when closed");
        Equal(1L, Calls(completed, "after"), "subsequent root nesting remains correct");
        True(Usec(completed, "spanning") > 0, "spanning duration is retained");
    }

    private static void WorkerScopeFromGenerationACannotEnterB()
    {
        True(CsProfiler.TryStartCapture("A", out var captureA), "A starts");
        var disposeA = new ManualResetEventSlim();
        var openedA = new ManualResetEventSlim();
        var worker = Task.Run(() =>
        {
            var oldScope = CsProfiler.Scope("old-worker");
            openedA.Set();
            disposeA.Wait();
            oldScope.Dispose();
        });
        openedA.Wait();
        True(captureA.Stop(), "A stops");
        True(CsProfiler.TryStartCapture("B", out var captureB), "B starts");
        using var capture = captureB;
        disposeA.Set();
        worker.GetAwaiter().GetResult();
        var snapshot = captureB.FlushFrame();
        Equal(0L, Calls(snapshot, "old-worker"), "generation A work is rejected by B");
    }

    private static void CrossThreadDisposalCannotPoisonNesting()
    {
        True(CsProfiler.TryStartCapture("cross-thread", out var lease), "capture starts");
        using var capture = lease;
        var outer = CsProfiler.Scope("outer");
        Task.Run(() => outer.Dispose()).GetAwaiter().GetResult();
        outer.Dispose();
        using (CsProfiler.Scope("next")) Thread.Sleep(1);
        var snapshot = lease.FlushFrame();
        Equal(0, Depth(snapshot, "next"), "later scope is still rooted");
        Equal(1L, Calls(snapshot, "outer"), "outer closes once");
        Equal(1L, Calls(snapshot, "next"), "later scope is retained");
    }

    private static void DynamicLabelsAndRetainedPathsAreBounded()
    {
        True(CsProfiler.TryStartCapture("bounds", out var lease), "capture starts");
        using var capture = lease;
        for (var index = 0; index < CsProfiler.MaximumRetainedNodes + 200; index++)
        {
            using var scope = CsProfiler.Scope(index + "-" + new string('x', CsProfiler.MaximumLabelLength + 20));
        }
        var snapshot = lease.FlushFrame();
        True(snapshot.Names.Length <= CsProfiler.MaximumRetainedNodes, "snapshot cardinality bounded");
        True(snapshot.Names.All(name => name.Length <= CsProfiler.MaximumLabelLength), "labels bounded");
        True(snapshot.DroppedScopes > 0, "dropped scopes diagnosed");
        True(snapshot.TruncatedLabels > 0, "truncated labels diagnosed");

        for (var frame = 0; frame < 3; frame++)
        {
            for (var index = 0; index < CsProfiler.MaximumRetainedNodes + 50; index++)
                using (CsProfiler.Scope($"frame-{frame}-label-{index}")) { }
            lease.FlushFrame();
        }
        True(CsProfiler.RetainedNodeCount <= CsProfiler.MaximumRetainedNodes,
            "retained paths cannot grow without bound across frames");
    }

    private static void MalformedReadyPayloadFailsClosed()
    {
        var valid = new Godot.Collections.Array("token", 42L, true, "game", "Game", false);
        True(CsProfilerRuntimeIdentity.TryFromWire(valid, out var identity), "valid payload accepted");
        Equal("token", identity.RuntimeToken, "valid identity parsed");

        Godot.Collections.Array[] malformed =
        {
            null,
            new("too-short"),
            new(123L, 42L, true, "game", "Game", false),
            new("token", "not-long", true, "game", "Game", false),
            new("token", 42L, "not-bool", "game", "Game", false),
            new("token", 42L, true, 123L, "Game", false),
            new("token", 42L, true, "game", 123L, false),
            new("token", 42L, true, "game", "Game", "not-bool"),
        };
        foreach (var payload in malformed)
        {
            False(CsProfilerRuntimeIdentity.TryFromWire(payload, out var rejected), "malformed payload rejected");
            True(ReferenceEquals(CsProfilerRuntimeIdentity.Unknown, rejected), "rejection returns Unknown");
        }
    }

    private static void CaptureStopAndResetAreExact()
    {
        True(CsProfiler.TryStartCapture("first", out var first), "first starts");
        using (CsProfiler.Scope("first-data")) { }
        True(first.Stop(), "first stop succeeds exactly once");
        False(first.Stop(), "second stop is inert");
        False(first.IsActive, "old lease inactive");
        Equal("", CsProfiler.CaptureOwner, "owner cleared");
        False(CsProfiler.Active, "capture inactive");

        True(CsProfiler.TryStartCapture("second", out var second), "second starts");
        Equal(0, second.FlushFrame().Names.Length, "restart contains no previous data");
        False(first.Stop(), "stale lease cannot stop second");
        True(second.IsActive, "second remains active");
        True(second.Stop(), "second stops");
        Equal(0, CsProfiler.RetainedNodeCount, "stop resets retained tree exactly");
    }

    private static long Calls(CsProfiler.FrameSnapshot snapshot, string name)
    {
        var index = System.Array.IndexOf(snapshot.Names, name);
        return index < 0 ? 0 : snapshot.Calls[index];
    }

    private static long Usec(CsProfiler.FrameSnapshot snapshot, string name)
    {
        var index = System.Array.IndexOf(snapshot.Names, name);
        return index < 0 ? 0 : snapshot.TotalUsec[index];
    }

    private static int Depth(CsProfiler.FrameSnapshot snapshot, string name)
    {
        var index = System.Array.IndexOf(snapshot.Names, name);
        return index < 0 ? -1 : snapshot.Depths[index];
    }

    private static void EnsureStopped()
    {
        if (!CsProfiler.Active)
            return;
        if (CsProfiler.TryStartCapture("cleanup", out var impossible))
            impossible.Stop();
        // Tests only stop captures through their leases. Reaching here indicates a failed test before cleanup.
    }

    private static void True(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static void False(bool value, string message) => True(!value, message);

    private static void Equal<T>(T expected, T actual, string message) where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }
}
