#if TOOLS
using Apeworks.GodotCSharpProfiler;
using Apeworks.GodotCSharpProfiler.Editor.Integration;
using Apeworks.GodotCSharpProfiler.Editor.Modes;
using Apeworks.GodotCSharpProfiler.Protocol;
using Apeworks.GodotCSharpProfiler.Runtime.Protocol.Adapters;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

public partial class CsProfilerDebuggerPlugin : EditorDebuggerPlugin
{
    private const string ReloadSessionIdsMeta = "_godot_csharp_profiler_reload_session_ids";
    private const int MaximumRememberedSessions = 64;
    private CsProfilerPanel _panel;
    private HashSet<int> _sessionIds = new();
    private HashSet<int> _activeSessionIds = new();
    private Dictionary<int, EditorCaptureCoordinator> _protocol = new();
    private Dictionary<int, CsProfilerRuntimeIdentity> _identities = new();
    private Dictionary<(int Session, long Generation, long Sequence), BatchFlushFrame> _batchFrames = new();
    private CsProfilerSessionRouterState _router = new();
    private PendingCaptureRequest _pendingCapture = new();
    private Dictionary<int, ModeConfiguration> _startIntent = new();
    private ModeConfiguration _unboundStartIntent;
    private Dictionary<int, OrphanResetAttempt> _resetAttempts = new();
    private double _nextOwnedDiscoveryAtSeconds;

    private sealed record OrphanResetAttempt(string RequestId, int Attempts, double NextRetryAtSeconds);

    public void Initialize(CsProfilerPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        EnsureManagedState();
        var samePanel = ReferenceEquals(_panel, panel);
        if (_panel != null && !samePanel) Teardown();
        _panel = panel;
        _panel.SessionActiveQuery = AnySessionActive;
        _panel.ProfilingToggled -= SendControlMessage;
        _panel.ProfilingToggled += SendControlMessage;
        _panel.DiscoveryRequested -= SendDiscoveryMessages;
        _panel.DiscoveryRequested += SendDiscoveryMessages;
        _panel.InstanceSelected -= OnInstanceSelected;
        _panel.InstanceSelected += OnInstanceSelected;
        // Field initializers may run when Godot reconstructs the managed debugger object, leaving
        // an empty (non-null) set. Restore retained native session IDs once per panel rebind too.
        RestoreSessionIds();
        RepublishCurrentState();
    }

    private void RepublishCurrentState()
    {
        PublishInstances();
        var sessionId = _router.SelectedSessionId;
        if (sessionId < 0 || !_identities.TryGetValue(sessionId, out var identity)) return;
        _panel?.OnBridgeReady(identity);
        if (!_protocol.TryGetValue(sessionId, out var endpoint)) return;
        _panel?.ApplyProtocolSnapshot(endpoint.Snapshot, identity);
        _panel?.ApplyProtocolResults(endpoint.CompletedResults);
        _panel?.ApplyProtocolTimeline(endpoint.Timeline);
    }

    public override void _SetupSession(int sessionId)
    {
        EnsureManagedState();
        _sessionIds.Add(sessionId);
        RememberSessionIds();
        _nextOwnedDiscoveryAtSeconds = 0;
        _panel?.InitializeSessionState(AnySessionActive());
    }

    internal void QueueStartAfterManagedReload(ModeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        EnsureManagedState();
        RestoreSessionIds();
        var normalized = configuration.Normalize();
        _unboundStartIntent = normalized;
        ApplyRouteChange(_router.Reconcile(IsSessionActive));
        var sessionId = _router.SelectedSessionId;
        if (sessionId < 0)
        {
            SendDiscoveryMessages();
            return;
        }
        _startIntent[sessionId] = normalized;
        _unboundStartIntent = null;
        DriveSelectedRecovery(sessionId);
    }

    public void PollActiveSessions()
    {
        EnsureManagedState();
        if (_panel == null) return;
        var active = _sessionIds.Where(IsSessionActive).ToHashSet();
        foreach (var stopped in _activeSessionIds.Except(active).ToArray())
        {
            _router.Forget(stopped);
            if (_protocol.Remove(stopped, out var endpoint)) endpoint.Disconnect();
            _identities.Remove(stopped);
            _startIntent.Remove(stopped);
            _resetAttempts.Remove(stopped);
            foreach (var key in _batchFrames.Keys.Where(key => key.Session == stopped).ToArray()) _batchFrames.Remove(key);
        }
        _activeSessionIds.Clear();
        _activeSessionIds.UnionWith(active);
        ApplyRouteChange(_router.Reconcile(active.Contains));
        _panel.InitializeSessionState(active.Count > 0);
        PublishInstances();
        var now = Time.GetTicksMsec() / 1000.0;
        RetrySelectedReset(now);
        if (active.Count == 0 || _router.SelectedSessionId >= 0 || now < _nextOwnedDiscoveryAtSeconds) return;
        _nextOwnedDiscoveryAtSeconds = now + 1;
        SendDiscoveryMessages();
    }

