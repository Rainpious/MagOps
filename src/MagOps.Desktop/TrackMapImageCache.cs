using System.Drawing.Drawing2D;

namespace MagOps.Desktop;

/// <summary>Owns both display bitmaps. Failed builds never replace a usable image or cache key.</summary>
internal sealed class TrackMapImageCache : IDisposable
{
    internal Bitmap? Overview { get; private set; }
    internal Bitmap? Detail { get; private set; }
    internal (int Revision, Size Size)? OverviewKey { get; private set; }
    internal (int Revision, Size Size, double Zoom, PointF Offset)? DetailKey { get; private set; }
    internal PointF Padding { get; private set; }
    internal long PixelBytes => Bytes(Overview) + Bytes(Detail);
    private static long Bytes(Bitmap? image) => image is null ? 0 : (long)image.Width * image.Height * 4;

    internal void BuildOverview((int Revision, Size Size) key, Color background, Action<Graphics> draw)
    {
        var image = Build(key.Size, background, draw);
        var prior = Overview;
        Overview = image; OverviewKey = key;
        prior?.Dispose();
    }

    internal void BuildDetail((int Revision, Size Size, double Zoom, PointF Offset) key,
        Size size, PointF padding, Action<Graphics> draw)
    {
        var image = Build(size, Color.Transparent, draw);
        var prior = Detail;
        Detail = image; DetailKey = key; Padding = padding;
        prior?.Dispose();
    }

    private static Bitmap Build(Size size, Color background, Action<Graphics> draw)
    {
        var image = new Bitmap(size.Width, size.Height);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(background); graphics.SmoothingMode = SmoothingMode.AntiAlias;
            draw(graphics);
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    public void Dispose()
    {
        Overview?.Dispose(); Detail?.Dispose();
        Overview = null; Detail = null; OverviewKey = null; DetailKey = null;
        Padding = PointF.Empty;
    }
}
