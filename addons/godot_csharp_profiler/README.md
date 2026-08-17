# Godot C# Profiler addon

Standalone managed profiling for **Godot 4.7 .NET**. Sampling is the zero-code default; automatic Fody instrumentation and manual semantic scopes are optional exact-span workflows. No custom engine build, account, service, or telemetry is required.

## Install and sample

Place this directory at `res://addons/godot_csharp_profiler`, commit or back up the project, and run from the project root:

```powershell
pwsh addons/godot_csharp_profiler/assets/setup.ps1
dotnet restore
dotnet build
```
Pass `-Project path/to/Project.csproj` if more than one top-level project exists; use `-WhatIf` to preview the owned edit. Setup adds the pinned Sampling/Debug dependency import listed in `assets/dependencies.json` and does not install Fody. Restart Godot, then enable **Godot C# Profiler** under **Project > Project Settings > Plugins**.

Open the **C# Profiler** bottom dock, run the game, leave **Sampling** selected, press **Start**, exercise the workload, and press **Stop**. Sampling needs no source annotations or weaving. Start may also be pressed before the game target is ready: one request is queued while the debugger discovers the bridge and negotiates capabilities, then starts automatically on the selected compatible target. Stop cancels a pending request.

The shrinkable main dock contains only target/status, Start/Stop/Clear, timeline, selected-batch Copy, and calls/results. Open the compact **⚙** button for modes, filters, automatic installation, export, and quality diagnostics.

## Result semantics

- **Sampling** reports statistical managed stack **sample counts** and **estimated stack-frame share**. The percentage is a depth-weighted distribution of observed managed stack frames, not exact timing, top-of-stack CPU usage, or an exact call count; short calls may be missed.
- **Automatic** reports exact **observed wall-clock spans/calls** for supported, matched methods successfully woven into rebuilt assemblies. Observed wall time is not CPU time. Async/iterator/generated/native behavior and skipped methods need interpretation.
- **Manual** reports exact **observed wall-clock spans/calls** only for explicit `CsProfiler.Scope(...)` and `CsProfiler.Fn()` call sites. These are optional targeted hooks for named semantic operations.
- Sources stay separated and are never summed. Bounded methods, buffers, labels, timeline, and depth can drop, truncate, or force-close observations; inspect quality diagnostics.

## Sampling constraints

Sampling uses managed EventPipe and requires a diagnostics-capable runtime, `DOTNET_EnableDiagnostics` not set to `0`, and platform/runtime policy that permits diagnostics. Another capture or diagnostics session may own the process. Symbols/rundown, filters, ignored infrastructure threads, trace-epoch renewal, and workload duration affect results.

The interval is **process-startup-only**. Set `DOTNET_EventPipeSamplingRate` or legacy `COMPlus_EventPipeSamplingRate` in nanoseconds before launching the game, then rerun it. The supported configuration range is 100,000–1,000,000,000 ns; the default request is 2,000,000 ns. There is no live per-session change or reliable effective-value query. Sampling can be unavailable or constrained on sandboxed/exported runtimes, NativeAOT, mobile/web/consoles, or unsupported platforms.

## Advanced automatic instrumentation

Automatic profiling is opt-in and is not required for Sampling.

1. Commit the project and stop the game.
2. Open **⚙ > Automatic**, configure narrow filters and a method limit, and choose **Preview Install**.
3. Review every proposed change, then choose **Apply Confirmed**.
4. Run `dotnet clean` and `dotnet build`; restart Godot and the game.
5. Rebuild/restart after every rule change.

The archive supplies the matching `GodotCSharpProfiler.Fody` package in `assets/nuget`; Fody is pinned to 6.9.3. `setup.ps1` owns only the base sampling/editor dependency import. The previewed in-editor installer separately owns its Fody package references and `FodyWeavers.xml` element. Treat “needs build,” “needs restart,” “stale build,” and “no matches” as unresolved coverage states.

## Optional manual scopes

```csharp
using Apeworks.GodotCSharpProfiler;

using var operation = CsProfiler.Scope("Loading.Inventory");
using var function = CsProfiler.Fn();
```

