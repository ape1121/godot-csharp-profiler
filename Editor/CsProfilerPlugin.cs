#if TOOLS
using Godot;
using System;
using System.Linq;

// Registers the C# Profiler debugger tab. All logic lives in CsProfilerDebuggerPlugin (session
// wiring) and CsProfilerPanel (UI); this class only owns the editor lifecycle.
[Tool]
public partial class CsProfilerPlugin : EditorPlugin
{
    private CsProfilerDebuggerPlugin _debugger;
    private CsProfilerPanel _panel;
    private EditorDock _dock;
    private bool _editorAttachedProbeRunning;
    private double _editorAttachedProbeDeadline;

    public override void _EnterTree()
    {
        _panel = new CsProfilerPanel { Name = "C# Profiler" };
        _dock = new EditorDock
        {
            Name = "CSharpProfilerDock",
            Title = "C# Profiler",
            LayoutKey = "csharp_profiler",
            DefaultSlot = EditorDock.DockSlot.Bottom,
            Closable = false
        };
        _dock.AddChild(_panel);
        AddDock(_dock);
        _debugger = new CsProfilerDebuggerPlugin();
        _debugger.Initialize(_panel);
        AddDebuggerPlugin(_debugger);
        if (OS.GetCmdlineUserArgs().Contains(
                "--cs-profiler-editor-probe", StringComparer.Ordinal))
        {
            CallDeferred(nameof(StartEditorAttachedProbe));
        }
    }

    public override void _ExitTree()
    {
        if (_debugger != null)
        {
            _debugger.Teardown();
            RemoveDebuggerPlugin(_debugger);
            _debugger = null;
        }
        if (_dock != null && IsInstanceValid(_dock))
        {
            RemoveDock(_dock);
            _dock.QueueFree();
            _dock = null;
        }
        _panel = null;
    }

    private void StartEditorAttachedProbe()
    {
        _editorAttachedProbeRunning = true;
        _editorAttachedProbeDeadline = Time.GetTicksMsec() / 1000.0 + 30.0;
        _panel.RequestCaptureForTests();
        EditorInterface.Singleton.PlayMainScene();
    }

    public override void _Process(double delta)
    {
        _debugger?.PollActiveSessions();
        if (!_editorAttachedProbeRunning)
            return;
        var now = Time.GetTicksMsec() / 1000.0;
        if (_panel?.BridgeReadyForTests == true &&
            _panel.IdentityForTests.EditorAttached &&
            _panel.FrameCountForTests >= 3)
        {
            var count = EditorInterface.Singleton.GetBaseControl()
                .FindChildren("*", "EditorDock", recursive: true, owned: false)
                .OfType<EditorDock>()
                .Count(dock => dock.Title == "C# Profiler");
            if (count != 1)
            {
                GD.PushError($"CS_PROFILER_EDITOR_ATTACHED_ASSERTIONS_FAILED docks={count}");
                FinishEditorAttachedProbe(1);
                return;
            }
            GD.Print("CS_PROFILER_EDITOR_ATTACHED_ASSERTIONS_OK docks=1 " +
                     $"editor_play=true frames={_panel.FrameCountForTests}");
            FinishEditorAttachedProbe(0);
        }
        else if (now >= _editorAttachedProbeDeadline)
        {
            var identity = _panel?.IdentityForTests ?? CsProfilerRuntimeIdentity.Unknown;
            GD.PushError("CS_PROFILER_EDITOR_ATTACHED_ASSERTIONS_FAILED bridge timeout " +
                         $"ready={_panel?.BridgeReadyForTests} " +
                         $"editor_play={identity.EditorAttached} role={identity.Role} " +
                         $"name={identity.DisplayName} frames={_panel?.FrameCountForTests} " +
                         $"status={_panel?.StatusTextForTests}");
            FinishEditorAttachedProbe(1);
        }
    }

    private void FinishEditorAttachedProbe(int exitCode)
    {
        _editorAttachedProbeRunning = false;
        if (EditorInterface.Singleton.IsPlayingScene())
            EditorInterface.Singleton.StopPlayingScene();
        GetTree().Quit(exitCode);
    }
}
#endif
