using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using MyLianPC.Core.Devices;
using MyLianPC.Core.Lighting;

namespace MyLianPC.App.Controls;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is Visibility.Visible) ^ Invert;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Device state → status color (same hues in both themes; text on top uses white).</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Ok = Freeze(Color.FromRgb(0x1F, 0x9D, 0x55));
    private static readonly SolidColorBrush Warn = Freeze(Color.FromRgb(0xC2, 0x7C, 0x0E));
    private static readonly SolidColorBrush Bad = Freeze(Color.FromRgb(0xC4, 0x2B, 0x1C));
    private static readonly SolidColorBrush Neutral = Freeze(Color.FromRgb(0x6B, 0x72, 0x80));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DeviceState.Connected => Ok,
        DeviceState.Busy or DeviceState.NeedsDriver or DeviceState.Disconnected => Warn,
        DeviceState.Unresponsive or DeviceState.Error => Bad,
        _ => Neutral,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

public sealed class ColorToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is RgbColor c ? new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)) : Brushes.Transparent;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public static class ColorExtensions
{
    public static Color ToMedia(this RgbColor c) => Color.FromRgb(c.R, c.G, c.B);
    public static RgbColor ToRgb(this Color c) => new(c.R, c.G, c.B);
}
