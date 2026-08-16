# Demo

This is a minimal Godot **4.7 .NET** scene. For repository development it is run by the root project, whose `project.godot` already points to `demo/Main.tscn`. To use `demo/` as a standalone project, copy the published `addons/godot_csharp_profiler` directory and a generated C# project into it, then run the dependency setup described in the addon README.

`Main.cs` wraps `_Process` and `HotMethod` in manual profiler scopes. The intentionally busy loop should be visible while capture is active. It is demonstration workload, not a benchmark.
