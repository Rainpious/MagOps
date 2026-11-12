using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MagOps.Core.Routes;
using MagOps.Core.SurveyPlanning;

namespace MagOps.Core.Analysis;

public static class ConcaveLineActions
{
    public const string Connect = "CONNECT";
    public const string Split = "SPLIT";
}

public sealed class ConcaveLineRule
{
    public string SourceLine { get; set; } = "";
    public string Action { get; set; } = "";
    public string SecondOutputLine { get; set; } = "";
    public string Notes { get; set; } = "";
    public string GeometryFingerprint { get; set; } = "";

    public ConcaveLineRule Clone() => new()
    {
        SourceLine = SourceLine,
        Action = Action,
        SecondOutputLine = SecondOutputLine,
        Notes = Notes,
        GeometryFingerprint = GeometryFingerprint
    };
}

public sealed record ConcaveLineInspectionItem(
    string SourceLine,
    int SourceLineNumber,
    int SegmentCount,
    IReadOnlyList<int> SegmentPointCounts,
    double GapDistance,
    string GapUnit,
    bool SegmentsInferred,
    int FirstSourceRow,
    string GeometryFingerprint)
{
    public string SegmentSummary =>
        string.Join(" + ", SegmentPointCounts.Select((count, index) => $"段{index + 1}:{count}点"));
}

public sealed class ConcaveLineInspection
{
    public string SourcePath { get; init; } = "";
    public string[] Headers { get; init; } = [];
    public int LineColumnNumber { get; init; }
    public int XColumnNumber { get; init; }
    public int YColumnNumber { get; init; }
    public int SourceDataRowCount { get; init; }
    public int SourceLineCount { get; init; }
    public int MaxPositiveLineNumber { get; init; }
    public string SourceGeometryFingerprint { get; init; } = "";
    public List<int> UsedLineNumbers { get; init; } = [];
    public List<ConcaveLineInspectionItem> DisconnectedLines { get; init; } = [];
}

public sealed record ConcaveLineProcessMapping(
    string SourceLine,
    string Action,
    string FirstOutputLine,
    string SecondOutputLine,
    int SourceSegments,
    int OutputLines,
    double GapDistance,
    string GapUnit,
    string Message)
{
    public IReadOnlyList<string> OutputLineLabels { get; init; } = [];
}

public sealed class ConcaveLineProcessResult
{
    public string OutputPath { get; init; } = "";
    public int SourceDataRowCount { get; init; }
    public int OutputDataRowCount { get; init; }
    public int SourceLineCount { get; init; }
    public int OutputLineCount { get; init; }
    public int ConnectedLineCount { get; init; }
    public int SplitLineCount { get; init; }
    public List<ConcaveLineProcessMapping> Mappings { get; init; } = [];
}

/// <summary>
/// Preprocesses route endpoint tables clipped by concave workarea boundaries.
/// Blank X/Y rows with the same L number are explicit segment separators.
/// CONNECT keeps the farthest outer endpoint pair. SPLIT keeps every source
/// segment as an independent numeric L line. The first segment keeps the
/// source L number; later segments receive unique numbers. No coordinate is
/// interpolated or invented.
/// </summary>
public sealed class ConcaveBoundaryLineService
{
    private static readonly Regex LineNumberRegex =
        new(@"(?i)L\s*([+-]?\d+)", RegexOptions.Compiled);

