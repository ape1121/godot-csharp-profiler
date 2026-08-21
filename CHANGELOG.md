# Changelog

All notable changes use [Keep a Changelog](https://keepachangelog.com/) conventions.

## [0.2.3] - 2026-08-20

### Fixed

- Starting or stopping the profiler after rebuilding game C# code no longer throws a
  `NullReferenceException` from `CsProfilerPanel.OnStartPressed`. Godot retains the native editor
  dock across managed assembly reloads but reconstructs its C# instance without rerunning
  `_Ready()`. The first retained control action now rebuilds the panel's managed controller and
  control tree in place, restores debugger sessions by reload-stable native IDs, rebinds command
  transport, and continues the original action.
- Reload recovery now clears disposed control wrappers before rebuilding and creates the controller
  only after the replacement UI exists, preventing follow-on disposed-object failures during editor
  polling.
- Active sampling teardown no longer runs a potentially unbounded EventPipe Stop call on Godot's
  debugger callback. One asynchronous epoch stop remains authoritative, stream backpressure has a
  bounded abort fallback, and reset acknowledgement is withheld until runtime and local processing
  are confirmed inactive.
- Managed-reload recovery preserves one process-wide sampling lease across collectible C# contexts,
  serializes epoch replacement, rejects conflicting reset retries, and starts the fresh generation
  only after the orphaned generation's matching reset acknowledgement.
- The sampling interval field now consistently converts displayed milliseconds to protocol
  nanoseconds when configuration is changed and retained across reload.

## [0.2.2] - 2026-08-19

### Fixed

- The frames timeline no longer stays a fixed ~56px sliver squished between the toolbar and the
  results tab bar. The strip now scales with the dock (~30% of its height, floored at 72px,
  capped at 260px) so timeline bars stay readable in tall docks while the call list keeps the
  majority of the space.

## [0.2.1] - 2026-08-19

### Fixed

- Stop now takes effect immediately while the game is still running. Previously the stop command
  was rejected by the runtime whenever result batches were in flight (its sequence number went
  stale), so capture continued until the game exited or Clear was pressed. Stop commands are now
  validated by generation, fingerprint, and lease owner with a lag-tolerant sequence bound, and no
  longer consume a protocol sequence slot.
- Switching back to a single instance after profiling multiple instances no longer leaves the
  panel bound to a dead instance preference; a preference that can no longer be honored is
  dropped instead of sticking until an editor restart.

### Changed

- Expand all / Collapse all moved from a separate row above the batch tree into the main toolbar
  as compact icon buttons (⊞/⊟) at the right of Copy, so the timeline no longer gets pushed up.
  The buttons disable when no grouped batch is shown.

## [0.2.0] - 2026-08-19

### Added

- Instance selector in the toolbar: when Run Multiple Instances (or any extra debugger-attached
  game process) is active, a dropdown lists every attached instance (name, PID, editor marker)
  and switches the profiled target; selection survives session churn and falls back automatically
  when the chosen instance exits.
- Selected-batch calls are grouped under their declaring type (for example `SaveManager`,
  `SaveSlotRepository`) with aggregated samples/share or wall time/calls/max on the group row,
  collapsible arrows, and Expand all / Collapse all buttons. Expansion state persists across
  batch re-renders.
- Selecting a group row copies all of its member calls; individual member rows keep the concise
  plain-line copy format.

## [0.1.1] - 2026-08-17

### Fixed

- Copy and Export remain enabled while reviewing retained captured batches after Stop.
- Selected-batch call rows support multi-selection and copy as concise plain-text lines.
- Timeline Export writes the complete retained capture as JSON.
- Removed redundant sampling-default text from Options.

## [0.1.0] - 2026-08-17

### Added

- Sampling-first standalone workflow for Godot 4.7 .NET: zero-code managed EventPipe capture is the default, with negotiated runtime capabilities and source-separated sample counts/estimated stack-frame share.
- A compact, shrinkable profiler dock focused on target/status, Start/Stop/Clear, frame timeline, and calls/results; advanced modes, filters, installer controls, export, and quality diagnostics now live behind the corner settings button.
- Queued pre-target Start: one capture request can wait for debugger bridge discovery and capability negotiation, be cancelled with Stop/Clear, and start automatically on the compatible selected runtime.
- Populated protocol-native timelines and source-specific result tables for Sampling, Automatic instrumentation, and Manual scopes.
- Advanced opt-in Fody automatic instrumentation with previewed, explicitly confirmed install/uninstall and exact observed wall-clock span/call results for supported matched methods.
- Optional manual semantic scopes through `CsProfiler.Scope(...)` and `CsProfiler.Fn()`.
- Focused lifecycle and protocol regressions covering negotiated capabilities, mode-command enablement, pending Start, result/frame rendering, disconnect/restart preservation, and responsive small-height layout.
- Asset Library distribution lane, deterministic archive validation, offline matching instrumentation package feed, dependency setup/removal scripts, and retained third-party notices/licenses.

### Changed

- Sampling, Automatic, and Manual semantics are explicitly separate: statistical samples/estimated share are never presented as exact timings or combined with observed span totals.
- Runtime/editor capture now uses strict versioned bridge discovery, fresh runtime identities across reruns, single-capture ownership, bounded aggregation, and defensive payload validation.
- Disconnects preserve completed editor-owned results for inspection while reruns perform fresh discovery and capability negotiation.
- Documentation now leads with Sampling setup and quick start, explains startup-only sampling intervals and platform/runtime constraints, and includes disabled Start/no target/no frames troubleshooting plus safe removal steps.

### Security

- No telemetry or automatic upload is performed; capture and exports remain local unless the user shares them.
- Setup edits only an ownership-marked top-level project import, rejects unsafe path indirection, preserves unrelated project content, and never edits `NuGet.Config`.
- Automatic installation requires a fresh reviewed preview and explicit confirmation; disable/uninstall remove only addon-owned configuration.

[0.2.3]: https://github.com/ape1121/godot-csharp-profiler/releases/tag/v0.2.3
[0.2.2]: https://github.com/ape1121/godot-csharp-profiler/releases/tag/v0.2.2
[0.2.1]: https://github.com/ape1121/godot-csharp-profiler/releases/tag/v0.2.1
[0.2.0]: https://github.com/ape1121/godot-csharp-profiler/releases/tag/v0.2.0
[0.1.1]: https://github.com/ape1121/godot-csharp-profiler/releases/tag/v0.1.1
[0.1.0]: https://github.com/ape1121/godot-csharp-profiler/releases/tag/v0.1.0
