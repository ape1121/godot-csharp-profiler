using Apeworks.GodotCSharpProfiler;
using Godot;

public partial class Main : Node2D
{
    private Polygon2D _pulse = null!;
    private double _time;

    public override void _Ready() => _pulse = GetNode<Polygon2D>("Pulse");

    public override void _Process(double delta)
    {
        using var frame = CsProfiler.Scope("Demo.Process");
        _time += delta;
        _pulse.Position = new Vector2(480 + Mathf.Sin((float)_time * 1.7f) * 280, 300);
        HotMethod();
    }

    private static double HotMethod()
    {
        using var scope = CsProfiler.Fn();
        var value = 0.0;
        for (var i = 1; i < 40_000; i++)
            value += Mathf.Sqrt(i) * Mathf.Sin(i * 0.001f);
        return value;
    }
}
