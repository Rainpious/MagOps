using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using MagOps.Reporting;

namespace MagOps.Desktop;

internal static class ReportQualityFigures
{
    public static IReadOnlyDictionary<string, byte[]> Render(ReportSnapshot snapshot)
    {
        var figures = new Dictionary<string, byte[]>();
        if (snapshot.Survey is { } survey)
        {
            Histogram("正式测线偏航距离分布", "偏航距离 m", survey.Points.Select(p => p.OffsetM));
            Histogram("正式测线实际飞高分布", "相对设计地形面的飞高 m", survey.Points.Select(p => p.AboveGroundM));
            var formalLines = survey.Mothers.Where(m => m.Role == "SURVEY").Select((m, i) => new QualityDynamicLine(i + 1,
                m.Name.Length > 0 ? m.Name : m.MotherId, i, (int)Math.Min(int.MaxValue, m.Points), 1,
                m.DynamicNoiseNt is null ? "INSUFFICIENT" : "AVAILABLE", m.DynamicNoiseNt, m.NoiseWindows, 0, "正式报告统计口径")).ToArray();
            if (formalLines.Length > 0)
                figures["正式测线动态噪声统计直方图"] = DynamicNoiseHistogram("测区航磁测线噪声统计直方图", formalLines,
                    $"正式测线原始磁通道；报告统计口径；梯度限值{snapshot.Inputs.GradientLimitNtKm:G6} nT/km；采样证据见附表", false);
            foreach (var repeat in survey.Repeats.Take(20))
            {
                if (repeat.Matches.Count == 0) continue;
                string title = "重复线原始磁对比 " + repeat.Name + (repeat.Excluded ? "（已人工排除）" : "");
                figures[title] = Plot(title, "沿原测线距离 m", "原始磁场 nT",
                    repeat.Matches.Select(p => new PlotPoint(p.ChainageM, p.OriginalMagNt, p.Fragment, p.CheckCsvRow)).ToArray(),
                    repeat.Matches.Select(p => new PlotPoint(p.ChainageM, p.CheckMagNt, p.Fragment, p.CheckCsvRow)).ToArray());
            }
        }
        foreach (var trial in snapshot.StaticTrials.Take(20))
        {
            if (trial.Kind == "GPS") continue;
            var start = trial.Curve.FirstOrDefault()?.Time;
            if (start is null || !trial.Curve.Any(p => p.RawMagNt is not null)) continue;
            string title = "独立静态磁场曲线 " + trial.Name;
            int fragment = 0; DateTimeOffset? previous = null;
            double interval = snapshot.Inputs.StaticTrials.Single(t => t.Name == trial.Name).SampleIntervalSeconds;
            figures[title] = Plot(title, "试验时间 s", "原始磁场 nT", trial.Curve.Select((p, i) =>
            {
                if (previous is { } time && (p.Time - time).TotalSeconds > interval * 1.2) fragment++;
                previous = p.Time;
                return new PlotPoint((p.Time - start.Value).TotalSeconds, p.RawMagNt, fragment.ToString(), i);
            }).ToArray(), []);
        }
        if (snapshot.QualityReference is { } reference)
            foreach (var image in RenderDynamicNoise(reference.Result)) figures.Add(image.Key, image.Value);
        return figures;

        void Histogram(string title, string xLabel, IEnumerable<double?> input)
        {
            double[] values = input.OfType<double>().Where(double.IsFinite).ToArray();
            if (values.Length == 0) return;
            double min = values.Min(), max = values.Max();
            if (max - min < 1e-9) { min -= .5; max += .5; }
            int count = Math.Clamp((int)Math.Ceiling(Math.Log2(values.Length) + 1), 6, 20);
            var bins = new int[count];
            foreach (double value in values) bins[Math.Clamp((int)((value - min) / (max - min) * count), 0, count - 1)]++;
            figures[title] = Canvas(title, xLabel, "数量", (g, area) =>
            {
                using var brush = new SolidBrush(Color.FromArgb(43, 112, 153));
                using var font = new Font("Microsoft YaHei", 12);
                for (int i = 0; i < bins.Length; i++)
                {
                    float width = area.Width / count, height = (float)bins[i] / Math.Max(1, bins.Max()) * area.Height;
                    g.FillRectangle(brush, area.Left + i * width + 1, area.Bottom - height, width - 2, height);
                }
                g.DrawString($"N={values.Length}    范围 {min:0.###} — {max:0.###}    最高频数 {bins.Max()}", font, Brushes.Black, area.Left, area.Bottom + 8);
            });
        }
    }

