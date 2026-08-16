#if !PROTOCOL_TESTS
#nullable enable
using Apeworks.GodotCSharpProfiler.Protocol;
using Godot;

namespace Apeworks.GodotCSharpProfiler.Runtime.Protocol.Adapters;

/// <summary>Godot debugger-channel adapter using one bounded scalar string Variant.</summary>
internal sealed class GodotDebuggerTransport(string messageName) : IRuntimeCaptureTransport
{
    public void Send(WireMap message) =>
        EngineDebugger.SendMessage(messageName, new Godot.Collections.Array { WireJsonEnvelope.Encode(message) });

    internal static bool TryRead(Godot.Collections.Array data, out object? payload)
    {
        payload = null;
        if (data is null || data.Count != 1 || data[0].VariantType != Variant.Type.String) return false;
        if (!WireJsonEnvelope.TryDecode(data[0].AsString(), out var map) || map is null) return false;
        payload = map;
        return true;
    }

    internal static Variant ToGodotVariant(WireValue value) => WireJsonEnvelope.Encode(value);
}
#endif
