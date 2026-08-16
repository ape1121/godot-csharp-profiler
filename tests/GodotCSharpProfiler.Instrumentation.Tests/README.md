# Instrumentation test artifact

`dotnet pack ../../../src/GodotCSharpProfiler.Fody -o artifacts` produces the build-only weaver package. Consumers reference it with `PrivateAssets="all"`; these tests intentionally do not install it into the root Godot project.
