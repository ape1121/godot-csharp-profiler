using System.Diagnostics;
using System.Runtime.CompilerServices;
using Apeworks.GodotCSharpProfiler.Runtime.Sampling;

namespace GodotCSharpProfiler.Sampling.Tests;

public sealed class ManagedSamplingSessionTests
{
    [Fact]
    public async Task StateTransitionsAndIdempotentStop()
    {
        await using var session = new ManagedSamplingSession(new SamplingOptions());
        Assert.Equal(ManagedSamplingSessionState.Stopped, session.State);
        await session.StartAsync();
        Assert.Equal(ManagedSamplingSessionState.Running, session.State);
        await Task.WhenAll(session.StopAsync(), session.StopAsync());
        Assert.Equal(ManagedSamplingSessionState.Stopped, session.State);
    }

    [Fact]
    public void AggregationIsBoundedAndReportsDropsAndTruncation()
    {
        var aggregator = new SamplingAggregator(new SamplingOptions
        {
            MaxUniqueMethods = 2, MaxUniqueStacks = 1, MaxStackDepth = 2, MaxLabelLength = 8
        });
        aggregator.AddSample("worker", new[]
        {
            new SamplingFrame("LongAssemblyName", "VeryLongMethodName"),
            new SamplingFrame("AssemblyB", "MethodB"),
            new SamplingFrame("AssemblyC", "MethodC")
        });
        aggregator.AddSample("worker", new[] { new SamplingFrame("AssemblyD", "MethodD") });
        aggregator.AddSample("worker", new[] { new SamplingFrame("AssemblyB", "MethodB") });
        var snapshot = aggregator.GetSnapshot(reset: false);
        Assert.True(snapshot.Methods.Count <= 2);
        Assert.Single(snapshot.Stacks);
        Assert.True(snapshot.Counters.DroppedMethods > 0);
        Assert.True(snapshot.Counters.DroppedStacks > 0);
        Assert.True(snapshot.Counters.TruncatedLabels > 0);
        Assert.True(snapshot.Counters.TruncatedFrames > 0);
        Assert.All(snapshot.Methods, method => Assert.True(method.Label.Length <= 8));
    }

    [Fact]
    public void AssemblyFiltersApplyIncludeThenExclude()
    {
        var aggregator = new SamplingAggregator(new SamplingOptions
        {
            IncludeAssemblyPrefixes = new[] { "Game", "Shared" },
            ExcludeAssemblyPrefixes = new[] { "Game.Generated" }
        });
        aggregator.AddSample("worker", new[]
        {
            new SamplingFrame("System.Private.CoreLib", "System.Object"),
            new SamplingFrame("Game.Generated.Proxy", "Proxy.Run"),
            new SamplingFrame("Game.Main", "Player.Tick"),
            new SamplingFrame("Shared.Utils", "Math.DoWork")
        });
        Assert.Equal(new[] { "Player.Tick", "Math.DoWork" },
            aggregator.GetSnapshot(reset: false).Methods.Select(method => method.Label));
    }

    [Fact]
    public void ExcludePrefixesAlsoRemoveProfilerNamespaceFrames()
    {
        var aggregator = new SamplingAggregator(new SamplingOptions
        {
            ExcludeAssemblyPrefixes = new[] { "Apeworks.GodotCSharpProfiler" }
        });
        aggregator.AddSample("worker", new[]
        {
            new SamplingFrame("ShopSimulator", "Apeworks.GodotCSharpProfiler.Runtime.Flush"),
            new SamplingFrame("ShopSimulator", "ShopSimulator.NpcManager.Tick")
        });

        Assert.Equal(new[] { "ShopSimulator.NpcManager.Tick" },
            aggregator.GetSnapshot(reset: false).Methods.Select(method => method.Label));
    }

