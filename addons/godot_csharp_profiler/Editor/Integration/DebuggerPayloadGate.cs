#nullable enable
namespace Apeworks.GodotCSharpProfiler.Editor.Integration;

/// <summary>Cheap hostile-input boundary before debugger payloads reach a protocol adapter.</summary>
public sealed class DebuggerPayloadGate
{
    private const int MaximumFields = 64;
    private const int MaximumMessageCharacters = 128;
    private const int MaximumFieldCharacters = 4096;
    private readonly IProfilerDockView view;
    private readonly int maximumStatusCharacters;

    public DebuggerPayloadGate(IProfilerDockView view, int maximumStatusCharacters = 160)
    {
        this.view = view ?? throw new ArgumentNullException(nameof(view));
        this.maximumStatusCharacters = Math.Clamp(maximumStatusCharacters, 32, 512);
    }

    public bool TryAccept(string? message, IReadOnlyList<object?>? fields, out string safeMessage)
    {
        safeMessage = ProfilerDockController.SafeText(message, MaximumMessageCharacters, "invalid message");
        var reason = Validate(message, fields);
        if (reason is null) return true;
        var status = ProfilerDockController.SafeText("Profiler payload rejected: " + reason,
            maximumStatusCharacters, "Profiler payload rejected");
        // Preserve the rest of the current view model while replacing status only.
        var preserving = new StatusOnlyView(view, status);
        preserving.RenderStatus();
        return false;
    }

    private static string? Validate(string? message, IReadOnlyList<object?>? fields)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > MaximumMessageCharacters ||
            message.Any(char.IsControl)) return "invalid message name";
        if (fields is null || fields.Count > MaximumFields) return "invalid field count";
        foreach (var field in fields)
        {
            if (field is string text && (text.Length > MaximumFieldCharacters || text.Any(char.IsControl)))
                return "invalid text field";
            if (field is not null && field is not string and not bool and not byte and not short and
                not int and not long and not float and not double and not decimal)
                return "unsupported field type";
        }
        return null;
    }

    private sealed class StatusOnlyView
    {
        private readonly IProfilerDockView target;
        private readonly string status;
        public StatusOnlyView(IProfilerDockView target, string status)
        {
            this.target = target;
            this.status = status;
        }
        public void RenderStatus() => target.Render(new ProfilerDockViewState(
            "Unknown target", status, Array.Empty<ToggleViewState>(),
            new ToggleViewState("Include Manual", false, false, ""),
            new CommandViewState(false, false, false, false, false), "", "", "", "", false,
            Array.Empty<ResultGroupViewState>(), CaptureTimeline.Empty, false));
    }
}
