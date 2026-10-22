using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MyLianPC.App.Services;
using MyLianPC.Core.Lighting;

namespace MyLianPC.App.Controls;

public partial class ColorPicker : UserControl
{
    private static readonly RgbColor[] BuiltIn =
    [
        RgbColor.ParseHex("#FF0000"), RgbColor.ParseHex("#FF6A00"), RgbColor.ParseHex("#FFD700"), RgbColor.ParseHex("#7CFF00"),
        RgbColor.ParseHex("#00FF40"), RgbColor.ParseHex("#00FFAA"), RgbColor.ParseHex("#00D7FF"), RgbColor.ParseHex("#0050FF"),
        RgbColor.ParseHex("#7A00FF"), RgbColor.ParseHex("#FF00C8"), RgbColor.ParseHex("#FFFFFF"), RgbColor.ParseHex("#FFB070"),
    ];

    private double _h, _s = 1, _v = 1;
    private bool _dragSv, _dragHue, _updating;

    /// <summary>Raised continuously while the user changes the color.</summary>
    public event EventHandler<RgbColor>? ColorChanged;

    public RgbColor Color { get; private set; } = RgbColor.ParseHex("#00FFAA");

    public ColorPicker()
    {
        InitializeComponent();
        Loaded += (_, _) => { RefreshPalette(); SetColor(Color, raise: false); };
        SizeChanged += (_, _) => UpdateThumbs();
    }

    public void SetColor(RgbColor c, bool raise = false)
    {
        Color = c;
        (_h, _s, _v) = ToHsv(c);
        UpdateVisuals();
        if (raise) ColorChanged?.Invoke(this, c);
    }

    private void Commit()
    {
        Color = RgbColor.FromHsv(_h, _s, _v);
        UpdateVisuals();
        ColorChanged?.Invoke(this, Color);
    }

    private void UpdateVisuals()
    {
        _updating = true;
        HueFill.Fill = new SolidColorBrush(RgbColor.FromHsv(_h).ToMedia());
        Swatch.Background = new SolidColorBrush(Color.ToMedia());
        if (!HexBox.IsKeyboardFocused) HexBox.Text = Color.ToHex();
        UpdateThumbs();
        _updating = false;
    }

    private void UpdateThumbs()
    {
        Canvas.SetLeft(SvThumb, _s * SvArea.ActualWidth - 7);
        Canvas.SetTop(SvThumb, (1 - _v) * SvArea.ActualHeight - 7);
        Canvas.SetTop(HueThumb, _h / 360 * HueArea.ActualHeight - 3);
    }

    private void SetSv(Point p)
    {
        _s = Math.Clamp(p.X / SvArea.ActualWidth, 0, 1);
        _v = Math.Clamp(1 - p.Y / SvArea.ActualHeight, 0, 1);
        Commit();
    }

    private void SetHue(Point p)
    {
        _h = Math.Clamp(p.Y / HueArea.ActualHeight, 0, 0.9999) * 360;
        Commit();
    }

    private void Sv_Down(object sender, MouseButtonEventArgs e) { _dragSv = true; SvArea.CaptureMouse(); SetSv(e.GetPosition(SvArea)); }
    private void Sv_Move(object sender, MouseEventArgs e) { if (_dragSv) SetSv(e.GetPosition(SvArea)); }
    private void Sv_Up(object sender, MouseButtonEventArgs e) { _dragSv = false; SvArea.ReleaseMouseCapture(); }
    private void Hue_Down(object sender, MouseButtonEventArgs e) { _dragHue = true; HueArea.CaptureMouse(); SetHue(e.GetPosition(HueArea)); }
    private void Hue_Move(object sender, MouseEventArgs e) { if (_dragHue) SetHue(e.GetPosition(HueArea)); }
    private void Hue_Up(object sender, MouseButtonEventArgs e) { _dragHue = false; HueArea.ReleaseMouseCapture(); }

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyHex();
    }

    private void HexBox_LostFocus(object sender, RoutedEventArgs e) => ApplyHex();

    private void ApplyHex()
    {
        if (_updating) return;
        if (RgbColor.TryParseHex(HexBox.Text, out var c)) SetColor(c, raise: true);
        else HexBox.Text = Color.ToHex();
    }

    private void PaletteColor_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is RgbColor c) SetColor(c, raise: true);
    }

    private void SaveColor_Click(object sender, RoutedEventArgs e)
    {
        var state = AppState.Current;
        var saved = state.Preferences.SavedColors.Where(x => x != Color).Prepend(Color).Take(24).ToList();
        state.SavePreferences(state.Preferences with { SavedColors = saved });
        RefreshPalette();
    }

    private void RefreshPalette() =>
        Palette.ItemsSource = AppState.Current.Preferences.SavedColors.Concat(BuiltIn.Where(b => !AppState.Current.Preferences.SavedColors.Contains(b))).ToList();

    private static (double H, double S, double V) ToHsv(RgbColor c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * ((g - b) / d % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        return ((h + 360) % 360, max == 0 ? 0 : d / max, max);
    }
}
