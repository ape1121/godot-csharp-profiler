using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
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
    public async Task StopTimeoutClosesContinuationAndWaitsForProcessingQuiescence()
    {
        var control = new FakeTraceEpochControl(acknowledgeOnStop: false);
        using var epoch = new ManagedSamplingTraceEpoch(control, TimeSpan.FromMilliseconds(20));

        var first = epoch.StopAsync();
        var second = epoch.StopAsync();
        await WaitUntilAsync(() => control.Aborts == 1, TimeSpan.FromSeconds(2));

        Assert.Same(first, second);
        Assert.False(first.IsCompleted);
        Assert.Equal(1, control.StopCalls);
        Assert.True(control.ProcessExited);

        control.AcknowledgeStop();
        var result = await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(result.StreamAborted);
        Assert.Equal(1, control.StopCalls);
        Assert.Equal(1, control.Aborts);
        Assert.Equal(1, control.Disposals);
    }

    [Fact]
    public async Task ConcurrentEpochStopCallersShareExactlyOneStopOperation()
    {
        var control = new FakeTraceEpochControl(acknowledgeOnStop: true);
        using var epoch = new ManagedSamplingTraceEpoch(control, TimeSpan.FromSeconds(1));

        var results = await Task.WhenAll(epoch.StopAsync(), epoch.StopAsync());

        Assert.Equal(1, control.StopCalls);
        Assert.Equal(1, control.Disposals);
        Assert.All(results, result => Assert.False(result.StreamAborted));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopRequestFailureStillWaitsForParserQuiescenceAndDisposesControlOnce(
        bool throwSynchronously)
    {
        var failure = new InvalidOperationException("StopTracing failed");
        var control = new FakeTraceEpochControl(
            acknowledgeOnStop: false,
            quiesceOnAbort: false,
            stopFailure: failure,
            throwStopSynchronously: throwSynchronously);
        using var epoch = new ManagedSamplingTraceEpoch(control, TimeSpan.FromMilliseconds(20));

        var first = epoch.StopAsync();
        var second = epoch.StopAsync();
        await WaitUntilAsync(() => control.Aborts == 1, TimeSpan.FromSeconds(2));

        Assert.Same(first, second);
        Assert.False(first.IsCompleted);
        Assert.Equal(1, control.StopCalls);
        Assert.Equal(0, control.Disposals);

        control.ReleaseProcessing();
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Same(failure, thrown);
        Assert.True(control.ProcessExited);
        Assert.Equal(1, control.Aborts);
        Assert.Equal(1, control.Disposals);
    }

    [Fact]
    public async Task StartupCancellationReturnsPromptlyWhileNativeFactoryIsBlockedAndRetainsOwnership()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var control = new FakeTraceEpochControl(acknowledgeOnStop: true);
        await using var first = new ManagedSamplingSession(new SamplingOptions(), () =>
        {
            entered.TrySetResult(true);
            release.Task.GetAwaiter().GetResult();
            return new ManagedSamplingTraceEpoch(control, TimeSpan.FromSeconds(1));
        });
        await using var replacement = new ManagedSamplingSession(new SamplingOptions(),
            () => new ManagedSamplingTraceEpoch(new FakeTraceEpochControl(true), TimeSpan.FromSeconds(1)));
        using var cancellation = new CancellationTokenSource();

        var start = Task.Run(() => first.StartAsync(cancellation.Token));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        try
        {
            Assert.Same(start, await Task.WhenAny(start, Task.Delay(TimeSpan.FromMilliseconds(250))));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            var busy = await Assert.ThrowsAsync<InvalidOperationException>(() => replacement.StartAsync());
            Assert.Contains("already active", busy.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            release.TrySetResult(true);
            try { await start.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            try { await first.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }

        await replacement.StartAsync();
        await replacement.StopAsync();
    }

    [Fact]
    public async Task AmbiguousNativeStartFailureIsPermanentlyQuarantinedInAChildProcess()
    {
        if (Environment.GetEnvironmentVariable("GCSP_AMBIGUOUS_START_CHILD") == "1") return;
        var resultPath = Path.Combine(Path.GetTempPath(),
            "gcsp-ambiguous-start-" + Guid.NewGuid().ToString("N"));
        var root = FindRepositoryRoot();
        var project = Path.Combine(root, "tests", "GodotCSharpProfiler.Sampling.Tests",
            "GodotCSharpProfiler.Sampling.Tests.csproj");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (string.IsNullOrWhiteSpace(dotnet))
        {
            var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            dotnet = string.IsNullOrWhiteSpace(dotnetRoot) ? "dotnet" :
                Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        }
        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "test", project, "-c", configuration, "--no-build", "--no-restore",
                     "--filter", "FullyQualifiedName~AmbiguousNativeStartFailureChildProbe",
                     "--logger", "console;verbosity=minimal"
                 })
            start.ArgumentList.Add(argument);
        start.Environment["GCSP_AMBIGUOUS_START_CHILD"] = "1";
        start.Environment["GCSP_AMBIGUOUS_START_RESULT"] = resultPath;

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start child testhost.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(process.ExitCode == 0,
                $"Child quarantine probe exited {process.ExitCode}.\n{await stdout}\n{await stderr}");
            Assert.True(File.Exists(resultPath), "Child quarantine probe did not reach its assertions.");
            Assert.Equal("AMBIGUOUS_NATIVE_START_QUARANTINE_OK", File.ReadAllText(resultPath));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            File.Delete(resultPath);
        }
    }

    [Fact]
    public async Task AmbiguousNativeStartFailureChildProbe()
    {
        if (Environment.GetEnvironmentVariable("GCSP_AMBIGUOUS_START_CHILD") != "1") return;
        var first = new ManagedSamplingSession(new SamplingOptions(), reportAcquisition =>
        {
            var acquisition = ManagedSamplingTraceAcquisition.BeginUnknown(TimeSpan.FromMilliseconds(20));
            reportAcquisition(acquisition);
            acquisition.MarkAdditionalNativeActivityUnaccounted();
            throw new InvalidOperationException("ambiguous native start response failure");
        });
        var replacement = new ManagedSamplingSession(new SamplingOptions(),
            () => new ManagedSamplingTraceEpoch(new FakeTraceEpochControl(true), TimeSpan.FromSeconds(1)));

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => first.StartAsync());
        Assert.Contains("ambiguous native start", failure.ToString(), StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.StopAsync());
        var busy = await Assert.ThrowsAsync<InvalidOperationException>(() => replacement.StartAsync());
        Assert.Contains("already active", busy.Message, StringComparison.OrdinalIgnoreCase);
        File.WriteAllText(Environment.GetEnvironmentVariable("GCSP_AMBIGUOUS_START_RESULT")!,
            "AMBIGUOUS_NATIVE_START_QUARANTINE_OK");
    }

    [Fact]
    public async Task DisposedCancellationSourceTokenCanStartAndStopWithoutOverlappingOwnership()
    {
        using var source = new CancellationTokenSource();
        var token = source.Token;
        source.Dispose();
        var firstControl = new FakeTraceEpochControl(acknowledgeOnStop: true);
        await using var first = new ManagedSamplingSession(new SamplingOptions(),
            () => new ManagedSamplingTraceEpoch(firstControl, TimeSpan.FromSeconds(1)));
        await using var replacement = new ManagedSamplingSession(new SamplingOptions(),
            () => new ManagedSamplingTraceEpoch(new FakeTraceEpochControl(true), TimeSpan.FromSeconds(1)));

        await first.StartAsync(token);
        var busy = await Assert.ThrowsAsync<InvalidOperationException>(() => replacement.StartAsync());
        Assert.Contains("already active", busy.Message, StringComparison.OrdinalIgnoreCase);
        await first.StopAsync();
        await replacement.StartAsync();
        await replacement.StopAsync();
    }

    [Fact]
    public async Task FailedAbortRetainsOwnershipUntilTheOriginalEpochQuiesces()
    {
        var control = new FakeTraceEpochControl(acknowledgeOnStop: false, quiesceOnAbort: false);
        await using var first = new ManagedSamplingSession(new SamplingOptions(),
            () => new ManagedSamplingTraceEpoch(control, TimeSpan.FromMilliseconds(20)));
        await using var replacement = new ManagedSamplingSession(new SamplingOptions(),
            () => new ManagedSamplingTraceEpoch(new FakeTraceEpochControl(true), TimeSpan.FromSeconds(1)));
        await first.StartAsync();

        var stop = first.StopAsync();
        await WaitUntilAsync(() => control.Aborts == 1, TimeSpan.FromSeconds(2));
        Assert.False(stop.IsCompleted);
        Assert.Equal(ManagedSamplingSessionState.Stopping, first.State);
        var busy = await Assert.ThrowsAsync<InvalidOperationException>(() => replacement.StartAsync());
        Assert.Contains("already active", busy.Message, StringComparison.OrdinalIgnoreCase);

        control.AcknowledgeStop();
        control.ReleaseProcessing();
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        await replacement.StartAsync();
        await replacement.StopAsync();
    }

    [Fact]
    public async Task PostAcquisitionStartupFailureRetainsProcessQuarantineUntilCleanupIsProven()
    {
        var failure = new InvalidOperationException("failed after native acquisition");
        var control = new FakeTraceEpochControl(acknowledgeOnStop: false, quiesceOnAbort: false);
        ManagedSamplingTraceEpoch? acquired = null;
        var first = new ManagedSamplingSession(new SamplingOptions(), reportAcquisition =>
        {
            acquired = new ManagedSamplingTraceEpoch(control, TimeSpan.FromMilliseconds(20));
            reportAcquisition(ManagedSamplingTraceAcquisition.FromEpoch(acquired));
            throw failure;
        });
        var replacement = new ManagedSamplingSession(new SamplingOptions(),
            () => new ManagedSamplingTraceEpoch(new FakeTraceEpochControl(true), TimeSpan.FromSeconds(1)));
        var replacementStarted = false;

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => first.StartAsync());
            Assert.Same(failure, thrown.InnerException);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1),
                $"Startup failure blocked for {stopwatch.Elapsed}.");

            await WaitUntilAsync(() => control.StopCalls == 1, TimeSpan.FromSeconds(2));
            Assert.Equal(0, control.Disposals);
            var busy = await Assert.ThrowsAsync<InvalidOperationException>(() => replacement.StartAsync());
            Assert.Contains("already active", busy.Message, StringComparison.OrdinalIgnoreCase);

            control.AcknowledgeStop();
            control.ReleaseProcessing();
            await first.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
            await replacement.StartAsync();
            replacementStarted = true;
            Assert.Equal(1, control.StopCalls);
            Assert.Equal(1, control.Disposals);
        }
        finally
        {
            control.AcknowledgeStop();
            control.ReleaseProcessing();
            if (acquired is not null)
                try { await acquired.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            try { await first.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            if (replacementStarted || replacement.State == ManagedSamplingSessionState.Running)
                try { await replacement.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    [Fact]
    public async Task RotationAcquisitionFailureCannotBeClearedByStoppingTheOldEpoch()
    {
        var firstControl = new FakeTraceEpochControl(acknowledgeOnStop: true);
        var failedControl = new FakeTraceEpochControl(acknowledgeOnStop: false, quiesceOnAbort: false);
        var firstEpoch = new ManagedSamplingTraceEpoch(firstControl, TimeSpan.FromSeconds(1));
        ManagedSamplingTraceEpoch? failedEpoch = null;
        var acquisition = 0;
        var session = new ManagedSamplingSession(new SamplingOptions(), reportAcquisition =>
        {
            if (acquisition++ == 0)
            {
                reportAcquisition(ManagedSamplingTraceAcquisition.FromEpoch(firstEpoch));
                return firstEpoch;
            }
            failedEpoch = new ManagedSamplingTraceEpoch(failedControl, TimeSpan.FromMilliseconds(20));
            reportAcquisition(ManagedSamplingTraceAcquisition.FromEpoch(failedEpoch));
            throw new InvalidOperationException("replacement acquisition failed");
        });
        var replacement = new ManagedSamplingSession(new SamplingOptions(),
            () => new ManagedSamplingTraceEpoch(new FakeTraceEpochControl(true), TimeSpan.FromSeconds(1)));
        var replacementStarted = false;

        try
        {
            await session.StartAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.RotateTraceEpochAsync(firstEpoch));
            await WaitUntilAsync(() => failedControl.StopCalls == 1, TimeSpan.FromSeconds(2));

            var stop = session.StopAsync();
            Assert.False(stop.IsCompleted);
            var busy = await Assert.ThrowsAsync<InvalidOperationException>(() => replacement.StartAsync());
            Assert.Contains("already active", busy.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, failedControl.Disposals);

            failedControl.AcknowledgeStop();
            failedControl.ReleaseProcessing();
            await stop.WaitAsync(TimeSpan.FromSeconds(2));
            await replacement.StartAsync();
            replacementStarted = true;
            Assert.Equal(1, failedControl.StopCalls);
            Assert.Equal(1, failedControl.Disposals);
        }
        finally
        {
            failedControl.AcknowledgeStop();
            failedControl.ReleaseProcessing();
            if (failedEpoch is not null)
                try { await failedEpoch.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            try { await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            if (replacementStarted || replacement.State == ManagedSamplingSessionState.Running)
                try { await replacement.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        }
    }

    [Fact]
    public async Task LateRotationCannotStopReplacementEpoch()
    {
        var firstControl = new FakeTraceEpochControl(true);
        var replacementControl = new FakeTraceEpochControl(true);
        var controls = new Queue<FakeTraceEpochControl>([firstControl, replacementControl]);
        await using var session = new ManagedSamplingSession(new SamplingOptions(),
            () => new ManagedSamplingTraceEpoch(controls.Dequeue(), TimeSpan.FromSeconds(1)));
        await session.StartAsync();
        var first = session.CurrentEpochForTests!;

        await session.RotateTraceEpochAsync(first);
        var replacement = session.CurrentEpochForTests!;
        await session.RotateTraceEpochAsync(first);

        Assert.NotSame(first, replacement);
        Assert.Equal(ManagedSamplingSessionState.Running, session.State);
        Assert.Equal(1, firstControl.StopCalls);
        Assert.Equal(0, replacementControl.StopCalls);
        await session.StopAsync();
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
    public void ProcessLeaseIsExclusiveAcrossCollectibleAssemblyContexts()
    {
        var assemblyPath = typeof(ManagedSamplingSessionTests).Assembly.Location;
        var firstContext = new CollectibleProbeLoadContext("sampling-lease-first");
        var secondContext = new CollectibleProbeLoadContext("sampling-lease-second");
        Type? firstProbe = null;
        Type? secondProbe = null;
        try
        {
            firstProbe = firstContext.LoadFromAssemblyPath(assemblyPath).GetType(
                "GodotCSharpProfiler.Sampling.Tests.CrossContextSamplingLeaseProbe", throwOnError: true)!;
            secondProbe = secondContext.LoadFromAssemblyPath(assemblyPath).GetType(
                "GodotCSharpProfiler.Sampling.Tests.CrossContextSamplingLeaseProbe", throwOnError: true)!;

            Assert.True(InvokeLeaseProbe(firstProbe, "TryAcquire"));
            Assert.False(InvokeLeaseProbe(secondProbe, "TryAcquire"));
            InvokeLeaseProbe(firstProbe, "Release");
            Assert.True(InvokeLeaseProbe(secondProbe, "TryAcquire"));
        }
        finally
        {
            if (firstProbe is not null) InvokeLeaseProbe(firstProbe, "Release");
            if (secondProbe is not null) InvokeLeaseProbe(secondProbe, "Release");
            firstContext.Unload();
            secondContext.Unload();
        }
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

    private sealed class FakeTraceEpochControl(
        bool acknowledgeOnStop,
        bool quiesceOnAbort = true,
        Exception? stopFailure = null,
        bool throwStopSynchronously = false) : IManagedSamplingTraceEpochControl
    {
        private readonly TaskCompletionSource<bool> _acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _processed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StopCalls { get; private set; }
        public int Aborts { get; private set; }
        public int Disposals { get; private set; }
        public bool ProcessExited => _processed.Task.IsCompleted;

        public Task ProcessAsync() => _processed.Task;
        public Task RequestStopAsync()
        {
            StopCalls++;
            if (stopFailure is not null)
            {
                if (throwStopSynchronously) throw stopFailure;
                return Task.FromException(stopFailure);
            }
            if (acknowledgeOnStop)
            {
                AcknowledgeStop();
                _processed.TrySetResult(true);
            }
            return _acknowledged.Task;
        }
        public void AbortStream()
        {
            Aborts++;
            if (quiesceOnAbort) _processed.TrySetResult(true);
        }
        public void ReleaseProcessing() => _processed.TrySetResult(true);
        public void Dispose()
        {
            Disposals++;
            _processed.TrySetResult(true);
        }
        public void AcknowledgeStop() => _acknowledged.TrySetResult(true);
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

    private static bool InvokeLeaseProbe(Type probe, string method) =>
        (bool)(probe.GetMethod(method, System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Static)!.Invoke(null, null) ?? false);

    private sealed class CollectibleProbeLoadContext(string name) : AssemblyLoadContext(name, isCollectible: true)
    {
        protected override System.Reflection.Assembly? Load(System.Reflection.AssemblyName assemblyName) => null;
    }

    private static HashSet<string> GetTraceArtifacts() =>
        Directory.EnumerateFiles(Path.GetTempPath())
            .Where(file => file.EndsWith(".etlx", StringComparison.OrdinalIgnoreCase) ||
                           file.EndsWith(".nettrace", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "addons")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
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
