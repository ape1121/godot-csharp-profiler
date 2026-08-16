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
        private readonly Queue<SamplingSnapshot> _snapshots = new(snapshots);
        public SamplingOptions? Options { get; set; }
        public void Start() { }
        public void Stop() { }
        public SamplingSnapshot Snapshot(bool reset) => _snapshots.Count == 0
            ? SamplingSnapshot.Empty(DateTimeOffset.UtcNow) : _snapshots.Dequeue();
        public void Dispose() { }
    }
}