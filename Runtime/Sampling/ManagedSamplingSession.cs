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

    private readonly SamplingOptions _options;
    private readonly SamplingAggregator _aggregator;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private EventPipeSession? _eventPipeSession;
    private Microsoft.Diagnostics.Tracing.Etlx.TraceLogEventSource? _eventSource;
    private Task? _processingTask;
    private CancellationTokenRegistration _cancellationRegistration;
    private int _state = (int)ManagedSamplingSessionState.Stopped;
    private bool _hasStarted;
    private Exception? _fault;

    public ManagedSamplingSession(SamplingOptions options)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).ValidateAndCopy();
        _aggregator = new SamplingAggregator(_options);
    }

    public ManagedSamplingSessionState State =>
        (ManagedSamplingSessionState)Volatile.Read(ref _state);

    /// <summary>The failure that caused <see cref="ManagedSamplingSessionState.Faulted"/>.</summary>
    public Exception? Fault => Volatile.Read(ref _fault);

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
            _hasStarted = true;

            try
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
                _eventPipeSession = client.StartEventPipeSession(
                    providers, requestRundown: false, _options.CircularBufferSizeMegabytes);
                _eventSource = Microsoft.Diagnostics.Tracing.Etlx.TraceLog.CreateFromEventPipeSession(
                    _eventPipeSession,
                    Microsoft.Diagnostics.Tracing.Etlx.TraceLog.EventPipeRundownConfiguration.Enable(client));
                _eventSource.AllEvents += OnAnyEvent;
                _processingTask = Task.Run(ProcessEvents);
                _cancellationRegistration = cancellationToken.Register(
                    static state => _ = ((ManagedSamplingSession)state!).StopAsync(), this);
                Volatile.Write(ref _state, (int)ManagedSamplingSessionState.Running);
            }
            catch (Exception exception)
            {
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

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = State;
            if (state is ManagedSamplingSessionState.Stopped or ManagedSamplingSessionState.Stopping)
                return;
            if (state == ManagedSamplingSessionState.Faulted && _eventPipeSession is null)
            {
                ReleaseGlobalOwnership();
                return;
            }

            Volatile.Write(ref _state, (int)ManagedSamplingSessionState.Stopping);
            _cancellationRegistration.Dispose();
            try
            {
                _eventPipeSession?.Stop();
            }
            catch (ServerNotAvailableException)
            {
                // The runtime may have already closed the stream during process shutdown.
            }

            if (_processingTask is not null)
                await _processingTask.ConfigureAwait(false);

            _eventSource?.Dispose();
            _eventPipeSession?.Dispose();
            _eventSource = null;
            _eventPipeSession = null;
            _processingTask = null;
            ReleaseGlobalOwnership();
            if (State != ManagedSamplingSessionState.Faulted)
                Volatile.Write(ref _state, (int)ManagedSamplingSessionState.Stopped);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _fault, exception);
            Volatile.Write(ref _state, (int)ManagedSamplingSessionState.Faulted);
            ReleaseGlobalOwnership();
            throw;
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

    private void ProcessEvents()
    {
        try
        {
            _eventSource!.Process();
        }
        catch (Exception exception) when (State is ManagedSamplingSessionState.Stopping or ManagedSamplingSessionState.Stopped)
        {
            // Stopping the EventPipe session terminates the processing stream.
            _ = exception;
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _fault, exception);
            Volatile.Write(ref _state, (int)ManagedSamplingSessionState.Faulted);
            ReleaseGlobalOwnership();
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

    private void ReleaseGlobalOwnership() =>
        Interlocked.CompareExchange(ref s_activeSession, null, this);
}
