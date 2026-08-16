# Harmony filtered instrumentation feasibility proof

This isolated .NET 8 spike tests whether `Lib.Harmony` can provide opt-in automatic method instrumentation without broad patch-all behavior.

## Run

```sh
dotnet test tests/GodotCSharpProfiler.Harmony.Tests/GodotCSharpProfiler.Harmony.Tests.csproj -c Release --logger "console;verbosity=normal"
```

A .NET 8 runtime is required. The host used for the recorded result had only .NET 10 globally, so the proof was run with a local .NET 8.0.424 SDK (`/tmp/dotnet8-harmony-proof/dotnet`). Harmony 2.3.6 rejects CoreCLR 10.

## Design constraints proven

- Discovery begins only from explicit `SelectedTypes`; there is no assembly-wide or AppDomain patch-all API.
- `Preview()` provides deterministic supported/skipped inventory before mutation.
- Method count and rendered names are bounded.
- Default exclusions cover compiler-generated methods, accessors, constructors, trivial IL, and profiler namespaces.
- Abstract/native/extern/open-generic methods are classified but skipped.
- Async and iterator `MoveNext`, accessors, and constructors require explicit opt-in.
- Harmony owners are unique caller-provided IDs and cleanup uses `UnpatchAll(owner)` only.
- Prefix/finalizer state records exact calls, inclusive `Stopwatch` ticks, and exception counts; finalizers preserve exceptions.
- Repeated patch/unpatch preserves another Harmony owner's patch.

## Decision

**Conditionally feasible for narrow, explicitly selected, non-reloadable method sets.** Do not adopt for hard-reload/collectible contexts with Harmony 2.3.6, and do not apply indiscriminately to hot methods. See [`result.json`](result.json) for machine-readable metrics and evidence.

The collectible test deliberately proves the current limitation: after owner unpatch, counter removal, `AssemblyLoadContext.Unload()`, and forced GC cycles, the context remains rooted. This is evidence against hard-reload feasibility rather than an ignored test.
