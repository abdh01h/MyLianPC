using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using MyLianPC.Core.Cooling;

namespace MyLianPC.App.Controls;

/// <summary>
/// Interactive temperature → duty curve. Drag points; double-click empty space to add a point;
/// right-click a point to remove it. Points stay sorted and can never cross a neighbour.
/// </summary>
public sealed class FanCurveEditor : FrameworkElement
{
    public static readonly DependencyProperty TextBrushProperty = Reg(nameof(TextBrush), Brushes.Gray);
    public static readonly DependencyProperty GridBrushProperty = Reg(nameof(GridBrush), Brushes.DimGray);
    public static readonly DependencyProperty AccentBrushProperty = Reg(nameof(AccentBrush), Brushes.DeepSkyBlue);

    private static DependencyProperty Reg(string name, Brush def) =>
        DependencyProperty.Register(name, typeof(Brush), typeof(FanCurveEditor), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush AccentBrush { get => (Brush)GetValue(AccentBrushProperty); set => SetValue(AccentBrushProperty, value); }

    private List<CurvePoint> _points = [];
    private int _drag = -1;
    private double _minTemp = 20, _maxTemp = 100;
    private double? _current;

    public FanCurveEditor()
    {
        // Points outside the plotted temperature range must never paint over the neighbouring cards.
        ClipToBounds = true;
    }

    public event EventHandler? CurveChanged;

    public IReadOnlyList<CurvePoint> Points => _points;
    public bool IsEditable { get; set; } = true;
    public string TemperatureLabel { get; set; } = "°C";

    public void SetCurve(IReadOnlyList<CurvePoint> points, double minTemp, double maxTemp)
    {
        _points = [.. points.OrderBy(p => p.TemperatureC)];
        (_minTemp, _maxTemp) = (minTemp, maxTemp);
        InvalidateVisual();
    }

    public void SetCurrent(double? temperature)
    {
        _current = temperature;
        InvalidateVisual();
    }

    private Rect Plot => new(44, 10, Math.Max(1, ActualWidth - 56), Math.Max(1, ActualHeight - 36));

    private Point ToScreen(CurvePoint p) =>
        new(Plot.Left + Plot.Width * (p.TemperatureC - _minTemp) / (_maxTemp - _minTemp), Plot.Bottom - Plot.Height * p.DutyPercent / 100.0);

    private CurvePoint FromScreen(Point s) =>
        new(Math.Round(Math.Clamp(_minTemp + (s.X - Plot.Left) / Plot.Width * (_maxTemp - _minTemp), _minTemp, _maxTemp)),
            (int)Math.Round(Math.Clamp((Plot.Bottom - s.Y) / Plot.Height * 100, 0, 100)));

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 80 || ActualHeight < 60) return;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight)); // hit-testable background
        var plot = Plot;
        var grid = new Pen(GridBrush, 1) { DashStyle = DashStyles.Dot };
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        FormattedText Label(string s) => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, TextBrush, dpi);
        for (int d = 0; d <= 100; d += 20)
        {
            double y = plot.Bottom - plot.Height * d / 100;
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
            var t = Label($"{d}%");
            dc.DrawText(t, new Point(plot.Left - t.Width - 6, y - t.Height / 2));
        }
        double step = (_maxTemp - _minTemp) > 40 ? 10 : 5;
        for (double temp = _minTemp; temp <= _maxTemp; temp += step)
        {
            double x = plot.Left + plot.Width * (temp - _minTemp) / (_maxTemp - _minTemp);
            dc.DrawLine(grid, new Point(x, plot.Top), new Point(x, plot.Bottom));
            var t = Label($"{temp:0}{TemperatureLabel}");
            dc.DrawText(t, new Point(x - t.Width / 2, plot.Bottom + 4));
        }
        if (_points.Count == 0) return;

        var pen = new Pen(AccentBrush, 2.5) { LineJoin = PenLineJoin.Round };
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(new Point(plot.Left, ToScreen(_points[0]).Y), false, false);
            foreach (var p in _points) g.LineTo(ToScreen(p), true, true);
            g.LineTo(new Point(plot.Right, ToScreen(_points[^1]).Y), true, true);
        }
        dc.DrawGeometry(null, pen, geo);
        foreach (var p in _points)
        {
            var s = ToScreen(p);
            dc.DrawEllipse(AccentBrush, new Pen(TextBrush, 1), s, 6, 6);
        }
        if (_current is { } cur && cur >= _minTemp && cur <= _maxTemp)
        {
            double x = plot.Left + plot.Width * (cur - _minTemp) / (_maxTemp - _minTemp);
            dc.DrawLine(new Pen(Brushes.OrangeRed, 1.5), new Point(x, plot.Top), new Point(x, plot.Bottom));
            var t = Label($"now {cur:0}{TemperatureLabel}");
            dc.DrawText(t, new Point(Math.Min(x + 4, plot.Right - t.Width), plot.Top));
        }
        if (_drag >= 0)
        {
            var p = _points[_drag];
            var t = Label($"{p.TemperatureC:0}{TemperatureLabel} → {p.DutyPercent}%");
            var s = ToScreen(p);
            dc.DrawText(t, new Point(Math.Clamp(s.X - t.Width / 2, plot.Left, plot.Right - t.Width), Math.Max(plot.Top, s.Y - 26)));
        }
    }

    private int HitTestPoint(Point pos)
    {
        for (int i = 0; i < _points.Count; i++)
            if ((ToScreen(_points[i]) - pos).Length < 10) return i;
        return -1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!IsEditable) return;
        var pos = e.GetPosition(this);
        if (e.ClickCount == 2 && HitTestPoint(pos) < 0 && _points.Count < 10)
        {
            var np = FromScreen(pos);
            if (_points.All(p => Math.Abs(p.TemperatureC - np.TemperatureC) >= 1))
            {
                _points.Add(np);
                _points = [.. _points.OrderBy(p => p.TemperatureC)];
                CurveChanged?.Invoke(this, EventArgs.Empty);
                InvalidateVisual();
            }
            return;
        }
        _drag = HitTestPoint(pos);
        if (_drag >= 0) CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Cursor = IsEditable && HitTestPoint(e.GetPosition(this)) >= 0 ? Cursors.Hand : Cursors.Arrow;
        if (_drag < 0 || e.LeftButton != MouseButtonState.Pressed) return;
        var np = FromScreen(e.GetPosition(this));
        double lo = _drag > 0 ? _points[_drag - 1].TemperatureC + 1 : _minTemp;
        double hi = _drag < _points.Count - 1 ? _points[_drag + 1].TemperatureC - 1 : _maxTemp;
        _points[_drag] = new CurvePoint(Math.Clamp(np.TemperatureC, lo, hi), np.DutyPercent);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag < 0) return;
        _drag = -1;
        ReleaseMouseCapture();
        CurveChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (!IsEditable) return;
        int i = HitTestPoint(e.GetPosition(this));
        if (i >= 0 && _points.Count > 2)
        {
            _points.RemoveAt(i);
            CurveChanged?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
        }
    }
}
