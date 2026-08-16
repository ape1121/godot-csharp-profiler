# Godot C# Profiler

A standalone, local managed-code profiler addon for **Godot 4.7 .NET** projects. It uses Godot's editor/debugger APIs and .NET diagnostics; no custom engine build, account, service, or telemetry is required.

> Pre-release: install into a disposable or source-controlled project first. Commit before enabling automatic instrumentation.

## Sampling-first quick start

Sampling is the universal, zero-code default. You do not need to annotate or weave game code.

1. Back up or commit the Godot project.
2. Extract the release ZIP into the project root. The archive is rooted at `addons/godot_csharp_profiler`.
3. From the project root run:

   ```powershell
   pwsh addons/godot_csharp_profiler/assets/setup.ps1
   dotnet restore
   dotnet build
   ```

   If the project has more than one top-level `.csproj`, pass the intended one explicitly with `-Project`. Use `-WhatIf` to preview the ownership-marked project edit. Setup imports the pinned sampling dependencies listed in `assets/dependencies.json` (`Microsoft.Diagnostics.NETCore.Client 0.2.661903` and `Microsoft.Diagnostics.Tracing.TraceEvent 3.2.5`) and the Debug editor API copy requirement; it does not install Fody.
4. Restart Godot and enable **Godot C# Profiler** under **Project > Project Settings > Plugins**.
5. Open the **C# Profiler** bottom dock. Leave the mode at **Sampling**.
6. Run the project and press **Start**. You may also request Start before the game target finishes launching: the debugger keeps one pending request, discovers the runtime bridge, negotiates its capabilities, and starts automatically when that target is ready. Press **Stop** to cancel a pending request or finish a running capture.
7. Exercise the workload, press **Stop**, then inspect the frame timeline and **Sampling (estimated)** call rows. **Clear** removes editor-owned results.

The main dock deliberately stays small and shrinkable: target/status, Start/Stop/Clear, frame timeline, and calls/results. Open the compact **⋮** corner button for mode selection, filters, installer controls, export, and capture-quality diagnostics.

## Understand the numbers

The three sources have different semantics. They remain source-separated and must not be added together.

| Mode | What it reports | Coverage and interpretation |
|---|---|---|
| **Sampling** (default) | Statistical managed stack **sample counts** and **estimated stack-frame share**. The percentage is a depth-weighted distribution of observed managed stack frames, not exact duration, top-of-stack CPU usage, or an exact call count. | Managed methods observed at EventPipe sample points, subject to runtime/platform support, symbols/rundown, thread filtering, and assembly filters. Short calls may never be sampled. |
| **Automatic instrumentation** (advanced opt-in) | Exact **observed wall-clock spans and call counts** recorded at injected method entry/exit hooks. “Exact” describes the observed spans; it does not mean CPU time. | Only supported methods that match the configured rules and are successfully woven into rebuilt assemblies. Generated code, async/iterator state machines, native/engine work, exceptions, and skipped methods require careful interpretation. |
| **Manual scopes** (optional) | Exact **observed wall-clock spans and call counts** for named semantic operations. They are not CPU time. | Only user-authored `CsProfiler.Scope(...)` and `CsProfiler.Fn()` sites. Useful for targeted hooks such as loading phases or gameplay operations. |

Buffers, methods, timeline points, labels, call depth, and retained nodes are bounded. Drops, overflow, forced closure, filtering, or disconnects can make a capture partial. Check the quality diagnostics in **⋮** before drawing conclusions.

## Sampling settings and limitations

The addon samples the current managed game process through in-process EventPipe collection. Sampling requires a diagnostics-capable .NET runtime, `DOTNET_EnableDiagnostics` not set to `0`, and platform/runtime policy that permits EventPipe. Only one managed sampling session can own the process at a time.

The sampling interval is **startup-only**. Set `DOTNET_EventPipeSamplingRate` (or legacy `COMPlus_EventPipeSamplingRate`) to a nanosecond value **before starting the game process**, then rerun it. The supported configuration range is 100,000 through 1,000,000,000 ns; the default request is 2,000,000 ns. The runtime offers no live per-capture interval change and may not report an effective value, so changing the field while a game is already running does not retune that process. Shorter intervals increase CPU and data cost.

