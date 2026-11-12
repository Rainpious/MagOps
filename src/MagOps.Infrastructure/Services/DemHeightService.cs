using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

public enum DemHeightStatus
{
    Calculated, InvalidCoordinate, MissingAltitude, DemUnavailable,
    OutsideDem, NoData, DatumUnconfirmed, InvalidResult
}

public sealed record DemHeightPoint(int InputIndex, UavMagCanonicalSample Source,
    double? TerrainElevationM, double? AboveGroundHeightM, DemHeightStatus Status);

public sealed record DemHeightSummary(int InputCount, int ValidCount, int NegativeCount,
    double? MeanM, double? MinM, double? MaxM, double? P05M, double? P95M);

public sealed record DemHeightSource(string Path, string StableId, string Sha256);

public sealed class DemHeightSnapshot
{
    public const string AlgorithmVersion = "dem-agl-1-bilinear";
    public required string SourceFingerprint { get; init; }
    public required string Fingerprint { get; init; }
    public required DemHeightOptions Options { get; init; }
    public required string DemSha256 { get; init; }
    public GeoTiffDemInfo? DemInfo { get; init; }
    public required IReadOnlyList<DemHeightPoint> Points { get; init; }
    public required DemHeightSummary Summary { get; init; }
    public string Notice { get; init; } = "";
    public IReadOnlyList<DemHeightSource> SourceFiles { get; init; } = [];
}

