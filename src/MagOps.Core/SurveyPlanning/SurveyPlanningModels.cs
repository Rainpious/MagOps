namespace MagOps.Core.SurveyPlanning;

public sealed record SurveyBoundaryPoint(double X, double Y);

public sealed record SurveyLineSegment(
    SurveyBoundaryPoint Start,
    SurveyBoundaryPoint End)
{
    public double Length => Math.Sqrt(
        Math.Pow(End.X - Start.X, 2) +
        Math.Pow(End.Y - Start.Y, 2));
}

public sealed class SurveyPlannedLine
{
    public int LineNumber { get; init; }
    public List<SurveyLineSegment> Segments { get; init; } = [];

    /// <summary>
    /// True only for the optional extra outer survey line used to turn an
    /// otherwise return-only final snake leg into productive survey coverage.
    /// Coverage-extension lines are additive: no original line is removed.
    /// </summary>
    public bool IsCoverageExtension { get; init; }

    public string LineLabel => $"L{LineNumber}";
}

public sealed class SurveyLinePlanOptions
{
    /// <summary>Flight-line spacing in the same projected metre units as PLY X/Y.</summary>
    public double SpacingM { get; set; } = 100.0;

    /// <summary>Azimuth clockwise from north. 0 = north/south, 90 = east/west.</summary>
    public double AzimuthDegrees { get; set; }

    /// <summary>
    /// Number assigned to the first positive-length survey line on the
    /// south/west side of the workarea. Polygon vertex order never affects it.
    /// </summary>
    public int InitialLineNumber { get; set; } = 1;

    /// <summary>Positive line-number delta toward north/east.</summary>
    public int LineNumberIncrement { get; set; } = 1;

    /// <summary>
    /// Experimental/frozen parity-completion switch. The outward extension
    /// algorithm is retained only for future redesign, but production route
    /// generation keeps this false until a passable-road / field-access model
    /// is available. Current production planning must never enlarge the survey
    /// grid merely to make the snake line count even.
    /// </summary>
    public bool ExtendForEvenSnakeCompletion { get; set; } = false;
}

public sealed class SurveyLinePlanResult
{
    public List<SurveyBoundaryPoint> Boundary { get; init; } = [];
    public SurveyBoundaryPoint Origin { get; init; } = new(0, 0);
    public SurveyLinePlanOptions Options { get; init; } = new();
    public List<SurveyPlannedLine> Lines { get; init; } = [];
    public int CandidateLineCount { get; init; }
    public int TangentOnlyLineCount { get; init; }
    public int CoverageExtensionLineCount { get; init; }

    public int SegmentCount => Lines.Sum(x => x.Segments.Count);
    public int DisconnectedLineCount => Lines.Count(x => x.Segments.Count > 1);
    public int EndpointCount => SegmentCount * 2;
}
