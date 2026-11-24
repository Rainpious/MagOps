using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MagOps.Infrastructure.Services;

public sealed record OasisMapOptions(string Executable, double Spacing, double Azimuth, int FirstCell,
    int CellStep, int LastCell, double StripLength, double[] Heights, string Format,
    int HanningPasses = 5, double RtpAmplitudeInclination = 20)
{
    public OasisMapScale Scale { get; init; } = new();
    public string AreaTitle { get; init; } = "";
}

public sealed record OasisMapProduct(string Engine, string GridDirectory, string OutputDirectory, string Name,
    string Unit, OasisMapGrid Grid, double Meridian, string Format, string Title)
{
    public double? ColourLimit { get; init; }
    public string SourceGrid { get; init; } = Name;
    public string AreaTitle { get; init; } = "";
    public int HanningPasses { get; init; }
    public OasisMapLegend? Legend { get; init; }
    public OasisMapLegend[] Comparison { get; init; } = [];
}

public static class OasisMapJob
{
    private static string F(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    private static string Setting(string name, string value) => $"SETINI {name}=\"{value}\"\n";
    private static string ExportGrid(string name) => Setting("GRIDXYZ.GRD", name + ".grd") + Setting("GRIDXYZ.XYZ", name + ".xyz")
        + Setting("GRIDXYZ.INDEX", "NO") + Setting("GRIDXYZ.LABEL", "NO") + Setting("GRIDXYZ.XDEC", "1")
        + Setting("GRIDXYZ.YDEC", "1") + "GX gridxyz.gx\n";
    private const string OpenSurvey = "SETINI OPENXYZ.FILE=\"survey.xyz\"\nSETINI OPENXYZ.LINES=\"500\"\nSETINI OPENXYZ.CHANNELS=\"20\"\nGX OpenXYZ.gx\n";

    public static string Run(string csv, string parent, OasisMapOptions o, Action<OasisMapProduct> exportImage)
    {
        if (!File.Exists(o.Executable) || !Path.GetFileName(o.Executable).Equals("omscore.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("请指定本机 Oasis 的 omscore.exe。");
        if (!double.IsFinite(o.Spacing) || o.Spacing <= 0 || !double.IsFinite(o.Azimuth) || o.Azimuth < 0 || o.Azimuth >= 180 ||
            o.FirstCell < 1 || o.FirstCell > o.LastCell || o.CellStep < 1 || !double.IsFinite(o.StripLength) || o.StripLength <= 0 ||
            o.Heights.Length == 0 || o.Heights.Any(h => !double.IsFinite(h) || h < 0 || h > 100000) || o.Format is not ("PNG" or "JPG")
            || o.HanningPasses < 1 || o.HanningPasses > 100 || !double.IsFinite(o.RtpAmplitudeInclination) || Math.Abs(o.RtpAmplitudeInclination) > 90)
            throw new InvalidDataException("请核对测线参数、候选格距、上延距离和图像格式。");
        o.Scale.Validate();
        string inputHash;
        using (var source = File.OpenRead(csv)) inputHash = Convert.ToHexString(SHA256.HashData(source));
        var survey = OasisSurveyInput.Read(csv);
        var heights = o.Heights.Distinct().Order().ToArray();
        string id = $"Oasis_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
        string work = Path.Combine(parent, id + ".working"), final = Path.Combine(parent, id);
        Directory.CreateDirectory(work);
        var attempts = new List<object>();
        var products = new List<(string Name, string Title, string ParentGrid, string Unit, string Path)>();
        var limits = new Dictionary<string, double>();
        var legends = new Dictionary<string, OasisMapLegend>();
        var comparisons = new List<string>();
        try
        {
            string prepared = Path.Combine(work, "input");
            Directory.CreateDirectory(prepared);
            string fieldFile = Prepare(prepared, survey, o);
            double xmin = survey.Lines.Min(l => l.Points.Min(p => p.X)), xmax = survey.Lines.Max(l => l.Points.Max(p => p.X));
            double ymin = survey.Lines.Min(l => l.Points.Min(p => p.Y)), ymax = survey.Lines.Max(l => l.Points.Max(p => p.Y));
            string? selected = null;
            int cell = 0;
            foreach (int trial in Enumerable.Range(0, (o.LastCell - o.FirstCell) / o.CellStep + 1).Select(i => o.FirstCell + i * o.CellStep))
            {
                if ((Math.Ceiling((xmax - xmin) / trial) + 3) * (Math.Ceiling((ymax - ymin) / trial) + 3) > 12000000)
                    throw new InvalidDataException($"候选 {trial} m 超过单幅 1200 万格点，请调整起始格距或分区。");
                string dir = Path.Combine(work, $"cell_{trial}");
                Directory.CreateDirectory(dir);
                File.Copy(fieldFile, Path.Combine(dir, "survey.xyz"));
                Template(dir, ["X", "Y", "dT"]);
                string script = OpenSurvey + Setting("RANGRID.CHAN", "dT") + Setting("RANGRID.GRID", "dt.grd")
                    + Setting("RANGRID.CS", F(trial)) + "GX rangrid.gx\n" + ExportGrid("dt");
                Execute(o.Executable, dir, "grid", script, "dt.grd", "dt.xyz");
                var grid = OasisMapGrid.Read(Path.Combine(dir, "dt.grd"), Path.Combine(dir, "dt.xyz"));
                var result = grid.InspectGaps(survey, o.Spacing, o.Azimuth, o.StripLength);
                attempts.Add(new { cellM = trial, result.LongestGapM, result.ComparedSections });
                WriteJson(work, "grid-trials.json", attempts);
                if (result.LongestGapM >= o.StripLength)
                {
                    // The rejected trial's scripts, logs and measured gap are sufficient for a replay.
                    foreach (string scratch in new[] { "survey.xyz", "survey.gdb", "survey.gdb.xml", "dt.xyz", "dt.grd", "dt.grd.gi", "dt.grd.xml" })
                        File.Delete(Path.Combine(dir, scratch));
                    continue;
                }
                selected = dir;
                cell = trial;
                break;
            }
            if (selected is null) throw new InvalidDataException("候选格距均有连续条状空隙。详见 grid-trials.json。");

            string maps = Path.Combine(work, "maps");
            Directory.CreateDirectory(maps);
            void Product(string name, string parentGrid, string unit, string title)
            {
                string path = Path.Combine(selected, name);
                var grid = OasisMapGrid.Read(path + ".grd", path + ".xyz");
                double limit = o.Scale.Candidate(grid.Values, unit);
                limits[unit] = Math.Max(limits.GetValueOrDefault(unit), limit);
                products.Add((name, title, parentGrid, unit, path));
            }
            Product("dt", "survey.dT", "nT", "微调平后磁异常 dT");
            Execute(o.Executable, selected, "hanning", Setting("GRIDFILT.GRD", "dt.grd") + Setting("GRIDFILT.NEW", "hn.grd")
                + Setting("GRIDFILT.FILTSTR", "0.06,0.10,0.06,0.10,0.36,0.10,0.06,0.10,0.06") + Setting("GRIDFILT.PASS", F(o.HanningPasses))
                + Setting("GRIDFILT.MULT", "1") + Setting("GRIDFILT.DUMMY", "INTERPOLATE") + "GX gridfilt.gx\n" + ExportGrid("hn"), "hn.grd", "hn.xyz");
            Product("hn", "dt", "nT", $"汉宁滤波（{o.HanningPasses} 遍）");
            void Filter(string source, string target, string command, string unit, string title)
            {
                File.WriteAllLines(Path.Combine(selected, target + ".con"), [target, "0", F(survey.Inclination), F(survey.GridDeclination), F(survey.TotalField), command + " /"], Encoding.ASCII);
                Execute(o.Executable, selected, target, Setting("MAGMAP1.INGRD", source + ".grd") + Setting("MAGMAP1.OUTGRD", target + ".grd")
                    + Setting("MAGMAP1.CONFIL", target + ".con") + Setting("MAGMAP1.PEX", "25") + Setting("MAGMAP1.PREP", "0")
                    + "GX magmap1.gx\n" + ExportGrid(target), target + ".grd", target + ".xyz");
                Product(target, source, unit, title);
            }
            foreach (double h in heights)
            {
                string tag = "up_" + h.ToString("R", CultureInfo.InvariantCulture).Replace('.', '_');
                string source = "hn";
                string heightLabel = h == 0 ? "原观测面" : $"上延 {h:G} m";
                if (h > 0) { Filter("hn", tag, "CNUP " + F(h), "nT", heightLabel); source = tag; }
                else products.Add((Name: tag, Title: heightLabel, ParentGrid: "hn", Unit: "nT", Path: Path.Combine(selected, "hn")));
                string pole = tag + "_rtp";
                Filter(source, pole, "REDP " + F(o.RtpAmplitudeInclination), "nT", heightLabel + " · 化极");
                Filter(pole, tag + "_dz1", "DRVZ 1", "nT/m", heightLabel + " · 化极后一阶垂向导数");
                Filter(pole, tag + "_dz2", "DRVZ 2", "nT/m²", heightLabel + " · 化极后二阶垂向导数");
            }
            // Load one grid at a time while rendering all comparable stages on the same scale.
            foreach (string unit in limits.Keys.ToArray())
                limits[unit] = o.Scale.FinalLimit(limits[unit]);
            (int Width, int Height, double X, double Y, double Cell)? footprint = null;
            foreach (var item in products.Where(p => p.Name != "hn"))
            {
                var grid = OasisMapGrid.Read(item.Path + ".grd", item.Path + ".xyz");
                var geometry = (grid.Width, grid.Height, grid.X, grid.Y, grid.Cell);
                footprint ??= geometry;
                if (footprint != geometry) throw new InvalidDataException($"图幅与 dT 网格不一致：{item.Name}");
                double limit = limits[item.Unit];
                var legend = new OasisMapLegend(item.Name, item.Title, item.Unit, limit, o.Scale.Method(item.Unit),
                    grid.Values.Count(float.IsFinite), grid.Values.Count(v => float.IsFinite(v) && Math.Abs((double)v) > limit));
                legends.Add(item.Name, legend);
                OasisMapLegend[] comparison = [];
                if (item.Name.EndsWith("_dz1", StringComparison.Ordinal))
                {
                    string tag = item.Name[..^4];
                    comparison = [legends["dt"], legends[tag], legends[tag + "_rtp"], legend];
                    comparisons.Add(tag + "_comparison." + o.Format.ToLowerInvariant());
                }
                exportImage(new(o.Executable, selected, maps, item.Name, item.Unit, grid, survey.Meridian, o.Format, item.Title)
                    { ColourLimit = limit, SourceGrid = Path.GetFileName(item.Path), Legend = legend, Comparison = comparison,
                      AreaTitle = string.IsNullOrWhiteSpace(o.AreaTitle) ? Path.GetFileName(csv) : o.AreaTitle.Trim(),
                      HanningPasses = o.HanningPasses });
            }
            using (var source = File.OpenRead(csv))
                if (Convert.ToHexString(SHA256.HashData(source)) != inputHash) throw new IOException("运行期间源 CSV 发生变化。");
            WriteJson(work, "result.json", new
            {
                source = Path.GetFullPath(csv),
                sha256 = inputHash,
                options = o,
                engineVersion = FileVersionInfo.GetVersionInfo(o.Executable).FileVersion,
                inputStage = survey.IsLevelled ? "dT" : "dTgz",
                survey.Meridian,
                survey.Inclination,
                survey.GridDeclination,
                survey.TotalField,
                survey.MaxPositionErrorM,
                cell,
                attempts,
                palette = new { name = OasisMapPalette.Name, version = OasisMapPalette.Version, center = 0, symmetricLimitsByUnit = limits },
                legends = legends.Values,
                comparisons,
                products = products.Select(p => new { name = p.Name, title = p.Title, parentGrid = p.ParentGrid,
                    unit = p.Unit, grid = Path.GetRelativePath(work, p.Path + ".grd") }),
                status = "待复核"
            });
            Directory.Move(work, final);
            return final;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Oasis 成图未完成。{ex.Message}\n本次日志：{work}", ex);
        }
    }

    private static string Prepare(string dir, OasisSurveyInput survey, OasisMapOptions o)
    {
        string source = Path.Combine(dir, "survey.xyz");
        using (var writer = new StreamWriter(source, false, Encoding.ASCII))
        {
            writer.WriteLine(survey.IsLevelled ? "/ X Y dT" : "/ X Y dTgz dT SampleId");
            int sampleId = 0;
            for (int i = 0; i < survey.Lines.Count; i++)
            {
                writer.WriteLine($"Line {i + 1}");
                foreach (var p in survey.Lines[i].Points)
                    writer.WriteLine($"{F(p.X)} {F(p.Y)} {F(p.Field)}" + (survey.IsLevelled ? "" : $" * {++sampleId}"));
            }
        }
        WriteJson(dir, "lines.json", survey.Lines.Select((l, i) => new { oasisLine = i + 1, l.Name, l.Pass, l.TaskId, count = l.Points.Count }));
        if (survey.IsLevelled) return source;
        Template(dir, ["X", "Y", "dTgz", "dT", "SampleId"]);
        File.WriteAllLines(Path.Combine(dir, "field.o0"), ["[EXPORT XYZ]", "EXPORT X,NORMAL,20,7", "EXPORT Y,NORMAL,20,7",
            "EXPORT SampleId,NORMAL,16,0","EXPORT dTgz,NORMAL,22,10","EXPORT dcor_noise,NORMAL,22,10",
            "EXPORT dT,NORMAL,22,10","EXPORT miclev,NORMAL,22,10","WRITEDUMMY YES","WRITEHEADER YES","CSV_COMMASEPARATE NO","CLIPMAP NO","MAXPOINTS 0"], Encoding.ASCII);
        string script = OpenSurvey + Setting("miclev.Xchan", "X") + Setting("miclev.Ychan", "Y") + Setting("miclev.Ochan", "dTgz")
            + Setting("miclev.Nchan", "dcor_noise") + Setting("miclev.Space", F(o.Spacing)) + Setting("miclev.Dir", F(o.Azimuth))
            + Setting("miclev.Cell", F(o.Spacing / 5)) + Setting("miclev.Wlen", F(4 * o.Spacing))
            + Setting("miclev.OGRID", "original.grd") + Setting("miclev.NNOISE", "noise.grd") + "GX decorr.gx\n"
            + Setting("miclev.Mchan", "miclev") + "GX miclev.gx\n"
            + Setting("MATH.EXP", "dT=dTgz-dcor_noise;") + "GX math.gx\n"
            + Setting("EXPORT.FILE", "levelled.xyz") + Setting("EXPORT.TEMPLATE", "field.o0") + "GX export.gx\n";
        Execute(o.Executable, dir, "microlevel", script, "noise.grd", "original.grd", "survey.gdb", "levelled.xyz");
        return RestoreLevelledLines(dir, survey);
    }

    private static string RestoreLevelledLines(string dir, OasisSurveyInput survey)
    {
        // EXPORT can omit the line headers. Match by original record ID, including stationary duplicate XY.
        var points = survey.Lines.SelectMany(line => line.Points).ToArray();
        var values = new double[points.Length];
        Array.Fill(values, double.NaN);
        foreach (string line in File.ReadLines(Path.Combine(dir, "levelled.xyz")))
        {
            string data = line.Trim();
            if (data.Length == 0 || data.StartsWith('/') || data.StartsWith("Line ", StringComparison.OrdinalIgnoreCase)) continue;
            var fields = data.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 7) throw new InvalidDataException("Oasis 微调平回读表与预期列数不符。");
            double N(int col) => double.Parse(fields[col], CultureInfo.InvariantCulture);
            int id = int.Parse(fields[2], CultureInfo.InvariantCulture) - 1;
            if ((uint)id >= points.Length || double.IsFinite(values[id])) throw new InvalidDataException("Oasis 导出的点号越界或重复。");
            var p = points[id];
            double dt = N(5);
            if (!double.IsFinite(dt) || !double.IsFinite(N(6)) || Math.Abs(N(0) - p.X) > 0.001 || Math.Abs(N(1) - p.Y) > 0.001 ||
                Math.Abs(N(3) - p.Field) > 0.001 || Math.Abs(dt - (N(3) - N(4))) > 0.001)
                throw new InvalidDataException($"微调平回读未通过，测点 {id + 1}。");
            values[id] = dt;
        }
        if (values.Any(v => !double.IsFinite(v))) throw new InvalidDataException("微调平回读存在缺失测点。");
        string path = Path.Combine(dir, "levelled-lines.xyz");
        using var writer = new StreamWriter(path, false, Encoding.ASCII);
        writer.WriteLine("/ X Y dT");
        int index = 0;
        for (int i = 0; i < survey.Lines.Count; i++)
        {
            writer.WriteLine($"Line {i + 1}");
            foreach (var p in survey.Lines[i].Points)
                writer.WriteLine($"{F(p.X)} {F(p.Y)} {F(values[index++])}");
        }
        return path;
    }

    private static void Template(string dir, string[] channels) => File.WriteAllLines(Path.Combine(dir, "survey.i0"),
        new[] { "[IMPORT XYZ]" }.Concat(channels.Select((c, i) => $"IMPORT {i + 1},NORMAL,{c},DOUBLE")), Encoding.ASCII);

    public static string DrawNative(OasisMapProduct product, IEnumerable<string> colourZones)
    {
        string name = product.SourceGrid, image = name + "_native.png";
        File.WriteAllLines(Path.Combine(product.GridDirectory, name + ".zon"), colourZones, Encoding.ASCII);
        string script = Setting("GRIDIMG1.NEW", "1") + Setting("GRIDIMG1.GRID", product.SourceGrid + ".grd")
            + Setting("GRIDIMG1.COLOR", name + ".zon") + Setting("GRIDIMG1.REG", "0") + "GX gridimg1.gx\n"
            + Setting("EXPMAP.MODE", "1") + Setting("EXPMAP.ALL", "1") + Setting("EXPMAP.FILE", image)
            + Setting("EXPMAP.FORMAT", "PNG") + Setting("EXPMAP.SIZEX", F(Math.Max(32, product.Grid.Width)))
            + Setting("EXPMAP.SIZEY", "0") + Setting("EXPMAP.BITS", "24") + "GX expmap.gx\n";
        Execute(product.Engine, product.GridDirectory, name + "_paint", script, name + ".map", image, image + ".xml");
        return Path.Combine(product.GridDirectory, image);
    }

    private static void Execute(string exe, string dir, string name, string script, params string[] expected)
    {
        File.WriteAllText(Path.Combine(dir, name + ".gs"), "INTERACTIVE OFF\n" + script, Encoding.ASCII);
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(name + ".gs");
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Oasis。");
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        bool finished = process.WaitForExit(600000);
        if (!finished) { process.Kill(true); process.WaitForExit(); }
        string log = output.GetAwaiter().GetResult() + Environment.NewLine + errors.GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(dir, name + ".log"), log);
        if (!finished || process.ExitCode != 0 || expected.Any(f => !File.Exists(Path.Combine(dir, f)) || new FileInfo(Path.Combine(dir, f)).Length == 0))
            throw new IOException($"阶段 {name} 失败，见 {name}.log。");
    }

    private static void WriteJson(string dir, string name, object value) => File.WriteAllText(Path.Combine(dir, name),
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}
