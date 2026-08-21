#nullable enable
using System;
using System.Threading;

namespace Apeworks.GodotCSharpProfiler.Runtime.Sampling;

/// <summary>
/// Process-lifetime exclusion for self-EventPipe capture. AppDomain data contains only a
/// corelib SemaphoreSlim, so the guard is shared across collectible managed rebuild contexts
/// without rooting any profiler assembly. A quarantined unknown capture intentionally retains it.
/// </summary>
internal static class ManagedSamplingProcessLease
{
    private const string SemaphoreKey =
        "Apeworks.GodotCSharpProfiler.ManagedSamplingProcessLease.v1";

    internal static IDisposable? TryAcquire()
    {
        var semaphore = GetSemaphore();
        return semaphore.Wait(0) ? new Lease(semaphore) : null;
    }

    private static SemaphoreSlim GetSemaphore()
    {
        var domain = AppDomain.CurrentDomain;
        lock (domain)
        {
            if (domain.GetData(SemaphoreKey) is SemaphoreSlim existing) return existing;
            var created = new SemaphoreSlim(1, 1);
            domain.SetData(SemaphoreKey, created);
            return created;
        }
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private SemaphoreSlim? _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();
    }
}
