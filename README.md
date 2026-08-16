# Godot C# Profiler

A local, open-source managed-code profiler addon for **Godot 4.7 .NET** projects. It uses standard Godot C# editor/runtime APIs and .NET diagnostics; **no custom engine build is required**.

> Pre-release: validate against a disposable, source-controlled project before adopting automatic instrumentation.

## Capture modes

| Mode | Meaning | Coverage | Typical overhead |
|---|---|---|---|
| **Sampling** | Statistical EventPipe stack samples; percentages are estimates, not exact timings. | Managed stacks observed at sample points, subject to runtime symbol/rundown availability and filters. Short calls may never be sampled. | Low at the default 2 ms startup interval; shorter intervals increase CPU/data cost. |
| **Automatic instrumentation** | Fody injects exact entry/exit spans at build time. “Exact” means observed wall-clock span/call counts, not CPU time. | Only methods matching filters and limits in rebuilt assemblies. Async state machines, iterators, generated/native/engine code, and skipped methods require careful interpretation. | Moderate and proportional to instrumented call frequency; broad/hot instrumentation can be high. |
| **Manual scopes** | `using var scope = CsProfiler.Scope("label")` or `CsProfiler.Fn()` records exact observed wall-clock spans. | Only code paths explicitly wrapped by the project. | Near-zero while capture is inactive; low when sparse, but hot/fine scopes can be material. |

Sources remain separated; values from sampling and spans are not summed. Buffers, labels, stack depth, and retained nodes are bounded, so quality counters and partial/truncated results matter.

## Install from the Asset Library ZIP

The ZIP is intentionally rooted at `addons/godot_csharp_profiler`.

1. Back up or commit the project.
2. Extract into the project root.
3. From the project root run:
   ```powershell
   pwsh addons/godot_csharp_profiler/assets/setup.ps1
   dotnet restore
   dotnet build
   ```
4. Restart Godot, then enable **Project > Project Settings > Plugins > Godot C# Profiler**.

The raw addon includes addon-local framework global usings and gates the only source file needing external diagnostics assemblies, so extraction compiles regardless of the host project's `ImplicitUsings` setting and does not introduce unresolved `Microsoft.Diagnostics.*` references. Setup imports exact, publicly available sampling dependencies: `Microsoft.Diagnostics.NETCore.Client 0.2.661903` and `Microsoft.Diagnostics.Tracing.TraceEvent 3.2.5`. `assets/dependencies.json` is the machine-readable manifest.

### Sampling limitations

Sampling is local, in-process EventPipe collection. It needs .NET diagnostics (`DOTNET_EnableDiagnostics` must not be `0`) and sufficient platform/runtime support. Only one managed sampling session may run. The interval cannot be changed per session or at runtime: set `DOTNET_EventPipeSamplingRate` (or legacy `COMPlus_EventPipeSamplingRate`) in nanoseconds **before the game process starts**, then restart. The runtime does not report the effective value. The implementation renews bounded in-memory trace epochs (default 30 seconds); renewal and missing rundown/symbol data can affect diagnostics and names.

### Automatic mode

The release archive contains the exact `GodotCSharpProfiler.Fody` nupkg built from the matching source under `assets/nuget`; it does **not** assume that package exists on nuget.org.

In Godot, open the profiler's **Automatic** mode, choose **Install**, review the ProjectInstaller preview, and apply it. `setup.ps1` is sampling-only and intentionally does not duplicate Fody references or XML edits. Then configure include/exclude rules and run:

```powershell
dotnet clean
dotnet build
```

Close running games, rebuild, and restart Godot/game after every instrumentation configuration change. “Needs build,” “needs restart,” “no matches,” and “stale build” are distinct states. To remove weaving, use the removal steps below, delete owned `Fody`/`GodotCSharpProfiler.Fody` references and the owned element in `FodyWeavers.xml` if present, then clean/rebuild/restart. Inspect diffs before accepting installer changes.

## Manual scopes

```csharp
using Apeworks.GodotCSharpProfiler;

using var operation = CsProfiler.Scope("Inventory.Rebuild");
using var currentFunction = CsProfiler.Fn();
```

Scopes become inert when no capture owns the runtime. Main-thread scopes form a bounded call tree; worker-thread scopes are aggregated. Out-of-order/late disposal and frame boundaries affect where spans appear. See [`demo/`](demo/) for a Godot 4.7 .NET scene and hot method.

## Compatibility

| Environment | Manual | Sampling | Automatic | Notes |
|---|---:|---:|---:|---|
| Godot 4.7 .NET, .NET 8, Windows x64 | Expected | Supported by .NET EventPipe; release validation required | Expected | CI builds/tests/packages on Windows but does not launch Godot. |
| Godot 4.7.1 .NET, .NET 8, Linux x64 | Verified editor-play lifecycle | Managed EventPipe suite verified; permissions/runtime policy apply | Verified build/weave path | Local release acceptance launched Godot headless, registered one dock/bridge, captured and rendered a strict-protocol Manual result, then disabled cleanly. CI itself does not launch Godot. |
| macOS | Unverified | Unverified | Unverified | No release claim until tested. |
| Android/iOS/Web, consoles, native AOT/export templates | Unsupported/unverified | Generally unavailable or constrained | Unverified | Editor-oriented addon; do not ship profiler integration without validation. |
| Godot non-.NET | Unsupported | Unsupported | Unsupported | C# project required. |

## Privacy and security

There is **no telemetry**, analytics, account, or network upload. Capture data stays in the local Godot/.NET processes unless the user copies or exports it. Sampling can expose managed type/method names; exports may contain project identifiers. Review before sharing. NuGet restore contacts configured package sources.

## Safe disable and uninstall

1. Stop capture and running games; disable the plugin in Project Settings.
2. Run `pwsh addons/godot_csharp_profiler/assets/setup.ps1 -Action Remove` while files still exist; this removes only its owned sampling import and never edits `NuGet.Config`.
3. In Automatic mode, preview and apply ProjectInstaller uninstall to remove only its owned package references and `FodyWeavers.xml` element.
4. Delete `addons/godot_csharp_profiler`.
5. Delete `.godot/mono`, and project `bin`/`obj` if stale woven output remains; then `dotnet restore`, clean/build, and restart Godot.
6. Confirm the project builds and no profiler references or woven outputs remain before committing.

## Releases and development

`pwsh scripts/build-release.ps1 -Version X.Y.Z` builds the matching Fody package, stages only the addon, creates a deterministic ZIP, validates layout/content/size, and writes hashes under `artifacts/release/`. CI does not claim to run the Godot editor. See [`docs/publication/asset-library-submission.md`](docs/publication/asset-library-submission.md).

MIT © 2026 Apeworks. See [LICENSE](LICENSE).