    public static IReadOnlyDictionary<string, byte[]> RenderDynamicNoise(QualityCheckResult result)
    {
        if (result.DynamicLines.Length == 0) return new Dictionary<string, byte[]>();
        string note = (result.DynamicAlgorithmVersion.Length > 0 ? result.DynamicAlgorithmVersion + "；" : "旧结果未记录算法版本；") +
            "sd(D4)/√70；梯度筛选" + (result.Request.FilterGradient ? $"开启 ≤{result.Request.GradientLimitNtKm:G6} nT/km" : "关闭") + "；采样证据见附表";
        return new Dictionary<string, byte[]> { ["动态噪声统计直方图（REFERENCE_ONLY）"] = DynamicNoiseHistogram(
            "测区航磁测线噪声统计直方图（独立验算参考）", result.DynamicLines, note, true) };
    }

    private static byte[] DynamicNoiseHistogram(string title, QualityDynamicLine[] rows, string note, bool referenceOnly)
    {
        var bins = QualityCheckService.HistogramBins(rows);
        double[] values = rows.Where(l => l.SvNt is { } v && double.IsFinite(v)).Select(l => l.SvNt!.Value).ToArray();
        int missing = rows.Length - values.Length;
        string stats = values.Length == 0 ? $"有效线数 N=0；缺值 {missing} 条" :
            $"最大 {values.Max():0.######} nT；最小 {values.Min():0.######} nT；平均 {values.Average():0.######} nT；有效线数 N={values.Length}；缺值 {missing} 条";
        using var bitmap = new Bitmap(1200, 760);
        using var g = Graphics.FromImage(bitmap); g.Clear(Color.White); g.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = new Font("Microsoft YaHei", 13); using var titleFont = new Font("Microsoft YaHei", 20, FontStyle.Bold);
        using var brush = new SolidBrush(Color.FromArgb(43, 112, 153));
        g.DrawString(title, titleFont, Brushes.Black, new RectangleF(60, 18, 1080, 60));
        g.DrawString(note, font, Brushes.DimGray, new RectangleF(60, 82, 1080, 30));
        g.DrawString(stats, font, Brushes.Black, new RectangleF(60, 122, 1080, 30));
        const int left = 120, right = 1100, top = 190, bottom = 545;
        if (values.Length == 0 || bins.Length == 0)
        {
            g.DrawRectangle(Pens.Black, left, top, right - left, bottom - top);
            using var emptyFont = new Font("Microsoft YaHei", 20, FontStyle.Bold);
            const string empty = "无有效逐线 Sv，无法绘图";
            var size = g.MeasureString(empty, emptyFont);
            g.DrawString(empty, emptyFont, Brushes.DimGray, (1200 - size.Width) / 2, (top + bottom - size.Height) / 2);
            g.DrawString("请检查逐线表中的有效窗口和缺值状态。", font, Brushes.DimGray, left, bottom + 18);
            g.DrawString(referenceOnly ? "独立验算参考，非正式合格结论。" : "按正式报告已选数据统计。", font, Brushes.DimGray, 60, 700);
            using var emptyStream = new MemoryStream(); bitmap.Save(emptyStream, ImageFormat.Png); return emptyStream.ToArray();
        }
        int maxFrequency = Math.Max(1, bins.Select(b => b.Count).DefaultIfEmpty(0).Max());
        for (int tick = 0; tick <= 4; tick++)
        {
            float y = bottom - (bottom - top) * tick / 4f;
            g.DrawLine(Pens.Gainsboro, left, y, right, y);
            g.DrawString((maxFrequency * tick / 4d).ToString("0", System.Globalization.CultureInfo.InvariantCulture), font, Brushes.DimGray, 65, y - 10);
        }
        g.DrawLine(Pens.Black, left, top, left, bottom); g.DrawLine(Pens.Black, left, bottom, right, bottom);
        float slot = bins.Length == 0 ? right - left : (right - left) / (float)bins.Length;
        for (int i = 0; i < bins.Length; i++)
        {
            var bin = bins[i]; float height = bin.Count / (float)maxFrequency * (bottom - top);
            float x = left + i * slot;
            g.FillRectangle(brush, x + 1, bottom - height, Math.Max(1, slot - 2), height);
            g.DrawString(bin.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), font, Brushes.Black, x + slot / 2 - 8, bottom - height - 24);
            if (bins.Length <= 8 || i % Math.Max(1, (int)Math.Ceiling(bins.Length / 8d)) == 0)
                g.DrawString(bin.Lower.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture), font, Brushes.DimGray, x - 10, bottom + 8);
        }
        double xmax = bins.LastOrDefault()?.Upper ?? 0;
        g.DrawString(xmax.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture), font, Brushes.DimGray, right - 45, bottom + 8);
        g.DrawString("线数", font, Brushes.Black, 75, top - 28);
        g.DrawString("各测线动态噪声 Sv（nT）", font, Brushes.Black, 450, 595);
        using var noteFont = new Font("Microsoft YaHei", 11);
        g.DrawString("每个柱形表示该噪声区间内的母测线数量；逐线号与数值见“动态噪声逐线统计.csv”。", noteFont, Brushes.DimGray, 60, 635);
        string binNote = "横轴为分箱下限（nT），区间左闭右开，末箱包含最大值；完整区间见分箱CSV。" +
            (values.Max() - values.Min() < 1e-12 ? " 所有有效Sv相同，箱界仅为显示扩展。" : "");
        g.DrawString(binNote, noteFont, Brushes.DimGray, 60, 662);
        g.DrawString(referenceOnly ? "独立验算参考，非正式合格结论。" : "按正式报告已选数据统计。", noteFont, Brushes.DimGray, 60, 689);
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
    }

    private sealed record PlotPoint(double X, double? Y, string Segment, long Row);
    private static byte[] Plot(string title, string xLabel, string yLabel, PlotPoint[] original, PlotPoint[] check)
    {
        var all = original.Concat(check).Where(p => p.Y is { } y && double.IsFinite(y)).ToArray();
        double minX = all.Min(p => p.X), maxX = all.Max(p => p.X), minY = all.Min(p => p.Y!.Value), maxY = all.Max(p => p.Y!.Value);
        if (maxX - minX < 1e-9) maxX = minX + 1;
        if (maxY - minY < 1e-9) { minY -= .5; maxY += .5; }
        return Canvas(title, xLabel, yLabel, (g, area) =>
        {
            void Series(PlotPoint[] rows, Color color)
            {
                using var pen = new Pen(color, 2);
                using var brush = new SolidBrush(color);
                PlotPoint? previous = null;
                PointF? previousPixel = null;
                // Thinning is only for drawing, never for the source statistics/CSV.
                int stride = Math.Max(1, rows.Length / 10000);
                for (int i = 0; i < rows.Length; i++)
                {
                    var p = rows[i];
                    bool continuous = previous is { } last && last.Segment == p.Segment && Math.Abs(last.Row - p.Row) == 1 && last.Y is not null;
                    if (!continuous) previousPixel = null;
                    if (p.Y is not { } y) { previous = p; previousPixel = null; continue; }
                    var pixel = new PointF(area.Left + (float)((p.X - minX) / (maxX - minX) * area.Width),
                        area.Bottom - (float)((y - minY) / (maxY - minY) * area.Height));
                    if (i % stride == 0 || !continuous || i == rows.Length - 1)
                    {
                        if (previousPixel is { } lastPixel) g.DrawLine(pen, lastPixel, pixel);
                        else g.FillEllipse(brush, pixel.X - 2, pixel.Y - 2, 4, 4);
                        previousPixel = pixel;
                    }
                    previous = p;
                }
            }
            Series(original, Color.FromArgb(29, 100, 156)); Series(check, Color.FromArgb(191, 80, 45));
            using var font = new Font("Microsoft YaHei", 12);
            g.DrawString($"X {minX:0.###} — {maxX:0.###}    Y {minY:0.######} — {maxY:0.######}" +
                (check.Length > 0 ? "    蓝：原测    橙：检查" : ""), font, Brushes.Black, area.Left, area.Bottom + 8);
        });
    }
    private static byte[] Canvas(string title, string xLabel, string yLabel, Action<Graphics, RectangleF> draw)
    {
        using var bitmap = new Bitmap(1200, 700); using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White); graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var titleFont = new Font("Microsoft YaHei", 21, FontStyle.Bold); using var labelFont = new Font("Microsoft YaHei", 14);
        graphics.DrawString(title, titleFont, Brushes.Black, new RectangleF(70, 20, 1080, 65));
        graphics.DrawString(yLabel, labelFont, Brushes.Black, 70, 85);
        graphics.DrawLine(Pens.Black, 80, 120, 80, 550); graphics.DrawLine(Pens.Black, 80, 550, 1140, 550);
        draw(graphics, new RectangleF(82, 122, 1056, 425));
        graphics.DrawString(xLabel, labelFont, Brushes.Black, 80, 610);
        graphics.DrawString("范围以本次报告选用数据为准；图件为计算参考，质量结论需人工审核。", labelFont, Brushes.DimGray, 80, 655);
        using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
    }
}