Sampling renews bounded in-memory trace epochs (30 seconds by default). Epoch renewal and unavailable rundown/symbol information can affect names and diagnostics. Sampling may be unavailable or constrained on some operating systems, sandboxed/exported games, disabled-diagnostics environments, NativeAOT, mobile/web/consoles, or runtimes already owned by another diagnostics session.

## Advanced automatic instrumentation

Automatic mode is an opt-in build transformation for exact supported-method spans. It is not required for Sampling.

1. Commit the project and stop all running games.
2. Open **⋮ > Automatic** in the profiler.
3. Configure narrow include/exclude patterns and a method limit.
4. Choose **Preview Install**, review every proposed project/Fody change, then choose **Apply Confirmed**.
5. Run:

   ```powershell
   dotnet clean
   dotnet build
   ```

6. Restart Godot and the game. Rebuild and restart after every instrumentation-rule change.

The release archive contains the matching `GodotCSharpProfiler.Fody` package in `addons/godot_csharp_profiler/assets/nuget`; the installer does not assume that package is published on nuget.org. Fody is pinned to 6.9.3. Setup and automatic installation are intentionally separate: `setup.ps1` owns the base sampling/editor dependency import, while the previewed in-editor installer owns its Fody package references and `FodyWeavers.xml` element. “Needs build,” “needs restart,” “stale build,” and “no matches” are distinct states, not successful coverage claims.

Broad weaving and frequently called methods can add material overhead. Verify rebuilt output and profiler diagnostics before relying on results.

## Optional manual semantic scopes

```csharp
using Apeworks.GodotCSharpProfiler;

using var operation = CsProfiler.Scope("Inventory.Rebuild");
using var currentFunction = CsProfiler.Fn();
```

Manual scopes are inert when no capture owns the runtime. Main-thread scopes form a bounded call tree; worker-thread scopes are aggregated. Late or out-of-order disposal and frame boundaries affect attribution. Keep scopes sparse and semantic; fine-grained hot scopes can materially perturb the workload. See [`demo/`](demo/) for a small Godot 4.7 .NET example.

## Runtime bridge and reruns

Enabling the plugin installs its owned `CsProfilerBridge` autoload. During editor play, the debugger discovers active bridges, chooses one target, exchanges a strict versioned hello/capabilities handshake, and enables only modes the selected runtime actually advertises. Sampling is unavailable if the addon was not set up for sampling; Automatic is unavailable without a valid manifest from a woven build.

A game stop or disconnect ends that runtime identity, but completed editor-owned results remain visible for post-mortem inspection. On rerun, a new runtime token triggers fresh bridge discovery and capability negotiation; start another capture after Ready, or leave the single pre-target Start request queued. The previous capture is never silently treated as the new process.

## Troubleshooting

### Start is disabled

- Wait for **Ready** or capability negotiation to complete. A Start requested before target readiness is shown as waiting and is sent when compatible capabilities arrive.
- Check the selected mode under **⋮**. Modes are enabled from negotiated runtime capabilities, not assumptions.
- For Sampling, rerun setup, restore/build successfully, restart Godot, and confirm diagnostics are enabled.
- For Automatic, complete Preview/Apply, clean/build, and restart; confirm the UI does not report no manifest, stale build, no matches, or needs restart.
- Stop another capture or diagnostics owner if the target reports busy. One capture owns a runtime at a time.

### No target / bridge not found

- Confirm this is Godot **4.7 .NET**, the plugin is enabled, the project builds, and an editor-play game is actually running.
- Restart Godot after setup. Check the Godot Output/Debugger for plugin, autoload, compile, handshake, or rejected-payload messages.
- Ensure the autoload name `CsProfilerBridge` is not owned by another path. The plugin refuses to overwrite someone else's autoload.
- If several debug sessions exist, stop stale sessions and rerun the intended target.

