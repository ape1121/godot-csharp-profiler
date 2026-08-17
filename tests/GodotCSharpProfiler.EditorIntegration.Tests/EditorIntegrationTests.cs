using Apeworks.GodotCSharpProfiler.Editor.Installation;
using Apeworks.GodotCSharpProfiler.Editor.Integration;
using Apeworks.GodotCSharpProfiler.Editor.Modes;
using Apeworks.GodotCSharpProfiler.Protocol;
using Apeworks.GodotCSharpProfiler.Runtime.Protocol.Adapters;

namespace GodotCSharpProfiler.EditorIntegration.Tests;

public sealed class EditorIntegrationTests
{
    [Fact]
    public void CompactDockKeepsCaptureAndCallsPrimaryWhileAdvancedControlsMoveToSettings()
    {
        var layout = ProfilerDockLayoutPolicy.ForHeight(180);

        Assert.True(layout.ShowPrimaryToolbar);
        Assert.True(layout.ShowCalls);
        Assert.True(layout.ShowSettingsButton);
        Assert.False(layout.ShowInlineSettings);
        Assert.False(layout.ShowQualityDetails);
        Assert.InRange(layout.GraphMinimumHeight, 32, 56);
    }

    [Fact]
    public void SamplingIsUniversalDefaultAndManualHooksAreAnOptionalOverlay()
    {
        var controller = new ProfilerDockController(new FakeView(), new FakeTransport(), null);

        Assert.Equal(PrimaryMode.Sampling, controller.Configuration.Primary);
        Assert.False(controller.Configuration.IncludeManual);

        controller.SetManualOverlay(true);
        Assert.Equal(CaptureModes.Sampling | CaptureModes.ManualScopes, controller.Configuration.Modes);
    }

    [Fact]
    public void StartupOnlySamplingUsesRuntimeEffectiveIntervalInsteadOfRejectingStart()
    {
        var sent = new Queue<WireMap>();
        var editor = new EditorCaptureCoordinator("owner", sent.Enqueue);
        Assert.True(editor.Receive(StrictWireAdapter.Serialize(new HelloMessage(
            ProtocolVersion.Major, ProtocolVersion.Minor, "runtime", "runtime", 4096))));
        Assert.True(editor.Receive(StrictWireAdapter.Serialize(new CapabilitiesMessage(
            ProtocolVersion.Major, ProtocolVersion.Minor, "runtime", 0,
            CaptureModes.Sampling, false, 2_000_000, 4096, 4096, 8))));

        Assert.True(editor.Start(ModeConfiguration.Default));
        var configure = Assert.IsType<ConfigureMessage>(Parse(sent.Dequeue()));
        Assert.Equal(0, configure.RequestedSamplingIntervalNanoseconds);
    }

    [Fact]
    public void StartRequestedDuringNegotiationIsQueuedUntilCapabilitiesAreReady()
    {
        var sent = new Queue<WireMap>();
        var endpoint = new EditorCaptureCoordinator("owner", sent.Enqueue);
        var pending = new PendingCaptureRequest();

        pending.Request(ModeConfiguration.Default);
        Assert.Equal(PendingStartOutcome.Waiting, pending.TryStart(endpoint));
        Assert.Empty(sent);

        Assert.True(endpoint.Receive(StrictWireAdapter.Serialize(new HelloMessage(
            ProtocolVersion.Major, ProtocolVersion.Minor, "runtime", "runtime", 4096))));
        Assert.True(endpoint.Receive(StrictWireAdapter.Serialize(new CapabilitiesMessage(
            ProtocolVersion.Major, ProtocolVersion.Minor, "runtime", 0,
            CaptureModes.Sampling | CaptureModes.ManualScopes, true, 2_000_000, 4096, 4096, 8))));

        Assert.Equal(PendingStartOutcome.Started, pending.TryStart(endpoint));
        Assert.Equal(2, sent.Count);
        Assert.False(pending.HasRequest);
    }
        [Fact]
    public void ProductionControllerQueuesPreTargetStartAndStopCancelsThroughRealTransportPath()
    {
        var view = new FakeView();
        var transport = new FakeTransport();
        var controller = new ProfilerDockController(view, transport, null);

        Assert.True(controller.RequestStart());
        Assert.False(controller.RequestStart());
        Assert.Equal("Waiting for target capabilities — capture will start automatically.", view.Last!.Status);
        Assert.False(view.Last.Commands.Start);
        Assert.True(view.Last.Commands.Stop);
        Assert.True(view.Last.CapturePending);
        Assert.Equal([ProfilerCommand.Start], transport.Commands);

        Assert.True(controller.Stop());
        Assert.Equal([ProfilerCommand.Start, ProfilerCommand.CancelPending], transport.Commands);
        Assert.Equal("Pending capture cancelled.", view.Last.Status);
        Assert.False(view.Last.CapturePending);
    }

