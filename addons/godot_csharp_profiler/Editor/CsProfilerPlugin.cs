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
    private const string ReloadDockInstanceMeta = "_godot_csharp_profiler_reload_dock_instance";
    private const string ReloadDebuggerInstanceMeta = "_godot_csharp_profiler_reload_debugger_instance";
    private const string ReloadPanelInstanceMeta = "_godot_csharp_profiler_reload_panel_instance";

    private CsProfilerDebuggerPlugin _debugger;
    private CsProfilerPanel _panel;
    private EditorDock _dock;
    private bool _editorAttachedProbeRunning;
    private bool _editorAttachedProbeCaptureRequested;
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
        // Godot normally retains the native EditorPlugin across a C# assembly reload without
        // replaying _EnterTree. Some versions may replay it, so recover the retained surfaces
        // instead of registering a duplicate dock/debugger pair.
        if (HasMeta(ReloadDockInstanceMeta) || HasMeta(ReloadDebuggerInstanceMeta) ||
            HasMeta(ReloadPanelInstanceMeta))
        {
            if (!RecoverAfterManagedReload())
                CallDeferred(nameof(RecoverAfterManagedReload));
            return;
        }
        _registered = true;
        // Clean an owned entry left by an older build or interrupted editor session. Merely
        // opening the editor must not add this setting to the tracked project configuration.
        RemoveOwnedRuntimeBridgeAutoload();
        CreateEditorSurfaces();
        StartRequestedProbe();
    }

    private void CreateEditorSurfaces()
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
        _panel.RememberReloadOwners(_debugger, this);
        RememberReloadHandles();
    }

    private void StartRequestedProbe()
    {
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

    internal void RecoverPanelAfterManagedReload(CsProfilerPanel panel)
    {
        _panel = panel;
        _registered = true;
        _dock ??= ResolveReloadHandle<EditorDock>(ReloadDockInstanceMeta);
        _debugger ??= ResolveReloadHandle<CsProfilerDebuggerPlugin>(ReloadDebuggerInstanceMeta);
        if (_debugger == null) return;
        _panel.RememberReloadOwners(_debugger, this);
        RememberReloadHandles();
    }

    private void RememberReloadHandles()
    {
        if (_dock != null && IsInstanceValid(_dock))
            SetMeta(ReloadDockInstanceMeta, unchecked((long)_dock.GetInstanceId()));
        if (_debugger != null && IsInstanceValid(_debugger))
            SetMeta(ReloadDebuggerInstanceMeta, unchecked((long)_debugger.GetInstanceId()));
        if (_panel != null && IsInstanceValid(_panel))
            SetMeta(ReloadPanelInstanceMeta, unchecked((long)_panel.GetInstanceId()));
    }

    private T ResolveReloadHandle<T>(string metadata) where T : GodotObject
    {
        if (!HasMeta(metadata)) return null;
        var id = unchecked((ulong)GetMeta(metadata).AsInt64());
        return id == 0 ? null : InstanceFromId(id) as T;
    }

    private bool RecoverAfterManagedReload()
    {
        if (_registered && _dock != null && _debugger != null && _panel != null &&
            IsInstanceValid(_dock) && IsInstanceValid(_debugger) && IsInstanceValid(_panel))
            return true;
        if (!IsInsideTree()) return false;

        var retainedDock = ResolveReloadHandle<EditorDock>(ReloadDockInstanceMeta);
        var retainedDebugger = ResolveReloadHandle<CsProfilerDebuggerPlugin>(ReloadDebuggerInstanceMeta);
        var retainedPanel = ResolveReloadHandle<CsProfilerPanel>(ReloadPanelInstanceMeta);
        if (retainedDock == null || retainedDebugger == null || retainedPanel == null ||
            !IsInstanceValid(retainedDock) || !IsInstanceValid(retainedDebugger) ||
            !IsInstanceValid(retainedPanel))
        {
            GD.PushError("C# Profiler could not recover its editor surfaces after the game rebuild. " +
                         "Disable and re-enable the plugin once.");
            return false;
        }

        _registered = true;
        _dock = retainedDock;
        _debugger = retainedDebugger;
        _panel = retainedPanel;
        if (!_panel.RecoverAfterManagedReload()) return false;
        _debugger.Initialize(_panel);
        _panel.RememberReloadOwners(_debugger, this);
        RememberReloadHandles();
        _debugger.PollActiveSessions();

        return true;
    }

    public override void _ExitTree()
    {
        if (!_registered)
        {
            // Disable may be the first callback after a managed rebuild. Recover only the retained
            // native handles needed for teardown; rebuilding the panel while disabling is wasteful
            // and can invoke controls that are about to be freed.
            _dock ??= ResolveReloadHandle<EditorDock>(ReloadDockInstanceMeta);
            _debugger ??= ResolveReloadHandle<CsProfilerDebuggerPlugin>(ReloadDebuggerInstanceMeta);
            _panel ??= ResolveReloadHandle<CsProfilerPanel>(ReloadPanelInstanceMeta);
            if (_dock == null && _debugger == null && _panel == null)
                return;
        }
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
        RemoveMeta(ReloadDockInstanceMeta);
        RemoveMeta(ReloadDebuggerInstanceMeta);
        RemoveMeta(ReloadPanelInstanceMeta);
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
        _editorAttachedProbeCaptureRequested = false;
        _editorAttachedProbeStopSent = false;
        _editorAttachedProbeStartedAt = Time.GetTicksMsec() / 1000.0;
        _editorAttachedProbeDeadline = Time.GetTicksMsec() / 1000.0 + 30.0;
        EditorInterface.Singleton.PlayMainScene();
    }

    public override void _Process(double delta)
    {
        if (!_registered || _dock == null || _debugger == null || _panel == null)
            RecoverAfterManagedReload();
        _debugger?.PollActiveSessions();
        if (!_editorAttachedProbeRunning)
            return;
        var now = Time.GetTicksMsec() / 1000.0;
        if (!_editorAttachedProbeCaptureRequested && EditorInterface.Singleton.IsPlayingScene() &&
            _panel?.BridgeReadyForTests == true && _panel.IdentityForTests.EditorAttached)
        {
            // RequestStart is refused in transient states (capabilities mid-negotiation), so keep
            // retrying every frame until the intent is actually recorded; a single unlucky attempt
            // otherwise strands the probe at "Press Start to capture" until the deadline.
            if (_panel.RequestSamplingCapture())
                _editorAttachedProbeCaptureRequested = true;
        }
        if (!_editorAttachedProbeStopSent && _panel?.BridgeReadyForTests == true &&
            _panel.TimelinePointCountForTests >= 1 &&
            now - _editorAttachedProbeStartedAt >= 3.0)
        {
            _editorAttachedProbeStopSent = true;
            _panel.RequestStopForTests();
        }
        if (_editorAttachedProbeStopSent && _panel?.BridgeReadyForTests == true &&
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
            if (!_panel.PerformanceTextForTests.StartsWith("Flush frame ", StringComparison.Ordinal) &&
                !string.Equals(_panel.PerformanceTextForTests, "Flush-frame timing unavailable", StringComparison.Ordinal))
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
                // Re-arm the retry loop above instead of firing once: the second capture request
                // can also land in a transient state right after the first capture completed.
                _editorAttachedProbeCaptureRequested = false;
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