Scopes are inert without an active owner. Main-thread scopes form a bounded tree; worker-thread scopes are aggregated. Late/out-of-order disposal and frame boundaries affect attribution. Sparse semantic scopes are preferable to hot fine-grained hooks.

## Bridge, disconnects, and reruns

Enabling the plugin installs its owned `CsProfilerBridge` autoload. The editor debugger discovers active game bridges and performs a strict versioned identity/capability handshake. Controls are enabled only for modes advertised by the selected runtime: Sampling requires successful dependency setup; Automatic requires a valid woven manifest.

Stopping the game disconnects that runtime, while completed editor-owned results remain visible. A rerun receives a new runtime identity and negotiates again; Start after Ready or leave one pre-target Start queued. Use Clear to discard preserved results.

## Troubleshooting

### Start disabled

- Wait for Ready/capability negotiation, or use the queued pre-target Start path.
- Open **⚙** and check whether the selected mode is supported by the target.
- For Sampling, rerun setup, restore/build, restart Godot, and ensure diagnostics are enabled.
- For Automatic, preview/apply, clean/build/restart, and resolve no-manifest, stale, no-match, needs-build, or needs-restart status.
- Stop another capture/diagnostics owner if the runtime is busy.

### No target

- Confirm Godot 4.7 .NET, a successful build, an enabled plugin, and a running editor-play game.
- Restart Godot after setup and inspect Output/Debugger for compile, autoload, bridge, handshake, or rejected-protocol errors.
- Ensure another path does not own the `CsProfilerBridge` autoload name; the plugin will not overwrite it.
- Stop stale debug sessions before rerunning the intended target.

### No frames or call rows

- Sampling includes all managed assemblies by default. Check any include/exclude prefixes under **⚙**.
- Inspect quality diagnostics for drops, truncation, ignored threads, missing symbols/rundown, or partial disconnect.
- Ensure `DOTNET_EnableDiagnostics` is not `0`; interval variables must be set before the game starts.
- Automatic/Manual rows require executed woven methods/scopes and do not substitute for Sampling rows.

## Export, privacy, and security

Selected-batch Copy is beside the batch selector; per-call Copy is in the row context menu. Export and diagnostics live under **⚙** and activate when results exist. Exports are local and source-separated. Review them before sharing because method/type names and project identifiers may be sensitive.

There is **no telemetry, analytics, account, or automatic upload**. NuGet restore can contact project-configured package sources. Protocol packets are versioned, identity/owner/sequence checked, strictly parsed, and bounded. Setup accepts a safe top-level SDK-style UTF-8 project, marks only its owned import, preserves unrelated project bytes, rejects symlink/reparse-point paths, and never edits `NuGet.Config`. Automatic changes require preview plus explicit confirmation.

## Safe removal

1. Stop capture and games.
2. If Automatic is installed, use **⚙ > Automatic > Preview Uninstall**, review, then **Apply Confirmed** before disabling the plugin.
3. Remove user-authored `CsProfiler.Scope` / `CsProfiler.Fn` calls and profiler namespace imports; host source is never rewritten automatically.
4. Disable the plugin so it removes only its owned bridge autoload.
5. While files remain, run:

   ```powershell
   pwsh addons/godot_csharp_profiler/assets/setup.ps1 -Action Remove
   ```

6. Delete this addon directory. If necessary clear `.godot/mono`, `bin`, and `obj`, then restore, clean/build, and restart Godot.
7. Inspect the diff; preserve unrelated NuGet/Fody configuration.

Windows x64 and Linux x64 on Godot 4.7 .NET/.NET 8 are intended targets, but platform diagnostics policy still applies. macOS, exports, NativeAOT, mobile, web, and consoles remain unverified or unsupported. Non-.NET Godot is unsupported. Automated builds/protocol tests do not prove the interactive dock visually.

See the repository `README.md`, this addon's `CHANGELOG.md`, `LICENSE`, and `THIRD-PARTY-NOTICES.md` for full details.