        [Fact]
    public void AcceptedBatchesPopulateBoundedSourceLabelledTimelineAndResultRows()
    {
        var sent = new Queue<WireMap>();
        var editor = ReadyEditor(sent);
        Assert.True(editor.Start(TestConfiguration));
        sent.Clear();
        Assert.True(editor.Receive(State(CaptureState.Capturing, 1, QualityCounters.Zero)));
        Assert.True(editor.Receive(Batch(CaptureSource.Sampling, 2, [new(7, "Game.Tick", 4, 0)])));

        var point = Assert.Single(editor.Timeline.Points);
        Assert.Equal((CaptureSource.Sampling, 4L, 4L), (point.Source, point.Value, point.Observations));
        var pointRow = Assert.Single(point.Rows);
        Assert.Equal(("Game.Tick", 4L, 100.0, 0.0),
            (pointRow.Name, pointRow.Samples, pointRow.EstimatedStackFrameShare, pointRow.ObservedWallTimeMilliseconds));
        Assert.True(editor.Stop());
        Assert.True(editor.Receive(State(CaptureState.Complete, 4, QualityCounters.Zero)));
        var row = Assert.Single(editor.CompletedResults.Groups.Single().Rows);
        Assert.Equal("Game.Tick", row.Name);
        Assert.Equal(4, row.Samples);
        Assert.Equal(100, row.EstimatedStackFrameShare);
        Assert.Equal(0, row.ObservedWallTimeMilliseconds);
    }

    [Fact]
    public void StopRequestedWhileStartingWaitsForCapturingSequenceThenStops()
    {
        var sent = new Queue<WireMap>();
        var editor = ReadyEditor(sent);
        Assert.True(editor.Start(TestConfiguration));
        sent.Clear();

        Assert.True(editor.Stop());
        Assert.Empty(sent);
        Assert.Equal(CaptureState.Starting, editor.Snapshot.State);

        Assert.True(editor.Receive(State(CaptureState.Capturing, 1, QualityCounters.Zero)));
        var stop = Assert.IsType<StopMessage>(Parse(sent.Dequeue()));
        Assert.Equal(2, stop.Sequence);
        Assert.Equal(CaptureState.Stopping, editor.Snapshot.State);
    }

    [Fact]
    public void FatalStartErrorReturnsToReadyAndCanRetryWithoutRestartingTarget()
    {
        var sent = new Queue<WireMap>();
        var editor = ReadyEditor(sent);
        Assert.True(editor.Start(TestConfiguration));
        sent.Clear();

        Assert.True(editor.Receive(StrictWireAdapter.Serialize(new ErrorMessage(
            ProtocolVersion.Major, ProtocolVersion.Minor, "runtime", 1, 1, 7,
            "sampling busy", true))));

        Assert.Equal(CaptureState.Ready, editor.Snapshot.State);
        Assert.True(editor.Start(TestConfiguration));
    }

