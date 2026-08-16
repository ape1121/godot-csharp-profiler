#nullable enable
using Apeworks.GodotCSharpProfiler.Protocol;

namespace Apeworks.GodotCSharpProfiler.Runtime.Protocol.Adapters;

/// <summary>Strict conversion and serialization at the Godot/untrusted transport boundary.</summary>
public static class StrictWireAdapter
{
    public static bool TryConvert(object? value, out WireValue? wire)
    {
        wire = null;
        return TryConvert(value, 0, ref wire);
    }

    public static WireMap Serialize(ProtocolMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var fields = new List<KeyValuePair<string, WireValue>>
        {
            Field("kind", Kind(message).ToWireName()), Field("major", message.Major),
            Field("minor", message.Minor), Field("runtimeToken", message.RuntimeToken)
        };
        switch (message)
        {
            case HelloMessage value:
                fields.Add(Field("role", value.Role)); fields.Add(Field("maxBatchBytes", value.MaxBatchBytes)); break;
            case CapabilitiesMessage value:
                fields.Add(Field("generation", value.Generation)); fields.Add(Field("modes", (long)value.Modes));
                fields.Add(Field("samplingIntervalRuntimeConfigurable", value.SamplingIntervalRuntimeConfigurable));
                fields.Add(Field("effectiveSamplingIntervalNanoseconds", value.EffectiveSamplingIntervalNanoseconds));
                fields.Add(Field("maxMethods", value.MaxMethods)); fields.Add(Field("maxBatchBytes", value.MaxBatchBytes));
                fields.Add(Field("maxDepth", value.MaxDepth)); break;
            case ConfigureMessage value:
                fields.Add(Field("generation", value.Generation)); fields.Add(Field("fingerprint", value.Fingerprint));
                fields.Add(Field("modes", (long)value.Modes)); fields.Add(Field("requestedSamplingIntervalNanoseconds", value.RequestedSamplingIntervalNanoseconds));
                fields.Add(Field("maxMethods", value.MaxMethods));
                fields.Add(Field("samplingIncludeAssemblies", value.SamplingIncludeAssemblies));
                fields.Add(Field("samplingExcludeAssemblies", value.SamplingExcludeAssemblies));
                fields.Add(Field("manualLabelPrefix", value.ManualLabelPrefix)); break;
            case StartMessage value:
                fields.Add(Field("generation", value.Generation)); fields.Add(Field("fingerprint", value.Fingerprint)); break;
            case StopMessage value:
                fields.Add(Field("generation", value.Generation)); fields.Add(Field("sequence", value.Sequence)); fields.Add(Field("fingerprint", value.Fingerprint)); break;
            case ErrorMessage value:
                fields.Add(Field("generation", value.Generation)); fields.Add(Field("sequence", value.Sequence));
                fields.Add(Field("code", value.Code)); fields.Add(Field("message", value.Message)); fields.Add(Field("fatal", value.Fatal)); break;
            case StateMessage value:
                fields.Add(Field("generation", value.Generation)); fields.Add(Field("sequence", value.Sequence)); fields.Add(Field("fingerprint", value.Fingerprint));
                fields.Add(Field("state", (long)value.State)); fields.Add(Field("source", (long)value.Source));
                fields.Add(Field("completeness", (long)value.Completeness)); fields.Add(Field("partialReason", (long)value.PartialReason));
                AddQuality(fields, value.Quality); break;
            case BatchMessage value:
                fields.Add(Field("generation", value.Generation)); fields.Add(Field("sequence", value.Sequence)); fields.Add(Field("fingerprint", value.Fingerprint));
                fields.Add(Field("source", (long)value.Source)); fields.Add(Field("exactCalls", value.ExactCalls)); fields.Add(Field("cpuTime", value.CpuTime));
                AddQuality(fields, value.Quality);
                fields.Add(Field("methods", new WireArray(value.Methods.Select(method => (WireValue)new WireArray([method.MethodId, method.Label, method.Value, method.Calls])))));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(message));
        }
        return new WireMap(fields);
    }

    public static int MeasureBytes(WireValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return checked((int)Measure(value, 0));
    }

    private static bool TryConvert(object? value, int depth, ref WireValue? wire)
    {
        if (value is null || depth > ProtocolLimits.MaxDepth) return false;
        switch (value)
        {
            case string text: wire = new WireString(text); return true;
            case bool boolean: wire = new WireBoolean(boolean); return true;
            case byte number: wire = new WireInteger(number); return true;
            case sbyte number: wire = new WireInteger(number); return true;
            case short number: wire = new WireInteger(number); return true;
            case ushort number: wire = new WireInteger(number); return true;
            case int number: wire = new WireInteger(number); return true;
            case uint number: wire = new WireInteger(number); return true;
            case long number: wire = new WireInteger(number); return true;
            case ulong number when number <= long.MaxValue: wire = new WireInteger((long)number); return true;
            case WireValue existing: wire = existing; return true;
            case System.Collections.IDictionary dictionary:
            {
                var fields = new List<KeyValuePair<string, WireValue>>();
                foreach (System.Collections.DictionaryEntry pair in dictionary)
                {
                    if (pair.Key is not string name || string.IsNullOrEmpty(name)) return false;
                    WireValue? child = null;
                    if (!TryConvert(pair.Value, depth + 1, ref child)) return false;
                    fields.Add(new(name, child!));
                }
                wire = new WireMap(fields); return true;
            }
            case System.Collections.IEnumerable enumerable:
            {
                var items = new List<WireValue>();
                foreach (var item in enumerable)
                {
                    if (items.Count > ProtocolLimits.MaxMethodsPerBatch * 3) return false;
                    WireValue? child = null;
                    if (!TryConvert(item, depth + 1, ref child)) return false;
                    items.Add(child!);
                }
                wire = new WireArray(items); return true;
            }
            default: return false;
        }
    }

    private static long Measure(WireValue value, int depth)
    {
        if (depth > ProtocolLimits.MaxDepth) throw new ArgumentException("Wire value is too deep.", nameof(value));
        return value switch
        {
            WireString text => checked(8L + text.Value.Length * 4L),
            WireInteger => 8, WireBoolean => 1,
            WireArray array => checked(8L + array.Items.Sum(item => Measure(item, depth + 1))),
            WireMap map => checked(8L + map.Fields.Sum(field => field.Key.Length * 4L + 8L + Measure(field.Value, depth + 1))),
            _ => throw new ArgumentException("Unsupported wire value.", nameof(value))
        };
    }

    private static void AddQuality(List<KeyValuePair<string, WireValue>> fields, QualityCounters quality)
    {
        fields.Add(Field("observed", quality.Observed)); fields.Add(Field("dropped", quality.Dropped));
        fields.Add(Field("overflowed", quality.Overflowed)); fields.Add(Field("invalid", quality.Invalid));
    }

    private static MessageKind Kind(ProtocolMessage message) => message switch
    {
        HelloMessage => MessageKind.Hello, CapabilitiesMessage => MessageKind.Capabilities,
        ConfigureMessage => MessageKind.Configure, StartMessage => MessageKind.Start,
        StateMessage => MessageKind.State, BatchMessage => MessageKind.Batch,
        StopMessage => MessageKind.Stop, ErrorMessage => MessageKind.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(message))
    };

    private static KeyValuePair<string, WireValue> Field(string name, WireValue value) => new(name, value);
}
