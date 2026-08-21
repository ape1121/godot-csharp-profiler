#nullable enable
using Apeworks.GodotCSharpProfiler.Protocol;

namespace Apeworks.GodotCSharpProfiler.Editor.Modes;

public static class ModePresenter
{
    private static readonly CaptureState[] ModeLockedStates =
        [CaptureState.Starting, CaptureState.Capturing, CaptureState.Stopping];

    public static ModePresentation Present(ModePresentationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var config = input.Configuration.Normalize();
        var validation = Validate(config, input);
        var mayStart = input.State is CaptureState.Ready or CaptureState.Complete or CaptureState.Partial;
        var hasResults = input.HasResults;
        var sources = input.ResultSources.Distinct().ToArray();
        var columns = sources.ToDictionary(source => source, source => ColumnsFor([source]));
        var options = ExportOptions(hasResults, sources);
        return new ModePresentation(
            new ModeAvailability(
                AvailabilityFor(input, CaptureModes.Sampling, "Sampling"),
                AvailabilityFor(input, CaptureModes.AutomaticInstrumentation, "Automatic Instrumentation"),
                AvailabilityFor(input, CaptureModes.ManualScopes, "Manual Scopes")),
            new CommandPresentation(
                new CommandState(mayStart && validation is null, validation ?? StartStateReason(input.State)),
                new CommandState(input.State is CaptureState.Starting or CaptureState.Capturing,
                    input.State is CaptureState.Starting or CaptureState.Capturing ? null : "No capture is running."),
                new CommandState(!ModeLockedStates.Contains(input.State) && hasResults, hasResults ? null : "There are no results to clear."),
                new CommandState(!ModeLockedStates.Contains(input.State) && hasResults, hasResults ? null : "There are no results to copy."),
                new CommandState(!ModeLockedStates.Contains(input.State) && hasResults, hasResults ? null : "There are no results to export.")),
            new SamplingPresentation(Interval(input)),
            new AutomaticPresentation(AutomaticStatus(input.Automatic.BuildStatus), input.Automatic.Eligible,
                input.Automatic.Instrumented, input.Automatic.Skipped),
            Overhead(config),
            hasResults,
            columns,
            sources.Length > 1,
            false,
            Quality(input),
            options,
            options);
    }