    [Fact]
    public void DuplicateNegotiationAndNewRuntimeTokenOnReusedSessionAreSafe()
    {
        var endpoint = new EditorCaptureCoordinator("owner", _ => { });
        var hello = StrictWireAdapter.Serialize(new HelloMessage(ProtocolVersion.Major, ProtocolVersion.Minor, "one", "runtime", 4096));
        var capabilities = StrictWireAdapter.Serialize(new CapabilitiesMessage(ProtocolVersion.Major, ProtocolVersion.Minor, "one", 0,
            CaptureModes.Sampling, false, 2_000_000, 4096, 4096, 8));
        Assert.True(endpoint.Receive(hello));
        Assert.True(endpoint.Receive(capabilities));
        Assert.True(endpoint.Receive(hello));
        Assert.True(endpoint.Receive(capabilities));

        Assert.True(endpoint.Receive(StrictWireAdapter.Serialize(new HelloMessage(
            ProtocolVersion.Major, ProtocolVersion.Minor, "two", "runtime", 4096))));
        Assert.Equal("two", endpoint.Snapshot.RuntimeToken);
        Assert.Equal(CaptureModes.None, endpoint.Snapshot.SupportedModes);
        Assert.True(endpoint.Receive(StrictWireAdapter.Serialize(new CapabilitiesMessage(
            ProtocolVersion.Major, ProtocolVersion.Minor, "two", 0, CaptureModes.Sampling, false,
            2_000_000, 4096, 4096, 8))));
    }
    [Fact]
    public void Controller_projects_mode_aware_target_commands_settings_and_quality()
    {
        var view = new FakeView();
        var transport = new FakeTransport();
        var controller = new ProfilerDockController(view, transport, null);
        controller.UpdateSnapshot(Snapshot(CaptureState.Ready), "Game (PID 42)");

        Assert.Equal("Game (PID 42)", view.Last!.Target);
        Assert.Equal("Ready", view.Last.Status);
        Assert.Equal(["Sampling", "Automatic", "Manual"], view.Last.ModeSegments.Select(x => x.Label));
        Assert.Equal("Include Manual", view.Last.ManualOverlay.Label);
        Assert.Contains("Low overhead", view.Last.SettingsStatus);
        Assert.Contains("effective interval 2 ms", view.Last.SettingsStatus);
        Assert.Contains("Complete capture", view.Last.QualityBanner);
        Assert.True(view.Last.Commands.Start);
        Assert.False(view.Last.Commands.Stop);
    }

    [Fact]
    public void Mode_selection_never_starts_or_installs_and_start_is_an_explicit_command()
    {
        var view = new FakeView();
        var transport = new FakeTransport();
        var installer = new FakeInstaller();
        var controller = new ProfilerDockController(view, transport, installer);
        controller.UpdateSnapshot(Snapshot(CaptureState.Ready), "Game");

        controller.SelectMode(PrimaryMode.AutomaticInstrumentation);
        controller.SetManualOverlay(true);
        Assert.Empty(transport.Commands);
        Assert.Equal(0, installer.PreviewCalls);
        Assert.Equal(0, installer.ApplyCalls);

        controller.Start();
        Assert.Equal([ProfilerCommand.Start], transport.Commands);
        Assert.Equal(0, installer.PreviewCalls);
    }

    [Fact]
    public void Stop_clear_copy_and_export_are_independent_commands()
    {
        var view = new FakeView();
        var transport = new FakeTransport();
        var output = new FakeOutput();
        var controller = new ProfilerDockController(view, transport, null, output);
        controller.UpdateSnapshot(Snapshot(CaptureState.Capturing), "Game");
        controller.Stop();
        controller.ReplaceResults(Results());
        controller.UpdateSnapshot(Snapshot(CaptureState.Complete), "Game");
        controller.Copy(ExportFormat.VisibleCsv);
        controller.Export(ExportFormat.LosslessJson);
        controller.Clear();

        Assert.Equal([ProfilerCommand.Stop], transport.Commands);
        Assert.Equal([ExportFormat.VisibleCsv], output.Copied);
        Assert.Equal([ExportFormat.LosslessJson], output.Exported);
        Assert.False(view.Last!.Commands.Clear);
    }

