namespace MagOps.Core.Routes;

/// <summary>Design-only check catalog, deliberately separate from ordinary production tasks and geometric cuts.</summary>
public sealed record RouteCheckLine(string Id, string Kind, string Name, string SourceTaskId,
    string SourceLineId, string SourceGeometryFingerprint, RouteEndpoint Start, RouteEndpoint End)
{
    public string CoordinateMode { get; init; } = "";
    public double CentralMeridian { get; init; }
}

public sealed record RouteDesignBasis(double GridAzimuthDegrees, string Source, double? DailyCapacityKm = null);

public static class RouteCheckLines
{
    public const string Repeat = "REPEAT_CHECK";
    public const string Cross = "CROSS_CHECK";

    public static void Validate(IReadOnlyList<RouteCheckLine> lines)
    {
        if (lines is null || lines.Any(l => l is null)) throw new ArgumentException("检查线清单不能为 null。");
        if (lines.Select(l => l.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != lines.Count)
            throw new ArgumentException("检查线身份重复。");
        foreach (var line in lines)
            if (string.IsNullOrWhiteSpace(line.Id) || string.IsNullOrWhiteSpace(line.Name) ||
                line.Kind is not (Repeat or Cross) || line.CoordinateMode is not ("projected" or "geographic") ||
                !double.IsFinite(line.CentralMeridian) || Math.Abs(line.CentralMeridian) > 180 || line.Start is null || line.End is null ||
                !double.IsFinite(line.Start.X) || !double.IsFinite(line.Start.Y) || !double.IsFinite(line.End.X) || !double.IsFinite(line.End.Y) ||
                line.Start.X == line.End.X && line.Start.Y == line.End.Y ||
                line.CoordinateMode == "geographic" && (Math.Abs(line.Start.X) > 180 || Math.Abs(line.End.X) > 180 || Math.Abs(line.Start.Y) > 90 || Math.Abs(line.End.Y) > 90) ||
                line.Kind == Repeat && (string.IsNullOrWhiteSpace(line.SourceTaskId) || string.IsNullOrWhiteSpace(line.SourceLineId) || string.IsNullOrWhiteSpace(line.SourceGeometryFingerprint)) ||
                line.Kind == Cross && (!string.IsNullOrEmpty(line.SourceTaskId) || !string.IsNullOrEmpty(line.SourceLineId)))
                throw new ArgumentException("检查线类型、关联身份或端点无效。");
    }
}
