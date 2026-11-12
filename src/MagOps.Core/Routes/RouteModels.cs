using System.Text.Json.Serialization;

namespace MagOps.Core.Routes;

using MagOps.Core.SurveyPlanning;

public enum RouteCoordinateMode
{
    Automatic,
    Projected,
    Geographic
}

public enum RouteStartDirection
{
    Southwest = 0,
    Northwest = 1,
    Southeast = 2,
    Northeast = 3
}

/// <summary>
/// Optional cardinal side used when automatically selecting the first route
/// point inside a generated partition. Automatic preserves the existing
/// shared-cut-boundary behavior. Explicit north/south/west/east choices let
/// adjacent partitions deliberately start from their common field boundary.
/// </summary>
public enum RoutePartitionStartSide
{
    Automatic = 0,
    North = 1,
    South = 2,
    West = 3,
    East = 4
}

public enum RouteTakeoffAnchorMode
{
    Automatic = 0,
    Boundary = 1,
    CutLine = 2,
    Custom = 3
}

public enum RouteFlightDirection
{
    Automatic = 0,
    WestToEast = 1,
    EastToWest = 2,
    SouthToNorth = 3,
    NorthToSouth = 4
}

public sealed record RawRoutePoint(double X, double Y, string Label, int Row);

public sealed class RouteEndpoint
{
    public string Line { get; set; } = "";
    public int Endpoint { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public string Label { get; set; } = "";
    public string Pid { get; set; } = "";

    [JsonIgnore]
    public string DisplayLabel =>
        $"{(string.IsNullOrWhiteSpace(Label) ? "无原始点名" : Label.Trim())} | {Pid} | X={X:0.###} | Y={Y:0.###}";

    public RouteEndpoint Clone() => new()
    {
        Line = Line,
        Endpoint = Endpoint,
        X = X,
        Y = Y,
        Label = Label,
        Pid = Pid
    };
}

public sealed class RouteLineInference
{
    public Dictionary<string, RouteEndpoint[]> Grouped { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<string> OrderedLines { get; init; } = [];
    public List<RouteEndpoint> Endpoints { get; init; } = [];
    public string MatchMode { get; init; } = "";
    public int Merged { get; init; }
}

public sealed class RouteSortie
{
    public string Name { get; set; } = "";
    public List<RouteEndpoint> Points { get; set; } = [];
    public List<string> SourceLines { get; set; } = [];
    // SourceLines remains readable for v25 and older route-project files.
    // A reference is attached to a planned line, never to a sortie position.
    public List<RouteTaskReference> TaskReferences { get; set; } = [];

    [JsonIgnore]
    public bool IsManuallyEdited => SourceLines.Count == 0;
}

public sealed class RouteTaskReference
{
    public string LineName { get; set; } = "";
    public string SurveyTaskStableId { get; set; } = "";
    public string SurveyLineStableId { get; set; } = "";
    public string MotherLineStableId { get; set; } = "";
    public string SurveyPurpose { get; set; } = "NORMAL";
    public int SegmentIndex { get; set; }
    public int RepeatIndex { get; set; }
    public string GeometryFingerprint { get; set; } = "";
    /// <summary>Legacy/display execution-segment label, e.g. L63.S1. Never machine identity.</summary>
    public string ExecutionSegmentId { get; set; } = "";
    /// <summary>Persistent opaque machine identity. Reordering/renumbering must not change it.</summary>
    public string ExecutionSegmentStableId { get; set; } = "";
    /// <summary>True when this task must not be mixed with ordinary lines.</summary>
    public bool ProtectedExecutionSegment { get; set; }
}

/// <summary>
/// A planned piece of a mother survey line. This is deliberately not a
/// workarea/subarea: the mother line remains the business identity.
/// </summary>
public sealed class RouteExecutionSegment
{
    public string MotherLine { get; set; } = "";
    /// <summary>Display label only, normally MotherLine.S{Sequence}.</summary>
    public string SegmentId { get; set; } = "";
    /// <summary>Persistent opaque machine identity.</summary>
    public string SegmentStableId { get; set; } = "";
    public string CutOperationId { get; set; } = "";
    public int Sequence { get; set; }
    public double CutX { get; init; }
    public double CutY { get; init; }
    public double CutLineStartX { get; set; }
    public double CutLineStartY { get; set; }
    public double CutLineEndX { get; set; }
    public double CutLineEndY { get; set; }
    public RouteEndpoint Start { get; set; } = new();
    public RouteEndpoint End { get; set; } = new();
    public bool ProtectedConnection { get; init; } = true;
}

public sealed record RouteOverLimitRoundTrip(
    int PairIndex,
    string First,
    string Second,
    double LengthM);

public sealed record RouteGeometryDiagnostic(
    string LineDirection,
    string ProgressDirection,
    double AzimuthDegrees,
    double MedianLineKm);

public sealed class RoutePlanOptions
{
    public RouteCoordinateMode CoordinateMode { get; set; } = RouteCoordinateMode.Automatic;
    public string HeaderlessOrder { get; set; } = "Point,Y,X";
    public double? CentralMeridian { get; set; }
    public double MaxSortieKm { get; set; } = 18.0;
    public RouteStartDirection StartDirection { get; set; } = RouteStartDirection.Southwest;
    public RoutePartitionStartSide PartitionStartSide { get; set; } = RoutePartitionStartSide.Automatic;
    public RouteTakeoffAnchorMode TakeoffAnchorMode { get; set; } = RouteTakeoffAnchorMode.Automatic;
    public RouteFlightDirection FlightDirection { get; set; } = RouteFlightDirection.Automatic;
    public string TakeoffAnchorRef { get; set; } = "";
    public double? CustomTakeoffX { get; set; }
    public double? CustomTakeoffY { get; set; }
    public string SelectedStartPid { get; set; } = "";
    public double? TakeoffX { get; set; }
    public double? TakeoffY { get; set; }
    public double KmzAglM { get; set; } = 50.0;