    [Fact]
    public void Completed_source_separated_results_survive_disconnect_without_mixed_totals()
    {
        var view = new FakeView();
        var controller = new ProfilerDockController(view, new FakeTransport(), null);
        controller.ReplaceResults(Results());
        controller.UpdateSnapshot(Snapshot(CaptureState.Complete), "Game");
        controller.Disconnected("Target disconnected");

        Assert.Equal("Target disconnected", view.Last!.Status);
        Assert.True(view.Last.ResultsVisible);
        Assert.True(view.Last.Commands.Copy);
        Assert.True(view.Last.Commands.Export);
        Assert.Equal(3, view.Last.ResultGroups.Count);
        Assert.Equal("Samples", view.Last.ResultGroups[0].Columns[1]);
        Assert.Contains("Estimated stack-frame %", view.Last.ResultGroups[0].Columns);
        Assert.Contains("Calls", view.Last.ResultGroups[1].Columns);
        Assert.Contains("Wall time", view.Last.ResultGroups[1].Columns);
        Assert.DoesNotContain("Samples", view.Last.ResultGroups[1].Columns);
        Assert.All(view.Last.ResultGroups, group => Assert.False(group.IsCrossSourceTotal));
        Assert.Equal(3.125, view.Last.ResultGroups[1].Rows[0].AverageWallTimeMilliseconds);
        Assert.Equal(8.0, view.Last.ResultGroups[1].Rows[0].MaximumWallTimeMilliseconds);
    }

    [Fact]
    public void Automatic_settings_invalidate_a_pending_preview()
    {
        var view = new FakeView();
        var installer = new FakeInstaller();
        var controller = new ProfilerDockController(view, new FakeTransport(), installer);
        controller.UpdateSnapshot(Snapshot(CaptureState.Ready), "Game");
        controller.SelectMode(PrimaryMode.AutomaticInstrumentation);
        var preview = controller.PreviewAutomaticInstall();

        controller.UpdateAutomatic(new AutomaticSettings("Game.*", "Generated", 1000));

        Assert.NotNull(preview);
        Assert.False(controller.ApplyAutomaticInstall(preview!.Token!, confirmed: true));
        Assert.Contains("Preview required", view.Last!.InstallerStatus);
        Assert.Equal("", view.Last.InstallerPreviewDiff);
        Assert.Equal(0, installer.ApplyCalls);
    }

    [Fact]
    public void Settings_updates_are_normalized_and_retained_per_mode()
    {
        var controller = new ProfilerDockController(new FakeView(), new FakeTransport(), null);
        controller.UpdateSampling(new SamplingSettings(" Game ; Core ", " System ", 3_000_000));
        controller.UpdateAutomatic(new AutomaticSettings(" Game.* ", " Generated ", 200));
        controller.UpdateManual(new ManualSettings(" Scope "));

        Assert.Equal("Game;Core", controller.Configuration.Sampling.IncludeAssemblies);
        Assert.Equal("Game.*", controller.Configuration.Automatic.IncludePatterns);
        Assert.Equal("Scope", controller.Configuration.Manual.LabelPrefix);
    }

    [Theory]
    [InlineData(false, false, InstallerGate.Ready)]
    [InlineData(true, false, InstallerGate.NeedsBuild)]
    [InlineData(false, true, InstallerGate.NeedsRestart)]
    [InlineData(true, true, InstallerGate.NeedsRestart)]
    public void Apply_gate_prioritizes_restart_then_rebuild(bool rebuild, bool restart, InstallerGate gate)
    {
        Assert.Equal(gate, ProjectInstallerAdapter.GateFor(rebuild, restart));
    }

