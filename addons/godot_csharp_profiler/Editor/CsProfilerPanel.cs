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

// The single "C# Profiler" bottom panel: Start/Stop toolbar, clickable frame-time graph, and the
// selected frame's call tree with Total/Self/Calls columns. Frames arrive from
// CsProfilerBridge via CsProfilerDebuggerPlugin (see the bridge for the message layout).
[Tool]
public partial class CsProfilerPanel : VBoxContainer, IProfilerDockView, IProfilerCommandTransport, IProfilerOutput
{
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

    public event Action<bool> ProfilingToggled;
    public event Action DiscoveryRequested;
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

    private readonly List<ProfileFrame> _frames = new();
    private readonly HashSet<string> _collapsedPaths = new(StringComparer.Ordinal);
    private Button _startButton;
    private Button _stopButton;
    private Button _copyButton;
    private Button _exportButton;
    private Button _settingsButton;
    private Button _samplingModeButton;
    private Button _automaticModeButton;
    private Button _manualModeButton;
    private CheckButton _includeManualButton;
    private SpinBox _frameSelector;
    private Label _targetLabel;
    private Label _statsLabel;
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
    private Window _settingsWindow;
    private TabContainer _resultTabs;
    private CsProfilerFrameGraph _graph;
    private Tree _tree;
    private int _selectedIndex = -1;
    private bool _liveFollow = true;
    private bool _updatingSelector;
    private bool _rebuildingTree;
    private readonly List<string> _displayedRowsForTests = new();
    private int _protocolResultRowsForTests;
    private int _samplingResultRowsForTests;
    private bool _sessionActive;
    private readonly CsProfilerSessionDiscoveryState _discovery = new();
    private string _runtimeDescription = "";
    private double _lastFrameAtSec = double.NegativeInfinity;
    private bool _profilingRequested;
    private ProfilerDockController _controller;
    internal ModeConfiguration ConfigurationForProtocol => _controller?.Configuration ?? ModeConfiguration.Default;
    internal string StatusTextForTests => _statsLabel?.Text ?? "";
    internal string[] DisplayedRowsForTests() => _displayedRowsForTests.ToArray();
    internal int FrameCountForTests => _frames.Count;
    internal int ProtocolResultRowsForTests => _protocolResultRowsForTests;
    internal int SamplingResultRowsForTests => _samplingResultRowsForTests;
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
        SizeFlagsVertical = SizeFlags.ExpandFill;
        CustomMinimumSize = Vector2.Zero;
        _controller = new ProfilerDockController(this, this, CreateInstallerSafely(), this);

        var header = new HBoxContainer();
        AddChild(header);
        _targetLabel = new Label
        {
            Text = "No target",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
            TooltipText = "Current debug target"
        };
        header.AddChild(_targetLabel);
        _statsLabel = new Label
        {
            Text = "Disconnected",
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis
        };
        header.AddChild(_statsLabel);
        _settingsButton = new Button
        {
            Text = "⋮",
            TooltipText = "Profiler settings, advanced modes, export, and diagnostics",
            CustomMinimumSize = new Vector2(32, 0)
        };
        _settingsButton.Pressed += ShowSettings;
        header.AddChild(_settingsButton);

        var toolbar = new HBoxContainer();
        AddChild(toolbar);
        _startButton = new Button { Text = "Start", TooltipText = "Start statistical sampling" };
        _startButton.Pressed += () =>
        {
            if (_controller.RequestStart()) _profilingRequested = true;
        };
        toolbar.AddChild(_startButton);
        _stopButton = new Button { Text = "Stop" };
        _stopButton.Pressed += () =>
        {
            if (_controller.Stop()) _profilingRequested = false;
        };
        toolbar.AddChild(_stopButton);

        var clearButton = new Button { Text = "Clear" };
        clearButton.Pressed += ClearAllResults;
        toolbar.AddChild(clearButton);
        // Copy/export are advanced actions and belong in the compact settings window.
        _copyButton = new Button { Text = "Copy Results", Disabled = true,
            TooltipText = "Copy visible source-separated results" };
        _copyButton.Pressed += () => _controller.Copy(ExportFormat.VisibleCsv);
        _exportButton = new Button { Text = "Export Results", Disabled = true,
            TooltipText = "Export lossless source-separated JSON" };
        _exportButton.Pressed += () => _controller.Export(ExportFormat.LosslessJson);

