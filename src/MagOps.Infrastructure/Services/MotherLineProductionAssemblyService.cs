using MagOps.Core.Coordinates;

namespace MagOps.Infrastructure.Services;

public sealed record MotherLineProductionObservation(string SourceSha256, UavMagCanonicalSample Sample,
    MagneticSegmentAssignment Assignment, string RecognizedPassId)
{
    public IReadOnlyList<string> ProcessingValues { get; init; } = [];
}

public sealed record MotherLineProductionBatch(string ProjectId, string WorkareaId, string DesignId,
    string SnapshotId, IReadOnlyList<MotherLineProductionObservation> Observations)
{
    public string AuditSnapshotFingerprint { get; init; } = "";
    public bool IsReviewDraft { get; init; }
    public string SourceKind { get; init; } = "NATIVE_PRODUCTION_SNAPSHOT";
    public bool OriginalLineageVerified { get; init; } = true;
}

public sealed record MotherLinePointProvenance(string SnapshotId, string SourceFile, string PartitionCode,
    string SurveyTaskStableId, string ExecutionSegmentStableId, string RecognizedPassId, int? OriginalLinePass);

public sealed record MotherLineProductionPoint(int PointOrder, string SourceSha256,
    UavMagCanonicalSample Sample, double AlongM, IReadOnlyList<MotherLinePointProvenance> Provenance)
{
    public IReadOnlyList<string> ProcessingValues { get; init; } = [];
}

public sealed record MotherLineProductionRun(string SnapshotId, string RecognizedPassId,
    string PartitionCode, string SurveyTaskStableId, int SessionIndex, int? OriginalLinePass,
    bool Reversed, bool DirectionUndetermined,
    double BacktrackingM, IReadOnlyList<MotherLineProductionPoint> Points);

public sealed record MotherLineProductionPointSet(string MotherLineStableId, string LineName, int LineOrder,
    bool IsQualityRepeat, string RepeatPassKey, IReadOnlyList<MotherLineProductionRun> Runs);

public sealed record MotherLineProductionAssembly(string ProjectId, string WorkareaId, string DesignId,
    int InputRecords, int ExcludedRecords, int DuplicateSourceRecords,
    IReadOnlyList<MotherLineProductionObservation> Unresolved,
    IReadOnlyList<MotherLineProductionPointSet> PointSets)
{
    public bool ContainsReviewDraft { get; init; }
    public IReadOnlyList<string> SourceKinds { get; init; } = [];
    public bool OriginalLineageVerified { get; init; } = true;
}

