using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using MagOps.Reporting;

namespace MagOps.Desktop;

internal static class ReportTrackFigure
{
    public static byte[] Render(IReadOnlyList<ReportTrackPoint> points)
    {
        if (points.Count == 0) throw new InvalidDataException("没有可绘制的保留轨迹。");
        using var bitmap = new Bitmap(1280, 800);
        using var g = Graphics.FromImage(bitmap); g.Clear(Color.White); g.SmoothingMode = SmoothingMode.AntiAlias;
        double latitude = points.Average(p => p.Latitude), longitude = points.Average(p => p.Longitude);
        double factor = Math.Cos(latitude * Math.PI / 180);
        var xy = points.Select(p => (X: (p.Longitude - longitude) * factor * 111195, Y: (p.Latitude - latitude) * 111195)).ToArray();
        double minX = xy.Min(p => p.X), minY = xy.Min(p => p.Y), maxX = xy.Max(p => p.X), maxY = xy.Max(p => p.Y);
        double scale = Math.Min(1160 / Math.Max(1, maxX - minX), 650 / Math.Max(1, maxY - minY));
        PointF Project(int i) => new((float)(640 + (xy[i].X - (minX + maxX) / 2) * scale),
            (float)(405 - (xy[i].Y - (minY + maxY) / 2) * scale));
        using var pen = new Pen(Color.FromArgb(0x00, 0x77, 0xBB), 1.5f);
        for (int i = 1; i < points.Count; i++)
            if (points[i].Segment == points[i - 1].Segment) g.DrawLine(pen, Project(i - 1), Project(i));
        using var title = new Font("Microsoft YaHei", 18);
        using var label = new Font("Microsoft YaHei", 11);
        g.DrawString("最终 CSV 保留作业轨迹", title, Brushes.Black, 32, 18);
        g.DrawString("北 ↑    局部等比例示意    不同 Pass 及时间断续处断开", label, Brushes.DimGray, 32, 760);
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
    }
}
