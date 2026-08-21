#if TOOLS
using Apeworks.GodotCSharpProfiler;
using Apeworks.GodotCSharpProfiler.Editor.Integration;
using Apeworks.GodotCSharpProfiler.Editor.Installation;
using Apeworks.GodotCSharpProfiler.Editor.Modes;
using Apeworks.GodotCSharpProfiler.Protocol;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Text;
using System.Text.Json;

// The single "C# Profiler" bottom panel: Start/Stop toolbar, clickable frame-time graph, and the
// selected frame's call tree with Total/Self/Calls columns. Frames arrive from
// CsProfilerBridge via CsProfilerDebuggerPlugin (see the bridge for the message layout).
[Tool]
public partial class CsProfilerPanel : VBoxContainer, IProfilerDockView, IProfilerCommandTransport, IProfilerOutput
{
    private const string ReloadDebuggerInstanceMeta = "_godot_csharp_profiler_debugger_instance";
    private const string ReloadOwnerPluginInstanceMeta = "_godot_csharp_profiler_owner_plugin_instance";
    private const string ReloadStateMeta = "_godot_csharp_profiler_reload_state";


    public sealed class ProfileFrame
    {
        public long Index;
        public double FrameMs;
        public double CsMs;
        public string[] Names;
        public int[] Depths;
        public long[] Calls;
        public long[] TotalUsec;
    }

    // One minute of history at 60 fps; the graph compresses whatever is stored into its width.
    private const int MaxFrames = 3600;
    private const int MaxScopeNodes = 4096;
    private const int MaxScopeDepth = 32;
    private const string ProfilerSelfFilter = "Apeworks.GodotCSharpProfiler";

    public event Action<bool> ProfilingToggled;
    public event Action DiscoveryRequested;
        public event Action<int> InstanceSelected;
    // Live session-state probe injected by the debugger plugin. Session lifecycle signals can be
    // missed around plugin/assembly reloads, so state shown to the user is queried, not cached.
    public Func<bool> SessionActiveQuery;
    public bool ProfilingRequested => _profilingRequested;
    private CaptureTimeline _protocolTimeline = CaptureTimeline.Empty;

    private bool SessionActive
    {
        get
        {
            try
            {
                return SessionActiveQuery?.Invoke() ?? _sessionActive;
            }
            catch (ObjectDisposedException)
            {
                return false; // session wrapper died during a plugin teardown/reload
            }
        }
    }

    private List<ProfileFrame> _frames = new();
    private HashSet<string> _collapsedPaths = new(StringComparer.Ordinal);
    private Button _startButton;
    private Button _stopButton;
    private Button _copyButton;
    private Button _exportButton;
    private Button _settingsButton;
    private CheckButton _automaticCaptureButton;
    private CheckButton _includeManualButton;
    private CheckButton _includeProfilerInternals;
    private SpinBox _frameSelector;
    private Label _targetLabel;
    private OptionButton _instanceSelector;
    private bool _updatingInstanceSelector;
    private Button _expandAllButton;
    private Button _collapseAllButton;
    private Label _statsLabel;
    private Label _performanceLabel;
    private OptionButton _samplingFilter;
    private Label _settingsLabel;
    private Label _qualityLabel;
    private LineEdit _samplingIncludes;
    private LineEdit _samplingExcludes;
    private SpinBox _samplingInterval;
    private LineEdit _automaticIncludes;
    private LineEdit _automaticExcludes;
    private SpinBox _automaticMaximum;
    private LineEdit _manualPrefix;
    private VBoxContainer _samplingSettings;
    private VBoxContainer _automaticSettings;
    private VBoxContainer _manualSettings;
    private Button _previewInstallButton;
    private Button _previewUninstallButton;
    private Button _applyInstallButton;
    private TextEdit _previewDiff;
    private PopupPanel _settingsPopup;
    private TabContainer _resultTabs;
    private Tree _selectedBatchTree;
    private PopupMenu _callContextMenu;
    private CaptureTimelinePoint _selectedTimelinePoint;
    private CsProfilerFrameGraph _graph;
    private Tree _tree;
    private int _selectedIndex = -1;
    private bool _liveFollow = true;
    private bool _updatingSelector;
    private int _startSignalInvocations;
    private int _stopSignalInvocations;
    private int _optionsSignalInvocations;
    private bool _rebuildingTree;
    private List<string> _displayedRowsForTests = new();
    private int _protocolResultRowsForTests;
    private int _samplingResultRowsForTests;
    private bool _sessionActive;
    private CsProfilerSessionDiscoveryState _discovery = new();
    private string _runtimeDescription = "";
    private double _lastFrameAtSec = double.NegativeInfinity;
    private bool _profilingRequested;
    private bool _reloadTransportBound;
    private ProfilerDockController _controller;
    internal bool ManagedSurfaceReadyForReload => _controller != null && _reloadTransportBound;
    internal ModeConfiguration ConfigurationForProtocol => _controller?.Configuration ?? ModeConfiguration.Default;
    internal string StatusTextForTests => _statsLabel?.Text ?? "";
    internal string PerformanceTextForTests => _performanceLabel?.Text ?? "";
    internal string[] DisplayedRowsForTests() => _displayedRowsForTests.ToArray();
    internal int FrameCountForTests => _frames.Count;
    internal int ProtocolResultRowsForTests => _protocolResultRowsForTests;
    internal int SamplingResultRowsForTests => _samplingResultRowsForTests;
    internal int SelectedBatchRowsForTests => _selectedBatchTree?.GetRoot()?.GetChildCount() ?? 0;
    internal bool SettingsVisibleForTests => _settingsPopup?.Visible == true;
    internal void OpenSettingsForTests() => ShowSettings();

    internal void RememberReloadOwners(CsProfilerDebuggerPlugin debugger, CsProfilerPlugin owner)
    {
        SetMeta(ReloadDebuggerInstanceMeta, unchecked((long)debugger.GetInstanceId()));
        SetMeta(ReloadOwnerPluginInstanceMeta, unchecked((long)owner.GetInstanceId()));
        _reloadTransportBound = true;
    }

    internal bool RecoverAfterManagedReload()
    {
        if (_controller != null && _reloadTransportBound) return true;
        if (!IsInsideTree()) return false;
        if (_controller == null)
        {
            _frames = new List<ProfileFrame>();
            _collapsedPaths = new HashSet<string>(StringComparer.Ordinal);
            _displayedRowsForTests = new List<string>();
            _expandedGroups = new HashSet<string>(StringComparer.Ordinal);
            _discovery = new CsProfilerSessionDiscoveryState();
            _selectedIndex = -1;
            _liveFollow = true;
            _runtimeDescription = "";
            _lastFrameAtSec = double.NegativeInfinity;
            ClearSurfaceReferences();
            foreach (var child in GetChildren().OfType<Node>().ToArray())
            {
                RemoveChild(child);
                child.QueueFree();
            }
            InitializeSurface();
        }
        var debugger = ResolveReloadObject<CsProfilerDebuggerPlugin>(ReloadDebuggerInstanceMeta);
        if (debugger == null) return false;
        debugger.Initialize(this);
        var owner = ResolveReloadObject<CsProfilerPlugin>(ReloadOwnerPluginInstanceMeta);
        owner?.RecoverPanelAfterManagedReload(this);
        _reloadTransportBound = true;
        return _controller != null && _startButton != null && _stopButton != null;
    }

    private T ResolveReloadObject<T>(string metadata) where T : GodotObject
    {
        if (!HasMeta(metadata)) return null;
        var id = unchecked((ulong)GetMeta(metadata).AsInt64());
        return id == 0 ? null : InstanceFromId(id) as T;
    }

    private void PersistReloadState(ProfilerDockReloadState state)
    {
        if (ProfilerReloadStateCodec.TryEncode(state, out var json)) SetMeta(ReloadStateMeta, json);
        else RemoveMeta(ReloadStateMeta);
    }

    private ProfilerDockReloadState ReadReloadState()
    {
        if (!HasMeta(ReloadStateMeta)) return null;
        if (ProfilerReloadStateCodec.TryDecode(GetMeta(ReloadStateMeta).AsString(), out var state))
            return state;
        RemoveMeta(ReloadStateMeta);
        return null;
    }

    private void ClearSurfaceReferences()
    {
        _controller = null;
        _startButton = null;
        _stopButton = null;
        _copyButton = null;
        _exportButton = null;
        _settingsButton = null;
        _automaticCaptureButton = null;
        _includeManualButton = null;
        _includeProfilerInternals = null;
        _frameSelector = null;
        _targetLabel = null;
        _instanceSelector = null;
        _expandAllButton = null;
        _collapseAllButton = null;
        _statsLabel = null;
        _performanceLabel = null;
        _samplingFilter = null;
        _settingsLabel = null;
        _qualityLabel = null;
        _samplingIncludes = null;
        _samplingExcludes = null;
        _samplingInterval = null;
        _automaticIncludes = null;
        _automaticExcludes = null;
        _automaticMaximum = null;
        _manualPrefix = null;
        _samplingSettings = null;
        _automaticSettings = null;
        _manualSettings = null;
        _previewInstallButton = null;
        _previewUninstallButton = null;
        _applyInstallButton = null;
        _previewDiff = null;
        _settingsPopup = null;
        _resultTabs = null;
        _selectedBatchTree = null;
        _callContextMenu = null;
        _selectedTimelinePoint = null;
        _graph = null;
        _tree = null;
        _pendingPreviewToken = null;
    }

