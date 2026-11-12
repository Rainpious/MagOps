using System.Text.Json;
using MagOps.Core.Routes;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private bool RouteWorkspaceHasUnsavedChanges => _routeWorkspaceDirty || _routePartitionContexts.Values.Any(c => c.Dirty);
    private RouteSubdivisionWorkspace? _restoredSubdivisionSnapshot;
    private RouteSubdivisionWorkspace? CaptureRouteSubdivisionWorkspace(RoutePlanResult plan)
    {
        if (_routePartitionContexts.Count == 0) return null;
        if (!_routePartitionContexts.ContainsKey(plan.PartitionCode))
            throw new InvalidDataException("当前航线计划不属于已打开的分区工作区，无法保存。");
        SyncActivePartitionRouteContext();
        string manifestPath = _routePartitionContexts.Values.First().Child.ManifestPath;
        if (_routePartitionContexts.Values.Any(c => !c.Child.ManifestPath.Equals(manifestPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("分区工作区包含不同母区来源，不能合成一个工程。");
        string manifestJson = _restoredSubdivisionSnapshot?.ManifestPath == manifestPath
            ? _restoredSubdivisionSnapshot.ManifestJson : File.ReadAllText(manifestPath);
        using var manifest = JsonDocument.Parse(manifestJson);
        string motherPath = manifest.RootElement.GetProperty("source_kml").GetString() ?? "";
        var snapshot = new RouteSubdivisionWorkspace
        {
            ManifestPath = manifestPath, ManifestJson = manifestJson, MotherKmlPath = motherPath,
            MotherKmlContent = _restoredSubdivisionSnapshot?.ManifestPath == manifestPath
                ? _restoredSubdivisionSnapshot.MotherKmlContent : File.ReadAllText(motherPath),
            OverviewActive = _routeWorkspace.IsPartitionOverviewActive,
            ActivePartitionCode = _routeWorkspace.IsPartitionOverviewActive ? _routeWorkspace.OverviewSelection : _routeActivePartition?.PartitionCode ?? ""
        };
        foreach (var context in _routePartitionContexts.Values.OrderBy(c => c.Index))
        {
            if (context.Plan is { } childPlan) RouteIdentityService.EnsureReferences(childPlan.Sorties);
            snapshot.Partitions.Add(new()
            {
                PartitionCode = context.PartitionCode, Index = context.Index, KmlPath = context.Child.KmlPath,
                EndpointCsvPath = context.Child.EndpointCsvPath, RouteSourcePath = context.RouteSourcePath,
                SourceSha256 = context.SourceSha256,
                CentralMeridian = context.Child.CentralMeridian, Datum = context.Child.Datum, Cuts = context.Child.Cuts.ToList(),
                Boundary = context.Boundary.ToList(), Plan = context.Plan, StartSide = context.StartSide,
                StartDirection = context.StartDirection, TakeoffAnchorMode = context.TakeoffAnchorMode,
                MotherFlightDirections = context.MotherFlightDirections, FlightDirection = context.FlightDirection, TakeoffAnchorRef = context.TakeoffAnchorRef,
                MaxSortieKm = context.MaxSortieKm, AglM = context.AglM, Status = context.Status,
                EditedSorties = Indices(context.EditedSorties), ReversedSorties = Indices(context.ReversedSorties),
                BoundaryAdjustedSorties = Indices(context.BoundaryAdjustedSorties)
            });
            List<int> Indices(HashSet<RouteSortie> selected) => context.Plan is null ? [] :
                context.Plan.Sorties.Select((s, i) => (s, i)).Where(p => selected.Contains(p.s)).Select(p => p.i).ToList();
        }
        return snapshot;
    }

    private void RestoreRouteSubdivisionWorkspace(RouteSubdivisionWorkspace snapshot)
    {
        var contexts = snapshot.Partitions.Select(p =>
        {
            var context = new PartitionRouteRuntimeContext
            {
                PartitionCode = p.PartitionCode, Index = p.Index,
                Child = new(p.KmlPath, p.EndpointCsvPath, snapshot.ManifestPath, p.Index, p.PartitionCode,
                    p.CentralMeridian, p.Datum, p.Cuts, false),
                Boundary = p.Boundary, RouteSourcePath = p.RouteSourcePath, SourceSha256 = p.SourceSha256, Plan = p.Plan,
                StartSide = p.StartSide, StartDirection = p.StartDirection, TakeoffAnchorMode = p.TakeoffAnchorMode,
                MotherFlightDirections = p.MotherFlightDirections, FlightDirection = p.FlightDirection, TakeoffAnchorRef = p.TakeoffAnchorRef,
                MaxSortieKm = p.MaxSortieKm, AglM = p.AglM, Status = p.Status, Dirty = false
            };
            if (p.Plan is { } plan)
            {
                // Restore the saved plan and manual edits; never regenerate from today's source CSV.
                foreach (int i in p.EditedSorties) context.EditedSorties.Add(plan.Sorties[i]);
                foreach (int i in p.ReversedSorties) context.ReversedSorties.Add(plan.Sorties[i]);
                foreach (int i in p.BoundaryAdjustedSorties) context.BoundaryAdjustedSorties.Add(plan.Sorties[i]);
                if (!PartitionSourceMatchesSnapshot(p.RouteSourcePath, p.SourceSha256))
                    context.Status = "来源缺失或已变化；当前为保存快照，重算前核对";
            }
            return context;
        }).ToArray();
        ClearPartitionRouteWorkspace();
        _restoredSubdivisionSnapshot = snapshot;
        foreach (var context in contexts) _routePartitionContexts.Add(context.PartitionCode, context);
        _routeWorkspace.SetPartitionOverviewEntries(contexts.Select(c => c.ToOverviewEntry()).ToArray(), snapshot.ActivePartitionCode);
        if (!snapshot.OverviewActive && snapshot.ActivePartitionCode.Length > 0 && _routePartitionContexts[snapshot.ActivePartitionCode].Plan is not null)
            ActivatePartitionRouteContext(_routePartitionContexts[snapshot.ActivePartitionCode]);
        else
        {
            _routePlan = null; _routeInference = null; _routeWorkspace.ClearRoutePreview();
            _routeWorkspace.SetKmlPath(snapshot.MotherKmlPath);
            _routeWorkspace.ShowPartitionOverviewTab();
        }
        SetRouteWorkspaceDirty(false);
    }

    private static bool PartitionSourceMatchesSnapshot(string path, string sha256)
    {
        try
        {
            return sha256.Length > 0 && File.Exists(path) &&
                GeneratedArtifactIdentity.Hash(path).Equals(sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