    [Fact]
    public void Preview_receives_current_automatic_settings_and_exposes_diff()
    {
        var view = new FakeView();
        var installer = new FakeInstaller();
        var controller = new ProfilerDockController(view, new FakeTransport(), installer);
        controller.UpdateSnapshot(Snapshot(CaptureState.Ready), "Game");
        controller.SelectMode(PrimaryMode.AutomaticInstrumentation);
        controller.UpdateAutomatic(new AutomaticSettings("Gameplay.*", "Generated", 321));

        controller.PreviewAutomaticInstall();

        Assert.Equal("Gameplay.*", installer.LastSettings!.IncludePatterns);
        Assert.Equal(321, installer.LastSettings.MaxMethods);
        Assert.Equal("diff", view.Last!.InstallerPreviewDiff);
    }

    [Fact]
    public void Installer_requires_preview_identity_and_confirmation_before_apply()
    {
        var view = new FakeView();
        var installer = new FakeInstaller();
        var controller = new ProfilerDockController(view, new FakeTransport(), installer);
        controller.UpdateSnapshot(Snapshot(CaptureState.Ready), "Game");

        var preview = controller.PreviewAutomaticInstall();
        Assert.NotNull(preview);
        Assert.Equal(1, installer.PreviewCalls);
        Assert.Equal(0, installer.ApplyCalls);
        Assert.False(controller.ApplyAutomaticInstall(preview!.Token!, confirmed: false));
        Assert.False(controller.ApplyAutomaticInstall("wrong", confirmed: true));
        Assert.Equal(0, installer.ApplyCalls);
        Assert.True(controller.ApplyAutomaticInstall(preview.Token!, confirmed: true));
        Assert.Equal(1, installer.ApplyCalls);
        Assert.Contains("Needs build", view.Last!.InstallerStatus);
        Assert.False(controller.ApplyAutomaticInstall(preview.Token!, confirmed: true));
    }

    [Fact]
    public void Automatic_uninstall_requires_its_own_preview_and_is_reachable_from_controller()
    {
        var view = new FakeView();
        var installer = new FakeInstaller();
        var controller = new ProfilerDockController(view, new FakeTransport(), installer);

        var preview = controller.PreviewAutomaticUninstall();

        Assert.NotNull(preview);
        Assert.Equal(1, installer.UninstallPreviewCalls);
        Assert.Equal(0, installer.PreviewCalls);
        Assert.False(controller.ApplyAutomaticInstall("wrong", confirmed: true));
        Assert.True(controller.ApplyAutomaticInstall(preview!.Token!, confirmed: true));
        Assert.Equal(1, installer.ApplyCalls);
    }

    [Theory]
    [InlineData(InstallerGate.PackageUnavailable, "Package unavailable")]
    [InlineData(InstallerGate.NeedsBuild, "Needs build")]
    [InlineData(InstallerGate.NeedsRestart, "Needs restart")]
    [InlineData(InstallerGate.Stale, "Stale build")]
    [InlineData(InstallerGate.NoMatches, "No matches")]
    public void Installer_gates_have_truthful_status(InstallerGate gate, string text)
    {
        var view = new FakeView();
        var installer = new FakeInstaller { Gate = gate };
        var controller = new ProfilerDockController(view, new FakeTransport(), installer);
        controller.UpdateSnapshot(Snapshot(CaptureState.Ready), "Game");

        var preview = controller.PreviewAutomaticInstall();
        Assert.Contains(text, view.Last!.InstallerStatus);
        if (gate == InstallerGate.PackageUnavailable)
        {
            Assert.Null(preview);
            Assert.Equal(0, installer.ApplyCalls);
        }
    }

