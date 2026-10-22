using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace MyLianPC.App.Controls;

/// <summary>
/// One line on a chart. <paramref name="Scale"/> and <paramref name="Offset"/> convert the stored values while
/// drawing (used for the °C → °F switch), so the history itself is never copied or rewritten.
/// </summary>
public sealed record ChartSeries(string Label, Color Color, IReadOnlyList<double?> Values, double Scale = 1, double Offset = 0);

/// <summary>Lightweight time-series chart (no external dependency). Values are drawn left (old) to right (new).</summary>
public sealed class LineChart : FrameworkElement
{
    private static readonly Typeface Face = new("Segoe UI");
    private static readonly Dictionary<Color, Pen> Pens = [];

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(nameof(TextBrush), typeof(Brush), typeof(LineChart),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(nameof(GridBrush), typeof(Brush), typeof(LineChart),
        new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush TextBrush { get => (Brush)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    public double? FixedMin { get; set; }
    public double? FixedMax { get; set; }
    public string Unit { get; set; } = "";
    public int Capacity { get; set; } = 300;

    private IReadOnlyList<ChartSeries> _series = [];

    public void SetSeries(IReadOnlyList<ChartSeries> series)
    {
        _series = series;
        InvalidateVisual();
    }

    /// <summary>Frozen pen per series colour, so a redraw does not create brushes and pens every second.</summary>
    private static Pen PenFor(Color color)
    {
        lock (Pens)
        {
            if (Pens.TryGetValue(color, out var pen)) return pen;
            pen = new Pen(new SolidColorBrush(color), 2) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            Pens[color] = pen;
            return pen;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 40 || h < 30) return;
        const double left = 40, bottom = 18, top = 6;
        var plot = new Rect(left, top, Math.Max(1, w - left - 8), Math.Max(1, h - top - bottom));

        // One pass over the samples for the range (no LINQ, no copies of the series).
        double min = double.MaxValue, max = double.MinValue;
        foreach (var s in _series)
        {
            var values = s.Values;
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] is not { } v) continue;
                v = v * s.Scale + s.Offset;
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }
        if (min > max) { min = 0; max = 100; }
        if (FixedMin is { } fixedMin) min = fixedMin; else min = Math.Floor(min / 10) * 10;
        if (FixedMax is { } fixedMax) max = fixedMax; else max = Math.Ceiling(max / 10) * 10;
        if (max - min < 10) max = min + 10;

        var gridPen = new Pen(GridBrush, 1) { DashStyle = DashStyles.Dot };
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int i = 0; i <= 4; i++)
        {
            double y = plot.Bottom - plot.Height * i / 4;
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = new FormattedText($"{min + (max - min) * i / 4:0}{Unit}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10.5, TextBrush, dpi);
            dc.DrawText(label, new Point(plot.Left - label.Width - 6, y - label.Height / 2));
        }
        var axisLabel = new FormattedText($"last {Capacity} samples", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10.5, TextBrush, dpi);
        dc.DrawText(axisLabel, new Point(plot.Right - axisLabel.Width, plot.Bottom + 2));

        foreach (var s in _series)
        {
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                bool drawing = false;
                var values = s.Values;
                int offset = Capacity - values.Count;
                for (int i = 0; i < values.Count; i++)
                {
                    if (values[i] is not { } v) { drawing = false; continue; }
                    v = v * s.Scale + s.Offset;
                    var p = new Point(plot.Left + plot.Width * (i + offset) / Math.Max(1, Capacity - 1),
                                      plot.Bottom - plot.Height * Math.Clamp((v - min) / (max - min), 0, 1));
                    if (!drawing) { g.BeginFigure(p, false, false); drawing = true; }
                    else g.LineTo(p, true, true);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, PenFor(s.Color), geo);
        }
    }
}

/// <summary>270° arc gauge.</summary>
public sealed class Gauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = Reg(nameof(Value), typeof(double?), null);
    public static readonly DependencyProperty MaximumProperty = Reg(nameof(Maximum), typeof(double), 100.0);
    public static readonly DependencyProperty TextProperty = Reg(nameof(Text), typeof(string), "--");
    public static readonly DependencyProperty LabelProperty = Reg(nameof(Label), typeof(string), "");
    public static readonly DependencyProperty AccentProperty = Reg(nameof(Accent), typeof(Brush), Brushes.DeepSkyBlue);
    public static readonly DependencyProperty TrackProperty = Reg(nameof(Track), typeof(Brush), Brushes.DimGray);
    public static readonly DependencyProperty ForegroundProperty = Reg(nameof(Foreground), typeof(Brush), Brushes.White);
    public static readonly DependencyProperty SecondaryProperty = Reg(nameof(Secondary), typeof(Brush), Brushes.Gray);

    private static DependencyProperty Reg(string name, Type t, object? def) =>
        DependencyProperty.Register(name, t, typeof(Gauge), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    public double? Value { get => (double?)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Brush Secondary { get => (Brush)GetValue(SecondaryProperty); set => SetValue(SecondaryProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 20) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double thick = Math.Max(6, size / 12), r = size / 2 - thick;
        dc.DrawGeometry(null, new Pen(Track, thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(c, r, 135, 270));
        if (Value is { } v && v > 0)
            dc.DrawGeometry(null, new Pen(Accent, thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Arc(c, r, 135, 270 * Math.Clamp(v / Maximum, 0.001, 1)));
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var big = new FormattedText(Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), size / 5, Foreground, dpi);
        dc.DrawText(big, new Point(c.X - big.Width / 2, c.Y - big.Height / 2 - size * 0.03));
        var small = new FormattedText(Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), Math.Max(10, size / 12), Secondary, dpi);
        dc.DrawText(small, new Point(c.X - small.Width / 2, c.Y + big.Height / 2 - size * 0.02));
    }

    private static Geometry Arc(Point c, double r, double startDeg, double sweepDeg)
    {
        Point P(double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(startDeg), false, false);
            ctx.ArcTo(P(startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        return g;
    }
}
