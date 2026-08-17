using Godot;
using System.Runtime.CompilerServices;

namespace Apeworks.GodotCSharpProfiler;

/// <summary>Attaches the debugger bridge in game processes without modifying project.godot.</summary>
internal static class CsProfilerRuntimeBootstrap
{
    private const string BridgeNodeName = "GodotCSharpProfilerBridge";

#pragma warning disable CA2255 // Intentional application-level bootstrap for a source addon.
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (Engine.IsEditorHint()) return;
        Callable.From(Attach).CallDeferred();
    }

    private static void Attach()
    {
        if (Engine.IsEditorHint() || !EngineDebugger.IsActive() ||
            Engine.GetMainLoop() is not SceneTree tree || tree.Root is null ||
            tree.Root.GetNodeOrNull(BridgeNodeName) != null) return;
        tree.Root.AddChild(new CsProfilerBridge { Name = BridgeNodeName });
    }
}
