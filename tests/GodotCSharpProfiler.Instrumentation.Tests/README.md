# Instrumentation weaver contract and test artifact

The installer-facing configuration contract is one `GodotCSharpProfiler` element inside `FodyWeavers.xml`. It owns all automatic-instrumentation configuration; no external JSON configuration is read.

```xml
<FodyWeavers>
  <GodotCSharpProfiler
      MaximumMethods="16384"
      MaximumLabelLength="512"
      ProjectRoot="/canonical/project/root">
    <Rule Action="include" Target="namespace" Pattern="MyGame.**" />
    <Rule Action="exclude" Target="type" Pattern="MyGame.Editor.**" />
  </GodotCSharpProfiler>
</FodyWeavers>
```

Rules are evaluated in XML order. `Action` is `include` or `exclude`; `Target` is `all`, `namespace`, `type`, or `method`. The optional `ConfigHash` attribute is reserved for an installer to embed the normalized configuration identity; the weaver computes the authoritative hash from normalized effective settings and ordered rules.

The weaver rejects unknown fields, invalid limits, targets, actions, and globs. Runtime bounds are `MaximumMethods <= 16384` and UTF-8 `MaximumLabelLength <= 512`. Safety/source exclusions cannot be overridden by rules.

`dotnet pack ../../../src/GodotCSharpProfiler.Fody -o artifacts` produces the build-only `0.1.0-dev` package. Consumers reference it with `PrivateAssets="all"`; these tests intentionally do not install it into the root Godot project.
