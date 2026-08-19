#nullable enable
using Apeworks.GodotCSharpProfiler.Editor.Modes;
using Apeworks.GodotCSharpProfiler.Protocol;
using System;

namespace Apeworks.GodotCSharpProfiler.Editor.Integration;

public sealed record ProfilerDockLayout(
    bool ShowPrimaryToolbar,
    bool ShowCalls,
    bool ShowSettingsButton,
    bool ShowInlineSettings,
    bool ShowQualityDetails,
    int GraphMinimumHeight);

/// <summary>Responsive layout contract: capture/results stay primary; advanced configuration never consumes the dock.</summary>
public static class ProfilerDockLayoutPolicy
{
    public static ProfilerDockLayout ForHeight(float height)
    {
        var compact = height < 300;
        // The timeline strip scales with the dock instead of staying a fixed sliver squeezed
        // between the toolbar and the results tab bar: ~30% of dock height, floored so bars stay
        // readable in short docks and capped so calls always keep the majority of the space.
        var graphHeight = (int)Math.Clamp(height * 0.30f, compact ? 36 : 72, 260);
        return new ProfilerDockLayout(
            ShowPrimaryToolbar: true,
            ShowCalls: true,
            ShowSettingsButton: true,
            ShowInlineSettings: false,
            ShowQualityDetails: !compact,
            GraphMinimumHeight: graphHeight);
    }
}

public enum PendingStartOutcome { None, Waiting, Started, Rejected }

/// <summary>One pending user intent, replayed exactly once after strict capabilities are available.</summary>
public sealed class PendingCaptureRequest
{
    private ModeConfiguration? configuration;
    public bool HasRequest => configuration is not null;

    public void Request(ModeConfiguration value) =>
        configuration = (value ?? throw new ArgumentNullException(nameof(value))).Normalize();

    public void Cancel() => configuration = null;

    public PendingStartOutcome TryStart(EditorCaptureCoordinator endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (configuration is null) return PendingStartOutcome.None;
        if (endpoint.Snapshot.State is CaptureState.Disconnected or CaptureState.Negotiating ||
            endpoint.Snapshot.RuntimeToken is null || endpoint.Snapshot.SupportedModes == CaptureModes.None)
            return PendingStartOutcome.Waiting;
        if (endpoint.Start(configuration))
        {
            configuration = null;
            return PendingStartOutcome.Started;
        }
        configuration = null;
        return PendingStartOutcome.Rejected;
    }
}
