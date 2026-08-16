# Screenshot guidance

Capture screenshots only from the released archive in a disposable Godot 4.7 .NET project.

1. **Dock overview:** running demo, C# Profiler dock visible, frame graph and call tree readable.
2. **Mode selection:** Sampling/Automatic/Manual availability, overhead, startup-only interval message, and remediation text visible.
3. **Quality state:** a capture showing observed/dropped/overflowed/truncated counters.

Use the same neutral demo scene, 16:9 at 1440p or 1080p, 100% UI scale, and the default Godot theme. Crop empty desktop areas but do not composite results or hide warnings. Remove usernames, absolute paths, machine names, and unrelated plugins. Export lossless PNG or high-quality WebP; target less than 1 MiB per image. Add concise alt text and record the addon/Godot versions in the release PR. Do not claim a platform or editor run from CI screenshots unless that run actually happened.
