using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CutFlow.Models;

namespace CutFlow.Controls;

public sealed class TimelineView : FrameworkElement
{
    private const double RulerHeight = 24;
    private const double HandleHitPixels = 8;
    private double _duration;
    private double _playhead;
    private double _zoom = 1;
    private double _offset;
    private IReadOnlyList<double> _peaks = Array.Empty<double>();
    private IReadOnlyList<SilenceSegment> _segments = Array.Empty<SilenceSegment>();
    private readonly HashSet<int> _selectedSegmentIndices = new();
    private int _selectionAnchor = -1;
    private bool _scrubbing;
    private long _lastPointerUpdateMs;
    private RenderTargetBitmap? _staticBitmap;
    private bool _staticDirty = true;
    private Size _lastCacheSize;

    private bool _regionEditMode;
    private bool _creatingRegion;
    private double _regionCreateStart;
    private double _regionCreateEnd;
    private int _resizingSegmentIndex = -1;
    private bool _resizeLeftEdge;
    private double _resizeOriginalOtherEdge;
    private double _resizeOriginalStart;
    private double _resizeOriginalEnd;
    private double _resizePreviewStart;
    private double _resizePreviewEnd;

    public event EventHandler<double>? SeekRequested;
    public event EventHandler<double>? ScrubPreviewRequested;
    public event EventHandler? ScrubStarted;
    public event EventHandler? ScrubCompleted;
    public event Action<IReadOnlyList<int>>? SegmentSelectionChanged;
    public event Action<int>? SegmentContextRequested;
    public event Action<double, double>? RegionCreated;
    public event Action<int, double, double, double, double>? SegmentBoundsChanged;
    public event EventHandler? ViewChanged;

    public double Duration
    {
        get => _duration;
        set
        {
            var next = Math.Max(0, value);
            if (Math.Abs(next - _duration) < 0.0001) return;
            _duration = next;
            ClampOffset();
            DirtyStatic();
        }
    }

