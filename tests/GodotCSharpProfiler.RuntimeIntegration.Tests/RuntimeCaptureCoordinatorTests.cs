using Apeworks.GodotCSharpProfiler.Protocol;
using Apeworks.GodotCSharpProfiler.Runtime.Protocol.Adapters;
using Xunit;

namespace GodotCSharpProfiler.RuntimeIntegration.Tests;

public sealed class RuntimeCaptureCoordinatorTests
{
    private const string Token = "runtime-token";
    private const string Fingerprint = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void LifecycleUsesOneLeaseAndLeavesBackendInactive()
    {
        var (runtime, transport, backend) = Runtime();
        runtime.Connect();
        Assert.Equal(2, transport.Messages.Count);
        Assert.True(runtime.Receive(Configure(1, CaptureModes.ManualScopes), "owner"));
        Assert.True(runtime.Receive(Start(1), "owner"));
        Assert.True(runtime.Capturing);
        Assert.False(runtime.Receive(Start(1), "other"));
        Assert.True(runtime.Receive(Stop(1, 2), "owner"));
        Assert.False(runtime.Capturing);
        Assert.Equal(1, backend.Starts);
        Assert.Equal(1, backend.Stops);
        runtime.Dispose();
        Assert.False(backend.IsActive);
    }

    [Fact]
    public void MalformedStaleDuplicateGapAndWrongFingerprintAreInert()
    {
        var (runtime, _, backend) = Runtime();
        runtime.Connect();
        Assert.False(runtime.Receive(new Dictionary<string, object?> { ["kind"] = "start" }, "owner"));
        Assert.True(runtime.Receive(Configure(2, CaptureModes.ManualScopes), "owner"));
        Assert.False(runtime.Receive(Configure(1, CaptureModes.ManualScopes), "owner"));
        Assert.False(runtime.Receive(Start(1), "owner"));
        Assert.True(runtime.Receive(Start(2), "owner"));
        Assert.False(runtime.Receive(Stop(2, 1), "owner")); // capturing state is sequence 1, duplicate
        Assert.False(runtime.Receive(Stop(2, 3), "owner")); // gap
        Assert.False(runtime.Receive(Stop(2, 2, new string('f', 32)), "owner"));
        Assert.True(backend.IsActive);
        Assert.True(runtime.Receive(Stop(2, 2), "owner"));
    }

    [Fact]
    public void MixedSourcesRemainSeparateAndNeverSumSemantics()
    {
        var (runtime, transport, backend) = Runtime(CaptureModes.AutomaticInstrumentation | CaptureModes.ManualScopes);
        backend.Pending.Add(new(CaptureSource.AutomaticSpans, true, false, QualityCounters.Zero,
            [new(7, "Game.Run", 50, 2)]));
        backend.Pending.Add(new(CaptureSource.ManualSpans, true, false, QualityCounters.Zero,
            [new(7, "Gameplay/Run", 90, 1)]));
        runtime.Connect();
        Assert.True(runtime.Receive(Configure(1, CaptureModes.AutomaticInstrumentation | CaptureModes.ManualScopes), "owner"));
        Assert.True(runtime.Receive(Start(1), "owner"));
        runtime.Flush();
        var batches = transport.Parsed.OfType<BatchMessage>().ToArray();
        Assert.Equal(2, batches.Length);
        Assert.Equal([CaptureSource.AutomaticSpans, CaptureSource.ManualSpans], batches.Select(value => value.Source));
        Assert.Equal([50L, 90L], batches.Select(value => value.Methods.Single().Value));
    }

    [Fact]
    public void BatchesAreBoundedByConfiguredCardinalityAndWireBytes()
    {
        var (runtime, transport, backend) = Runtime();
        backend.Pending.Add(new(CaptureSource.ManualSpans, true, false, QualityCounters.Zero,
            Enumerable.Range(0, 25).Select(i => new MethodSample(i,
                new string((char)('a' + i % 26), ProtocolLimits.MaxMethodLabelCharacters), i, 1)).ToArray()));
        runtime.Connect();
        Assert.True(runtime.Receive(Configure(1, CaptureModes.ManualScopes, maxMethods: 4), "owner"));
        Assert.True(runtime.Receive(Start(1), "owner"));
        runtime.Flush();
        var batches = transport.Parsed.OfType<BatchMessage>().ToArray();
        Assert.Equal(7, batches.Length);
        Assert.All(batches, batch => Assert.InRange(batch.Methods.Count, 1, 4));
        Assert.All(transport.Messages, message => Assert.True(StrictWireAdapter.MeasureBytes(message) <= ProtocolLimits.MaxPayloadBytes));
    }

    [Fact]
    public void ConfigureTransportsSamplingFiltersAndManualPrefixToBackend()
    {
        var (runtime, _, backend) = Runtime();
        runtime.Connect();
        Assert.True(runtime.Receive(Configure(1, CaptureModes.Sampling | CaptureModes.ManualScopes,
            include: "Game;Core", exclude: "System", manualPrefix: "Gameplay/"), "owner"));
        Assert.True(runtime.Receive(Start(1), "owner"));
        Assert.Equal("Game;Core", backend.LastConfiguration!.SamplingIncludeAssemblies);
        Assert.Equal("System", backend.LastConfiguration.SamplingExcludeAssemblies);
        Assert.Equal("Gameplay/", backend.LastConfiguration.ManualLabelPrefix);
    }