    public void Teardown()
    {
        EnsureManagedState();
        _pendingCapture.Cancel();
        StopSelectedOwner();
        if (_panel != null)
        {
            _panel.ProfilingToggled -= SendControlMessage;
            _panel.DiscoveryRequested -= SendDiscoveryMessages;
            _panel.InstanceSelected -= OnInstanceSelected;
            _panel.SessionActiveQuery = null;
        }
        foreach (var endpoint in _protocol.Values) endpoint.Disconnect();
        _sessionIds.Clear();
        _activeSessionIds.Clear();
        _protocol.Clear();
        _identities.Clear();
        _startIntent.Clear();
        _resetAttempts.Clear();
        _batchFrames.Clear();
        _router.Clear();
        RememberSessionIds();
        _panel = null;
    }

    private bool AnySessionActive()
    {
        ApplyRouteChange(_router.Reconcile(IsSessionActive));
        return _sessionIds.Any(IsSessionActive);
    }

    private bool IsSessionActive(int sessionId)
    {
        try { return TryResolveSession(sessionId, out var session) && session.IsActive(); }
        catch (ObjectDisposedException) { return false; }
    }

    private bool TryResolveSession(int sessionId, out EditorDebuggerSession session)
    {
        session = null;
        if (!_sessionIds.Contains(sessionId)) return false;
        try { session = GetSession(sessionId); return session != null && IsInstanceValid(session); }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException or
                                      ArgumentOutOfRangeException or IndexOutOfRangeException) { return false; }
    }

    private void SendControlMessage(bool start)
    {
        ApplyRouteChange(_router.Reconcile(IsSessionActive));
        var sessionId = _router.SelectedSessionId;
        if (sessionId < 0)
        {
            _unboundStartIntent = start ? _panel.ConfigurationForProtocol.Normalize() : null;
            if (start) SendDiscoveryMessages();
            return;
        }
        if (start)
            _startIntent[sessionId] = _panel.ConfigurationForProtocol.Normalize();
        else
        {
            _startIntent.Remove(sessionId);
            _pendingCapture.Cancel();
            if (_protocol.TryGetValue(sessionId, out var endpoint) && endpoint.Stop())
                return;
        }
        DriveSelectedRecovery(sessionId);
    }

    private void StopSelectedOwner()
    {
        if (_router.SelectedSessionId >= 0 && IsSessionActive(_router.SelectedSessionId) &&
            _protocol.TryGetValue(_router.SelectedSessionId, out var endpoint)) endpoint.Stop();
    }

    private void SendDiscoveryMessages()
    {
        foreach (var sessionId in _sessionIds.Where(IsSessionActive).OrderBy(id => id))
            SendSessionMessage(sessionId, CsProfilerBridge.MessagePrefix + ":discover", new Godot.Collections.Array());
    }

    private void OnInstanceSelected(int sessionId)
    {
        _router.Prefer(sessionId);
        ApplyRouteChange(_router.Reconcile(IsSessionActive));
        PublishInstances();
    }

    private void PublishInstances()
    {
        if (_panel == null) return;
        var instances = _identities.Where(pair => IsSessionActive(pair.Key)).OrderBy(pair => pair.Key)
            .Select(pair => new CsProfilerInstanceOption(pair.Key,
                $"{pair.Value.DisplayName} · PID {pair.Value.ProcessId}" +
                (pair.Value.EditorAttached ? " · editor" : "")))
            .ToArray();
        _panel.UpdateInstanceOptions(instances, _router.SelectedSessionId);
    }

    private void SendProtocol(int sessionId, WireMap payload)
    {
        var data = new Godot.Collections.Array { GodotDebuggerTransport.ToGodotVariant(payload) };
        SendSessionMessage(sessionId, CsProfilerBridge.ProtocolMessage, data);
    }

    private void SendSessionMessage(int sessionId, string message, Godot.Collections.Array data)
    {
        try { if (TryResolveSession(sessionId, out var session) && session.IsActive()) session.SendMessage(message, data); }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException) { }
    }

    public override bool _HasCapture(string capture) => capture == CsProfilerBridge.MessagePrefix;

    public override bool _Capture(string message, Godot.Collections.Array data, int sessionId)
    {
        EnsureManagedState();
        if (_sessionIds.Add(sessionId)) RememberSessionIds();
        if (message == CsProfilerBridge.ReadyMessage)
        {
            if (!CsProfilerRuntimeIdentity.TryFromWire(data, out var identity))
            {
                _panel?.ReportDebuggerPayloadError("Profiler payload rejected: malformed ready message.");
                return true;
            }
            _identities[sessionId] = identity;
            ApplyRouteChange(_router.AcceptReady(sessionId, identity, IsSessionActive));
            PublishInstances();
            Callable.From(() => SendSessionMessage(sessionId, CsProfilerBridge.HandshakeMessage,
                new Godot.Collections.Array())).CallDeferred();
            if (_router.SelectedSessionId == sessionId)
                Callable.From(() => DriveSelectedRecovery(sessionId)).CallDeferred();
            return true;
        }
        if (message == CsProfilerBridge.MetricsMessage)
        {
            if (_router.SelectedSessionId == sessionId && data.Count == 3)
            {
                var fps = data[0].AsDouble();
                var frameMilliseconds = data[1].AsDouble();
                var runtimeFrame = data[2].AsInt64();
                if (double.IsFinite(fps) && fps >= 0 && double.IsFinite(frameMilliseconds) &&
                    frameMilliseconds >= 0 && runtimeFrame >= 0)
                    _panel?.ApplyRuntimeMetrics(runtimeFrame, fps, frameMilliseconds);
            }
            return true;
        }
        if (message == CsProfilerBridge.BatchFrameMessage)
        {
            if (_router.SelectedSessionId == sessionId && data.Count == 4 &&
                data.All(value => value.VariantType == Variant.Type.Int))
            {
                var generation = data[0].AsInt64();
                var sequence = data[1].AsInt64();
                var processFrame = data[2].AsInt64();
                var elapsedNanoseconds = data[3].AsInt64();
                if (generation > 0 && sequence > 0 && processFrame >= 0 && elapsedNanoseconds > 0)
                {
                    var association = new BatchFlushFrame(processFrame, elapsedNanoseconds);
                    _batchFrames[(sessionId, generation, sequence)] = association;
                    if (_protocol.TryGetValue(sessionId, out var associatedEndpoint))
                        associatedEndpoint.AssociateBatchFlushFrame(generation, sequence, association);
                }
            }
            return true;
        }
        if (message != CsProfilerBridge.ProtocolMessage) return false;
        if (!GodotDebuggerTransport.TryRead(data, out var payload))
        {
            _panel?.ReportDebuggerPayloadError("Profiler protocol payload rejected before conversion.");
            return true;
        }
        var endpoint = Endpoint(sessionId);
        var accepted = endpoint.Receive(payload);
        if (accepted)
            foreach (var pair in _batchFrames.Where(pair => pair.Key.Session == sessionId).ToArray())
                endpoint.AssociateBatchFlushFrame(pair.Key.Generation, pair.Key.Sequence, pair.Value);
        if (accepted && _router.SelectedSessionId == sessionId)
        {
            if (endpoint.ResetCompletedGeneration > 0)
                _resetAttempts.Remove(sessionId);
            Callable.From(() => DriveSelectedRecovery(sessionId)).CallDeferred();
        }
        return true;
    }

    private void DriveSelectedRecovery(int sessionId)
    {
        if (sessionId < 0 || sessionId != _router.SelectedSessionId || !IsSessionActive(sessionId) ||
            !_identities.TryGetValue(sessionId, out var identity)) return;
        var endpoint = Endpoint(sessionId);
        var runtimeMatches = string.Equals(endpoint.Snapshot.RuntimeToken, identity.RuntimeToken,
            StringComparison.Ordinal);
        var explicitIntent = _startIntent.ContainsKey(sessionId);
        var action = OrphanRecoveryPolicy.Decide(true, runtimeMatches, identity.Capturing,
            identity.ResetSupported, identity.Generation, endpoint.Snapshot,
            endpoint.ResetCompletedGeneration, explicitIntent);
        switch (action)
        {
            case OrphanRecoveryAction.WaitForNegotiation:
                SendSessionMessage(sessionId, CsProfilerBridge.HandshakeMessage, new Godot.Collections.Array());
                break;
            case OrphanRecoveryAction.RestartTargetRequired:
                _panel?.ReportDebuggerPayloadError(
                    "This running target predates reload recovery. Restart the game, then press Start again.");
                break;
            case OrphanRecoveryAction.ResetOrphan:
                SendOrRetryReset(sessionId, endpoint, identity);
                break;
            case OrphanRecoveryAction.StartFresh:
                if (!_startIntent.Remove(sessionId, out var configuration)) return;
                _pendingCapture.Request(configuration);
                var outcome = _pendingCapture.TryStart(endpoint);
                if (outcome == PendingStartOutcome.Rejected)
                    _panel?.ReportDebuggerPayloadError(
                        "Selected capture mode is not supported by this target.");
                break;
            case OrphanRecoveryAction.None:
                break;
        }
    }

    private void SendOrRetryReset(int sessionId, EditorCaptureCoordinator endpoint,
        CsProfilerRuntimeIdentity identity)
    {
        if (_resetAttempts.ContainsKey(sessionId) || endpoint.ResetPending) return;
        var requestId = Guid.NewGuid().ToString("N");
        if (!endpoint.RequestOrphanReset(identity.Generation, requestId, identity.ResetSupported)) return;
        _resetAttempts[sessionId] = new OrphanResetAttempt(requestId, 1,
            Time.GetTicksMsec() / 1000.0 + 1.0);
        _panel?.ReportDebuggerPayloadError(
            "Game code rebuilt during capture; discarding the incomplete capture before restarting.");
    }

    private void RetrySelectedReset(double now)
    {
        var sessionId = _router.SelectedSessionId;
        if (sessionId < 0 || !_resetAttempts.TryGetValue(sessionId, out var attempt) ||
            now < attempt.NextRetryAtSeconds || !_protocol.TryGetValue(sessionId, out var endpoint)) return;
        if (!endpoint.ResetPending)
        {
            _resetAttempts.Remove(sessionId);
            DriveSelectedRecovery(sessionId);
            return;
        }
        if (attempt.Attempts >= 3)
        {
            _resetAttempts.Remove(sessionId);
            _startIntent.Remove(sessionId);
            _panel?.ReportDebuggerPayloadError(
                "Profiler could not reset the pre-rebuild capture. Restart the game, then press Start again.");
            return;
        }
        if (!endpoint.RetryOrphanReset()) return;
        _resetAttempts[sessionId] = attempt with
        {
            Attempts = attempt.Attempts + 1,
            NextRetryAtSeconds = now + 1.0
        };
    }

    private EditorCaptureCoordinator Endpoint(int sessionId)
    {
        if (_protocol.TryGetValue(sessionId, out var endpoint)) return endpoint;
        endpoint = new EditorCaptureCoordinator($"godot-editor-debugger:{sessionId}", payload => SendProtocol(sessionId, payload));
        endpoint.Rejected += status => { if (_router.SelectedSessionId == sessionId) _panel?.ReportDebuggerPayloadError(status); };
        endpoint.SnapshotChanged += snapshot =>
        {
            if (_router.SelectedSessionId == sessionId && _identities.TryGetValue(sessionId, out var identity))
                _panel?.ApplyProtocolSnapshot(snapshot, identity);
        };
        endpoint.CompletedResultsChanged += results =>
        {
            if (_router.SelectedSessionId == sessionId) _panel?.ApplyProtocolResults(results);
        };
        endpoint.TimelineChanged += timeline =>
        {
            if (_router.SelectedSessionId == sessionId) _panel?.ApplyProtocolTimeline(timeline);
        };
        endpoint.TerminalCaptureChanged += capture =>
        {
            if (_router.SelectedSessionId == sessionId) _panel?.ApplyProtocolTerminalCapture(capture);
        };
        _protocol.Add(sessionId, endpoint);
        return endpoint;
    }

    private void EnsureManagedState()
    {
        var restored = _sessionIds == null;
        _sessionIds ??= new HashSet<int>();
        _activeSessionIds ??= new HashSet<int>();
        _protocol ??= new Dictionary<int, EditorCaptureCoordinator>();
        _identities ??= new Dictionary<int, CsProfilerRuntimeIdentity>();
        _batchFrames ??= new Dictionary<(int Session, long Generation, long Sequence), BatchFlushFrame>();
        _router ??= new CsProfilerSessionRouterState();
        _pendingCapture ??= new PendingCaptureRequest();
        _startIntent ??= new Dictionary<int, ModeConfiguration>();
        _resetAttempts ??= new Dictionary<int, OrphanResetAttempt>();
        if (restored) RestoreSessionIds();
    }

    private void RememberSessionIds()
    {
        SetMeta(ReloadSessionIdsMeta, string.Join(",", _sessionIds.OrderBy(id => id)
            .Take(MaximumRememberedSessions)));
    }

    private void RestoreSessionIds()
    {
        if (!HasMeta(ReloadSessionIdsMeta)) return;
        foreach (var value in GetMeta(ReloadSessionIdsMeta).AsString()
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Take(MaximumRememberedSessions))
            if (int.TryParse(value, out var sessionId) && sessionId >= 0)
                _sessionIds.Add(sessionId);
        _nextOwnedDiscoveryAtSeconds = 0;
    }

    private void ApplyRouteChange(CsProfilerRouteChange change)
    {
        if (!change.Changed) return;
        if (change.PreviousSessionId >= 0 && change.PreviousSessionId != change.SelectedSessionId)
        {
            if (_startIntent.Remove(change.PreviousSessionId, out var previousIntent) &&
                change.SelectedSessionId < 0)
                _unboundStartIntent = previousIntent;
            if (_protocol.TryGetValue(change.PreviousSessionId, out var previous)) previous.Stop();
        }
        if (change.SelectedSessionId < 0 || change.Identity == null)
        {
            // A queued pre-target request must survive transient route loss during debugger startup.
            // Requests that started were already consumed; explicit Stop/teardown still cancel waiting intent.
            _panel?.OnSessionStopped();
            return;
        }
        _panel?.OnBridgeReady(change.Identity);
        if (_protocol.TryGetValue(change.SelectedSessionId, out var endpoint))
            _panel?.ApplyProtocolSnapshot(endpoint.Snapshot, change.Identity);
        if (_unboundStartIntent is not null && !_startIntent.ContainsKey(change.SelectedSessionId))
        {
            _startIntent[change.SelectedSessionId] = _unboundStartIntent;
            _unboundStartIntent = null;
        }
        DriveSelectedRecovery(change.SelectedSessionId);
    }
}

