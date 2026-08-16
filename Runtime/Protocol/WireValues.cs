#nullable enable
using System.Collections.ObjectModel;

namespace Apeworks.GodotCSharpProfiler.Protocol;

/// <summary>Backend-neutral values accepted by the protocol parser. Transport adapters must convert to these types.</summary>
public abstract record WireValue
{
    public static implicit operator WireValue(string value) => new WireString(value);
    public static implicit operator WireValue(long value) => new WireInteger(value);
    public static implicit operator WireValue(bool value) => new WireBoolean(value);
}

public sealed record WireString(string Value) : WireValue;
public sealed record WireInteger(long Value) : WireValue;
public sealed record WireBoolean(bool Value) : WireValue;

public sealed record WireArray : WireValue
{
    public WireArray(IEnumerable<WireValue> items) => Items = new ReadOnlyCollection<WireValue>(items.ToArray());
    public IReadOnlyList<WireValue> Items { get; }
}

public sealed record WireMap : WireValue
{
    public WireMap() : this([]) { }
    public WireMap(IEnumerable<KeyValuePair<string, WireValue>> fields) =>
        Fields = new ReadOnlyCollection<KeyValuePair<string, WireValue>>(fields.ToArray());
    public IReadOnlyList<KeyValuePair<string, WireValue>> Fields { get; }
}