    internal bool RunNativeSignalUiProbeForTests()
    {
        _startButton.Disabled = false;
        _stopButton.Disabled = false;
        _startButton.EmitSignal(BaseButton.SignalName.Pressed);
        _stopButton.EmitSignal(BaseButton.SignalName.Pressed);
        _settingsButton.EmitSignal(BaseButton.SignalName.Pressed);
        _automaticCaptureButton.EmitSignal(BaseButton.SignalName.Toggled, true);
        var automaticSelected = _controller.Configuration.Primary == PrimaryMode.AutomaticInstrumentation;
        _automaticCaptureButton.EmitSignal(BaseButton.SignalName.Toggled, false);
        var samplingSelected = _controller.Configuration.Primary == PrimaryMode.Sampling;
        _samplingFilter.EmitSignal(OptionButton.SignalName.ItemSelected, 2L);
        var allManaged = string.IsNullOrEmpty(_controller.Configuration.Sampling.IncludeAssemblies);
        _samplingFilter.EmitSignal(OptionButton.SignalName.ItemSelected, 0L);
        var projectOnly = _controller.Configuration.Sampling.IncludeAssemblies == ProjectAssemblyName();
        ApplyRuntimeMetrics(42, 60, 1000.0 / 60.0);
        var rows = new[]
        {
            new ResultRow("Game.Update", 12, 24.5, 0, 0, 0, 0),
            new ResultRow("Game.Render", 5, 10.2, 0, 0, 0, 0)
        };
        _controller.UpdateTimeline(new CaptureTimeline([
            new CaptureTimelinePoint(7, CaptureSource.Sampling, 17, 17, rows)
        ]));
        RenderSelectedBatch(_protocolTimeline.Points[0]);
        var inlineGroupControls = _expandAllButton.GetParent() == _copyButton.GetParent() &&
                                  _expandAllButton.GetParent() == _startButton.GetParent() &&
                                  !_expandAllButton.Disabled && !_collapseAllButton.Disabled &&
                                  _selectedBatchTree.GetParent() == _resultTabs;
        var group = _selectedBatchTree.GetRoot()?.GetFirstChild();
        var groupedTree = group != null && group.GetMetadata(0).AsString() == "group:Game" &&
                          group.GetText(1) == "17" && group.GetChildCount() == 2 &&
                          group.GetFirstChild()?.GetText(0) == "Update";
        OnCollapseAllPressed();
        var collapsedAll = group is { Collapsed: true } && !_expandedGroups.Contains("Game");
        OnExpandAllPressed();
        var expandedAll = group is { Collapsed: false } && _expandedGroups.Contains("Game");
        group?.Select(0);
        var selected = SelectedTimelineNames();
        var copied = BuildTimelineReport(_selectedTimelinePoint, selected);
        var outputEnabled = !_copyButton.Disabled && !_exportButton.Disabled;
        var simpleMultiCopy = selected.Count == 2 &&
                              copied == "Game.Update | 12 samples | 24.5%\nGame.Render | 5 samples | 10.2%\n";
        var selectedInstanceId = -1;
        InstanceSelected += id => selectedInstanceId = id;
        UpdateInstanceOptions([new(3, "Game · PID 100 · editor"), new(7, "Game · PID 200")], 3);
        var instanceListShown = _instanceSelector.Visible && _instanceSelector.ItemCount == 2 &&
                                _instanceSelector.Selected == 0;
        _instanceSelector.EmitSignal(OptionButton.SignalName.ItemSelected, 1L);
        var instanceSwitchRequested = selectedInstanceId == 7;
        UpdateInstanceOptions([new(7, "Game · PID 200")], 7);
        var instanceListHidden = !_instanceSelector.Visible;
        var exportPath = ProjectSettings.GlobalizePath("res://cs-profiler-timeline.json");
        if (File.Exists(exportPath)) File.Delete(exportPath);
        _exportButton.EmitSignal(BaseButton.SignalName.Pressed);
        var timelineExported = File.Exists(exportPath) &&
                               File.ReadAllText(exportPath).Contains("Game.Update", StringComparison.Ordinal);
        if (File.Exists(exportPath)) File.Delete(exportPath);
        var redundantTextRemoved = !_settingsPopup.FindChildren("*", "Label", recursive: true, owned: false)
            .OfType<Label>().Any(label => label.Text.Contains("statistical sampling by default", StringComparison.Ordinal));
        return _startSignalInvocations == 1 && _stopSignalInvocations == 1 &&
               _optionsSignalInvocations == 1 && _settingsPopup.Visible && automaticSelected && samplingSelected &&
               allManaged && projectOnly && outputEnabled && groupedTree && collapsedAll && expandedAll &&
               inlineGroupControls && simpleMultiCopy && instanceListShown && instanceSwitchRequested && instanceListHidden &&
               timelineExported && redundantTextRemoved &&
               _performanceLabel.Text == "Flush-frame timing unavailable" &&
               _settingsPopup.FindChildren("*", "Control", recursive: true, owned: false).Count >= 10;
    }
    internal int TimelinePointCountForTests => _protocolTimeline.Points.Count;
    internal int SelectedIndexForTests => _selectedIndex;
    internal bool BridgeReadyForTests => _discovery.BridgeReady;
    internal CsProfilerRuntimeIdentity IdentityForTests => _discovery.Identity;
    internal string BuildCallReportForTests() => _selectedIndex >= 0 &&
                                                  _selectedIndex < _frames.Count
        ? BuildCallReport(_frames[_selectedIndex], "")
        : "";

    internal bool RequestSamplingCapture()
    {
        if (_profilingRequested || _controller == null) return false;
        _controller.SelectMode(PrimaryMode.Sampling);
        return _controller.RequestStart();
    }

    internal void RequestCaptureForTests()
    {
        if (_profilingRequested) return;
        _controller?.SelectMode(PrimaryMode.Sampling);
        _profilingRequested = true;
        ProfilingToggled?.Invoke(true); // Test-only pre-launch intent; debugger queues until capabilities arrive.
    }

    internal void RequestStopForTests() => _controller?.Stop();

    public override void _Ready()
    {
        if (_controller == null)
            InitializeSurface();
    }

