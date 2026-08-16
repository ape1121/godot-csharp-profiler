#if TOOLS
using Godot;
using Apeworks.GodotCSharpProfiler.Editor.Integration;
using Apeworks.GodotCSharpProfiler.Protocol;
using System;
using System.Collections.Generic;

// Exact manual-span frame strip for the legacy bridge: total engine frame time is drawn dim behind
// observed C# wall time, with 60/30 fps guide lines. Sampling estimates use a separate result
// group and are never drawn here. Left-click/drag selects a frame; the mouse wheel zooms the
// timeline around the cursor and middle-drag pans. While zoomed with the view pinned to the
// newest frame, the window keeps following incoming frames.
[Tool]
public partial class CsProfilerFrameGraph : Control
{
    public event Action<int> FrameClicked;

    private const double MinVisibleFrames = 8.0;
    private const double ZoomStep = 1.25;

    private IReadOnlyList<CsProfilerPanel.ProfileFrame> _frames = Array.Empty<CsProfilerPanel.ProfileFrame>();
    private IReadOnlyList<CaptureTimelinePoint> _timeline = Array.Empty<CaptureTimelinePoint>();
    private int _selectedIndex = -1;
    private bool _selecting;
    private bool _panning;
    // View window over the frame history, in fractional frame indices. _viewCount <= 0 means
    // "fit everything"; _pinnedToEnd keeps a zoomed window glued to the newest frame.
    private double _viewStart;
    private double _viewCount = -1;
    private bool _pinnedToEnd = true;

    private static readonly Color BackgroundColor = new(0.11f, 0.12f, 0.14f);
    private static readonly Color FrameColor = new(0.42f, 0.46f, 0.52f, 0.55f);
    private static readonly Color CsColor = new(0.36f, 0.72f, 1.0f);
    private static readonly Color GuideColor = new(1.0f, 1.0f, 1.0f, 0.14f);
    private static readonly Color GuideTextColor = new(1.0f, 1.0f, 1.0f, 0.45f);
    private static readonly Color SelectionColor = new(1.0f, 1.0f, 1.0f, 0.85f);

    public CsProfilerFrameGraph()
    {
        CustomMinimumSize = new Vector2(0, 48);
        MouseDefaultCursorShape = CursorShape.PointingHand;
        ClipContents = true;
        TooltipText = "Capture timeline · Sampling bars are sample counts; exact-span bars are observed time";
    }

    public void SetFrames(IReadOnlyList<CsProfilerPanel.ProfileFrame> frames)
    {
        _timeline = Array.Empty<CaptureTimelinePoint>();
        _frames = frames ?? Array.Empty<CsProfilerPanel.ProfileFrame>();
        ResetWindow(_frames.Count);
        QueueRedraw();
    }

    public void SetTimeline(CaptureTimeline timeline)
    {
        _frames = Array.Empty<CsProfilerPanel.ProfileFrame>();
        _timeline = timeline?.Points ?? Array.Empty<CaptureTimelinePoint>();
        ResetWindow(_timeline.Count);
        QueueRedraw();
    }

    private void ResetWindow(int count)
    {
        if (count == 0)
        {
            _viewCount = -1;
            _pinnedToEnd = true;
        }
        else if (_viewCount > 0)
        {
            _viewCount = Math.Min(_viewCount, count);
            if (_pinnedToEnd) _viewStart = count - _viewCount;
            ClampView();
        }
    }

    public void SetSelectedIndex(int index)
    {
        if (_selectedIndex == index)
            return;
        _selectedIndex = index;
        QueueRedraw();
    }

    private int ItemCount => _timeline.Count > 0 ? _timeline.Count : _frames.Count;

        private (double Start, double Count) VisibleWindow()
    {
        if (_viewCount <= 0 || _viewCount >= ItemCount)
            return (0, Math.Max(1, ItemCount));
        return (_viewStart, _viewCount);
    }

