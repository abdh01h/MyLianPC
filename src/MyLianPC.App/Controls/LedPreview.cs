using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using MyLianPC.Core.Lighting;

namespace MyLianPC.App.Controls;

/// <summary>Animated row of LEDs showing what an effect will look like (uses the same generator as the pump ring).</summary>
public sealed class LedPreview : FrameworkElement
{
    private readonly DispatcherTimer _timer = new();
    private EffectFrames.Animation? _anim;
    private int _frame;

    public int LedCount { get; set; } = 24;

    public LedPreview()
    {
        _timer.Tick += (_, _) => { _frame++; InvalidateVisual(); };
        IsVisibleChanged += (_, _) => UpdateTimer();
    }

    public void SetLighting(ZoneLighting lighting)
    {
        _anim = EffectFrames.Generate(lighting, LedCount);
        _frame = 0;
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(20, _anim.FrameIntervalMs));
        UpdateTimer();
        InvalidateVisual();
    }

    private void UpdateTimer()
    {
        if (IsVisible && _anim is { Frames.Count: > 1 }) _timer.Start();
        else _timer.Stop();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_anim is null || ActualWidth < 10) return;
        var frame = _anim.Frames[_frame % _anim.Frames.Count];
        double step = ActualWidth / frame.Length, r = Math.Min(step * 0.38, ActualHeight / 2 - 2);
        for (int i = 0; i < frame.Length; i++)
        {
            var c = frame[i];
            var center = new Point(step * (i + 0.5), ActualHeight / 2);
            var glow = new RadialGradientBrush(Color.FromArgb(150, c.R, c.G, c.B), Color.FromArgb(0, c.R, c.G, c.B));
            dc.DrawEllipse(glow, null, center, r * 1.8, r * 1.8);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), new Pen(new SolidColorBrush(Color.FromArgb(60, 128, 128, 128)), 1), center, r, r);
        }
    }
}
