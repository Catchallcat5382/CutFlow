using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace CutFlow.Controls;

public sealed class ColorChangedEventArgs : EventArgs
{
    public Color Color { get; }
    public ColorChangedEventArgs(Color color) => Color = color;
}

public sealed class HsvColorPicker : FrameworkElement
{
    private double _hue;
    private double _saturation = 0.5;
    private double _value = 0.5;
    private bool _draggingSv;
    private bool _draggingHue;

    public event EventHandler<ColorChangedEventArgs>? ColorChanged;

    public Color SelectedColor
    {
        get => HsvToRgb(_hue, _saturation, _value);
        set
        {
            RgbToHsv(value, out _hue, out _saturation, out _value);
            InvalidateVisual();
        }
    }

    public HsvColorPicker()
    {
        MinHeight = 180;
        Focusable = true;
        Cursor = Cursors.Cross;
        SnapsToDevicePixels = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var size = new Size(Math.Max(1, ActualWidth), Math.Max(1, ActualHeight));
        var hueWidth = 22.0;
        var gap = 12.0;
        var sv = new Rect(0, 0, Math.Max(40, size.Width - hueWidth - gap), size.Height);
        var hueRect = new Rect(sv.Right + gap, 0, hueWidth, size.Height);

        dc.DrawRoundedRectangle(new SolidColorBrush(HsvToRgb(_hue, 1, 1)), null, sv, 7, 7);

        var whiteOverlay = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5)
        };
        whiteOverlay.GradientStops.Add(new GradientStop(Colors.White, 0));
        whiteOverlay.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1));
        dc.DrawRoundedRectangle(whiteOverlay, null, sv, 7, 7);

        var blackOverlay = new LinearGradientBrush
        {
            StartPoint = new Point(0.5, 0),
            EndPoint = new Point(0.5, 1)
        };
        blackOverlay.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
        blackOverlay.GradientStops.Add(new GradientStop(Colors.Black, 1));
        dc.DrawRoundedRectangle(blackOverlay, null, sv, 7, 7);

        var hueBrush = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        hueBrush.GradientStops.Add(new GradientStop(Colors.Red, 0.00));
        hueBrush.GradientStops.Add(new GradientStop(Colors.Yellow, 0.1667));
        hueBrush.GradientStops.Add(new GradientStop(Colors.Lime, 0.3333));
        hueBrush.GradientStops.Add(new GradientStop(Colors.Cyan, 0.5000));
        hueBrush.GradientStops.Add(new GradientStop(Colors.Blue, 0.6667));
        hueBrush.GradientStops.Add(new GradientStop(Colors.Magenta, 0.8333));
        hueBrush.GradientStops.Add(new GradientStop(Colors.Red, 1.00));
        dc.DrawRoundedRectangle(hueBrush, null, hueRect, 7, 7);

        var border = new Pen(new SolidColorBrush(Color.FromArgb(120, 125, 130, 136)), 1);
        dc.DrawRoundedRectangle(null, border, sv, 7, 7);
        dc.DrawRoundedRectangle(null, border, hueRect, 7, 7);

        var selectorX = sv.Left + _saturation * sv.Width;
        var selectorY = sv.Top + (1 - _value) * sv.Height;
        dc.DrawEllipse(null, new Pen(Brushes.White, 2.4), new Point(selectorX, selectorY), 7, 7);
        dc.DrawEllipse(null, new Pen(Brushes.Black, 1), new Point(selectorX, selectorY), 8.5, 8.5);

        var hueY = hueRect.Top + (_hue / 360.0) * hueRect.Height;
        var markerRect = new Rect(hueRect.Left - 3, hueY - 3, hueRect.Width + 6, 6);
        dc.DrawRoundedRectangle(Brushes.White, new Pen(Brushes.Black, 1), markerRect, 3, 3);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        CaptureMouse();
        var p = e.GetPosition(this);
        var (sv, hue) = GetRects();
        if (hue.Contains(p)) _draggingHue = true;
        else _draggingSv = true;
        UpdateFromPoint(p, sv, hue);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (e.LeftButton != MouseButtonState.Pressed || (!_draggingSv && !_draggingHue)) return;
        var (sv, hue) = GetRects();
        UpdateFromPoint(e.GetPosition(this), sv, hue);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_draggingSv && !_draggingHue) return;
        var (sv, hue) = GetRects();
        UpdateFromPoint(e.GetPosition(this), sv, hue);
        _draggingSv = false;
        _draggingHue = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    private (Rect sv, Rect hue) GetRects()
    {
        var hueWidth = 22.0;
        var gap = 12.0;
        var sv = new Rect(0, 0, Math.Max(40, ActualWidth - hueWidth - gap), Math.Max(1, ActualHeight));
        var hue = new Rect(sv.Right + gap, 0, hueWidth, Math.Max(1, ActualHeight));
        return (sv, hue);
    }

    private void UpdateFromPoint(Point p, Rect sv, Rect hue)
    {
        if (_draggingHue)
            _hue = Math.Clamp((p.Y - hue.Top) / Math.Max(1, hue.Height), 0, 1) * 360.0;
        else
        {
            _saturation = Math.Clamp((p.X - sv.Left) / Math.Max(1, sv.Width), 0, 1);
            _value = 1.0 - Math.Clamp((p.Y - sv.Top) / Math.Max(1, sv.Height), 0, 1);
        }
        InvalidateVisual();
        ColorChanged?.Invoke(this, new ColorChangedEventArgs(SelectedColor));
    }

    private static Color HsvToRgb(double hue, double saturation, double value)
    {
        hue = ((hue % 360) + 360) % 360;
        saturation = Math.Clamp(saturation, 0, 1);
        value = Math.Clamp(value, 0, 1);
        var c = value * saturation;
        var x = c * (1 - Math.Abs((hue / 60.0) % 2 - 1));
        var m = value - c;
        (double r, double g, double b) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x)
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static void RgbToHsv(Color color, out double hue, out double saturation, out double value)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        hue = d < 0.000001 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * (((b - r) / d) + 2) : 60 * (((r - g) / d) + 4);
        if (hue < 0) hue += 360;
        saturation = max <= 0 ? 0 : d / max;
        value = max;
    }
}
