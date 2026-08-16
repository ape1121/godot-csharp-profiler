#if PROTOCOL_TESTS
using Apeworks.GodotCSharpProfiler.Protocol;

namespace GodotCSharpProfiler.Protocol.Tests;

public sealed class ProtocolTests
{
    private const string Token = "runtime-1";
    private const string Fingerprint = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void ParsesEveryMessageWithExactTypedSchema()
    {
        Assert.IsType<HelloMessage>(Parse(MessageKind.Hello, Token, "client", 1024L));
        Assert.IsType<CapabilitiesMessage>(Parse(MessageKind.Capabilities, Token, 7L,
            (long)(CaptureModes.Sampling | CaptureModes.ManualScopes), 128L, 4096L, 8L));
        Assert.IsType<ConfigureMessage>(Parse(MessageKind.Configure, Token, 7L, Fingerprint,
            (long)(CaptureModes.Sampling | CaptureModes.ManualScopes), 100L, 64L));
        Assert.IsType<StartMessage>(Parse(MessageKind.Start, Token, 7L, Fingerprint));
        Assert.IsType<StateMessage>(Parse(MessageKind.State, Token, 7L, 1L, Fingerprint,
            (long)CaptureState.Capturing, (long)CaptureSource.Sampling, (long)CaptureCompleteness.InProgress,
            (long)PartialReason.None, 0L, 0L, 0L, 0L));
        Assert.IsType<BatchMessage>(Parse(MessageKind.Batch, Token, 7L, 2L, Fingerprint,
            (long)CaptureSource.Sampling, false, true, 2L, 1L, 0L, 0L,
            new WireArray([new WireArray([1L, 100L, 3L]), new WireArray([2L, 200L, 1L])])));
        Assert.IsType<StopMessage>(Parse(MessageKind.Stop, Token, 7L, 3L, Fingerprint));
        Assert.IsType<ErrorMessage>(Parse(MessageKind.Error, Token, 7L, 4L, 12L, "bad request", false));
    }

    [Fact]
    public void RejectsWrongCountNameTypeRangeAndBounds()
    {
        AssertRejected(new WireMap());
        AssertRejected(Map(("kind", (WireValue)"hello"), ("major", 1L)));
        AssertRejected(Map(("kind", "hello"), ("kind", "hello"), ("major", 1L), ("minor", 0L),
            ("runtimeToken", Token), ("role", "client"), ("maxBatchBytes", 1024L)));
        AssertRejected(Map(("kind", "hello"), ("major", 1L), ("minor", 0L), ("runtimeToken", Token),
            ("role", "client"), ("maxBatchBytes", 1024L), ("extra", 1L)));
        AssertRejected(Map(("kind", "hello"), ("major", "1"), ("minor", 0L), ("runtimeToken", Token),
            ("role", "client"), ("maxBatchBytes", 1024L)));
        AssertRejected(Map(("kind", "hello"), ("major", 1L), ("minor", 0L), ("runtime_token", Token),
            ("role", "client"), ("maxBatchBytes", 1024L)));
        AssertRejected(Map(("kind", "hello"), ("major", 1L), ("minor", 0L),
            ("runtimeToken", new string('x', ProtocolLimits.MaxTokenCharacters + 1)), ("role", "client"),
            ("maxBatchBytes", 1024L)));
        AssertRejected(Map(("kind", "configure"), ("major", 1L), ("minor", 0L), ("runtimeToken", Token),
            ("generation", 1L), ("fingerprint", Fingerprint), ("modes", (long)(CaptureModes.Sampling | CaptureModes.AutomaticInstrumentation)),
            ("samplingHertz", 100L), ("maxMethods", 64L)));
        AssertRejected(Map(("kind", "configure"), ("major", 1L), ("minor", 0L), ("runtimeToken", Token),
            ("generation", 1L), ("fingerprint", Fingerprint), ("modes", (long)CaptureModes.ManualScopes),
            ("samplingHertz", 100L), ("maxMethods", 64L)));
        AssertRejected(Map(("kind", "batch"), ("major", 1L), ("minor", 0L), ("runtimeToken", Token),
            ("generation", 1L), ("sequence", 1L), ("fingerprint", Fingerprint), ("source", 0L),
            ("exactCalls", false), ("cpuTime", true), ("observed", 0L), ("dropped", 0L),
            ("overflowed", 0L), ("invalid", 0L), ("methods", DeepArray(ProtocolLimits.MaxDepth + 1))));
        AssertRejected(Map(("kind", "batch"), ("major", 1L), ("minor", 0L), ("runtimeToken", Token),
            ("generation", 1L), ("sequence", 1L), ("fingerprint", Fingerprint), ("source", 0L),
            ("exactCalls", false), ("cpuTime", true), ("observed", 0L), ("dropped", 0L),
            ("overflowed", 0L), ("invalid", 0L), ("methods", Methods(ProtocolLimits.MaxMethodsPerBatch + 1))));
        AssertRejected(Map(("kind", "error"), ("major", 1L), ("minor", 0L), ("runtimeToken", Token),
            ("generation", 1L), ("sequence", 1L), ("code", 1L),
            ("message", new string('x', ProtocolLimits.MaxErrorCharacters + 1)), ("fatal", false)));
    }

