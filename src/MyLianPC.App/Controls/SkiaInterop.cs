using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace MyLianPC.App.Controls;

public static class SkiaInterop
{
    /// <summary>Copies a Skia bitmap into a frozen WPF bitmap.</summary>
    public static BitmapSource ToBitmapSource(SKBitmap src)
    {
        using var bgra = src.ColorType == SKColorType.Bgra8888 ? src.Copy() : src.Copy(SKColorType.Bgra8888);
        var wb = new WriteableBitmap(bgra.Width, bgra.Height, 96, 96, PixelFormats.Pbgra32, null);
        wb.WritePixels(new Int32Rect(0, 0, bgra.Width, bgra.Height), bgra.GetPixels(), bgra.ByteCount, bgra.RowBytes);
        wb.Freeze();
        return wb;
    }
}
