# Godot C# Profiler

A managed-code profiler integrated into the Godot editor for Godot 4 .NET projects.

> **Status:** Active extraction and hardening. This repository is not yet released to the Asset Library.

The finished addon will offer three explicit capture modes:

- **Sampling** — zero-source-change statistical managed CPU sampling.
- **Automatic Instrumentation** — opt-in, filtered exact method timings through reversible build integration.
- **Manual Scopes** — optional exact semantic timing with a stable runtime API.

The editor UI will expose mode availability, overhead expectations, filters, capture ownership, dropped/truncated data, a frame graph, call trees, and copy/export actions.

## Release bar

The first public release will not require a custom Godot engine build. It must pass clean install, enable, capture, disable, uninstall, rebuild, and re-enable tests in a fresh Godot 4.7 .NET project. Runtime buffers and protocol payloads must remain bounded, malformed input must fail closed, and inactive overhead must be measured.

See [`docs/implementation-plan.md`](docs/implementation-plan.md) for the executable roadmap.

## License

MIT © 2026 Apeworks.
