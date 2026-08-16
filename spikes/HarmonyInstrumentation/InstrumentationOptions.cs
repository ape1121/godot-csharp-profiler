namespace GodotCSharpProfiler.HarmonyInstrumentation;

public sealed record InstrumentationOptions
{
    public required string OwnerId { get; init; }

    public required IReadOnlyCollection<Type> SelectedTypes { get; init; }

    public int MaxMethods { get; init; } = 128;

    public int MaxNameLength { get; init; } = 160;

    public int TrivialIlByteThreshold { get; init; } = 2;

    public bool IncludeCompilerGenerated { get; init; }

    public bool IncludeAccessors { get; init; }

    public bool IncludeConstructors { get; init; }

    public bool IncludeTrivial { get; init; }

    public bool IncludeProfilerNamespaces { get; init; }
}
