# Automatic instrumentation backend decision

**Status:** Accepted for v1

## Decision

Use a custom Fody add-in backed by Mono.Cecil for the opt-in **Automatic Instrumentation** mode. Keep EventPipe for **Sampling** and the addon runtime facade for **Manual Scopes**. Do not ship Harmony as a production dependency in v1. Keep the capture protocol backend-neutral so a native CoreCLR profiler can be added later.

## Evidence

### EventPipe sampling

A .NET 8 process successfully attached to itself, consumed SampleProfiler events on a background thread, resolved managed methods/stacks, and stopped cleanly. Sampling remains statistical and zero-instrumentation.

### Harmony

The retained proof under `spikes/HarmonyInstrumentation` passed 10/10 tests and demonstrated filtered patching, exact accounting, exception cleanup, and owner-scoped unpatch. Measured Release results for a small fixture:

- patch startup: 4.9197 ms for eight methods;
- baseline: 2.382 ns/call;
- patched while capture disabled: 186.006 ns/call;
- patched while capture enabled: 265.682 ns/call;
- collectible `AssemblyLoadContext` remained rooted after unpatch and forced collection.

Harmony is therefore conditionally useful for a tiny explicit allowlist, but it is rejected as v1's default automatic backend because patched hot methods retain high overhead while not recording and conflict with managed reload expectations.

### Mono.Cecil/Fody

The retained executable proof under `spikes/CecilInstrumentation`:

- classified supported and unsupported methods deterministically;
- instrumented a copied assembly, never source output;
- preserved a readable portable PDB;
- produced exact ordinary, recursive, overloaded, generic, and throwing-method entry/exit counts;
- cleaned up through exceptions;
- rejected double weaving;
- left the source DLL hash unchanged;
- added 1024 bytes to the fixture and completed weaving in approximately 81–83 ms in independent runs.

## Product semantics

- **Sampling:** zero-code, statistical managed CPU stacks, low expected overhead, dynamically started/stopped when .NET diagnostics are available.
- **Automatic Instrumentation:** exact observed entry/exit spans for the eligible filtered set; requires explicit build integration, clean rebuild, and game restart when enabled, removed, or reconfigured.
- **Manual Scopes:** exact semantic spans intentionally authored by the developer; optional overlay with either Sampling or Automatic Instrumentation.

"Exact" never means whole-runtime coverage. Automatic mode must report eligible, instrumented, and skipped method counts and reasons. Sampling must never report exact calls or exact per-call duration.

## Production requirements beyond the proof

The production Fody weaver must add:

- arbitrary project/PDB-root include and exclude filters;
- uncommon-IL validation and deterministic skip reasons;
- nested existing exception-handler regression fixtures;
- bounded manifest and method labels;
- idempotent marker/config hash;
- addon runtime recorder integration with generation ownership;
- measured inactive and enabled overhead;
- atomic, ownership-marked project/Fody configuration installation and removal;
- clean rebuild/uninstall acceptance.

Harmony remains research evidence only. Native CoreCLR profiling remains a future advanced backend requiring separately distributed platform-native binaries and launcher/startup integration.
