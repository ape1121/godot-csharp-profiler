#if TOOLS
using Apeworks.GodotCSharpProfiler;
using Apeworks.GodotCSharpProfiler.Editor.Integration;
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
    private bool _editorAttachedProbeStopSent;
    private double _editorAttachedProbeStartedAt;
    private double _editorAttachedProbeDeadline;
    private int _editorAttachedProbeRuns;
    private bool _registered;
    private ICoordinatorLifetime _coordinatorLifetime;

    // Backend ownership stays outside this plugin. A composition root may inject a disposal request.
    internal void SetCoordinatorLifetime(ICoordinatorLifetime lifetime) =>
        _coordinatorLifetime = lifetime;

    public override void _EnterTree()
    {
        if (_registered)
            return;
        _registered = true;
        EnsureRuntimeBridgeAutoload();
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
        else if (OS.GetCmdlineUserArgs().Contains(
                     "--cs-profiler-ui-probe", StringComparer.Ordinal))
        {
            CallDeferred(nameof(RunUiProbe));
        }
    }

    public override void _ExitTree()
    {
        if (!_registered)
            return;
        _registered = false;
        if (_debugger != null)
        {
            // Teardown first: it sends the selected target's owner-correct strict stop while the
            // debugger session is still registered. Disposal is requested only after unregister.
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
        _coordinatorLifetime?.RequestDispose();
        _coordinatorLifetime = null;
        RemoveOwnedRuntimeBridgeAutoload();
    }

    private void RemoveOwnedRuntimeBridgeAutoload()
    {
        if (!ProjectSettings.HasSetting(ProfilerAutoloadPolicy.Setting))
            return;
        var existing = ResolveAutoloadPath(ProjectSettings.GetSetting(ProfilerAutoloadPolicy.Setting).AsString());
        if (ProfilerAutoloadPolicy.IsOwnedValue(existing))
        {
            RemoveAutoloadSingleton(ProfilerAutoloadPolicy.Name);
            var saveError = ProjectSettings.Save();
            if (saveError != Error.Ok)
                GD.PushError($"C# Profiler removed its bridge but could not persist project.godot: {saveError}.");
        }
    }

    private void EnsureRuntimeBridgeAutoload()
    {
        if (ProjectSettings.HasSetting(ProfilerAutoloadPolicy.Setting))
        {
            var existing = ResolveAutoloadPath(ProjectSettings.GetSetting(ProfilerAutoloadPolicy.Setting).AsString());
            if (!ProfilerAutoloadPolicy.IsOwnedValue(existing))
                GD.PushError($"C# Profiler cannot register its runtime bridge: autoload name {ProfilerAutoloadPolicy.Name} is owned by another path.");
            return;
        }
        AddAutoloadSingleton(ProfilerAutoloadPolicy.Name, ProfilerAutoloadPolicy.ScriptPath);
        // Godot may normalize C# script autoloads to generated uid:// sidecars. The addon archive
        // intentionally does not depend on editor-generated sidecars, so persist our stable path.
        ProjectSettings.SetSetting(ProfilerAutoloadPolicy.Setting, "*" + ProfilerAutoloadPolicy.ScriptPath);
        var saveError = ProjectSettings.Save();
        if (saveError != Error.Ok)
            GD.PushError($"C# Profiler registered its bridge but could not persist project.godot: {saveError}.");
    }

    private static string ResolveAutoloadPath(string value)
    {
        var normalized = value.TrimStart('*');
        if (!normalized.StartsWith("uid://", StringComparison.Ordinal))
            return normalized;
        var id = ResourceUid.TextToId(normalized);
        return id == ResourceUid.InvalidId ? normalized : ResourceUid.GetIdPath(id);
    }

        private void StartEditorAttachedProbe()
    {
        _editorAttachedProbeRunning = true;
        _editorAttachedProbeStopSent = false;
        _editorAttachedProbeStartedAt = Time.GetTicksMsec() / 1000.0;
        _editorAttachedProbeDeadline = Time.GetTicksMsec() / 1000.0 + 30.0;
        _panel.RequestSamplingCapture();
        EditorInterface.Singleton.PlayMainScene();
    }

    public override void _Process(double delta)
    {
        _debugger?.PollActiveSessions();
        if (!_editorAttachedProbeRunning)
            return;
        var now = Time.GetTicksMsec() / 1000.0;
        if (!_editorAttachedProbeStopSent && _panel?.BridgeReadyForTests == true &&
            _panel.TimelinePointCountForTests >= 1 &&
            now - _editorAttachedProbeStartedAt >= 3.0)
        {
            _editorAttachedProbeStopSent = true;
            _panel.RequestStopForTests();
        }
        if (_panel?.BridgeReadyForTests == true &&
            _panel.SamplingResultRowsForTests >= 1 &&
            _panel.SelectedBatchRowsForTests >= 1 &&
            _panel.TimelinePointCountForTests >= 1 &&
            _panel.SelectedIndexForTests >= 0)
        {
            _panel.OpenSettingsForTests();
            if (!_panel.SettingsVisibleForTests)
            {
                GD.PushError("CS_PROFILER_EDITOR_ATTACHED_ASSERTIONS_FAILED options popup did not open");
                FinishEditorAttachedProbe(1);
                return;
            }
            if (!_panel.PerformanceTextForTests.StartsWith("Frame ", StringComparison.Ordinal) ||
                _panel.PerformanceTextForTests.Contains('—'))
            {
                GD.PushError($"CS_PROFILER_EDITOR_ATTACHED_ASSERTIONS_FAILED runtime metrics missing: {_panel.PerformanceTextForTests}");
                FinishEditorAttachedProbe(1);
                return;
            }
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
            GD.Print($"CS_PROFILER_EDITOR_ATTACHED_RUN_OK run={_editorAttachedProbeRuns + 1} docks=1 " +
                     $"sampling_rows={_panel.SamplingResultRowsForTests} " +
                     $"selected_rows={_panel.SelectedBatchRowsForTests} options_open=true " +
                     $"timeline_points={_panel.TimelinePointCountForTests} selected={_panel.SelectedIndexForTests}");
            if (++_editorAttachedProbeRuns == 1)
            {
                _editorAttachedProbeStopSent = false;
                _editorAttachedProbeStartedAt = now;
                _editorAttachedProbeDeadline = now + 30.0;
                _panel.RequestSamplingCapture();
                return;
            }
            GD.Print("CS_PROFILER_EDITOR_ATTACHED_ASSERTIONS_OK reruns=2");
            var tree = GetTree();
            var disableTimer = new Godot.Timer { OneShot = true, WaitTime = 0.1 };
            tree.Root.AddChild(disableTimer);
            disableTimer.Timeout += () =>
            {
                EditorInterface.Singleton.SetPluginEnabled("godot_csharp_profiler", false);
                var verifyTimer = new Godot.Timer { OneShot = true, WaitTime = 0.5 };
                tree.Root.AddChild(verifyTimer);
                verifyTimer.Timeout += () =>
                {
                    if (ProjectSettings.HasSetting(ProfilerAutoloadPolicy.Setting) ||
                        EditorInterface.Singleton.IsPluginEnabled("godot_csharp_profiler"))
                    {
                        GD.PushError("CS_PROFILER_DISABLE_ASSERTIONS_FAILED plugin or owned autoload remained after disable.");
                        tree.Quit(1);
                        return;
                    }
                    ProjectSettings.SetSetting("editor_plugins/enabled", Array.Empty<string>());
                    var saveError = ProjectSettings.Save();
                    if (saveError != Error.Ok)
                    {
                        GD.PushError($"CS_PROFILER_DISABLE_ASSERTIONS_FAILED plugin state save returned {saveError}.");
                        tree.Quit(1);
                        return;
                    }
                    GD.Print("CS_PROFILER_DISABLE_ASSERTIONS_OK plugin_disabled=true autoload_removed=true");
                    tree.Quit(0);
                };
                verifyTimer.Start();
            };
            disableTimer.Start();
            _editorAttachedProbeRunning = false;
        }
        else if (now >= _editorAttachedProbeDeadline)
        {
            var identity = _panel?.IdentityForTests ?? CsProfilerRuntimeIdentity.Unknown;
            GD.PushError("CS_PROFILER_EDITOR_ATTACHED_ASSERTIONS_FAILED bridge timeout " +
                         $"ready={_panel?.BridgeReadyForTests} " +
                         $"editor_play={identity.EditorAttached} role={identity.Role} " +
                         $"name={identity.DisplayName} sampling_rows={_panel?.SamplingResultRowsForTests} " +
                         $"timeline_points={_panel?.TimelinePointCountForTests} " +
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

    private void RunUiProbe()
    {
        if (_panel?.RunNativeSignalUiProbeForTests() == true)
        {
            GD.Print("CS_PROFILER_UI_ASSERTIONS_OK native_signals=true options_content=true");
            GetTree().Quit(0);
            return;
        }
        GD.PushError("CS_PROFILER_UI_ASSERTIONS_FAILED native signals or options content");
        GetTree().Quit(1);
    }
}
#else
public partial class CsProfilerPlugin : Godot.Node { }
#endif
