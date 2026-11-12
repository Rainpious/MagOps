using System.Text.Json;
using System.Text.Json.Nodes;
using MagOps.Application;
using MagOps.Core.Analysis;
using MagOps.Core.Routes;
using MagOps.Infrastructure.Services;
using MagOps.Infrastructure.Storage;

namespace MagOps.SmokeTests;

internal static class PartitionMotherIdentityChecks
{
    public static void Run()
    {
        string root = Path.GetFullPath(Path.Combine("artifacts/ui-work-20261021/mother-identity", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string mother = Path.Combine(root, "mother.kml");
        SpatialGeometryService.WriteKmlPolygons(mother, [new("母区", [new(108, 35), new(108.02, 35),
            new(108.02, 35.02), new(108, 35.02)], [])]);
        byte[] original = File.ReadAllBytes(mother);
        var store = new JsonStore();
        var workspace = new WorkspaceService(store);
        var artifacts = new ArtifactService(store, workspace);
        var projects = new RouteProjectService();
        var workflow = new RoutePlanningWorkflow(new RoutePlanner(), new ConcaveSplitRoutePlanService(),
            new RouteExecutionSegmentPlanner(), projects, new RouteExportService(), artifacts,
            new RouteManifestWriter(), new RouteResultService(store, workspace, artifacts),
            new ProcessingRunService(store, workspace, artifacts));
        var subdivision = new KmlSurveySubdivisionService();
        var preparation = subdivision.Prepare(mother, 108, "CGCS2000", new() { SpacingM = 200 }, 8);
        var cuts = subdivision.CreateEqualCuts(preparation, MagOps.Core.SurveyPlanning.SurveySubdivisionAxis.AlongSurveyLine, 2);
        var bundle = subdivision.WriteSubdivision(preparation, cuts, MagOps.Core.SurveyPlanning.SurveySubdivisionAxis.AlongSurveyLine,
            Path.Combine(root, "split"), overlapLengthM: 10);
        RoutePlanResult Generate(KmlSurveySubdivisionBundle b, int i) => workflow.Generate(new(b.Children[i].EndpointCsvPath,
            new() { CoordinateMode = RouteCoordinateMode.Projected, CentralMeridian = 108, MaxSortieKm = 8 },
            PartitionCode: b.Children[i].PartitionCode, PartitionManifestPath: b.ManifestPath));
        var first = Generate(bundle, 0);
        var second = Generate(bundle, 1);
        var firstIds = first.Sorties.SelectMany(s => s.TaskReferences).GroupBy(r => r.LineName)
            .ToDictionary(g => g.Key, g => g.Select(r => r.MotherLineStableId).Distinct().Single());
        var secondIds = second.Sorties.SelectMany(s => s.TaskReferences).GroupBy(r => r.LineName)
            .ToDictionary(g => g.Key, g => g.Select(r => r.MotherLineStableId).Distinct().Single());
        if (firstIds.Count == 0 || !firstIds.Keys.Intersect(secondIds.Keys).Any() ||
            firstIds.Keys.Intersect(secondIds.Keys).Any(name => firstIds[name] != secondIds[name] || firstIds[name].Length == 0))
            throw new Exception("Clipped tasks do not share explicit mother identity");
        var tasks = new FieldOperationsAnalysisService().FromRoutePlan(first).SurveyLines;
        if (tasks.Any(t => !firstIds.Values.Contains(t.MotherLineStableId))) throw new Exception("Field task adapter lost mother identity");
        string project = Path.Combine(root, "child.json");
        projects.Save(project, first, root, "Point,Y,X", "投影", "西南", "自动");
        var saved = projects.Load(project);
        if (!saved.Sorties.SelectMany(s => s.TaskReferences).Select(r => r.MotherLineStableId)
            .SequenceEqual(first.Sorties.SelectMany(s => s.TaskReferences).Select(r => r.MotherLineStableId)))
            throw new Exception("Saving/reopening changed mother identities");
        var regenerated = Generate(bundle, 0);
        projects.Save(project, regenerated, root, "Point,Y,X", "投影", "西南", "自动");
        if (!saved.Sorties.SelectMany(s => s.TaskReferences).Select(r => r.SurveyTaskStableId)
            .SequenceEqual(projects.Load(project).Sorties.SelectMany(s => s.TaskReferences).Select(r => r.SurveyTaskStableId)))
            throw new Exception("Regenerating the same design lost persistent child task identities");
        var other = subdivision.WriteSubdivision(preparation, cuts, MagOps.Core.SurveyPlanning.SurveySubdivisionAxis.AlongSurveyLine,
            Path.Combine(root, "other"), overlapLengthM: 10);
        var otherPlan = Generate(other, 0);
        if (otherPlan.Sorties.SelectMany(s => s.TaskReferences).Any(r => firstIds.Values.Contains(r.MotherLineStableId)))
            throw new Exception("An independent same-name design inherited another mother's identity");
        byte[] savedBytes = File.ReadAllBytes(project);
        Refuse(() => projects.Save(project, otherPlan, root, "Point,Y,X", "投影", "西南", "自动"));
        if (!savedBytes.SequenceEqual(File.ReadAllBytes(project))) throw new Exception("Conflicting identity replaced existing project");
        // A genuine child split must retain the mother, while unaffected tasks retain their own IDs.
        var cutPlan = Generate(bundle, 0);
        var line = cutPlan.Inference.Grouped.First();
        double dx = line.Value[1].X - line.Value[0].X, dy = line.Value[1].Y - line.Value[0].Y;
        double x = (line.Value[0].X + line.Value[1].X) / 2, y = (line.Value[0].Y + line.Value[1].Y) / 2;
        cutPlan.Options.MaxSortieKm = Math.Sqrt(dx * dx + dy * dy) * .75 / 1000;
        var cut = workflow.ApplyCutLine(cutPlan, [line.Key], x - dy, y + dx, x + dy, y - dx).Plan;
        if (cut.Sorties.SelectMany(s => s.TaskReferences).Where(r => r.LineName == line.Key).Any(r => r.MotherLineStableId != firstIds[line.Key]))
            throw new Exception("Execution cut replaced the original mother identity");
        var manifest = JsonNode.Parse(File.ReadAllText(bundle.ManifestPath))!;
        var loadedCatalog = PartitionMotherLineIdentityService.ReadManifest(bundle.ManifestPath);
        if (string.IsNullOrWhiteSpace(loadedCatalog.DesignId) || loadedCatalog.Partitions.Count == 0)
            throw new Exception("Explicit mother identity manifest did not load");
        void CheckSavedCatalog(JsonNode data, double cm = 108)
        {
            using var document = JsonDocument.Parse(data.ToJsonString());
            PartitionMotherLineIdentityService.ValidateSavedReferences(document.RootElement,
                [(first.PartitionCode, first.Sorties), ("", Array.Empty<RouteSortie>())], cm);
        }
        CheckSavedCatalog(manifest);
        Refuse(() => CheckSavedCatalog(manifest, 111));
        var wrongProjection = manifest.DeepClone();
        wrongProjection["central_meridian"] = 111;
        Refuse(() => CheckSavedCatalog(wrongProjection));
        var wrongDatum = manifest.DeepClone();
        wrongDatum["datum"] = "WGS84";
        Refuse(() => CheckSavedCatalog(wrongDatum));
        // A child with no saved plan must still belong to the same catalog.
        var missingPendingChild = manifest.DeepClone();
        missingPendingChild["mother_line_identity"]!["Partitions"]!.AsArray().RemoveAt(1);
        Refuse(() => CheckSavedCatalog(missingPendingChild));
        var duplicateChild = manifest.DeepClone();
        duplicateChild["children"]![1]!["partition_code"] = duplicateChild["children"]![0]!["partition_code"]!.GetValue<string>();
        Refuse(() => CheckSavedCatalog(duplicateChild));
        var corruptPlan = Generate(bundle, 0);
        corruptPlan.Sorties[0].Points[0].X += 10;
        Refuse(() => PartitionMotherLineIdentityService.BindGeneratedPlan(corruptPlan));
        var malformed = JsonNode.Parse(manifest.ToJsonString())!;
        malformed["mother_line_identity"]!["Partitions"]![0]!["Lines"]![0]!["MotherStableId"] = "foreign-mother";
        File.WriteAllText(bundle.ManifestPath, malformed.ToJsonString());
        Refuse(() => Generate(bundle, 0));
        File.WriteAllText(bundle.ManifestPath, manifest.ToJsonString());
        // Deliberate SPLIT aliases come from the recorded operation, never suffix guessing.
        var aliasPlan = Generate(bundle, 0);
        var aliasLine = aliasPlan.Sorties[0].SourceLines[0];
        aliasPlan.Sorties[0].SourceLines[0] = "explicit-piece";
        aliasPlan.Sorties[0].TaskReferences.Clear();
        Refuse(() => PartitionMotherLineIdentityService.BindGeneratedPlan(aliasPlan));
        PartitionMotherLineIdentityService.BindGeneratedPlan(aliasPlan,
            new Dictionary<string,string> { ["explicit-piece"] = aliasLine });
        if (aliasPlan.Sorties[0].TaskReferences[0].MotherLineStableId != firstIds[aliasLine])
            throw new Exception("Explicit split alias did not bind to original mother");
        File.AppendAllText(bundle.Children[0].EndpointCsvPath, "\n");
        Refuse(() => Generate(bundle, 0));
        // Legacy manifests remain readable, but no global mother key is inferred from names.
        manifest.AsObject().Remove("mother_line_identity");
        File.WriteAllText(bundle.ManifestPath, manifest.ToJsonString());
        var legacy = Generate(bundle, 0);
        if (legacy.Sorties.SelectMany(s => s.TaskReferences).Any()) throw new Exception("Legacy manifest was silently assigned new shared mother identity");
        if (!original.SequenceEqual(File.ReadAllBytes(mother))) throw new Exception("Mother input was modified");
    }

    private static void Refuse(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Invalid design identity was accepted");
    }
}