internal readonly record struct CsProfilerRouteChange(bool Changed, int PreviousSessionId, int SelectedSessionId, CsProfilerRuntimeIdentity Identity);

internal readonly record struct CsProfilerInstanceOption(int SessionId, string Label);

internal sealed class CsProfilerSessionRouterState
{
    private readonly Dictionary<int, CsProfilerRuntimeIdentity> _identities = new();
    private int _preferredSessionId = -1;
    public int SelectedSessionId { get; private set; } = -1;
    public void Prefer(int sessionId) => _preferredSessionId = sessionId;
    public CsProfilerRouteChange AcceptReady(int sessionId, CsProfilerRuntimeIdentity identity, Func<int, bool> isActive)
    {
        _identities.TryGetValue(sessionId, out var previousIdentity);
        _identities[sessionId] = identity;
        var change = Reconcile(isActive);
        if (!change.Changed && SelectedSessionId == sessionId && previousIdentity?.RuntimeToken != identity.RuntimeToken)
            return new(true, sessionId, sessionId, identity);
        return change;
    }
    public CsProfilerRouteChange Reconcile(Func<int, bool> isActive)
    {
        var previous = SelectedSessionId;
        // A preference that can no longer be honored is dropped permanently: debugger session ids
        // are reused across play-mode reruns, so a stale preference could silently bind a future
        // unrelated instance (or keep routing at a dead session and blank the panel).
        if (_preferredSessionId >= 0 && (!isActive(_preferredSessionId) || !_identities.ContainsKey(_preferredSessionId)))
            _preferredSessionId = -1;
        var next = _preferredSessionId >= 0
            ? _preferredSessionId
            : _identities.Where(pair => isActive(pair.Key)).OrderByDescending(pair => pair.Value.EditorAttached)
                .ThenBy(pair => pair.Key).Select(pair => pair.Key).FirstOrDefault(-1);
        SelectedSessionId = next;
        _identities.TryGetValue(next, out var identity);
        return new(previous != next, previous, next, identity);
    }
    public void Clear() { _identities.Clear(); _preferredSessionId = -1; SelectedSessionId = -1; }
    public void Forget(int sessionId)
    {
        _identities.Remove(sessionId);
        if (sessionId == _preferredSessionId) _preferredSessionId = -1;
    }
}
#else
public partial class CsProfilerDebuggerPlugin : Godot.Node { }
#endif
