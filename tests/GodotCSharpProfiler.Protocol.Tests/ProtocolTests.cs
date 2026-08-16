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
            (long)(CaptureModes.Sampling | CaptureModes.ManualScopes), false, 1_000_000L,
            128L, 4096L, 8L));
        var configure = Assert.IsType<ConfigureMessage>(Parse(MessageKind.Configure, Token, 7L, Fingerprint,
            (long)(CaptureModes.Sampling | CaptureModes.ManualScopes), 0L, 64L,
            "Game;Core", "System", "Gameplay/"));
        Assert.Equal("Game;Core", configure.SamplingIncludeAssemblies);
        Assert.Equal("System", configure.SamplingExcludeAssemblies);
        Assert.Equal("Gameplay/", configure.ManualLabelPrefix);
        Assert.IsType<StartMessage>(Parse(MessageKind.Start, Token, 7L, Fingerprint));
        Assert.IsType<StateMessage>(Parse(MessageKind.State, Token, 7L, 1L, Fingerprint,
            (long)CaptureState.Capturing, (long)CaptureSource.Sampling, (long)CaptureCompleteness.InProgress,
            (long)PartialReason.None, 0L, 0L, 0L, 0L));
        Assert.IsType<BatchMessage>(Parse(MessageKind.Batch, Token, 7L, 2L, Fingerprint,
            (long)CaptureSource.Sampling, false, false, 2L, 1L, 0L, 0L,
            new WireArray([new WireArray([1L, "Game.Tick", 100L, 3L]),
                new WireArray([2L, "Game.Render", 200L, 1L])])));
        Assert.IsType<StopMessage>(Parse(MessageKind.Stop, Token, 7L, 3L, Fingerprint));
        Assert.IsType<ErrorMessage>(Parse(MessageKind.Error, Token, 7L, 4L, 12L, "bad request", false));
    }

    [Fact]
    public void RejectsWrongCountNameTypeRangeAndBounds()
    {
        AssertRejected(new WireMap());
        AssertRejected(Map(("kind", (WireValue)"hello"), ("major", (long)ProtocolVersion.Major)));
        AssertRejected(Map(("kind", "hello"), ("kind", "hello"), ("major", (long)ProtocolVersion.Major), ("minor", 0L),
            ("runtimeToken", Token), ("role", "client"), ("maxBatchBytes", 1024L)));
        AssertRejected(Map(("kind", "hello"), ("major", (long)ProtocolVersion.Major), ("minor", 0L), ("runtimeToken", Token),
            ("role", "client"), ("maxBatchBytes", 1024L), ("extra", 1L)));
        AssertRejected(Map(("kind", "hello"), ("major", "1"), ("minor", 0L), ("runtimeToken", Token),
            ("role", "client"), ("maxBatchBytes", 1024L)));
        AssertRejected(Map(("kind", "hello"), ("major", (long)ProtocolVersion.Major), ("minor", 0L), ("runtime_token", Token),
            ("role", "client"), ("maxBatchBytes", 1024L)));
        AssertRejected(Map(("kind", "hello"), ("major", (long)ProtocolVersion.Major), ("minor", 0L),
            ("runtimeToken", new string('x', ProtocolLimits.MaxTokenCharacters + 1)), ("role", "client"),
            ("maxBatchBytes", 1024L)));
        AssertRejected(Map(("kind", "configure"), ("major", (long)ProtocolVersion.Major), ("minor", 0L), ("runtimeToken", Token),
            ("generation", 1L), ("fingerprint", Fingerprint), ("modes", (long)(CaptureModes.Sampling | CaptureModes.AutomaticInstrumentation)),
            ("requestedSamplingIntervalNanoseconds", 1_000_000L), ("maxMethods", 64L)));
        AssertRejected(Map(("kind", "configure"), ("major", (long)ProtocolVersion.Major), ("minor", 0L), ("runtimeToken", Token),
            ("generation", 1L), ("fingerprint", Fingerprint), ("modes", (long)CaptureModes.ManualScopes),
            ("samplingHertz", 100L), ("maxMethods", 64L)));
        AssertRejected(Map(("kind", "batch"), ("major", (long)ProtocolVersion.Major), ("minor", 0L), ("runtimeToken", Token),
            ("generation", 1L), ("sequence", 1L), ("fingerprint", Fingerprint), ("source", 0L),
            ("exactCalls", false), ("cpuTime", true), ("observed", 0L), ("dropped", 0L),
            ("overflowed", 0L), ("invalid", 0L), ("methods", DeepArray(ProtocolLimits.MaxDepth + 1))));
        AssertRejected(Map(("kind", "batch"), ("major", (long)ProtocolVersion.Major), ("minor", 0L), ("runtimeToken", Token),
            ("generation", 1L), ("sequence", 1L), ("fingerprint", Fingerprint), ("source", 0L),
            ("exactCalls", false), ("cpuTime", true), ("observed", 0L), ("dropped", 0L),
            ("overflowed", 0L), ("invalid", 0L), ("methods", Methods(ProtocolLimits.MaxMethodsPerBatch + 1))));
        AssertRejected(Map(("kind", "error"), ("major", (long)ProtocolVersion.Major), ("minor", 0L), ("runtimeToken", Token),
            ("generation", 1L), ("sequence", 1L), ("code", 1L),
            ("message", new string('x', ProtocolLimits.MaxErrorCharacters + 1)), ("fatal", false)));
        AssertRejected(ConfigureMap(CaptureModes.Sampling, 0,
            include: new string('x', ProtocolLimits.MaxConfigurationListCharacters + 1)));
        AssertRejected(BatchMap(new WireArray([new WireArray([1L,
            new string('x', ProtocolLimits.MaxMethodLabelCharacters + 1), 1L, 0L])])));
        AssertRejected(BatchMap(new WireArray([new WireArray([1L, "", 1L, 0L])])));
    }

    [Fact]
    public void RejectsIncompatibleMajorAndOversizedPayload()
    {
        AssertRejected(Map(("kind", "hello"), ("major", (long)ProtocolVersion.Major + 1), ("minor", 0L), ("runtimeToken", Token),
            ("role", "client"), ("maxBatchBytes", 1024L)));
        var parser = new CaptureProtocolParser(64);
        Assert.False(parser.TryParse(Map(("kind", "hello"), ("major", (long)ProtocolVersion.Major), ("minor", 0L),
            ("runtimeToken", Token), ("role", "client"), ("maxBatchBytes", 1024L)), out _, out var failure));
        Assert.Equal(ParseFailure.Oversized, failure);
    }

    [Theory]
    [InlineData(CaptureSource.Sampling, false, true)]
    [InlineData(CaptureSource.AutomaticSpans, true, true)]
    [InlineData(CaptureSource.ManualSpans, true, true)]
    public void RejectsFalseSourceSemantics(CaptureSource source, bool exactCalls, bool cpuTime)
    {
        var map = Map(("kind", "batch"), ("major", (long)ProtocolVersion.Major), ("minor", 0L), ("runtimeToken", Token),
            ("generation", 1L), ("sequence", 1L), ("fingerprint", Fingerprint), ("source", (long)source),
            ("exactCalls", exactCalls), ("cpuTime", cpuTime), ("observed", 0L), ("dropped", 0L),
            ("overflowed", 0L), ("invalid", 0L), ("methods", Methods(0)));
        AssertRejected(map);
    }

    [Fact]
    public void AcceptsTruthfulSourceSemantics()
    {
        Parse(MessageKind.Batch, Token, 1L, 1L, Fingerprint, (long)CaptureSource.Sampling,
            false, false, 0L, 0L, 0L, 0L, Methods(0));
        Parse(MessageKind.Batch, Token, 1L, 1L, Fingerprint, (long)CaptureSource.AutomaticSpans,
            true, false, 0L, 0L, 0L, 0L, Methods(0));
        Parse(MessageKind.Batch, Token, 1L, 1L, Fingerprint, (long)CaptureSource.ManualSpans,
            true, false, 0L, 0L, 0L, 0L, Methods(0));
    }

    [Fact]
    public void ParsesNanosecondIntervalSentinelsAndStrictBounds()
    {
        var fixedUnknown = Assert.IsType<CapabilitiesMessage>(Parse(MessageKind.Capabilities, Token, 0L,
            (long)CaptureModes.Sampling, false, 0L, 64L, 4096L, 8L));
        Assert.Equal(0, fixedUnknown.EffectiveSamplingIntervalNanoseconds);

        var configurable = Assert.IsType<CapabilitiesMessage>(Parse(MessageKind.Capabilities, Token, 0L,
            (long)(CaptureModes.Sampling | CaptureModes.AutomaticInstrumentation | CaptureModes.ManualScopes),
            true, ProtocolLimits.MinSamplingIntervalNanoseconds, 64L, 4096L, 8L));
        Assert.True(configurable.SamplingIntervalRuntimeConfigurable);

        var noRequest = Assert.IsType<ConfigureMessage>(Parse(MessageKind.Configure, Token, 1L, Fingerprint,
            (long)CaptureModes.ManualScopes, 0L, 64L, "", "", ""));
        Assert.Equal(0, noRequest.RequestedSamplingIntervalNanoseconds);
        Parse(MessageKind.Configure, Token, 1L, Fingerprint, (long)CaptureModes.Sampling,
            ProtocolLimits.MinSamplingIntervalNanoseconds, 64L, "Game", "System", "");
        Parse(MessageKind.Configure, Token, 1L, Fingerprint, (long)CaptureModes.Sampling,
            ProtocolLimits.MaxSamplingIntervalNanoseconds, 64L, "Game", "System", "");

        AssertRejected(ConfigureMap(CaptureModes.Sampling, -1));
        AssertRejected(ConfigureMap(CaptureModes.Sampling, ProtocolLimits.MinSamplingIntervalNanoseconds - 1));
        AssertRejected(ConfigureMap(CaptureModes.Sampling, (long)ProtocolLimits.MaxSamplingIntervalNanoseconds + 1));
        AssertRejected(ConfigureMap(CaptureModes.ManualScopes, ProtocolLimits.MinSamplingIntervalNanoseconds));
    }

    [Fact]
    public void CapabilityIntervalFieldsRequireSamplingAndUseExactSchema()
    {
        Parse(MessageKind.Capabilities, Token, 0L, (long)CaptureModes.ManualScopes,
            false, 0L, 64L, 4096L, 8L);
        AssertRejected(CapabilitiesMap(CaptureModes.ManualScopes, true, 0));
        AssertRejected(CapabilitiesMap(CaptureModes.ManualScopes, false,
            ProtocolLimits.MinSamplingIntervalNanoseconds));
        AssertRejected(CapabilitiesMap(CaptureModes.Sampling, false,
            ProtocolLimits.MinSamplingIntervalNanoseconds - 1));
    }
    private static ProtocolMessage Parse(MessageKind kind, params WireValue[] values)
    {
        var names = ProtocolSchema.FieldNames(kind);
        Assert.Equal(names.Count - 3, values.Length);
        var fields = new List<KeyValuePair<string, WireValue>>
        {
            new("kind", kind.ToWireName()), new("major", (long)ProtocolVersion.Major), new("minor", 0L)
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

    private static WireMap ConfigureMap(CaptureModes modes, long interval) =>
        Map(("kind", "configure"), ("major", (long)ProtocolVersion.Major), ("minor", 0L), ("runtimeToken", Token),
            ("generation", 1L), ("fingerprint", Fingerprint), ("modes", (long)modes),
            ("requestedSamplingIntervalNanoseconds", interval), ("maxMethods", 64L),
            ("samplingIncludeAssemblies", ""), ("samplingExcludeAssemblies", ""),
            ("manualLabelPrefix", ""));

    private static WireMap ConfigureMap(CaptureModes modes, long interval, string include) =>
        Map(("kind", "configure"), ("major", (long)ProtocolVersion.Major), ("minor", 0L), ("runtimeToken", Token),
            ("generation", 1L), ("fingerprint", Fingerprint), ("modes", (long)modes),
            ("requestedSamplingIntervalNanoseconds", interval), ("maxMethods", 64L),
            ("samplingIncludeAssemblies", include), ("samplingExcludeAssemblies", ""),
            ("manualLabelPrefix", ""));

    private static WireMap BatchMap(WireArray methods) =>
        Map(("kind", "batch"), ("major", (long)ProtocolVersion.Major), ("minor", 0L), ("runtimeToken", Token),
            ("generation", 1L), ("sequence", 1L), ("fingerprint", Fingerprint),
            ("source", (long)CaptureSource.Sampling), ("exactCalls", false), ("cpuTime", false),
            ("observed", 0L), ("dropped", 0L), ("overflowed", 0L), ("invalid", 0L),
            ("methods", methods));

    private static WireMap CapabilitiesMap(CaptureModes modes, bool configurable, long effectiveInterval) =>
        Map(("kind", "capabilities"), ("major", (long)ProtocolVersion.Major), ("minor", 0L), ("runtimeToken", Token),
            ("generation", 0L), ("modes", (long)modes),
            ("samplingIntervalRuntimeConfigurable", configurable),
            ("effectiveSamplingIntervalNanoseconds", effectiveInterval),
            ("maxMethods", 64L), ("maxBatchBytes", 4096L), ("maxDepth", 8L));
    private static WireArray Methods(int count) =>
        new(Enumerable.Range(0, count).Select(i => (WireValue)new WireArray([(long)i, $"Method {i}", 1L, 1L])));

    private static WireArray DeepArray(int depth)
    {
        WireValue value = 1L;
        for (var i = 0; i < depth; i++) value = new WireArray([value]);
        return (WireArray)value;
    }
}
#endif
