using Apeworks.GodotCSharpProfiler.Instrumentation;
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
    public void SamplingStopFailureStillStopsManualOverlayAndReturnsItsFinalBatchAfterRetry()
    {
        var samplingFailure = new InvalidOperationException("sampling stop failed");
        var sampling = new FakeSamplingLease(
            Snapshot(new SampledMethod(0, "Game", "Game.FinalSample", 7)))
        {
            StopFailure = samplingFailure
        };
        var manual = new FakeManualLease
        {
            Snapshot = new Apeworks.GodotCSharpProfiler.CsProfiler.FrameSnapshot
            {
                Names = ["ManualFinal"], Depths = [0], Calls = [2], TotalUsec = [4_000]
            }
        };
        using var backend = new ProductionRuntimeCaptureBackend(_ => sampling, null, _ => manual);
        var configuration = new RuntimeCaptureConfiguration(1, new string('a', 32),
            CaptureModes.Sampling | CaptureModes.ManualScopes,
            0, 16, "", "", "");
        Assert.True(backend.TryStart(configuration, "owner", out var startError), startError);

        var thrown = Assert.Throws<InvalidOperationException>(() => backend.Stop());
        Assert.Contains("sampling", thrown.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(backend.IsActive);
        Assert.False(manual.IsActive);
        Assert.Equal(1, manual.Stops);

        sampling.StopFailure = null;
        var result = backend.Stop();

        Assert.Equal([CaptureSource.Sampling, CaptureSource.ManualSpans],
            result.Select(batch => batch.Source));
        Assert.Equal("Game.FinalSample",
            Assert.Single(result.Single(batch => batch.Source == CaptureSource.Sampling).Methods).Label);
        var manualBatch = result.Single(batch => batch.Source == CaptureSource.ManualSpans);
        var method = Assert.Single(manualBatch.Methods);
        Assert.Equal("ManualFinal", method.Label);
        Assert.Equal(2, method.Calls);
        Assert.False(backend.IsActive);
    }

    [Fact]
    public void SamplingFinalBatchSurvivesLaterManualStopFailureAndRetry()
    {
        var sampling = new FakeSamplingLease(
            Snapshot(new SampledMethod(0, "Game", "Game.FinalSample", 7)));
        var manualFailure = new InvalidOperationException("manual stop failed");
        var manual = new FakeManualLease
        {
            StopFailure = manualFailure,
            Snapshot = new Apeworks.GodotCSharpProfiler.CsProfiler.FrameSnapshot
            {
                Names = ["ManualFinal"], Depths = [0], Calls = [3], TotalUsec = [5_000]
            }
        };
        using var backend = new ProductionRuntimeCaptureBackend(_ => sampling, null, _ => manual);
        var configuration = new RuntimeCaptureConfiguration(1, new string('a', 32),
            CaptureModes.Sampling | CaptureModes.ManualScopes,
            0, 16, "", "", "");
        Assert.True(backend.TryStart(configuration, "owner", out var startError), startError);

        Assert.Throws<InvalidOperationException>(() => backend.Stop());
        Assert.True(backend.IsActive);
        Assert.Equal(1, sampling.Disposals);

        manual.StopFailure = null;
        manual.Snapshot = Apeworks.GodotCSharpProfiler.CsProfiler.FrameSnapshot.Empty;
        var result = backend.Stop();

        Assert.Equal([CaptureSource.Sampling, CaptureSource.ManualSpans],
            result.Select(batch => batch.Source));
        Assert.Equal("Game.FinalSample",
            Assert.Single(result.Single(batch => batch.Source == CaptureSource.Sampling).Methods).Label);
        Assert.Equal("ManualFinal",
            Assert.Single(result.Single(batch => batch.Source == CaptureSource.ManualSpans).Methods).Label);
        Assert.False(backend.IsActive);
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
    public async Task ConcurrentStartsAcquireOnlyOneManualLease()
    {
        var factoryEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var factoryRelease = new ManualResetEventSlim(false);
        var factoryCalls = 0;
        using var backend = new ProductionRuntimeCaptureBackend(null, null, _ =>
        {
            Interlocked.Increment(ref factoryCalls);
            factoryEntered.TrySetResult(true);
            factoryRelease.Wait(TimeSpan.FromSeconds(2));
            return new FakeManualLease();
        });
        var configuration = new RuntimeCaptureConfiguration(1, new string('a', 32),
            CaptureModes.ManualScopes, 0, 16, "", "", "");

        var first = Task.Run(() => backend.TryStart(configuration, "first", out _));
        await factoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = Task.Run(() => backend.TryStart(configuration, "second", out _));
        Assert.NotSame(second, await Task.WhenAny(second, Task.Delay(50)));
        factoryRelease.Set();

        var accepted = await Task.WhenAll(first, second);
        Assert.Single(accepted, value => value);
        Assert.Equal(1, factoryCalls);
        Assert.True(backend.IsActive);
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
    public async Task ReentrantSamplingStopObservesThePublishedSingleStopOperation()
    {
        var lease = new FakeSamplingLease();
        using var backend = new ProductionRuntimeCaptureBackend(_ => lease, null);
        var configuration = new RuntimeCaptureConfiguration(1, new string('a', 32), CaptureModes.Sampling,
            0, 16, "", "", "");
        Assert.True(backend.TryStart(configuration, "owner", out var startError), startError);
        lease.ReentrantStop = backend.StopAsync;

        var stop = backend.StopAsync();
        var result = await stop.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Same(stop, lease.ReentrantStopTask);
        Assert.Equal(1, lease.Stops);
        Assert.Equal(1, lease.Disposals);
        Assert.False(result.DataIncomplete);
        Assert.False(backend.IsActive);
    }

    [Fact]
    public async Task ReentrantDisposeFromManualFactoryCannotInstallAnUnstoppableLease()
    {
        var manual = new FakeManualLease();
        ProductionRuntimeCaptureBackend? backend = null;
        backend = new ProductionRuntimeCaptureBackend(null, null, _ =>
        {
            backend!.Dispose();
            return manual;
        });
        var configuration = new RuntimeCaptureConfiguration(1, new string('a', 32), CaptureModes.ManualScopes,
            0, 16, "", "", "");

        Assert.False(backend.TryStart(configuration, "owner", out var error));
        Assert.Contains("disposed", error, StringComparison.OrdinalIgnoreCase);
        for (var attempt = 0; attempt < 100 && backend.IsActive; attempt++) await Task.Delay(10);
        Assert.False(backend.IsActive);
        Assert.Equal(1, manual.Stops);
    }

    [Fact]
    public async Task AutomaticOwnerIsRetainedUntilTheProcessRecorderHasStopped()
    {
        var manifest = CreateManifest("Game.Automatic");
        using var first = new ProductionRuntimeCaptureBackend(null, manifest);
        using var second = new ProductionRuntimeCaptureBackend(null, manifest);
        var configuration = new RuntimeCaptureConfiguration(1, new string('a', 32),
            CaptureModes.AutomaticInstrumentation, 0, 16, "", "", "");
        Assert.True(first.TryStart(configuration, "first", out var firstError), firstError);

        const System.Reflection.BindingFlags staticFlags =
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
        const System.Reflection.BindingFlags instanceFlags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var recorderGate = typeof(Apeworks.GodotCSharpProfiler.Instrumentation.InstrumentationRecorder)
            .GetField("Gate", staticFlags)!.GetValue(null)!;
        var ownerField = typeof(ProductionRuntimeCaptureBackend)
            .GetField("s_automaticOwner", staticFlags)!;
        var stopTaskField = typeof(ProductionRuntimeCaptureBackend)
            .GetField("_stopTask", instanceFlags)!;
        Task<RuntimeCaptureStopResult> firstStop;
        bool acceptedWhileFirstWasStopping;
        lock (recorderGate)
        {
            firstStop = Task.Factory.StartNew(first.StopAsync, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            Assert.True(SpinWait.SpinUntil(
                    () => ownerField.GetValue(null) is null || stopTaskField.GetValue(first) is not null,
                    TimeSpan.FromSeconds(2)),
                "First backend did not publish or enter automatic-recorder teardown.");
            acceptedWhileFirstWasStopping = second.TryStart(
                configuration with { Generation = 2 }, "second", out _);
        }

        await firstStop.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(acceptedWhileFirstWasStopping);
        var rejectedStop = (Task<RuntimeCaptureStopResult>?)stopTaskField.GetValue(second);
        if (rejectedStop is not null) await rejectedStop.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(second.TryStart(configuration with { Generation = 2 }, "second", out var secondError),
            secondError);
        Assert.True(second.IsActive);
        Assert.True(Apeworks.GodotCSharpProfiler.Instrumentation.InstrumentationRecorder.Active);
        await second.StopAsync();
        Assert.False(second.IsActive);
    }

    [Fact]
    public async Task BlockedSamplingStopStillStopsManualOverlayPromptly()
    {
        var sampling = new FakeSamplingLease { BlockStopAsync = true };
        var manual = new FakeManualLease();
        using var backend = new ProductionRuntimeCaptureBackend(_ => sampling, null, _ => manual);
        var configuration = new RuntimeCaptureConfiguration(1, new string('a', 32),
            CaptureModes.Sampling | CaptureModes.ManualScopes, 0, 16, "", "", "");
        Assert.True(backend.TryStart(configuration, "owner", out var startError), startError);

        var stop = backend.StopAsync();
        await sampling.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(stop.IsCompleted);
        Assert.False(manual.IsActive);
        Assert.Equal(1, manual.Stops);
        sampling.StopAcknowledged.TrySetResult(true);
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(backend.IsActive);
    }

    [Fact]
    public async Task LostManualFinalDataSurvivesSamplingRetryAsIncomplete()
    {
        var sampling = new FakeSamplingLease { StopFailure = new InvalidOperationException("sampling failed") };
        var manual = new FakeManualLease { FlushFailure = new InvalidOperationException("manual flush failed") };
        using var backend = new ProductionRuntimeCaptureBackend(_ => sampling, null, _ => manual);
        var configuration = new RuntimeCaptureConfiguration(1, new string('a', 32),
            CaptureModes.Sampling | CaptureModes.ManualScopes, 0, 16, "", "", "");
        Assert.True(backend.TryStart(configuration, "owner", out var startError), startError);

        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.StopAsync());
        Assert.True(backend.IsActive);
        Assert.False(manual.IsActive);
        sampling.StopFailure = null;

        var result = await backend.StopAsync();

        Assert.True(result.DataIncomplete);
        Assert.False(backend.IsActive);
    }

    [Fact]
    public async Task InactiveManualFailurePreservesSamplingBatchAsPartialResult()
    {
        var sampling = new FakeSamplingLease(
            Snapshot(new SampledMethod(0, "Game", "Game.FinalSample", 7)));
        var manual = new FakeManualLease { FlushFailure = new InvalidOperationException("manual flush failed") };
        using var backend = new ProductionRuntimeCaptureBackend(_ => sampling, null, _ => manual);
        var configuration = new RuntimeCaptureConfiguration(1, new string('a', 32),
            CaptureModes.Sampling | CaptureModes.ManualScopes, 0, 16, "", "", "");
        Assert.True(backend.TryStart(configuration, "owner", out var startError), startError);

        var result = await backend.StopAsync();

        Assert.True(result.DataIncomplete);
        Assert.Equal("Game.FinalSample",
            Assert.Single(Assert.Single(result.Batches).Methods).Label);
        Assert.False(backend.IsActive);
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
    public async Task FailedSamplingStartReturnsPromptlyAndRetainsBackendUntilCleanupIsProven()
    {
        var lease = new FakeSamplingLease
        {
            StartFailure = new InvalidOperationException("failed after native acquisition"),
            BlockStopAsync = true
        };
        using var backend = new ProductionRuntimeCaptureBackend(_ => lease, null);
        var configuration = new RuntimeCaptureConfiguration(1, new string('a', 32), CaptureModes.Sampling,
            0, 16, "", "", "");

        var start = Task.Run(() =>
        {
            var accepted = backend.TryStart(configuration, "owner", out var error);
            return (accepted, error);
        });
        await lease.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var returned = await Task.WhenAny(start, Task.Delay(TimeSpan.FromMilliseconds(250)));
        if (ReferenceEquals(returned, start))
        {
            var failed = await start;
            Assert.False(failed.accepted);
            Assert.Contains("failed after native acquisition", failed.error, StringComparison.Ordinal);
            Assert.True(backend.IsActive);
            Assert.False(backend.TryStart(configuration with { Generation = 2 }, "replacement", out _));
            Assert.Equal(0, lease.Disposals);
        }

        lease.StopAcknowledged.TrySetResult(true);
        var result = await start.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Same(start, returned);
        Assert.False(result.accepted);
        for (var attempt = 0; attempt < 100 && backend.IsActive; attempt++) await Task.Delay(10);
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

    [Fact]
    public async Task ManualFlushFailureReturnsAnInactivePartialResult()
    {
        var failure = new InvalidOperationException("manual flush failed");
        var manual = new FakeManualLease { FlushFailure = failure };
        using var backend = new ProductionRuntimeCaptureBackend(null, null, _ => manual);
        var configuration = new RuntimeCaptureConfiguration(1, new string('a', 32), CaptureModes.ManualScopes,
            0, 16, "", "", "");
        Assert.True(backend.TryStart(configuration, "owner", out var startError), startError);

        var result = await backend.StopAsync();

        Assert.True(result.DataIncomplete);
        Assert.Empty(result.Batches);
        Assert.Equal(1, manual.Flushes);
        Assert.Equal(1, manual.Stops);
        Assert.False(backend.IsActive);
    }

    [Fact]
    public void ManualStopFailureRetainsLeaseAndBackendOwnershipUntilRetrySucceeds()
    {
        var failure = new InvalidOperationException("manual stop failed");
        var manual = new FakeManualLease { StopFailure = failure };
        using var backend = new ProductionRuntimeCaptureBackend(null, null, _ => manual);
        var configuration = new RuntimeCaptureConfiguration(1, new string('a', 32), CaptureModes.ManualScopes,
            0, 16, "", "", "");
        Assert.True(backend.TryStart(configuration, "owner", out var startError), startError);

        var thrown = Assert.Throws<InvalidOperationException>(() => backend.Stop());
        Assert.Same(failure, thrown.InnerException);
        Assert.True(backend.IsActive);
        Assert.False(backend.TryStart(configuration with { Generation = 2 }, "replacement", out _));
        Assert.Equal(1, manual.Stops);

        manual.StopFailure = null;
        backend.Stop();
        Assert.Equal(2, manual.Stops);
        Assert.False(backend.IsActive);
    }

    [Fact]
    public void ManualStopRetryReturnsTheFinalBatchFlushedBeforeTheFirstStopFailure()
    {
        var failure = new InvalidOperationException("manual stop failed");
        var manual = new FakeManualLease
        {
            StopFailure = failure,
            Snapshot = new Apeworks.GodotCSharpProfiler.CsProfiler.FrameSnapshot
            {
                Names = ["Final"], Depths = [0], Calls = [2], TotalUsec = [3_000]
            }
        };
        using var backend = new ProductionRuntimeCaptureBackend(null, null, _ => manual);
        var configuration = new RuntimeCaptureConfiguration(1, new string('a', 32), CaptureModes.ManualScopes,
            0, 16, "", "", "");
        Assert.True(backend.TryStart(configuration, "owner", out var startError), startError);

        Assert.Throws<InvalidOperationException>(() => backend.Stop());
        manual.StopFailure = null;
        manual.Snapshot = Apeworks.GodotCSharpProfiler.CsProfiler.FrameSnapshot.Empty;

        var result = backend.Stop();

        var method = Assert.Single(Assert.Single(result).Methods);
        Assert.Equal("Final", method.Label);
        Assert.Equal(2, method.Calls);
        Assert.Equal(3_000_000, method.Value);
    }

    [Fact]
    public async Task ManualStopRetryMergesDataRecordedAfterTheFirstFailedAttempt()
    {
        var failure = new InvalidOperationException("manual stop failed");
        var manual = new FakeManualLease
        {
            StopFailure = failure,
            Snapshot = new Apeworks.GodotCSharpProfiler.CsProfiler.FrameSnapshot
            {
                Names = ["Accumulated"], Depths = [0], Calls = [2], TotalUsec = [4_000]
            }
        };
        using var backend = new ProductionRuntimeCaptureBackend(null, null, _ => manual);
        var configuration = new RuntimeCaptureConfiguration(1, new string('a', 32), CaptureModes.ManualScopes,
            0, 16, "", "", "");
        Assert.True(backend.TryStart(configuration, "owner", out var startError), startError);

        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.StopAsync());
        manual.StopFailure = null;
        manual.Snapshot = new Apeworks.GodotCSharpProfiler.CsProfiler.FrameSnapshot
        {
            Names = ["Accumulated"], Depths = [0], Calls = [3], TotalUsec = [6_000]
        };

        var result = await backend.StopAsync();

        var batch = Assert.Single(result.Batches);
        var method = Assert.Single(batch.Methods);
        Assert.Equal("Accumulated", method.Label);
        Assert.Equal(5, method.Calls);
        Assert.Equal(10_000_000, method.Value);
        Assert.Equal(5, batch.Quality.Observed);
        Assert.False(result.DataIncomplete);
        Assert.Equal(2, manual.Flushes);
        Assert.False(backend.IsActive);
    }

    [Fact]
    public void NonThrowingManualStopThatRemainsActiveIsRetryableAndRetainsOwnership()
    {
        var manual = new FakeManualLease { RemainActiveAfterStop = true };
        using var backend = new ProductionRuntimeCaptureBackend(null, null, _ => manual);
        var configuration = new RuntimeCaptureConfiguration(1, new string('a', 32), CaptureModes.ManualScopes,
            0, 16, "", "", "");
        Assert.True(backend.TryStart(configuration, "owner", out var startError), startError);

        var first = Assert.Throws<InvalidOperationException>(() => backend.Stop());
        Assert.Contains("remained active", first.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(backend.IsActive);
        Assert.Equal(1, manual.Stops);

        manual.RemainActiveAfterStop = false;
        backend.Stop();
        Assert.Equal(2, manual.Stops);
        Assert.False(backend.IsActive);
    }

    private static InstrumentationManifest CreateManifest(params string[] labels)
    {
        var constructor = typeof(InstrumentationManifest).GetConstructors(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
        return (InstrumentationManifest)constructor.Invoke(
            [typeof(ProductionRuntimeCaptureBackendTests).Assembly, "0123456789abcdef", labels, 0]);
    }

    private static SamplingSnapshot Snapshot(params SampledMethod[] methods) => new(DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow, methods, [], new SamplingCounters(1, 1, 0, 0, 0, 0, 0, 0, 0));

    private sealed class FakeSamplingLease(params SamplingSnapshot[] snapshots) : ProductionRuntimeCaptureBackend.IManagedSamplingLease
    {
        public Exception? Fault => null;
        private readonly Queue<SamplingSnapshot> _snapshots = new(snapshots);
        public SamplingOptions? Options { get; set; }
        public Exception? StartFailure { get; set; }
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
        public Func<Task<RuntimeCaptureStopResult>>? ReentrantStop { get; set; }
        public Task<RuntimeCaptureStopResult>? ReentrantStopTask { get; private set; }
        public void Start()
        {
            Starts++;
            if (StartFailure is not null) throw StartFailure;
        }
        public void Stop()
        {
            Stops++;
            if (StopFailure is not null) throw StopFailure;
        }
        public async Task<bool> StopAsync()
        {
            Stops++;
            StopEntered.TrySetResult(true);
            var reentrantStop = ReentrantStop;
            ReentrantStop = null;
            if (reentrantStop is not null) ReentrantStopTask = reentrantStop();
            if (StopFailure is not null) throw StopFailure;
            if (BlockStopAsync) await StopAcknowledged.Task;
            return StopDataIncomplete;
        }
        public SamplingSnapshot Snapshot(bool reset) => _snapshots.Count == 0
            ? SamplingSnapshot.Empty(DateTimeOffset.UtcNow) : _snapshots.Dequeue();
        public void Dispose() => Disposals++;
    }

    private sealed class FakeManualLease : ProductionRuntimeCaptureBackend.IManualCaptureLease
    {
        public Exception? FlushFailure { get; set; }
        public Exception? StopFailure { get; set; }
        public Apeworks.GodotCSharpProfiler.CsProfiler.FrameSnapshot Snapshot { get; set; } =
            Apeworks.GodotCSharpProfiler.CsProfiler.FrameSnapshot.Empty;
        public bool RemainActiveAfterStop { get; set; }
        public bool IsActive { get; private set; } = true;
        public int Flushes { get; private set; }
        public int Stops { get; private set; }
        public Apeworks.GodotCSharpProfiler.CsProfiler.FrameSnapshot FlushFrame()
        {
            Flushes++;
            if (FlushFailure is not null) throw FlushFailure;
            return Snapshot;
        }
        public bool Stop()
        {
            Stops++;
            if (StopFailure is not null) throw StopFailure;
            if (RemainActiveAfterStop) return false;
            IsActive = false;
            return true;
        }
    }
}