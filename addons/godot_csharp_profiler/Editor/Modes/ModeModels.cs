#nullable enable
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Apeworks.GodotCSharpProfiler.Protocol;

namespace Apeworks.GodotCSharpProfiler.Editor.Modes;

public enum PrimaryMode { Sampling, AutomaticInstrumentation, None }
public enum AutomaticBuildStatus { Ready, NeedsBuild, NeedsRestart, NoMatches, StaleBuild }
public enum OverheadLevel { Low, Moderate, High }
public enum ResultColumn { Name, Samples, EstimatedStackFrameShare, ObservedWallTime, Calls, AverageWallTime, MaximumWallTime, CpuTime }

public sealed record SamplingSettings(string IncludeAssemblies, string ExcludeAssemblies, long RequestedIntervalNanoseconds)
{
    internal SamplingSettings Normalize() => new(
        ModeConfiguration.NormalizeList(IncludeAssemblies),
        ModeConfiguration.NormalizeList(ExcludeAssemblies),
        RequestedIntervalNanoseconds);
}

public sealed record AutomaticSettings(string IncludePatterns, string ExcludePatterns, int MaxMethods)
{
    internal AutomaticSettings Normalize() => new(
        ModeConfiguration.NormalizeList(IncludePatterns),
        ModeConfiguration.NormalizeList(ExcludePatterns),
        MaxMethods);
}

public sealed record ManualSettings(string LabelPrefix)
{
    internal ManualSettings Normalize() => new((LabelPrefix ?? string.Empty).Trim());
}

public sealed record ModeConfiguration(
    PrimaryMode Primary,
    bool IncludeManual,
    SamplingSettings Sampling,
    AutomaticSettings Automatic,
    ManualSettings Manual)
{
    public static ModeConfiguration Default { get; } = new(
        PrimaryMode.Sampling,
        false,
        new SamplingSettings(string.Empty, string.Empty, 2_000_000),
        new AutomaticSettings("Game", string.Empty, 4_096),
        new ManualSettings(string.Empty));

    public CaptureModes Modes => Primary switch
    {
        PrimaryMode.Sampling => CaptureModes.Sampling | (IncludeManual ? CaptureModes.ManualScopes : CaptureModes.None),
        PrimaryMode.AutomaticInstrumentation => CaptureModes.AutomaticInstrumentation | (IncludeManual ? CaptureModes.ManualScopes : CaptureModes.None),
        _ => IncludeManual ? CaptureModes.ManualScopes : CaptureModes.None
    };

    public string Fingerprint
    {
        get
        {
            var normalized = Normalize();
            var canonical = FormattableString.Invariant(
                $"modes={(int)normalized.Modes}|sampling.include={normalized.Sampling.IncludeAssemblies.ToLowerInvariant()}|sampling.exclude={normalized.Sampling.ExcludeAssemblies.ToLowerInvariant()}|sampling.interval={normalized.Sampling.RequestedIntervalNanoseconds}|automatic.include={normalized.Automatic.IncludePatterns.ToLowerInvariant()}|automatic.exclude={normalized.Automatic.ExcludePatterns.ToLowerInvariant()}|automatic.max={normalized.Automatic.MaxMethods}|manual.prefix={normalized.Manual.LabelPrefix}");
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..ProtocolLimits.FingerprintCharacters].ToLowerInvariant();
        }
    }

    public ModeConfiguration Normalize() => this with
    {
        Sampling = Sampling.Normalize(),
        Automatic = Automatic.Normalize(),
        Manual = Manual.Normalize()
    };

    internal static string NormalizeList(string? value) => string.Join(';',
        (value ?? string.Empty).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
}

public sealed class ModeUiController
{
    public ModeConfiguration Configuration { get; private set; } = ModeConfiguration.Default;

    public void Restore(ModeConfiguration configuration) =>
        Configuration = (configuration ?? ModeConfiguration.Default).Normalize();

    public void SelectPrimary(PrimaryMode mode) => Configuration = Configuration with
    {
        Primary = mode,
        IncludeManual = mode == PrimaryMode.None || Configuration.IncludeManual
    };

    public void SelectManualOnly() => Configuration = Configuration with { Primary = PrimaryMode.None, IncludeManual = true };
    public void SetManualOverlay(bool included) => Configuration = Configuration with { IncludeManual = included };
    public void UpdateSampling(SamplingSettings settings) => Configuration = Configuration with { Sampling = settings.Normalize() };
    public void UpdateAutomatic(AutomaticSettings settings) => Configuration = Configuration with { Automatic = settings.Normalize() };
    public void UpdateManual(ManualSettings settings) => Configuration = Configuration with { Manual = settings.Normalize() };
}

public sealed record AutomaticFacts(AutomaticBuildStatus BuildStatus, int Eligible, int Instrumented, int Skipped)
{
    public static AutomaticFacts Ready { get; } = new(AutomaticBuildStatus.Ready, 0, 0, 0);
}

public sealed record ModePresentationInput(
    CaptureState State,
    CaptureModes SupportedModes,
    bool SamplingIntervalRuntimeConfigurable,
    long EffectiveSamplingIntervalNanoseconds,
    int CapabilityMaxMethods,
    ModeConfiguration Configuration,
    bool HasResults,
    IReadOnlyList<CaptureSource> ResultSources,
    CaptureCompleteness Completeness,
    PartialReason PartialReason,
    QualityCounters Quality,
    long Truncated,
    AutomaticFacts Automatic)
{
    public static ModePresentationInput FromSnapshot(CaptureSnapshot snapshot, ModeConfiguration configuration,
        bool hasResults, IReadOnlyList<CaptureSource> resultSources, long truncated, AutomaticFacts automatic) => new(
        snapshot.State, snapshot.SupportedModes, snapshot.SamplingIntervalRuntimeConfigurable,
        snapshot.EffectiveSamplingIntervalNanoseconds, snapshot.CapabilityMaxMethods, configuration,
        hasResults, resultSources, snapshot.Completeness, snapshot.PartialReason, snapshot.Quality, truncated, automatic);
}

public sealed record Availability(bool Enabled, string? Reason = null, string? Remediation = null);
public sealed record ModeAvailability(Availability Sampling, Availability Automatic, Availability Manual);
public sealed record CommandState(bool Enabled, string? Reason = null);
public sealed record CommandPresentation(CommandState Start, CommandState Stop, CommandState Clear, CommandState Copy, CommandState Export);
public sealed record IntervalPresentation(bool Visible, bool Editable, string Display);
public sealed record SamplingPresentation(IntervalPresentation Interval);
public sealed record AutomaticPresentation(string Status, int Eligible, int Instrumented, int Skipped);
public sealed record QualityPresentation(bool Visible, string Banner);
public sealed record ExportOption(string Name, bool Available, string Description);
public sealed record ExportOptionPresentation(ExportOption LosslessJson, ExportOption VisibleCsv, ExportOption ChromeTrace);
public sealed record ModePresentation(
    ModeAvailability Modes,
    CommandPresentation Commands,
    SamplingPresentation Sampling,
    AutomaticPresentation Automatic,
    OverheadLevel Overhead,
    bool ResultsVisible,
    IReadOnlyDictionary<CaptureSource, IReadOnlyList<ResultColumn>> Columns,
    bool SeparateSourceSections,
    bool SumAcrossSources,
    QualityPresentation Quality,
    ExportOptionPresentation CopyOptions,
    ExportOptionPresentation ExportOptions);
