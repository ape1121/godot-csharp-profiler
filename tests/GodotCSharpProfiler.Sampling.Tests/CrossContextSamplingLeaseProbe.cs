using Apeworks.GodotCSharpProfiler.Runtime.Sampling;

namespace GodotCSharpProfiler.Sampling.Tests;

public static class CrossContextSamplingLeaseProbe
{
    private static IDisposable? s_lease;

    public static bool TryAcquire()
    {
        s_lease ??= ManagedSamplingProcessLease.TryAcquire();
        return s_lease is not null;
    }

    public static bool Release()
    {
        s_lease?.Dispose();
        s_lease = null;
        return true;
    }
}
