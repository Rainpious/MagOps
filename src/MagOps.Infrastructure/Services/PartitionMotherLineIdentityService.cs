using System.Text.Json;
using MagOps.Core.Routes;
using MagOps.Core.SurveyPlanning;

namespace MagOps.Infrastructure.Services;

public sealed record PartitionMotherLineDefinition(string LineName, string StableId, int Order,
    IReadOnlyList<SurveyLineSegment> Segments);
public sealed record PartitionChildLineDefinition(string LineName, string MotherStableId,
    IReadOnlyList<SurveyLineSegment> Segments);
public sealed record PartitionChildIdentity(string PartitionCode, IReadOnlyList<PartitionChildLineDefinition> Lines);
public sealed record PartitionMotherLineIdentityCatalog(string SchemaVersion, string DesignId,
    double CentralMeridian, string Datum, IReadOnlyList<PartitionMotherLineDefinition> Mothers,
    IReadOnlyList<PartitionChildIdentity> Partitions);

/// <summary>Explicit design lineage, not a proximity-based recognition or merge rule.</summary>
public static class PartitionMotherLineIdentityService
{
    public const string SchemaVersion = "magops-partition-mother-identities-1";
    // Only a serialization/geometry-binding tolerance in projected metres.
    private const double BindingToleranceM = .001;

