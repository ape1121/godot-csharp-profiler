using System;

namespace GodotCSharpProfiler.Fody;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = false)]
internal sealed class GodotCSharpProfilerInstrumentedAttribute : Attribute
{
    public GodotCSharpProfilerInstrumentedAttribute(string configHash) => ConfigHash = configHash;
    public string ConfigHash { get; }
}
