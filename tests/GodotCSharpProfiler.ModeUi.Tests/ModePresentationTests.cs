using Apeworks.GodotCSharpProfiler.Editor.Modes;
using Apeworks.GodotCSharpProfiler.Protocol;

namespace GodotCSharpProfiler.ModeUi.Tests;

public sealed class ModePresentationTests
{
    [Fact]
    public void Selection_enforces_exclusive_primary_and_retains_each_modes_settings()
    {
        var controller = new ModeUiController();
        controller.UpdateSampling(new SamplingSettings(" Game ; Core ", "System", 2_000_000));
        controller.UpdateAutomatic(new AutomaticSettings(" Game.* ", " Generated ", 500));
        controller.SelectPrimary(PrimaryMode.AutomaticInstrumentation);
        controller.SetManualOverlay(true);

        Assert.Equal(CaptureModes.AutomaticInstrumentation | CaptureModes.ManualScopes, controller.Configuration.Modes);
        Assert.Equal("Game;Core", controller.Configuration.Sampling.IncludeAssemblies);
        Assert.Equal("Game.*", controller.Configuration.Automatic.IncludePatterns);
        controller.SelectManualOnly();
        Assert.Equal(CaptureModes.ManualScopes, controller.Configuration.Modes);
        controller.SelectPrimary(PrimaryMode.Sampling);
        Assert.Equal(CaptureModes.Sampling | CaptureModes.ManualScopes, controller.Configuration.Modes);
        controller.SetManualOverlay(false);
        Assert.Equal(CaptureModes.Sampling, controller.Configuration.Modes);
    }

    [Fact]
    public void Fingerprint_is_normalized_stable_and_sensitive_to_configuration()
    {
        var first = ModeConfiguration.Default with
        {
            Sampling = new SamplingSettings(" Game ; Core ", " System ", 2_000_000)
        };
        var equivalent = ModeConfiguration.Default with
        {
            Sampling = new SamplingSettings("game;core", "system", 2_000_000)
        };
        var changed = equivalent with { IncludeManual = true };

        Assert.Equal(first.Fingerprint, equivalent.Fingerprint);
        Assert.Equal(ProtocolLimits.FingerprintCharacters, first.Fingerprint.Length);
        Assert.NotEqual(first.Fingerprint, changed.Fingerprint);
    }

    [Theory]
    [InlineData(CaptureState.Disconnected, false, false, true, true, true)]
    [InlineData(CaptureState.Negotiating, false, false, true, true, true)]
    [InlineData(CaptureState.Ready, true, false, true, true, true)]
    [InlineData(CaptureState.Starting, false, true, false, false, false)]
    [InlineData(CaptureState.Capturing, false, true, false, false, false)]
    [InlineData(CaptureState.Stopping, false, false, false, false, false)]
    [InlineData(CaptureState.Complete, true, false, true, true, true)]
    [InlineData(CaptureState.Partial, true, false, true, true, true)]
    [InlineData(CaptureState.Busy, false, false, true, true, true)]
    [InlineData(CaptureState.Error, false, false, true, true, true)]
    public void Commands_follow_capture_state(CaptureState state, bool start, bool stop, bool clear, bool copy, bool export)
    {
        var vm = ModePresenter.Present(Input(state: state, hasResults: true));
        Assert.Equal((start, stop, clear, copy, export),
            (vm.Commands.Start.Enabled, vm.Commands.Stop.Enabled, vm.Commands.Clear.Enabled,
             vm.Commands.Copy.Enabled, vm.Commands.Export.Enabled));
    }

    [Fact]
    public void Completed_results_survive_unavailable_target()
    {
        var vm = ModePresenter.Present(Input(state: CaptureState.Disconnected, hasResults: true));
        Assert.True(vm.ResultsVisible);
        Assert.True(vm.Commands.Copy.Enabled);
        Assert.True(vm.Commands.Export.Enabled);
        Assert.False(vm.Commands.Start.Enabled);
    }

