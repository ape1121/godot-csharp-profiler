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
        var options = new SamplingOptions
        {
            MaxUniqueMethods = 2,
            MaxUniqueStacks = 1,
            MaxStackDepth = 2,
            MaxLabelLength = 8
        };
        var aggregator = new SamplingAggregator(options);

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
        Assert.Throws<NotSupportedException>(() =>
            ((IList<SampledMethod>)snapshot.Methods).Add(new SampledMethod(99, "x", "x", 1)));
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

        var snapshot = aggregator.GetSnapshot(reset: false);
        Assert.Equal(new[] { "Player.Tick", "Math.DoWork" }, snapshot.Methods.Select(m => m.Label));
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
        if (!OperatingSystem.IsLinux())
            return;

        await using var session = new ManagedSamplingSession(new SamplingOptions
        {
            IncludeAssemblyPrefixes = new[] { "GodotCSharpProfiler.Sampling" },
            MaxUniqueMethods = 512,
            MaxUniqueStacks = 512
        });

        await session.StartAsync();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(3))
            SamplingSmokeHotMethod();
        await session.StopAsync();

        var snapshot = session.GetSnapshot(reset: false);
        Assert.True(session.Fault is null, session.Fault?.ToString());
        Assert.Contains(snapshot.Methods, method =>
            method.Label.Contains(nameof(SamplingSmokeHotMethod), StringComparison.Ordinal));
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static long SamplingSmokeHotMethod()
    {
        long value = 0;
        for (var i = 0; i < 100_000; i++)
            value = unchecked(value * 31 + i);
        return value;
    }

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