    private void InitializeSurface()
    {
        SizeFlagsVertical = SizeFlags.ExpandFill;
        CustomMinimumSize = Vector2.Zero;

        var toolbar = new HBoxContainer();
        AddChild(toolbar);
        _startButton = new Button { Text = "▶", TooltipText = "Start profiling" };
        _startButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, nameof(OnStartPressed)));
        toolbar.AddChild(_startButton);
        _stopButton = new Button { Text = "■", TooltipText = "Stop profiling" };
        _stopButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, nameof(OnStopPressed)));
        toolbar.AddChild(_stopButton);

        var clearButton = new Button { Text = "⌫", TooltipText = "Clear captured results" };
        clearButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, nameof(ClearAllResults)));
        toolbar.AddChild(clearButton);
        _settingsButton = new Button
        {
            Text = "⚙",
            TooltipText = "Profiler settings, advanced modes, export, and diagnostics"
        };
        _settingsButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, nameof(ShowSettings)));
        toolbar.AddChild(_settingsButton);
        _samplingFilter = new OptionButton
        {
            TooltipText = "Sampling assembly filter (applies on next Start). Project hides framework and engine internals.",
            CustomMinimumSize = new Vector2(132, 0)
        };
        _samplingFilter.AddItem("Calls: Project", 0);
        _samplingFilter.AddItem("Calls: Project + Godot", 1);
        _samplingFilter.AddItem("Calls: All managed", 2);
        _samplingFilter.AddItem("Custom…", 3);
        _samplingFilter.Selected = 0;
        _samplingFilter.Connect(OptionButton.SignalName.ItemSelected,
            new Callable(this, nameof(OnSamplingFilterSelected)));
        toolbar.AddChild(_samplingFilter);

        _copyButton = new Button { Text = "⧉ Copy", Disabled = true,
            TooltipText = "Copy every call in the selected batch" };
        _copyButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, nameof(OnCopyPressed)));
        _exportButton = new Button { Text = "Export Results", Disabled = true,
            TooltipText = "Export lossless source-separated JSON" };
        _exportButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, nameof(OnExportPressed)));

        toolbar.AddChild(new VSeparator());
        _instanceSelector = new OptionButton
        {
            Visible = false,
            TooltipText = "Profiled game instance. Other running instances stay attached and can be selected here.",
            CustomMinimumSize = new Vector2(150, 0)
        };
        _instanceSelector.Connect(OptionButton.SignalName.ItemSelected,
            new Callable(this, nameof(OnInstanceSelectorChanged)));
        toolbar.AddChild(_instanceSelector);
        _targetLabel = new Label
        {
            Text = "No target",
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            TooltipText = "Current debug target"
        };
        toolbar.AddChild(_targetLabel);
        _statsLabel = new Label
        {
            Text = "Disconnected",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        toolbar.AddChild(_statsLabel);
        _performanceLabel = new Label
        {
            Text = "FPS — · frame — ms",
            TooltipText = "Live game _Process delta, or the selected batch emission-frame timing when available."
        };
        toolbar.AddChild(_performanceLabel);
        toolbar.AddChild(new Label { Text = "Batch" });
        _frameSelector = new SpinBox
        {
            MinValue = 0,
            MaxValue = 0,
            Rounded = true,
            CustomMinimumSize = new Vector2(72, 0)
        };
        _frameSelector.Connect(Godot.Range.SignalName.ValueChanged,
            new Callable(this, nameof(OnFrameSelectorChanged)));
        toolbar.AddChild(_frameSelector);
        toolbar.AddChild(_copyButton);
        _expandAllButton = new Button { Text = "⊞", Disabled = true, TooltipText = "Expand all call groups" };
        _expandAllButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, nameof(OnExpandAllPressed)));
        toolbar.AddChild(_expandAllButton);
        _collapseAllButton = new Button { Text = "⊟", Disabled = true, TooltipText = "Collapse all call groups" };
        _collapseAllButton.Connect(BaseButton.SignalName.Pressed, new Callable(this, nameof(OnCollapseAllPressed)));
        toolbar.AddChild(_collapseAllButton);

        _graph = new CsProfilerFrameGraph { SizeFlagsVertical = SizeFlags.ShrinkBegin };
        _graph.Connect(CsProfilerFrameGraph.SignalName.FrameClicked,
            new Callable(this, nameof(OnGraphFrameClicked)));
        AddChild(_graph);

        _resultTabs = new TabContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = Vector2.Zero
        };
        AddChild(_resultTabs);
        _tree = new Tree
        {
            Name = "Manual frames",
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = Vector2.Zero,
            Columns = 4,
            ColumnTitlesVisible = true,
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row
        };
        _tree.SetColumnTitle(0, "Name");
        _tree.SetColumnTitle(1, "Wall time");
        _tree.SetColumnTitle(2, "Self");
        _tree.SetColumnTitle(3, "Calls");
        _tree.SetColumnExpand(0, true);
        for (var column = 1; column < 4; column++)
        {
            _tree.SetColumnExpand(column, false);
            _tree.SetColumnCustomMinimumWidth(column, 72);
        }
        _tree.Connect(Tree.SignalName.ItemCollapsed, new Callable(this, nameof(OnItemCollapsed)));
        _tree.Connect(Control.SignalName.GuiInput, new Callable(this, nameof(OnTreeGuiInput)));
        _resultTabs.AddChild(_tree);
        _selectedBatchTree = new Tree
        {
            Name = "Selected batch",
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = Vector2.Zero,
            Columns = 3,
            ColumnTitlesVisible = true,
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Multi
        };
        _selectedBatchTree.SetColumnTitle(0, "Function");
        _selectedBatchTree.SetColumnTitle(1, "Samples");
        _selectedBatchTree.SetColumnTitle(2, "Share");
        _selectedBatchTree.SetColumnExpand(0, true);
        _selectedBatchTree.Connect(Tree.SignalName.ItemCollapsed,
            new Callable(this, nameof(OnBatchGroupCollapsedToggled)));
        _selectedBatchTree.Connect(Control.SignalName.GuiInput,
            new Callable(this, nameof(OnSelectedBatchGuiInput)));
        _resultTabs.AddChild(_selectedBatchTree);
        _callContextMenu = new PopupMenu();
        _callContextMenu.AddItem("Copy call", 0);
        _callContextMenu.Connect(PopupMenu.SignalName.IdPressed,
            new Callable(this, nameof(OnCallContextAction)));
        AddChild(_callContextMenu);

        BuildSettingsUi();
        var reloadState = ReadReloadState();
        _controller = new ProfilerDockController(this, this, CreateInstallerSafely(), this,
            reloadState, PersistReloadState);
        if (reloadState is null)
            _controller.UpdateSampling(new SamplingSettings(ProjectAssemblyName(), ProfilerSelfFilter, 2_000_000));
        else
            PersistReloadState(_controller.CreateReloadSnapshot());
        SyncSettingsControls(_controller.Configuration);
        var resized = new Callable(this, nameof(ApplyResponsiveLayout));
        if (!IsConnected(Control.SignalName.Resized, resized))
            Connect(Control.SignalName.Resized, resized);
        ApplyResponsiveLayout();

        if (_discovery.BridgeReady)
            ApplyBridgeReadyUi(_discovery.Identity);
        else
            TryRequestDiscovery(NowSec());
    }

    private IAutomaticInstaller CreateInstallerSafely()
    {
        try
        {
            var projectRoot = ProjectSettings.GlobalizePath("res://");
            _ = ProjectInstaller.DiscoverProject(projectRoot);
            var package = ProjectSettings.GlobalizePath(
                $"res://addons/godot_csharp_profiler/assets/nuget/GodotCSharpProfiler.Fody.{ProjectInstaller.ProfilerFodyVersion}.nupkg");
            return new ProjectInstallerAdapter(projectRoot, package, () => File.Exists(package));
        }
        catch (Exception error) when (error is InstallationRefusedException or
                                      ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void BuildSettingsUi()
    {
        _settingsPopup = new PopupPanel();
        AddChild(_settingsPopup);
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(560, 470)
        };
        _settingsPopup.AddChild(scroll);
        var settingsRoot = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(530, 0)
        };
        scroll.AddChild(settingsRoot);

        var outputCommands = new HBoxContainer();
        outputCommands.AddChild(_exportButton);
        settingsRoot.AddChild(outputCommands);

        settingsRoot.AddChild(new Label { Text = "Capture" });
        _automaticCaptureButton = new CheckButton
        {
            Text = "Use exact method timing",
            TooltipText = "Requires the setup below, then a clean rebuild and game restart."
        };
        _automaticCaptureButton.Connect(BaseButton.SignalName.Toggled,
            new Callable(this, nameof(OnAutomaticCaptureToggled)));
        settingsRoot.AddChild(_automaticCaptureButton);
        _includeManualButton = new CheckButton { Text = "Include manual scopes" };
        _includeManualButton.Connect(BaseButton.SignalName.Toggled,
            new Callable(this, nameof(OnManualOverlayToggled)));
        settingsRoot.AddChild(_includeManualButton);
        _settingsLabel = new Label
        {
            Text = "",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        settingsRoot.AddChild(_settingsLabel);

        _samplingSettings = new VBoxContainer();
        settingsRoot.AddChild(_samplingSettings);
        _includeProfilerInternals = new CheckButton
        {
            Text = "Include profiler internals",
            TooltipText = "Show profiler infrastructure calls. Off by default."
        };
        _includeProfilerInternals.Connect(BaseButton.SignalName.Toggled,
            new Callable(this, nameof(OnProfilerInternalsToggled)));
        _samplingSettings.AddChild(_includeProfilerInternals);
        _samplingIncludes = AddLineSetting(_samplingSettings, "Include assemblies", ProjectAssemblyName(),
            nameof(OnSamplingIncludesChanged));
        _samplingExcludes = AddLineSetting(_samplingSettings, "Exclude assemblies", ProfilerSelfFilter,
            nameof(OnSamplingExcludesChanged));
        _samplingIncludes.Editable = false;
        _samplingExcludes.Editable = false;
        _samplingInterval = AddSpinSetting(_samplingSettings, "Sample every (ms)", 1, 1_000,
            2, nameof(OnSamplingIntervalChanged));
        _automaticSettings = new VBoxContainer();
        settingsRoot.AddChild(_automaticSettings);
        _automaticSettings.AddChild(new Label
        {
            Text = "Exact timing setup (preview changes before applying)",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        });
        _automaticIncludes = AddLineSetting(_automaticSettings, "Include", "Game",
            nameof(OnAutomaticIncludesChanged));
        _automaticExcludes = AddLineSetting(_automaticSettings, "Exclude", "",
            nameof(OnAutomaticExcludesChanged));
        _automaticMaximum = AddSpinSetting(_automaticSettings, "Method limit", 1, 1_000_000, 4096,
            nameof(OnAutomaticMaximumChanged));
        var installCommands = new HBoxContainer();
        _automaticSettings.AddChild(installCommands);
        _previewInstallButton = new Button { Text = "Preview Install" };
        _previewInstallButton.Connect(BaseButton.SignalName.Pressed,
            new Callable(this, nameof(PreviewAutomaticInstall)));
        installCommands.AddChild(_previewInstallButton);
        _previewUninstallButton = new Button { Text = "Preview Uninstall" };
        _previewUninstallButton.Connect(BaseButton.SignalName.Pressed,
            new Callable(this, nameof(PreviewAutomaticUninstall)));
        installCommands.AddChild(_previewUninstallButton);
        _applyInstallButton = new Button { Text = "Apply Confirmed", Disabled = true,
            TooltipText = "Review the diff, then click to explicitly confirm Apply." };
        _applyInstallButton.Connect(BaseButton.SignalName.Pressed,
            new Callable(this, nameof(ApplyAutomaticInstall)));
        installCommands.AddChild(_applyInstallButton);
        _previewDiff = new TextEdit { Editable = false, CustomMinimumSize = new Vector2(0, 120) };
        _automaticSettings.AddChild(_previewDiff);

        _manualSettings = new VBoxContainer();
        settingsRoot.AddChild(_manualSettings);
        _manualPrefix = AddLineSetting(_manualSettings, "Manual scope prefix", "",
            nameof(OnManualPrefixChanged));
        _qualityLabel = new Label
        {
            Text = "Complete capture · no observations",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        settingsRoot.AddChild(_qualityLabel);
    }

    private void OnStartPressed()
    {
        if (_controller == null || !_reloadTransportBound)
        {
            if (!TryHandoffRetainedStartIntent())
            {
                _profilingRequested = false;
                GD.PushWarning("Profiler UI could not recover after the game rebuild; disable and re-enable the plugin once.");
                return;
            }
            if (!RecoverAfterManagedReload())
                GD.PushWarning("Profiler UI could not recover after the game rebuild; disable and re-enable the plugin once.");
            return;
        }
        CompleteStartRequest();
    }

    private bool TryHandoffRetainedStartIntent()
    {
        var debugger = ResolveReloadObject<CsProfilerDebuggerPlugin>(ReloadDebuggerInstanceMeta);
        if (debugger == null) return false;
        var configuration = ReadReloadState()?.Configuration ?? ModeConfiguration.Default;
        debugger.QueueStartAfterManagedReload(configuration);
        return true;
    }

    private void CompleteStartRequest()
    {
        _startSignalInvocations++;
        if (_controller.RequestStart()) _profilingRequested = true;
    }

    private void OnStopPressed()
    {
        if (!RecoverAfterManagedReload())
        {
            GD.PushWarning("Profiler UI could not recover after the game rebuild; disable and re-enable the plugin once.");
            return;
        }
        _stopSignalInvocations++;
        if (_controller.Stop()) _profilingRequested = false;
    }

    private void OnCopyPressed()
    {
        if (_selectedTimelinePoint != null) CopyTimelineRows(SelectedTimelineNames());
        else _controller.Copy(ExportFormat.VisibleCsv);
    }

    private void OnExportPressed()
    {
        if (_protocolTimeline.Points.Count > 0) ExportTimeline();
        else _controller.Export(ExportFormat.LosslessJson);
    }

    private void OnGraphFrameClicked(int index)
    {
        _liveFollow = false;
        SelectFrame(index);
    }

    private void OnAutomaticCaptureToggled(bool enabled) =>
        _controller.SelectMode(enabled ? PrimaryMode.AutomaticInstrumentation : PrimaryMode.Sampling);
    private void OnManualOverlayToggled(bool included) => _controller.SetManualOverlay(included);

    private void OnSamplingFilterSelected(long index)
    {
        var assembly = ProjectAssemblyName();
        var exclusions = _includeProfilerInternals?.ButtonPressed == true ? string.Empty : ProfilerSelfFilter;
        switch (index)
        {
            case 0:
                _samplingIncludes.Text = assembly;
                _samplingExcludes.Text = exclusions;
                _controller.UpdateSampling(CurrentSampling() with
                    { IncludeAssemblies = assembly, ExcludeAssemblies = exclusions });
                break;
            case 1:
                _controller.UpdateSampling(CurrentSampling() with
                    { IncludeAssemblies = $"{assembly};GodotSharp", ExcludeAssemblies = exclusions });
                break;
            case 2:
                _samplingIncludes.Text = string.Empty;
                _samplingExcludes.Text = exclusions;
                _controller.UpdateSampling(CurrentSampling() with
                    { IncludeAssemblies = string.Empty, ExcludeAssemblies = exclusions });
                break;
        }
        var custom = index == 3;
        if (_samplingIncludes != null) _samplingIncludes.Editable = custom;
        if (_samplingExcludes != null) _samplingExcludes.Editable = custom;
    }

    private void OnProfilerInternalsToggled(bool included)
    {
        var exclusions = CurrentSampling().ExcludeAssemblies.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => !value.Equals(ProfilerSelfFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!included) exclusions.Add(ProfilerSelfFilter);
        var value = string.Join(';', exclusions);
        _samplingExcludes.Text = value;
        _controller.UpdateSampling(CurrentSampling() with { ExcludeAssemblies = value });
    }

    private void OnSamplingIncludesChanged(string text) =>
        _controller.UpdateSampling(CurrentSampling() with { IncludeAssemblies = text });
    private void OnSamplingExcludesChanged(string text) =>
        _controller.UpdateSampling(CurrentSampling() with { ExcludeAssemblies = text });
    private void OnSamplingIntervalChanged(double value) =>
        _controller.UpdateSampling(CurrentSampling() with
        {
            RequestedIntervalNanoseconds = (long)(value * 1_000_000)
        });
    private void OnAutomaticIncludesChanged(string text) =>
        _controller.UpdateAutomatic(CurrentAutomatic() with { IncludePatterns = text });
    private void OnAutomaticExcludesChanged(string text) =>
        _controller.UpdateAutomatic(CurrentAutomatic() with { ExcludePatterns = text });
    private void OnAutomaticMaximumChanged(double value) =>
        _controller.UpdateAutomatic(CurrentAutomatic() with { MaxMethods = (int)value });
    private void OnManualPrefixChanged(string text) =>
        _controller.UpdateManual(new ManualSettings(text));

    internal void ApplyRuntimeMetrics(long runtimeFrame, double fps, double frameMilliseconds)
    {
        if (_selectedTimelinePoint != null) return;
        if (_performanceLabel != null)
            _performanceLabel.Text = $"Frame {runtimeFrame} · {frameMilliseconds:0.00} ms · {fps:0} FPS";
    }

    private static string ProjectAssemblyName()
    {
        var configured = ProjectSettings.GetSetting("dotnet/project/assembly_name").AsString().Trim();
        if (!string.IsNullOrEmpty(configured)) return configured;
        var projectName = ProjectSettings.GetSetting("application/config/name").AsString().Trim();
        return string.IsNullOrEmpty(projectName) ? "Game" : projectName.Replace(" ", string.Empty);
    }

    private void ShowSettings()
    {
        _optionsSignalInvocations++;
        if (_settingsPopup == null || _settingsButton == null) return;
        var usable = DisplayServer.ScreenGetUsableRect();
        var size = new Vector2I(Math.Min(700, usable.Size.X - 32), Math.Min(620, usable.Size.Y - 32));
        var anchorPosition = _settingsButton.GetScreenPosition() + new Vector2(0, _settingsButton.Size.Y);
        var anchor = new Vector2I((int)anchorPosition.X, (int)anchorPosition.Y);
        var x = Math.Clamp(anchor.X, usable.Position.X + 8, usable.End.X - size.X - 8);
        var y = Math.Clamp(anchor.Y, usable.Position.Y + 8, usable.End.Y - size.Y - 8);
        _settingsPopup.Popup(new Rect2I(new Vector2I(x, y), size));
    }

        private void ApplyResponsiveLayout()
    {
        var layout = ProfilerDockLayoutPolicy.ForHeight(Size.Y);
        Visible = layout.ShowPrimaryToolbar && layout.ShowCalls && layout.ShowSettingsButton;
        if (_settingsButton != null) _settingsButton.Visible = layout.ShowSettingsButton;
        if (_resultTabs != null) _resultTabs.Visible = layout.ShowCalls &&
            (_protocolTimeline.Points.Count > 0 || _frames.Count > 0 || _protocolResultRowsForTests > 0);
        if (_graph != null) _graph.CustomMinimumSize = new Vector2(0, layout.GraphMinimumHeight);
        if (_qualityLabel != null) _qualityLabel.Visible = layout.ShowQualityDetails;
    }

    private LineEdit AddLineSetting(Control parent, string label, string value, string method)
    {
        var row = new HBoxContainer();
        parent.AddChild(row);
        row.AddChild(new Label { Text = label });
        var edit = new LineEdit { Text = value, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        edit.Connect(LineEdit.SignalName.TextChanged, new Callable(this, method));
        row.AddChild(edit);
        return edit;
    }

    private SpinBox AddSpinSetting(Control parent, string label, double minimum,
        double maximum, double value, string method)
    {
        var row = new HBoxContainer();
        parent.AddChild(row);
        row.AddChild(new Label { Text = label });
        var spin = new SpinBox { MinValue = minimum, MaxValue = maximum, Value = value, Rounded = true };
        spin.Connect(Godot.Range.SignalName.ValueChanged, new Callable(this, method));
        row.AddChild(spin);
        return spin;
    }

    private void SyncSettingsControls(ModeConfiguration configuration)
    {
        _automaticCaptureButton?.SetPressedNoSignal(
            configuration.Primary == PrimaryMode.AutomaticInstrumentation);
        _includeManualButton?.SetPressedNoSignal(configuration.IncludeManual);
        if (_samplingIncludes != null) _samplingIncludes.Text = configuration.Sampling.IncludeAssemblies;
        if (_samplingExcludes != null) _samplingExcludes.Text = configuration.Sampling.ExcludeAssemblies;
        if (_samplingInterval != null)
            _samplingInterval.SetValueNoSignal(configuration.Sampling.RequestedIntervalNanoseconds / 1_000_000.0);
        if (_automaticIncludes != null) _automaticIncludes.Text = configuration.Automatic.IncludePatterns;
        if (_automaticExcludes != null) _automaticExcludes.Text = configuration.Automatic.ExcludePatterns;
        if (_automaticMaximum != null) _automaticMaximum.SetValueNoSignal(configuration.Automatic.MaxMethods);
        if (_manualPrefix != null) _manualPrefix.Text = configuration.Manual.LabelPrefix;
    }

    private SamplingSettings CurrentSampling() => new(_samplingIncludes?.Text ?? "",
        _samplingExcludes?.Text ?? "", (long)((_samplingInterval?.Value ?? 2) * 1_000_000));
    private AutomaticSettings CurrentAutomatic() => new(_automaticIncludes?.Text ?? "Game",
        _automaticExcludes?.Text ?? "", (int)(_automaticMaximum?.Value ?? 4096));

    private string _pendingPreviewToken;
    private void PreviewAutomaticInstall()
    {
        var preview = _controller.PreviewAutomaticInstall();
        _pendingPreviewToken = preview?.Token;
        _applyInstallButton.Disabled = string.IsNullOrEmpty(_pendingPreviewToken);
        if (preview != null)
        {
            _previewDiff.Text = preview.Diff;
            _applyInstallButton.TooltipText = "Reviewed " + preview.ChangeCount +
                " change(s). Click to explicitly confirm Apply.";
        }
    }

    private void PreviewAutomaticUninstall()
    {
        var preview = _controller.PreviewAutomaticUninstall();
        _pendingPreviewToken = preview?.Token;
        _applyInstallButton.Disabled = string.IsNullOrEmpty(_pendingPreviewToken);
    }

    private void ApplyAutomaticInstall()
    {
        if (string.IsNullOrEmpty(_pendingPreviewToken)) return;
        _controller.ApplyAutomaticInstall(_pendingPreviewToken, confirmed: true);
        _pendingPreviewToken = null;
        _applyInstallButton.Disabled = true;
    }

        public void Render(ProfilerDockViewState state)
    {
        if (_targetLabel == null)
            return;
        _targetLabel.Text = state.Target;
        _statsLabel.Text = state.Status;
        var samplingSelected = state.ModeSegments.Any(mode =>
            mode.Label == "Sampling" && mode.Selected);
        var automaticSelected = state.ModeSegments.Any(mode =>
            mode.Label == "Automatic" && mode.Selected);
        var manualSelected = state.ModeSegments.Any(mode =>
            mode.Label == "Manual" && mode.Selected);
        _automaticCaptureButton?.SetPressedNoSignal(automaticSelected);
        ApplyMode(_includeManualButton, state.ManualOverlay);
        _startButton.Disabled = !state.Commands.Start;
        _stopButton.Disabled = !state.Commands.Stop;
        _copyButton.Disabled = !state.Commands.Copy;
        _exportButton.Disabled = !state.Commands.Export;
        _settingsLabel.Text = string.IsNullOrEmpty(state.InstallerStatus)
            ? ""
            : state.InstallerStatus;
        _qualityLabel.Text = state.QualityBanner;
        _samplingSettings.Visible = samplingSelected;
        _automaticSettings.Visible = automaticSelected;
        _manualSettings.Visible = manualSelected || state.ManualOverlay.Selected;
        _previewDiff.Text = state.InstallerPreviewDiff;
        if (string.IsNullOrEmpty(state.InstallerPreviewDiff))
        {
            _pendingPreviewToken = null;
            _applyInstallButton.Disabled = true;
        }
        RenderResultGroups(state.ResultGroups);
        ApplyTimeline(state.Timeline);
        ApplyResponsiveLayout();
    }

            private void ApplyTimeline(CaptureTimeline timeline)
    {
        _protocolTimeline = timeline ?? CaptureTimeline.Empty;
        _graph?.SetTimeline(_protocolTimeline);
        _updatingSelector = true;
        _frameSelector.MaxValue = Math.Max(0, _protocolTimeline.Points.Count - 1);
        _updatingSelector = false;
        if (_protocolTimeline.Points.Count == 0)
        {
            _selectedIndex = -1;
            _graph?.SetSelectedIndex(-1);
            return;
        }
        if (_liveFollow || _selectedIndex < 0 || _selectedIndex >= _protocolTimeline.Points.Count)
            _selectedIndex = _protocolTimeline.Points.Count - 1;
        SelectFrame(_selectedIndex);
    }

        private void RenderResultGroups(IReadOnlyList<ResultGroupViewState> groups)
    {
        if (_resultTabs == null) return;
        _protocolResultRowsForTests = groups.Where(item => !item.IsCrossSourceTotal).Sum(item => item.Rows.Count);
        _samplingResultRowsForTests = groups.Where(item => !item.IsCrossSourceTotal &&
            item.Source == CaptureSource.Sampling).Sum(item => item.Rows.Count);
        foreach (var child in _resultTabs.GetChildren())
            if (!ReferenceEquals(child, _tree) && !ReferenceEquals(child, _selectedBatchTree)) child.QueueFree();
        _tree.Visible = _frames.Count > 0;
        _selectedBatchTree.Visible = _protocolTimeline.Points.Count > 0;
        SyncGroupControls();
        foreach (var group in groups.Where(item => !item.IsCrossSourceTotal))
        {
            var tree = new Tree { Name = group.Title, Columns = group.Columns.Count,
                ColumnTitlesVisible = true, HideRoot = true, SizeFlagsVertical = SizeFlags.ExpandFill };
            for (var column = 0; column < group.Columns.Count; column++)
                tree.SetColumnTitle(column, group.Columns[column]);
            var root = tree.CreateItem();
            foreach (var row in group.Rows)
            {
                var item = tree.CreateItem(root);
                item.SetText(0, row.Name);
                if (group.Source == CaptureSource.Sampling)
                {
                    item.SetText(1, row.Samples.ToString());
                    item.SetText(2, $"{row.EstimatedStackFrameShare:0.##}%");
                }
                else
                {
                    item.SetText(1, $"{row.ObservedWallTimeMilliseconds:0.###} ms");
                    item.SetText(2, row.Calls.ToString());
                    item.SetText(3, $"{row.AverageWallTimeMilliseconds:0.###} ms");
                    item.SetText(4, $"{row.MaximumWallTimeMilliseconds:0.###} ms");
                }
            }
            _resultTabs.AddChild(tree);
        }
        _resultTabs.Visible = groups.Count > 0 || _frames.Count > 0 || _protocolTimeline.Points.Count > 0;
    }

    private static void ApplyMode(BaseButton button, ToggleViewState state)
    {
        if (button == null || state == null) return;
        button.SetPressedNoSignal(state.Selected);
        button.Disabled = !state.Enabled;
        button.TooltipText = state.Tooltip;
    }

    public void Copy(ExportFormat format, ProfilerResults results)
    {
        var text = SerializeResults(results, format);
        if (text.Length > 0) DisplayServer.ClipboardSet(text);
    }

    public void Export(ExportFormat format, ProfilerResults results)
    {
        var text = SerializeResults(results, format);
        if (text.Length == 0) return;
        var path = ProjectSettings.GlobalizePath("res://cs-profiler-export." +
            (format == ExportFormat.VisibleCsv ? "csv" : "json"));
        File.WriteAllText(path, text);
        _controller.ReportStatus("Exported source-separated results to " + path);
    }

    private static string SerializeResults(ProfilerResults results, ExportFormat format)
    {
        var builder = new StringBuilder();
        foreach (var group in results.Groups)
        {
            builder.AppendLine(group.Source.ToString());
            builder.AppendLine(group.Source == CaptureSource.Sampling
                ? "Name,Samples,Estimated stack-frame %"
                : "Name,Wall time ms,Calls,Average wall time ms,Maximum wall time ms");
            foreach (var row in group.Rows)
            {
                builder.Append(row.Name.Replace(',', ';')).Append(',');
                if (group.Source == CaptureSource.Sampling)
                    builder.Append(row.Samples).Append(',').Append(row.EstimatedStackFrameShare);
                else
                    builder.Append(row.ObservedWallTimeMilliseconds).Append(',').Append(row.Calls)
                        .Append(',').Append(row.AverageWallTimeMilliseconds).Append(',')
                        .Append(row.MaximumWallTimeMilliseconds);
                builder.AppendLine();
            }
        }
        return builder.ToString();
    }

            public void Send(ProfilerCommand command, ModeConfiguration configuration)
    {
        _ = configuration;
        if (command == ProfilerCommand.CancelPending)
        {
            _profilingRequested = false;
            ProfilingToggled?.Invoke(false);
            return;
        }
        var start = command == ProfilerCommand.Start;
        _profilingRequested = start;
        _liveFollow = start;
        if (!start) _controller?.UpdateTimeline(_protocolTimeline);
        ProfilingToggled?.Invoke(start);
    }

    public void InitializeSessionState(bool active)
    {
        if (active)
        {
            _sessionActive = true;
            _discovery.OnSessionStarted();
        }
        else
        {
            OnSessionStopped();
        }
    }

    public void OnSessionStarted()
    {
        _sessionActive = true;
        if (!_discovery.OnSessionStarted())
            return;
        _runtimeDescription = "";
        ClearHistory();
        if (ProfilingRequested)
            _liveFollow = true;
        TryRequestDiscovery(NowSec());
    }

    public void OnSessionStopped()
    {
        // Keep completed history around for post-mortem scrubbing; disconnect never clears data.
        _sessionActive = false;
        _profilingRequested = false;
        if (!_discovery.OnSessionStopped())
            return;
        _runtimeDescription = "";
        _controller?.Disconnected("Target disconnected — completed result preserved.");
    }

    internal void ReportDebuggerPayloadError(string status) =>
        _controller?.ReportStatus(status);

    internal void ApplyProtocolSnapshot(CaptureSnapshot snapshot, CsProfilerRuntimeIdentity identity) =>
        _controller?.UpdateSnapshot(snapshot, FormatRuntimeDescription(identity));

    internal void ApplyProtocolResults(ProfilerResults results) =>
        _controller?.ReplaceResults(results);

    internal void ApplyProtocolTimeline(CaptureTimeline timeline) =>
        _controller?.UpdateTimeline(timeline);

    internal void ApplyProtocolTerminalCapture(ProfilerTerminalCapture capture) =>
        _controller?.ReplaceTerminalCapture(capture);

    internal void OnBridgeReady(CsProfilerRuntimeIdentity identity)
    {
        _sessionActive = true;
        var runtimeChanged = _discovery.AcceptReady(identity);
        if (runtimeChanged && _frames.Count > 0)
            ClearHistory();
        ApplyBridgeReadyUi(identity);
    }

    private void ApplyBridgeReadyUi(CsProfilerRuntimeIdentity identity)
    {
        _runtimeDescription = FormatRuntimeDescription(identity);
        if (_frames.Count == 0 && _statsLabel != null)
        {
            _statsLabel.Text = ProfilingRequested || identity.Capturing
                ? RuntimeStatus("Bridge ready; capturing...")
                : RuntimeStatus("Bridge ready. Press Start to capture.");
        }
    }

        public override void _Process(double delta)
    {
        _ = delta;
        if (_statsLabel == null) return;
        var now = NowSec();
        var active = SessionActive;
        if (active && !_discovery.SessionActive)
            _discovery.OnSessionStarted();
        else if (!active && _discovery.SessionActive)
            _discovery.OnSessionStopped();
        TryRequestDiscovery(now);
    }

    private void TryRequestDiscovery(double nowSeconds)
    {
        if (!_discovery.TryScheduleDiscovery(nowSeconds))
            return;
        DiscoveryRequested?.Invoke();
        if (_statsLabel != null && !ProfilingRequested)
            _statsLabel.Text = "Looking for the C# profiler bridge in this active session...";
    }

    private string RuntimeStatus(string status) => string.IsNullOrEmpty(_runtimeDescription)
        ? status
        : $"{_runtimeDescription} | {status}";

    private static string FormatRuntimeDescription(CsProfilerRuntimeIdentity identity)
    {
        var location = identity.EditorAttached ? "editor play" : identity.Role;
        var process = identity.ProcessId > 0 ? $"PID {identity.ProcessId}" : "PID unknown";
        return $"{identity.DisplayName} ({location}, {process})";
    }

    private static double NowSec() => Time.GetTicksMsec() / 1000.0;

    private void ClearAllResults()
    {
        _controller?.Clear();
        ClearHistory();
    }

        private void ClearHistory()
    {
        if (_graph == null)
            return; // session signal arrived before _Ready built the controls
        _frames.Clear();
        _protocolTimeline = CaptureTimeline.Empty;
        _selectedIndex = -1;
        _liveFollow = true;
        _graph.SetTimeline(_protocolTimeline);
        _graph.SetSelectedIndex(-1);
        _tree?.Clear();
        _selectedBatchTree?.Clear();
        if (_selectedBatchTree != null) _selectedBatchTree.Visible = false;
        SyncGroupControls();
        if (_copyButton != null)
            _copyButton.Disabled = true;
        _updatingSelector = true;
        _frameSelector.MaxValue = 0;
        _frameSelector.Value = 0;
        _updatingSelector = false;
        _statsLabel.Text = _discovery.BridgeReady
            ? RuntimeStatus("No data. Press Start while the game is running.")
            : "No data. Press Start while the game is running.";
    }

    public void IngestFrame(Godot.Collections.Array data)
    {
        if (_graph == null)
            return;
        if (!TryParseFrame(data, out var frame, out var error))
        {
            _tree.Clear();
            _displayedRowsForTests.Clear();
            _statsLabel.Text = RuntimeStatus(ProfilerDockController.SafeText(
                "Profiler data error: " + error, 160, "Profiler data error"));
            return;
        }
        _lastFrameAtSec = NowSec();
        _frames.Add(frame);
        if (_frames.Count > MaxFrames)
        {
            var trimmed = _frames.Count - MaxFrames;
            _frames.RemoveRange(0, trimmed);
            if (_selectedIndex >= 0)
                _selectedIndex = Math.Max(0, _selectedIndex - trimmed);
        }

        _updatingSelector = true;
        _frameSelector.MaxValue = _frames.Count - 1;
        _updatingSelector = false;

        _graph.SetFrames(_frames);
        if (_liveFollow)
            SelectFrame(_frames.Count - 1);
        else
            _graph.SetSelectedIndex(_selectedIndex);
    }

    private void OnFrameSelectorChanged(double value)
    {
        if (_updatingSelector)
            return;
        _liveFollow = false;
        SelectFrame((int)value);
    }

        private void SelectFrame(int index)
    {
        if (_protocolTimeline.Points.Count > 0)
        {
            index = Math.Clamp(index, 0, _protocolTimeline.Points.Count - 1);
            _selectedIndex = index;
            _graph.SetSelectedIndex(index);
            _updatingSelector = true;
            _frameSelector.Value = index;
            _updatingSelector = false;
            var point = _protocolTimeline.Points[index];
            RenderSelectedBatch(point);
            _statsLabel.Text = point.Source == CaptureSource.Sampling
                ? RuntimeStatus($"{point.Value} statistical samples | {point.Rows.Count} methods | batch #{point.Sequence}")
                : RuntimeStatus($"{point.Value / 1_000_000.0:0.###} ms observed exact-span time | " +
                    $"{point.Observations} calls | batch #{point.Sequence}");
            return;
        }

        if (_frames.Count == 0) return;
        index = Math.Clamp(index, 0, _frames.Count - 1);
        _selectedIndex = index;
        _graph.SetSelectedIndex(index);
        _updatingSelector = true;
        _frameSelector.Value = index;
        _updatingSelector = false;

        var frame = _frames[index];
        if (_copyButton != null) _copyButton.Disabled = frame.Names.Length == 0;
        if (frame.Names.Length == 0)
        {
            _statsLabel.Text = RuntimeStatus(
                $"No samples | C# {frame.CsMs:0.00} ms | frame {frame.FrameMs:0.00} ms | frame #{frame.Index}");
            RebuildTree(frame);
            return;
        }
        _statsLabel.Text = RuntimeStatus(
            $"C# {frame.CsMs:0.00} ms | frame {frame.FrameMs:0.00} ms | " +
            $"{frame.Names.Length} scopes | frame #{frame.Index}");
        _controller?.ReplaceResults(new ProfilerResults([
            new SourceResultGroup(CaptureSource.ManualSpans,
                frame.Names.Select((name, row) => new ResultRow(name, 0, 0,
                    frame.Calls[row], frame.TotalUsec[row] / 1000.0,
                    frame.Calls[row] > 0 ? frame.TotalUsec[row] / 1000.0 / frame.Calls[row] : 0,
                    frame.TotalUsec[row] / 1000.0)).ToArray())
        ], 0));
        RebuildTree(frame);
    }

    private void RenderSelectedBatch(CaptureTimelinePoint point)
    {
        if (_selectedBatchTree == null) return;
        _selectedTimelinePoint = point;
        if (point.FlushFrame is { } flush)
        {
            var milliseconds = flush.ElapsedNanoseconds / 1_000_000.0;
            _performanceLabel.Text = $"Flush frame {flush.ProcessFrame} · {milliseconds:0.00} ms · " +
                $"{1_000_000_000.0 / flush.ElapsedNanoseconds:0} FPS";
        }
        else
            _performanceLabel.Text = "Flush-frame timing unavailable";
        _selectedBatchTree.Clear();
        var sampling = point.Source == CaptureSource.Sampling;
        _selectedBatchTree.Columns = sampling ? 3 : 5;
        _selectedBatchTree.SetColumnTitle(0, "Function");
        _selectedBatchTree.SetColumnTitle(1, sampling ? "Samples" : "Wall time");
        _selectedBatchTree.SetColumnTitle(2, sampling ? "Share" : "Calls");
        if (!sampling)
        {
            _selectedBatchTree.SetColumnTitle(3, "Average");
            _selectedBatchTree.SetColumnTitle(4, "Maximum");
        }
        var root = _selectedBatchTree.CreateItem();
        foreach (var group in GroupTimelineRows(point.Rows))
        {
            var parent = _selectedBatchTree.CreateItem(root);
            parent.SetText(0, group.Name);
            parent.SetMetadata(0, GroupMetadataPrefix + group.Name);
            parent.Collapsed = !_expandedGroups.Contains(group.Name);
            if (sampling)
            {
                parent.SetText(1, group.Samples.ToString());
                parent.SetText(2, $"{group.Share:0.##}%");
            }
            else
            {
                parent.SetText(1, $"{group.WallMilliseconds:0.###} ms");
                parent.SetText(2, group.Calls.ToString());
                parent.SetText(3, group.Calls > 0 ? $"{group.WallMilliseconds / group.Calls:0.###} ms" : "");
                parent.SetText(4, $"{group.MaximumMilliseconds:0.###} ms");
            }
            foreach (var row in group.Rows)
            {
                var item = _selectedBatchTree.CreateItem(parent);
                item.SetText(0, MemberDisplayName(group.Name, row.Name));
                item.SetMetadata(0, row.Name);
                if (sampling)
                {
                    item.SetText(1, row.Samples.ToString());
                    item.SetText(2, $"{row.EstimatedStackFrameShare:0.##}%");
                }
                else
                {
                    item.SetText(1, $"{row.ObservedWallTimeMilliseconds:0.###} ms");
                    item.SetText(2, row.Calls.ToString());
                    item.SetText(3, $"{row.AverageWallTimeMilliseconds:0.###} ms");
                    item.SetText(4, $"{row.MaximumWallTimeMilliseconds:0.###} ms");
                }
            }
        }
        _selectedBatchTree.Visible = true;
        _resultTabs.CurrentTab = _selectedBatchTree.GetIndex();
        SyncGroupControls();
    }

    // Expand/collapse-all only act on the grouped batch tree; keep them inert (not hidden, to avoid
    // toolbar reflow) whenever no grouped rows exist.
    private void SyncGroupControls()
    {
        var hasGroups = _selectedBatchTree?.GetRoot()?.GetChildCount() > 0;
        if (_expandAllButton != null) _expandAllButton.Disabled = !hasGroups;
        if (_collapseAllButton != null) _collapseAllButton.Disabled = !hasGroups;
    }

    private void OnSelectedBatchGuiInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Right } mouse)
        {
            var item = _selectedBatchTree.GetItemAtPosition(mouse.Position);
            if (item != null) item.Select(0);
            _callContextMenu.Position = DisplayServer.MouseGetPosition();
            _callContextMenu.Popup();
            _selectedBatchTree.AcceptEvent();
        }
        else if (inputEvent is InputEventKey { Pressed: true, Echo: false, Keycode: Key.C } key &&
                 (key.CtrlPressed || key.MetaPressed))
        {
            CopyTimelineRows(SelectedTimelineNames());
            _selectedBatchTree.AcceptEvent();
        }
    }

    private void OnCallContextAction(long id) => CopyTimelineRows(SelectedTimelineNames());

    private IReadOnlySet<string> SelectedTimelineNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (_selectedBatchTree == null) return names;
        TreeItem item = null;
        while ((item = _selectedBatchTree.GetNextSelected(item)) != null)
        {
            var name = item.GetMetadata(0).AsString();
            if (string.IsNullOrEmpty(name)) continue;
            if (name.StartsWith(GroupMetadataPrefix, StringComparison.Ordinal))
            {
                for (var child = item.GetFirstChild(); child != null; child = child.GetNext())
                {
                    var member = child.GetMetadata(0).AsString();
                    if (!string.IsNullOrEmpty(member)) names.Add(member);
                }
            }
            else names.Add(name);
        }
        return names;
    }

    private const string GroupMetadataPrefix = "group:";
    private HashSet<string> _expandedGroups = new(StringComparer.Ordinal);

    private sealed record TimelineRowGroup(string Name, long Samples, double Share, long Calls,
        double WallMilliseconds, double MaximumMilliseconds, IReadOnlyList<ResultRow> Rows);

    internal static string GroupNameForCall(string callName)
    {
        var open = callName.IndexOf('(');
        var scope = open >= 0 ? callName[..open] : callName;
        var split = scope.LastIndexOf('.');
        return split > 0 ? scope[..split] : callName;
    }

    private static string MemberDisplayName(string groupName, string callName) =>
        callName.Length > groupName.Length + 1 &&
        callName.StartsWith(groupName + ".", StringComparison.Ordinal)
            ? callName[(groupName.Length + 1)..]
            : callName;

    private static IReadOnlyList<TimelineRowGroup> GroupTimelineRows(IReadOnlyList<ResultRow> rows) =>
        rows.Select((row, order) => (Row: row, Order: order))
            .GroupBy(entry => GroupNameForCall(entry.Row.Name), StringComparer.Ordinal)
            .OrderBy(group => group.Min(entry => entry.Order))
            .Select(group => new TimelineRowGroup(group.Key,
                group.Sum(entry => entry.Row.Samples),
                group.Sum(entry => entry.Row.EstimatedStackFrameShare),
                group.Sum(entry => entry.Row.Calls),
                group.Sum(entry => entry.Row.ObservedWallTimeMilliseconds),
                group.Max(entry => entry.Row.MaximumWallTimeMilliseconds),
                group.Select(entry => entry.Row).ToArray()))
            .ToArray();

    private void SetAllGroupsCollapsed(bool collapsed)
    {
        var root = _selectedBatchTree?.GetRoot();
        if (root == null) return;
        for (var group = root.GetFirstChild(); group != null; group = group.GetNext())
        {
            group.Collapsed = collapsed;
            var name = group.GetMetadata(0).AsString();
            if (!name.StartsWith(GroupMetadataPrefix, StringComparison.Ordinal)) continue;
            var groupName = name[GroupMetadataPrefix.Length..];
            if (collapsed) _expandedGroups.Remove(groupName);
            else _expandedGroups.Add(groupName);
        }
    }

    private void OnExpandAllPressed() => SetAllGroupsCollapsed(false);

    private void OnCollapseAllPressed() => SetAllGroupsCollapsed(true);

    private void OnBatchGroupCollapsedToggled(TreeItem item)
    {
        var name = item?.GetMetadata(0).AsString() ?? "";
        if (!name.StartsWith(GroupMetadataPrefix, StringComparison.Ordinal)) return;
        var groupName = name[GroupMetadataPrefix.Length..];
        if (item.Collapsed) _expandedGroups.Remove(groupName);
        else _expandedGroups.Add(groupName);
    }

    internal void UpdateInstanceOptions(IReadOnlyList<CsProfilerInstanceOption> instances, int selectedSessionId)
    {
        if (_instanceSelector == null) return;
        _updatingInstanceSelector = true;
        _instanceSelector.Clear();
        var selectedIndex = -1;
        for (var index = 0; index < instances.Count; index++)
        {
            _instanceSelector.AddItem(instances[index].Label, instances[index].SessionId);
            _instanceSelector.SetItemMetadata(index, instances[index].SessionId);
            if (instances[index].SessionId == selectedSessionId) selectedIndex = index;
        }
        if (selectedIndex >= 0) _instanceSelector.Selected = selectedIndex;
        _instanceSelector.Visible = instances.Count > 1;
        _updatingInstanceSelector = false;
    }

    private void OnInstanceSelectorChanged(long index)
    {
        if (_updatingInstanceSelector || _instanceSelector == null || index < 0) return;
        InstanceSelected?.Invoke(_instanceSelector.GetItemMetadata((int)index).AsInt32());
    }

    private void CopyTimelineRows(IReadOnlySet<string> selectedNames)
    {
        if (_selectedTimelinePoint == null) return;
        var report = BuildTimelineReport(_selectedTimelinePoint, selectedNames);
        if (report.Length == 0) return;
        DisplayServer.ClipboardSet(report);
        _statsLabel.Text = RuntimeStatus(selectedNames.Count == 0
            ? $"Copied batch #{_selectedTimelinePoint.Sequence}"
            : $"Copied {selectedNames.Count} call{(selectedNames.Count == 1 ? "" : "s")}");
    }

    private static string BuildTimelineReport(CaptureTimelinePoint point, IReadOnlySet<string> selectedNames)
    {
        var rows = selectedNames.Count == 0
            ? point.Rows
            : point.Rows.Where(row => selectedNames.Contains(row.Name)).ToArray();
        if (rows.Count == 0) return "";
        var sampling = point.Source == CaptureSource.Sampling;
        var builder = new StringBuilder(256);
        foreach (var row in rows)
        {
            builder.Append(row.Name);
            if (sampling)
                builder.Append(" | ").Append(row.Samples).Append(" samples | ")
                    .Append(row.EstimatedStackFrameShare.ToString("0.##")).AppendLine("%");
            else
                builder.Append(" | ").Append(row.ObservedWallTimeMilliseconds.ToString("0.###"))
                    .Append(" ms | ").Append(row.Calls).AppendLine(row.Calls == 1 ? " call" : " calls");
        }
        return builder.ToString();
    }

    private void ExportTimeline()
    {
        if (_protocolTimeline.Points.Count == 0) return;
        var path = ProjectSettings.GlobalizePath("res://cs-profiler-timeline.json");
        File.WriteAllText(path, JsonSerializer.Serialize(_protocolTimeline,
            new JsonSerializerOptions { WriteIndented = true }));
        _controller.ReportStatus("Exported captured timeline to " + path);
    }

    private sealed class DisplayNode
    {
        public int Index;
        public long SelfUsec;
        public readonly List<DisplayNode> Children = new();
    }

    private void RebuildTree(ProfileFrame frame)
    {
        _rebuildingTree = true;
        _tree.Clear();
        _displayedRowsForTests.Clear();
        var root = _tree.CreateItem();

        // Reconstruct the hierarchy from the pre-order (depth, ...) arrays. Self time is total
        // minus direct children; the runtime intentionally leaves that to this side.
        var virtualRoot = BuildDisplayTree(frame);

        foreach (var child in virtualRoot.Children.OrderByDescending(c => frame.TotalUsec[c.Index]))
            PopulateItem(root, child, frame, "");
        _rebuildingTree = false;
    }

    private void PopulateItem(TreeItem parent, DisplayNode node, ProfileFrame frame, string parentPath)
    {
        var name = frame.Names[node.Index];
        var path = parentPath.Length == 0 ? name : parentPath + "/" + name;
        var item = _tree.CreateItem(parent);
        item.SetText(0, name);
        item.SetTooltipText(0, path);
        item.SetMetadata(0, path);
        item.SetText(1, FormatMs(frame.TotalUsec[node.Index]));
        item.SetText(2, FormatMs(Math.Max(0, node.SelfUsec)));
        item.SetText(3, frame.Calls[node.Index].ToString());
        _displayedRowsForTests.Add(
            $"{name}|{FormatMs(frame.TotalUsec[node.Index])}|" +
            $"{FormatMs(Math.Max(0, node.SelfUsec))}|{frame.Calls[node.Index]}");
        for (var column = 1; column < 4; column++)
            item.SetTextAlignment(column, HorizontalAlignment.Right);
        item.Collapsed = _collapsedPaths.Contains(path);

        foreach (var child in node.Children.OrderByDescending(c => frame.TotalUsec[c.Index]))
            PopulateItem(item, child, frame, path);
    }

    private static string FormatMs(long usec) => $"{usec / 1000.0:0.000} ms";

    private void OnTreeGuiInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventKey
            {
                Pressed: true,
                Echo: false,
                Keycode: Key.C
            } key || (!key.CtrlPressed && !key.MetaPressed))
        {
            return;
        }
        CopySelectedCalls();
        _tree.AcceptEvent();
    }

    private void CopySelectedCalls()
    {
        if (_selectedIndex < 0 || _selectedIndex >= _frames.Count)
            return;
        var selectedPath = _tree?.GetSelected()?.GetMetadata(0).Obj as string ?? "";
        var report = BuildCallReport(_frames[_selectedIndex], selectedPath);
        if (report.Length == 0)
            return;
        DisplayServer.ClipboardSet(report);
        _statsLabel.Text = RuntimeStatus(
            $"Copied {(selectedPath.Length == 0 ? "frame call tree" : selectedPath)} | " +
            $"frame #{_frames[_selectedIndex].Index}");
    }

    private static string BuildCallReport(ProfileFrame frame, string selectedPath)
    {
        if (frame?.Names == null || frame.Names.Length == 0)
            return "";
        var root = BuildDisplayTree(frame);
        var builder = new StringBuilder(512);
        builder.AppendLine("C# Profiler call report")
            .Append("frame #").Append(frame.Index)
            .Append(" | frame ").Append(frame.FrameMs.ToString("0.000"))
            .Append(" ms | C# ").Append(frame.CsMs.ToString("0.000"))
            .AppendLine(" ms")
            .Append("selection: ").AppendLine(
                string.IsNullOrEmpty(selectedPath) ? "complete frame" : selectedPath)
            .AppendLine("Name\tTotal\tSelf\tCalls");
        var foundSelection = false;
        foreach (var child in root.Children.OrderByDescending(node => frame.TotalUsec[node.Index]))
        {
            AppendCallReportNode(
                builder, child, frame, "", selectedPath, 0, false, ref foundSelection);
        }
        return string.IsNullOrEmpty(selectedPath) || foundSelection ? builder.ToString() : "";
    }

    private static DisplayNode BuildDisplayTree(ProfileFrame frame)
    {
        var virtualRoot = new DisplayNode { Index = -1 };
        var stack = new List<DisplayNode> { virtualRoot };
        for (var index = 0; index < frame.Names.Length; index++)
        {
            var depth = frame.Depths[index];
            if (depth + 1 > stack.Count)
                continue;
            stack.RemoveRange(depth + 1, stack.Count - depth - 1);
            var node = new DisplayNode { Index = index, SelfUsec = frame.TotalUsec[index] };
            stack[depth].Children.Add(node);
            if (depth > 0)
                stack[depth].SelfUsec -= frame.TotalUsec[index];
            stack.Add(node);
        }
        return virtualRoot;
    }

    private static void AppendCallReportNode(
        StringBuilder builder,
        DisplayNode node,
        ProfileFrame frame,
        string parentPath,
        string selectedPath,
        int depth,
        bool insideSelection,
        ref bool foundSelection)
    {
        var name = frame.Names[node.Index];
        var path = parentPath.Length == 0 ? name : parentPath + "/" + name;
        var isTarget = path == selectedPath;
        var selected = string.IsNullOrEmpty(selectedPath) || insideSelection || isTarget;
        if (isTarget)
        {
            foundSelection = true;
            depth = 0;
        }
        if (selected)
        {
            builder.Append(' ', depth * 2).Append(name).Append('\t')
                .Append(FormatMs(frame.TotalUsec[node.Index])).Append('\t')
                .Append(FormatMs(Math.Max(0, node.SelfUsec))).Append('\t')
                .AppendLine(frame.Calls[node.Index].ToString());
        }
        foreach (var child in node.Children.OrderByDescending(item => frame.TotalUsec[item.Index]))
        {
            AppendCallReportNode(
                builder, child, frame, path, selectedPath,
                selected ? depth + 1 : depth,
                insideSelection || isTarget,
                ref foundSelection);
        }
    }

    private static bool TryParseFrame(
        Godot.Collections.Array data,
        out ProfileFrame frame,
        out string error)
    {
        frame = null;
        error = "payload is missing required fields";
        if (data == null || data.Count != 7)
            return false;
        try
        {
            if (data[0].VariantType != Variant.Type.Int ||
                data[1].VariantType != Variant.Type.Int ||
                data[2].VariantType != Variant.Type.Int ||
                data[3].VariantType != Variant.Type.PackedStringArray ||
                data[4].VariantType != Variant.Type.PackedInt32Array ||
                data[5].VariantType != Variant.Type.PackedInt64Array ||
                data[6].VariantType != Variant.Type.PackedInt64Array)
            {
                error = "payload field types are invalid";
                return false;
            }
            var index = data[0].AsInt64();
            var frameUsec = data[1].AsInt64();
            var csUsec = data[2].AsInt64();
            var names = data[3].AsStringArray();
            var depths = data[4].AsInt32Array();
            var calls = data[5].AsInt64Array();
            var totals = data[6].AsInt64Array();
            if (index < 0 || frameUsec < 0 || csUsec < 0)
            {
                error = "frame counters must be nonnegative";
                return false;
            }
            if (names == null || depths == null || calls == null || totals == null ||
                depths.Length != names.Length || calls.Length != names.Length ||
                totals.Length != names.Length)
            {
                error = "scope arrays have mismatched lengths";
                return false;
            }
            if (names.Length > MaxScopeNodes)
            {
                error = "scope count exceeds the bounded snapshot limit";
                return false;
            }
            for (var i = 0; i < names.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(names[i]) || calls[i] < 0 || totals[i] < 0 ||
                    depths[i] < 0 || depths[i] >= MaxScopeDepth ||
                    (i == 0 && depths[i] != 0) ||
                    (i > 0 && depths[i] > depths[i - 1] + 1))
                {
                    error = "scope hierarchy or counters are malformed";
                    return false;
                }
            }

            // Inclusive time must cover the sum of each node's direct children so self time is
            // meaningful rather than silently clamped from a malformed or stale packet.
            var directChildren = new long[names.Length];
            var ancestors = new List<int>();
            for (var i = 0; i < names.Length; i++)
            {
                while (ancestors.Count > depths[i])
                    ancestors.RemoveAt(ancestors.Count - 1);
                if (depths[i] > 0)
                {
                    var parent = ancestors[depths[i] - 1];
                    if (totals[i] > long.MaxValue - directChildren[parent])
                    {
                        error = "direct child time sum overflowed";
                        return false;
                    }
                    directChildren[parent] += totals[i];
                }
                if (directChildren[i] > totals[i])
                {
                    error = "child time exceeds parent inclusive time";
                    return false;
                }
                ancestors.Add(i);
            }
            for (var i = 0; i < names.Length; i++)
            {
                if (directChildren[i] <= totals[i])
                    continue;
                error = "child time exceeds parent inclusive time";
                return false;
            }

            frame = new ProfileFrame
            {
                Index = index,
                FrameMs = frameUsec / 1000.0,
                CsMs = csUsec / 1000.0,
                Names = names,
                Depths = depths,
                Calls = calls,
                TotalUsec = totals
            };
            error = "";
            return true;
        }
        catch (Exception exception)
        {
            error = "payload types are invalid (" + exception.GetType().Name + ")";
            return false;
        }
    }

    private void OnItemCollapsed(TreeItem item)
    {
        if (_rebuildingTree)
            return;
        if (item.GetMetadata(0).Obj is not string path)
            return;
        if (item.Collapsed)
            _collapsedPaths.Add(path);
        else
            _collapsedPaths.Remove(path);
    }
}
#else
public partial class CsProfilerPanel : Godot.Control { }
#endif
