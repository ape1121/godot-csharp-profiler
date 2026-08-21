#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.EventPipe;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace Apeworks.GodotCSharpProfiler.Runtime.Sampling;

public enum ManagedSamplingSessionState
{
    Starting,
    Running,
    Stopping,
    Stopped,
    Faulted
}

public sealed class ManagedSamplingSession : IAsyncDisposable
{
    private static readonly Guid SampleProfilerProviderGuid =
        new("3c530d44-97ae-513a-1e6d-783e8f8e03a9");
    private static ManagedSamplingSession? s_activeSession;

    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(1);
    private readonly SamplingOptions _options;
    private readonly SamplingAggregator _aggregator;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _stopGate = new();
    private readonly Func<ManagedSamplingTraceEpoch> _epochFactory;
    private readonly CancellationTokenSource _rotationCancellation = new();
    private ManagedSamplingTraceEpoch? _currentEpoch;
    private IDisposable? _processLease;
    private CancellationTokenRegistration _cancellationRegistration;
    private Task? _stopTask;
    private int _state = (int)ManagedSamplingSessionState.Stopped;
    private int _traceEpochCount;
    private int _streamAborted;
    private bool _hasStarted;
    private Exception? _fault;

    public ManagedSamplingSession(SamplingOptions options)
        : this(options, null) { }

