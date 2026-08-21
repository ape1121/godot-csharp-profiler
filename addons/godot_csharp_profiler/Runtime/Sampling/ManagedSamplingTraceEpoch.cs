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
        Exception? stopFailure = null;
        Task? acknowledged = null;
        try { acknowledged = _control.RequestStopAsync(); }
        catch (Exception exception)
        {
            stopFailure = exception;
            AbortStreamOnce();
        }

        if (acknowledged is not null)
        {
            if (await Task.WhenAny(acknowledged, Task.Delay(_gracePeriod)).ConfigureAwait(false) != acknowledged)
                AbortStreamOnce();

            try { await acknowledged.ConfigureAwait(false); }
            catch (Exception exception)
            {
                stopFailure = exception;
                AbortStreamOnce();
            }
        }

        if (await Task.WhenAny(_processingTask, Task.Delay(_gracePeriod)).ConfigureAwait(false) != _processingTask)
            AbortStreamOnce();

        var processingFaulted = false;
        try { await _processingTask.ConfigureAwait(false); }
        catch { processingFaulted = true; }
        DisposeControlOnce();
        if (stopFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopFailure).Throw();
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

/// <summary>
/// Transactional owner for a native EventPipe acquisition. Ownership is reported to the session
/// immediately after StartEventPipeSession succeeds, before TraceEvent can perform fallible setup.
/// A failed transaction receives the same one-shot StopTracing discipline as a published epoch.
/// </summary>
internal sealed class ManagedSamplingTraceAcquisition
{
    private readonly object _gate = new();
    private readonly TimeSpan _gracePeriod;
    private EventPipeSession? _session;
    private TraceLogEventSource? _source;
    private ManagedSamplingTraceEpoch? _epoch;
    private Task? _cleanupTask;
    private int _unaccountedNativeActivity;
    private int _sessionDisposed;
    private int _published;

    private ManagedSamplingTraceAcquisition(EventPipeSession session, TimeSpan gracePeriod)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        if (gracePeriod <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(gracePeriod));
        _gracePeriod = gracePeriod;
    }

    private ManagedSamplingTraceAcquisition(TimeSpan gracePeriod)
    {
        if (gracePeriod <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(gracePeriod));
        _gracePeriod = gracePeriod;
    }

    private ManagedSamplingTraceAcquisition(ManagedSamplingTraceEpoch epoch)
    {
        _epoch = epoch ?? throw new ArgumentNullException(nameof(epoch));
        _gracePeriod = TimeSpan.FromSeconds(1);
    }

    internal static ManagedSamplingTraceAcquisition FromEventPipeSession(
        EventPipeSession session,
        TimeSpan gracePeriod) => new(session, gracePeriod);

    internal static ManagedSamplingTraceAcquisition BeginUnknown(TimeSpan gracePeriod) =>
        new(gracePeriod);

    internal static ManagedSamplingTraceAcquisition FromEpoch(
        ManagedSamplingTraceEpoch epoch) => new(epoch);

    internal void MarkAdditionalNativeActivityUnaccounted()
    {
        lock (_gate) _unaccountedNativeActivity++;
    }

    internal void MarkAdditionalNativeActivityAccountedFor()
    {
        lock (_gate)
        {
            if (_unaccountedNativeActivity <= 0)
                throw new InvalidOperationException("No unaccounted native activity was pending.");
            _unaccountedNativeActivity--;
        }
    }

    internal void AttachSession(EventPipeSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (_gate)
        {
            if (_session is not null || _epoch is not null || Volatile.Read(ref _published) != 0)
                throw new InvalidOperationException("The EventPipe acquisition already owns a session or epoch.");
            _session = session;
        }
    }

    internal void AttachSource(TraceLogEventSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            if (_source is not null || _epoch is not null || Volatile.Read(ref _published) != 0)
                throw new InvalidOperationException("The EventPipe acquisition already owns a source or epoch.");
            _source = source;
        }
    }

    internal ManagedSamplingTraceEpoch CreateEpoch()
    {
        lock (_gate)
        {
            if (_session is null || _source is null || _epoch is not null ||
                Volatile.Read(ref _published) != 0)
                throw new InvalidOperationException("The EventPipe acquisition is not ready for publication.");
            var control = new EventPipeSamplingTraceEpochControl(_session, _source);
            _epoch = new ManagedSamplingTraceEpoch(control, _gracePeriod);
            _source = null; // The epoch control now owns it.
            return _epoch;
        }
    }

    internal void Publish(ManagedSamplingTraceEpoch epoch)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_epoch, epoch))
                throw new InvalidOperationException("The published epoch does not belong to this acquisition.");
            if (Interlocked.Exchange(ref _published, 1) != 0)
                throw new InvalidOperationException("The EventPipe acquisition was already published.");
        }
    }

    internal Task CleanupAsync()
    {
        lock (_gate)
            return _cleanupTask ??= CleanupCoreAsync();
    }

    private async Task CleanupCoreAsync()
    {
        ManagedSamplingTraceEpoch? epoch;
        TraceLogEventSource? source;
        lock (_gate)
        {
            if (Volatile.Read(ref _published) != 0)
                throw new InvalidOperationException("A published EventPipe acquisition must be stopped by its session epoch.");
            epoch = _epoch;
            source = _source;
        }

        if (epoch is not null)
            await epoch.StopAsync().ConfigureAwait(false);
        else
            await CleanupRawSessionAsync(source).ConfigureAwait(false);

        int unaccountedNativeActivity;
        lock (_gate) unaccountedNativeActivity = _unaccountedNativeActivity;
        if (unaccountedNativeActivity != 0)
            throw new InvalidOperationException(
                "EventPipe construction failed while native activity was unaccounted for; " +
                "process sampling ownership remains quarantined.");
    }

    private async Task CleanupRawSessionAsync(TraceLogEventSource? source)
    {
        if (_session is null)
            return;

        Exception? stopFailure = null;
        Task? acknowledged = null;
        try { acknowledged = _session.StopAsync(CancellationToken.None); }
        catch (Exception exception)
        {
            stopFailure = exception;
            DisposeSessionOnce();
        }

        if (acknowledged is not null)
        {
            if (await Task.WhenAny(acknowledged, Task.Delay(_gracePeriod)).ConfigureAwait(false) != acknowledged)
                DisposeSessionOnce();
            try { await acknowledged.ConfigureAwait(false); }
            catch (Exception exception)
            {
                stopFailure = exception;
                DisposeSessionOnce();
            }
        }

        try { source?.Dispose(); }
        catch (Exception exception) { stopFailure ??= exception; }
        DisposeSessionOnce();
        if (stopFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(stopFailure).Throw();
    }

    private void DisposeSessionOnce()
    {
        if (Interlocked.Exchange(ref _sessionDisposed, 1) == 0)
            _session?.Dispose();
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
