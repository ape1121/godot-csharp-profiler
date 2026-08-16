using System.Reflection;

namespace GodotCSharpProfiler.HarmonyInstrumentation;

public enum MethodDisposition
{
    Supported,
    Skipped
}

public enum MethodCategory
{
    Ordinary,
    Overloaded,
    Generic,
    Recursive,
    Throwing,
    AsyncStateMachineMoveNext,
    IteratorStateMachineMoveNext,
    PropertyAccessor,
    Constructor,
    InliningCandidate,
    Abstract,
    NativeOrExtern,
    CompilerGenerated,
    Trivial,
    ProfilerNamespace,
    Unsupported
}

public sealed record MethodInventoryItem(
    MethodBase Method,
    string BoundedName,
    MethodDisposition Disposition,
    MethodCategory Category,
    string Reason);

public sealed record InstrumentationPreview(
    IReadOnlyList<MethodInventoryItem> Items,
    int CandidateCount,
    int SupportedCount,
    int SkippedCount,
    int OmittedByMethodLimit);
