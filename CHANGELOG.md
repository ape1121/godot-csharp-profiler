# Changelog

All notable changes use [Keep a Changelog](https://keepachangelog.com/) conventions.

## [Unreleased]

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

[Unreleased]: https://github.com/apeworks/godot-csharp-profiler/commits/main