    // Legacy/frozen DJI real-time terrain-following KMZ export setting.
    // RoutePlanner intentionally ignores this value.
    public double KmzSpeedMps { get; set; } = 12.0;
}

public sealed class RoutePlanResult
{
    public string AlgorithmVersion { get; init; } = RoutePlanner.AlgorithmVersion;
    public string SourcePath { get; init; } = "";
    public string Mode { get; init; } = "";
    public double CentralMeridian { get; init; }
    public RouteLineInference Inference { get; init; } = new();
    public List<string> OrderedLines { get; init; } = [];
    public RouteEndpoint StartPoint { get; set; } = new();
    public int ActiveStartEndpoint { get; set; }
    public List<RouteSortie> Sorties { get; init; } = [];
    public List<RouteExecutionSegment> ExecutionSegments { get; set; } = [];
    public List<RouteOverLimitRoundTrip> OverLimitRoundTrips { get; init; } = [];
    public RouteGeometryDiagnostic GeometryDiagnostic { get; init; } =
        new("未知", "未知", double.NaN, 0);
    public List<string> Warnings { get; init; } = [];
    public RoutePlanOptions Options { get; init; } = new();
    public double? RoundTripDistanceM { get; set; }
    public AccessPlanningData? AccessPlanning { get; set; }
    public List<RouteCheckLine> CheckLines { get; set; } = [];
    public RouteDesignBasis? DesignBasis { get; set; }

    /// <summary>Optional concise partition identity such as P01.</summary>
    public string PartitionCode { get; set; } = "";

    /// <summary>Subdivision lineage manifest used to derive this partition.</summary>
    public string PartitionManifestPath { get; set; } = "";