    private static readonly HashSet<string> LineHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "line", "lineid", "line_id", "lineno", "line_no", "测线", "线号", "测线号"
    };

    private static readonly HashSet<string> XHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "x", "easting", "east", "lon", "longitude", "经度", "东坐标"
    };

    private static readonly HashSet<string> YHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "y", "northing", "north", "lat", "latitude", "纬度", "北坐标"
    };

    public bool TryInspectConcaveInputBeforeNumericParse(
        string sourcePath,
        out ConcaveLineInspection? inspection)
    {
        inspection = null;
        if (!File.Exists(sourcePath))
            return false;

        var parsed = DelimitedTextReader.ReadRows(sourcePath);
        if (parsed.Rows.Count < 2)
            return false;

        string[] headers = parsed.Rows[0];
        int lineIndex = FindHeader(headers, LineHeaders);
        int xIndex = FindHeader(headers, XHeaders);
        int yIndex = FindHeader(headers, YHeaders);
        if (lineIndex < 0 || xIndex < 0 || yIndex < 0)
            return false;

        bool hasCoordinateBlank = parsed.Rows
            .Skip(1)
            .Any(row =>
            {
                string lineText = GetCell(row, lineIndex).Trim();
                if (lineText.Length == 0)
                    return false;

                string xText = GetCell(row, xIndex).Trim();
                string yText = GetCell(row, yIndex).Trim();
                return xText.Length == 0 || yText.Length == 0;
            });

        if (!hasCoordinateBlank)
            return false;

        // Once a recognizable endpoint table contains blank coordinate cells,
        // validate it with the concave-boundary preprocessor before the generic
        // v25 numeric reader gets a chance to report "X is not a valid number".
        inspection = Inspect(sourcePath);
        return true;
    }

    public ConcaveLineInspection Inspect(string sourcePath)
    {
        ParsedEndpointTable table = ReadEndpointTable(sourcePath);
        List<LineGroup> groups = BuildLineGroups(table);
        var disconnected = groups
            .Where(x => x.Segments.Count > 1)
            .OrderBy(x => x.FirstSourceRow)
            .Select(group =>
            {
                bool geo = IsGeographic(group.Segments.SelectMany(x => x));
                return new ConcaveLineInspectionItem(
                    CanonicalLine(group.LineNumber),
                    group.LineNumber,
                    group.Segments.Count,
                    group.Segments.Select(x => x.Count).ToArray(),
                    MinimumInterSegmentGap(group.Segments),
                    geo ? "m" : "输入坐标单位",
                    group.SegmentsInferred,
                    group.FirstSourceRow,
                    ComputeLineGeometryFingerprint(group));
            })
            .ToList();

        int maxPositive = groups.Select(x => x.LineNumber).Where(x => x >= 0).DefaultIfEmpty(0).Max();
        return new ConcaveLineInspection
        {
            SourcePath = Path.GetFullPath(sourcePath),
            Headers = table.Headers,
            LineColumnNumber = table.LineIndex + 1,
            XColumnNumber = table.XIndex + 1,
            YColumnNumber = table.YIndex + 1,
            SourceDataRowCount = table.Rows.Count,
            SourceLineCount = groups.Count,
            MaxPositiveLineNumber = maxPositive,
            SourceGeometryFingerprint = ComputeSourceGeometryFingerprint(groups),
            UsedLineNumbers = groups.Select(x => x.LineNumber).Distinct().ToList(),
            DisconnectedLines = disconnected
        };
    }

    public ConcaveLineProcessResult Process(
        string sourcePath,
        string outputPath,
        IReadOnlyList<ConcaveLineRule> rules,
        IReadOnlyList<SurveyBoundaryPoint>? allowedBoundary = null,
        string boundaryLabel = "有效工区边界",
        bool allowExplicitOutsideConnections = false)
    {
        ParsedEndpointTable table = ReadEndpointTable(sourcePath);
        List<LineGroup> groups = BuildLineGroups(table);
        List<LineGroup> disconnected = groups.Where(x => x.Segments.Count > 1).ToList();

        var ruleMap = (rules ?? [])
            .Select(NormalizeRule)
            .GroupBy(x => x.SourceLine, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);

        foreach (LineGroup group in disconnected)
        {
            string source = CanonicalLine(group.LineNumber);
            if (!ruleMap.TryGetValue(source, out ConcaveLineRule? rule) || string.IsNullOrWhiteSpace(rule.Action))
                throw new InvalidOperationException($"测线 {source} 存在 {group.Segments.Count} 个断段，但尚未选择‘连接’或‘分割’。");

            string currentGeometryFingerprint =
                ComputeLineGeometryFingerprint(group);
            if (!string.IsNullOrWhiteSpace(rule.GeometryFingerprint) &&
                !string.Equals(
                    rule.GeometryFingerprint,
                    currentGeometryFingerprint,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"测线 {source} 的 CONNECT/SPLIT 规则几何指纹已过期。" +
                    " 当前端点/断口已变化，必须重新确认处理方式。");
            }
        }

        ValidateSplitTargets(groups, disconnected, ruleMap);

        var outputRows = new List<string[]>();
        var mappings = new List<ConcaveLineProcessMapping>();
        var connections = new List<ExplicitRouteConnection>();
        int connected = 0;
        int split = 0;
        var reservedOutputLines = groups
            .Select(group => group.LineNumber)
            .ToHashSet();
        foreach (LineGroup group in disconnected)
        {
            string source = CanonicalLine(group.LineNumber);
            ConcaveLineRule rule = ruleMap[source];
            if (!rule.Action.Equals(ConcaveLineActions.Split, StringComparison.OrdinalIgnoreCase))
                continue;
            int target = ParseExactLineNumber(
                rule.SecondOutputLine,
                $"测线 {source} 的第二段输出线号");
            reservedOutputLines.Add(target);
        }
        int nextAutoLine = reservedOutputLines
            .Where(value => value >= 0)
            .DefaultIfEmpty(0)
            .Max();
        if (nextAutoLine < int.MaxValue)
            nextAutoLine++;

        foreach (LineGroup group in groups.OrderBy(x => x.FirstSourceRow))
        {
            string source = CanonicalLine(group.LineNumber);
            if (group.Segments.Count == 1)
            {
                foreach (EndpointRow point in group.Segments[0])
                    outputRows.Add(CloneWithLine(point.Values, table.LineIndex, point.OriginalLineText));
                continue;
            }

            ConcaveLineRule rule = ruleMap[source];
            if (rule.Action.Equals(ConcaveLineActions.Connect, StringComparison.OrdinalIgnoreCase))
            {
                EndpointRow[] outer = FarthestPair(group.Segments.SelectMany(x => x).ToList());
                if (!allowExplicitOutsideConnections && allowedBoundary is not null &&
                    !RouteBoundarySafetyService.IsSegmentInsideOrOnBoundary(
                        new SurveyBoundaryPoint(outer[0].X, outer[0].Y),
                        new SurveyBoundaryPoint(outer[1].X, outer[1].Y),
                        allowedBoundary))
                {
                    throw new InvalidOperationException(
                        $"测线 {source} 不能使用 CONNECT：最外侧端点直连会穿出{boundaryLabel}。" +
                        " 已拒绝生成跨工区直线，请改用 SPLIT。" );
                }
                foreach (EndpointRow point in outer.OrderBy(x => x.SourceRow))
                    outputRows.Add(CloneWithLine(point.Values, table.LineIndex, source));

                if (allowExplicitOutsideConnections)
                    connections.Add(new ExplicitRouteConnection { Line = source,
                        X1 = outer[0].X, Y1 = outer[0].Y, X2 = outer[1].X, Y2 = outer[1].Y });
                connected++;
                bool geo = IsGeographic(group.Segments.SelectMany(x => x));
                double gap = MinimumInterSegmentGap(group.Segments);
                mappings.Add(new ConcaveLineProcessMapping(
                    source,
                    ConcaveLineActions.Connect,
                    source,
                    "",
                    group.Segments.Count,
                    1,
                    gap,
                    geo ? "m" : "输入坐标单位",
                    $"{source}：{group.Segments.Count} 个断段连接为 1 条测线；保留最外侧两个端点。")
                {
                    OutputLineLabels = [source]
                });
                continue;
            }

            if (!rule.Action.Equals(ConcaveLineActions.Split, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"测线 {source} 的处理方式无效：{rule.Action}");

            string second = CanonicalLine(ParseExactLineNumber(
                rule.SecondOutputLine,
                $"测线 {source} 的第二段输出线号"));

            List<List<EndpointRow>> orderedSegments =
                OrderSplitSegments(group.Segments);
            var outputLabels = new List<string> { source, second };
            for (int segmentIndex = 2; segmentIndex < orderedSegments.Count; segmentIndex++)
            {
                while (reservedOutputLines.Contains(nextAutoLine))
                {
                    if (nextAutoLine == int.MaxValue)
                        throw new InvalidOperationException("无法继续为多断段测线自动分配新的 L 线号。");
                    nextAutoLine++;
                }
                int allocated = nextAutoLine;
                reservedOutputLines.Add(allocated);
                if (nextAutoLine < int.MaxValue)
                    nextAutoLine++;
                outputLabels.Add(CanonicalLine(allocated));
            }

            for (int segmentIndex = 0; segmentIndex < orderedSegments.Count; segmentIndex++)
            {
                string outputLine = outputLabels[segmentIndex];
                foreach (EndpointRow point in orderedSegments[segmentIndex])
                    outputRows.Add(CloneWithLine(point.Values, table.LineIndex, outputLine));
            }

            split++;
            bool splitGeo = IsGeographic(group.Segments.SelectMany(x => x));
            double splitGap = MinimumInterSegmentGap(group.Segments);
            mappings.Add(new ConcaveLineProcessMapping(
                source,
                ConcaveLineActions.Split,
                source,
                second,
                group.Segments.Count,
                outputLabels.Count,
                splitGap,
                splitGeo ? "m" : "输入坐标单位",
                $"{source}：按测线主轴统一断段顺序；" +
                $"{group.Segments.Count} 个断段分别输出为 {string.Join("、", outputLabels)}。")
            {
                OutputLineLabels = outputLabels
            });
        }

        WriteTable(outputPath, table.Headers, outputRows, table.Delimiter);
        ExplicitRouteConnectionStore.Write(outputPath, connections);
        int outputLineCount = outputRows
            .Select(row => TryParseLineNumber(GetCell(row, table.LineIndex), out int line) ? line : int.MinValue)
            .Where(x => x != int.MinValue)
            .Distinct()
            .Count();

        return new ConcaveLineProcessResult
        {
            OutputPath = Path.GetFullPath(outputPath),
            SourceDataRowCount = table.Rows.Count,
            OutputDataRowCount = outputRows.Count,
            SourceLineCount = groups.Count,
            OutputLineCount = outputLineCount,
            ConnectedLineCount = connected,
            SplitLineCount = split,
            Mappings = mappings
        };
    }

    public static int ParseExactLineNumber(string value, string label = "测线号")
    {
        string text = (value ?? "").Trim();
        Match match = Regex.Match(text, @"(?i)^L\s*([+-]?\d+)\s*$");
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
        {
            throw new FormatException($"{label} 必须是 L 加整数，例如 L8、L77。当前值：{value}");
        }
        return number;
    }

    public static string CanonicalLine(int lineNumber) =>
        $"L{lineNumber.ToString(CultureInfo.InvariantCulture)}";

    private static ParsedEndpointTable ReadEndpointTable(string sourcePath)
    {
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("测线端点表不存在。", sourcePath);

        var parsed = DelimitedTextReader.ReadRows(sourcePath);
        if (parsed.Rows.Count < 2)
            throw new InvalidDataException("测线断段处理需要包含表头和至少一行端点数据。");

        string[] headers = parsed.Rows[0];
        int lineIndex = FindHeader(headers, LineHeaders);
        int xIndex = FindHeader(headers, XHeaders);
        int yIndex = FindHeader(headers, YHeaders);
        if (lineIndex < 0 || xIndex < 0 || yIndex < 0)
        {
            throw new InvalidDataException(
                "测线断段处理需要带表头的端点表，并能识别 Line/X/Y 字段。支持示例：Line,X,Y；线号,东坐标,北坐标。");
        }
        if (new[] { lineIndex, xIndex, yIndex }.Distinct().Count() != 3)
            throw new InvalidDataException("Line、X、Y 必须是三个不同字段。");

        var rows = parsed.Rows.Skip(1)
            .Select((row, index) => new InputRow(index + 2, NormalizeRow(row, headers.Length)))
            .ToList();
        return new ParsedEndpointTable(headers, rows, parsed.Delimiter, lineIndex, xIndex, yIndex);
    }

    private static List<LineGroup> BuildLineGroups(ParsedEndpointTable table)
    {
        var byLine = new Dictionary<int, List<InputRow>>();
        var firstRows = new Dictionary<int, int>();

        foreach (InputRow row in table.Rows)
        {
            string lineText = GetCell(row.Values, table.LineIndex).Trim();
            string xText = GetCell(row.Values, table.XIndex).Trim();
            string yText = GetCell(row.Values, table.YIndex).Trim();

            if (lineText.Length == 0)
            {
                if (xText.Length == 0 && yText.Length == 0) continue;
                throw new InvalidDataException($"第 {row.RowNumber} 行存在坐标但没有测线号。");
            }
            if (!TryParseLineNumber(lineText, out int lineNumber))
            {
                throw new InvalidDataException(
                    $"第 {row.RowNumber} 行无法从‘{lineText}’识别 L 整数线号。本功能需要与 v25 配对兼容的 L 线号。");
            }

            if (!byLine.TryGetValue(lineNumber, out List<InputRow>? list))
            {
                byLine[lineNumber] = list = [];
                firstRows[lineNumber] = row.RowNumber;
            }
            list.Add(row);
        }

        var groups = new List<LineGroup>();
        foreach (var pair in byLine.OrderBy(x => firstRows[x.Key]))
        {
            int lineNumber = pair.Key;
            var segments = new List<List<EndpointRow>>();
            var current = new List<EndpointRow>();
            bool explicitBreak = false;

            void CloseCurrent()
            {
                if (current.Count == 0) return;
                segments.Add(current);
                current = [];
            }

            foreach (InputRow sourceRow in pair.Value)
            {
                string xText = GetCell(sourceRow.Values, table.XIndex).Trim();
                string yText = GetCell(sourceRow.Values, table.YIndex).Trim();
                if (xText.Length == 0 && yText.Length == 0)
                {
                    explicitBreak = true;
                    CloseCurrent();
                    continue;
                }
                if (xText.Length == 0 || yText.Length == 0)
                {
                    throw new InvalidDataException(
                        $"第 {sourceRow.RowNumber} 行的 X/Y 只有一个为空。断段分隔行必须同时为空；真实端点必须同时有 X/Y。");
                }

                double x = DelimitedTextReader.ParseNumber(xText, $"第 {sourceRow.RowNumber} 行 X");
                double y = DelimitedTextReader.ParseNumber(yText, $"第 {sourceRow.RowNumber} 行 Y");
                current.Add(new EndpointRow(
                    sourceRow.RowNumber,
                    sourceRow.Values,
                    GetCell(sourceRow.Values, table.LineIndex),
                    x,
                    y));
            }
            CloseCurrent();

            bool inferred = false;
            if (segments.Count == 1 && segments[0].Count > 2)
            {
                List<EndpointRow> flat = segments[0];
                if (flat.Count % 2 != 0)
                {
                    throw new InvalidDataException(
                        $"{CanonicalLine(lineNumber)} 有 {flat.Count} 个连续有效端点且没有空 X/Y 分隔行；端点数量为奇数，无法可靠推断断段。");
                }
                segments = flat.Chunk(2).Select(chunk => chunk.ToList()).ToList();
                inferred = true;
            }

            if (segments.Count == 0) continue;
            foreach (List<EndpointRow> segment in segments)
            {
                if (segment.Count != 2)
                {
                    string origin = explicitBreak ? "空 X/Y 分隔" : "连续端点";
                    throw new InvalidDataException(
                        $"{CanonicalLine(lineNumber)} 的一个断段包含 {segment.Count} 个有效端点。航线端点表中每个断段必须恰好 2 个端点（来源：{origin}）。");
                }
            }

            groups.Add(new LineGroup(lineNumber, firstRows[lineNumber], segments, inferred));
        }

        if (groups.Count == 0)
            throw new InvalidDataException("端点表中没有可处理的 L 测线。");
        return groups;
    }

    private static void ValidateSplitTargets(
        IReadOnlyList<LineGroup> allGroups,
        IReadOnlyList<LineGroup> disconnected,
        IReadOnlyDictionary<string, ConcaveLineRule> ruleMap)
    {
        var used = allGroups.Select(x => x.LineNumber).ToHashSet();
        foreach (LineGroup group in disconnected.OrderBy(x => x.FirstSourceRow))
        {
            string source = CanonicalLine(group.LineNumber);
            ConcaveLineRule rule = ruleMap[source];
            if (!rule.Action.Equals(ConcaveLineActions.Split, StringComparison.OrdinalIgnoreCase)) continue;

            int target = ParseExactLineNumber(rule.SecondOutputLine, $"测线 {source} 的第二段输出线号");
            if (!used.Add(target))
            {
                throw new InvalidOperationException(
                    $"测线 {source} 的第二段输出线号 {CanonicalLine(target)} 与原测线或其它分割结果冲突。请重新分配分割编号。");
            }
        }
    }

    private static ConcaveLineRule NormalizeRule(ConcaveLineRule input)
    {
        int sourceNumber = ParseExactLineNumber(input.SourceLine, "规则源测线号");
        string action = (input.Action ?? "").Trim().ToUpperInvariant();
        if (!action.Equals(ConcaveLineActions.Connect, StringComparison.OrdinalIgnoreCase) &&
            !action.Equals(ConcaveLineActions.Split, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"测线 {CanonicalLine(sourceNumber)} 的规则类型必须是 CONNECT 或 SPLIT。");

        return new ConcaveLineRule
        {
            SourceLine = CanonicalLine(sourceNumber),
            Action = action,
            SecondOutputLine = (input.SecondOutputLine ?? "").Trim(),
            Notes = (input.Notes ?? "").Trim(),
            GeometryFingerprint = (input.GeometryFingerprint ?? "").Trim()
        };
    }

    private static EndpointRow[] FarthestPair(IReadOnlyList<EndpointRow> points)
    {
        if (points.Count < 2) throw new InvalidOperationException("至少需要两个端点才能连接测线。");
        bool geographic = IsGeographic(points);
        EndpointRow? bestA = null;
        EndpointRow? bestB = null;
        double best = double.NegativeInfinity;
        for (int i = 0; i < points.Count; i++)
        {
            for (int j = i + 1; j < points.Count; j++)
            {
                double distance = Distance(points[i], points[j], geographic);
                if (distance <= best) continue;
                best = distance;
                bestA = points[i];
                bestB = points[j];
            }
        }
        return [
            bestA ?? throw new InvalidOperationException("无法确定连接后的首端点。"),
            bestB ?? throw new InvalidOperationException("无法确定连接后的末端点。")
        ];
    }

    private static List<List<EndpointRow>> OrderSplitSegments(
        IReadOnlyList<List<EndpointRow>> segments)
    {
        var result = segments
            .Select(segment => segment.ToList())
            .ToList();

        if (result.Count < 2)
            return result;

        EndpointRow[] all = result
            .SelectMany(segment => segment)
            .ToArray();

        double minX = all.Min(point => point.X);
        double maxX = all.Max(point => point.X);
        double minY = all.Min(point => point.Y);
        double maxY = all.Max(point => point.Y);

        bool orderByX =
            Math.Abs(maxX - minX) >=
            Math.Abs(maxY - minY);

        return result
            .OrderBy(segment =>
                orderByX
                    ? segment.Average(point => point.X)
                    : segment.Average(point => point.Y))
            .ThenBy(segment =>
                orderByX
                    ? segment.Average(point => point.Y)
                    : segment.Average(point => point.X))
            .ToList();
    }

    private static double MinimumInterSegmentGap(IReadOnlyList<List<EndpointRow>> segments)
    {
        if (segments.Count < 2) return 0.0;
        bool geographic = IsGeographic(segments.SelectMany(x => x));
        double best = double.PositiveInfinity;
        for (int s = 0; s < segments.Count - 1; s++)
        {
            foreach (EndpointRow a in segments[s])
            foreach (EndpointRow b in segments[s + 1])
                best = Math.Min(best, Distance(a, b, geographic));
        }
        return double.IsFinite(best) ? best : 0.0;
    }

    private static bool IsGeographic(IEnumerable<EndpointRow> points)
    {
        EndpointRow[] rows = points.ToArray();
        return rows.Length > 0 && rows.All(x => x.X is >= -180 and <= 180 && x.Y is >= -90 and <= 90);
    }

    private static double Distance(EndpointRow a, EndpointRow b, bool geographic)
    {
        if (geographic) return RouteCoordinateMath.Haversine(a.X, a.Y, b.X, b.Y);
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static string ComputeSourceGeometryFingerprint(
        IReadOnlyList<LineGroup> groups)
    {
        var builder = new StringBuilder();
        foreach (LineGroup group in groups.OrderBy(group => group.LineNumber))
        {
            builder.Append(CanonicalLine(group.LineNumber));
            builder.Append('|');
            builder.Append(ComputeLineGeometryCanonicalText(group));
            builder.Append('\n');
        }
        return Sha256Text(builder.ToString());
    }

    private static string ComputeLineGeometryFingerprint(LineGroup group) =>
        Sha256Text(ComputeLineGeometryCanonicalText(group));

    private static string ComputeLineGeometryCanonicalText(LineGroup group)
    {
        string[] segments = group.Segments
            .Select(segment =>
            {
                string[] endpoints = segment
                    .Select(point =>
                        point.X.ToString("R", CultureInfo.InvariantCulture) + "," +
                        point.Y.ToString("R", CultureInfo.InvariantCulture))
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToArray();
                return string.Join(";", endpoints);
            })
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        return CanonicalLine(group.LineNumber) + ":" + string.Join("/", segments);
    }

    private static string Sha256Text(string value)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static bool TryParseLineNumber(string text, out int lineNumber)
    {
        lineNumber = 0;
        Match match = LineNumberRegex.Match(text ?? "");
        if (!match.Success)
            return false;

        return int.TryParse(
            match.Groups[1].Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out lineNumber);
    }

    private static int FindHeader(IReadOnlyList<string> headers, HashSet<string> aliases)
    {
        for (int i = 0; i < headers.Count; i++)
        {
            string value = (headers[i] ?? "").Trim().Replace(" ", "").ToLowerInvariant();
            if (aliases.Contains(value)) return i;
        }
        return -1;
    }

    private static string[] NormalizeRow(string[] row, int columnCount)
    {
        if (row.Length >= columnCount) return row.ToArray();
        var result = new string[columnCount];
        Array.Copy(row, result, row.Length);
        for (int i = row.Length; i < result.Length; i++) result[i] = "";
        return result;
    }

    private static string[] CloneWithLine(string[] values, int lineIndex, string line)
    {
        string[] clone = values.ToArray();
        clone[lineIndex] = line;
        return clone;
    }

    private static string GetCell(string[] row, int index) =>
        index >= 0 && index < row.Length ? row[index] ?? "" : "";

    private static void WriteTable(string path, string[] headers, IEnumerable<string[]> rows, char delimiter)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var writer = new StreamWriter(full, false, new UTF8Encoding(true));
        writer.WriteLine(string.Join(delimiter, headers.Select(x => Escape(x, delimiter))));
        foreach (string[] row in rows)
            writer.WriteLine(string.Join(delimiter, row.Select(x => Escape(x, delimiter))));
    }

    private static string Escape(string value, char delimiter)
    {
        string text = value ?? "";
        if (text.Contains('"')) text = text.Replace("\"", "\"\"");
        return text.IndexOfAny([delimiter, '"', '\r', '\n']) >= 0 ? "\"" + text + "\"" : text;
    }

    private sealed record ParsedEndpointTable(
        string[] Headers,
        List<InputRow> Rows,
        char Delimiter,
        int LineIndex,
        int XIndex,
        int YIndex);

    private sealed record InputRow(int RowNumber, string[] Values);

    private sealed record EndpointRow(
        int SourceRow,
        string[] Values,
        string OriginalLineText,
        double X,
        double Y);

    private sealed record LineGroup(
        int LineNumber,
        int FirstSourceRow,
        List<List<EndpointRow>> Segments,
        bool SegmentsInferred);
}
