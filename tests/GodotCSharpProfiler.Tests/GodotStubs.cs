namespace Godot
{
    internal static class Variant
    {
        internal enum Type
        {
            Nil,
            Bool,
            Int,
            String
        }
    }
}

namespace Godot.Collections
{
    internal sealed class Array
    {
        private readonly object[] _values;

        internal Array(params object[] values) => _values = values ?? System.Array.Empty<object>();

        internal int Count => _values.Length;

        internal VariantValue this[int index] => new(_values[index]);
    }

    internal readonly struct VariantValue
    {
        private readonly object _value;

        internal VariantValue(object value) => _value = value;

        internal Godot.Variant.Type VariantType => _value switch
        {
            string => Godot.Variant.Type.String,
            long => Godot.Variant.Type.Int,
            bool => Godot.Variant.Type.Bool,
            _ => Godot.Variant.Type.Nil
        };

        internal string AsString() => _value is string value ? value : throw new InvalidCastException();
        internal long AsInt64() => _value is long value ? value : throw new InvalidCastException();
        internal bool AsBool() => _value is bool value ? value : throw new InvalidCastException();
    }
}
