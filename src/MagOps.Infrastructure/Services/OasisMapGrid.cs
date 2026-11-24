using System.Globalization;

namespace MagOps.Infrastructure.Services;

public sealed record OasisMapGrid(int Width, int Height, double X, double Y, double Cell, float[] Values)
{
    public static OasisMapGrid Read(string grd, string xyz)
    {
        using var header = new BinaryReader(File.OpenRead(grd));
        header.ReadInt32(); header.ReadInt32();
        int width = header.ReadInt32(), height = header.ReadInt32(), order = header.ReadInt32();
        double dx = header.ReadDouble(), dy = header.ReadDouble(), x = header.ReadDouble(), y = header.ReadDouble(), rotation = header.ReadDouble();
        if (order != 1 || rotation != 0 || dx != dy || !double.IsFinite(dx) || dx <= 0 ||
            width <= 1 || height <= 1 || (long)width * height > 12000000)
            throw new InvalidDataException("成图需要未旋转的等距网格，单幅最多 1200 万格点。");
        var values = new float[width * height];
        Array.Fill(values, float.NaN);
        int valid = 0;
        foreach (string line in File.ReadLines(xyz))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) continue;
            if (fields.Length != 3) throw new InvalidDataException("Oasis 节点导出列数不符。");
            double Number(int i) => double.Parse(fields[i], CultureInfo.InvariantCulture);
            int col = (int)Math.Round((Number(0) - x) / dx), row = (int)Math.Round((Number(1) - y) / dy);
            if ((uint)col >= width || (uint)row >= height) throw new InvalidDataException("XYZ 节点超出 GRD 范围。");
            var value = (float)Number(2);
            if (!float.IsFinite(value) || value < -1e30) continue;
            int index = row * width + col;
            if (float.IsFinite(values[index])) throw new InvalidDataException("Oasis 节点坐标精度不足，存在重复格点。");
            values[index] = value;
            valid++;
        }
        if (valid < 2) throw new InvalidDataException("Oasis 网格缺少有效节点。");
        return new(width, height, x, y, dx, values);
    }

    private bool HasData(double x, double y)
    {
        int col = (int)Math.Round((x - X) / Cell), row = (int)Math.Round((y - Y) / Cell);
        return (uint)col < Width && (uint)row < Height && float.IsFinite(Values[row * Width + col]);
    }

    // Compare only stretches where both adjacent tracks were actually sampled.
    public (double LongestGapM, int ComparedSections) InspectGaps(OasisSurveyInput input, double spacing, double azimuth, double minimumOverlap)
    {
        double sin = Math.Sin(azimuth * Math.PI / 180), cos = Math.Cos(azimuth * Math.PI / 180);
        var sections = new SortedDictionary<int, Dictionary<int, double>>();
        for (int id = 0; id < input.Lines.Count; id++)
        {
            var points = input.Lines[id].Points;
            for (int i = 1; i < points.Count; i++)
            {
                var a = points[i - 1]; var b = points[i];
                double da = (a.X - X) * sin + (a.Y - Y) * cos, db = (b.X - X) * sin + (b.Y - Y) * cos;
                if (da == db || double.Hypot(b.X - a.X, b.Y - a.Y) > spacing) continue;
                for (int bin = (int)Math.Ceiling(Math.Min(da, db) / Cell); bin <= Math.Floor(Math.Max(da, db) / Cell); bin++)
                {
                    double t = (bin * Cell - da) / (db - da);
                    double cross = (a.X - X + t * (b.X - a.X)) * cos - (a.Y - Y + t * (b.Y - a.Y)) * sin;
                    if (!sections.TryGetValue(bin, out var observations)) sections[bin] = observations = [];
                    observations[id] = cross;
                }
            }
        }
        int probes = Math.Max(3, (int)Math.Ceiling(1.5 * spacing / Cell));
        var previous = new Dictionary<(int Left, int Right, int Probe), int>();
        var overlap = new Dictionary<(int Left, int Right), (int Bin, int Count)>();
        int longestOverlap = 0;
        int previousBin = int.MinValue;
        int compared = 0, longest = 0;
        foreach (var (bin, observations) in sections)
        {
            var current = new Dictionary<(int Left, int Right, int Probe), int>();
            if (previousBin != bin - 1) previous.Clear();
            var tracks = observations.OrderBy(o => o.Value).ToArray();
            for (int j = 1; j < tracks.Length; j++)
            {
                var left = tracks[j - 1]; var right = tracks[j];
                double gap = right.Value - left.Value;
                if (gap < spacing * 0.5 || gap > spacing * 1.5 || input.Lines[left.Key].Name == input.Lines[right.Key].Name) continue;
                compared++;
                var pair = (left.Key, right.Key);
                int overlapCount = overlap.TryGetValue(pair, out var last) && last.Bin == bin - 1 ? last.Count + 1 : 1;
                overlap[pair] = (bin, overlapCount);
                longestOverlap = Math.Max(longestOverlap, overlapCount);
                for (int k = 1; k < probes; k++)
                {
                    double cross = left.Value + gap * k / probes, along = bin * Cell;
                    if (HasData(X + along * sin + cross * cos, Y + along * cos - cross * sin)) continue;
                    var key = (left.Key, right.Key, k);
                    int length = 1 + Math.Max(previous.GetValueOrDefault(key), Math.Max(
                        previous.GetValueOrDefault((left.Key, right.Key, k - 1)),
                        previous.GetValueOrDefault((left.Key, right.Key, k + 1))));
                    current[key] = length;
                    longest = Math.Max(longest, length);
                }
            }
            previous = current;
            previousBin = bin;
        }
        if (compared < 3 || longestOverlap * Cell < minimumOverlap)
            throw new InvalidDataException("相邻实测线的连续共同覆盖不足以判断条状空隙，请核对线距、方位和所选测线。");
        return (longest * Cell, compared);
    }
}
