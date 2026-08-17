#nullable enable
namespace Apeworks.GodotCSharpProfiler.Editor.Integration;

/// <summary>Exact ownership policy for the runtime bridge autoload.</summary>
public static class ProfilerAutoloadPolicy
{
    public const string Name = "GodotCSharpProfilerBridge";
    public const string ScriptPath = "res://addons/godot_csharp_profiler/Runtime/CsProfilerBridge.cs";
    public const string Setting = "autoload/" + Name;

    public static bool IsOwnedValue(object? value)
    {
        if (value is not string text)
            return false;
        return string.Equals(text.TrimStart('*'), ScriptPath, StringComparison.Ordinal);
    }
}