    [Fact]
    public void RejectsIncompatibleMajorAndOversizedPayload()
    {
        AssertRejected(Map(("kind", "hello"), ("major", 2L), ("minor", 0L), ("runtimeToken", Token),
            ("role", "client"), ("maxBatchBytes", 1024L)));
        var parser = new CaptureProtocolParser(64);
        Assert.False(parser.TryParse(Map(("kind", "hello"), ("major", 1L), ("minor", 0L),
            ("runtimeToken", Token), ("role", "client"), ("maxBatchBytes", 1024L)), out _, out var failure));
        Assert.Equal(ParseFailure.Oversized, failure);
    }

    [Theory]
    [InlineData(CaptureSource.Sampling, true, true)]
    [InlineData(CaptureSource.AutomaticSpans, true, true)]
    [InlineData(CaptureSource.ManualSpans, true, true)]
    public void RejectsFalseSourceSemantics(CaptureSource source, bool exactCalls, bool cpuTime)
    {
        var map = Map(("kind", "batch"), ("major", 1L), ("minor", 0L), ("runtimeToken", Token),
            ("generation", 1L), ("sequence", 1L), ("fingerprint", Fingerprint), ("source", (long)source),
            ("exactCalls", exactCalls), ("cpuTime", cpuTime), ("observed", 0L), ("dropped", 0L),
            ("overflowed", 0L), ("invalid", 0L), ("methods", Methods(0)));
        AssertRejected(map);
    }

    [Fact]
    public void AcceptsTruthfulSourceSemantics()
    {
        Parse(MessageKind.Batch, Token, 1L, 1L, Fingerprint, (long)CaptureSource.Sampling,
            false, true, 0L, 0L, 0L, 0L, Methods(0));
        Parse(MessageKind.Batch, Token, 1L, 1L, Fingerprint, (long)CaptureSource.AutomaticSpans,
            true, false, 0L, 0L, 0L, 0L, Methods(0));
        Parse(MessageKind.Batch, Token, 1L, 1L, Fingerprint, (long)CaptureSource.ManualSpans,
            true, false, 0L, 0L, 0L, 0L, Methods(0));
    }

    private static ProtocolMessage Parse(MessageKind kind, params WireValue[] values)
    {
        var names = ProtocolSchema.FieldNames(kind);
        Assert.Equal(names.Count - 3, values.Length);
        var fields = new List<KeyValuePair<string, WireValue>>
        {
            new("kind", kind.ToWireName()), new("major", 1L), new("minor", 0L)
        };
        for (var i = 0; i < values.Length; i++) fields.Add(new(names[i + 3], values[i]));
        var parser = new CaptureProtocolParser();
        Assert.True(parser.TryParse(new WireMap(fields), out var message, out var failure), failure.ToString());
        return message!;
    }

    private static void AssertRejected(WireMap map)
    {
        var parser = new CaptureProtocolParser();
        Assert.False(parser.TryParse(map, out _, out _));
    }

    private static WireMap Map(params (string Name, WireValue Value)[] fields) =>
        new(fields.Select(field => new KeyValuePair<string, WireValue>(field.Name, field.Value)));

    private static WireArray Methods(int count) =>
        new(Enumerable.Range(0, count).Select(i => (WireValue)new WireArray([(long)i, 1L, 1L])));

    private static WireArray DeepArray(int depth)
    {
        WireValue value = 1L;
        for (var i = 0; i < depth; i++) value = new WireArray([value]);
        return (WireArray)value;
    }
}
#endif