    public override void _GuiInput(InputEvent inputEvent)
    {
        switch (inputEvent)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true } wheelUp:
                ZoomAt(wheelUp.Position.X, 1.0 / ZoomStep);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true } wheelDown:
                ZoomAt(wheelDown.Position.X, ZoomStep);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } select:
                _selecting = select.Pressed;
                if (select.Pressed)
                    SelectAt(select.Position.X);
                AcceptEvent();
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Middle } pan:
                _panning = pan.Pressed;
                AcceptEvent();
                break;
            case InputEventMouseMotion motion when _panning:
                PanBy(motion.Relative.X);
                AcceptEvent();
                break;
            case InputEventMouseMotion motion when _selecting:
                SelectAt(motion.Position.X);
                AcceptEvent();
                break;
        }
    }

    private void ZoomAt(float x, double factor)
    {
        if (ItemCount == 0)
            return;
        var (start, count) = VisibleWindow();
        var newCount = Math.Clamp(count * factor, MinVisibleFrames, ItemCount);
        if (newCount >= ItemCount)
        {
            // Fully zoomed out returns to fit-everything mode so future history growth keeps
            // filling the whole strip.
            _viewCount = -1;
            _pinnedToEnd = true;
            QueueRedraw();
            return;
        }
        var anchorIndex = start + x / Mathf.Max(1.0f, Size.X) * count;
        _viewCount = newCount;
        _viewStart = anchorIndex - (anchorIndex - start) * (newCount / count);
        ClampView();
        QueueRedraw();
    }

    private void PanBy(float deltaPixels)
    {
        if (ItemCount == 0 || _viewCount <= 0)
            return;
        _viewStart -= deltaPixels / Mathf.Max(1.0f, Size.X) * _viewCount;
        ClampView();
        QueueRedraw();
    }

    private void ClampView()
    {
        _viewStart = Math.Clamp(_viewStart, 0, Math.Max(0, ItemCount - _viewCount));
        _pinnedToEnd = _viewStart + _viewCount >= ItemCount - 0.5;
    }

    private void SelectAt(float x)
    {
        if (ItemCount == 0)
            return;
        var (start, count) = VisibleWindow();
        var index = (int)(start + x / Mathf.Max(1.0f, Size.X) * count);
        FrameClicked?.Invoke(Mathf.Clamp(index, 0, ItemCount - 1));
    }

    public override void _Draw()
    {
        var size = Size;
        DrawRect(new Rect2(Vector2.Zero, size), BackgroundColor);
        if (ItemCount == 0) return;

        var (start, count) = VisibleWindow();
        var firstVisible = Math.Max(0, (int)Math.Floor(start));
        var lastVisible = Math.Min(ItemCount, (int)Math.Ceiling(start + count));
        var font = GetThemeFont("font", "Label");
        var fontSize = Mathf.Max(10, GetThemeFontSize("font_size", "Label") - 2);
        var step = size.X / (float)count;
        var barWidth = Mathf.Max(1.0f, step - (step >= 3.0f ? 1.0f : 0.0f));

        if (_timeline.Count > 0)
        {
            var maxValue = 1L;
            for (var i = firstVisible; i < lastVisible; i++) maxValue = Math.Max(maxValue, _timeline[i].Value);
            for (var i = firstVisible; i < lastVisible; i++)
            {
                var point = _timeline[i];
                var x = (float)((i - start) * step);
                var height = (float)(point.Value / (double)maxValue * size.Y);
                var color = point.Source == CaptureSource.Sampling ? CsColor : FrameColor;
                DrawRect(new Rect2(x, size.Y - height, barWidth, height), color);
            }
            var sampling = _timeline.Any(point => point.Source == CaptureSource.Sampling);
            DrawString(font, new Vector2(4, 14), sampling ? "Samples per batch" : "Observed exact-span time per batch",
                HorizontalAlignment.Left, -1, fontSize, GuideTextColor);
        }
        else
        {
            var maxMs = 20.0;
            for (var i = firstVisible; i < lastVisible; i++) maxMs = Math.Max(maxMs, _frames[i].FrameMs);
            maxMs *= 1.05;
            foreach (var guideMs in new[] { 1000.0 / 60.0, 1000.0 / 30.0 })
            {
                if (guideMs >= maxMs) continue;
                var y = (float)(size.Y - guideMs / maxMs * size.Y);
                DrawLine(new Vector2(0, y), new Vector2(size.X, y), GuideColor);
            }
            for (var i = firstVisible; i < lastVisible; i++)
            {
                var frame = _frames[i];
                var x = (float)((i - start) * step);
                var frameHeight = (float)(frame.FrameMs / maxMs * size.Y);
                var csHeight = (float)(frame.CsMs / maxMs * size.Y);
                DrawRect(new Rect2(x, size.Y - frameHeight, barWidth, frameHeight), FrameColor);
                DrawRect(new Rect2(x, size.Y - csHeight, barWidth, csHeight), CsColor);
            }
        }

        if (_viewCount > 0 && _viewCount < ItemCount)
            DrawString(font, new Vector2(4, size.Y - 4), $"zoom {ItemCount / count:0.#}x",
                HorizontalAlignment.Left, -1, fontSize, GuideTextColor);
        if (_selectedIndex >= firstVisible && _selectedIndex < lastVisible)
        {
            var x = (float)((_selectedIndex - start) * step) + barWidth * 0.5f;
            DrawLine(new Vector2(x, 0), new Vector2(x, size.Y), SelectionColor, 1.0f);
        }
    }
}
#else
public partial class CsProfilerFrameGraph : Godot.Control { }
#endif
