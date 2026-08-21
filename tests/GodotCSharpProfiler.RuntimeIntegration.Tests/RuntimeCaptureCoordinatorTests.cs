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
    public void OrphanResetRequiresExactOwnerAndGenerationBeforeFreshCapture()
    {
        var (runtime, transport, backend) = Runtime();
        runtime.Connect();
        Assert.True(runtime.Receive(Configure(4, CaptureModes.ManualScopes), "owner"));
        Assert.True(runtime.Receive(Start(4), "owner"));

        const string requestId = "11111111111111111111111111111111";
        Assert.False(runtime.Receive(Reset(4, requestId), "other"));
        Assert.False(runtime.Receive(Reset(3, requestId), "owner"));
        Assert.True(runtime.Capturing);

        Assert.True(runtime.Receive(Reset(4, requestId), "owner"));
        Assert.False(runtime.Capturing);
        Assert.Equal(1, backend.Stops);
        var acknowledgement = Assert.IsType<ResetAckMessage>(transport.Parsed.Last());
        Assert.Equal((4L, requestId), (acknowledgement.Generation, acknowledgement.RequestId));

        Assert.True(runtime.Receive(Configure(5, CaptureModes.ManualScopes), "owner"));
        Assert.True(runtime.Receive(Start(5), "owner"));
        Assert.False(runtime.Receive(Reset(4, requestId), "owner"));
        Assert.True(runtime.Capturing);
    }

    [Fact]
    public void ResetAcknowledgementIsIdempotentForTheSameOwnerGenerationAndRequest()
    {
        var (runtime, transport, backend) = Runtime();
        runtime.Connect();
        Assert.True(runtime.Receive(Configure(4, CaptureModes.ManualScopes), "owner"));
        Assert.True(runtime.Receive(Start(4), "owner"));
        const string requestId = "11111111111111111111111111111111";

        Assert.True(runtime.Receive(Reset(4, requestId), "owner"));
        Assert.True(runtime.Receive(Reset(4, requestId), "owner"));

        Assert.Equal(1, backend.Stops);
        var receipts = transport.Parsed.OfType<ResetAckMessage>().ToArray();
        Assert.Equal(2, receipts.Length);
        Assert.All(receipts, receipt => Assert.Equal((4L, requestId),
            (receipt.Generation, receipt.RequestId)));
        Assert.False(runtime.Receive(Reset(4, "22222222222222222222222222222222"), "owner"));
    }

    [Fact]
    public void ResetFailureDoesNotAcknowledgeOrReleaseTheActiveLease()
    {
        var (runtime, transport, backend) = Runtime();
        runtime.Connect();
        Assert.True(runtime.Receive(Configure(4, CaptureModes.ManualScopes), "owner"));
        Assert.True(runtime.Receive(Start(4), "owner"));
        backend.StopFailure = new InvalidOperationException("stop failed");
        const string requestId = "11111111111111111111111111111111";

        Assert.False(runtime.Receive(Reset(4, requestId), "owner"));
        Assert.True(runtime.Capturing);
        Assert.Equal("owner", runtime.LeaseOwner);
        Assert.DoesNotContain(transport.Parsed, message => message is ResetAckMessage);

        backend.StopFailure = null;
        Assert.False(runtime.Receive(Reset(4, "22222222222222222222222222222222"), "owner"));
        Assert.False(runtime.Receive(Reset(4, requestId), "other"));
        Assert.True(runtime.Receive(Reset(4, requestId), "owner"));
        Assert.False(runtime.Capturing);
        Assert.Single(transport.Parsed.OfType<ResetAckMessage>());
    }

    [Fact]
    public async Task ResetDoesNotBlockTheDebuggerCallbackAndAcknowledgesOnlyAfterStopCompletes()
    {
        var (runtime, transport, backend) = Runtime();
        runtime.Connect();
        Assert.True(runtime.Receive(Configure(4, CaptureModes.ManualScopes), "owner"));
        Assert.True(runtime.Receive(Start(4), "owner"));
        backend.BlockStop = true;

        const string requestId = "11111111111111111111111111111111";
        var receive = Task.Run(() => runtime.Receive(Reset(4, requestId), "owner"));
        await backend.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Same(receive, await Task.WhenAny(receive, Task.Delay(TimeSpan.FromSeconds(1))));
            Assert.True(await receive);
            Assert.True(runtime.Capturing);
            Assert.Equal("owner", runtime.LeaseOwner);
            Assert.DoesNotContain(transport.Parsed, message => message is ResetAckMessage);
        }
        finally
        {
            backend.StopRelease.Set();
        }

        await backend.StopExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runtime.Flush();
        Assert.False(runtime.Capturing);
        Assert.Null(runtime.LeaseOwner);
        Assert.Single(transport.Parsed.OfType<ResetAckMessage>());
    }

    [Fact]
    public async Task PendingResetRetriesConvergeOnOneStopAndAckOnlyAfterInactivity()
    {
        var (runtime, transport, backend) = Runtime();
        runtime.Connect();
        Assert.True(runtime.Receive(Configure(4, CaptureModes.Sampling), "owner"));
        Assert.True(runtime.Receive(Start(4), "owner"));
        backend.BlockStop = true;
        const string requestId = "11111111111111111111111111111111";

        Assert.True(runtime.Receive(Reset(4, requestId), "owner"));
        await backend.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(runtime.Receive(Reset(4, requestId), "owner"));
        Assert.False(runtime.Receive(Reset(4, "22222222222222222222222222222222"), "owner"));
        Assert.Equal(1, backend.StopAttempts);
        Assert.DoesNotContain(transport.Parsed, message => message is ResetAckMessage);

        backend.StopRelease.Set();
        await backend.StopExited.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var attempt = 0; attempt < 100 && runtime.Capturing; attempt++)
        {
            runtime.Flush();
            await Task.Delay(1);
        }
        Assert.False(runtime.Capturing);
        Assert.Single(transport.Parsed.OfType<ResetAckMessage>());
        Assert.True(runtime.Receive(Reset(4, requestId), "owner"));
        Assert.Equal(2, transport.Parsed.OfType<ResetAckMessage>().Count());
        Assert.Equal(1, backend.Stops);
    }

    [Fact]
    public async Task CompletionObservedByExactResetRetryEmitsOneAcknowledgement()
    {
        var (runtime, transport, backend) = Runtime();
        runtime.Connect();
        Assert.True(runtime.Receive(Configure(4, CaptureModes.Sampling), "owner"));
        Assert.True(runtime.Receive(Start(4), "owner"));
        backend.BlockStop = true;
        const string requestId = "11111111111111111111111111111111";

        Assert.True(runtime.Receive(Reset(4, requestId), "owner"));
        await backend.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        backend.StopRelease.Set();
        await backend.StopExited.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(10);

        Assert.True(runtime.Receive(Reset(4, requestId), "owner"));
        Assert.Single(transport.Parsed.OfType<ResetAckMessage>());
        Assert.True(runtime.Receive(Reset(4, requestId), "owner"));
        Assert.Equal(2, transport.Parsed.OfType<ResetAckMessage>().Count());
    }

    [Fact]
    public void SuccessfulStopResultThatRemainsActiveRetainsResetIdentity()
    {
        var (runtime, transport, backend) = Runtime();
        runtime.Connect();
        Assert.True(runtime.Receive(Configure(4, CaptureModes.Sampling), "owner"));
        Assert.True(runtime.Receive(Start(4), "owner"));
        backend.RemainActiveAfterStop = true;
        const string requestId = "11111111111111111111111111111111";

        Assert.False(runtime.Receive(Reset(4, requestId), "owner"));
        Assert.True(runtime.Capturing);
        Assert.Equal("owner", runtime.LeaseOwner);
        Assert.DoesNotContain(transport.Parsed, message => message is ResetAckMessage);

        backend.RemainActiveAfterStop = false;
        Assert.False(runtime.Receive(Reset(4, "22222222222222222222222222222222"), "owner"));
        Assert.True(runtime.Receive(Reset(4, requestId), "owner"));
        Assert.False(runtime.Capturing);
        Assert.Single(transport.Parsed.OfType<ResetAckMessage>());
        Assert.Equal(2, backend.StopAttempts);
    }

    [Fact]
    public async Task ExactResetRetryResumesWhenAsynchronousSuccessfulStopRemainsActive()
    {
        var (runtime, transport, backend) = Runtime();
        runtime.Connect();
        Assert.True(runtime.Receive(Configure(4, CaptureModes.Sampling), "owner"));
        Assert.True(runtime.Receive(Start(4), "owner"));
        backend.BlockStop = true;
        backend.RemainActiveAfterStop = true;
        const string requestId = "11111111111111111111111111111111";

        Assert.True(runtime.Receive(Reset(4, requestId), "owner"));
        await backend.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        backend.StopRelease.Set();
        await backend.StopExited.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(10);
        backend.BlockStop = false;
        backend.RemainActiveAfterStop = false;

        Assert.True(runtime.Receive(Reset(4, requestId), "owner"));
        Assert.False(runtime.Capturing);
        Assert.Single(transport.Parsed.OfType<ResetAckMessage>());
        Assert.Equal(2, backend.StopAttempts);
    }

    [Fact]
    public async Task ExactResetRetryResumesAfterAsynchronousStopFailure()
    {
        var (runtime, transport, backend) = Runtime();
        runtime.Connect();
        Assert.True(runtime.Receive(Configure(4, CaptureModes.Sampling), "owner"));
        Assert.True(runtime.Receive(Start(4), "owner"));
        backend.BlockStop = true;
        backend.StopFailure = new InvalidOperationException("async failure");
        const string requestId = "11111111111111111111111111111111";

        Assert.True(runtime.Receive(Reset(4, requestId), "owner"));
        await backend.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        backend.StopRelease.Set();
        for (var attempt = 0; attempt < 100 && !backend.StopExited.Task.IsCompleted; attempt++)
            await Task.Delay(1);
        await Task.Delay(10);
        backend.BlockStop = false;
        backend.StopFailure = null;

        Assert.True(runtime.Receive(Reset(4, requestId), "owner"));
        Assert.False(runtime.Capturing);
        Assert.Single(transport.Parsed.OfType<ResetAckMessage>());
        Assert.Equal(2, backend.StopAttempts);
    }

    [Fact]
    public void OrdinaryStopFailureRetainsOwnerAndEmitsNoCompleteUntilRetrySucceeds()
    {
        var (runtime, transport, backend) = Runtime();
        runtime.Connect();
        Assert.True(runtime.Receive(Configure(1, CaptureModes.Sampling), "owner"));
        Assert.True(runtime.Receive(Start(1), "owner"));
        backend.StopFailure = new InvalidOperationException("still active");

        Assert.False(runtime.Receive(Stop(1, 2), "owner"));
        Assert.True(runtime.Capturing);
        Assert.Equal("owner", runtime.LeaseOwner);
        Assert.DoesNotContain(transport.Parsed,
            message => message is StateMessage { State: CaptureState.Complete });
        Assert.False(runtime.Receive(Configure(2, CaptureModes.Sampling), "owner"));

        backend.StopFailure = null;
        Assert.True(runtime.Receive(Stop(1, 2), "owner"));
        Assert.False(runtime.Capturing);
        Assert.Contains(transport.Parsed,
            message => message is StateMessage { State: CaptureState.Complete });
    }

    [Fact]
    public void InactiveAbortFinishesOrdinaryStopAsPartialRatherThanComplete()
    {
        var (runtime, transport, backend) = Runtime();
        runtime.Connect();
        Assert.True(runtime.Receive(Configure(1, CaptureModes.Sampling), "owner"));
        Assert.True(runtime.Receive(Start(1), "owner"));
        backend.StopDataIncomplete = true;

        Assert.True(runtime.Receive(Stop(1, 2), "owner"));

        var terminal = transport.Parsed.OfType<StateMessage>().Last();
        Assert.Equal(CaptureState.Partial, terminal.State);
        Assert.Equal(CaptureCompleteness.Partial, terminal.Completeness);
        Assert.Equal(PartialReason.RuntimeError, terminal.PartialReason);
        Assert.False(runtime.Capturing);
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
    public void EveryFlushEmittedChunkReportsItsGenerationAndSequence()
    {
        var (runtime, transport, backend) = Runtime();
        backend.Pending.Add(new(CaptureSource.ManualSpans, true, false, QualityCounters.Zero,
            Enumerable.Range(0, 9).Select(i => new MethodSample(i, $"Method{i}", i, 1)).ToArray()));
        var emitted = new List<(long Generation, long Sequence)>();
        runtime.BatchEmitted += (generation, sequence) => emitted.Add((generation, sequence));
        runtime.Connect();
        Assert.True(runtime.Receive(Configure(1, CaptureModes.ManualScopes, maxMethods: 2), "owner"));
        Assert.True(runtime.Receive(Start(1), "owner"));

        runtime.Flush();

        var batches = transport.Parsed.OfType<BatchMessage>().ToArray();
        Assert.Equal(batches.Select(batch => (batch.Generation, batch.Sequence)), emitted);
        Assert.Equal(5, emitted.Count);
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
    public async Task DrainFailureAndDisconnectDoNotBlockRuntimeCallbacks()
    {
        var (faultedRuntime, _, faultedBackend) = Runtime();
        faultedRuntime.Connect();
        Assert.True(faultedRuntime.Receive(Configure(1, CaptureModes.Sampling), "owner"));
        Assert.True(faultedRuntime.Receive(Start(1), "owner"));
        faultedBackend.DrainFailure = new InvalidOperationException("drain failed");
        faultedBackend.BlockStop = true;

        var (disconnectRuntime, _, disconnectBackend) = Runtime();
        disconnectRuntime.Connect();
        Assert.True(disconnectRuntime.Receive(Configure(1, CaptureModes.Sampling), "owner"));
        Assert.True(disconnectRuntime.Receive(Start(1), "owner"));
        disconnectBackend.BlockStop = true;

        var flush = Task.Run(faultedRuntime.Flush);
        var disconnect = Task.Run(disconnectRuntime.Disconnect);
        await Task.WhenAll(
            faultedBackend.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            disconnectBackend.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        try
        {
            Assert.Same(flush, await Task.WhenAny(flush, Task.Delay(TimeSpan.FromSeconds(1))));
            Assert.Same(disconnect, await Task.WhenAny(disconnect, Task.Delay(TimeSpan.FromSeconds(1))));
            Assert.True(faultedRuntime.Capturing);
            Assert.True(disconnectRuntime.Capturing);
        }
        finally
        {
            faultedBackend.StopRelease.Set();
            disconnectBackend.StopRelease.Set();
        }

        await Task.WhenAll(
            faultedBackend.StopExited.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            disconnectBackend.StopExited.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        for (var attempt = 0; attempt < 100 && faultedRuntime.Capturing; attempt++)
        {
            faultedRuntime.Flush();
            await Task.Delay(1);
        }
        Assert.False(faultedRuntime.Capturing);
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
    public void AsynchronousBackendFaultTerminatesCaptureAsRuntimeError()
    {
        var (runtime, transport, backend) = Runtime();
        runtime.Connect();
        Assert.True(runtime.Receive(Configure(1, CaptureModes.Sampling), "owner"));
        Assert.True(runtime.Receive(Start(1), "owner"));
        backend.DrainFailure = new InvalidOperationException("sampler fault");

        runtime.Flush();

        Assert.False(runtime.Capturing);
        Assert.Contains(transport.Parsed, message => message is ErrorMessage { Fatal: true });
        Assert.Contains(transport.Parsed, message => message is StateMessage
            { State: CaptureState.Partial, PartialReason: PartialReason.RuntimeError });
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
    private static WireMap Reset(long generation, string requestId) =>
        StrictWireAdapter.Serialize(new ResetMessage(ProtocolVersion.Major, ProtocolVersion.Minor, Token, generation, requestId));

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
        public int StopAttempts { get; private set; }
        public List<RuntimeSourceBatch> Pending { get; } = [];
        public RuntimeCaptureConfiguration? LastConfiguration { get; private set; }
        public Exception? DrainFailure { get; set; }
        public Exception? StopFailure { get; set; }
        public bool StopDataIncomplete { get; set; }
        public bool RemainActiveAfterStop { get; set; }
        public bool BlockStop { get; set; }
        public TaskCompletionSource<bool> StopEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim StopRelease { get; } = new(false);
        public TaskCompletionSource<bool> StopExited { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool TryStart(RuntimeCaptureConfiguration configuration, string owner, out string? error)
        { LastConfiguration = configuration; _ = owner; error = null; Starts++; IsActive = true; return true; }
        public IReadOnlyList<RuntimeSourceBatch> Drain()
        {
            if (DrainFailure is not null) throw DrainFailure;
            var value = Pending.ToArray(); Pending.Clear(); return value;
        }
        public Task<RuntimeCaptureStopResult> StopAsync() => BlockStop
            ? Task.Run(() => new RuntimeCaptureStopResult(Stop(), StopDataIncomplete))
            : Task.FromResult(new RuntimeCaptureStopResult(Stop(), StopDataIncomplete));

        public IReadOnlyList<RuntimeSourceBatch> Stop()
        {
            if (!IsActive) return [];
            StopAttempts++;
            if (BlockStop)
            {
                StopEntered.TrySetResult(true);
                StopRelease.Wait(TimeSpan.FromSeconds(10));
            }
            if (StopFailure is not null)
            {
                StopExited.TrySetResult(true);
                throw StopFailure;
            }
            Stops++;
            if (!RemainActiveAfterStop) IsActive = false;
            StopExited.TrySetResult(true);
            return Drain();
        }
        public void Dispose() { if (IsActive) Stop(); }
    }
}
