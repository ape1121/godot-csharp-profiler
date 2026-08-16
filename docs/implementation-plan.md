# Godot C# Profiler Publication Implementation Plan

> **For Hermes:** Execute this plan through isolated Pi workers with one writer per file set. Integrate, review, and verify each wave before the next. Do not create the public GitHub repository until the exact release candidate is complete.

**Goal:** Ship a public Godot 4.7 .NET profiler addon with zero-code sampling, filtered automatic exact instrumentation, optional manual semantic scopes, a mode-aware editor UI, safe removal, and Asset Library-ready packaging.

**Architecture:** A versioned capture protocol connects one editor dock to one runtime bridge and interchangeable capture backends. `Sampling` uses self-process EventPipe and produces statistical stack aggregates. `Automatic Instrumentation` uses a filtered runtime backend selected only after Harmony and output-only Mono.Cecil proofs are compared; `Manual Scopes` uses a hardened stable facade. A future native CoreCLR backend implements the same backend/protocol contracts without redesigning the UI.

**Tech stack:** Godot 4.7 .NET, .NET 8, C#, Microsoft.Diagnostics.NETCore.Client, Microsoft.Diagnostics.Tracing.TraceEvent, Harmony or Mono.Cecil after measured selection, xUnit, GitHub Actions.

---

## Release invariants

1. No custom Godot engine build.
2. Sampling requires no game-source edits and can start/stop dynamically.
3. Automatic instrumentation is opt-in, filtered, bounded, exception-safe, and removable without leaving source references or broken project files.
4. Manual scopes remain optional; removing the full addon requires either deterministic call-site migration or retention of an explicitly documented tiny compatibility facade.
5. Every mode states coverage, precision, startup/restart requirements, and overhead in the UI.
6. One capture owner and generation at a time; stale work cannot contaminate successor captures.
7. Labels, stack depth, method count, packets, frame history, and retained memory are bounded with visible dropped/truncated counters.
8. Plugin disable/uninstall stops capture and leaves no stale autoload, Harmony patches, build imports, or generated artifacts.
9. Public release requires clean install → build → enable → capture in every mode → stop → disable → uninstall → rebuild → re-enable proof in a fresh project.
10. GitHub repository/release creation happens only after the exact candidate passes verification and independent review.

## Wave 1 — Standalone extraction and runtime correctness

### Task 1: Extract a self-contained addon

**Files:**
- `addons/godot_csharp_profiler/plugin.cfg`
- `addons/godot_csharp_profiler/Runtime/*`
- `addons/godot_csharp_profiler/Editor/*`
- root development project and tests

**Acceptance:** The standalone development project builds with zero warnings/errors and the addon contains every runtime/editor dependency under its own directory.

### Task 2: Harden manual-scope lifecycle

**Files:**
- `addons/godot_csharp_profiler/Runtime/CsProfiler.cs`
- `addons/godot_csharp_profiler/Runtime/CsProfilerSessionDiscoveryState.cs`
- `tests/GodotCSharpProfiler.Tests/*`

**Tests first:**
- scopes spanning frame flush have defined, non-corrupt behavior;
- worker scopes carry capture generation and cannot enter a successor capture;
- cross-thread/async disposal fails safely without poisoning nesting;
- label/path/cardinality bounds expose truncation/drop counters;
- malformed ready payloads fail closed;
- stop/reset is idempotent and exact.

**Acceptance:** Focused tests and standalone build pass; inactive manual-scope overhead remains a boolean fast path with no allocation for static names.

## Wave 2 — Automatic backends in parallel

### Task 3: Implement EventPipe sampling backend

**Files:**
- `Runtime/Sampling/*`
- `tests/GodotCSharpProfiler.Sampling.Tests/*`

**Behavior:** Self-attach with `DiagnosticsClient`, consume SampleProfiler stacks in a background worker, aggregate only selected project assemblies, and publish bounded immutable snapshots.

**Acceptance:** Actual .NET 8 self-process smoke resolves a named hot method; start/stop/fault states are deterministic; diagnostics-disabled failure is actionable; buffers and labels are bounded.

### Task 4: Compare automatic exact instrumentation candidates

**Proof A — Harmony:**
- patch project-assembly methods only;
- include/exclude namespace/type filters;
- skip profiler code, generated/trivial/accessor/native/abstract methods by default;
- handle exceptions with finalizers;
- classify async/iterator state-machine coverage;
- prove owner-scoped unpatch/restart/reload behavior.

**Proof B — output-only Mono.Cecil:**
- weave copied build outputs, never source;
- preserve symbols and exception handlers;
- implement deterministic include/exclude filters;
- prove rebuilding without integration restores clean output;
- prove safe `.csproj` install/uninstall if selected.

