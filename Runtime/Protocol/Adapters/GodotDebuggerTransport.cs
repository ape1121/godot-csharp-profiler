#if !PROTOCOL_TESTS
#nullable enable
using Apeworks.GodotCSharpProfiler.Protocol;
using Godot;

namespace Apeworks.GodotCSharpProfiler.Runtime.Protocol.Adapters;

/// <summary>Godot debugger-channel adapter. Only scalar/array/dictionary Variants cross this boundary.</summary>
internal sealed class GodotDebuggerTransport(string messageName) : IRuntimeCaptureTransport
{
    public void Send(WireMap message) => EngineDebugger.SendMessage(messageName, new Godot.Collections.Array { ToVariant(message) });

    internal static bool TryRead(Godot.Collections.Array data, out object? payload)
    {
        payload = null;
        if (data is null || data.Count != 1) return false;
        return TryRead(data[0], 0, out payload);
    }

    private static bool TryRead(Variant value, int depth, out object? result)
    {
        result = null;
        if (depth > ProtocolLimits.MaxDepth) return false;
        switch (value.VariantType)
        {
            case Variant.Type.String: result = value.AsString(); return true;
            case Variant.Type.Int: result = value.AsInt64(); return true;
            case Variant.Type.Bool: result = value.AsBool(); return true;
            case Variant.Type.Array:
            {
                var source = value.AsGodotArray(); var array = new object?[source.Count];
                for (var index = 0; index < array.Length; index++) if (!TryRead(source[index], depth + 1, out array[index])) return false;
                result = array; return true;
            }
            case Variant.Type.Dictionary:
            {
                var source = value.AsGodotDictionary(); var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var key in source.Keys)
                    if (key.VariantType != Variant.Type.String || !TryRead(source[key], depth + 1, out var child) || !map.TryAdd(key.AsString(), child)) return false;
                result = map; return true;
            }
            default: return false;
        }
    }

    private static Variant ToVariant(WireValue value) => value switch
    {
        WireString text => text.Value, WireInteger integer => integer.Value, WireBoolean boolean => boolean.Value,
        WireArray array => ToArray(array), WireMap map => ToDictionary(map), _ => default
    };
    private static Godot.Collections.Array ToArray(WireArray value)
    {
        var result = new Godot.Collections.Array(); foreach (var item in value.Items) result.Add(ToVariant(item)); return result;
    }
    private static Godot.Collections.Dictionary ToDictionary(WireMap value)
    {
        var result = new Godot.Collections.Dictionary(); foreach (var field in value.Fields) result[field.Key] = ToVariant(field.Value); return result;
    }
}
#endif
