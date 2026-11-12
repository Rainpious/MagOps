using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MagOps.Core.Routes;

namespace MagOps.Core.Analysis;

public sealed class ConcaveSplitRoutePair
{
    [JsonPropertyName("source_line")]
    public string SourceLine { get; set; } = "";

    [JsonPropertyName("first_output_line")]
    public string FirstOutputLine { get; set; } = "";

    [JsonPropertyName("second_output_line")]
    public string SecondOutputLine { get; set; } = "";
}

public sealed class ConcaveSplitRouteGroup
{
    [JsonPropertyName("source_line")]
    public string SourceLine { get; set; } = "";

    [JsonPropertyName("output_lines")]
    public List<string> OutputLines { get; set; } = [];
}

public sealed class ConcaveSplitRouteContext
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } =
        ConcaveSplitRoutePlanService.SchemaVersion;

    [JsonPropertyName("processed_path")]
    public string ProcessedPath { get; set; } = "";

    [JsonPropertyName("generated_at")]
    public string GeneratedAt { get; set; } = "";

    // Kept for backward compatibility with v0.5-v0.77 sidecars.
    [JsonPropertyName("split_pairs")]
    public List<ConcaveSplitRoutePair> SplitPairs { get; set; } = [];

    [JsonPropertyName("split_groups")]
    public List<ConcaveSplitRouteGroup> SplitGroups { get; set; } = [];
}

public sealed record ConcaveSplitPlanApplication(
    RoutePlanResult Plan,
    int ProtectedSplitPairs,
    int RouteBranches);

/// <summary>
/// Keeps disconnected concave-boundary segments from being re-connected by
/// the unchanged v25 snake transition. Segment 1 remains in branch 0;
/// segment 2/3/... are put into branch 1/2/... and a sortie boundary is forced
/// between branches. This now supports any number of disconnected segments.
/// </summary>
public sealed class ConcaveSplitRoutePlanService
{
    public const string SchemaVersion = "magops-concave-split-route-2";
    public const string LegacySchemaVersion = "magops-concave-split-route-1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string SidecarPath(string processedPath) =>
        Path.GetFullPath(processedPath) + ".magops-split.json";

    public string WriteContext(
        string processedPath,
        ConcaveLineProcessResult processResult)
    {
        string sidecar = SidecarPath(processedPath);

        List<ConcaveSplitRouteGroup> groups = processResult.Mappings
            .Where(mapping =>
                mapping.Action.Equals(
                    ConcaveLineActions.Split,
                    StringComparison.OrdinalIgnoreCase))
            .Select(mapping =>
            {
                List<string> labels = mapping.OutputLineLabels
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (labels.Count < 2 &&
                    !string.IsNullOrWhiteSpace(mapping.FirstOutputLine) &&
                    !string.IsNullOrWhiteSpace(mapping.SecondOutputLine))
                {
                    labels = [mapping.FirstOutputLine, mapping.SecondOutputLine];
                }
                return new ConcaveSplitRouteGroup
                {
                    SourceLine = mapping.SourceLine,
                    OutputLines = labels
                };
            })
            .Where(group => group.OutputLines.Count >= 2)
            .ToList();

        if (groups.Count == 0)
        {
            if (File.Exists(sidecar))
                File.Delete(sidecar);
            return "";
        }

        List<ConcaveSplitRoutePair> pairs = groups
            .Select(group => new ConcaveSplitRoutePair
            {
                SourceLine = group.SourceLine,
                FirstOutputLine = group.OutputLines[0],
                SecondOutputLine = group.OutputLines[1]
            })
            .ToList();

        var context = new ConcaveSplitRouteContext
        {
            ProcessedPath = Path.GetFullPath(processedPath),
            GeneratedAt = DateTimeOffset.Now.ToString("O"),
            SplitPairs = pairs,
            SplitGroups = groups
        };

        string? directory = Path.GetDirectoryName(sidecar);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(
            sidecar,
            JsonSerializer.Serialize(context, JsonOptions),
            new UTF8Encoding(false));

        return sidecar;
    }