    [Fact]
    public void Lifecycle_registers_and_removes_each_surface_once_and_disposes_coordinator_once()
    {
        var host = new FakeHost();
        var coordinator = new FakeCoordinator();
        var lifecycle = new ProfilerPluginLifecycle(host, coordinator);

        lifecycle.Enter();
        lifecycle.Enter();
        lifecycle.Exit();
        lifecycle.Exit();

        Assert.Equal((1, 1, 1, 1), (host.AddDock, host.AddDebugger, host.RemoveDebugger, host.RemoveDock));
        Assert.Equal(1, coordinator.DisposeCalls);
    }

    [Fact]
    public void Runtime_bridge_autoload_policy_recognizes_only_the_exact_owned_path()
    {
        Assert.True(ProfilerAutoloadPolicy.IsOwnedValue(ProfilerAutoloadPolicy.ScriptPath));
        Assert.True(ProfilerAutoloadPolicy.IsOwnedValue("*" + ProfilerAutoloadPolicy.ScriptPath));
        Assert.False(ProfilerAutoloadPolicy.IsOwnedValue("res://foreign/Bridge.cs"));
        Assert.False(ProfilerAutoloadPolicy.IsOwnedValue(null));
        Assert.Equal("autoload/GodotCSharpProfilerBridge", ProfilerAutoloadPolicy.Setting);
    }

    [Fact]
    public void Project_installer_adapter_checks_package_before_previewing()
    {
        using var project = TemporaryProject.Create();
        var adapter = new ProjectInstallerAdapter(new ProjectInstaller(project.Path), () => false);

        var preview = adapter.Preview(ModeConfiguration.Default.Automatic);

        Assert.Equal(InstallerGate.PackageUnavailable, preview.Gate);
        Assert.Null(preview.Token);
        Assert.Equal("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>",
            File.ReadAllText(System.IO.Path.Combine(project.Path, "Game.csproj")));
    }

    [Fact]
    public void Malformed_payloads_fail_closed_with_bounded_safe_status()
    {
        var view = new FakeView();
        var sink = new DebuggerPayloadGate(view, maximumStatusCharacters: 80);

        Assert.False(sink.TryAccept("cs_profiler:ready", [new string('x', 20_000)], out _));
        Assert.DoesNotContain('\n', view.Last!.Status);
        Assert.InRange(view.Last.Status.Length, 1, 80);
        Assert.False(sink.TryAccept(new string('m', 20_000), Array.Empty<object?>(), out _));
        Assert.InRange(view.Last.Status.Length, 1, 80);
    }

    [Fact]
    public void EditorRendersBoundedLabelsAndKeepsSameNumericIdsSeparateBySource()
    {
        var sent = new Queue<WireMap>();
        var editor = ReadyEditor(sent);
        Assert.True(editor.Start(TestConfiguration));
        sent.Clear();
        Assert.True(editor.Receive(State(CaptureState.Capturing, 1, QualityCounters.Zero)));
        Assert.True(editor.Receive(Batch(CaptureSource.Sampling, 2, [new(1, "Game.Tick", 3, 0)])));
        Assert.True(editor.Receive(Batch(CaptureSource.ManualSpans, 3, [new(1, "Gameplay/Tick", 2_000_000, 1)])));
        Assert.True(editor.Stop());
        Assert.True(editor.Receive(State(CaptureState.Complete, 5, new QualityCounters(2, 0, 0, 0))));

        Assert.Equal("Game.Tick", editor.CompletedResults.Groups.Single(x => x.Source == CaptureSource.Sampling).Rows.Single().Name);
        Assert.Equal("Gameplay/Tick", editor.CompletedResults.Groups.Single(x => x.Source == CaptureSource.ManualSpans).Rows.Single().Name);
    }