/// <summary>Pointwise pre-segmentation DEM derivation; no magnetic, pass or KML dependency.</summary>
public static class DemHeightService
{
    public static DemHeightSnapshot Calculate(FieldMagFolderAnalysis analysis, DemHeightOptions options,
        CancellationToken cancellationToken = default)
    {
        TerrainSetSampler? dem = null;
        string notice = "";
        cancellationToken.ThrowIfCancellationRequested();
        try { dem = new TerrainSetSampler(options.DemPath); dem.ValidateForHeightCalculation(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or OverflowException)
        { dem = null; notice = "DEM 无法读取；原始数据与分线流程可继续。" + ex.Message; }
        string hash = dem?.ContentSha256 ?? "";
        bool compatible = options.CompatibilityConfirmed &&
            !string.IsNullOrWhiteSpace(options.ObservationHeightDatum) &&
            !string.IsNullOrWhiteSpace(options.DemHeightDatum) &&
            !string.IsNullOrWhiteSpace(options.CompatibilityEvidence) &&
            hash.Length > 0 && string.Equals(hash, options.ConfirmedDemSha256, StringComparison.OrdinalIgnoreCase);
        if (dem is not null && !compatible)
            notice = "坐标、高程基准及米单位的相容性未确认，或 DEM 内容已变化；仅提供地形高程，离地高度留空。";
        var points = new DemHeightPoint[analysis.UavSamples.Count];
        for (int i = 0; i < points.Length; i++)
        {
            if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var sample = analysis.UavSamples[i];
            double? terrain = null, agl = null;
            DemHeightStatus status;
            if (sample.Longitude is not { } lon || sample.Latitude is not { } lat ||
                !WorkareaSpatialService.ValidCoordinate(lon, lat)) status = DemHeightStatus.InvalidCoordinate;
            else if (dem is null) status = DemHeightStatus.DemUnavailable;
            else if (!dem.CanSampleBilinear(lon, lat)) status = DemHeightStatus.OutsideDem;
            else
            {
                try
                {
                    terrain = dem.SampleBilinear(lon, lat);
                    if (!double.IsFinite(terrain.Value)) { terrain = null; status = DemHeightStatus.InvalidResult; }
                    else if (sample.AltitudeM is not { } altitude || !double.IsFinite(altitude))
                        status = DemHeightStatus.MissingAltitude;
                    else if (!compatible) status = DemHeightStatus.DatumUnconfirmed;
                    else
                    {
                        double value = altitude - terrain.Value;
                        if (double.IsFinite(value)) { agl = value; status = DemHeightStatus.Calculated; }
                        else status = DemHeightStatus.InvalidResult;
                    }
                }
                catch (DemNoDataException) { status = DemHeightStatus.NoData; }
                catch (InvalidDataException) { status = DemHeightStatus.InvalidResult; }
                catch (OverflowException) { status = DemHeightStatus.InvalidResult; }
            }
            points[i] = new(i, sample, terrain, agl, status);
        }
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            DemHeightSnapshot.AlgorithmVersion + "|" + analysis.Fingerprint + "|" + hash + "|" + JsonSerializer.Serialize(options))));
        return new()
        {
            SourceFingerprint = analysis.Fingerprint, Fingerprint = fingerprint, Options = options,
            DemSha256 = hash, DemInfo = dem?.Info, Points = points, Summary = Summarize(points), Notice = notice,
            SourceFiles = analysis.UavSamples.Select(s => s.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).Select(path =>
            {
                var file = analysis.Files.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                return new DemHeightSource(path, file?.SourceFileStableId ?? "", file?.SourceContentSha256 ?? "");
            }).ToArray()
        };
    }

    public static DemHeightSummary Summarize(IEnumerable<DemHeightPoint> points)
    {
        int count = 0;
        var values = new List<double>();
        foreach (var p in points) { count++; if (p.AboveGroundHeightM is { } h) values.Add(h); }
        values.Sort();
        double? Quantile(double q)
        {
            if (values.Count == 0) return null;
            double pos = (values.Count - 1) * q;
            int lower = (int)pos;
            return values[lower] + (values[Math.Min(lower + 1, values.Count - 1)] - values[lower]) * (pos - lower);
        }
        return new(count, values.Count, values.Count(x => x < 0),
            values.Count == 0 ? null : values.Average(), values.Count == 0 ? null : values[0],
            values.Count == 0 ? null : values[^1], Quantile(.05), Quantile(.95));
    }

    public static string Describe(DemHeightStatus status) => status switch
    {
        DemHeightStatus.Calculated => "已计算", DemHeightStatus.InvalidCoordinate => "坐标无效",
        DemHeightStatus.MissingAltitude => "海拔缺失或无效", DemHeightStatus.DemUnavailable => "DEM 不可用",
        DemHeightStatus.OutsideDem => "超出 DEM 可采样范围", DemHeightStatus.NoData => "DEM 空值邻域",
        DemHeightStatus.DatumUnconfirmed => "基准相容性未确认", _ => "计算数据异常"
    };

    public static bool DemIsCurrent(DemHeightSnapshot snapshot)
    {
        try
        {
            return snapshot.DemSha256.Length > 0 && new TerrainSetSampler(snapshot.Options.DemPath).ContentSha256 == snapshot.DemSha256;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public static string Export(DemHeightSnapshot snapshot, string outputDirectory)
    {
        if (!DemIsCurrent(snapshot)) throw new InvalidOperationException("DEM 已改变或不可读取，请重新计算后导出。");
        if (!SourcesAreCurrent(snapshot)) throw new InvalidOperationException("原始来源文件已改变或不可读取，请重新扫描原始数据并计算后导出。");
        // Each run gets an independent directory; earlier deliveries remain intact.
        string directory = Path.Combine(outputDirectory, "离地高度_DEM_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N")[..6]);
        string path = Path.Combine(directory, "离地高度逐点明细.csv");
        static string Num(double? v) => v?.ToString("G17", CultureInfo.InvariantCulture) ?? "";
        static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        AtomicFileWriter.Write(path, stream =>
        {
            using var writer = new StreamWriter(stream, new UTF8Encoding(true), leaveOpen: true);
            writer.WriteLine("InputOrder,SourceFile,SourceRecord,TimestampUtc,Longitude,Latitude,AltitudeM,TerrainElevationM,AboveGroundHeightM,CalculationStatus");
            foreach (var p in snapshot.Points)
                writer.WriteLine(string.Join(",", p.InputIndex + 1, Csv(p.Source.SourceFile), p.Source.SourceRecord,
                    p.Source.TimestampUtc.ToString("O"), Num(p.Source.Longitude), Num(p.Source.Latitude), Num(p.Source.AltitudeM),
                    Num(p.TerrainElevationM), Num(p.AboveGroundHeightM), p.Status));
        });
        AtomicFileWriter.Write(Path.Combine(directory, "离地高度计算依据.json"), stream => JsonSerializer.Serialize(stream,
            new { Algorithm = DemHeightSnapshot.AlgorithmVersion, snapshot.SourceFingerprint, snapshot.Fingerprint,
                snapshot.DemSha256, snapshot.Options, snapshot.DemInfo, snapshot.Summary, snapshot.Notice, snapshot.SourceFiles,
                SourceVerification = snapshot.SourceFiles.All(s => s.Sha256.Length > 0) && snapshot.SourceFiles.Count > 0
                    ? "原始来源 SHA-256 已核验" : "部分或全部来源无导入 SHA，未验证；结果仅代表已导入快照",
                StatusCounts = snapshot.Points.GroupBy(p => p.Status).ToDictionary(g => g.Key.ToString(), g => g.Count()),
                Scope = "导入原始点集，未按正式测线筛选；离地高度仅作分线参考，不改变测线身份、观测值或生产选择。",
                Sampling = "双线性，完整四邻点；边缘不外推，NoData 不补零；高程单位由使用者确认是米。" },
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return path;
    }

    public static bool SourcesAreCurrent(DemHeightSnapshot snapshot)
    {
        foreach (var source in snapshot.SourceFiles.Where(s => !string.IsNullOrWhiteSpace(s.Sha256)))
        {
            try
            {
                using var stream = File.OpenRead(source.Path);
                if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException or ArgumentException) { return false; }
        }
        return true;
    }
}