        toolbar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        toolbar.AddChild(new Label { Text = "Frame" });
        _frameSelector = new SpinBox
        {
            MinValue = 0,
            MaxValue = 0,
            Rounded = true,
            CustomMinimumSize = new Vector2(72, 0)
        };
        _frameSelector.ValueChanged += OnFrameSelectorChanged;
        toolbar.AddChild(_frameSelector);

        _graph = new CsProfilerFrameGraph { SizeFlagsVertical = SizeFlags.ShrinkBegin };
        _graph.FrameClicked += index =>
        {
            _liveFollow = false;
            SelectFrame(index);
        };
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
        _tree.ItemCollapsed += OnItemCollapsed;
        _tree.GuiInput += OnTreeGuiInput;
        _resultTabs.AddChild(_tree);

        BuildSettingsUi();
        Resized += ApplyResponsiveLayout;
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
        _settingsWindow = new Window
        {
            Title = "C# Profiler Settings",
            Size = new Vector2I(560, 560),
            MinSize = new Vector2I(320, 240),
            Transient = true,
            Exclusive = false
        };
        _settingsWindow.CloseRequested += _settingsWindow.Hide;
        AddChild(_settingsWindow);
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        _settingsWindow.AddChild(scroll);
        var settingsRoot = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(settingsRoot);

        var outputCommands = new HBoxContainer();
        outputCommands.AddChild(_copyButton);
        outputCommands.AddChild(_exportButton);
        settingsRoot.AddChild(outputCommands);