    public static PartitionMotherLineIdentityCatalog ReadManifest(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("mother_line_identity", out var block) || block.ValueKind != JsonValueKind.Object)
            throw Invalid("清单缺少显式母线身份目录，旧设计需先绑定身份");
        var catalog = block.Deserialize<PartitionMotherLineIdentityCatalog>() ?? throw Invalid("身份清单为空");
        ValidateManifest(document.RootElement, catalog);
        return catalog;
    }

    public static PartitionMotherLineIdentityCatalog Create(SurveyLinePlanResult mother,
        SurveyLineSubdivisionResult split, double centralMeridian, string datum)
    {
        var mothers = mother.Lines.Select((line, index) => new PartitionMotherLineDefinition(
            line.LineLabel, "mother-" + Guid.NewGuid().ToString("N"), index + 1, line.Segments.ToArray())).ToArray();
        var byName = mothers.ToDictionary(m => m.LineName, StringComparer.OrdinalIgnoreCase);
        var result = new PartitionMotherLineIdentityCatalog(SchemaVersion, "design-" + Guid.NewGuid().ToString("N"),
            centralMeridian, datum, mothers, split.Subareas.Select(p => new PartitionChildIdentity($"P{p.Index:D2}",
                p.Lines.Select(l => new PartitionChildLineDefinition(l.LineLabel, byName[l.LineLabel].StableId,
                    l.Segments.ToArray())).ToArray())).ToArray());
        Validate(result);
        return result;
    }

    /// <summary>Bind a freshly generated child plan. Historical manifests without an explicit catalog are unchanged.</summary>
    public static bool BindGeneratedPlan(RoutePlanResult plan, IReadOnlyDictionary<string, string>? explicitAliases = null)
    {
        if (string.IsNullOrWhiteSpace(plan.PartitionCode) || string.IsNullOrWhiteSpace(plan.PartitionManifestPath) ||
            !File.Exists(plan.PartitionManifestPath)) return false;
        using var document = JsonDocument.Parse(File.ReadAllText(plan.PartitionManifestPath));
        var root = document.RootElement;
        if (!root.TryGetProperty("mother_line_identity", out var block)) return false;
        var catalog = block.Deserialize<PartitionMotherLineIdentityCatalog>()
            ?? throw Invalid("身份清单为空");
        ValidateManifest(root, catalog);
        if (plan.Mode != "projected" || plan.CentralMeridian != catalog.CentralMeridian)
            throw Invalid("计划与母区的投影基准不一致");
        var childRows = root.GetProperty("children").EnumerateArray().ToArray();
        var child = catalog.Partitions.SingleOrDefault(p => p.PartitionCode.Equals(plan.PartitionCode, StringComparison.OrdinalIgnoreCase))
            ?? throw Invalid("清单没有当前子区");
        var manifestChild = childRows.Single(c => string.Equals(c.GetProperty("partition_code").GetString(),
            plan.PartitionCode, StringComparison.OrdinalIgnoreCase));
        string directory = Path.GetDirectoryName(Path.GetFullPath(plan.PartitionManifestPath))!;
        string endpointPath = Path.GetFullPath(Path.Combine(directory, manifestChild.GetProperty("endpoint_csv").GetString() ?? ""));
        if (!endpointPath.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(endpointPath) || !manifestChild.TryGetProperty("endpoint_csv_sha256", out var sha) ||
            !GeneratedArtifactIdentity.Hash(endpointPath).Equals(sha.GetString(), StringComparison.OrdinalIgnoreCase))
            throw Invalid("子区端点文件缺失、越界或与清单不一致");
        var lines = child.Lines.ToDictionary(l => l.LineName, StringComparer.OrdinalIgnoreCase);
        var bindings = new List<(RouteSortie Sortie, int Index, string MotherId)>();
        foreach (var sortie in plan.Sorties)
        {
            if (sortie.SourceLines.Count == 0) continue; // A manual route has no invented design identity.
            if (sortie.Points.Count < sortie.SourceLines.Count * 2) throw Invalid("架次端点不完整");
            for (int i = 0; i < sortie.SourceLines.Count; i++)
            {
                string label = sortie.SourceLines[i];
                string fingerprint = RouteIdentityService.Fingerprint(sortie.Points[2 * i], sortie.Points[2 * i + 1]);
                var existing = sortie.TaskReferences.FirstOrDefault(r => r.ExecutionSegmentId.Equals(label, StringComparison.OrdinalIgnoreCase));
                existing ??= sortie.TaskReferences.FirstOrDefault(r => r.LineName.Equals(label, StringComparison.OrdinalIgnoreCase) &&
                    r.GeometryFingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase));
                string mother = explicitAliases is not null && explicitAliases.TryGetValue(label, out var original)
                    ? original : existing?.LineName is { Length: > 0 } name ? name : label;
                if (!lines.TryGetValue(mother, out var line) ||
                    !OnSegments(sortie.Points[2 * i].X, sortie.Points[2 * i].Y, line.Segments) ||
                    !OnSegments(sortie.Points[2 * i + 1].X, sortie.Points[2 * i + 1].Y, line.Segments))
                    throw Invalid($"{label} 没有与当前子区几何一致的显式母线映射");
                if (existing?.MotherLineStableId is { Length: > 0 } id && id != line.MotherStableId)
                    throw Invalid($"{label} 已有母线身份与当前设计不同，需另存设计或核对迁移");
                bindings.Add((sortie, i, line.MotherStableId));
            }
        }
        RouteIdentityService.EnsureReferences(plan.Sorties);
        foreach (var binding in bindings) binding.Sortie.TaskReferences[binding.Index].MotherLineStableId = binding.MotherId;
        return true;
    }

    /// <summary>Validate saved lineage without requiring live files or regenerating edited geometry.</summary>
    public static void ValidateSavedReferences(JsonElement manifest,
        IEnumerable<(string PartitionCode, IReadOnlyList<RouteSortie> Sorties)> plans,
        double? centralMeridian = null)
    {
        if (!manifest.TryGetProperty("mother_line_identity", out var block)) return;
        var catalog = block.Deserialize<PartitionMotherLineIdentityCatalog>() ?? throw Invalid("身份清单为空");
        ValidateManifest(manifest, catalog);
        if (centralMeridian.HasValue && centralMeridian.Value != catalog.CentralMeridian)
            throw Invalid("保存的工程与母线清单投影不一致");
        foreach (var (code, sorties) in plans)
        {
            // The top-level project record may intentionally hold no active
            // child plan (for example, a pending-only subdivision snapshot).
            // It has no lineage to validate until it carries sorties or a
            // partition identity.
            if (string.IsNullOrWhiteSpace(code) && sorties.Count == 0) continue;
            var child = catalog.Partitions.SingleOrDefault(c => c.PartitionCode.Equals(code, StringComparison.OrdinalIgnoreCase))
                ?? throw Invalid("保存的计划没有对应子区");
            var lines = child.Lines.ToDictionary(l => l.LineName, StringComparer.OrdinalIgnoreCase);
            var allowed = child.Lines.Select(l => l.MotherStableId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var sortie in sorties)
            {
                if (sortie.SourceLines.Count > 0 && sortie.TaskReferences.Count != sortie.SourceLines.Count)
                    throw Invalid("保存的子任务缺少身份引用");
                foreach (var reference in sortie.TaskReferences)
                    if (!allowed.Contains(reference.MotherLineStableId) ||
                        (lines.TryGetValue(reference.LineName, out var line) && reference.MotherLineStableId != line.MotherStableId))
                        throw Invalid("保存的母线引用不属于当前子区设计");
            }
        }
    }

    private static void ValidateManifest(JsonElement manifest, PartitionMotherLineIdentityCatalog catalog)
    {
        Validate(catalog);
        if (!manifest.TryGetProperty("central_meridian", out var cm) || cm.ValueKind != JsonValueKind.Number ||
            !cm.TryGetDouble(out double value) || value != catalog.CentralMeridian ||
            !manifest.TryGetProperty("datum", out var datum) || datum.ValueKind != JsonValueKind.String || datum.GetString() != catalog.Datum)
            throw Invalid("分区清单与母线目录的投影基准不一致");
        if (!manifest.TryGetProperty("children", out var children) || children.ValueKind != JsonValueKind.Array)
            throw Invalid("分区清单缺少子区");
        var codes = children.EnumerateArray().Select(child =>
            child.ValueKind == JsonValueKind.Object && child.TryGetProperty("partition_code", out var code) &&
                code.ValueKind == JsonValueKind.String ? code.GetString() ?? "" : "").ToArray();
        if (codes.Length != catalog.Partitions.Count ||
            codes.Distinct(StringComparer.OrdinalIgnoreCase).Count() != codes.Length ||
            !codes.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(catalog.Partitions.Select(p => p.PartitionCode)))
            throw Invalid("子区身份与分区清单不一致");
    }

    public static void Validate(PartitionMotherLineIdentityCatalog catalog)
    {
        if (catalog.SchemaVersion != SchemaVersion || string.IsNullOrWhiteSpace(catalog.DesignId) ||
            !double.IsFinite(catalog.CentralMeridian) || string.IsNullOrWhiteSpace(catalog.Datum) ||
            catalog.Mothers is null || catalog.Partitions is null || catalog.Mothers.Count == 0 || catalog.Partitions.Count == 0 ||
            catalog.Mothers.Any(m => m is null || string.IsNullOrWhiteSpace(m.LineName) || string.IsNullOrWhiteSpace(m.StableId) || m.Order <= 0 || !ValidSegments(m.Segments)) ||
            catalog.Mothers.Select(m => m.LineName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Mothers.Count ||
            catalog.Mothers.Select(m => m.StableId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Mothers.Count ||
            catalog.Mothers.Select(m => m.Order).Distinct().Count() != catalog.Mothers.Count ||
            catalog.Partitions.Any(p => p is null || string.IsNullOrWhiteSpace(p.PartitionCode) || p.Lines is null) ||
            catalog.Partitions.Select(p => p.PartitionCode).Distinct(StringComparer.OrdinalIgnoreCase).Count() != catalog.Partitions.Count)
            throw Invalid("版本、设计身份或母线定义无效");
        var mothers = catalog.Mothers.ToDictionary(m => m.LineName, StringComparer.OrdinalIgnoreCase);
        foreach (var child in catalog.Partitions)
        {
            if (child.Lines.Any(l => l is null || string.IsNullOrWhiteSpace(l.LineName)) ||
                child.Lines.Select(l => l.LineName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != child.Lines.Count)
                throw Invalid("同一子区存在重复母线映射");
            foreach (var line in child.Lines)
                if (!mothers.TryGetValue(line.LineName, out var mother) || line.MotherStableId != mother.StableId ||
                    !ValidSegments(line.Segments) || line.Segments.Any(s =>
                        !OnSegments(s.Start.X, s.Start.Y, mother.Segments) || !OnSegments(s.End.X, s.End.Y, mother.Segments)))
                    throw Invalid("子线来源或几何与母线不一致");
        }
    }

    private static bool ValidSegments(IReadOnlyList<SurveyLineSegment>? segments) => segments is { Count: > 0 } &&
        segments.All(s => s is not null && s.Start is not null && s.End is not null &&
            double.IsFinite(s.Start.X) && double.IsFinite(s.Start.Y) && double.IsFinite(s.End.X) &&
            double.IsFinite(s.End.Y) && double.IsFinite(s.Length) && s.Length > 0);

    private static bool OnSegments(double x, double y, IReadOnlyList<SurveyLineSegment> segments) =>
        double.IsFinite(x) && double.IsFinite(y) && segments.Any(s =>
        {
            double dx = s.End.X - s.Start.X, dy = s.End.Y - s.Start.Y;
            double along = Math.Clamp(((x - s.Start.X) * dx + (y - s.Start.Y) * dy) / (dx * dx + dy * dy), 0, 1);
            double ex = x - s.Start.X - along * dx, ey = y - s.Start.Y - along * dy;
            return ex * ex + ey * ey <= BindingToleranceM * BindingToleranceM;
        });

    private static InvalidDataException Invalid(string detail) => new("分区母线身份校验失败：" + detail + "。请核对分区来源并重新生成；未推断或合并测线。");
}