    [Fact]
    public void Availability_explains_unsupported_and_locked_segments()
    {
        var disconnected = ModePresenter.Present(Input(state: CaptureState.Disconnected));
        Assert.False(disconnected.Modes.Sampling.Enabled);
        Assert.Equal("Target is disconnected.", disconnected.Modes.Sampling.Reason);
        Assert.Equal("Start the target and reconnect.", disconnected.Modes.Sampling.Remediation);

        var unsupported = ModePresenter.Present(Input(supported: CaptureModes.ManualScopes));
        Assert.False(unsupported.Modes.Automatic.Enabled);
        Assert.Equal("Target does not support Automatic Instrumentation.", unsupported.Modes.Automatic.Reason);
        Assert.Equal("Install or enable the automatic instrumentation runtime, then reconnect.", unsupported.Modes.Automatic.Remediation);

        var capturing = ModePresenter.Present(Input(state: CaptureState.Capturing));
        Assert.False(capturing.Modes.Sampling.Enabled);
        Assert.Equal("Mode is locked while a capture is active.", capturing.Modes.Sampling.Reason);
    }

    [Fact]
    public void Sampling_interval_is_startup_only_and_not_editable_when_capability_is_false()
    {
        var unknown = ModePresenter.Present(Input(runtimeInterval: false, effectiveInterval: 0));
        Assert.True(unknown.Sampling.Interval.Visible);
        Assert.False(unknown.Sampling.Interval.Editable);
        Assert.Equal("Startup-only; effective interval unknown", unknown.Sampling.Interval.Display);

        var known = ModePresenter.Present(Input(runtimeInterval: false, effectiveInterval: 2_000_000));
        Assert.False(known.Sampling.Interval.Editable);
        Assert.Equal("Startup-only; effective interval 2 ms", known.Sampling.Interval.Display);

        var editable = ModePresenter.Present(Input(runtimeInterval: true));
        Assert.True(editable.Sampling.Interval.Editable);
    }

    [Theory]
    [InlineData(AutomaticBuildStatus.NeedsBuild, "Needs build")]
    [InlineData(AutomaticBuildStatus.NeedsRestart, "Needs restart")]
    [InlineData(AutomaticBuildStatus.NoMatches, "No matches")]
    [InlineData(AutomaticBuildStatus.StaleBuild, "Stale build")]
    public void Automatic_status_and_counts_are_explicit(AutomaticBuildStatus status, string label)
    {
        var input = Input() with { Automatic = new AutomaticFacts(status, 100, 70, 30) };
        var vm = ModePresenter.Present(input);
        Assert.Equal(label, vm.Automatic.Status);
        Assert.Equal((100, 70, 30), (vm.Automatic.Eligible, vm.Automatic.Instrumented, vm.Automatic.Skipped));
    }

    [Theory]
    [InlineData(PrimaryMode.Sampling, false, 5_000_000, 100, OverheadLevel.Low)]
    [InlineData(PrimaryMode.Sampling, false, 500_000, 100, OverheadLevel.Moderate)]
    [InlineData(PrimaryMode.AutomaticInstrumentation, false, 0, 2_000, OverheadLevel.Moderate)]
    [InlineData(PrimaryMode.AutomaticInstrumentation, true, 0, 20_000, OverheadLevel.High)]
    public void Overhead_is_based_on_mode_and_bounds(PrimaryMode mode, bool manual, long interval, int methods, OverheadLevel expected)
    {
        var config = ModeConfiguration.Default with
        {
            Primary = mode,
            IncludeManual = manual,
            Sampling = ModeConfiguration.Default.Sampling with { RequestedIntervalNanoseconds = interval },
            Automatic = ModeConfiguration.Default.Automatic with { MaxMethods = methods }
        };
        Assert.Equal(expected, ModePresenter.Present(Input() with { Configuration = config }).Overhead);
    }

    [Fact]
    public void Source_columns_never_claim_or_sum_incompatible_metrics()
    {
        var sampling = ModePresenter.ColumnsFor([CaptureSource.Sampling]);
        Assert.Contains(ResultColumn.Samples, sampling);
        Assert.Contains(ResultColumn.EstimatedStackFrameShare, sampling);
        Assert.DoesNotContain(ResultColumn.Calls, sampling);
        Assert.DoesNotContain(ResultColumn.AverageWallTime, sampling);
        Assert.DoesNotContain(ResultColumn.LargestBatchAverageWallTime, sampling);

        var exact = ModePresenter.ColumnsFor([CaptureSource.AutomaticSpans]);
        Assert.Contains(ResultColumn.ObservedWallTime, exact);
        Assert.Contains(ResultColumn.Calls, exact);
        Assert.Contains(ResultColumn.LargestBatchAverageWallTime, exact);
        Assert.DoesNotContain(ResultColumn.CpuTime, exact);

        var mixed = ModePresenter.Present(Input() with { ResultSources = [CaptureSource.Sampling, CaptureSource.ManualSpans] });
        Assert.True(mixed.SeparateSourceSections);
        Assert.False(mixed.SumAcrossSources);
    }

