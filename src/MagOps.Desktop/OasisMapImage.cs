using System.Drawing.Imaging;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using MagOps.Core.Coordinates;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public static class OasisMapImage
{
    public static void Export(OasisMapProduct product)
    {
        var (_, _, _, name, unit, grid, _, _, title) = product;
        double limit = product.ColourLimit ?? grid.Values.Where(float.IsFinite).Max(v => Math.Abs((double)v));
        if (limit == 0) limit = 1;
        var colours = OasisMapPalette.Colours;
        var zones = new List<string> { "{zone cyn mag yel}" };
        for (int i = 0; i < 256; i++)
        {
            var c = colours[i];
            double boundary = limit * ((i - 128) / (i < 128 ? 128d : 127d));
            zones.Add(FormattableString.Invariant($"{boundary:G17} {255 - c.R} {255 - c.G} {255 - c.B}"));
        }
        string nativePath = OasisMapJob.DrawNative(product, zones);
        using var native = new Bitmap(nativePath);
        var bounds = XDocument.Load(nativePath + ".xml").Descendants().First(e => e.Name.LocalName == "extent2d");
        double Bound(string key) => (double)bounds.Attribute(key)!;
        double leftX = Bound("minx"), rightX = Bound("maxx"), bottomY = Bound("miny"), topY = Bound("maxy");
        if (native.Width < 2 || native.Height < 2 || !(rightX > leftX && topY > bottomY) || Math.Abs(leftX - grid.X) > grid.Cell * 2 ||
            Math.Abs(bottomY - grid.Y) > grid.Cell * 2 || Math.Abs(rightX - (grid.X + (grid.Width - 1) * grid.Cell)) > grid.Cell * 2 ||
            Math.Abs(topY - (grid.Y + (grid.Height - 1) * grid.Cell)) > grid.Cell * 2)
            throw new InvalidDataException("Oasis 渲染图与母网格配准范围不符。");
        using var raster = new Bitmap(grid.Width, grid.Height, PixelFormat.Format32bppArgb);
        // Colour each numerical cell once so every exported view uses the identical LUT and mask.
        var locked = raster.LockBits(new Rectangle(0, 0, grid.Width, grid.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new int[grid.Width];
            for (int r = 0; r < grid.Height; r++)
            {
                for (int c = 0; c < grid.Width; c++)
                {
                    float z = grid.Values[(grid.Height - 1 - r) * grid.Width + c];
                    if (!float.IsFinite(z)) row[c] = 0;
                    else
                        row[c] = colours[(int)Math.Round(128 + Math.Clamp(z / limit, -1, 1) * (z < 0 ? 128 : 127))].ToArgb();
                }
                Marshal.Copy(row, 0, locked.Scan0 + r * locked.Stride, row.Length);
            }
        }
        finally { raster.UnlockBits(locked); }

        var legend = product.Legend ?? new OasisMapLegend(name, title, unit, limit, "全量程", grid.Values.Count(float.IsFinite), 0);
        OasisMapSheets.Single(product, legend, raster);
        WriteOverlay(product, legend, raster);
        if (product.Comparison.Length > 0) OasisMapSheets.Compare(product);
    }

    private static void WriteOverlay(OasisMapProduct product, OasisMapLegend legend, Bitmap raster)
    {
        var (_, _, folder, name, unit, grid, cm, _, title) = product;
        double limit = legend.Limit;
        XNamespace k = "http://www.opengis.net/kml/2.2", gx = "http://www.google.com/kml/ext/2.2";
        var document = new XElement(k + "Document", new XElement(k + "name", title + " (" + unit + ")"),
            new XElement(k + "description", FormattableString.Invariant($"{product.AreaTitle} · {legend.Caption} · {OasisMapPalette.Name} v{OasisMapPalette.Version}  ·  {-limit:G7} / 0 / {limit:G7} {unit}")));
        using var zip = ZipFile.Open(Path.Combine(folder, name + ".kmz"), ZipArchiveMode.Create);
        // Each texture is north-up. The KML quad starts at its lower left, counterclockwise.
        for (int top = 0; top < grid.Height; top += 256)
            for (int left = 0; left < grid.Width; left += 256)
            {
                int w = Math.Min(256, grid.Width - left), h = Math.Min(256, grid.Height - top);
                string href = $"tiles/{top}_{left}.png";
                using (var tile = raster.Clone(new Rectangle(left, top, w, h), PixelFormat.Format32bppArgb))
                using (var buffer = new MemoryStream())
                {
                    tile.Save(buffer, ImageFormat.Png);
                    using var file = zip.CreateEntry(href, CompressionLevel.NoCompression).Open();
                    buffer.Position = 0; buffer.CopyTo(file);
                }
                string Corner(int col, int row)
                {
                    var ll = CoordinateService.GaussKrugerInverse(grid.X + (col - 0.5) * grid.Cell,
                        grid.Y + (grid.Height - row - 0.5) * grid.Cell, cm);
                    return string.Create(CultureInfo.InvariantCulture, $"{ll.Longitude:F9},{ll.Latitude:F9}");
                }
                document.Add(new XElement(k + "GroundOverlay", new XElement(k + "name", name),
                    new XElement(k + "Icon", new XElement(k + "href", href)),
                    new XElement(gx + "LatLonQuad", new XElement(k + "coordinates", string.Join(" ",
                        Corner(left, top + h), Corner(left + w, top + h), Corner(left + w, top), Corner(left, top))))));
            }
        using var entry = zip.CreateEntry("doc.kml").Open();
        new XDocument(new XElement(k + "kml", new XAttribute(XNamespace.Xmlns + "gx", gx), document)).Save(entry);
    }

}
