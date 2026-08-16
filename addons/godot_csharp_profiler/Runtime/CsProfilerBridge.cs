using Godot;
using System;

namespace Apeworks.GodotCSharpProfiler;

// Autoload transport between CsProfiler and the "C# Profiler" editor debugger tab. Dormant unless
// the game was launched from the editor with the debugger attached; then it listens for
// start/stop from the tab and, while profiling, flushes CsProfiler once per rendered frame over
// the debugger channel. Runs at maximum process priority so every other node's _Process this
// frame is already inside the flushed tree.
public partial class CsProfilerBridge : Node
{
    public const string MessagePrefix = "cs_profiler";
    public const string FrameMessage = MessagePrefix + ":frame";
    public const string ReadyMessage = MessagePrefix + ":ready";

    // data[] layout of one frame message (parsed by CsProfilerPanel in the editor):
    // [0] frame index (long)  [1] engine frame usec (long)  [2] main-thread C# usec (long)
    // [3] names (string[])    [4] depths (int[])            [5] calls (long[])
    // [6] total usec (long[])
    private ulong _frameIndex;
    private bool _captureRegistered;
    private string _runtimeToken = "";
    private CsProfiler.CaptureLease _captureLease;

    public override void _EnterTree()
    {
        ProcessPriority = int.MaxValue;
        // Profiling must keep flushing while the game is paused (a paused frame is exactly when
        // you inspect what still ticks), so this node never pauses.
        ProcessMode = ProcessModeEnum.Always;
        _runtimeToken = $"{OS.GetProcessId()}:{Guid.NewGuid():N}";
        TryRegisterCapture();
    }

    public override void _Ready()
    {
        // Announce the bridge once the capture is registered. A managed editor-plugin reload can
        // still miss this packet, so the editor also sends bounded discover messages and this
        // bridge replays the same scalar identity in response.
        TryRegisterCapture();
    }

    public override void _ExitTree()
    {
        if (_captureRegistered)
        {
            EngineDebugger.UnregisterMessageCapture(MessagePrefix);
            _captureRegistered = false;
        }
        _captureLease?.Stop();
        _captureLease = null;
    }

    private bool OnEditorMessage(string message, Godot.Collections.Array data)
    {
        switch (message)
        {
            case "discover":
                SendReady();
                return true;
            case "start":
                // Idempotent retries retain this bridge's lease. A competing owner fails closed:
                // the bridge cannot flush or stop that capture.
                if (_captureLease?.IsActive != true &&
                    CsProfiler.TryStartCapture($"editor-bridge:{_runtimeToken}", out var lease))
                {
                    _captureLease = lease;
                    _frameIndex = 0;
                    GD.Print("C# Profiler: capture started.");
                }
                return true;
            case "stop":
                if (_captureLease?.Stop() == true)
                    GD.Print("C# Profiler: capture stopped.");
                _captureLease = null;
                return true;
            default:
                return false;
        }
    }

    private void SendReady()
    {
        if (!_captureRegistered)
            return;
        EngineDebugger.SendMessage(
            ReadyMessage,
            BuildReadyPayload(
                _runtimeToken,
                OS.GetProcessId(),
                // Window embedding and debugger connectivity are not launch identity: the primary
                // Play process is the one Godot launches with its editor PID, even when project run
                // arguments make that process the multiplayer host.
                IsEditorLaunched(System.Environment.GetCommandLineArgs()),
                OS.GetCmdlineUserArgs(),
                _captureLease?.IsActive == true));
    }

    // Ready payload contains only bounded scalar Variants. In particular, never put a Node,
    // EditorDebuggerSession, Callable, RID, or other Object here: Godot retains debugger packets
    // across managed reloads and cannot marshal those values back into a replacement assembly.
    // Layout: token, process id, embedded flag, normalized role, normalized display name, active.
    internal static Godot.Collections.Array BuildReadyPayload(
        string runtimeToken,
        long processId,
        bool editorLaunched,
        string[] userArguments,
        bool capturing)
    {
        var role = "game";
        var displayName = "Game";
        var arguments = userArguments ?? Array.Empty<string>();
        for (var index = 0; index < arguments.Length; index++)
        {
            switch (arguments[index])
            {
                case "--mp-host":
                    role = "host";
                    displayName = "Host";
                    break;
                case "--mp-join":
                    role = "client";
                    displayName = "Client";
                    break;
                case "--mp-name" when index + 1 < arguments.Length:
                    var authoredName = arguments[++index];
                    displayName = authoredName;
                    break;
            }
        }

        // Multiplayer role is independent from launch ownership. The primary Play process can be
        // configured as --mp-host; explicit peers may also have an active debugger transport.
        var editorAttached = editorLaunched;
        if (editorAttached)
        {
            role = "editor-play";
            displayName = "Editor Play";
        }

        return new Godot.Collections.Array
        {
            CsProfilerRuntimeIdentity.Normalize(
                runtimeToken,
                CsProfilerRuntimeIdentity.MaximumTokenLength,
                "unknown"),
            Math.Max(0, processId),
            editorAttached,
            CsProfilerRuntimeIdentity.Normalize(
                role,
                CsProfilerRuntimeIdentity.MaximumLabelLength,
                "game"),
            CsProfilerRuntimeIdentity.Normalize(
                displayName,
                CsProfilerRuntimeIdentity.MaximumLabelLength,
                "Game"),
            capturing
        };
    }

    internal static bool IsEditorLaunched(string[] engineArguments)
    {
        var arguments = engineArguments ?? Array.Empty<string>();
        foreach (var argument in arguments)
        {
            if (string.Equals(argument, "--editor-pid", StringComparison.Ordinal) ||
                argument.StartsWith("--editor-pid=", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    public override void _Process(double delta)
    {
        // In embedded play, the autoload can enter the tree before Godot activates the debugger
        // transport. Keep retrying until the ordinary profiler/debugger session exists instead of
        // permanently disabling this bridge after one early false check.
        if (!_captureRegistered)
        {
            TryRegisterCapture();
            return;
        }
        if (_captureLease?.IsActive != true)
            return;

        var snapshot = _captureLease.FlushFrame();
        EngineDebugger.SendMessage(FrameMessage, new Godot.Collections.Array
        {
            (long)_frameIndex++,
            (long)(delta * 1_000_000.0),
            snapshot.CsTotalUsec,
            snapshot.Names,
            snapshot.Depths,
            snapshot.Calls,
            snapshot.TotalUsec
        });
    }

    private void TryRegisterCapture()
    {
        if (_captureRegistered || !EngineDebugger.IsActive())
            return;
        EngineDebugger.RegisterMessageCapture(
            MessagePrefix,
            Callable.From<string, Godot.Collections.Array, bool>(OnEditorMessage));
        _captureRegistered = true;
        SendReady();
    }
}