    [Fact]
    public void SplitBatchEmitsQualityOnceAndTerminalPreservesAccumulatedCounters()
    {
        var (runtime, transport, backend) = Runtime();
        var quality = new QualityCounters(9, 2, 3, 4);
        backend.Pending.Add(new(CaptureSource.ManualSpans, true, false, quality,
            Enumerable.Range(0, 9).Select(i => new MethodSample(i,
                new string('x', ProtocolLimits.MaxMethodLabelCharacters), 1, 1)).ToArray()));
        runtime.Connect();
        Assert.True(runtime.Receive(Configure(1, CaptureModes.ManualScopes, maxMethods: 2), "owner"));
        Assert.True(runtime.Receive(Start(1), "owner"));
        runtime.Flush();
        Assert.True(runtime.Receive(Stop(1, runtime.Sequence + 1), "owner"));

        var batches = transport.Parsed.OfType<BatchMessage>().ToArray();
        Assert.Equal(quality, batches.Select(batch => batch.Quality)
            .Aggregate(QualityCounters.Zero, (sum, delta) => sum.Add(delta)));
        Assert.Equal(quality, transport.Parsed.OfType<StateMessage>().Last().Quality);
    }

    [Fact]
    public void DisconnectAndDisposeReleaseOnlyOwnedCapture()
    {
        var (runtime, _, backend) = Runtime();
        runtime.Connect();
        runtime.Receive(Configure(1, CaptureModes.ManualScopes), "owner");
        runtime.Receive(Start(1), "owner");
        runtime.Disconnect();
        Assert.False(backend.IsActive);
        Assert.Equal(1, backend.Stops);
        runtime.Dispose();
        Assert.Equal(1, backend.Stops);

        var (_, _, idleBackend) = Runtime();
        runtime = new RuntimeCaptureCoordinator(Token, new FakeTransport(), idleBackend);
        runtime.Connect();
        runtime.Dispose();
        Assert.Equal(0, idleBackend.Stops);
    }

    [Fact]
    public void SamplingAndAutomaticAreExclusiveWhileManualOverlayIsAllowed()
    {
        var (runtime, _, _) = Runtime(CaptureModes.Sampling | CaptureModes.AutomaticInstrumentation | CaptureModes.ManualScopes);
        runtime.Connect();
        Assert.False(runtime.Receive(Configure(1, CaptureModes.Sampling | CaptureModes.AutomaticInstrumentation), "owner"));
        Assert.True(runtime.Receive(Configure(1, CaptureModes.Sampling | CaptureModes.ManualScopes), "owner"));
    }

    [Fact]
    public void StrictAdapterRejectsObjectsNullAndExcessDepth()
    {
        Assert.False(StrictWireAdapter.TryConvert(new object(), out _));
        Assert.False(StrictWireAdapter.TryConvert(null, out _));
        object value = 1L;
        for (var i = 0; i < ProtocolLimits.MaxDepth + 2; i++) value = new object[] { value };
        Assert.False(StrictWireAdapter.TryConvert(value, out _));
    }

    private static (RuntimeCaptureCoordinator Runtime, FakeTransport Transport, FakeBackend Backend) Runtime(
        CaptureModes modes = CaptureModes.Sampling | CaptureModes.AutomaticInstrumentation | CaptureModes.ManualScopes)
    {
        var transport = new FakeTransport();
        var backend = new FakeBackend(modes);
        return (new RuntimeCaptureCoordinator(Token, transport, backend), transport, backend);
    }

    private static WireMap Configure(long generation, CaptureModes modes, int maxMethods = 64,
        string include = "", string exclude = "", string manualPrefix = "") =>
        StrictWireAdapter.Serialize(new ConfigureMessage(ProtocolVersion.Major, ProtocolVersion.Minor, Token, generation, Fingerprint, modes, 0,
            maxMethods, include, exclude, manualPrefix));
    private static WireMap Start(long generation) => StrictWireAdapter.Serialize(new StartMessage(ProtocolVersion.Major, ProtocolVersion.Minor, Token, generation, Fingerprint));
    private static WireMap Stop(long generation, long sequence, string fingerprint = Fingerprint) =>
        StrictWireAdapter.Serialize(new StopMessage(ProtocolVersion.Major, ProtocolVersion.Minor, Token, generation, sequence, fingerprint));

    private sealed class FakeTransport : IRuntimeCaptureTransport
    {
        public List<WireMap> Messages { get; } = [];
        public IEnumerable<ProtocolMessage> Parsed => Messages.Select(message =>
        {
            Assert.True(new CaptureProtocolParser().TryParse(message, out var parsed, out var failure), failure.ToString());
            return parsed!;
        });
        public void Send(WireMap message) => Messages.Add(message);
    }

    private sealed class FakeBackend(CaptureModes modes) : IRuntimeCaptureBackend
    {
        public RuntimeBackendCapabilities Capabilities { get; } = new(modes, false, 0, 128, "test", "test");
        public bool IsActive { get; private set; }
        public int Starts { get; private set; }
        public int Stops { get; private set; }
        public List<RuntimeSourceBatch> Pending { get; } = [];
        public RuntimeCaptureConfiguration? LastConfiguration { get; private set; }
        public bool TryStart(RuntimeCaptureConfiguration configuration, string owner, out string? error)
        { LastConfiguration = configuration; _ = owner; error = null; Starts++; IsActive = true; return true; }
        public IReadOnlyList<RuntimeSourceBatch> Drain() { var value = Pending.ToArray(); Pending.Clear(); return value; }
        public IReadOnlyList<RuntimeSourceBatch> Stop() { if (!IsActive) return []; Stops++; IsActive = false; return Drain(); }
        public void Dispose() { if (IsActive) Stop(); }
    }
}
