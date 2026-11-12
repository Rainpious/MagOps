using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using MagOps.Reporting;

namespace MagOps.Desktop;

internal static class QualityOffsetFigure
{
    public static byte[] Render(QualityCheckResult result)
    {
        var values = result.Details.Where(d => d.Metric == "OFFSET" && d.Value is not null).Select(d => d.Value!.Value).ToArray();
        using var bitmap = new Bitmap(1200, 760);
        using var g = Graphics.FromImage(bitmap); g.Clear(Color.White); g.SmoothingMode = SmoothingMode.AntiAlias;
        using var title = new Font("Microsoft YaHei", 20, FontStyle.Bold); using var font = new Font("Microsoft YaHei", 12);
        g.DrawString(QualityCheckService.OffsetBasis(result.Request) + "分布", title, Brushes.Black, 60, 25);
        g.DrawString($"有效观测点 {values.Length}；未计算点 {result.OffsetLines.Sum(l => l.InputPoints - l.ValidPoints)}；大偏差不按匹配容差剔除", font, Brushes.DimGray, 60, 90);
        if (values.Length > 0)
        {
            double max = Math.Max(.001, values.Max()); const int count = 12;
            var bins = new int[count]; foreach (double v in values) bins[Math.Clamp((int)(v / max * count), 0, count - 1)]++;
            const int left = 120, top = 180, right = 1110, bottom = 570;
            int frequency = Math.Max(1, bins.Max());
            for (int tick = 0; tick <= 4; tick++)
            {
                float y = bottom - (bottom - top) * tick / 4f;
                g.DrawLine(Pens.Gainsboro, left, y, right, y);
                g.DrawString((frequency * tick / 4d).ToString("0"), font, Brushes.DimGray, 55, y - 8);
            }
            g.DrawLine(Pens.Black, left, top, left, bottom); g.DrawLine(Pens.Black, left, bottom, right, bottom);
            for (int i = 0; i < count; i++)
            {
                float width = (right - left) / (float)count, height = bins[i] * (bottom - top) / (float)frequency;
                g.FillRectangle(Brushes.SteelBlue, left + i * width + 2, bottom - height, width - 4, height);
                if (i % 2 == 0) g.DrawString((max * i / count).ToString("0.###"), font, Brushes.Black, left + i * width - 10, bottom + 8);
            }
            g.DrawString(max.ToString("0.###"), font, Brushes.Black, right - 20, bottom + 8);
            g.DrawString(result.Request.OffsetReferenceMode == "ACTUAL_FIT" ? "实测拟合偏离（米）" : "水平偏航距（米）", font, Brushes.Black, 500, 625);
            g.DrawString("观测点数", font, Brushes.Black, 20, 145);
            g.DrawString($"平均 {values.Average():0.###} m；最大 {values.Max():0.###} m；仅代表当前参考和可计算观测点", font, Brushes.Black, 60, 130);
        }
        g.DrawString("按观测点统计，未按航程加权；端外和未对应记录不补零；不自动判合格。", font, Brushes.DimGray, 60, 700);
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
    }
}
