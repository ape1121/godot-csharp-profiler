#nullable enable
namespace Apeworks.GodotCSharpProfiler.Protocol;

/// <summary>Strict parser for untrusted transport values. It never returns a partially populated DTO.</summary>
public sealed class CaptureProtocolParser
{
    private readonly int _maximumBytes;

    public CaptureProtocolParser(int maximumBytes = ProtocolLimits.MaxPayloadBytes)
    {
        if (maximumBytes < 1 || maximumBytes > ProtocolLimits.MaxPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumBytes = maximumBytes;
    }

    public bool TryParse(WireValue value, out ProtocolMessage? message, out ParseFailure failure)
    {
        message = null;
        failure = ParseFailure.Malformed;
        if (!TryMeasure(value, 0, out var bytes) || bytes > _maximumBytes)
        {
            failure = bytes > _maximumBytes ? ParseFailure.Oversized : ParseFailure.Malformed;
            return false;
        }
        Dictionary<string, WireValue>? fields = null;
        if (value is not WireMap map || !TryFields(map, out fields) ||
            !TryString(fields, "kind", 16, out var kindName) || !TryKind(kindName, out var kind) ||
            !HasExactFields(fields, ProtocolSchema.FieldNames(kind)) ||
            !TryInteger(fields, "major", ProtocolVersion.Major, ProtocolVersion.Major, out var major))
        {
            if (fields is not null && fields.TryGetValue("major", out var majorValue) &&
                majorValue is WireInteger incompatible && incompatible.Value != ProtocolVersion.Major)
                failure = ParseFailure.IncompatibleMajor;
            return false;
        }
        if (!TryInteger(fields, "minor", 0, ProtocolVersion.Minor, out var minor) ||
            !TryString(fields, "runtimeToken", ProtocolLimits.MaxTokenCharacters, out var token) ||
            string.IsNullOrWhiteSpace(token)) return false;

        try
        {
            message = kind switch
            {
                MessageKind.Hello => ParseHello(fields, (int)major, (int)minor, token),
                MessageKind.Capabilities => ParseCapabilities(fields, (int)major, (int)minor, token),
                MessageKind.Configure => ParseConfigure(fields, (int)major, (int)minor, token),
                MessageKind.Start => ParseStart(fields, (int)major, (int)minor, token),
                MessageKind.State => ParseState(fields, (int)major, (int)minor, token),
                MessageKind.Batch => ParseBatch(fields, (int)major, (int)minor, token),
                MessageKind.Stop => ParseStop(fields, (int)major, (int)minor, token),
                MessageKind.Reset => ParseReset(fields, (int)major, (int)minor, token),
                MessageKind.ResetAck => ParseResetAck(fields, (int)major, (int)minor, token),
                MessageKind.Error => ParseError(fields, (int)major, (int)minor, token),
                _ => null
            };
        }
        catch (InvalidWireException exception)
        {
            failure = exception.Semantic ? ParseFailure.InvalidSemantics : ParseFailure.Malformed;
            return false;
        }
        failure = message is null ? ParseFailure.Malformed : ParseFailure.None;
        return message is not null;
    }

    private static HelloMessage ParseHello(Dictionary<string, WireValue> f, int major, int minor, string token) =>
        new(major, minor, token, RequiredString(f, "role", ProtocolLimits.MaxRoleCharacters),
            RequiredInt(f, "maxBatchBytes", 1, ProtocolLimits.MaxBatchBytes));

    private static CapabilitiesMessage ParseCapabilities(Dictionary<string, WireValue> f, int major, int minor, string token)
    {
        var modes = RequiredAvailableModes(f);
        var configurable = RequiredBool(f, "samplingIntervalRuntimeConfigurable");
        var effective = RequiredInterval(f, "effectiveSamplingIntervalNanoseconds", allowUnknown: true);
        var hasSampling = (modes & CaptureModes.Sampling) != 0;
        if ((!hasSampling && (configurable || effective != 0)) || (configurable && !hasSampling))
            throw new InvalidWireException(true);
        return new(major, minor, token, RequiredLong(f, "generation", 0, long.MaxValue), modes,
            configurable, effective, RequiredInt(f, "maxMethods", 1, ProtocolLimits.MaxConfiguredMethods),
            RequiredInt(f, "maxBatchBytes", 1, ProtocolLimits.MaxBatchBytes),
            RequiredInt(f, "maxDepth", 1, ProtocolLimits.MaxDepth));
    }

    private static ConfigureMessage ParseConfigure(Dictionary<string, WireValue> f, int major, int minor, string token)
    {
        var modes = RequiredModes(f);
        var interval = RequiredInterval(f, "requestedSamplingIntervalNanoseconds", allowUnknown: true);
        if ((modes & CaptureModes.Sampling) == 0 && interval != 0) throw new InvalidWireException(true);
        var include = RequiredOptionalString(f, "samplingIncludeAssemblies", ProtocolLimits.MaxConfigurationListCharacters);
        var exclude = RequiredOptionalString(f, "samplingExcludeAssemblies", ProtocolLimits.MaxConfigurationListCharacters);
        var manualPrefix = RequiredOptionalString(f, "manualLabelPrefix", ProtocolLimits.MaxManualLabelPrefixCharacters);
        if (!ValidConfigurationList(include) || !ValidConfigurationList(exclude)) throw new InvalidWireException(true);
        return new(major, minor, token, RequiredLong(f, "generation", 1, long.MaxValue),
            RequiredFingerprint(f), modes, interval,
            RequiredInt(f, "maxMethods", 1, ProtocolLimits.MaxConfiguredMethods), include, exclude, manualPrefix);
    }

    private static StartMessage ParseStart(Dictionary<string, WireValue> f, int major, int minor, string token) =>
        new(major, minor, token, RequiredLong(f, "generation", 1, long.MaxValue), RequiredFingerprint(f));

    private static StopMessage ParseStop(Dictionary<string, WireValue> f, int major, int minor, string token) =>
        new(major, minor, token, RequiredLong(f, "generation", 1, long.MaxValue),
            RequiredLong(f, "sequence", 1, long.MaxValue), RequiredFingerprint(f));

    private static ResetMessage ParseReset(Dictionary<string, WireValue> f, int major, int minor, string token) =>
        new(major, minor, token, RequiredLong(f, "generation", 1, long.MaxValue), RequiredRequestId(f));

    private static ResetAckMessage ParseResetAck(Dictionary<string, WireValue> f, int major, int minor, string token) =>
        new(major, minor, token, RequiredLong(f, "generation", 1, long.MaxValue), RequiredRequestId(f));

    private static ErrorMessage ParseError(Dictionary<string, WireValue> f, int major, int minor, string token) =>
        new(major, minor, token, RequiredLong(f, "generation", 0, long.MaxValue),
            RequiredLong(f, "sequence", 0, long.MaxValue), RequiredInt(f, "code", 1, 65535),
            RequiredString(f, "message", ProtocolLimits.MaxErrorCharacters), RequiredBool(f, "fatal"));

    private static StateMessage ParseState(Dictionary<string, WireValue> f, int major, int minor, string token)
    {
        var state = RequiredEnum<CaptureState>(f, "state");
        var completeness = RequiredEnum<CaptureCompleteness>(f, "completeness");
        var reason = RequiredEnum<PartialReason>(f, "partialReason");
        if ((state == CaptureState.Complete) != (completeness == CaptureCompleteness.Complete) ||
            (state == CaptureState.Partial) != (completeness == CaptureCompleteness.Partial) ||
            (completeness == CaptureCompleteness.Partial) != (reason != PartialReason.None))
            throw new InvalidWireException(true);
        return new(major, minor, token, RequiredLong(f, "generation", 1, long.MaxValue),
            RequiredLong(f, "sequence", 1, long.MaxValue), RequiredFingerprint(f), state,
            RequiredEnum<CaptureSource>(f, "source"), completeness, reason, RequiredQuality(f));
    }

    private static BatchMessage ParseBatch(Dictionary<string, WireValue> f, int major, int minor, string token)
    {
        var source = RequiredEnum<CaptureSource>(f, "source");
        var exactCalls = RequiredBool(f, "exactCalls");
        var cpuTime = RequiredBool(f, "cpuTime");
        if ((source == CaptureSource.Sampling && (exactCalls || cpuTime)) ||
            (source != CaptureSource.Sampling && cpuTime) ||
            (source != CaptureSource.Sampling && !exactCalls))
            throw new InvalidWireException(true);
        if (!f.TryGetValue("methods", out var raw) || raw is not WireArray array ||
            array.Items.Count > ProtocolLimits.MaxMethodsPerBatch) throw new InvalidWireException();
        var methods = new MethodSample[array.Items.Count];
        for (var index = 0; index < methods.Length; index++)
        {
            if (array.Items[index] is not WireArray row || row.Items.Count != 4 ||
                row.Items[0] is not WireInteger method || method.Value < 0 ||
                row.Items[1] is not WireString label || !ValidLabel(label.Value) ||
                row.Items[2] is not WireInteger value || value.Value < 0 ||
                row.Items[3] is not WireInteger calls || calls.Value < 0)
                throw new InvalidWireException();
            methods[index] = new MethodSample(method.Value, label.Value, value.Value, calls.Value);
        }
        return new(major, minor, token, RequiredLong(f, "generation", 1, long.MaxValue),
            RequiredLong(f, "sequence", 1, long.MaxValue), RequiredFingerprint(f), source,
            exactCalls, cpuTime, RequiredQuality(f), methods);
    }

    private static QualityCounters RequiredQuality(Dictionary<string, WireValue> f) => new(
        RequiredLong(f, "observed", 0, long.MaxValue), RequiredLong(f, "dropped", 0, long.MaxValue),
        RequiredLong(f, "overflowed", 0, long.MaxValue), RequiredLong(f, "invalid", 0, long.MaxValue));

    private static CaptureModes RequiredAvailableModes(Dictionary<string, WireValue> f) =>
        (CaptureModes)RequiredLong(f, "modes", 1, 7);

    private static CaptureModes RequiredModes(Dictionary<string, WireValue> f)
    {
        var modes = RequiredAvailableModes(f);
        if ((modes & CaptureModes.Sampling) != 0 && (modes & CaptureModes.AutomaticInstrumentation) != 0)
            throw new InvalidWireException(true);
        return modes;
    }

    private static long RequiredInterval(Dictionary<string, WireValue> f, string name, bool allowUnknown)
    {
        var value = RequiredLong(f, name, 0, ProtocolLimits.MaxSamplingIntervalNanoseconds);
        if (value == 0 && allowUnknown) return 0;
        if (value < ProtocolLimits.MinSamplingIntervalNanoseconds) throw new InvalidWireException(true);
        return value;
    }

    private static T RequiredEnum<T>(Dictionary<string, WireValue> f, string name) where T : struct, Enum
    {
        var value = RequiredLong(f, name, 0, int.MaxValue);
        if (!Enum.IsDefined(typeof(T), (int)value)) throw new InvalidWireException();
        return (T)Enum.ToObject(typeof(T), (int)value);
    }

    private static string RequiredFingerprint(Dictionary<string, WireValue> f)
    {
        var value = RequiredString(f, "fingerprint", ProtocolLimits.FingerprintCharacters);
        if (value.Length != ProtocolLimits.FingerprintCharacters || value.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidWireException();
        return value;
    }

    private static string RequiredRequestId(Dictionary<string, WireValue> f)
    {
        var value = RequiredString(f, "requestId", ProtocolLimits.FingerprintCharacters);
        if (value.Length != ProtocolLimits.FingerprintCharacters || value.Any(c => !Uri.IsHexDigit(c)))
            throw new InvalidWireException();
        return value.ToLowerInvariant();
    }

    private static string RequiredString(Dictionary<string, WireValue> f, string name, int max)
    {
        if (!TryString(f, name, max, out var value) || value.Length == 0 || value.Any(char.IsControl))
            throw new InvalidWireException();
        return value;
    }

    private static string RequiredOptionalString(Dictionary<string, WireValue> f, string name, int max)
    {
        if (!TryString(f, name, max, out var value) || value.Any(char.IsControl))
            throw new InvalidWireException();
        return value;
    }

    private static bool ValidConfigurationList(string value) => value.Length == 0 ||
        value.Split(';').All(part => part.Length > 0 && part == part.Trim());

    private static bool ValidLabel(string? value) => !string.IsNullOrEmpty(value) &&
        value.Length <= ProtocolLimits.MaxMethodLabelCharacters && !value.Any(char.IsControl);

    private static bool RequiredBool(Dictionary<string, WireValue> f, string name)
    {
        if (!f.TryGetValue(name, out var value) || value is not WireBoolean boolean) throw new InvalidWireException();
        return boolean.Value;
    }

    private static int RequiredInt(Dictionary<string, WireValue> f, string name, long min, long max) =>
        checked((int)RequiredLong(f, name, min, max));

    private static long RequiredLong(Dictionary<string, WireValue> f, string name, long min, long max)
    {
        if (!TryInteger(f, name, min, max, out var value)) throw new InvalidWireException();
        return value;
    }

    private static bool TryString(Dictionary<string, WireValue> f, string name, int max, out string value)
    {
        value = string.Empty;
        if (!f.TryGetValue(name, out var raw) || raw is not WireString text || text.Value is null ||
            text.Value.Length > max) return false;
        value = text.Value;
        return true;
    }

    private static bool TryInteger(Dictionary<string, WireValue> f, string name, long min, long max, out long value)
    {
        value = 0;
        if (!f.TryGetValue(name, out var raw) || raw is not WireInteger integer ||
            integer.Value < min || integer.Value > max) return false;
        value = integer.Value;
        return true;
    }

    private static bool TryFields(WireMap map, out Dictionary<string, WireValue> fields)
    {
        fields = new Dictionary<string, WireValue>(StringComparer.Ordinal);
        foreach (var pair in map.Fields)
            if (string.IsNullOrEmpty(pair.Key) || pair.Value is null || !fields.TryAdd(pair.Key, pair.Value)) return false;
        return true;
    }

    private static bool HasExactFields(Dictionary<string, WireValue> fields, IReadOnlyList<string> names) =>
        fields.Count == names.Count && names.All(fields.ContainsKey);

    private static bool TryKind(string name, out MessageKind kind)
    {
        foreach (var candidate in Enum.GetValues<MessageKind>())
            if (candidate.ToWireName() == name) { kind = candidate; return true; }
        kind = default;
        return false;
    }

    private static bool TryMeasure(WireValue value, int depth, out long bytes)
    {
        bytes = 0;
        if (value is null || depth > ProtocolLimits.MaxDepth) return false;
        switch (value)
        {
            case WireString text when text.Value is not null:
                bytes = 8L + text.Value.Length * 4L; return true;
            case WireInteger: bytes = 8; return true;
            case WireBoolean: bytes = 1; return true;
            case WireArray array:
                bytes = 8;
                foreach (var item in array.Items)
                {
                    if (!TryMeasure(item, depth + 1, out var child)) return false;
                    bytes += child;
                    if (bytes > ProtocolLimits.MaxPayloadBytes) return true;
                }
                return true;
            case WireMap map:
                bytes = 8;
                foreach (var item in map.Fields)
                {
                    if (item.Key is null || !TryMeasure(item.Value, depth + 1, out var child)) return false;
                    bytes += item.Key.Length * 4L + 8 + child;
                    if (bytes > ProtocolLimits.MaxPayloadBytes) return true;
                }
                return true;
            default: return false;
        }
    }

    private sealed class InvalidWireException(bool semantic = false) : Exception
    {
        public bool Semantic { get; } = semantic;
    }
}