    internal ManagedSamplingSession(
        SamplingOptions options,
        Func<ManagedSamplingTraceEpoch>? epochFactory)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).ValidateAndCopy();
        _aggregator = new SamplingAggregator(_options);
        _epochFactory = epochFactory ?? CreateProductionTraceEpoch;
    }

    public static SamplingCapabilities Capabilities { get; } = SamplingCapabilities.Detect();

    public ManagedSamplingSessionState State =>
        (ManagedSamplingSessionState)Volatile.Read(ref _state);

    /// <summary>The failure that caused <see cref="ManagedSamplingSessionState.Faulted"/>.</summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>
    /// Number of bounded-lifetime in-memory TraceLog epochs created by this session.
    /// Exposed so hosts can monitor retention renewal.
    /// </summary>
    public int TraceEpochCount => Volatile.Read(ref _traceEpochCount);

    /// <summary>True when teardown required stream abort or parser processing faulted.</summary>
    public bool StopDataIncomplete => Volatile.Read(ref _streamAborted) != 0;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_hasStarted)
                throw new InvalidOperationException("A managed sampling session instance can only be started once.");
            Volatile.Write(ref _state, (int)ManagedSamplingSessionState.Starting);

            if (Interlocked.CompareExchange(ref s_activeSession, this, null) is not null)
                throw new InvalidOperationException("Another managed sampling session is already active.");
            _processLease = ManagedSamplingProcessLease.TryAcquire();
            if (_processLease is null)
            {
                ReleaseGlobalOwnership();
                throw new InvalidOperationException("Another managed sampling session is already active in this process.");
            }
            _hasStarted = true;

            try
            {
                _currentEpoch = _epochFactory();
                Interlocked.Increment(ref _traceEpochCount);
                Volatile.Write(ref _state, (int)ManagedSamplingSessionState.Running);
                ScheduleRotation(_currentEpoch);
                _cancellationRegistration = cancellationToken.Register(
                    static state => _ = ((ManagedSamplingSession)state!).StopAsync(), this);
            }
            catch (Exception exception)
            {
                _currentEpoch?.Dispose();
                _currentEpoch = null;
                ReleaseGlobalOwnership();
                var wrapped = CreateStartFailure(exception);
                Volatile.Write(ref _fault, wrapped);
                Volatile.Write(ref _state, (int)ManagedSamplingSessionState.Faulted);
                throw wrapped;
            }
        }
        catch
        {
            if (State == ManagedSamplingSessionState.Starting)
                Volatile.Write(ref _state, (int)ManagedSamplingSessionState.Faulted);
            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public Task StopAsync()
    {
        lock (_stopGate)
        {
            if (State == ManagedSamplingSessionState.Stopped) return Task.CompletedTask;
            return _stopTask ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        ManagedSamplingTraceEpoch? epoch;
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == ManagedSamplingSessionState.Stopped) return;
            Volatile.Write(ref _state, (int)ManagedSamplingSessionState.Stopping);
            _rotationCancellation.Cancel();
            _cancellationRegistration.Dispose();
            epoch = _currentEpoch;
        }
        finally
        {
            _lifecycle.Release();
        }

        try
        {
            if (epoch is not null)
            {
                var result = await epoch.StopAsync().ConfigureAwait(false);
                if (result.DataIncomplete) Interlocked.Exchange(ref _streamAborted, 1);
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _fault, exception);
            Volatile.Write(ref _state, (int)ManagedSamplingSessionState.Faulted);
            throw;
        }

        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_currentEpoch, epoch)) _currentEpoch = null;
            ReleaseGlobalOwnership();
            Volatile.Write(ref _state, (int)ManagedSamplingSessionState.Stopped);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public SamplingSnapshot GetSnapshot(bool reset = true) => _aggregator.GetSnapshot(reset);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    private void ScheduleRotation(ManagedSamplingTraceEpoch epoch) =>
        _ = RotateAfterDelayAsync(epoch);

    private async Task RotateAfterDelayAsync(ManagedSamplingTraceEpoch epoch)
    {
        try
        {
            await Task.Delay(_options.TraceRetentionDuration, _rotationCancellation.Token)
                .ConfigureAwait(false);
            await RotateTraceEpochAsync(epoch).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_rotationCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Volatile.Write(ref _fault, exception);
            Volatile.Write(ref _state, (int)ManagedSamplingSessionState.Faulted);
        }
    }

    internal async Task RotateTraceEpochAsync(ManagedSamplingTraceEpoch epoch)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != ManagedSamplingSessionState.Running ||
                !ReferenceEquals(_currentEpoch, epoch)) return;
        }
        finally
        {
            _lifecycle.Release();
        }

        var result = await epoch.StopAsync().ConfigureAwait(false);
        if (result.DataIncomplete) Interlocked.Exchange(ref _streamAborted, 1);

        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State != ManagedSamplingSessionState.Running ||
                !ReferenceEquals(_currentEpoch, epoch)) return;
            _currentEpoch = _epochFactory();
            Interlocked.Increment(ref _traceEpochCount);
            ScheduleRotation(_currentEpoch);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    internal ManagedSamplingTraceEpoch? CurrentEpochForTests => _currentEpoch;

    private ManagedSamplingTraceEpoch CreateProductionTraceEpoch()
    {
        var providers = new List<EventPipeProvider>
        {
            new(
                "Microsoft-Windows-DotNETRuntime",
                EventLevel.Verbose,
                (long)(ClrTraceEventParser.Keywords.Jit | ClrTraceEventParser.Keywords.Loader)),
            new("Microsoft-DotNETCore-SampleProfiler", EventLevel.Informational)
        };
        var client = new DiagnosticsClient(Environment.ProcessId);
        var session = client.StartEventPipeSession(
            providers, requestRundown: false, _options.CircularBufferSizeMegabytes);
        try
        {
            var source = Microsoft.Diagnostics.Tracing.Etlx.TraceLog.CreateFromEventPipeSession(
                session,
                Microsoft.Diagnostics.Tracing.Etlx.TraceLog.EventPipeRundownConfiguration.Enable(client));
            source.AllEvents += OnAnyEvent;
            return new ManagedSamplingTraceEpoch(
                new EventPipeSamplingTraceEpochControl(session, source), StopGracePeriod);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private void OnAnyEvent(TraceEvent traceEvent)
    {
        if (traceEvent.ProviderGuid == SampleProfilerProviderGuid)
            OnThreadSample(traceEvent);
    }

    private void OnThreadSample(TraceEvent traceEvent)
    {
        var callStack = Microsoft.Diagnostics.Tracing.Etlx.TraceLogExtensions.CallStack(traceEvent);
        if (callStack is null)
            return;

        var frames = new List<SamplingFrame>();
        for (var frame = callStack; frame is not null; frame = frame.Caller)
        {
            var codeAddress = frame.CodeAddress;
            if (codeAddress is null)
                continue;
            var methodName = codeAddress.FullMethodName;
            if (string.IsNullOrEmpty(methodName))
                continue;
            var moduleName = codeAddress.ModuleFile?.Name;
            var assemblyName = string.IsNullOrEmpty(moduleName)
                ? "(unknown assembly)"
                : Path.GetFileNameWithoutExtension(moduleName);
            frames.Add(new SamplingFrame(assemblyName, methodName));
        }
        if (frames.Count > 0)
        {
            var thread = Microsoft.Diagnostics.Tracing.Etlx.TraceLogExtensions.Thread(traceEvent);
            _aggregator.AddSample(thread?.ThreadInfo, frames);
        }
    }

    private static Exception CreateStartFailure(Exception exception)
    {
        if (exception is ServerNotAvailableException ||
            exception.Message.Contains("diagnostic", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("connect", StringComparison.OrdinalIgnoreCase))
        {
            return new InvalidOperationException(
                "Unable to start managed sampling. .NET diagnostics are unavailable or disabled " +
                "(for example, DOTNET_EnableDiagnostics=0).", exception);
        }
        return new InvalidOperationException("Unable to start the managed EventPipe sampling session.", exception);
    }

    private void ReleaseGlobalOwnership()
    {
        Interlocked.CompareExchange(ref s_activeSession, null, this);
        Interlocked.Exchange(ref _processLease, null)?.Dispose();
    }
}