    [Fact]
    public async Task OnlyOneSessionMayBeActive()
    {
        await using var first = new ManagedSamplingSession(new SamplingOptions());
        await using var second = new ManagedSamplingSession(new SamplingOptions());
        await first.StartAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => second.StartAsync());
        Assert.Contains("already active", error.Message, StringComparison.OrdinalIgnoreCase);
        await first.StopAsync();
        await second.StartAsync();
        await second.StopAsync();
    }

    [Fact]
    public async Task CancellationStopsSession()
    {
        using var cancellation = new CancellationTokenSource();
        await using var session = new ManagedSamplingSession(new SamplingOptions());
        await session.StartAsync(cancellation.Token);
        cancellation.Cancel();
        await WaitUntilAsync(() => session.State == ManagedSamplingSessionState.Stopped, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task SelfProcessSmokeObservesNamedManagedMethodOnLinux()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var session = new ManagedSamplingSession(new SamplingOptions
        {
            IncludeAssemblyPrefixes = new[] { "GodotCSharpProfiler.Sampling" },
            MaxUniqueMethods = 512, MaxUniqueStacks = 512
        });
        await session.StartAsync();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(3)) SamplingSmokeHotMethod();
        await session.StopAsync();
        var snapshot = session.GetSnapshot(reset: false);
        Assert.True(session.Fault is null, session.Fault?.ToString());
        Assert.Contains(snapshot.Methods, method =>
            method.Label.Contains(nameof(SamplingSmokeHotMethod), StringComparison.Ordinal));
    }

        [Fact]
    public void SampleIntervalCapabilityIsStartupOnlyAndNotAnOption()
    {
        var capabilities = ManagedSamplingSession.Capabilities;
        Assert.Equal(SampleIntervalConfigurationScope.ProcessStartup, capabilities.SampleIntervalScope);
        Assert.False(capabilities.SupportsPerSessionSampleInterval);
        Assert.False(capabilities.SupportsRuntimeSampleIntervalChanges);
        Assert.False(capabilities.CanReportEffectiveSampleInterval);
        Assert.Contains("DOTNET_EventPipeSamplingRate", capabilities.SampleIntervalRuntimeSetting);
        Assert.DoesNotContain(typeof(SamplingOptions).GetProperties(),
            property => property.Name.Contains("Interval", StringComparison.OrdinalIgnoreCase));
    }

            [Fact]
    public async Task RepeatedResetSnapshotsRenewTraceRetentionAndBoundManagedMemoryAndTempArtifacts()
    {
        if (!OperatingSystem.IsLinux()) return;
        var artifactsBefore = GetTraceArtifacts();
        await using var session = new ManagedSamplingSession(new SamplingOptions
        {
            IncludeAssemblyPrefixes = new[] { "GodotCSharpProfiler.Sampling" },
            MaxUniqueMethods = 128, MaxUniqueStacks = 128,
            TraceRetentionDuration = TimeSpan.FromSeconds(1),
            CircularBufferSizeMegabytes = 4
        });
        await session.StartAsync();
        var memoryByEpoch = new List<long>();
        var observedEpoch = 0;
        var resetSnapshots = 0;
        var totalObservedSamples = 0L;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(12))
        {
            for (var index = 0; index < 100; index++) SamplingSoakHotMethod();
            var snapshot = session.GetSnapshot(reset: true);
            resetSnapshots++;
            totalObservedSamples += snapshot.Counters.SamplesReceived;
            if (session.TraceEpochCount != observedEpoch)
            {
                observedEpoch = session.TraceEpochCount;
                GC.Collect();
                GC.WaitForPendingFinalizers();
                memoryByEpoch.Add(GC.GetTotalMemory(forceFullCollection: true));
            }
        }
        await session.StopAsync();
        Assert.True(session.Fault is null, session.Fault?.ToString());
        Assert.True(resetSnapshots > 1_000, $"Only {resetSnapshots} reset snapshots were taken.");
        Assert.True(totalObservedSamples > 1_000, $"Only {totalObservedSamples} samples were observed.");
        Assert.True(session.TraceEpochCount >= 6, $"Only {session.TraceEpochCount} trace epochs were renewed.");
        Assert.True(memoryByEpoch.Count >= 6, "Insufficient epoch memory observations.");
        var latterHalf = memoryByEpoch.Skip(memoryByEpoch.Count / 2).ToArray();
        Assert.True(latterHalf.Max() - latterHalf.Min() < 32 * 1024 * 1024,
            $"Managed memory did not plateau: {string.Join(", ", memoryByEpoch)}");
        Assert.Equal(artifactsBefore, GetTraceArtifacts());
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static long SamplingSmokeHotMethod() => SamplingSoakHotMethod();

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static long SamplingSoakHotMethod()
    {
        long value = 17;
        for (var i = 0; i < 10_000; i++) value = unchecked(value * 31 + i);
        return value;
    }

    private static HashSet<string> GetTraceArtifacts() =>
        Directory.EnumerateFiles(Path.GetTempPath())
            .Where(file => file.EndsWith(".etlx", StringComparison.OrdinalIgnoreCase) ||
                           file.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.Ordinal);

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            Assert.True(stopwatch.Elapsed < timeout, "Timed out waiting for session state.");
            await Task.Delay(20);
        }
    }
}
