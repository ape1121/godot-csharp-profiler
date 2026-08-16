#if TOOLS
using Apeworks.GodotCSharpProfiler;
using Apeworks.GodotCSharpProfiler.Editor.Integration;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// Routes every debugger session through one editor-owned bottom panel. Session tabs are
// intentionally never created: an editor play session and its debugger companion must not
// produce separate "C# Profiler" / "C# Profiler Editor" surfaces.
[Tool]
public partial class CsProfilerDebuggerPlugin : EditorDebuggerPlugin
{
    private CsProfilerPanel _panel;
    private readonly HashSet<int> _sessionIds = new();
    private readonly HashSet<int> _activeSessionIds = new();
    private readonly CsProfilerSessionRouterState _router = new();
    private double _nextOwnedDiscoveryAtSeconds;

    // Godot instantiates every [Tool] script during managed assembly reload. Keep this constructor
    // side-effect free; the owning EditorPlugin supplies the one real panel through Initialize.
    public CsProfilerDebuggerPlugin()
    {
    }

    public void Initialize(CsProfilerPanel panel)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _panel.SessionActiveQuery = AnySessionActive;
        _panel.ProfilingToggled += SendControlMessage;
        _panel.DiscoveryRequested += SendDiscoveryMessages;
    }

    public override void _SetupSession(int sessionId)
    {
        _sessionIds.Add(sessionId);
        _nextOwnedDiscoveryAtSeconds = 0.0;
        _panel.InitializeSessionState(AnySessionActive());
    }

    // The editor plugin owns discovery independently of dock visibility. A collapsed/hidden bottom
    // dock must not be responsible for finding the game, and a reused scalar session id must not
    // retain the prior process identity.
    public void PollActiveSessions()
    {
        if (_panel == null)
            return;
        var active = _sessionIds.Where(IsSessionActive).ToHashSet();
        foreach (var stopped in _activeSessionIds.Except(active).ToArray())
            _router.Forget(stopped);
        _activeSessionIds.Clear();
        _activeSessionIds.UnionWith(active);
        ApplyRouteChange(_router.Reconcile(active.Contains));
        _panel.InitializeSessionState(active.Count > 0);
        var now = Time.GetTicksMsec() / 1000.0;
        if (active.Count == 0 || _router.SelectedSessionId >= 0 ||
            now < _nextOwnedDiscoveryAtSeconds)
        {
            return;
        }
        _nextOwnedDiscoveryAtSeconds = now + 1.0;
        SendDiscoveryMessages();
    }

    public void Teardown()
    {
        if (_panel == null)
            return;
        _panel.ProfilingToggled -= SendControlMessage;
        _panel.DiscoveryRequested -= SendDiscoveryMessages;
        _panel.SessionActiveQuery = null;
        _sessionIds.Clear();
        _activeSessionIds.Clear();
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
        try
        {
            return TryResolveSession(sessionId, out var session) && session.IsActive();
        }
        catch (ObjectDisposedException) { return false; }
    }

    private bool TryResolveSession(int sessionId, out EditorDebuggerSession session)
    {
        session = null;
        try
        {
            session = GetSession(sessionId);
            return session != null && IsInstanceValid(session);
        }
        catch (Exception error) when (error is ObjectDisposedException or
                                      InvalidOperationException or
                                      ArgumentOutOfRangeException or
                                      IndexOutOfRangeException)
        {
            return false;
        }
    }

    private void SendControlMessage(bool start)
    {
        var message = start ? "cs_profiler:start" : "cs_profiler:stop";
        if (!start)
        {
            // Stop every known peer. A peer that was superseded by the editor game must never
            // keep an invisible capture running after the shared toggle is released.
            foreach (var sessionId in _sessionIds.Where(IsSessionActive).OrderBy(id => id))
                SendSessionMessage(sessionId, message);
            return;
        }
        ApplyRouteChange(_router.Reconcile(IsSessionActive));
        if (_router.SelectedSessionId >= 0)
            SendSessionMessage(_router.SelectedSessionId, message);
        else
            SendDiscoveryMessages();
    }

    private void SendDiscoveryMessages()
    {
        foreach (var sessionId in _sessionIds.Where(IsSessionActive).OrderBy(id => id))
            SendSessionMessage(sessionId, "cs_profiler:discover");
    }

    private void SendSessionMessage(int sessionId, string message)
    {
        try
        {
            if (TryResolveSession(sessionId, out var session) && session.IsActive())
                session.SendMessage(message, new Godot.Collections.Array());
        }
        catch (Exception error) when (error is ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    public override bool _HasCapture(string capture) => capture == "cs_profiler";

    public override bool _Capture(string message, Godot.Collections.Array data, int sessionId)
    {
        _sessionIds.Add(sessionId);
        if (string.IsNullOrWhiteSpace(message) || message.Length > 128 || message.Any(char.IsControl))
        {
            _panel?.ReportDebuggerPayloadError("Profiler payload rejected: invalid message name.");
            return true;
        }
        switch (message)
        {
            case "cs_profiler:ready":
            {
                if (!CsProfilerRuntimeIdentity.TryFromWire(data, out var identity))
                {
                    _panel?.ReportDebuggerPayloadError(
                        "Profiler payload rejected: malformed ready message.");
                    return true; // Known capture, rejected closed without changing routing.
                }
                ApplyRouteChange(_router.AcceptReady(sessionId, identity, IsSessionActive));
                return true;
            }
            case "cs_profiler:frame":
                if (_router.SelectedSessionId == sessionId)
                    _panel?.IngestFrame(data);
                return true;
            default:
                return false;
        }
    }

    private void ApplyRouteChange(CsProfilerRouteChange change)
    {
        if (!change.Changed)
            return;
        if (change.PreviousSessionId >= 0 && change.PreviousSessionId != change.SelectedSessionId &&
            _panel.ProfilingRequested && IsSessionActive(change.PreviousSessionId))
            SendSessionMessage(change.PreviousSessionId, "cs_profiler:stop");
        if (change.SelectedSessionId < 0 || change.Identity == null)
        {
            _panel.OnSessionStopped();
            return;
        }
        _panel.OnBridgeReady(change.Identity);
        if (_panel.ProfilingRequested)
            SendSessionMessage(change.SelectedSessionId, "cs_profiler:start");
    }
}

internal readonly record struct CsProfilerRouteChange(
    bool Changed,
    int PreviousSessionId,
    int SelectedSessionId,
    CsProfilerRuntimeIdentity Identity);

// Pure scalar routing policy kept separate from Godot's reload-sensitive debugger wrappers so the
// preference and failover transitions can be regression tested without constructing editor state.
internal sealed class CsProfilerSessionRouterState
{
    private readonly Dictionary<int, CsProfilerRuntimeIdentity> _identities = new();
    public int SelectedSessionId { get; private set; } = -1;

    public CsProfilerRouteChange AcceptReady(
        int sessionId,
        CsProfilerRuntimeIdentity identity,
        Func<int, bool> isActive)
    {
        _identities.TryGetValue(sessionId, out var previousIdentity);
        _identities[sessionId] = identity;
        var change = Reconcile(isActive);
        if (!change.Changed && SelectedSessionId == sessionId &&
            previousIdentity?.RuntimeToken != identity.RuntimeToken)
        {
            // Godot may reuse the numeric debugger id after play/stop. The runtime token, not the
            // session id, owns history identity and capture-state restoration.
            return new CsProfilerRouteChange(true, sessionId, sessionId, identity);
        }
        return change;
    }

    public CsProfilerRouteChange Reconcile(Func<int, bool> isActive)
    {
        var previous = SelectedSessionId;
        var next = _identities
            .Where(pair => isActive(pair.Key))
            .OrderByDescending(pair => pair.Value.EditorAttached)
            .ThenBy(pair => pair.Key)
            .Select(pair => pair.Key)
            .FirstOrDefault(-1);
        SelectedSessionId = next;
        _identities.TryGetValue(next, out var identity);
        return new CsProfilerRouteChange(previous != next, previous, next, identity);
    }

    public void Clear()
    {
        _identities.Clear();
        SelectedSessionId = -1;
    }

    public void Forget(int sessionId)
    {
        _identities.Remove(sessionId);
    }
}
#endif
