using System.Diagnostics;
using Apeworks.GodotCSharpProfiler.Instrumentation;

internal static class BenchmarkFixture
{
        internal static (double inactiveNs, double enabledNs, long inactiveBytes) Measure(int iterations = 200_000)
    {
        for (var i = 0; i < 1000; i++) Call(i);
        var sw = Stopwatch.StartNew();
        var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < iterations; i++) Call(i);
        var stopped = Stopwatch.GetTimestamp();
        var inactiveBytes = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
        var inactive = (stopped - started) * (1_000_000_000.0 / Stopwatch.Frequency) / iterations;
        InstrumentationRecorder.StartCapture();
        sw.Restart();
        for (var i = 0; i < iterations; i++) Call(i);
        sw.Stop();
        var enabled = sw.Elapsed.TotalNanoseconds / iterations;
        InstrumentationRecorder.StopCapture();
        return (inactive, enabled, inactiveBytes);
    }

    private static void Call(int id) { var token = InstrumentationRecorder.Enter(id & 15); try { } finally { InstrumentationRecorder.Exit(token); } }
}
