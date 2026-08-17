using Godot;
using System;
using Apeworks.GodotCSharpProfiler.Runtime.Protocol.Adapters;

namespace Apeworks.GodotCSharpProfiler;

public partial class CsProfilerBridge : Node
{
    public const string MessagePrefix = "cs_profiler";
    public const string ProtocolMessage = MessagePrefix + ":protocol";
    public const string ReadyMessage = MessagePrefix + ":ready";
    public const string HandshakeMessage = MessagePrefix + ":handshake";
    public const string MetricsMessage = MessagePrefix + ":metrics";

    private bool _captureRegistered;
    private string _runtimeToken = "";
    private RuntimeCaptureCoordinator _coordinator;
    private double _metricsElapsed;
    private double _smoothedFrameSeconds;

    public override void _EnterTree()
    {
        ProcessPriority = int.MaxValue;
        ProcessMode = ProcessModeEnum.Always;
        _runtimeToken = $"{OS.GetProcessId()}:{Guid.NewGuid():N}";
        TryRegisterCapture();
    }

    public override void _Ready() => TryRegisterCapture();

    public override void _ExitTree()
    {
        if (_captureRegistered)
        {
            EngineDebugger.UnregisterMessageCapture(MessagePrefix);
            _captureRegistered = false;
        }
        _coordinator?.Dispose();
        _coordinator = null;
    }

    private bool OnEditorMessage(string message, Godot.Collections.Array data)
    {
        switch (message)
        {
            case "discover": SendReady(); return true;
            case "handshake": _coordinator?.Announce(); return true;
            case "protocol":
                if (_coordinator is null || !GodotDebuggerTransport.TryRead(data, out var payload)) return false;
                return _coordinator.Receive(payload, "godot-editor-debugger");
            default: return false;
        }
    }

    private void SendReady()
    {
        if (!_captureRegistered) return;
        EngineDebugger.SendMessage(ReadyMessage, BuildReadyPayload(_runtimeToken, OS.GetProcessId(),
            IsEditorLaunched(OS.GetCmdlineArgs(), EngineDebugger.IsActive()), OS.GetCmdlineUserArgs(),
            _coordinator?.Capturing == true));
    }

    internal static Godot.Collections.Array BuildReadyPayload(string runtimeToken, long processId,
        bool editorLaunched, string[] userArguments, bool capturing)
    {
        var role = "game";
        var displayName = "Game";
        var arguments = userArguments ?? Array.Empty<string>();
        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--mp-host": role = "host"; displayName = "Host"; break;
                case "--mp-join": role = "client"; displayName = "Client"; break;
                case "--mp-name" when index + 1 < arguments.Length: displayName = arguments[++index]; break;
            }
        }
        if (editorLaunched) { role = "editor-play"; displayName = "Editor Play"; }
        return new Godot.Collections.Array
        {
            CsProfilerRuntimeIdentity.Normalize(runtimeToken, CsProfilerRuntimeIdentity.MaximumTokenLength, "unknown"),
            Math.Max(0, processId), editorLaunched,
            CsProfilerRuntimeIdentity.Normalize(role, CsProfilerRuntimeIdentity.MaximumLabelLength, "game"),
            CsProfilerRuntimeIdentity.Normalize(displayName, CsProfilerRuntimeIdentity.MaximumLabelLength, "Game"), capturing
        };
    }

    internal static bool IsEditorLaunched(string[] engineArguments, bool debuggerActive = false)
    {
        if (debuggerActive) return true;
        foreach (var argument in engineArguments ?? Array.Empty<string>())
            if (string.Equals(argument, "--editor-pid", StringComparison.Ordinal) ||
                argument.StartsWith("--editor-pid=", StringComparison.Ordinal)) return true;
        return false;
    }

    public override void _Process(double delta)
    {
        if (!_captureRegistered) { TryRegisterCapture(); return; }
        _coordinator?.Flush();
        if (delta > 0 && delta < 1)
            _smoothedFrameSeconds = _smoothedFrameSeconds <= 0
                ? delta
                : _smoothedFrameSeconds * 0.9 + delta * 0.1;
        _metricsElapsed += Math.Max(0, delta);
        if (_metricsElapsed >= 0.25)
        {
            _metricsElapsed = 0;
            var frameMilliseconds = Math.Max(0, _smoothedFrameSeconds) * 1000.0;
            var fps = frameMilliseconds > 0 ? 1000.0 / frameMilliseconds : 0;
            EngineDebugger.SendMessage(MetricsMessage, new Godot.Collections.Array
            {
                (double)fps, frameMilliseconds
            });
        }
    }

    private void TryRegisterCapture()
    {
        if (_captureRegistered || !EngineDebugger.IsActive()) return;
        EngineDebugger.RegisterMessageCapture(MessagePrefix,
            Callable.From<string, Godot.Collections.Array, bool>(OnEditorMessage));
        _captureRegistered = true;
        _coordinator = new RuntimeCaptureCoordinator(_runtimeToken,
            new GodotDebuggerTransport(ProtocolMessage), new ProductionRuntimeCaptureBackend());
        _coordinator.Connect();
        SendReady();
    }
}
