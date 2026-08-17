# Godot Asset Library submission draft

## Metadata

- **Title:** Godot C# Profiler
- **Category:** Tools
- **Godot version:** 4.7
- **License:** MIT
- **Support level:** Community
- **Repository:** `https://github.com/ape1121/godot-csharp-profiler`
- **Download commit:** use the immutable commit of the matching `main` addon release
- **Icon URL:** `https://raw.githubusercontent.com/ape1121/godot-csharp-profiler/main/icon.png`
- **Summary:** Managed CPU profiling for Godot .NET with statistical sampling, opt-in exact build instrumentation, and exact manual scopes.

## Description draft

Profile managed C# work inside Godot 4.7 .NET projects without a custom engine build. EventPipe sampling identifies statistical hotspots; optional Fody weaving and manual scopes record observed wall-clock spans and calls. Captures remain local and the addon has no telemetry.

Sampling and automatic instrumentation require the included dependency setup. Automatic mode changes the project build and requires a rebuild and process restart. See the packaged README for limitations and clean removal.

## Submission checklist

- [x] Canonical repository and direct 256×256 PNG icon URLs are recorded.
- [ ] Release version equals `plugin.cfg`, dependency manifest, Fody package, tag, and archive name.
- [ ] Run `pwsh scripts/build-release.ps1 -Version X.Y.Z` from a clean checkout.
- [ ] Linux and Windows Actions are green; do not state editor validation unless a workflow actually launches Godot.
- [ ] Packaging tests pass against the exact archive.
- [ ] SHA-256 and byte size match `artifacts/release/SHA256SUMS` and release attachment.
- [ ] Archive extracts as `addons/godot_csharp_profiler/...`, with no extra top directory.
- [ ] Fresh Godot 4.7 .NET install: run setup, build, enable plugin, run demo capture, disable, remove setup, delete addon, rebuild.
- [ ] Sampling tested on every platform claimed in the matrix; unsupported platforms remain marked experimental/unavailable.
- [ ] Automatic install/rebuild/restart/removal tested with a project under source control.
- [ ] Screenshots are current, show the dock and mode/quality indicators, contain no private project names/paths, and are PNG/WebP under 1 MiB each.
- [ ] Icon renders legibly at 16, 32, 64, and 128 px and is original work.
- [ ] README includes privacy, compatibility, limitations, license, and uninstall instructions.
- [ ] Release notes link the changelog and disclose dependency versions.
- [ ] No `bin`, `obj`, `.godot`, tests, spikes, internal docs, or credentials occur in the ZIP.