/// <summary>
/// Read-only candidate assembly. Never recognizes, interpolates or changes production selection.
/// Whole temporal runs are reversed; local spatial jitter is not sorted away. Overlapping real
/// observations survive. Only the same content-hash/source-record pair is deduplicated.
/// </summary>
public static class MotherLineProductionAssemblyService
{
    public static MotherLineProductionAssembly Build(string projectId, string workareaId,
        PartitionMotherLineIdentityCatalog catalog, IReadOnlyList<MotherLineProductionBatch> batches)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(batches);
        PartitionMotherLineIdentityService.Validate(catalog);
        Require(!string.IsNullOrWhiteSpace(projectId) && !string.IsNullOrWhiteSpace(workareaId), "缺少项目或工区稳定身份");
        Require(catalog.CentralMeridian is >= -180 and <= 180, "中央经线超出范围");
        // Validate the supported datum even when the input contains no selected points.
        CoordinateService.GaussKrugerForward(catalog.CentralMeridian, 0, catalog.CentralMeridian, catalog.Datum);
        var mothers = catalog.Mothers.ToDictionary(m => m.StableId, StringComparer.Ordinal);
        var axes = catalog.Mothers.ToDictionary(m => m.StableId, Axis, StringComparer.Ordinal);
        var partitions = catalog.Partitions.ToDictionary(p => p.PartitionCode, StringComparer.OrdinalIgnoreCase);
        var snapshots = new HashSet<string>(StringComparer.Ordinal);
        var sourcePoints = new Dictionary<(string Hash, int Record), Candidate>();
        var unresolved = new List<MotherLineProductionObservation>();
        int input = 0, excluded = 0, duplicates = 0;
        foreach (var batch in batches.OrderBy(b => b.SnapshotId, StringComparer.Ordinal))
        {
            Require(batch.ProjectId == projectId && batch.WorkareaId == workareaId && batch.DesignId == catalog.DesignId,
                "跨项目、工区或设计版本的成果不能组合");
            Require(!string.IsNullOrWhiteSpace(batch.SnapshotId) && snapshots.Add(batch.SnapshotId), "快照身份为空或重复");
            foreach (var row in batch.Observations)
            {
                Require(row is not null && row.Sample is not null,
                    "候选缺少点或归属记录");
                input++;
                var a = row.Assignment;
                if (!a.IncludeInProcessing) { excluded++; continue; }
                Require(row.SourceSha256.Length == 64 && row.SourceSha256.All(Uri.IsHexDigit), "来源必须有完整内容 SHA256");
                Require(row.ProcessingValues is not null && (row.ProcessingValues.Count == 0 ||
                    row.ProcessingValues.Count == MagneticPreLevelProcessingService.ProcessingColumns.Count) &&
                    row.ProcessingValues.All(v => v is not null), "处理用 CSV 记录列不完整");
                var s = row.Sample;
                Require(s.Longitude.HasValue && s.Latitude.HasValue && WorkareaSpatialService.ValidCoordinate(s.Longitude.Value, s.Latitude.Value) && s.SourceRecord >= 0 &&
                    s.QualityFlags is not null, "候选坐标、来源记录号或质量标记无效");
                Require(s.SourceFile == a.SourceFile && s.SourceRecord == a.SourceRecord && s.TimestampUtc == a.TimestampUtc &&
                    s.Longitude == a.Longitude && s.Latitude == a.Latitude, "点与分线归属的来源、时间或坐标不一致");
                if (a.SegmentClass != MagneticSegmentClass.SurveyLine || string.IsNullOrWhiteSpace(a.MotherLineStableId))
                {
                    unresolved.Add(row); continue;
                }
                Require(mothers.ContainsKey(a.MotherLineStableId) &&
                    partitions.TryGetValue(a.PartitionCode, out var partition) &&
                    partition.Lines.Any(l => l.MotherStableId == a.MotherLineStableId), "子区与母线身份没有同设计映射");
                Require(!string.IsNullOrWhiteSpace(a.SurveyTaskStableId) &&
                    !string.IsNullOrWhiteSpace(a.ExecutionSegmentStableId) &&
                    !string.IsNullOrWhiteSpace(row.RecognizedPassId), "已归属点缺少任务、执行段或 Pass 身份");
                var xy = CoordinateService.GaussKrugerForward(a.Longitude, a.Latitude, catalog.CentralMeridian, catalog.Datum);
                var axis = axes[a.MotherLineStableId];
                double along = (xy.Easting - axis.X) * axis.Dx + (xy.Northing - axis.Y) * axis.Dy;
                Require(double.IsFinite(along), "点的投影位置无效");
                string hash = row.SourceSha256.ToUpperInvariant();
                var provenance = new MotherLinePointProvenance(batch.SnapshotId, s.SourceFile, a.PartitionCode,
                    a.SurveyTaskStableId, a.ExecutionSegmentStableId, row.RecognizedPassId, a.LinePass);
                var key = (hash, s.SourceRecord);
                if (sourcePoints.TryGetValue(key, out var existing))
                {
                    Require(existing.Row.Assignment.MotherLineStableId == a.MotherLineStableId &&
                        existing.Row.Assignment.IsQualityRepeat == a.IsQualityRepeat && SameSample(existing.Row.Sample, s) &&
                        existing.Row.Assignment.LinePass == a.LinePass &&
                        existing.Row.RecognizedPassId == row.RecognizedPassId,
                        "相同来源记录具有冲突的观测值、母线、Pass或重复线用途");
                    Require(existing.Row.ProcessingValues.Count == row.ProcessingValues.Count &&
                        existing.Row.ProcessingValues.SequenceEqual(row.ProcessingValues),
                        "相同来源记录的改正/投影/IGRF/T0成果不一致，不能静默去重");
                    existing.Provenance.Add(provenance); duplicates++; continue;
                }
                sourcePoints.Add(key, new(row, hash, along, [provenance]));
            }
        }
        var sets = sourcePoints.Values.GroupBy(p => (p.Row.Assignment.MotherLineStableId,
            p.Row.Assignment.IsQualityRepeat,
            RepeatKey: p.Row.Assignment.IsQualityRepeat
                ? System.Text.Json.JsonSerializer.Serialize(new[] { p.Provenance[0].SnapshotId, p.Row.RecognizedPassId }) : ""))
            .OrderBy(g => mothers[g.Key.MotherLineStableId].Order)
            .ThenBy(g => g.Key.IsQualityRepeat).ThenBy(g => g.Key.RepeatKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var mother = mothers[group.Key.MotherLineStableId];
                int order = 0;
                var runs = group.GroupBy(p => (p.Provenance[0].SnapshotId, p.Row.RecognizedPassId,
                        p.Row.Assignment.PartitionCode, p.Row.Assignment.SurveyTaskStableId,
                        p.Row.Assignment.SessionIndex, p.Row.Assignment.LinePass))
                    .Select(g =>
                    {
                        var points = g.OrderBy(p => p.Row.Sample.TimestampUtc)
                            .ThenBy(p => p.Hash, StringComparer.Ordinal).ThenBy(p => p.Row.Sample.SourceRecord).ToArray();
                        double span = points[^1].Along - points[0].Along;
                        bool reversed = span < 0;
                        if (reversed) Array.Reverse(points);
                        double backtracking = points.Zip(points.Skip(1), (a, b) => Math.Max(0, a.Along - b.Along)).Sum();
                        return (g.Key, Points: points, Reversed: reversed, Undetermined: Math.Abs(span) < .001, Backtracking: backtracking);
                    }).OrderBy(r => r.Points.Min(p => p.Along))
                    .ThenBy(r => r.Key.SnapshotId, StringComparer.Ordinal)
                    .ThenBy(r => r.Key.RecognizedPassId, StringComparer.Ordinal)
                    .ThenBy(r => r.Key.PartitionCode, StringComparer.Ordinal)
                    .ThenBy(r => r.Key.SurveyTaskStableId, StringComparer.Ordinal)
                    .ThenBy(r => r.Key.SessionIndex).ThenBy(r => r.Key.LinePass)
                    .Select(r => new MotherLineProductionRun(r.Key.SnapshotId, r.Key.RecognizedPassId,
                        r.Key.PartitionCode, r.Key.SurveyTaskStableId, r.Key.SessionIndex, r.Key.LinePass,
                        r.Reversed, r.Undetermined, r.Backtracking,
                        r.Points.Select(p => new MotherLineProductionPoint(++order, p.Hash, p.Row.Sample, p.Along,
                            p.Provenance.OrderBy(x => x.SnapshotId, StringComparer.Ordinal)
                                .ThenBy(x => x.PartitionCode, StringComparer.Ordinal).ToArray())
                            { ProcessingValues = p.Row.ProcessingValues }).ToArray())).ToArray();
                return new MotherLineProductionPointSet(mother.StableId, mother.LineName, mother.Order,
                    group.Key.IsQualityRepeat, group.Key.RepeatKey, runs);
            }).ToArray();
        return new(projectId, workareaId, catalog.DesignId, input, excluded, duplicates, unresolved.ToArray(), sets)
        { ContainsReviewDraft = batches.Any(b => b.IsReviewDraft),
            SourceKinds = batches.Select(b => b.SourceKind).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            OriginalLineageVerified = batches.All(b => b.OriginalLineageVerified) };
    }

    private sealed record Candidate(MotherLineProductionObservation Row, string Hash, double Along,
        List<MotherLinePointProvenance> Provenance);

    private static (double X, double Y, double Dx, double Dy) Axis(PartitionMotherLineDefinition mother)
    {
        var first = mother.Segments[0];
        double dx = (first.End.X - first.Start.X) / first.Length, dy = (first.End.Y - first.Start.Y) / first.Length;
        foreach (var point in mother.Segments.SelectMany(s => new[] { s.Start, s.End }))
            Require(Math.Abs((point.X - first.Start.X) * dy - (point.Y - first.Start.Y) * dx) <= .001,
                "母线不是共线设计，不能自动推断统一方向");
        return (first.Start.X, first.Start.Y, dx, dy);
    }

    private static bool SameSample(UavMagCanonicalSample a, UavMagCanonicalSample b) =>
        a with { SourceFile = "", QualityFlags = b.QualityFlags } == b with { SourceFile = "" } &&
        a.QualityFlags.SequenceEqual(b.QualityFlags);

    private static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool valid, string message)
    {
        if (!valid) throw new InvalidDataException("母线点集候选生成失败：" + message + "。正式成果未改动。");
    }
}