**Selection gate:** Choose Harmony only if filtered coverage, unpatch/reload safety, async classification, and measured overhead are acceptable. Otherwise choose reversible output-only weaving. Record the decision and limitations in `docs/backend-decision.md`.

### Task 5: Implement selected automatic backend

**Files:**
- `Runtime/Instrumentation/*`
- focused tests/fixtures

**Acceptance:** No source call sites; bounded method inventory preview; explicit restart/build requirement; exact entry/exit/call counts for supported methods; exceptions do not leak timers; uninstall/restart leaves no instrumentation.

## Wave 3 — Versioned protocol and mode-aware UI

### Task 6: Define backend-neutral capture protocol

**Files:**
- `Runtime/Protocol/*`
- protocol tests

**Messages:** versioned `hello/capabilities`, `configure`, `start`, `state`, `batch`, `stop`, `error`. Every payload has exact type/count validation and bounds.

**Acceptance:** Unsupported versions, malformed packets, stale generations, and oversized batches fail closed without mutating panel state.

### Task 7: Implement mode-aware editor dock

**Files:**
- `Editor/CsProfilerPlugin.cs`
- `Editor/CsProfilerDebuggerPlugin.cs`
- `Editor/CsProfilerPanel.cs`
- new mode/settings controls and UI tests

**UI:**
- Target selector and connection state.
- Mode selector: Sampling / Automatic Instrumentation / Manual Scopes.
- Mode summary showing precision, coverage, overhead, source/build/restart requirements.
- Sampling interval and assembly filters.
- Automatic method-count preview, include/exclude filters, unsupported-method count, and restart/install state.
- Manual-scope instrumentation help and compatibility-facade status.
- Start/Stop, Clear, Copy, Export; dropped/truncated counters and backend status.
- Mode changes disabled during capture; explicit stop/reconfigure flow.

**Acceptance:** UI never labels sampling as exact; automatic mode never implies full coverage; unavailable dependencies show actionable remediation; active capture survives harmless editor reload but terminal disable sends Stop.

## Wave 4 — Safe install/removal and publication package

### Task 8: Implement installation lifecycle

**Files:**
- editor installer/settings code
- package tests and fresh-project fixture

**Acceptance:** Enable installs the stable addon-local bridge/autoload idempotently. Disable stops capture. Uninstall removes autoload/build integration/generated outputs. Rebuild succeeds after deletion. Existing manual call sites are handled by an explicit migration command or documented compatibility-facade choice—never silently broken.

### Task 9: Add documentation and presentation

**Files:**
- root and addon-local `README.md`, `LICENSE`, `CHANGELOG.md`
- `docs/modes.md`, `docs/install.md`, `docs/troubleshooting.md`, `docs/backend-decision.md`
- icon, screenshots with `.gdignore`, demo project

**Acceptance:** Documentation clearly distinguishes statistical sampling, exact supported automatic hooks, and semantic manual scopes; lists platform/version support and measured overhead; includes install, disable, uninstall, and migration procedures.

### Task 10: Add CI and release packaging

**Files:**
- `.github/workflows/ci.yml`
- `.github/workflows/release.yml`
- package validation scripts

**Acceptance:** Linux CI builds/tests, validates `plugin.cfg`, verifies archive root/layout and addon-local README/license, rejects generated artifacts, and smoke-installs the produced ZIP. Add Windows tests for EventPipe and selected automatic backend before stable release.

## Wave 5 — Exact-candidate verification and publication

1. Run all unit/integration tests serially.
2. Run Godot headless import/editor smoke in a fresh .NET project.
3. Run live editor-attached capture in Sampling, Automatic, and Manual modes.
4. Measure inactive, Sampling, and Automatic overhead against fixed fixtures; publish honest numbers.
5. Run clean disable/uninstall/rebuild/re-enable acceptance.
6. Freeze exact candidate bytes and obtain independent runtime, editor UX, packaging/security, and final holistic reviews.
7. Fix blockers, then rerun invalidated tests/reviews.
8. Create public `ape1121/godot-csharp-profiler` only now.
9. Push `main`, create signed/tagged `v1.0.0` release with Asset Library ZIP, download and smoke-test that exact asset.
10. Fetch and prove local/remote commit and tree equality; submit to Godot Asset Library with matching MIT metadata and minimum supported Godot version.

## Parallel ownership map

- **Runtime-hardening worker:** existing manual runtime core and pure tests.
- **Sampling worker:** only `Runtime/Sampling` and sampling tests.
- **Harmony proof worker:** isolated proof/fixtures; no shared production files.
- **Mono.Cecil proof worker:** isolated proof/fixtures; no shared production files.
- **UI/protocol worker:** starts only after backend contracts are frozen.
- **Packaging/docs worker:** starts after paths/API are stable.
- **Coordinator:** architecture decisions, shared-file integration, builds/Godot runs, reviews, GitHub release, remote verification, and worktree cleanup.