    public static IReadOnlyList<ResultColumn> ColumnsFor(IReadOnlyCollection<CaptureSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count != 1)
            return [ResultColumn.Name];
        return sources.Single() == CaptureSource.Sampling
            ? [ResultColumn.Name, ResultColumn.Samples, ResultColumn.EstimatedStackFrameShare]
            : [ResultColumn.Name, ResultColumn.ObservedWallTime, ResultColumn.Calls,
                ResultColumn.AverageWallTime, ResultColumn.LargestBatchAverageWallTime];
    }

    private static Availability AvailabilityFor(ModePresentationInput input, CaptureModes mode, string name)
    {
        if (ModeLockedStates.Contains(input.State))
            return new(false, "Mode is locked while a capture is active.", "Stop the capture before changing modes.");
        if (input.State == CaptureState.Disconnected)
            return new(false, "Target is disconnected.", "Start the target and reconnect.");
        if (input.State == CaptureState.Negotiating)
            return new(false, "Target capabilities are being negotiated.", "Wait for capability negotiation to finish.");
        if ((input.SupportedModes & mode) == 0)
        {
            var remediation = mode switch
            {
                CaptureModes.AutomaticInstrumentation => "Install or enable the automatic instrumentation runtime, then reconnect.",
                CaptureModes.Sampling => "Use a runtime with managed sampling support, then reconnect.",
                _ => "Enable the manual scopes runtime, then reconnect."
            };
            return new(false, $"Target does not support {name}.", remediation);
        }
        return new(true);
    }

    private static IntervalPresentation Interval(ModePresentationInput input)
    {
        if (input.SamplingIntervalRuntimeConfigurable)
            return new(true, true, "Editable for this capture session");
        var display = input.EffectiveSamplingIntervalNanoseconds > 0
            ? $"Startup-only; effective interval {FormatInterval(input.EffectiveSamplingIntervalNanoseconds)}"
            : "Startup-only; effective interval unknown";
        return new(true, false, display);
    }

    private static string FormatInterval(long nanoseconds)
    {
        if (nanoseconds % 1_000_000 == 0) return $"{nanoseconds / 1_000_000} ms";
        if (nanoseconds % 1_000 == 0) return $"{nanoseconds / 1_000} µs";
        return $"{nanoseconds} ns";
    }

        private static string? Validate(ModeConfiguration config, ModePresentationInput input)
    {
        if (config.Modes == CaptureModes.None) return "At least one capture mode is required.";
        if ((config.Modes & ~input.SupportedModes) != 0) return "Selected mode is not supported by the target.";
        if (config.Primary == PrimaryMode.Sampling)
        {
            var hostile = ControlCharacterReason(config.Sampling.IncludeAssemblies, "Sampling include assemblies")
                ?? ControlCharacterReason(config.Sampling.ExcludeAssemblies, "Sampling exclude assemblies");
            if (hostile is not null) return hostile;
            if (config.Sampling.RequestedIntervalNanoseconds is < ProtocolLimits.MinSamplingIntervalNanoseconds or > ProtocolLimits.MaxSamplingIntervalNanoseconds)
                return $"Sampling interval must be between {ProtocolLimits.MinSamplingIntervalNanoseconds} and {ProtocolLimits.MaxSamplingIntervalNanoseconds} nanoseconds.";
        }
        if (config.Primary == PrimaryMode.AutomaticInstrumentation)
        {
            if (string.IsNullOrWhiteSpace(config.Automatic.IncludePatterns))
                return "Automatic include patterns is required.";
            var hostile = ControlCharacterReason(config.Automatic.IncludePatterns, "Automatic include patterns")
                ?? ControlCharacterReason(config.Automatic.ExcludePatterns, "Automatic exclude patterns");
            if (hostile is not null) return hostile;
            var max = Math.Min(input.CapabilityMaxMethods, ProtocolLimits.MaxConfiguredMethods);
            if (config.Automatic.MaxMethods < 1 || config.Automatic.MaxMethods > max)
                return $"Automatic max methods must be between 1 and {max}.";
            if (input.Automatic.BuildStatus != AutomaticBuildStatus.Ready)
                return $"Automatic instrumentation {AutomaticStatus(input.Automatic.BuildStatus).ToLowerInvariant()}.";
        }
        return ControlCharacterReason(config.Manual.LabelPrefix, "Manual label prefix");
    }

    private static string? ControlCharacterReason(string value, string field) =>
        value.Any(char.IsControl) ? $"{field} contains a control character." : null;

    private static string? StartStateReason(CaptureState state) => state switch
    {
        CaptureState.Disconnected => "Target is disconnected.",
        CaptureState.Negotiating => "Target capability negotiation is in progress.",
        CaptureState.Starting or CaptureState.Capturing or CaptureState.Stopping => "A capture is already active.",
        CaptureState.Busy => "Target is busy with another capture owner.",
        CaptureState.Error => "Resolve the target error before starting.",
        _ => null
    };

    private static string AutomaticStatus(AutomaticBuildStatus status) => status switch
    {
        AutomaticBuildStatus.NeedsBuild => "Needs build",
        AutomaticBuildStatus.NeedsRestart => "Needs restart",
        AutomaticBuildStatus.NoMatches => "No matches",
        AutomaticBuildStatus.StaleBuild => "Stale build",
        _ => "Ready"
    };

    private static OverheadLevel Overhead(ModeConfiguration config)
    {
        var level = config.Primary switch
        {
            PrimaryMode.Sampling when config.Sampling.RequestedIntervalNanoseconds >= 2_000_000 => OverheadLevel.Low,
            PrimaryMode.Sampling => OverheadLevel.Moderate,
            PrimaryMode.AutomaticInstrumentation when config.Automatic.MaxMethods > 10_000 => OverheadLevel.High,
            PrimaryMode.AutomaticInstrumentation => OverheadLevel.Moderate,
            _ => OverheadLevel.Low
        };
        if (config.IncludeManual && level == OverheadLevel.Moderate && config.Automatic.MaxMethods > 10_000)
            return OverheadLevel.High;
        return level;
    }

    private static QualityPresentation Quality(ModePresentationInput input)
    {
        var partial = input.Completeness == CaptureCompleteness.Partial || input.State == CaptureState.Partial;
        var reason = input.PartialReason switch
        {
            PartialReason.RequestedStop => "Requested stop",
            PartialReason.BufferOverflow => "Buffer overflow",
            PartialReason.TransportLoss => "Transport loss",
            PartialReason.RuntimeError => "Runtime error",
            PartialReason.Disconnected => "Disconnected",
            _ => partial ? "Partial capture" : "Complete capture"
        };
        var q = input.Quality;
        return new(true, $"{reason} · Observed {q.Observed} · Dropped {q.Dropped} · Overflowed {q.Overflowed} · Invalid {q.Invalid} · Truncated {input.Truncated}");
    }

    private static ExportOptionPresentation ExportOptions(bool hasResults, IReadOnlyCollection<CaptureSource> sources)
    {
        var hasSpans = sources.Any(source => source is CaptureSource.AutomaticSpans or CaptureSource.ManualSpans);
        return new(
            new ExportOption("Lossless JSON", hasResults, "All source-separated values and quality metadata."),
            new ExportOption("Visible CSV", hasResults, "Only currently visible columns and rows."),
            new ExportOption("Chrome trace", hasResults && hasSpans, hasSpans ? "Exact span timeline." : "Requires exact span data."));
    }
}
