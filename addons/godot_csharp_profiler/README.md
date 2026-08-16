# Godot C# Profiler addon

Godot 4.7 .NET managed profiling with manual exact wall-time scopes, statistical managed EventPipe sampling, and opt-in Fody automatic exact wall-time spans. No custom engine build is required. There is no telemetry; captures remain local unless you export them.

## Install

After placing this directory at `res://addons/godot_csharp_profiler`, run from the project root:

```powershell
pwsh addons/godot_csharp_profiler/assets/setup.ps1
dotnet restore
dotnet build
```

Restart Godot and enable **Godot C# Profiler** in Project Settings > Plugins. Extraction alone is compile-safe: sampling's external references are gated until setup imports `assets/GodotCSharpProfiler.Dependencies.props`. Exact dependency versions and local feed location are in `assets/dependencies.json`.

For automatic instrumentation, first commit your project, then run setup with `-EnableAutomatic`. The archive supplies the exact matching `GodotCSharpProfiler.Fody` package in `assets/nuget`; Fody itself is restored at exact version 6.9.3. Configure filters, close the game, clean/build, and restart both editor and game. Rebuild/restart after every rule change. Remove owned references/weaver configuration before deleting the addon.

## Semantics and limitations

- Sampling is statistical CPU distribution, may miss short calls, depends on symbols/runtime data, and is not exact wall time.
- Sampling interval is process-startup-only through `DOTNET_EventPipeSamplingRate` (nanoseconds); no per-session/live change or effective-value query exists. Disabled diagnostics, platform policy, or another session can prevent startup.
- Automatic and manual values are observed wall-clock spans/calls, not CPU time. Automatic coverage is only rebuilt, matched, successfully woven methods; generated/async/native behavior needs interpretation.
- Manual coverage is only `CsProfiler.Scope(...)` / `CsProfiler.Fn()` call sites. Inactive scopes return immediately.
- Modes have separate result semantics and are never summed. Fine sampling, broad weaving, or hot scopes increase overhead.
- Bounded buffers/nodes/depth/labels can drop, overflow, or truncate data; inspect quality diagnostics.

Manual example:

```csharp
using Apeworks.GodotCSharpProfiler;
using var scope = CsProfiler.Scope("Loading.Inventory");
```

## Platform status

Windows x64 and Linux x64 with Godot 4.7 .NET/.NET 8 are release targets; CI builds/tests/packages but does not launch the editor. macOS, mobile, web, consoles, NativeAOT, and exports are unverified/unsupported until explicitly tested. Non-.NET Godot is unsupported.

## Uninstall

Stop games/capture, disable the plugin, run:

```powershell
pwsh addons/godot_csharp_profiler/assets/setup.ps1 -Action Remove
```

Review/remove only addon-owned `NuGet.Config`, project references/imports, and `FodyWeavers.xml` element; delete this directory; clear stale `.godot/mono`, `bin`, and `obj`; restore, rebuild, and restart. Keep unrelated NuGet/Fody configuration. See `CHANGELOG.md` and `LICENSE`.
