#nullable enable
using Apeworks.GodotCSharpProfiler.Editor.Modes;

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
        return new ProfilerDockLayout(
            ShowPrimaryToolbar: true,
            ShowCalls: true,
            ShowSettingsButton: true,
            ShowInlineSettings: false,
            ShowQualityDetails: !compact,
            GraphMinimumHeight: compact ? 48 : 72);
    }
}

/// <summary>One pending user intent, replayed exactly once after strict capabilities are available.</summary>
public sealed class PendingCaptureRequest
{
    private ModeConfiguration? configuration;
    public bool HasRequest => configuration is not null;

    public void Request(ModeConfiguration value) =>
        configuration = (value ?? throw new ArgumentNullException(nameof(value))).Normalize();

    public void Cancel() => configuration = null;

    public bool TryStart(EditorCaptureCoordinator endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (configuration is null || !endpoint.Start(configuration)) return false;
        configuration = null;
        return true;
    }
}
