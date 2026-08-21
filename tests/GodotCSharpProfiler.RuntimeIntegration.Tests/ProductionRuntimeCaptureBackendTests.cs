using Apeworks.GodotCSharpProfiler.Protocol;
using Apeworks.GodotCSharpProfiler.Runtime.Protocol.Adapters;
using Apeworks.GodotCSharpProfiler.Runtime.Sampling;
using Xunit;

namespace GodotCSharpProfiler.RuntimeIntegration.Tests;

public sealed class ProductionRuntimeCaptureBackendTests
{
    [Fact]
    public void SamplingFiltersAreAppliedAndResetLocalIdsNeverAlias()
    {
        var lease = new FakeSamplingLease(
            Snapshot(new SampledMethod(0, "Game", "Game.First", 2)),
            Snapshot(new SampledMethod(0, "Game", "Game.Second", 3)));
        using var backend = new ProductionRuntimeCaptureBackend(options =>
        {
            lease.Options = options;
            return lease;
        }, null);
        var configuration = new RuntimeCaptureConfiguration(1, new string('a', 32), CaptureModes.Sampling,
            0, 16, "Game;Core", "System", "");

        Assert.True(backend.TryStart(configuration, "owner", out var error), error);
        Assert.Equal(["Game", "Core"], lease.Options!.IncludeAssemblyPrefixes);
        Assert.Equal(["System"], lease.Options.ExcludeAssemblyPrefixes);
        var first = backend.Drain().Single().Methods.Single();
        var second = backend.Drain().Single().Methods.Single();
        Assert.Equal("Game.First", first.Label);
        Assert.Equal("Game.Second", second.Label);
        Assert.NotEqual(first.MethodId, second.MethodId);
    }

    [Fact]
    public void SamplingStopFailureRetainsLeaseOwnershipUntilRetryQuiescesIt()
    {
        var lease = new FakeSamplingLease { StopFailure = new InvalidOperationException("still active") };
        using var backend = new ProductionRuntimeCaptureBackend(_ => lease, null);
        var configuration = new RuntimeCaptureConfiguration(1, new string('a', 32), CaptureModes.Sampling,
            0, 16, "", "", "");
        Assert.True(backend.TryStart(configuration, "owner", out var startError), startError);

        Assert.Throws<InvalidOperationException>(() => backend.Stop());
        Assert.True(backend.IsActive);
        Assert.False(backend.TryStart(configuration with { Generation = 2 }, "replacement", out _));
        Assert.Equal(0, lease.Disposals);

        lease.StopFailure = null;
        backend.Stop();
        Assert.False(backend.IsActive);
        Assert.Equal(2, lease.Stops);
        Assert.Equal(1, lease.Disposals);
    }

    [Fact]
    public void SamplingIsNotStartedWhenTheBoundedManualLeaseCannotBeAcquired()
    {
        Assert.True(Apeworks.GodotCSharpProfiler.CsProfiler.TryStartCapture(
            "existing-owner", out var existing));
        try
        {
            var lease = new FakeSamplingLease();
            using var backend = new ProductionRuntimeCaptureBackend(_ => lease, null);
            var configuration = new RuntimeCaptureConfiguration(1, new string('a', 32),
                CaptureModes.Sampling | CaptureModes.ManualScopes,
                0, 16, "", "", "");

            Assert.False(backend.TryStart(configuration, "new-owner", out var error));
            Assert.Contains("Manual profiler lease", error, StringComparison.Ordinal);
            Assert.Equal(0, lease.Starts);
            Assert.False(backend.IsActive);
        }
        finally
        {
            existing.Dispose();
        }
    }

    [Fact]
    public async Task SamplingStopAsyncRetainsOwnershipUntilAcknowledgedAndReportsIncomplete()
    {
        var lease = new FakeSamplingLease { BlockStopAsync = true, StopDataIncomplete = true };
        using var backend = new ProductionRuntimeCaptureBackend(_ => lease, null);
        var configuration = new RuntimeCaptureConfiguration(1, new string('a', 32), CaptureModes.Sampling,
            0, 16, "", "", "");
        Assert.True(backend.TryStart(configuration, "owner", out var startError), startError);

        var stop = ((IRuntimeCaptureBackend)backend).StopAsync();
        await lease.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(stop.IsCompleted);
        Assert.True(backend.IsActive);
        Assert.Equal(0, lease.Disposals);

        lease.StopAcknowledged.TrySetResult(true);
        var result = await stop.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.DataIncomplete);
        Assert.False(backend.IsActive);
        Assert.Equal(1, lease.Stops);
        Assert.Equal(1, lease.Disposals);
    }

    [Fact]
    public async Task DisposeBeginsSamplingTeardownWithoutWaitingForAcknowledgement()
    {
        var lease = new FakeSamplingLease { BlockStopAsync = true };
        var backend = new ProductionRuntimeCaptureBackend(_ => lease, null);
        var configuration = new RuntimeCaptureConfiguration(1, new string('a', 32), CaptureModes.Sampling,
            0, 16, "", "", "");
        Assert.True(backend.TryStart(configuration, "owner", out var startError), startError);

        var dispose = Task.Run(backend.Dispose);
        await lease.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            Assert.Same(dispose, await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(1))));
            Assert.True(backend.IsActive);
        }
        finally
        {
            lease.StopAcknowledged.TrySetResult(true);
        }

        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        for (var attempt = 0; attempt < 100 && backend.IsActive; attempt++) await Task.Delay(1);
        Assert.False(backend.IsActive);
        Assert.Equal(1, lease.Disposals);
    }

    [Fact]
    public void ManualLabelPrefixIsAppliedToTransportedLabels()
    {
        using var backend = new ProductionRuntimeCaptureBackend(null, null);
        var configuration = new RuntimeCaptureConfiguration(1, new string('a', 32), CaptureModes.ManualScopes,
            0, 16, "", "", "Gameplay/");
        Assert.True(backend.TryStart(configuration, "owner", out var error), error);
        using (Apeworks.GodotCSharpProfiler.CsProfiler.Scope("Tick")) { }

        var method = backend.Drain().Single().Methods.Single();
        Assert.Equal("Gameplay/Tick", method.Label);
    }

    private static SamplingSnapshot Snapshot(params SampledMethod[] methods) => new(DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow, methods, [], new SamplingCounters(1, 1, 0, 0, 0, 0, 0, 0, 0));

    private sealed class FakeSamplingLease(params SamplingSnapshot[] snapshots) : ProductionRuntimeCaptureBackend.IManagedSamplingLease
    {
        public Exception? Fault => null;
        private readonly Queue<SamplingSnapshot> _snapshots = new(snapshots);
        public SamplingOptions? Options { get; set; }
        public Exception? StopFailure { get; set; }
        public bool BlockStopAsync { get; set; }
        public bool StopDataIncomplete { get; set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public int Disposals { get; private set; }
        public TaskCompletionSource<bool> StopEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> StopAcknowledged { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Start() => Starts++;
        public void Stop()
        {
            Stops++;
            if (StopFailure is not null) throw StopFailure;
        }
        public async Task<bool> StopAsync()
        {
            Stops++;
            StopEntered.TrySetResult(true);
            if (StopFailure is not null) throw StopFailure;
            if (BlockStopAsync) await StopAcknowledged.Task;
            return StopDataIncomplete;
        }
        public SamplingSnapshot Snapshot(bool reset) => _snapshots.Count == 0
            ? SamplingSnapshot.Empty(DateTimeOffset.UtcNow) : _snapshots.Dequeue();
        public void Dispose() => Disposals++;
    }
}