    public bool HasOverLimitSorties =>
        Sorties.Any(s => RouteCoordinateMath.RouteLength(s.Points, Mode, CentralMeridian) > Options.MaxSortieKm * 1000.0)
        || RouteAccessPlanner.Assess(this).Any(a => a.Status == "OVER_LIMIT");
}

public sealed class RouteProjectFile
{
    public AccessPlanningData? AccessPlanning { get; set; }
    public List<RouteCheckLine> CheckLines { get; set; } = [];
    public RouteDesignBasis? DesignBasis { get; set; }
    [JsonPropertyName("subdivision_workspace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RouteSubdivisionWorkspace? SubdivisionWorkspace { get; set; }
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "magops-route-project-2";

    [JsonPropertyName("algorithm_version")]
    public string AlgorithmVersion { get; set; } = RoutePlanner.AlgorithmVersion;

    [JsonPropertyName("csv_path")]
    public string CsvPath { get; set; } = "";

    [JsonPropertyName("csv_last_write_utc")]
    public string CsvLastWriteUtc { get; set; } = "";

    [JsonPropertyName("coord_mode")]
    public string CoordMode { get; set; } = "自动判断";

    [JsonPropertyName("headerless_order")]
    public string HeaderlessOrder { get; set; } = "Point,Y,X";

    [JsonPropertyName("axis_order_text")]
    public string AxisOrderText { get; set; } = "Point,Y,X";

    [JsonPropertyName("central_meridian")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public double CentralMeridian { get; set; }

    [JsonPropertyName("max_sortie_km")]
    public double MaxSortieKm { get; set; } = 18.0;

    [JsonPropertyName("output_dir")]
    public string OutputDirectory { get; set; } = "";

    [JsonPropertyName("partition_code")]
    public string PartitionCode { get; set; } = "";

    [JsonPropertyName("partition_manifest_path")]
    public string PartitionManifestPath { get; set; } = "";

    [JsonPropertyName("start_direction")]
    public string StartDirection { get; set; } = "西南";

    [JsonPropertyName("partition_start_side")]
    public string PartitionStartSide { get; set; } = "自动";

    [JsonPropertyName("takeoff_anchor_mode")]
    public string TakeoffAnchorMode { get; set; } = "自动";

    [JsonPropertyName("flight_direction")]
    public string FlightDirection { get; set; } = "自动";

    [JsonPropertyName("takeoff_anchor_ref")]
    public string TakeoffAnchorRef { get; set; } = "";

    [JsonPropertyName("custom_takeoff_x")]
    public double? CustomTakeoffX { get; set; }

    [JsonPropertyName("custom_takeoff_y")]
    public double? CustomTakeoffY { get; set; }

    [JsonPropertyName("selected_start_pid")]
    public string SelectedStartPid { get; set; } = "";

    [JsonPropertyName("takeoff_x")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public double? TakeoffX { get; set; }

    [JsonPropertyName("takeoff_y")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public double? TakeoffY { get; set; }


    [JsonPropertyName("active_start_ep")]
    public int ActiveStartEndpoint { get; set; }

    [JsonPropertyName("kmz_agl")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public double KmzAglM { get; set; } = 50.0;

    // Retained for backward compatibility with legacy Route Project JSON.
    // It is used only if the frozen real-time terrain-following KMZ workflow
    // is explicitly restored in a future version.
    [JsonPropertyName("kmz_speed")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public double KmzSpeedMps { get; set; } = 12.0;

    [JsonPropertyName("sorties")]
    public List<RouteSortie> Sorties { get; set; } = [];

    [JsonPropertyName("execution_segments")]
    public List<RouteExecutionSegment> ExecutionSegments { get; set; } = [];
}

/// <summary>Complete subdivision snapshot; active child is independent of the full set.</summary>
public sealed class RouteSubdivisionWorkspace
{
    public string SchemaVersion { get; set; } = "magops-route-subdivision-workspace-1";
    public string ManifestPath { get; set; } = "";
    public string ManifestJson { get; set; } = "";
    public string MotherKmlPath { get; set; } = "";
    public string MotherKmlContent { get; set; } = "";
    public bool OverviewActive { get; set; }
    public string ActivePartitionCode { get; set; } = "";
    public List<RouteSubdivisionPartition> Partitions { get; set; } = [];
}

public sealed class RouteSubdivisionPartition
{
    public string PartitionCode { get; set; } = "";
    public int Index { get; set; }
    public string KmlPath { get; set; } = "";
    public string EndpointCsvPath { get; set; } = "";
    public string RouteSourcePath { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public double CentralMeridian { get; set; }
    public string Datum { get; set; } = "";
    public List<SurveySubdivisionCutLine> Cuts { get; set; } = [];
    public List<SurveyBoundaryPoint> Boundary { get; set; } = [];
    public RoutePlanResult? Plan { get; set; }
    public RoutePartitionStartSide StartSide { get; set; }
    public RouteStartDirection StartDirection { get; set; }
    public RouteTakeoffAnchorMode TakeoffAnchorMode { get; set; }
    public Dictionary<string,double[]> MotherFlightDirections { get; set; } = [];
    public RouteFlightDirection FlightDirection { get; set; }
    public string TakeoffAnchorRef { get; set; } = "";
    public double MaxSortieKm { get; set; }
    public double AglM { get; set; }
    public string Status { get; set; } = "";
    public List<int> EditedSorties { get; set; } = [];
    public List<int> ReversedSorties { get; set; } = [];
    public List<int> BoundaryAdjustedSorties { get; set; } = [];
}
