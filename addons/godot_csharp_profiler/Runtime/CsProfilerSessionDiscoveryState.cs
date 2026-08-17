using Godot;
using System;

namespace Apeworks.GodotCSharpProfiler;

// Pure lifecycle state shared by the editor panel and its headless regression probe. Godot debugger
// sessions can already be active when a managed editor plugin is reloaded, so discovery is driven by
// current session state rather than relying on one lossy Started signal or one game-side ready packet.
internal sealed class CsProfilerSessionDiscoveryState
{
    internal const double RetryIntervalSeconds = 1.0;

    private double _lastDiscoveryAtSeconds = double.NegativeInfinity;

    internal bool SessionActive { get; private set; }
    internal bool BridgeReady { get; private set; }
    internal CsProfilerRuntimeIdentity Identity { get; private set; } =
        CsProfilerRuntimeIdentity.Unknown;

    internal bool OnSessionStarted()
    {
        if (SessionActive)
            return false;
        SessionActive = true;
        BridgeReady = false;
        Identity = CsProfilerRuntimeIdentity.Unknown;
        _lastDiscoveryAtSeconds = double.NegativeInfinity;
        return true;
    }

    internal bool OnSessionStopped()
    {
        if (!SessionActive)
            return false;
        SessionActive = false;
        BridgeReady = false;
        Identity = CsProfilerRuntimeIdentity.Unknown;
        _lastDiscoveryAtSeconds = double.NegativeInfinity;
        return true;
    }

    internal bool TryScheduleDiscovery(double nowSeconds)
    {
        if (!SessionActive || BridgeReady || !double.IsFinite(nowSeconds) ||
            nowSeconds - _lastDiscoveryAtSeconds < RetryIntervalSeconds)
        {
            return false;
        }

        _lastDiscoveryAtSeconds = nowSeconds;
        return true;
    }

    // Returns true when the ready packet describes a newly attached runtime. Duplicate ready
    // replies from bounded discovery retries are idempotent and preserve captured frame history.
    internal bool AcceptReady(CsProfilerRuntimeIdentity identity)
    {
        identity ??= CsProfilerRuntimeIdentity.Unknown;
        var runtimeChanged = !BridgeReady ||
                             !string.Equals(
                                 Identity.RuntimeToken,
                                 identity.RuntimeToken,
                                 StringComparison.Ordinal);
        SessionActive = true;
        BridgeReady = true;
        Identity = identity;
        return runtimeChanged;
    }
}

internal sealed class CsProfilerRuntimeIdentity
{
    internal const int MaximumTokenLength = 96;
    internal const int MaximumLabelLength = 64;

    internal static readonly CsProfilerRuntimeIdentity Unknown = new(
        "unknown", 0, false, "game", "Game", false);

    internal CsProfilerRuntimeIdentity(
        string runtimeToken,
        long processId,
        bool editorAttached,
        string role,
        string displayName,
        bool capturing)
    {
        RuntimeToken = Normalize(runtimeToken, MaximumTokenLength, "unknown");
        ProcessId = Math.Max(0, processId);
        EditorAttached = editorAttached;
        Role = Normalize(role, MaximumLabelLength, "game");
        DisplayName = Normalize(displayName, MaximumLabelLength, "Game");
        Capturing = capturing;
    }

    internal string RuntimeToken { get; }
    internal long ProcessId { get; }
    internal bool EditorAttached { get; }
    internal string Role { get; }
    internal string DisplayName { get; }
    internal bool Capturing { get; }

        internal static CsProfilerRuntimeIdentity FromWire(Godot.Collections.Array data) =>
        TryFromWire(data, out var identity) ? identity : Unknown;

        internal static bool TryFromWire(
        Godot.Collections.Array data,
        out CsProfilerRuntimeIdentity identity)
    {
        identity = Unknown;
        if (data == null || data.Count != 6)
            return false;
        try
        {
            if (data[0].VariantType != Variant.Type.String ||
                data[1].VariantType != Variant.Type.Int ||
                data[2].VariantType != Variant.Type.Bool ||
                data[3].VariantType != Variant.Type.String ||
                data[4].VariantType != Variant.Type.String ||
                data[5].VariantType != Variant.Type.Bool)
            {
                return false;
            }

            identity = new CsProfilerRuntimeIdentity(
                data[0].AsString(),
                data[1].AsInt64(),
                data[2].AsBool(),
                data[3].AsString(),
                data[4].AsString(),
                data[5].AsBool());
            return true;
        }
        catch (Exception error) when (error is InvalidCastException or ArgumentException or OverflowException)
        {
            identity = Unknown;
            return false;
        }
    }

    internal static string Normalize(string value, int maximumLength, string fallback)
    {
        var source = (value ?? "").Trim();
        if (source.Length == 0)
            return fallback;
        Span<char> buffer = stackalloc char[Math.Min(source.Length, maximumLength)];
        var count = 0;
        foreach (var character in source)
        {
            if (count >= buffer.Length)
                break;
            if (!char.IsControl(character))
                buffer[count++] = character;
        }
        return count == 0 ? fallback : new string(buffer[..count]);
    }
}
