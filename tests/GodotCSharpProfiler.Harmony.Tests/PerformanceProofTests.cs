using System.Diagnostics;
using System.Reflection;
using GodotCSharpProfiler.HarmonyInstrumentation;
using HarmonyProofFixtures;
using Xunit;

namespace GodotCSharpProfiler.Harmony.Tests;

[Collection("Harmony")]
public sealed class PerformanceProofTests
{
    private const int Iterations = 250_000;

    [Fact]
    public void Measure_patch_startup_and_disabled_enabled_overhead()
    {
        using var instrumentor = new HarmonyFilteredInstrumentor(new InstrumentationOptions
        {
            OwnerId = $"proof.performance.{Guid.NewGuid():N}",
            SelectedTypes = [typeof(MethodFixture)],
            MaxMethods = 128,
            MaxNameLength = 100,
            TrivialIlByteThreshold = 2
        });
        var fixture = new MethodFixture();
        var method = typeof(MethodFixture).GetMethod(nameof(MethodFixture.Ordinary), [typeof(int)])!;

        Warmup(fixture);
        var baseline = Measure(fixture);
        var patchMetrics = instrumentor.Patch();
        HarmonyFilteredInstrumentor.Enabled = false;
        var disabled = Measure(fixture);
        HarmonyFilteredInstrumentor.Enabled = true;
        instrumentor.ResetMeasurements();
        var enabled = Measure(fixture);

        var measurement = instrumentor.GetMeasurement(method);
        Assert.Equal(Iterations, measurement.Calls);
        Assert.True(patchMetrics.PatchedMethodCount > 0);
        Assert.True(patchMetrics.PatchStartup > TimeSpan.Zero);

        Console.WriteLine($"METRIC patch_startup_ms={patchMetrics.PatchStartup.TotalMilliseconds:F6}");
        Console.WriteLine($"METRIC patched_methods={patchMetrics.PatchedMethodCount}");
        Console.WriteLine($"METRIC baseline_ns_per_call={baseline:F3}");
        Console.WriteLine($"METRIC disabled_ns_per_call={disabled:F3}");
        Console.WriteLine($"METRIC enabled_ns_per_call={enabled:F3}");
        Console.WriteLine($"METRIC disabled_overhead_ns_per_call={disabled - baseline:F3}");
        Console.WriteLine($"METRIC enabled_overhead_ns_per_call={enabled - baseline:F3}");
    }

    private static void Warmup(MethodFixture fixture)
    {
        for (var index = 0; index < 10_000; index++)
        {
            _ = fixture.Ordinary(index);
        }
    }

    private static double Measure(MethodFixture fixture)
    {
        var checksum = 0;
        var stopwatch = Stopwatch.StartNew();
        for (var index = 0; index < Iterations; index++)
        {
            checksum += fixture.Ordinary(index);
        }

        stopwatch.Stop();
        GC.KeepAlive(checksum);
        return stopwatch.Elapsed.TotalNanoseconds / Iterations;
    }
}
