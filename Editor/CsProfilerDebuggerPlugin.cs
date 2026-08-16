#if TOOLS
using Apeworks.GodotCSharpProfiler;
using Apeworks.GodotCSharpProfiler.Editor.Integration;
using Apeworks.GodotCSharpProfiler.Runtime.Protocol.Adapters;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

[Tool]
public partial class CsProfilerDebuggerPlugin : EditorDebuggerPlugin
{
    private CsProfilerPanel _panel;
    private readonly HashSet<int> _sessionIds = new();
    private readonly HashSet<int> _activeSessionIds = new();
    private readonly Dictionary<int, EditorCaptureCoordinator> _protocol = new();
    private readonly Dictionary<int, CsProfilerRuntimeIdentity> _identities = new();
    private readonly CsProfilerSessionRouterState _router = new();
    private double _nextOwnedDiscoveryAtSeconds;

    public void Initialize(CsProfilerPanel panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        if (ReferenceEquals(_panel, panel)) return;
        if (_panel != null) Teardown();
        _panel = panel;
        _panel.SessionActiveQuery = AnySessionActive;
        _panel.ProfilingToggled += SendControlMessage;
        _panel.DiscoveryRequested += SendDiscoveryMessages;
    }

    public override void _SetupSession(int sessionId)
    {
        _sessionIds.Add(sessionId);
        _nextOwnedDiscoveryAtSeconds = 0;
        _panel?.InitializeSessionState(AnySessionActive());
    }

    public void PollActiveSessions()
    {
        if (_panel == null) return;
        var active = _sessionIds.Where(IsSessionActive).ToHashSet();
        foreach (var stopped in _activeSessionIds.Except(active).ToArray())
        {
            _router.Forget(stopped);
            if (_protocol.Remove(stopped, out var endpoint)) endpoint.Disconnect();
            _identities.Remove(stopped);
        }
        _activeSessionIds.Clear();
        _activeSessionIds.UnionWith(active);
        ApplyRouteChange(_router.Reconcile(active.Contains));
        _panel.InitializeSessionState(active.Count > 0);
        var now = Time.GetTicksMsec() / 1000.0;
        if (active.Count == 0 || _router.SelectedSessionId >= 0 || now < _nextOwnedDiscoveryAtSeconds) return;
        _nextOwnedDiscoveryAtSeconds = now + 1;
        SendDiscoveryMessages();
    }

    public void Teardown()
    {
        if (_panel == null) return;
        StopSelectedOwner();
        _panel.ProfilingToggled -= SendControlMessage;
        _panel.DiscoveryRequested -= SendDiscoveryMessages;
        _panel.SessionActiveQuery = null;
        foreach (var endpoint in _protocol.Values) endpoint.Disconnect();
        _sessionIds.Clear();
        _activeSessionIds.Clear();
        _protocol.Clear();
        _identities.Clear();
        _router.Clear();
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
        try { session = GetSession(sessionId); return session != null && IsInstanceValid(session); }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException or
                                      ArgumentOutOfRangeException or IndexOutOfRangeException) { return false; }
    }

    private void SendControlMessage(bool start)
    {
        ApplyRouteChange(_router.Reconcile(IsSessionActive));
        if (_router.SelectedSessionId < 0) { if (start) SendDiscoveryMessages(); return; }
        if (!_protocol.TryGetValue(_router.SelectedSessionId, out var endpoint)) return;
        if (start) endpoint.Start(_panel.ConfigurationForProtocol);
        else endpoint.Stop();
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
        _sessionIds.Add(sessionId);
        if (message == CsProfilerBridge.ReadyMessage)
        {
            if (!CsProfilerRuntimeIdentity.TryFromWire(data, out var identity))
            {
                _panel?.ReportDebuggerPayloadError("Profiler payload rejected: malformed ready message.");
                return true;
            }
            _identities[sessionId] = identity;
            ApplyRouteChange(_router.AcceptReady(sessionId, identity, IsSessionActive));
            return true;
        }
        if (message != CsProfilerBridge.ProtocolMessage) return false;
        if (!GodotDebuggerTransport.TryRead(data, out var payload))
        {
            _panel?.ReportDebuggerPayloadError("Profiler protocol payload rejected before conversion.");
            return true;
        }
        Endpoint(sessionId).Receive(payload);
        return true;
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
        _protocol.Add(sessionId, endpoint);
        return endpoint;
    }

    private void ApplyRouteChange(CsProfilerRouteChange change)
    {
        if (!change.Changed) return;
        if (change.PreviousSessionId >= 0 && change.PreviousSessionId != change.SelectedSessionId &&
            _protocol.TryGetValue(change.PreviousSessionId, out var previous)) previous.Stop();
        if (change.SelectedSessionId < 0 || change.Identity == null) { _panel?.OnSessionStopped(); return; }
        _panel?.OnBridgeReady(change.Identity);
        if (_protocol.TryGetValue(change.SelectedSessionId, out var endpoint))
            _panel?.ApplyProtocolSnapshot(endpoint.Snapshot, change.Identity);
        if (_panel?.ProfilingRequested == true) Endpoint(change.SelectedSessionId).Start(_panel.ConfigurationForProtocol);
    }
}

internal readonly record struct CsProfilerRouteChange(bool Changed, int PreviousSessionId, int SelectedSessionId, CsProfilerRuntimeIdentity Identity);

internal sealed class CsProfilerSessionRouterState
{
    private readonly Dictionary<int, CsProfilerRuntimeIdentity> _identities = new();
    public int SelectedSessionId { get; private set; } = -1;
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
        var next = _identities.Where(pair => isActive(pair.Key)).OrderByDescending(pair => pair.Value.EditorAttached)
            .ThenBy(pair => pair.Key).Select(pair => pair.Key).FirstOrDefault(-1);
        SelectedSessionId = next;
        _identities.TryGetValue(next, out var identity);
        return new(previous != next, previous, next, identity);
    }
    public void Clear() { _identities.Clear(); SelectedSessionId = -1; }
    public void Forget(int sessionId) => _identities.Remove(sessionId);
}
#endif
