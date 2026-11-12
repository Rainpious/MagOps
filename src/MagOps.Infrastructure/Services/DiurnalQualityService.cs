using MagOps.Core.Diurnal;

namespace MagOps.Infrastructure.Services;

public sealed record DiurnalQualityPoint(UavMagCanonicalSample Source, string Category, string Reason, string Line);
public sealed record DiurnalQualityInterval(string Category, string Reason, string SourceFile,
    DateTimeOffset StartUtc, DateTimeOffset EndUtc, int FirstRecord, int LastRecord, int Count, string Line);
public sealed record DiurnalQualitySnapshot(string Fingerprint, string Scope, int Total, int Eligible,
    int Corrected, int SensorExcluded, int MissingMagnetic, int Uncorrected, bool Attention,
    IReadOnlyList<DiurnalQualityPoint> Points, IReadOnlyList<DiurnalQualityInterval> Intervals);

/// <summary>Read-only presentation summary. Alert thresholds never affect calculation or production selection.</summary>
public static class DiurnalQualityService
{
    // Provisional UI thresholds, explicitly shown in the workspace; not scientific acceptance limits.
    public const int AlertMinimumPoints = 100;
    public const double AlertFraction = .01;

    public static DiurnalQualitySnapshot Build(FieldMagFolderAnalysis analysis, DiurnalCorrectionResult correction,
        string scope, IReadOnlyDictionary<(string SourceFile, int Record), string>? lines = null)
    {
        var corrected = correction.Samples.ToDictionary(s => (s.RoverSourceFile, s.RoverSourceRow));
        var points = new List<DiurnalQualityPoint>();
        int sensor = 0, missing = 0, failures = 0, success = 0;
        foreach (var row in analysis.UavSamples.OrderBy(r => r.SourceFile, StringComparer.Ordinal).ThenBy(r => r.TimestampUtc).ThenBy(r => r.SourceRecord))
        {
            string category, reason;
            if (row.SensorStatus.Equals("F", StringComparison.OrdinalIgnoreCase))
            { category = "输入资格排除"; reason = "SensorStatus=F；未参与插值"; sensor++; }
            else if (!row.MagneticFieldNt.HasValue)
            { category = "输入资格排除"; reason = "缺少磁值；未参与插值"; missing++; }
            else if (!corrected.TryGetValue((row.SourceFile,row.SourceRecord), out var sample))
                throw new InvalidDataException("日变质量与计算来源不一致，缺少合格记录的计算状态。");
            else if (sample.Mode == DiurnalCorrectionMode.Uncorrected)
            { category = "未改正"; reason = Describe(sample.QualityFlags); failures++; }
            else { var warnings=sample.QualityFlags.Where(f=>f is not ("EXACT" or "INTERPOLATED")).ToArray();
                category = warnings.Length > 0 ? "已改正有提示" : "已改正";
                reason = Describe(warnings); success++; }
            string line = lines is not null && lines.TryGetValue((row.SourceFile,row.SourceRecord),out var name) ? name : "未提供测线归属";
            points.Add(new(row, category, reason, line));
        }
        if (correction.Samples.Count != success + failures)
            throw new InvalidDataException("日变质量与计算范围不一致，请重新计算。");
        var intervals = new List<DiurnalQualityInterval>();
        foreach (var point in points)
        {
            var row=point.Source;
            var last=intervals.LastOrDefault();
            if(last is not null && last.Category==point.Category && last.Reason==point.Reason && last.Line==point.Line &&
                last.SourceFile==row.SourceFile && last.StartUtc.UtcDateTime.Date==row.TimestampUtc.UtcDateTime.Date &&
                row.SourceRecord==last.LastRecord+1 && (row.TimestampUtc-last.EndUtc).TotalSeconds is >=0 and <=8)
                intervals[^1]=last with {EndUtc=row.TimestampUtc,LastRecord=row.SourceRecord,Count=last.Count+1};
            else intervals.Add(new(point.Category,point.Reason,row.SourceFile,row.TimestampUtc,row.TimestampUtc,
                row.SourceRecord,row.SourceRecord,1,point.Line));
        }
        int eligible=success+failures;
        return new(analysis.Fingerprint,scope,points.Count,eligible,success,sensor,missing,failures,
            failures>=AlertMinimumPoints && eligible>0 && (double)failures/eligible>=AlertFraction,points,intervals);
    }

    private static string Describe(IEnumerable<string> flags)=>string.Join("；",flags.Select(flag=>flag switch
    {
        "NO_VALID_BASE_FOR_UTC_DATE"=>"该 UTC 日没有合格 Base（NO_VALID_BASE_FOR_UTC_DATE）",
        "NO_OVERLAP"=>"Rover 与 Base 时间范围不重叠（NO_OVERLAP）",
        "NO_BASE_BEFORE"=>"早于当日 Base 覆盖起点（NO_BASE_BEFORE）",
        "NO_BASE_AFTER"=>"晚于当日 Base 覆盖终点（NO_BASE_AFTER）",
        "DUPLICATE_BASE_TIME"=>"Base 时间键重复，未猜测插值（DUPLICATE_BASE_TIME）",
        "BASE_GAP_TOO_LARGE"=>"Base 间隔超过当前插值上限（BASE_GAP_TOO_LARGE）",
        "BASE_GAP_LARGE"=>"Base 插值间隔较大（BASE_GAP_LARGE）",
        _=>flag
    }));
}
