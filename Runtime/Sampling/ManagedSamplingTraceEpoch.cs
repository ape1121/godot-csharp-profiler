#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace Apeworks.GodotCSharpProfiler.Runtime.Sampling;

internal interface IManagedSamplingTraceEpochControl : IDisposable
{
    Task ProcessAsync();
    Task RequestStopAsync();
    void AbortStream();
}

internal readonly record struct ManagedSamplingTraceEpochStopResult(
    bool StreamAborted,
    bool ProcessingFaulted)
{
    internal bool DataIncomplete => StreamAborted || ProcessingFaulted;
}

internal sealed class ManagedSamplingTraceEpoch : IDisposable
{
    private readonly IManagedSamplingTraceEpochControl _control;
    private readonly TimeSpan _gracePeriod;
    private readonly Task _processingTask;
    private readonly object _stopGate = new();
    private Task<ManagedSamplingTraceEpochStopResult>? _stopTask;
    private int _streamAborted;
    private int _disposed;

    internal ManagedSamplingTraceEpoch(
        IManagedSamplingTraceEpochControl control,
        TimeSpan gracePeriod)
    {
        _control = control ?? throw new ArgumentNullException(nameof(control));
        if (gracePeriod <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(gracePeriod));
        _gracePeriod = gracePeriod;
        _processingTask = _control.ProcessAsync();
    }

    internal Task ProcessingTask => _processingTask;

    internal Task<ManagedSamplingTraceEpochStopResult> StopAsync()
    {
        lock (_stopGate)
            return _stopTask ??= StopCoreAsync();
    }

    private async Task<ManagedSamplingTraceEpochStopResult> StopCoreAsync()
    {
        Task acknowledged;
        try { acknowledged = _control.RequestStopAsync(); }
        catch
        {
            AbortStreamOnce();
            throw;
        }

        if (await Task.WhenAny(acknowledged, Task.Delay(_gracePeriod)).ConfigureAwait(false) != acknowledged)
            AbortStreamOnce();

        try { await acknowledged.ConfigureAwait(false); }
        catch
        {
            AbortStreamOnce();
            throw;
        }

        if (await Task.WhenAny(_processingTask, Task.Delay(_gracePeriod)).ConfigureAwait(false) != _processingTask)
            AbortStreamOnce();

        var processingFaulted = false;
        try { await _processingTask.ConfigureAwait(false); }
        catch { processingFaulted = true; }
        DisposeControlOnce();
        return new ManagedSamplingTraceEpochStopResult(
            Volatile.Read(ref _streamAborted) != 0,
            processingFaulted);
    }

    private void AbortStreamOnce()
    {
        if (Interlocked.Exchange(ref _streamAborted, 1) == 0)
            _control.AbortStream();
    }

    private void DisposeControlOnce()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _control.Dispose();
    }

    public void Dispose()
    {
        AbortStreamOnce();
        DisposeControlOnce();
    }
}

internal sealed class EventPipeSamplingTraceEpochControl : IManagedSamplingTraceEpochControl
{
    private readonly EventPipeSession _session;
    private readonly TraceLogEventSource _source;
    private readonly Task _processingTask;

    internal EventPipeSamplingTraceEpochControl(EventPipeSession session, TraceLogEventSource source)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _processingTask = Task.Run(() => _source.Process());
    }

    public Task ProcessAsync() => _processingTask;
    public Task RequestStopAsync() => _session.StopAsync(CancellationToken.None);
    public void AbortStream() => _session.Dispose();

    public void Dispose()
    {
        _source.Dispose();
        _session.Dispose();
    }
}
