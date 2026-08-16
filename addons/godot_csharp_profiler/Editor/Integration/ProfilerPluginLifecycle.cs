#nullable enable
namespace Apeworks.GodotCSharpProfiler.Editor.Integration;

/// <summary>Idempotent lifecycle policy for Godot's reload-prone editor plugin callbacks.</summary>
public sealed class CoordinatorLifetimeAdapter : ICoordinatorLifetime
{
    private readonly Action requestDispose;
    public CoordinatorLifetimeAdapter(Action requestDispose) =>
        this.requestDispose = requestDispose ?? throw new ArgumentNullException(nameof(requestDispose));
    public void RequestDispose() => requestDispose();
}

public sealed class ProfilerPluginLifecycle
{
    private readonly IProfilerPluginHost host;
    private readonly ICoordinatorLifetime coordinator;
    private bool entered;
    private bool disposed;

    public ProfilerPluginLifecycle(IProfilerPluginHost host, ICoordinatorLifetime coordinator)
    {
        this.host = host ?? throw new ArgumentNullException(nameof(host));
        this.coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    public void Enter()
    {
        if (entered) return;
        host.RegisterDock();
        host.RegisterDebugger();
        entered = true;
    }

    public void Exit()
    {
        if (!entered) return;
        host.UnregisterDebugger();
        host.UnregisterDock();
        entered = false;
        if (disposed) return;
        coordinator.RequestDispose();
        disposed = true;
    }
}
