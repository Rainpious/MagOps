using System.ComponentModel;

namespace MagOps.Reporting;

// Explicit, report-only supplements for legacy projects. Never write these into route/production authority.
public sealed class ReportSurveyTask
{
    [DisplayName("任务稳定ID")] public string TaskId { get; set; } = "";
    [DisplayName("母线稳定ID")] public string MotherId { get; set; } = "";
    [DisplayName("母线名称")] public string MotherName { get; set; } = "";
    [DisplayName("用途 SURVEY/CHECK/TIE/EXCLUDE")] public string Role { get; set; } = "SURVEY";
    [DisplayName("测线KML完整路径")] public string KmlPath { get; set; } = "";
    [DisplayName("KML线名称")] public string Placemark { get; set; } = "";
    [DisplayName("设计离地飞高m")] public double? DesignAglM { get; set; }
    [DisplayName("已确认海拔基准兼容")] public bool HeightDatumConfirmed { get; set; }
    [DisplayName("依据及版本")] public string Evidence { get; set; } = "";
    [DisplayName("已确认归属和用途")] public bool Confirmed { get; set; }
    [Browsable(false)] public double? DesignStartLon { get; set; }
    [Browsable(false)] public double? DesignStartLat { get; set; }
    [Browsable(false)] public double? DesignEndLon { get; set; }
    [Browsable(false)] public double? DesignEndLat { get; set; }
    [Browsable(false)] public ReportSource[] DesignSources { get; set; } = [];
}

public sealed class ReportRepeatPair
{
    [DisplayName("配对名称")] public string Name { get; set; } = "";
    [DisplayName("原测任务ID")] public string OriginalTask { get; set; } = "";
    [DisplayName("原测Pass")] public string OriginalPass { get; set; } = "";
    [DisplayName("检查任务ID")] public string CheckTask { get; set; } = "";
    [DisplayName("检查Pass")] public string CheckPass { get; set; } = "";
    [DisplayName("排除整条检查线")] public bool Excluded { get; set; }
    [DisplayName("排除理由或配对依据")] public string Reason { get; set; } = "";
}

public sealed class ReportSupplement
{
    [DisplayName("类别")] public string Category { get; set; } = "";
    [DisplayName("名称或字段")] public string Name { get; set; } = "";
    [DisplayName("内容或数值")] public string Value { get; set; } = "";
    [DisplayName("单位")] public string Unit { get; set; } = "";
    [DisplayName("来源说明")] public string Evidence { get; set; } = "";
    [DisplayName("依据文件完整路径（可选）")] public string SourcePath { get; set; } = "";
    [DisplayName("已人工确认")] public bool Confirmed { get; set; }
}

public sealed class ReportStaticTrial
{
    [DisplayName("试验名称")] public string Name { get; set; } = "";
    [DisplayName("类型 AIR/GROUND/GPS")] public string Kind { get; set; } = "AIR";
    [DisplayName("独立试验CSV完整路径")] public string CsvPath { get; set; } = "";
    [DisplayName("开始UTC yyyy-MM-ddTHH:mm:ssZ")] public string StartUtc { get; set; } = "";
    [DisplayName("结束UTC yyyy-MM-ddTHH:mm:ssZ")] public string EndUtc { get; set; } = "";
    [DisplayName("等效采样间隔秒")] public double SampleIntervalSeconds { get; set; } = .5;
    [DisplayName("设备、供电及运行条件")] public string Conditions { get; set; } = "";
    [DisplayName("已确认静态试验")] public bool Confirmed { get; set; }
}
public sealed record ReportStaticPoint(DateTimeOffset Time, double? RawMagNt);
public sealed record ReportStaticResult(string Name, string Kind, int InputPoints, int ResampledPoints,
    double DurationSeconds, int NoiseWindows, double? NoiseNt, double? PlaneRepeatabilityM,
    double? VerticalRepeatabilityM, string Conditions,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<ReportStaticPoint> Curve);

public sealed record ReportSurveyPoint(long CsvRow, string TaskId, string Pass, DateTimeOffset Time,
    double Longitude, double Latitude, double? AltitudeM, double? RawMagNt)
{
    public string MotherId { get; init; } = "";
    public string Role { get; init; } = "";
    public string SourceId { get; init; } = "";
    public int? SourceRecord { get; init; }
    public string Fragment { get; init; } = "";
    public double ChainageM { get; init; }
    public bool Reversed { get; init; }
    public double? OffsetM { get; init; }
    public double? HeightDeviationM { get; init; }
    public double? AboveGroundM { get; init; }
}

public sealed record ReportMotherLine(string MotherId, string Name, string Role, long Points,
    int Tasks, int Passes, double TrackLengthKm, double CoveredLengthKm, double DesignLengthKm,
    double? MeanOffsetM, double? MaxOffsetM, double? MeanHeightDeviationM, double? MeanAboveGroundM,
    int HeightPoints, int NoiseWindows, double? DynamicNoiseNt)
{
    public int ValidOffsetPoints { get; init; }
    public int HeightDeviationPoints { get; init; }
    public double? MinHeightDeviationM { get; init; }
    public double? MaxHeightDeviationM { get; init; }
    public double? MaxAbsHeightDeviationM { get; init; }
    public double? MinAboveGroundM { get; init; }
    public double? MaxAboveGroundM { get; init; }
    public int TimeRejectedNoiseWindows { get; init; }
    public int UnknownCadenceFragments { get; init; }
    public double DurationSeconds { get; init; }
    public double? AverageSpeedMps { get; init; }
    public double? EffectiveSamplingRateHz { get; init; }
    public double? MeanPointSpacingM { get; init; }
}
public sealed record ReportFragmentCadence(string Fragment, string MotherId, string TaskId, string Pass,
    string Role, int Points, MagOps.Core.Analysis.CadenceEvidence Evidence);
public sealed record ReportRepeatResult(string Name, bool Excluded, string Reason, int Pairs,
    double MatchedLengthKm, double? RmsDifferenceNt, double? PrecisionNt)
{
    [System.Text.Json.Serialization.JsonIgnore] public IReadOnlyList<ReportRepeatPoint> Matches { get; init; } = [];
}
public sealed record ReportRepeatPoint(string Fragment, long CheckCsvRow, double ChainageM, double OriginalMagNt, double CheckMagNt);
public sealed record ReportSurveyResult(IReadOnlyList<ReportMotherLine> Mothers,
    [property: System.Text.Json.Serialization.JsonIgnore] IReadOnlyList<ReportSurveyPoint> Points,
    IReadOnlyList<ReportRepeatResult> Repeats)
{
    public IReadOnlyList<ReportFragmentCadence> Cadences { get; init; } = [];
}