    [Fact]
    public void Quality_and_partial_reasons_are_always_visible()
    {
        var input = Input(state: CaptureState.Partial) with
        {
            Completeness = CaptureCompleteness.Partial,
            PartialReason = PartialReason.TransportLoss,
            Quality = new QualityCounters(50, 2, 3, 4),
            Truncated = 7
        };
        var quality = ModePresenter.Present(input).Quality;
        Assert.True(quality.Visible);
        Assert.Contains("Transport loss", quality.Banner);
        Assert.Contains("Dropped 2", quality.Banner);
        Assert.Contains("Overflowed 3", quality.Banner);
        Assert.Contains("Invalid 4", quality.Banner);
        Assert.Contains("Truncated 7", quality.Banner);
    }

    [Fact]
    public void Copy_and_export_options_distinguish_lossless_visible_and_trace_formats()
    {
        var sampling = ModePresenter.Present(Input(hasResults: true));
        Assert.Equal(sampling.ExportOptions, sampling.CopyOptions);
        Assert.True(sampling.ExportOptions.LosslessJson.Available);
        Assert.True(sampling.ExportOptions.VisibleCsv.Available);
        Assert.False(sampling.ExportOptions.ChromeTrace.Available);

        var spans = ModePresenter.Present(Input(hasResults: true) with { ResultSources = [CaptureSource.ManualSpans] }).ExportOptions;
        Assert.True(spans.ChromeTrace.Available);
    }

    [Fact]
    public void Protocol_snapshot_is_used_directly_for_capability_negotiation()
    {
        var snapshot = new CaptureSnapshot(CaptureState.Ready, "runtime", 0, 0, null, null,
            CaptureModes.None, CaptureSource.Sampling, CaptureCompleteness.Complete, PartialReason.None,
            QualityCounters.Zero, CaptureModes.ManualScopes, false, 0, 123);
        var input = ModePresentationInput.FromSnapshot(snapshot, ModeConfiguration.Default, false,
            [CaptureSource.Sampling], 0, AutomaticFacts.Ready);
        var vm = ModePresenter.Present(input);

        Assert.False(vm.Modes.Sampling.Enabled);
        Assert.True(vm.Modes.Manual.Enabled);
        Assert.Equal(123, input.CapabilityMaxMethods);
    }

    [Fact]
    public void Empty_sampling_include_means_all_managed_assemblies()
    {
        var vm = ModePresenter.Present(Input() with { Configuration = ModeConfiguration.Default });
        Assert.True(vm.Commands.Start.Enabled);
    }

    [Fact]
    public void Invalid_sampling_interval_disables_start_with_exact_reason()
    {
        var config = ModeConfiguration.Default with
        {
            Sampling = new SamplingSettings("Game", "System", -1)
        };
        var vm = ModePresenter.Present(Input() with { Configuration = config });
        Assert.False(vm.Commands.Start.Enabled);
        Assert.Equal("Sampling interval must be between 100000 and 1000000000 nanoseconds.", vm.Commands.Start.Reason);
    }

    [Fact]
    public void Invalid_automatic_pattern_has_exact_field_reason()
    {
        var config = ModeConfiguration.Default with
        {
            Primary = PrimaryMode.AutomaticInstrumentation,
            Automatic = new AutomaticSettings("Game\nInjected", "", 100)
        };
        var vm = ModePresenter.Present(Input() with { Configuration = config });
        Assert.False(vm.Commands.Start.Enabled);
        Assert.Equal("Automatic include patterns contains a control character.", vm.Commands.Start.Reason);
    }

    private static ModePresentationInput Input(CaptureState state = CaptureState.Ready, bool hasResults = false,
        CaptureModes supported = CaptureModes.Sampling | CaptureModes.AutomaticInstrumentation | CaptureModes.ManualScopes,
        bool runtimeInterval = false, long effectiveInterval = 0) => new(
        state, supported, runtimeInterval, effectiveInterval, 50_000,
        ModeConfiguration.Default, hasResults, [CaptureSource.Sampling], CaptureCompleteness.Complete,
        PartialReason.None, QualityCounters.Zero, 0, AutomaticFacts.Ready);
}