        settingsRoot.AddChild(new Label { Text = "Capture mode" });
        var modeBar = new HBoxContainer();
        settingsRoot.AddChild(modeBar);
        _samplingModeButton = ModeButton("Sampling", () => _controller.SelectMode(PrimaryMode.Sampling));
        _automaticModeButton = ModeButton("Automatic", () => _controller.SelectMode(PrimaryMode.AutomaticInstrumentation));
        _manualModeButton = ModeButton("Manual only", _controller.SelectManualOnly);
        modeBar.AddChild(_samplingModeButton);
        modeBar.AddChild(_automaticModeButton);
        modeBar.AddChild(_manualModeButton);
        _includeManualButton = new CheckButton { Text = "Include manual semantic scopes" };
        _includeManualButton.Toggled += _controller.SetManualOverlay;
        settingsRoot.AddChild(_includeManualButton);
        _settingsLabel = new Label
        {
            Text = "Sampling is the universal default. Add manual scopes only for named hotspots.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        settingsRoot.AddChild(_settingsLabel);

        _samplingSettings = new VBoxContainer();
        settingsRoot.AddChild(_samplingSettings);
        _samplingIncludes = AddLineSetting(_samplingSettings, "Include assemblies", "",
            text => _controller.UpdateSampling(CurrentSampling() with { IncludeAssemblies = text }));
        _samplingExcludes = AddLineSetting(_samplingSettings, "Exclude assemblies", "",
            text => _controller.UpdateSampling(CurrentSampling() with { ExcludeAssemblies = text }));
        _samplingInterval = AddSpinSetting(_samplingSettings, "Interval (ns, startup-only)", 100_000, 1_000_000_000,
            2_000_000, value => _controller.UpdateSampling(CurrentSampling() with
                { RequestedIntervalNanoseconds = (long)value }));

        _automaticSettings = new VBoxContainer();
        settingsRoot.AddChild(_automaticSettings);
        _automaticSettings.AddChild(new Label
        {
            Text = "Advanced: build-time exact spans. Preview project changes before applying.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        });
        _automaticIncludes = AddLineSetting(_automaticSettings, "Include patterns", "Game",
            text => _controller.UpdateAutomatic(CurrentAutomatic() with { IncludePatterns = text }));
        _automaticExcludes = AddLineSetting(_automaticSettings, "Exclude patterns", "",
            text => _controller.UpdateAutomatic(CurrentAutomatic() with { ExcludePatterns = text }));
        _automaticMaximum = AddSpinSetting(_automaticSettings, "Maximum methods", 1, 1_000_000, 4096,
            value => _controller.UpdateAutomatic(CurrentAutomatic() with { MaxMethods = (int)value }));
        var installCommands = new HBoxContainer();
        _automaticSettings.AddChild(installCommands);
        _previewInstallButton = new Button { Text = "Preview Install" };
        _previewInstallButton.Pressed += PreviewAutomaticInstall;
        installCommands.AddChild(_previewInstallButton);
        _previewUninstallButton = new Button { Text = "Preview Uninstall" };
        _previewUninstallButton.Pressed += PreviewAutomaticUninstall;
        installCommands.AddChild(_previewUninstallButton);
        _applyInstallButton = new Button { Text = "Apply Confirmed", Disabled = true,
            TooltipText = "Review the diff, then click to explicitly confirm Apply." };
        _applyInstallButton.Pressed += ApplyAutomaticInstall;
        installCommands.AddChild(_applyInstallButton);
        _previewDiff = new TextEdit { Editable = false, CustomMinimumSize = new Vector2(0, 120) };
        _automaticSettings.AddChild(_previewDiff);

        _manualSettings = new VBoxContainer();
        settingsRoot.AddChild(_manualSettings);
        _manualPrefix = AddLineSetting(_manualSettings, "Manual label prefix", "",
            text => _controller.UpdateManual(new ManualSettings(text)));
        _qualityLabel = new Label
        {
            Text = "Complete capture · no observations",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        settingsRoot.AddChild(_qualityLabel);
    }

        private void ShowSettings()
    {
        if (_settingsWindow == null) return;
        var usable = DisplayServer.ScreenGetUsableRect();
        var width = Math.Clamp(560, _settingsWindow.MinSize.X, Math.Max(_settingsWindow.MinSize.X, usable.Size.X - 32));
        var height = Math.Clamp(560, _settingsWindow.MinSize.Y, Math.Max(_settingsWindow.MinSize.Y, usable.Size.Y - 32));
        _settingsWindow.PopupCentered(new Vector2I(width, height));
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

    private static LineEdit AddLineSetting(Control parent, string label, string value,
        Action<string> changed)
    {
        var row = new HBoxContainer();
        parent.AddChild(row);
        row.AddChild(new Label { Text = label });
        var edit = new LineEdit { Text = value, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        edit.TextChanged += text => changed(text);
        row.AddChild(edit);
        return edit;
    }

    private static SpinBox AddSpinSetting(Control parent, string label, double minimum,
        double maximum, double value, Action<double> changed)
    {
        var row = new HBoxContainer();
        parent.AddChild(row);
        row.AddChild(new Label { Text = label });
        var spin = new SpinBox { MinValue = minimum, MaxValue = maximum, Value = value, Rounded = true };
        spin.ValueChanged += newValue => changed(newValue);
        row.AddChild(spin);
        return spin;
    }

    private SamplingSettings CurrentSampling() => new(_samplingIncludes?.Text ?? "",
        _samplingExcludes?.Text ?? "", (long)(_samplingInterval?.Value ?? 2_000_000));
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

    private static Button ModeButton(string text, Action pressed)
    {
        var button = new Button { Text = text, ToggleMode = true };
        button.Pressed += pressed;
        return button;
    }

        public void Render(ProfilerDockViewState state)
    {
        if (_targetLabel == null)
            return;
        _targetLabel.Text = state.Target;
        _statsLabel.Text = state.Status;
        ApplyMode(_samplingModeButton, state.ModeSegments.ElementAtOrDefault(0));
        ApplyMode(_automaticModeButton, state.ModeSegments.ElementAtOrDefault(1));
        ApplyMode(_manualModeButton, state.ModeSegments.ElementAtOrDefault(2));
        ApplyMode(_includeManualButton, state.ManualOverlay);
        _startButton.Disabled = !state.Commands.Start;
        _stopButton.Disabled = !state.Commands.Stop;
        _copyButton.Disabled = !state.Commands.Copy;
        _exportButton.Disabled = !state.Commands.Export;
        _settingsLabel.Text = string.IsNullOrEmpty(state.InstallerStatus)
            ? state.SettingsStatus
            : state.SettingsStatus + " | " + state.InstallerStatus;
        _qualityLabel.Text = state.QualityBanner;
        var primary = _controller.Configuration.Primary;
        _samplingSettings.Visible = primary == PrimaryMode.Sampling;
        _automaticSettings.Visible = primary == PrimaryMode.AutomaticInstrumentation;
        _manualSettings.Visible = primary == PrimaryMode.None || _controller.Configuration.IncludeManual;
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
            if (!ReferenceEquals(child, _tree)) child.QueueFree();
        _tree.Visible = _frames.Count > 0;
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
        _sessionActive = active;
        if (active)
            _discovery.OnSessionStarted();
        else
            _discovery.OnSessionStopped();
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
        if (identity.Capturing && !_profilingRequested)
        {
            // A managed editor-plugin reload replaces the panel but need not stop runtime capture.
            _profilingRequested = true;
            _liveFollow = true;
        }
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
            _statsLabel.Text = point.Source == CaptureSource.Sampling
                ? RuntimeStatus($"{point.Value} samples | {point.Rows.Count} methods | batch #{point.Sequence}")
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
