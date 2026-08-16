namespace GodotCSharpProfiler.HarmonyInstrumentation;

public readonly record struct MethodMeasurement(long Calls, long InclusiveTimestampTicks, long Exceptions);

public sealed record PatchSessionMetrics(TimeSpan PatchStartup, int PatchedMethodCount);