    public double Playhead
    {
        get => _playhead;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, Duration));
            if (Math.Abs(clamped - _playhead) < 0.002) return;
            _playhead = clamped;
            InvalidateVisual();
        }
    }

    public IReadOnlyList<double> Peaks
    {
        get => _peaks;
        set { _peaks = value ?? Array.Empty<double>(); DirtyStatic(); }
    }

    public IReadOnlyList<SilenceSegment> Segments
    {
        get => _segments;
        set
        {
            _segments = value ?? Array.Empty<SilenceSegment>();
            _selectedSegmentIndices.RemoveWhere(i => i < 0 || i >= _segments.Count || IsHidden(_segments[i]));
            if (_selectionAnchor >= _segments.Count) _selectionAnchor = -1;
            DirtyStatic();
        }
    }

    public bool RegionEditMode
    {
        get => _regionEditMode;
        set
        {
            _regionEditMode = value;
            if (!value) CancelRegionInteraction();
            Cursor = value ? Cursors.Cross : Cursors.Arrow;
            InvalidateVisual();
        }
    }

    public int SelectedSegmentIndex
    {
        get => _selectedSegmentIndices.OrderBy(i => i).FirstOrDefault(-1);
        set
        {
            if (value < 0) SetSelectedSegments(Array.Empty<int>(), false);
            else SetSelectedSegments(new[] { value }, false);
        }
    }

    public IReadOnlyList<int> SelectedSegmentIndices => _selectedSegmentIndices.OrderBy(i => i).ToArray();
    public bool SnapEnabled { get; set; } = true;
    public double SnapThresholdSeconds { get; set; } = 0.08;
    public double Zoom => _zoom;
    public bool CanScroll => _zoom > 1.001 && Duration > 0;
    public double ScrollFraction
    {
        get
        {
            var max = Math.Max(0, Duration - VisibleDuration);
            return max <= 0.0001 ? 0 : Math.Clamp(_offset / max, 0, 1);
        }
    }

    public TimelineView()
    {
        Focusable = true;
        Cursor = Cursors.Arrow;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        SizeChanged += (_, _) => DirtyStatic();
    }

    public void ZoomIn() => SetZoom(_zoom * 1.35, ActualWidth / 2);
    public void ZoomOut() => SetZoom(_zoom / 1.35, ActualWidth / 2);
    public void SetZoomLevel(double zoom) => SetZoom(zoom, ActualWidth / 2);

    public void Fit()
    {
        _zoom = 1;
        _offset = 0;
        DirtyStatic();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetScrollFraction(double fraction)
    {
        if (!CanScroll) return;
        var max = Math.Max(0, Duration - VisibleDuration);
        var next = Math.Clamp(fraction, 0, 1) * max;
        if (Math.Abs(next - _offset) < 0.001) return;
        _offset = next;
        DirtyStatic();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetSelectedSegments(IEnumerable<int> indices, bool notify = true)
    {
        var next = indices.Where(i => i >= 0 && i < Segments.Count && !IsHidden(Segments[i])).Distinct().ToArray();
        if (_selectedSegmentIndices.SetEquals(next)) return;
        _selectedSegmentIndices.Clear();
        foreach (var i in next) _selectedSegmentIndices.Add(i);
        if (next.Length > 0) _selectionAnchor = next[^1];
        InvalidateVisual();
        if (notify) SegmentSelectionChanged?.Invoke(SelectedSegmentIndices);
    }

    public void ClearSelection(bool notify = true) => SetSelectedSegments(Array.Empty<int>(), notify);
    public void InvalidateStaticCache() => DirtyStatic();

    public void FollowPlayhead(double seconds)
    {
        if (_zoom <= 1.01 || Duration <= 0) return;
        var visible = VisibleDuration;
        if (seconds < _offset + visible * 0.10 || seconds > _offset + visible * 0.90)
        {
            _offset = seconds - visible * 0.22;
            ClampOffset();
            DirtyStatic();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        try
        {
            EnsureStaticBitmap();
            if (_staticBitmap is not null)
                dc.DrawImage(_staticBitmap, new Rect(0, 0, ActualWidth, ActualHeight));
        }
        catch
        {
            _staticBitmap = null;
            _staticDirty = true;
            dc.DrawRectangle(BrushResource("PanelBrush", Colors.White), null, new Rect(0, 0, ActualWidth, ActualHeight));
        }

        if (Duration <= 0) return;
        DrawSelectionOutlines(dc);
        DrawSelectionHandles(dc);
        DrawCreatingRegion(dc);

        var playPen = MakePen(BrushResource("CutBrush", Color.FromRgb(205, 82, 78)), 1.25);
        var cutStrong = BrushResource("CutBrush", Color.FromRgb(205, 82, 78));
        var px = TimeToX(Playhead);
        if (px < 0 || px > ActualWidth) return;

        dc.DrawLine(playPen, new Point(px, 0), new Point(px, ActualHeight));
        var marker = new StreamGeometry();
        using (var ctx = marker.Open())
        {
            ctx.BeginFigure(new Point(px - 4, 0), true, true);
            ctx.LineTo(new Point(px + 4, 0), true, false);
            ctx.LineTo(new Point(px, 7), true, false);
        }
        marker.Freeze();
        dc.DrawGeometry(cutStrong, null, marker);
    }

    private void EnsureStaticBitmap()
    {
        var size = new Size(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        if (!_staticDirty && _staticBitmap is not null && NearlySame(size, _lastCacheSize)) return;
        if (size.Width <= 1 || size.Height <= 1) return;

        // Cache at device-independent resolution. Rendering the cache at full 150–200% DPI
        // caused multi-megabyte bitmap allocations on every zoom/pan and made the editor feel
        // like it was about to crash on high-DPI monitors. The final bitmap is still scaled by
        // WPF, while the playhead remains vector-sharp.
        var pixelWidth = Math.Clamp((int)Math.Ceiling(size.Width), 1, 3072);
        var pixelHeight = Math.Clamp((int)Math.Ceiling(size.Height), 1, 768);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // If the window is wider/taller than the cache cap, scale the complete logical
            // timeline into the cache instead of cropping the right/bottom side. WPF stretches
            // this cached image back to the full control size in OnRender.
            var sx = pixelWidth / size.Width;
            var sy = pixelHeight / size.Height;
            dc.PushTransform(new ScaleTransform(sx, sy));
            DrawStatic(dc, size.Width, size.Height);
            dc.Pop();
        }

        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        if (bitmap.CanFreeze) bitmap.Freeze();
        _staticBitmap = bitmap;
        _lastCacheSize = size;
        _staticDirty = false;
    }

    private void DrawStatic(DrawingContext dc, double width, double height)
    {
        var panel = BrushResource("PanelBrush", Colors.White);
        var panelAlt = BrushResource("PanelAltBrush", Color.FromRgb(248, 248, 247));
        var lineBrush = BrushResource("LineBrush", Color.FromRgb(218, 219, 215));
        var muted = BrushResource("MutedBrush", Color.FromRgb(105, 108, 104));
        var waveform = BrushResource("AccentBrush", Color.FromRgb(34, 119, 166));
        var cutFill = BrushResource("CutFillBrush", Color.FromArgb(82, 214, 92, 87));
        var cut = BrushResource("CutBrush", Color.FromRgb(196, 82, 78));
        var markedFill = cutFill.CloneCurrentValue();
        markedFill.Opacity = 0.58;
        if (markedFill.CanFreeze) markedFill.Freeze();

        var line = MakePen(lineBrush, 1);
        var markedPen = MakePen(cut, 1.5);

        dc.DrawRectangle(panel, null, new Rect(0, 0, width, height));
        dc.DrawRectangle(panelAlt, null, new Rect(0, 0, width, RulerHeight));
        dc.DrawLine(line, new Point(0, RulerHeight), new Point(width, RulerHeight));
        DrawRuler(dc, line, muted, width);

        var trackTop = RulerHeight + 1;
        var trackHeight = Math.Max(20, height - trackTop);
        DrawWaveform(dc, waveform, trackTop, trackHeight, width);

        // The review timeline has ONE region state: a red marked range. Once a cut is
        // successfully applied, IsHidden() removes that marker from this layer. There is no
        // READY/yellow/dashed/committed visual state for the user to reason about.
        for (var i = 0; i < Segments.Count; i++)
        {
            var segment = Segments[i];
            if (IsHidden(segment)) continue;

            var x1 = TimeToX(segment.StartSeconds);
            var x2 = TimeToX(segment.EndSeconds);
            if (x2 < -10 || x1 > width + 10) continue;
            var rawLeft = Math.Max(0, x1);
            var rawRight = Math.Min(width, x2);
            var rawWidth = Math.Max(0.5, rawRight - rawLeft);
            var minVisualWidth = 6.0;
            var center = (rawLeft + rawRight) / 2.0;
            var drawWidth = Math.Max(minVisualWidth, rawWidth);
            var left = Math.Clamp(center - drawWidth / 2.0, 0, Math.Max(0, width - drawWidth));
            var rect = new Rect(left, trackTop, Math.Min(width, drawWidth), trackHeight);
            // Selection is drawn dynamically in OnRender, so clicking a region remains cheap.
            dc.DrawRectangle(markedFill, markedPen, rect);
        }

        if (Duration <= 0)
            dc.DrawText(MakeText("Create a project to build the timeline", 13, muted), new Point(16, RulerHeight + 18));
    }

    private void DrawSelectionOutlines(DrawingContext dc)
    {
        if (_selectedSegmentIndices.Count == 0) return;
        var accent = BrushResource("AccentBrush", Color.FromRgb(77, 126, 155));
        var pen = MakePen(accent, 2.4);
        var top = RulerHeight + 1;
        var height = Math.Max(1, ActualHeight - top);
        foreach (var index in _selectedSegmentIndices)
        {
            if (index < 0 || index >= Segments.Count || IsHidden(Segments[index])) continue;
            var segment = Segments[index];
            var start = index == _resizingSegmentIndex ? _resizePreviewStart : segment.StartSeconds;
            var end = index == _resizingSegmentIndex ? _resizePreviewEnd : segment.EndSeconds;
            var x1 = TimeToX(start);
            var x2 = TimeToX(end);
            if (x2 < -10 || x1 > ActualWidth + 10) continue;
            var left = Math.Max(0, Math.Min(x1, x2));
            var right = Math.Min(ActualWidth, Math.Max(x1, x2));
            var width = Math.Max(6, right - left);
            dc.DrawRoundedRectangle(null, pen, new Rect(left, top, width, height), 3, 3);
        }
    }

    private void DrawSelectionHandles(DrawingContext dc)
    {
        if (_selectedSegmentIndices.Count != 1) return;
        var index = _selectedSegmentIndices.First();
        if (index < 0 || index >= Segments.Count || IsHidden(Segments[index])) return;
        var segment = Segments[index];
        var top = RulerHeight + 8;
        var bottom = Math.Max(top + 12, ActualHeight - 8);
        var start = index == _resizingSegmentIndex ? _resizePreviewStart : segment.StartSeconds;
        var end = index == _resizingSegmentIndex ? _resizePreviewEnd : segment.EndSeconds;
        var x1 = TimeToX(start);
        var x2 = TimeToX(end);
        var brush = BrushResource("AccentBrush", Color.FromRgb(77, 126, 155));
        if (x1 >= -8 && x1 <= ActualWidth + 8) dc.DrawRoundedRectangle(brush, null, new Rect(x1 - 3, top, 6, bottom - top), 3, 3);
        if (x2 >= -8 && x2 <= ActualWidth + 8) dc.DrawRoundedRectangle(brush, null, new Rect(x2 - 3, top, 6, bottom - top), 3, 3);
    }

    private void DrawCreatingRegion(DrawingContext dc)
    {
        if (!_creatingRegion) return;
        var start = Math.Min(_regionCreateStart, _regionCreateEnd);
        var end = Math.Max(_regionCreateStart, _regionCreateEnd);
        var x1 = TimeToX(start);
        var x2 = TimeToX(end);
        var fill = BrushResource("CutFillBrush", Color.FromArgb(72, 214, 92, 87));
        var pen = MakePen(BrushResource("CutBrush", Color.FromRgb(196, 82, 78)), 2);
        dc.DrawRectangle(fill, pen, new Rect(Math.Min(x1, x2), RulerHeight + 1, Math.Max(1, Math.Abs(x2 - x1)), Math.Max(1, ActualHeight - RulerHeight - 1)));
    }

    private void DrawWaveform(DrawingContext dc, Brush brush, double top, double height, double width)
    {
        if (Peaks.Count == 0 || Duration <= 0 || width <= 1) return;
        var center = top + height / 2;
        var amplitude = height * 0.42;
        var visibleStart = _offset;
        var visibleEnd = _offset + VisibleDuration;
        var startIndex = Math.Clamp((int)Math.Floor(visibleStart / Duration * Peaks.Count), 0, Peaks.Count - 1);
        var endIndex = Math.Clamp((int)Math.Ceiling(visibleEnd / Duration * Peaks.Count), startIndex + 1, Peaks.Count);
        var visibleCount = Math.Max(1, endIndex - startIndex);
        var columns = Math.Max(1, (int)Math.Ceiling(width));
        var pen = MakePen(brush, 1);

        for (var x = 0; x < columns; x++)
        {
            var a = startIndex + (int)((long)x * visibleCount / columns);
            var b = startIndex + (int)((long)(x + 1) * visibleCount / columns);
            b = Math.Clamp(Math.Max(a + 1, b), a + 1, endIndex);
            var max = 0.0;
            for (var i = a; i < b; i++) max = Math.Max(max, Peaks[i]);
            var h = Math.Max(1, Math.Pow(Math.Clamp(max, 0, 1), 0.58) * amplitude);
            dc.DrawLine(pen, new Point(x + 0.5, center - h), new Point(x + 0.5, center + h));
        }
    }

    private void DrawRuler(DrawingContext dc, Pen line, Brush textBrush, double width)
    {
        if (Duration <= 0 || width <= 0) return;
        var visible = VisibleDuration;
        var rough = visible / Math.Max(2, width / 90);
        var step = NiceStep(rough);
        var first = Math.Floor(_offset / step) * step;
        for (var t = first; t <= _offset + visible + step; t += step)
        {
            var x = TimeToX(t);
            if (x < 0 || x > width) continue;
            dc.DrawLine(line, new Point(x, RulerHeight - 5), new Point(x, RulerHeight));
            dc.DrawText(MakeText(FormatRulerTime(t), 10, textBrush), new Point(x + 3, 4));
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var point = e.GetPosition(this);
        if (point.Y < RulerHeight) { BeginScrub(point); e.Handled = true; return; }

        var rawTime = XToTimeRaw(point.X);
        var edge = HitTestSelectedEdge(point.X);
        if (edge.index >= 0)
        {
            _resizingSegmentIndex = edge.index;
            _resizeLeftEdge = edge.left;
            var segment = Segments[edge.index];
            _resizeOriginalStart = segment.StartSeconds;
            _resizeOriginalEnd = segment.EndSeconds;
            _resizePreviewStart = segment.StartSeconds;
            _resizePreviewEnd = segment.EndSeconds;
            _resizeOriginalOtherEdge = edge.left ? segment.EndSeconds : segment.StartSeconds;
            CaptureMouse();
            Cursor = Cursors.SizeWE;
            e.Handled = true;
            return;
        }

        var modifiers = Keyboard.Modifiers;
        var hitIndex = HitTestSegment(rawTime);

        // + Region is a dedicated draw mode. Once active, dragging on the waveform creates
        // a region even if the drag starts on top of another suggestion. Selected edge
        // handles are still checked above so resizing remains available.
        if (RegionEditMode && (modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
        {
            _creatingRegion = true;
            _regionCreateStart = ApplySnap(rawTime);
            _regionCreateEnd = _regionCreateStart;
            CaptureMouse();
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        UpdateSelectionFromClick(hitIndex, modifiers);
        if (hitIndex >= 0)
        {
            // Clicking a region selects it; it should not also scrub/restart the video.
            e.Handled = true;
            return;
        }

        BeginScrub(point);
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Focus();
        var point = e.GetPosition(this);
        if (point.Y < RulerHeight) return;
        var index = HitTestSegment(XToTimeRaw(point.X));
        if (index < 0) return;
        if (!_selectedSegmentIndices.Contains(index)) SetSelectedSegments(new[] { index });
        SegmentContextRequested?.Invoke(index);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var point = e.GetPosition(this);

        if (_resizingSegmentIndex >= 0 && e.LeftButton == MouseButtonState.Pressed)
        {
            ResizeSelectedSegment(point.X);
            e.Handled = true;
            return;
        }
        if (_creatingRegion && e.LeftButton == MouseButtonState.Pressed)
        {
            _regionCreateEnd = ApplySnap(XToTimeRaw(point.X));
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        if (_scrubbing && e.LeftButton == MouseButtonState.Pressed)
        {
            var now = Environment.TickCount64;
            if (now - _lastPointerUpdateMs >= 14)
            {
                _lastPointerUpdateMs = now;
                HandlePointer(point);
            }
            e.Handled = true;
            return;
        }

        var edge = point.Y >= RulerHeight ? HitTestSelectedEdge(point.X) : (-1, false);
        Cursor = edge.Item1 >= 0 ? Cursors.SizeWE : RegionEditMode && point.Y >= RulerHeight ? Cursors.Cross : Cursors.Arrow;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var point = e.GetPosition(this);

        if (_resizingSegmentIndex >= 0)
        {
            ResizeSelectedSegment(point.X);
            var index = _resizingSegmentIndex;
            _resizingSegmentIndex = -1;
            ReleaseMouseCapture();
            Cursor = RegionEditMode ? Cursors.Cross : Cursors.Arrow;
            if (index >= 0 && index < Segments.Count)
                SegmentBoundsChanged?.Invoke(index, _resizeOriginalStart, _resizeOriginalEnd, _resizePreviewStart, _resizePreviewEnd);
            e.Handled = true;
            return;
        }

        if (_creatingRegion)
        {
            _regionCreateEnd = ApplySnap(XToTimeRaw(point.X));
            var start = Math.Min(_regionCreateStart, _regionCreateEnd);
            var end = Math.Max(_regionCreateStart, _regionCreateEnd);
            _creatingRegion = false;
            ReleaseMouseCapture();
            InvalidateVisual();
            if (end - start >= 0.02) RegionCreated?.Invoke(start, end);
            e.Handled = true;
            return;
        }

        if (!_scrubbing) return;
        HandlePointer(point);
        _scrubbing = false;
        ReleaseMouseCapture();
        SeekRequested?.Invoke(this, Playhead);
        ScrubCompleted?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            SetZoom(_zoom * (e.Delta > 0 ? 1.22 : 1 / 1.22), e.GetPosition(this).X);
        else if (CanScroll)
        {
            _offset += VisibleDuration * ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? 0.24 : 0.12) * (e.Delta > 0 ? -1 : 1);
            ClampOffset();
            DirtyStatic();
            ViewChanged?.Invoke(this, EventArgs.Empty);
        }
        e.Handled = true;
    }

    private void BeginScrub(Point point)
    {
        CaptureMouse();
        _scrubbing = true;
        ScrubStarted?.Invoke(this, EventArgs.Empty);
        HandlePointer(point);
    }

    private void ResizeSelectedSegment(double x)
    {
        if (_resizingSegmentIndex < 0 || _resizingSegmentIndex >= Segments.Count) return;
        var t = ApplySnap(XToTimeRaw(x));
        const double minLength = 0.02;
        if (_resizeLeftEdge)
        {
            _resizePreviewStart = Math.Clamp(t, 0, Math.Max(0, _resizeOriginalOtherEdge - minLength));
            _resizePreviewEnd = _resizeOriginalOtherEdge;
        }
        else
        {
            _resizePreviewStart = _resizeOriginalOtherEdge;
            _resizePreviewEnd = Math.Clamp(t, Math.Min(Duration, _resizeOriginalOtherEdge + minLength), Duration);
        }
        // Live edge dragging is a lightweight overlay. The cached waveform/regions are
        // rebuilt once on mouse-up when MainWindow commits the new bounds.
        InvalidateVisual();
    }

    private (int index, bool left) HitTestSelectedEdge(double x)
    {
        if (_selectedSegmentIndices.Count != 1) return (-1, false);
        var index = _selectedSegmentIndices.First();
        if (index < 0 || index >= Segments.Count || IsHidden(Segments[index])) return (-1, false);
        var segment = Segments[index];
        var x1 = TimeToX(segment.StartSeconds);
        var x2 = TimeToX(segment.EndSeconds);
        if (Math.Abs(x - x1) <= HandleHitPixels) return (index, true);
        if (Math.Abs(x - x2) <= HandleHitPixels) return (index, false);
        return (-1, false);
    }

    private void UpdateSelectionFromClick(int index, ModifierKeys modifiers)
    {
        if (index < 0)
        {
            if ((modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0) ClearSelection();
            return;
        }

        if ((modifiers & ModifierKeys.Shift) != 0 && _selectionAnchor >= 0)
        {
            var min = Math.Min(_selectionAnchor, index);
            var max = Math.Max(_selectionAnchor, index);
            SetSelectedSegments(Enumerable.Range(min, max - min + 1).Where(i => i < Segments.Count && !IsHidden(Segments[i])));
            return;
        }

        if ((modifiers & ModifierKeys.Control) != 0)
        {
            var next = _selectedSegmentIndices.ToHashSet();
            if (!next.Add(index)) next.Remove(index);
            _selectionAnchor = index;
            SetSelectedSegments(next);
            return;
        }

        _selectionAnchor = index;
        SetSelectedSegments(new[] { index });
    }

    private int HitTestSegment(double time)
    {
        for (var i = Segments.Count - 1; i >= 0; i--)
        {
            var segment = Segments[i];
            if (IsHidden(segment)) continue;
            if (time >= segment.StartSeconds && time <= segment.EndSeconds) return i;
        }
        return -1;
    }

    private void HandlePointer(Point point)
    {
        var time = XToTimeRaw(point.X);
        if (SnapEnabled && (Keyboard.Modifiers & ModifierKeys.Alt) == 0) time = SnapTime(time);
        Playhead = time;
        ScrubPreviewRequested?.Invoke(this, time);
    }

    private double ApplySnap(double time) => SnapEnabled && (Keyboard.Modifiers & ModifierKeys.Alt) == 0 ? SnapTime(time) : time;

    private double SnapTime(double time)
    {
        var threshold = Math.Max(0, SnapThresholdSeconds);
        if (threshold <= 0) return time;
        var best = time;
        var distance = threshold + 0.0001;
        foreach (var segment in Segments)
        {
            if (IsHidden(segment)) continue;
            Check(segment.StartSeconds);
            Check(segment.EndSeconds);
        }
        return best;

        void Check(double candidate)
        {
            var d = Math.Abs(candidate - time);
            if (d <= threshold && d < distance) { best = candidate; distance = d; }
        }
    }

    private void SetZoom(double zoom, double anchorX)
    {
        if (Duration <= 0) return;
        var before = XToTimeRaw(anchorX);
        _zoom = Math.Clamp(zoom, 1, 80);
        _offset = before - (anchorX / Math.Max(1, ActualWidth)) * VisibleDuration;
        ClampOffset();
        DirtyStatic();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private double VisibleDuration => Duration <= 0 ? 1 : Duration / _zoom;
    private double TimeToX(double time) => (time - _offset) / VisibleDuration * Math.Max(1, ActualWidth);
    private double XToTimeRaw(double x) => Math.Clamp(_offset + (x / Math.Max(1, ActualWidth)) * VisibleDuration, 0, Math.Max(0, Duration));

    private void ClampOffset()
    {
        var max = Math.Max(0, Duration - VisibleDuration);
        _offset = Math.Clamp(_offset, 0, max);
    }

    private void DirtyStatic()
    {
        _staticDirty = true;
        InvalidateVisual();
    }

    private void CancelRegionInteraction()
    {
        _creatingRegion = false;
        _resizingSegmentIndex = -1;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    // Explicit Keep hides a marker. Confirmed cuts are physically flattened into the working media,
    // so consumed regions disappear because that timeline space no longer exists.
    private static bool IsHidden(SilenceSegment segment) => segment.IsIgnored;
    private static bool NearlySame(Size a, Size b) => Math.Abs(a.Width - b.Width) < 0.5 && Math.Abs(a.Height - b.Height) < 0.5;

    private static double NiceStep(double rough)
    {
        if (rough <= 0) return 1;
        var exponent = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var fraction = rough / exponent;
        var nice = fraction < 1.5 ? 1 : fraction < 3.5 ? 2 : fraction < 7.5 ? 5 : 10;
        return nice * exponent;
    }

    private static string FormatRulerTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}" : $"{(int)ts.TotalMinutes}:{ts.Seconds:00}";
    }

    private Brush BrushResource(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);
    private static Pen MakePen(Brush brush, double thickness) { var p = new Pen(brush, thickness); if (p.CanFreeze) p.Freeze(); return p; }
    private static Pen MakeDashedPen(Brush brush, double thickness) { var p = new Pen(brush, thickness) { DashStyle = new DashStyle(new[] { 4.0, 3.0 }, 0) }; if (p.CanFreeze) p.Freeze(); return p; }
    private FormattedText MakeText(string text, double size, Brush brush) => new(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI Variable Text, Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