    [Fact]
    public void LateRowOverflowAndIdentityConflictRejectWithoutAnyVisibleOrPendingMutation()
    {
        var sent = new Queue<WireMap>();
        var editor = ReadyEditor(sent);
        Assert.True(editor.Start(TestConfiguration));
        sent.Clear();
        Assert.True(editor.Receive(State(CaptureState.Capturing, 1, QualityCounters.Zero)));
        Assert.True(editor.Receive(Batch(CaptureSource.Sampling, 2, [new(1, "Game.Tick", long.MaxValue, 0)])));
        var before = editor.Snapshot;

        Assert.False(editor.Receive(Batch(CaptureSource.Sampling, 3,
            [new(2, "Game.Render", 7, 0), new(1, "Game.Tick", 1, 0)], new QualityCounters(8, 1, 1, 1))));
        Assert.Equal(before, editor.Snapshot);
        Assert.False(editor.Receive(Batch(CaptureSource.Sampling, 3,
            [new(1, "Different.Method", 0, 0)])));
        Assert.Equal(before, editor.Snapshot);

        Assert.True(editor.Stop());
        Assert.True(editor.Receive(State(CaptureState.Complete, 4, before.Quality)));
        var rows = editor.CompletedResults.Groups.Single().Rows;
        Assert.Single(rows);
        Assert.Equal("Game.Tick", rows[0].Name);
        Assert.Equal(long.MaxValue, rows[0].Samples);
    }

    [Fact]
    public void TerminalZeroQualityCannotEraseAccumulatedBatchCounters()
    {
        var sent = new Queue<WireMap>();
        var editor = ReadyEditor(sent);
        Assert.True(editor.Start(TestConfiguration));
        sent.Clear();
        Assert.True(editor.Receive(State(CaptureState.Capturing, 1, QualityCounters.Zero)));
        var quality = new QualityCounters(10, 2, 3, 4);
        Assert.True(editor.Receive(Batch(CaptureSource.Sampling, 2, [new(1, "Game.Tick", 5, 0)], quality)));
        Assert.True(editor.Stop());
        Assert.True(editor.Receive(State(CaptureState.Complete, 4, QualityCounters.Zero)));
        Assert.Equal(quality, editor.Snapshot.Quality);
        Assert.Equal(5, editor.CompletedResults.Truncated);
    }

    private static ProtocolMessage Parse(WireMap payload)
    {
        Assert.True(new CaptureProtocolParser().TryParse(payload, out var message, out var failure), failure.ToString());
        return message!;
    }

    private static ModeConfiguration TestConfiguration => ModeConfiguration.Default with { IncludeManual = true };

    private static EditorCaptureCoordinator ReadyEditor(Queue<WireMap> sent)
    {
        var editor = new EditorCaptureCoordinator("owner", sent.Enqueue);
        Assert.True(editor.Receive(StrictWireAdapter.Serialize(new HelloMessage(ProtocolVersion.Major, ProtocolVersion.Minor, "runtime", "runtime", 4096))));
        Assert.True(editor.Receive(StrictWireAdapter.Serialize(new CapabilitiesMessage(ProtocolVersion.Major, ProtocolVersion.Minor, "runtime", 0,
            CaptureModes.Sampling | CaptureModes.ManualScopes, true, 2_000_000, 4096, 4096, 8))));
        return editor;
    }

    private static WireMap State(CaptureState state, long sequence, QualityCounters quality) =>
        StrictWireAdapter.Serialize(new StateMessage(ProtocolVersion.Major, ProtocolVersion.Minor, "runtime", 1, sequence, TestConfiguration.Fingerprint,
            state, CaptureSource.Sampling,
            state == CaptureState.Complete ? CaptureCompleteness.Complete : CaptureCompleteness.InProgress,
            PartialReason.None, quality));

    private static WireMap Batch(CaptureSource source, long sequence, IReadOnlyList<MethodSample> methods,
        QualityCounters? quality = null) => StrictWireAdapter.Serialize(new BatchMessage(ProtocolVersion.Major, ProtocolVersion.Minor, "runtime", 1,
            sequence, TestConfiguration.Fingerprint, source, source != CaptureSource.Sampling, false,
            quality ?? QualityCounters.Zero, methods));

