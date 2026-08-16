#if TOOLS
using Apeworks.GodotCSharpProfiler;
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

// The single "C# Profiler" bottom panel: Start/Stop toolbar, clickable frame-time graph, and the
// selected frame's call tree with Total/Self/Calls columns. Frames arrive from
// CsProfilerBridge via CsProfilerDebuggerPlugin (see the bridge for the message layout).
[Tool]
public partial class CsProfilerPanel : VBoxContainer
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
    public bool ProfilingRequested => _startButton is { ButtonPressed: true };

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
    private Button _copyButton;
    private SpinBox _frameSelector;
    private Label _statsLabel;
    private CsProfilerFrameGraph _graph;
    private Tree _tree;
    private int _selectedIndex = -1;
    private bool _liveFollow = true;
    private bool _updatingSelector;
    private bool _rebuildingTree;
    private readonly List<string> _displayedRowsForTests = new();
    private bool _sessionActive;
    private readonly CsProfilerSessionDiscoveryState _discovery = new();
    private string _runtimeDescription = "";
    private double _lastFrameAtSec = double.NegativeInfinity;
    private double _lastStartSentAtSec = double.NegativeInfinity;
    internal string StatusTextForTests => _statsLabel?.Text ?? "";
    internal string[] DisplayedRowsForTests() => _displayedRowsForTests.ToArray();
    internal int FrameCountForTests => _frames.Count;
    internal int SelectedIndexForTests => _selectedIndex;
    internal bool BridgeReadyForTests => _discovery.BridgeReady;
    internal CsProfilerRuntimeIdentity IdentityForTests => _discovery.Identity;
    internal string BuildCallReportForTests() => _selectedIndex >= 0 &&
                                                  _selectedIndex < _frames.Count
        ? BuildCallReport(_frames[_selectedIndex], "")
        : "";

    internal void RequestCaptureForTests()
    {
        if (_startButton != null && !_startButton.ButtonPressed)
            _startButton.ButtonPressed = true;
    }

    public override void _Ready()
    {
        SizeFlagsVertical = SizeFlags.ExpandFill;

        var toolbar = new HBoxContainer();
        AddChild(toolbar);

        _startButton = new Button
        {
            Text = "Start",
            ToggleMode = true,
            TooltipText = "Start/stop capturing C# scope timings from the running game.\n" +
                          "Left pressed before launching, capturing starts with the session."
        };
        _startButton.Toggled += OnStartToggled;
        toolbar.AddChild(_startButton);

        var clearButton = new Button { Text = "Clear" };
        clearButton.Pressed += ClearHistory;
        toolbar.AddChild(clearButton);

        _copyButton = new Button
        {
            Text = "Copy Calls",
            Disabled = true,
            TooltipText = "Copy the selected call subtree (or the complete frame) with frame and " +
                          "timing metadata. Ctrl+C works while the call tree is focused."
        };
        _copyButton.Pressed += CopySelectedCalls;
        toolbar.AddChild(_copyButton);

        toolbar.AddChild(new VSeparator());
        toolbar.AddChild(new Label { Text = "Frame:" });
        _frameSelector = new SpinBox { MinValue = 0, MaxValue = 0, Rounded = true };
        _frameSelector.ValueChanged += OnFrameSelectorChanged;
        toolbar.AddChild(_frameSelector);

        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        toolbar.AddChild(spacer);

        _statsLabel = new Label { Text = "No data. Press Start while the game is running." };
        toolbar.AddChild(_statsLabel);

        _graph = new CsProfilerFrameGraph();
        _graph.FrameClicked += index =>
        {
            _liveFollow = false;
            SelectFrame(index);
        };
        AddChild(_graph);

        _tree = new Tree
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            Columns = 4,
            ColumnTitlesVisible = true,
            HideRoot = true,
            SelectMode = Tree.SelectModeEnum.Row
        };
        _tree.SetColumnTitle(0, "Name");
        _tree.SetColumnTitle(1, "Total");
        _tree.SetColumnTitle(2, "Self");
        _tree.SetColumnTitle(3, "Calls");
        _tree.SetColumnExpand(0, true);
        for (var column = 1; column < 4; column++)
        {
            _tree.SetColumnExpand(column, false);
            _tree.SetColumnCustomMinimumWidth(column, 96);
        }
        _tree.ItemCollapsed += OnItemCollapsed;
        _tree.GuiInput += OnTreeGuiInput;
        AddChild(_tree);

        if (_discovery.BridgeReady)
            ApplyBridgeReadyUi(_discovery.Identity);
        else
            TryRequestDiscovery(NowSec());
    }

    public void InitializeSessionState(bool active)
    {
        _sessionActive = active;
        if (active)
            _discovery.OnSessionStarted();
        else
            _discovery.OnSessionStopped();
    }

    private void OnStartToggled(bool pressed)
    {
        _liveFollow = pressed;
        _lastStartSentAtSec = NowSec();
        if (pressed)
        {
            _statsLabel.Text = SessionActive
                ? RuntimeStatus("Capturing... waiting for frames.")
                : "Waiting for a debug session — capture starts when the game launches.";
        }
        ProfilingToggled?.Invoke(pressed);
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
        // Keep the captured history around for post-mortem scrubbing; the Start toggle keeps its
        // state so relaunching the game resumes capturing.
        _sessionActive = false;
        if (!_discovery.OnSessionStopped())
            return;
        _runtimeDescription = "";
    }

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
        if (identity.Capturing && _startButton != null && !_startButton.ButtonPressed)
        {
            // A managed editor-plugin reload replaces the panel but need not stop the runtime
            // capture. Reflect its scalar ready state without emitting a duplicate UI toggle.
            _startButton.SetPressedNoSignal(true);
            _liveFollow = true;
        }
        if (_frames.Count == 0 && _statsLabel != null)
        {
            _statsLabel.Text = ProfilingRequested || identity.Capturing
                ? RuntimeStatus("Bridge ready; capturing...")
                : RuntimeStatus("Bridge ready. Press Start to capture.");
        }
    }

    // The start message can be lost around session startup (and gives no error when it is), so
    // while the toggle is held and no frames are flowing, re-send it once a second. The game
    // side treats repeated starts as no-ops, and the plugin drops sends while the session is
    // down — so this must NOT gate on session state, or a missed session signal wedges the
    // panel forever.
    public override void _Process(double delta)
    {
        if (_statsLabel == null)
            return;
        var now = NowSec();
        var active = SessionActive;
        if (active && !_discovery.SessionActive)
            _discovery.OnSessionStarted();
        else if (!active && _discovery.SessionActive)
            _discovery.OnSessionStopped();
        TryRequestDiscovery(now);

        if (!ProfilingRequested)
            return;
        if (now - _lastFrameAtSec < 1.0 || now - _lastStartSentAtSec < 1.0)
            return;
        _lastStartSentAtSec = now;
        ProfilingToggled?.Invoke(true);
        _statsLabel.Text = SessionActive
            ? "Start sent — no frames yet. Check this session's game is running " +
              "and check its Output for 'C# Profiler: capture started.'"
            : "Waiting for a debug session — capture starts when the game launches.";
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

    private void ClearHistory()
    {
        if (_graph == null)
            return; // session signal arrived before _Ready built the controls
        _frames.Clear();
        _selectedIndex = -1;
        _liveFollow = true;
        _graph.SetFrames(_frames);
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
            _statsLabel.Text = RuntimeStatus("Profiler data error: " + error);
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
        if (_frames.Count == 0)
            return;
        index = Math.Clamp(index, 0, _frames.Count - 1);
        _selectedIndex = index;
        _graph.SetSelectedIndex(index);
        _updatingSelector = true;
        _frameSelector.Value = index;
        _updatingSelector = false;

        var frame = _frames[index];
        if (_copyButton != null)
            _copyButton.Disabled = frame.Names.Length == 0;
        if (frame.Names.Length == 0)
        {
            _statsLabel.Text = RuntimeStatus(
                $"No samples | C# {frame.CsMs:0.00} ms | frame {frame.FrameMs:0.00} ms | " +
                $"frame #{frame.Index}");
            RebuildTree(frame);
            return;
        }
        _statsLabel.Text = RuntimeStatus(
            $"C# {frame.CsMs:0.00} ms | frame {frame.FrameMs:0.00} ms | " +
            $"{frame.Names.Length} scopes | frame #{frame.Index}");
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
#endif
