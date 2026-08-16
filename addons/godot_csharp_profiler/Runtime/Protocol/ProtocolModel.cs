#nullable enable
namespace Apeworks.GodotCSharpProfiler.Protocol;

public static class ProtocolVersion
{
    public const int Major = 1;
    public const int Minor = 0;
}

public static class ProtocolLimits
{
    public const int MaxPayloadBytes = 1_048_576;
    public const int MaxBatchBytes = 262_144;
    public const int MaxTokenCharacters = 96;
    public const int MaxRoleCharacters = 32;
    public const int FingerprintCharacters = 32;
    public const int MaxErrorCharacters = 512;
    public const int MaxMethodsPerBatch = 4096;
    public const int MaxDepth = 8;
    public const int MaxSamplingHertz = 10_000;
    public const int MaxConfiguredMethods = 1_000_000;
}

[Flags]
public enum CaptureModes
{
    None = 0,
    Sampling = 1,
    AutomaticInstrumentation = 2,
    ManualScopes = 4
}

public enum CaptureSource { Sampling, AutomaticSpans, ManualSpans }
public enum CaptureCompleteness { InProgress, Complete, Partial }
public enum PartialReason { None, RequestedStop, BufferOverflow, TransportLoss, RuntimeError, Disconnected }
public enum CaptureState { Disconnected, Negotiating, Ready, Starting, Capturing, Stopping, Complete, Partial, Busy, Error }
public enum MessageKind { Hello, Capabilities, Configure, Start, State, Batch, Stop, Error }
public enum ParseFailure { None, Malformed, Oversized, IncompatibleMajor, InvalidSemantics }

public readonly record struct QualityCounters(long Observed, long Dropped, long Overflowed, long Invalid)
{
    public static QualityCounters Zero => new(0, 0, 0, 0);
    public QualityCounters Add(QualityCounters other) => checked(new(
        Observed + other.Observed, Dropped + other.Dropped,
        Overflowed + other.Overflowed, Invalid + other.Invalid));
}

public readonly record struct MethodSample(long MethodId, long Value, long Calls);

public abstract record ProtocolMessage(int Major, int Minor, string RuntimeToken);
public sealed record HelloMessage(int Major, int Minor, string RuntimeToken, string Role, int MaxBatchBytes)
    : ProtocolMessage(Major, Minor, RuntimeToken);
public sealed record CapabilitiesMessage(int Major, int Minor, string RuntimeToken, long Generation,
    CaptureModes Modes, int MaxMethods, int MaxBatchBytes, int MaxDepth)
    : ProtocolMessage(Major, Minor, RuntimeToken);
public sealed record ConfigureMessage(int Major, int Minor, string RuntimeToken, long Generation,
    string Fingerprint, CaptureModes Modes, int SamplingHertz, int MaxMethods)
    : ProtocolMessage(Major, Minor, RuntimeToken);
public sealed record StartMessage(int Major, int Minor, string RuntimeToken, long Generation, string Fingerprint)
    : ProtocolMessage(Major, Minor, RuntimeToken);
public sealed record StateMessage(int Major, int Minor, string RuntimeToken, long Generation, long Sequence,
    string Fingerprint, CaptureState State, CaptureSource Source, CaptureCompleteness Completeness,
    PartialReason PartialReason, QualityCounters Quality)
    : ProtocolMessage(Major, Minor, RuntimeToken);
public sealed record BatchMessage(int Major, int Minor, string RuntimeToken, long Generation, long Sequence,
    string Fingerprint, CaptureSource Source, bool ExactCalls, bool CpuTime, QualityCounters Quality,
    IReadOnlyList<MethodSample> Methods)
    : ProtocolMessage(Major, Minor, RuntimeToken);
public sealed record StopMessage(int Major, int Minor, string RuntimeToken, long Generation, long Sequence,
    string Fingerprint) : ProtocolMessage(Major, Minor, RuntimeToken);
public sealed record ErrorMessage(int Major, int Minor, string RuntimeToken, long Generation, long Sequence,
    int Code, string Message, bool Fatal) : ProtocolMessage(Major, Minor, RuntimeToken);

public static class MessageKindExtensions
{
    public static string ToWireName(this MessageKind kind) => kind switch
    {
        MessageKind.Hello => "hello", MessageKind.Capabilities => "capabilities",
        MessageKind.Configure => "configure", MessageKind.Start => "start", MessageKind.State => "state",
        MessageKind.Batch => "batch", MessageKind.Stop => "stop", MessageKind.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}

public static class ProtocolSchema
{
    private static readonly IReadOnlyDictionary<MessageKind, IReadOnlyList<string>> Names =
        new Dictionary<MessageKind, IReadOnlyList<string>>
        {
            [MessageKind.Hello] = ["kind", "major", "minor", "runtimeToken", "role", "maxBatchBytes"],
            [MessageKind.Capabilities] = ["kind", "major", "minor", "runtimeToken", "generation", "modes", "maxMethods", "maxBatchBytes", "maxDepth"],
            [MessageKind.Configure] = ["kind", "major", "minor", "runtimeToken", "generation", "fingerprint", "modes", "samplingHertz", "maxMethods"],
            [MessageKind.Start] = ["kind", "major", "minor", "runtimeToken", "generation", "fingerprint"],
            [MessageKind.State] = ["kind", "major", "minor", "runtimeToken", "generation", "sequence", "fingerprint", "state", "source", "completeness", "partialReason", "observed", "dropped", "overflowed", "invalid"],
            [MessageKind.Batch] = ["kind", "major", "minor", "runtimeToken", "generation", "sequence", "fingerprint", "source", "exactCalls", "cpuTime", "observed", "dropped", "overflowed", "invalid", "methods"],
            [MessageKind.Stop] = ["kind", "major", "minor", "runtimeToken", "generation", "sequence", "fingerprint"],
            [MessageKind.Error] = ["kind", "major", "minor", "runtimeToken", "generation", "sequence", "code", "message", "fatal"]
        };

    public static IReadOnlyList<string> FieldNames(MessageKind kind) => Names[kind];
}
