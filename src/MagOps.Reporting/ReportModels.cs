namespace MagOps.Reporting;

public sealed record ReportInputs
{
    public int SchemaVersion { get; init; } = 1;
    public string ProjectName { get; init; } = "";
    public string WorkareaName { get; init; } = "";
    public string Organization { get; init; } = "";
    public string Author { get; init; } = "";
    public string ReportDate { get; init; } = DateTime.Today.ToString("yyyy-MM-dd");
    public string ProductionCsv { get; init; } = "";
    public string AssessmentDirectory { get; init; } = "";
    public string TemplatePath { get; init; } = "";
    public string QualityCheckResultPath { get; init; } = "";
    public string FigurePaths { get; init; } = "";
    public string AutoProductionCsv { get; init; } = "";
    public string AutoAssessmentDirectory { get; init; } = "";
    public string Objectives { get; init; } = "";
    public string Methods { get; init; } = "";
    public string ProcessingDescription { get; init; } = "";
    public string Interpretation { get; init; } = "";
    public string Conclusions { get; init; } = "";
    public ReportSurveyTask[] SurveyTasks { get; init; } = [];
    public ReportRepeatPair[] RepeatPairs { get; init; } = [];
    public bool SharedSelectionReviewed { get; init; }
    public ReportSharedItem[] SharedItems { get; init; } = [];
    public ReportSupplement[] Supplements { get; init; } = [];
    public ReportStaticTrial[] StaticTrials { get; init; } = [];
    public string DesignBoundaryKml { get; init; } = "";
    public string BoundaryDatum { get; init; } = "WGS84";
    public string BoundaryCentralMeridian { get; init; } = "";
    public string BoundaryEvidence { get; init; } = "";
    public double MaximumTimeGapSeconds { get; init; } = 5;
    public double MaximumSpatialGapM { get; init; } = 100;
    public double KmlMatchToleranceM { get; init; } = 100;
    public double RepeatMatchToleranceM { get; init; } = 20;
    public double GradientLimitNtKm { get; init; } = 600;
}

public sealed record ReportSource(string Role, string Path, string Sha256);
public sealed record ReportFact(string Id, string Value, string Unit, string Status, string Source, string Method);
public sealed record ReportLine(string Name, string TaskId, string Pass, long Points, double LengthKm);
public sealed record ReportTrackPoint(string Segment, double Longitude, double Latitude);
public sealed record ReportSnapshot(string SchemaVersion, DateTimeOffset CreatedUtc, string ProjectId,
    string WorkareaId, double? CentralMeridian, ReportInputs Inputs, IReadOnlyList<ReportSource> Sources,
    IReadOnlyList<ReportFact> Facts, IReadOnlyList<ReportLine> Lines, IReadOnlyList<ReportTrackPoint> Track,
    IReadOnlyList<string> Warnings)
{
    public ReportSurveyResult? Survey { get; init; }
    public ReportQualityReference? QualityReference { get; init; }
    public IReadOnlyList<ReportStaticResult> StaticTrials { get; init; } = [];
    public IReadOnlyList<MagOps.Infrastructure.Services.BoundaryVertex> BoundaryVertices { get; init; } = [];
}
public sealed record ReportBuildResult(string Directory, string DocumentPath, string SnapshotPath, string ManifestPath);
