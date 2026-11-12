using MagOps.Core.Routes;
using MagOps.Core.SurveyPlanning;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

/// <summary>
/// In-memory orchestration state for one generated Pxx child. The existing
/// single-partition editor remains the detailed editor; this context prevents
/// route plans and manual-edit state from leaking across partition boundaries.
/// </summary>
internal sealed class PartitionRouteRuntimeContext
{
    public string PartitionCode { get; init; } = "";
    public int Index { get; init; }
    public KmlSurveySubdivisionResolvedChild Child { get; init; } = null!;
    public IReadOnlyList<SurveyBoundaryPoint> Boundary { get; init; } = [];
    public RoutePartitionStartSide StartSide { get; set; } =
        RoutePartitionStartSide.Automatic;
    public RouteStartDirection StartDirection { get; set; } =
        RouteStartDirection.Southwest;
    public RouteTakeoffAnchorMode TakeoffAnchorMode { get; set; } = RouteTakeoffAnchorMode.Automatic;
    public Dictionary<string,double[]> MotherFlightDirections { get; set; } = [];
    public RouteFlightDirection FlightDirection { get; set; } = RouteFlightDirection.Automatic;
    public string TakeoffAnchorRef { get; set; } = "";
    public double MaxSortieKm { get; set; } = 8d;
    public double AglM { get; set; } = 100d;
    // Starts as the generated child endpoint CSV. After the existing detailed
    // concave CONNECT/SPLIT flow creates a route-ready table, this is updated
    // to Plan.SourcePath so later overview parameter edits can rebuild the same
    // confirmed partition instead of falling back to the unresolved raw table.
    public string RouteSourcePath { get; set; } = "";
    public string SourceSha256 { get; set; } = "";
    public RoutePlanResult? Plan { get; set; }
    public string Status { get; set; } = "等待生成";
    public bool Dirty { get; set; }

    public HashSet<RouteSortie> EditedSorties { get; } = [];
    public HashSet<RouteSortie> ReversedSorties { get; } = [];
    public HashSet<RouteSortie> BoundaryAdjustedSorties { get; } = [];

    public PartitionRouteOverviewEntry ToOverviewEntry() => new()
    {
        PartitionCode = PartitionCode,
        Boundary = Boundary,
        Plan = Plan,
        StartSide = StartSide,
        StartDirection = StartDirection,
        MaxSortieKm = MaxSortieKm,
        Dirty = Dirty,
        Status = Status
    };
}
