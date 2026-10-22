using SkiaSharp;

namespace MyLianPC.Lcd;

/// <summary>A decoded image or animation scaled to the 480x480 panel (center-crop "cover" fit).</summary>
public sealed class LcdMedia : IDisposable
{
    public const int MaxFrames = 600;
    public const int MinFrameMs = 66; // ~15 fps: what the panel's JPEG path sustains comfortably

    public IReadOnlyList<SKBitmap> Frames { get; }
    public IReadOnlyList<int> DurationsMs { get; }
    public bool IsAnimated => Frames.Count > 1;

    private LcdMedia(IReadOnlyList<SKBitmap> frames, IReadOnlyList<int> durations)
    {
        Frames = frames;
        DurationsMs = durations;
    }

    public static readonly IReadOnlySet<string> SupportedExtensions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    /// <summary>Decodes a still image or every frame of an animated GIF/WebP. Throws <see cref="InvalidDataException"/> for unsupported data.</summary>
    public static LcdMedia Load(byte[] data)
    {
        using var stream = new SKMemoryStream(data);
        using var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("Unsupported or corrupt image file.");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var frames = new List<SKBitmap>();
        var durations = new List<int>();
        int count = Math.Clamp(codec.FrameCount, 1, MaxFrames);
        var frameInfo = codec.FrameInfo;
        using var full = new SKBitmap(info);
        for (int i = 0; i < count; i++)
        {
            int required = frameInfo.Length > i ? frameInfo[i].RequiredFrame : -1;
            var opts = new SKCodecOptions(i, required);
            if (required < 0) full.Erase(SKColors.Transparent);
            var result = codec.GetPixels(info, full.GetPixels(), opts);
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) { if (i == 0) throw new InvalidDataException($"Could not decode image ({result})."); break; }
            frames.Add(Cover(full));
            durations.Add(Math.Max(MinFrameMs, frameInfo.Length > i && frameInfo[i].Duration > 0 ? frameInfo[i].Duration : 100));
        }
        return new LcdMedia(frames, durations);
    }

    private static SKBitmap Cover(SKBitmap src)
    {
        var dst = new SKBitmap(LcdRenderer.Size, LcdRenderer.Size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var c = new SKCanvas(dst);
        c.Clear(SKColors.Black);
        float scale = Math.Max((float)LcdRenderer.Size / src.Width, (float)LcdRenderer.Size / src.Height);
        float w = src.Width * scale, h = src.Height * scale;
        var rect = SKRect.Create((LcdRenderer.Size - w) / 2, (LcdRenderer.Size - h) / 2, w, h);
        using var img = SKImage.FromBitmap(src);
        using var paint = new SKPaint { IsAntialias = true };
        c.DrawImage(img, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
        return dst;
    }

    public void Dispose()
    {
        foreach (var f in Frames) f.Dispose();
    }
}
