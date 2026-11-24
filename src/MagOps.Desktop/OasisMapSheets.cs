using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO.Compression;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal static class OasisMapSheets
{
    private static readonly Color Ink = Color.FromArgb(31, 58, 70);
    private static readonly Color Muted = Color.FromArgb(77, 101, 113);
    private static readonly Color Rule = Color.FromArgb(148, 165, 173);

    private static void Text(Graphics g, string value, float x, float y, float size, bool bold = false,
        bool center = false, float maxWidth = 0)
    {
        using var font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(bold ? Ink : Muted);
        using var sf = new StringFormat { Alignment = center ? StringAlignment.Center : StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        if (maxWidth > 0) g.DrawString(value, font, brush, new RectangleF(x, y, maxWidth, 2 * size), sf);
        else g.DrawString(value, font, brush, new PointF(x, y), sf);
    }

    internal static void Single(OasisMapProduct product, OasisMapLegend legend, Bitmap raster)
    {
        bool wide = raster.Width >= raster.Height;
        using var sheet = new Bitmap(wide ? 2480 : 1754, wide ? 1754 : 2480);
        int w = sheet.Width, h = sheet.Height;
        using var g = Graphics.FromImage(sheet);
        Setup(g);
        Text(g, product.AreaTitle, 100, 65, 32, maxWidth: w - 200);
        Text(g, legend.Title, 100, 120, 57, true, maxWidth: w - 200);
        Text(g, $"格距 {product.Grid.Cell:G} m    ·    {legend.Caption}", 100, 220, 32);
        Plot(g, product.Grid, raster, new RectangleF(210, 332, w - 395, h - 805), scaleBar: true);
        Bar(g, legend, new RectangleF(w * .22f, h - 305, w * .56f, 28), legend.Unit);
        Footer(g, product, 100, h - 100, 29);
        Save(sheet, product, legend.Name);
    }

    internal static void Compare(OasisMapProduct product)
    {
        using var sheet = new Bitmap(2760, 1610);
        using var g = Graphics.FromImage(sheet);
        Setup(g);
        var panels = product.Comparison;
        Text(g, product.AreaTitle + "  /  航磁异常处理对照", 135, 48, 58, true, maxWidth: 2480);
        Text(g, $"格距 {product.Grid.Cell:G} m    /    汉宁 {product.HanningPasses} 遍    /    {panels[1].Title}", 135, 145, 33);
        bool wide = product.Grid.Width >= product.Grid.Height;
        int cols = wide ? 2 : 4;
        float cw = 2590f / cols, rh = wide ? 515 : 960;
        string[] titles = ["磁异常 dT", panels[1].Title, "化极", "一阶垂向导数"];
        for (int i = 0; i < panels.Length; i++)
        {
            var legend = panels[i];
            float x = 75 + i % cols * cw, y = 238 + i / cols * rh;
            Text(g, titles[i], x + 80, y, 38, true);
            Text(g, legend.Method == "全量程" ? "" : legend.Caption, x + 80, y + 59, 24);
            using var raster = ReadOverlay(product, legend.Name);
            Plot(g, product.Grid, raster, new RectangleF(x + 88, y + 112, cw - 145, rh - 185));
        }
        Bar(g, panels[0], new RectangleF(225, 1338, 1250, 29), $"磁异常 dT、上延及化极（nT） · {panels[0].Method}");
        Bar(g, panels[3], new RectangleF(1735, 1338, 805, 29), $"一阶垂导（nT/m） · {panels[3].Method}");
        Footer(g, product, 135, 1514, 27);
        Save(sheet, product, panels[1].Name + "_comparison");
    }

    private static void Setup(Graphics g)
    {
        g.Clear(Color.White);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
    }

    private static Bitmap ReadOverlay(OasisMapProduct product, string name)
    {
        var bitmap = new Bitmap(product.Grid.Width, product.Grid.Height);
        try
        {
            using var zip = ZipFile.OpenRead(Path.Combine(product.OutputDirectory, name + ".kmz"));
            using var g = Graphics.FromImage(bitmap);
            g.CompositingMode = CompositingMode.SourceCopy;
            for (int row = 0; row < bitmap.Height; row += 256)
                for (int col = 0; col < bitmap.Width; col += 256)
                {
                    using var stream = zip.GetEntry($"tiles/{row}_{col}.png")!.Open();
                    using var tile = new Bitmap(stream);
                    g.DrawImageUnscaled(tile, col, row);
                }
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private static double NiceStep(double raw)
    {
        double unit = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        double n = raw / unit;
        return unit * (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10);
    }

    private static void Plot(Graphics g, OasisMapGrid grid, Bitmap raster, RectangleF box, bool scaleBar = false)
    {
        float scale = Math.Min(box.Width / raster.Width, box.Height / raster.Height);
        float w = raster.Width * scale, h = raster.Height * scale;
        var map = new RectangleF(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
        using var missing = new SolidBrush(OasisMapPalette.Missing);
        using var pen = new Pen(Rule, 2);
        g.FillRectangle(missing, map);
        g.DrawImage(raster, map, new RectangleF(0, 0, raster.Width, raster.Height), GraphicsUnit.Pixel);
        g.DrawRectangle(pen, map.X, map.Y, w, h);
        double left = grid.X - grid.Cell / 2 - Origin(grid.X), bottom = grid.Y - grid.Cell / 2 - Origin(grid.Y);
        double widthM = grid.Width * grid.Cell, heightM = grid.Height * grid.Cell;
        double sx = NiceStep(widthM / Math.Max(1, w / 140)), sy = NiceStep(heightM / Math.Max(1, h / 95));
        for (double t = Math.Ceiling(left / sx) * sx; t < left + widthM; t += sx)
        {
            float px = map.Left + (float)((t - left) / widthM) * w;
            g.DrawLine(pen, px, map.Bottom, px, map.Bottom + 9);
            Text(g, t.ToString("G5"), px, map.Bottom + 10, 22, center: true);
        }
        if (scaleBar)
        {
            double distance = NiceStep(widthM / 6);
            float pixels = (float)(distance / widthM) * w;
            float barY = map.Bottom + 80;
            using var barPen = new Pen(Ink, 4);
            g.DrawLine(barPen, map.Left, barY, map.Left + pixels, barY);
            g.DrawLine(barPen, map.Left, barY - 8, map.Left, barY + 4);
            g.DrawLine(barPen, map.Left + pixels, barY - 8, map.Left + pixels, barY + 4);
            Text(g, $"{distance:G5} m", map.Left + pixels / 2, barY + 5, 24, center: true);
        }
        for (double t = Math.Ceiling(bottom / sy) * sy; t < bottom + heightM; t += sy)
        {
            float py = map.Bottom - (float)((t - bottom) / heightM) * h;
            g.DrawLine(pen, map.Left - 9, py, map.Left, py);
            var state = g.Save();
            g.TranslateTransform(map.Left - 20, py);
            g.RotateTransform(-90);
            Text(g, t.ToString("G5"), 0, -26, 22, center: true);
            g.Restore(state);
        }
    }

    private static double Origin(double v) => Math.Floor(v / 1000) * 1000;

    private static void Footer(Graphics g, OasisMapProduct p, float x, float y, float size)
    {
        Text(g, $"格网北向上    ·    横轴东、纵轴北（m）    ·    原点 E {Origin(p.Grid.X):0}，N {Origin(p.Grid.Y):0} m    ·    中央子午线 {p.Meridian:G}°    ·    灰色为无数据", x, y, size);
    }

    private static void Bar(Graphics g, OasisMapLegend legend, RectangleF rect, string label)
    {
        for (int i = 0; i < 256; i++)
        {
            using var brush = new SolidBrush(OasisMapPalette.Colours[i]);
            float start = (float)Math.Round(i * rect.Width / 256);
            float end = (float)Math.Round((i + 1) * rect.Width / 256);
            g.FillRectangle(brush, rect.Left + start, rect.Top, end - start, rect.Height);
        }
        for (int i = 0; i <= 4; i++)
            Text(g, (legend.Limit * ((i - 2) / 2d)).ToString("G5"), rect.Left + rect.Width * i / 4, rect.Bottom + 5, 26, center: true);
        Text(g, label, rect.Left + rect.Width / 2, rect.Bottom + 51, 30, center: true);
    }

    private static void Save(Bitmap sheet, OasisMapProduct product, string name)
    {
        string path = Path.Combine(product.OutputDirectory, name + "." + product.Format.ToLowerInvariant());
        sheet.SetResolution(230, 230);
        if (product.Format == "PNG") { sheet.Save(path, ImageFormat.Png); return; }
        var jpeg = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        using var settings = new EncoderParameters(1);
        settings.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 98L);
        sheet.Save(path, jpeg, settings);
    }
}