### Capture runs but no frames or calls appear

- Sampling includes all managed assemblies by default. If you set an include prefix under **⋮**, an over-narrow value can filter all game methods.
- Inspect quality diagnostics for dropped/truncated observations, unavailable symbols/rundown, ignored threads, or a partial disconnect.
- Verify `DOTNET_EnableDiagnostics` is not `0`. Set any interval environment variable before process startup and rerun the game.
- Automatic and Manual produce rows only when woven supported methods or explicit scopes execute. They do not manufacture Sampling rows.

### Disconnect or rerun

A stopped game is reported as disconnected and completed results are intentionally preserved. Stop/cancel any pending request, rerun the game, allow the new bridge to negotiate, and Start again. Use Clear when you no longer need the preserved result.

## Export, privacy, and security

Copy/export controls and quality diagnostics are under **⋮** and are enabled only when results exist. Exports are written locally under the Godot project and retain source-separated semantics. Review them before sharing: managed method/type names and project identifiers may be sensitive.

There is **no telemetry, analytics, account, or automatic upload**. Capture data stays in the local Godot/.NET processes unless you copy or export it. NuGet restore can contact the package sources configured by your project.

Protocol messages are versioned, identity/owner checked, sequence checked, strictly parsed, and bounded before aggregation. Setup requires a top-level SDK-style UTF-8 project, rejects unsafe symlink/reparse-point paths, preserves unrelated project bytes, marks its owned import, and never edits `NuGet.Config`. Automatic changes require a fresh preview token and explicit confirmation. Release packaging is deterministic, keeps third-party notices/licenses, and validates archive layout, content, and size.

## Safe disable and removal

1. Stop capture and all running games.
2. If Automatic was installed, while the plugin is still enabled open **⋮ > Automatic**, choose **Preview Uninstall**, inspect the changes, and **Apply Confirmed**. Keep unrelated NuGet/Fody configuration.
3. Remove or replace every user-authored `CsProfiler.Scope(...)` / `CsProfiler.Fn()` call and `using Apeworks.GodotCSharpProfiler;` directive. The addon intentionally never rewrites host source.
4. Disable **Godot C# Profiler** in Project Settings. Disable removes only the bridge autoload when its value is owned by this addon.
5. While addon files still exist, run:

   ```powershell
   pwsh addons/godot_csharp_profiler/assets/setup.ps1 -Action Remove
   ```

   This removes only the ownership-marked base dependency import and does not edit `NuGet.Config`.
6. Delete `addons/godot_csharp_profiler`.
7. If woven or compiled output is stale, delete `.godot/mono` and project `bin`/`obj`; then run `dotnet restore`, `dotnet clean`, and `dotnet build`, and restart Godot.
8. Inspect the diff and confirm no profiler references, owned autoload, or woven output remains before committing.

## Compatibility

| Environment | Status |
|---|---|
| Godot 4.7 .NET / .NET 8, Linux x64 | Editor-play bridge/manual lifecycle and managed EventPipe tests have been exercised locally; runtime permissions and diagnostics policy still apply. |
| Godot 4.7 .NET / .NET 8, Windows x64 | Build/test/package target; perform project-specific editor/runtime acceptance before relying on release behavior. |
| macOS | Unverified. |
| Android, iOS, Web, consoles, NativeAOT, export templates | Unsupported or unverified; EventPipe and editor integration are commonly unavailable or constrained. |
| Godot non-.NET | Unsupported. A C# project is required. |

CI builds, tests, and packages but does not prove interactive dock layout or a visual editor lifecycle. Do not infer a visual/headless capability claim from automated protocol tests.

## Releases and development

`pwsh scripts/build-release.ps1 -Version X.Y.Z` builds the matching Fody package, stages only the addon, creates a deterministic ZIP, validates layout/content/size, and writes hashes under `artifacts/release/`. See [`docs/publication/asset-library-submission.md`](docs/publication/asset-library-submission.md).

MIT © 2026 Apeworks. See [LICENSE](LICENSE).