    public bool TryLoadContext(
        string processedPath,
        out ConcaveSplitRouteContext? context)
    {
        context = null;
        string sidecar = SidecarPath(processedPath);
        if (!File.Exists(sidecar))
            return false;

        try
        {
            ConcaveSplitRouteContext? loaded =
                JsonSerializer.Deserialize<ConcaveSplitRouteContext>(
                    File.ReadAllText(sidecar, Encoding.UTF8),
                    JsonOptions);

            if (loaded is null ||
                (!loaded.SchemaVersion.Equals(
                     SchemaVersion,
                     StringComparison.OrdinalIgnoreCase) &&
                 !loaded.SchemaVersion.Equals(
                     LegacySchemaVersion,
                     StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            loaded.SplitGroups ??= [];
            loaded.SplitPairs ??= [];
            if (loaded.SplitGroups.Count == 0 && loaded.SplitPairs.Count > 0)
            {
                loaded.SplitGroups = loaded.SplitPairs
                    .Where(pair =>
                        !string.IsNullOrWhiteSpace(pair.FirstOutputLine) &&
                        !string.IsNullOrWhiteSpace(pair.SecondOutputLine))
                    .Select(pair => new ConcaveSplitRouteGroup
                    {
                        SourceLine = pair.SourceLine,
                        OutputLines = [pair.FirstOutputLine, pair.SecondOutputLine]
                    })
                    .ToList();
            }

            if (loaded.SplitGroups.Count == 0)
                return false;

            context = loaded;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool MixesProtectedBranches(
        IEnumerable<string> lines,
        ConcaveSplitRouteContext context)
    {
        Dictionary<string, int> branchMap = BuildBranchMap(context);
        return lines
            .Select(line => branchMap.TryGetValue(line, out int branch) ? branch : 0)
            .Distinct()
            .Take(2)
            .Count() > 1;
    }

    public ConcaveSplitPlanApplication Apply(
        RoutePlanResult plan,
        ConcaveSplitRouteContext context)
    {
        HashSet<string> knownLines = plan.Inference.Grouped.Keys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<ConcaveSplitRouteGroup> usableGroups = GetGroups(context)
            .Select(group => new ConcaveSplitRouteGroup
            {
                SourceLine = group.SourceLine,
                OutputLines = group.OutputLines
                    .Where(knownLines.Contains)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .Where(group => group.OutputLines.Count >= 2)
            .ToList();

        if (usableGroups.Count == 0)
            return new ConcaveSplitPlanApplication(plan, 0, 1);

        var branchMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (ConcaveSplitRouteGroup group in usableGroups)
        {
            for (int index = 0; index < group.OutputLines.Count; index++)
                branchMap[group.OutputLines[index]] = index;
        }

        int maxBranch = branchMap.Values.DefaultIfEmpty(0).Max();
        var branchInputs = new List<List<string>>();
        for (int branch = 0; branch <= maxBranch; branch++)
        {
            int localBranch = branch;
            List<string> lines = plan.OrderedLines
                .Where(line =>
                {
                    int mapped = branchMap.TryGetValue(line, out int value)
                        ? value
                        : 0;
                    return mapped == localBranch;
                })
                .ToList();
            if (lines.Count > 0)
                branchInputs.Add(lines);
        }

        if (branchInputs.Count <= 1)
            return new ConcaveSplitPlanApplication(plan, 0, 1);

        int startBranchIndex = branchInputs.FindIndex(branch =>
            branch.Contains(plan.StartPoint.Line, StringComparer.OrdinalIgnoreCase));
        if (startBranchIndex > 0)
        {
            List<string> selected = branchInputs[startBranchIndex];
            branchInputs.RemoveAt(startBranchIndex);
            branchInputs.Insert(0, selected);
        }

        var combinedOrder = new List<string>();
        var combinedSorties = new List<RouteSortie>();
        var combinedOverLimit = new List<RouteOverLimitRoundTrip>();
        RouteEndpoint? firstStart = null;
        int firstStartEp = plan.ActiveStartEndpoint;
        int overPairIndex = 1;

        foreach (List<string> branch in branchInputs)
        {
            RouteEndpoint branchStart;
            if (firstStart is null &&
                branch.Contains(plan.StartPoint.Line, StringComparer.OrdinalIgnoreCase))
            {
                branchStart = plan.StartPoint;
            }
            else
            {
                branchStart = RoutePlanner.GetStartPointFromCorner(
                    plan.Inference.Grouped,
                    branch,
                    plan.Options.StartDirection,
                    plan.Mode);
            }

            var orderInfo = RoutePlanner.GetLineOrder(
                plan.Inference.Grouped,
                branch,
                branchStart,
                plan.Mode,
                plan.CentralMeridian);

            firstStart ??= branchStart;
            if (combinedOrder.Count == 0)
                firstStartEp = orderInfo.StartEp;

            combinedOrder.AddRange(orderInfo.Order);
            combinedSorties.AddRange(
                RoutePlanner.SplitSorties(
                    plan.Inference.Grouped,
                    orderInfo.Order,
                    orderInfo.StartEp,
                    plan.Options.MaxSortieKm * 1000.0,
                    plan.Mode,
                    plan.CentralMeridian));

            foreach (RouteOverLimitRoundTrip row in
                     RoutePlanner.GetOverLimitRoundTrips(
                         plan.Inference.Grouped,
                         orderInfo.Order,
                         orderInfo.StartEp,
                         plan.Options.MaxSortieKm * 1000.0,
                         plan.Mode,
                         plan.CentralMeridian))
            {
                combinedOverLimit.Add(
                    new RouteOverLimitRoundTrip(
                        overPairIndex++,
                        row.First,
                        row.Second,
                        row.LengthM));
            }
        }

        for (int i = 0; i < combinedSorties.Count; i++)
            combinedSorties[i].Name = $"Sortie {i + 1:D2}";

        var warnings = plan.Warnings.ToList();
        warnings.Add(
            $"凹形 SPLIT 安全模式：检测到 {usableGroups.Count} 组分割测线，" +
            $"按 {branchInputs.Count} 个断段分支分别规划并强制结束架次；" +
            "避免 v25 蛇形连接段重新跨越凹口。");

        var inference = new RouteLineInference
        {
            Grouped = plan.Inference.Grouped,
            OrderedLines = combinedOrder,
            Endpoints = plan.Inference.Endpoints,
            MatchMode = plan.Inference.MatchMode,
            Merged = plan.Inference.Merged
        };

        var adapted = new RoutePlanResult
        {
            SourcePath = plan.SourcePath,
            Mode = plan.Mode,
            CentralMeridian = plan.CentralMeridian,
            Inference = inference,
            OrderedLines = combinedOrder,
            StartPoint = firstStart ?? plan.StartPoint,
            ActiveStartEndpoint = firstStartEp,
            Sorties = combinedSorties,
            OverLimitRoundTrips = combinedOverLimit,
            GeometryDiagnostic = plan.GeometryDiagnostic,
            Warnings = warnings,
            Options = plan.Options
        };

        return new ConcaveSplitPlanApplication(
            adapted,
            usableGroups.Count,
            branchInputs.Count);
    }

    private static Dictionary<string, int> BuildBranchMap(
        ConcaveSplitRouteContext context)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (ConcaveSplitRouteGroup group in GetGroups(context))
        {
            for (int index = 0; index < group.OutputLines.Count; index++)
            {
                string line = group.OutputLines[index];
                if (!string.IsNullOrWhiteSpace(line))
                    result[line] = index;
            }
        }
        return result;
    }

    private static List<ConcaveSplitRouteGroup> GetGroups(
        ConcaveSplitRouteContext context)
    {
        if (context.SplitGroups is { Count: > 0 })
            return context.SplitGroups;
        return (context.SplitPairs ?? [])
            .Where(pair =>
                !string.IsNullOrWhiteSpace(pair.FirstOutputLine) &&
                !string.IsNullOrWhiteSpace(pair.SecondOutputLine))
            .Select(pair => new ConcaveSplitRouteGroup
            {
                SourceLine = pair.SourceLine,
                OutputLines = [pair.FirstOutputLine, pair.SecondOutputLine]
            })
            .ToList();
    }
}