    private static CaptureSnapshot Snapshot(CaptureState state) => new(
        state, "runtime", 1, 1, ModeConfiguration.Default.Fingerprint, null,
        CaptureModes.Sampling, CaptureSource.Sampling,
        state == CaptureState.Partial ? CaptureCompleteness.Partial : CaptureCompleteness.Complete,
        state == CaptureState.Partial ? PartialReason.TransportLoss : PartialReason.None,
        new QualityCounters(12, 1, 0, 0),
        CaptureModes.Sampling | CaptureModes.AutomaticInstrumentation | CaptureModes.ManualScopes,
        false, 2_000_000, 50_000);

    private static ProfilerResults Results() => new([
        new SourceResultGroup(CaptureSource.Sampling, [new ResultRow("Tick", 8, 75, 0, 0, 0, 0)]),
        new SourceResultGroup(CaptureSource.AutomaticSpans, [new ResultRow("Run", 0, 0, 4, 12.5, 3.125, 8)]),
        new SourceResultGroup(CaptureSource.ManualSpans, [new ResultRow("Scope", 0, 0, 2, 3.5, 1.75, 2)])
    ], 2);

    private sealed class FakeView : IProfilerDockView
    {
        public ProfilerDockViewState? Last { get; private set; }
        public void Render(ProfilerDockViewState state) => Last = state;
    }

    private sealed class FakeTransport : IProfilerCommandTransport
    {
        public List<ProfilerCommand> Commands { get; } = [];
        public void Send(ProfilerCommand command, ModeConfiguration configuration) => Commands.Add(command);
    }

    private sealed class FakeOutput : IProfilerOutput
    {
        public List<ExportFormat> Copied { get; } = [];
        public List<ExportFormat> Exported { get; } = [];
        public void Copy(ExportFormat format, ProfilerResults results) => Copied.Add(format);
        public void Export(ExportFormat format, ProfilerResults results) => Exported.Add(format);
    }

    private sealed class FakeInstaller : IAutomaticInstaller
    {
        public InstallerGate Gate { get; set; } = InstallerGate.Ready;
        public int PreviewCalls { get; private set; }
        public int UninstallPreviewCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public AutomaticSettings? LastSettings { get; private set; }
        public InstallerPreviewResult Preview(AutomaticSettings settings)
        {
            PreviewCalls++;
            LastSettings = settings;
            return Gate == InstallerGate.PackageUnavailable
                ? new InstallerPreviewResult(Gate, null, "unavailable", 0)
                : new InstallerPreviewResult(Gate, "token", "diff", 2);
        }
        public InstallerApplyResult Apply(string previewToken)
        {
            ApplyCalls++;
            return new InstallerApplyResult(InstallerGate.NeedsBuild, true, true, true);
        }
        public InstallerPreviewResult PreviewUninstall()
        {
            UninstallPreviewCalls++;
            return new InstallerPreviewResult(InstallerGate.Ready, "uninstall-token", "uninstall-diff", 2);
        }
    }

    private sealed class FakeHost : IProfilerPluginHost
    {
        public int AddDock, AddDebugger, RemoveDock, RemoveDebugger;
        public void RegisterDock() => AddDock++;
        public void RegisterDebugger() => AddDebugger++;
        public void UnregisterDock() => RemoveDock++;
        public void UnregisterDebugger() => RemoveDebugger++;
    }

    private sealed class FakeCoordinator : ICoordinatorLifetime
    {
        public int DisposeCalls { get; private set; }
        public void RequestDispose() => DisposeCalls++;
    }

    private sealed class TemporaryProject : IDisposable
    {
        public string Path { get; }
        private TemporaryProject(string path) => Path = path;
        public static TemporaryProject Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            File.WriteAllText(System.IO.Path.Combine(path, "Game.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
            return new TemporaryProject(path);
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
