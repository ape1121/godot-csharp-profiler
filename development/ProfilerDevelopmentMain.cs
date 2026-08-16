using Apeworks.GodotCSharpProfiler;
using Godot;

public partial class ProfilerDevelopmentMain : Node
{
    private long frames;

    public override void _Process(double delta)
    {
        _ = delta;
        using var scope = CsProfiler.Scope("Development.Process");
        frames++;
        var value = 0.0;
        for (var index = 1; index < 20_000; index++)
            value += Mathf.Sqrt(index);
        if (frames % 300 == 0)
            GD.Print($"Profiler development workload active: {value:F1}");
    }
}
