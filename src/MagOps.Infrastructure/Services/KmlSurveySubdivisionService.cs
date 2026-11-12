using System.Text.Json;
using MagOps.Core.Coordinates;
using MagOps.Core.SurveyPlanning;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

/// <summary>
/// Converts one KML Polygon to a projected mother survey-line plan, then
/// creates child blocks by clipping that mother plan. Child line geometry is
/// never independently re-planned, so all blocks remain aligned.
/// </summary>
public sealed class KmlSurveySubdivisionService
{
    private readonly SurveyLinePlanner _surveyPlanner = new();
    private readonly SurveyLineSubdivisionPlanner _subdivisionPlanner = new();
    private readonly PlySurveyLineService _endpointWriter = new();

    public KmlSurveySubdivisionPreparation Prepare(
        string sourceKml,
        double? centralMeridian,
        string datum,
        SurveyLinePlanOptions lineOptions,
        double maxSortieKm)
    {
        IReadOnlyList<SpatialPolygon> polygons = ReadRegionPolygons(sourceKml);
        IReadOnlyList<GeoCoordinate> allOuter = polygons
            .SelectMany(x => x.OuterRing).ToArray();
        double cm = centralMeridian ?? InferCentralMeridian(allOuter);
        SurveyRegionGeometry region = ProjectRegion(polygons, cm, datum);

        SurveyLinePlanResult mother = _surveyPlanner.Plan(
            region,
            lineOptions);
        if (mother.Lines.Count == 0)
            throw new InvalidOperationException(
                "当前 KML/航线参数没有生成有效母区测线。");

        int suggested = _subdivisionPlanner.SuggestSubareaCount(
            mother,
            maxSortieKm);

        return new KmlSurveySubdivisionPreparation(
            Path.GetFullPath(sourceKml),
            RegionDisplayName(polygons),
            cm,
            datum,
            mother,
            suggested);
    }

    public IReadOnlyList<SurveySubdivisionCutLine> CreateEqualCuts(
        KmlSurveySubdivisionPreparation preparation,
        SurveySubdivisionAxis axis,
        int subareaCount)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        return _subdivisionPlanner.CreateEqualCuts(
            preparation.MotherPlan,
            axis,
            subareaCount);
    }

    public SurveySubdivisionOperationPlan CreatePinTemplate(
        KmlSurveySubdivisionPreparation preparation,
        SurveySubdivisionTemplate template,
        double primaryRatio = 0.5,
        double secondaryRatio = 0.5)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        return _subdivisionPlanner.CreatePinTemplate(preparation.MotherPlan, template, primaryRatio, secondaryRatio);
    }

    public KmlSurveySubdivisionBundle WriteSubdivision(
        KmlSurveySubdivisionPreparation preparation,
        int subareaCount,
        string outputDirectory)
    {
        IReadOnlyList<SurveySubdivisionCutLine> cuts = CreateEqualCuts(
            preparation,
            SurveySubdivisionAxis.AlongSurveyLine,
            subareaCount);
        return WriteSubdivision(
            preparation,
            cuts,
            SurveySubdivisionAxis.AlongSurveyLine,
            outputDirectory);
    }

    public KmlSurveySubdivisionBundle WriteSubdivision(
        KmlSurveySubdivisionPreparation preparation,
        IReadOnlyList<SurveySubdivisionCutLine> cuts,
        SurveySubdivisionAxis axis,
        string outputDirectory,
        double overlapLengthM = 0)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        string output = Path.GetFullPath(outputDirectory);

        SurveyLineSubdivisionResult split = _subdivisionPlanner.Subdivide(
            preparation.MotherPlan,
            cuts,
            axis,
            overlapLengthM);
        return WriteAtomically(preparation, split, output, axis, null);
    }

    public KmlSurveySubdivisionBundle WriteSubdivision(
        KmlSurveySubdivisionPreparation preparation,
        SurveySubdivisionOperationPlan operationPlan,
        string outputDirectory,
        double overlapLengthM = 0)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(operationPlan);
        string output = Path.GetFullPath(outputDirectory);
        SurveyLineSubdivisionResult split = _subdivisionPlanner.SubdivideByOperations(preparation.MotherPlan, operationPlan, overlapLengthM);
        return WriteAtomically(preparation, split, output, SurveySubdivisionAxis.Custom, operationPlan);
    }

    private KmlSurveySubdivisionBundle WriteAtomically(
        KmlSurveySubdivisionPreparation preparation,
        SurveyLineSubdivisionResult split,
        string output,
        SurveySubdivisionAxis axis,
        SurveySubdivisionOperationPlan? operationPlan)
    {
        KmlSurveySubdivisionBundle staged = AtomicDirectoryBundle.Execute(
            output,
            stage => WriteSubdivisionResult(preparation, split, stage, axis, operationPlan),
            (stage, bundle) =>
            {
                if (!File.Exists(bundle.ManifestPath) ||
                    bundle.Children.Count != split.Subareas.Count ||
                    bundle.OutputPaths.Any(path => !File.Exists(path)))
                    throw new InvalidDataException("分区输出未通过文件完整性校验。");
                using var manifest = JsonDocument.Parse(File.ReadAllText(bundle.ManifestPath));
                if (manifest.RootElement.GetProperty("subarea_count").GetInt32() != split.Subareas.Count)
                    throw new InvalidDataException("分区清单与生成结果数量不一致。");
            },
            preserveUnrelatedFiles: true);
        string MovePath(string path) => Path.Combine(output, Path.GetRelativePath(staged.OutputDirectory, path));
        return staged with
        {
            OutputDirectory = output,
            ManifestPath = MovePath(staged.ManifestPath),
            MotherEndpointCsvPath = MovePath(staged.MotherEndpointCsvPath),
            ChildEndpointCsvPaths = staged.ChildEndpointCsvPaths.Select(MovePath).ToArray(),
            OutputPaths = staged.OutputPaths.Select(MovePath).ToArray(),
            Children = staged.Children.Select(child => child with
            {
                KmlPath = MovePath(child.KmlPath),
                EndpointCsvPath = MovePath(child.EndpointCsvPath)
            }).ToArray()
        };
    }

    private KmlSurveySubdivisionBundle WriteSubdivisionResult(
        KmlSurveySubdivisionPreparation preparation,
        SurveyLineSubdivisionResult split,
        string output,
        SurveySubdivisionAxis axis,
        SurveySubdivisionOperationPlan? operationPlan)
    {
        int subareaCount = split.Subareas.Count;

        string stem = CompactSubdivisionStem(preparation.SourceKml);
        DeletePreviousBundleFiles(output, stem);
        var outputPaths = new List<string>();
        var endpointCsvPaths = new List<string>();
        var childFiles = new List<KmlSurveySubdivisionChildFile>();

        string motherEndpoints = Path.Combine(
            output,
            "survey_lines.csv");
        _endpointWriter.WriteEndpointCsv(
            motherEndpoints,
            preparation.MotherPlan);
        outputPaths.Add(motherEndpoints);

        foreach (SurveyLineSubdivisionBlock block in split.Subareas)
        {
            string partitionCode = $"P{block.Index:D2}";
            string partitionDirectory = Path.Combine(output, partitionCode);
            Directory.CreateDirectory(partitionDirectory);
            string kmlPath = Path.Combine(
                partitionDirectory,
                partitionCode + ".kml");
            string csvPath = Path.Combine(
                partitionDirectory,
                "lines.csv");

            List<GeoCoordinate> childLonLat = block.Boundary
                .Select(point =>
                {
                    var ll = CoordinateService.GaussKrugerInverse(
                        point.X,
                        point.Y,
                        preparation.CentralMeridian,
                        preparation.Datum);
                    return new GeoCoordinate(ll.Longitude, ll.Latitude);
                })
                .ToList();

            if (childLonLat.Count < 3)
            {
                throw new InvalidDataException(
                    $"子区 {block.Index:D2} 没有形成有效 KML Polygon。" +
                    "请检查母区 KML 是否为简单闭合边界，或减少子区数量。");
            }

            if (childLonLat[0] != childLonLat[^1])
                childLonLat.Add(childLonLat[0]);

            SpatialGeometryService.WriteKml(
                kmlPath,
                [new SpatialGeometry
                {
                    Name = partitionCode,
                    Type = "Polygon",
                    Coordinates = childLonLat,
                    Closed = true
                }]);

            var childPlan = new SurveyLinePlanResult
            {
                Boundary = block.Boundary,
                Origin = preparation.MotherPlan.Origin,
                Options = preparation.MotherPlan.Options,
                Lines = block.Lines,
                CandidateLineCount = preparation.MotherPlan.CandidateLineCount,
                TangentOnlyLineCount = preparation.MotherPlan.TangentOnlyLineCount,
                CoverageExtensionLineCount = block.Lines.Count(line =>
                    line.IsCoverageExtension)
            };
            _endpointWriter.WriteEndpointCsv(csvPath, childPlan);

            outputPaths.Add(kmlPath);
            outputPaths.Add(csvPath);
            endpointCsvPaths.Add(csvPath);
            childFiles.Add(new KmlSurveySubdivisionChildFile(
                block.Index,
                partitionCode,
                kmlPath,
                csvPath,
                block.Lines.Count,
                block.Lines.Sum(line => line.Segments.Count),
                block.MaxSegmentLengthM));
        }

        string manifest = Path.Combine(
            output,
            "partition_manifest.json");
        var payload = new
        {
            schema_version = operationPlan is null ? "magops-kml-route-subdivision-2" : "magops-kml-route-subdivision-3",
            mother_line_identity = PartitionMotherLineIdentityService.Create(preparation.MotherPlan, split,
                preparation.CentralMeridian, preparation.Datum),
            partition_model = operationPlan is null ? "global-cuts" : "operation-tree",
            template_name = operationPlan?.TemplateName ?? "",
            operations = operationPlan?.Operations.Select(op => new { id = op.OperationId, target_region = op.TargetRegionId, start_x = op.Cut.Start.X, start_y = op.Cut.Start.Y, end_x = op.Cut.End.X, end_y = op.Cut.End.Y }),
            created_at = DateTimeOffset.Now,
            source_kml = preparation.SourceKml,
            source_name = preparation.SourceName,
            central_meridian = preparation.CentralMeridian,
            datum = preparation.Datum,
            subarea_count = subareaCount,
            suggested_subarea_count = preparation.SuggestedSubareaCount,
            split_axis = AxisManifestName(axis),
            survey_line_overlap_enabled = split.OverlapLengthM > 0,
            survey_line_overlap_length_m = split.OverlapLengthM,
            survey_line_overlap_rule = "total overlap along mother survey line; half on each side of internal cuts; clipped to mother boundary; child polygons are effective flight boundaries",
            cut_lines = split.Cuts.Select((cut, index) => new
            {
                index = index + 1,
                start_x = cut.Start.X,
                start_y = cut.Start.Y,
                end_x = cut.End.X,
                end_y = cut.End.Y
            }),
            derivation_rule = "mother survey lines are numbered once; child partitions only clip those lines and keep the same LineName",
            numbering_rule = "south/west -> north/east; polygon vertex order does not affect numbering",
            snake_completion_status = "PENDING_MODIFICATION",
            snake_completion_enable_condition = "enable only after passable-road / field-access calculation is implemented and accepted",
            snake_completion_rule = "frozen: no extra survey line is added outside the workarea merely to make the snake count even",
            line_parameters = new
            {
                spacing_m = preparation.MotherPlan.Options.SpacingM,
                azimuth_degrees = preparation.MotherPlan.Options.AzimuthDegrees,
                initial_line_number = preparation.MotherPlan.Options.InitialLineNumber,
                line_number_increment = preparation.MotherPlan.Options.LineNumberIncrement,
                extend_for_even_snake_completion = preparation.MotherPlan.Options.ExtendForEvenSnakeCompletion
            },
            mother = new
            {
                endpoint_csv = RelativeManifestPath(output, motherEndpoints),
                line_count = preparation.MotherPlan.Lines.Count,
                coverage_extension_line_count = preparation.MotherPlan.CoverageExtensionLineCount,
                segment_count = preparation.MotherPlan.SegmentCount,
                max_segment_length_m = preparation.MotherPlan.Lines
                    .SelectMany(line => line.Segments)
                    .Select(segment => segment.Length)
                    .DefaultIfEmpty(0d)
                    .Max()
            },
            children = childFiles.Select(child => new
            {
                index = child.Index,
                partition_code = child.PartitionCode,
                kml = RelativeManifestPath(output, child.KmlPath),
                endpoint_csv = RelativeManifestPath(output, child.EndpointCsvPath),
                endpoint_csv_sha256 = GeneratedArtifactIdentity.Hash(child.EndpointCsvPath),
                line_count = child.LineCount,
                segment_count = child.SegmentCount,
                max_segment_length_m = child.MaxSegmentLengthM
            })
        };
        File.WriteAllText(
            manifest,
            JsonSerializer.Serialize(
                payload,
                new JsonSerializerOptions { WriteIndented = true }));
        outputPaths.Add(manifest);

        return new KmlSurveySubdivisionBundle(
            output,
            manifest,
            motherEndpoints,
            endpointCsvPaths,
            outputPaths,
            childFiles,
            split);
    }

    public bool TryResolveGeneratedChild(
        string childKmlPath,
        out KmlSurveySubdivisionResolvedChild? child)
    {
        child = null;
        if (!File.Exists(childKmlPath))
            return false;

        string fullKml = Path.GetFullPath(childKmlPath);
        string? directory = Path.GetDirectoryName(fullKml);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;

        foreach (string manifestPath in CandidateManifestPaths(directory))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (!doc.RootElement.TryGetProperty("children", out JsonElement children) ||
                    children.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                string manifestDirectory = Path.GetDirectoryName(
                    Path.GetFullPath(manifestPath))!;
                double centralMeridian = doc.RootElement.TryGetProperty(
                        "central_meridian",
                        out JsonElement centralElement) &&
                    centralElement.TryGetDouble(out double cm)
                        ? cm
                        : double.NaN;
                string datum = doc.RootElement.TryGetProperty(
                        "datum",
                        out JsonElement datumElement)
                    ? datumElement.GetString() ?? "WGS84"
                    : "WGS84";

                int coverageExtensionLineCount = 0;
                if (doc.RootElement.TryGetProperty("mother", out JsonElement motherElement) &&
                    motherElement.ValueKind == JsonValueKind.Object &&
                    motherElement.TryGetProperty(
                        "coverage_extension_line_count",
                        out JsonElement extensionCountElement) &&
                    extensionCountElement.TryGetInt32(out int parsedExtensionCount))
                {
                    coverageExtensionLineCount = Math.Max(0, parsedExtensionCount);
                }
                bool containsFrozenSnakeExtension = coverageExtensionLineCount > 0;

                var cuts = new List<SurveySubdivisionCutLine>();
                if (doc.RootElement.TryGetProperty(
                        "cut_lines",
                        out JsonElement cutRows) &&
                    cutRows.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement row in cutRows.EnumerateArray())
                    {
                        if (TryReadDouble(row, "start_x", out double sx) &&
                            TryReadDouble(row, "start_y", out double sy) &&
                            TryReadDouble(row, "end_x", out double ex) &&
                            TryReadDouble(row, "end_y", out double ey))
                        {
                            cuts.Add(new SurveySubdivisionCutLine(
                                new SurveyBoundaryPoint(sx, sy),
                                new SurveyBoundaryPoint(ex, ey)));
                        }
                    }
                }

                foreach (JsonElement row in children.EnumerateArray())
                {
                    if (!row.TryGetProperty("kml", out JsonElement kmlElement) ||
                        !row.TryGetProperty("endpoint_csv", out JsonElement csvElement))
                    {
                        continue;
                    }

                    string kmlText = kmlElement.GetString() ?? "";
                    string csvText = csvElement.GetString() ?? "";
                    if (kmlText.Length == 0 || csvText.Length == 0)
                        continue;

                    string kml = ResolveManifestPath(manifestDirectory, kmlText);
                    if (!Path.GetFullPath(kml).Equals(
                            fullKml,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string endpointPath = ResolveManifestPath(
                        manifestDirectory,
                        csvText);
                    int index = row.TryGetProperty(
                            "index",
                            out JsonElement indexElement)
                        ? indexElement.GetInt32()
                        : 0;
                    string partitionCode = row.TryGetProperty(
                            "partition_code",
                            out JsonElement codeElement)
                        ? codeElement.GetString() ?? $"P{index:D2}"
                        : $"P{index:D2}";

                    child = new KmlSurveySubdivisionResolvedChild(
                        fullKml,
                        endpointPath,
                        Path.GetFullPath(manifestPath),
                        index,
                        partitionCode,
                        centralMeridian,
                        datum,
                        cuts,
                        containsFrozenSnakeExtension);
                    return File.Exists(child.EndpointCsvPath);
                }
            }
            catch
            {
                // Ignore stale/invalid historical manifests and keep searching.
            }
        }

        return false;
    }

    private static IEnumerable<string> CandidateManifestPaths(string childDirectory)
    {
        var directories = new List<string>();
        string? current = Path.GetFullPath(childDirectory);
        for (int depth = 0; depth < 3 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            directories.Add(current);
            current = Directory.GetParent(current)?.FullName;
        }

        return directories
            .SelectMany(directory =>
                Directory.Exists(directory)
                    ? Directory.EnumerateFiles(
                        directory,
                        "*.json",
                        SearchOption.TopDirectoryOnly)
                    : Enumerable.Empty<string>())
            .Where(path =>
                Path.GetFileName(path).Equals(
                    "partition_manifest.json",
                    StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).EndsWith(
                    "分区清单.json",
                    StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc);
    }

    private static bool TryReadDouble(
        JsonElement row,
        string name,
        out double value)
    {
        value = 0d;
        return row.TryGetProperty(name, out JsonElement element) &&
               element.TryGetDouble(out value);
    }

    private static string ResolveManifestPath(
        string manifestDirectory,
        string value) =>
        Path.GetFullPath(
            Path.IsPathRooted(value)
                ? value
                : Path.Combine(manifestDirectory, value));

    private static string RelativeManifestPath(
        string manifestDirectory,
        string fullPath) =>
        Path.GetRelativePath(
                manifestDirectory,
                Path.GetFullPath(fullPath))
            .Replace('\\', '/');

    private static string AxisManifestName(SurveySubdivisionAxis axis) => axis switch
    {
        SurveySubdivisionAxis.ProjectedX => "projected_x_or_longitude",
        SurveySubdivisionAxis.ProjectedY => "projected_y_or_latitude",
        SurveySubdivisionAxis.Custom => "user_drawn_cut_lines",
        _ => "along_survey_line_direction"
    };

    public IReadOnlyList<SurveyBoundaryPoint> ReadProjectedBoundary(
        string sourceKml,
        double? centralMeridian,
        string datum)
    {
        SurveyRegionGeometry region = ReadProjectedRegion(
            sourceKml, centralMeridian, datum);
        return region.RequireSimpleSinglePolygon(
            "Legacy projected boundary API");
    }

    public SurveyRegionGeometry ReadProjectedRegion(
        string sourceKml,
        double? centralMeridian,
        string datum)
    {
        IReadOnlyList<SpatialPolygon> polygons = ReadRegionPolygons(sourceKml);
        IReadOnlyList<GeoCoordinate> allOuter = polygons
            .SelectMany(x => x.OuterRing).ToArray();
        double resolvedCentralMeridian = centralMeridian ??
            InferCentralMeridian(allOuter);
        return ProjectRegion(polygons, resolvedCentralMeridian, datum);
    }

    public IReadOnlyList<SpatialPolygon> ReadRegionPolygons(string sourceKml)
    {
        if (!File.Exists(sourceKml))
            throw new FileNotFoundException(
                "请选择有效的 KML 范围文件。", sourceKml);
        if (!Path.GetExtension(sourceKml).Equals(
                ".kml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("KML 分区源文件必须是 .kml。");

        SpatialPolygon[] polygons = SpatialGeometryService
            .ParseKmlPolygons(sourceKml)
            .Where(x => x.OuterRing.Count >= 3)
            .ToArray();
        if (polygons.Length == 0)
            throw new InvalidDataException(
                "KML 中未找到至少 3 点的 Polygon 外环。");
        return polygons;
    }

    public SpatialGeometry ReadPrimaryPolygon(string sourceKml)
    {
        IReadOnlyList<SpatialPolygon> polygons = ReadRegionPolygons(sourceKml);
        if (polygons.Count != 1 || polygons[0].InnerRings.Count != 0)
            throw new NotSupportedException(
                "Legacy primary-polygon API cannot represent MultiPolygon or holes. " +
                "Use ReadProjectedRegion instead.");
        SpatialPolygon polygon = polygons[0];
        return new SpatialGeometry
        {
            Name = polygon.Name,
            Type = "Polygon",
            Coordinates = polygon.OuterRing.ToList(),
            Closed = true
        };
    }

    private static SurveyRegionGeometry ProjectRegion(
        IReadOnlyList<SpatialPolygon> polygons,
        double centralMeridian,
        string datum)
    {
        var projected = new List<SurveyRegionPolygon>();
        foreach (SpatialPolygon polygon in polygons)
        {
            List<SurveyBoundaryPoint> outer = ProjectBoundary(
                polygon.OuterRing, centralMeridian, datum);
            IReadOnlyList<IReadOnlyList<SurveyBoundaryPoint>> holes =
                polygon.InnerRings
                    .Select(ring => (IReadOnlyList<SurveyBoundaryPoint>)
                        ProjectBoundary(ring, centralMeridian, datum))
                    .ToArray();
            projected.Add(new SurveyRegionPolygon(outer, holes));
        }
        return new SurveyRegionGeometry(projected);
    }

    private static string RegionDisplayName(
        IReadOnlyList<SpatialPolygon> polygons)
    {
        string[] names = polygons.Select(x => x.Name)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return names.Length == 1 ? names[0] :
            names.Length > 1 ? string.Join(" + ", names) : "Region";
    }

    private static double InferCentralMeridian(
        IReadOnlyList<GeoCoordinate> coordinates)
    {
        double[] longitudes = coordinates
            .Select(point => point.Longitude)
            .OrderBy(value => value)
            .ToArray();
        if (longitudes.Length == 0)
            throw new InvalidDataException("KML Polygon 没有有效经度。");

        double median = longitudes.Length % 2 == 1
            ? longitudes[longitudes.Length / 2]
            : (longitudes[longitudes.Length / 2 - 1] +
               longitudes[longitudes.Length / 2]) / 2.0;
        return CoordinateService.SuggestGk3CentralMeridian(median);
    }

    private static List<SurveyBoundaryPoint> ProjectBoundary(
        IReadOnlyList<GeoCoordinate> source,
        double centralMeridian,
        string datum)
    {
        var points = new List<SurveyBoundaryPoint>();
        foreach (GeoCoordinate geo in source)
        {
            var projected = CoordinateService.GaussKrugerForward(
                geo.Longitude,
                geo.Latitude,
                centralMeridian,
                datum);
            var point = new SurveyBoundaryPoint(
                projected.Easting,
                projected.Northing);
            if (points.Count == 0 || Distance(points[^1], point) > 1e-6)
                points.Add(point);
        }

        if (points.Count > 1 && Distance(points[0], points[^1]) <= 1e-6)
            points.RemoveAt(points.Count - 1);
        if (points.Count < 3)
            throw new InvalidDataException("KML Polygon 投影后不足 3 个不同点。");
        return points;
    }

    private static double SignedDegreeArea(
        IReadOnlyList<GeoCoordinate> points)
    {
        double area2 = 0d;
        for (int i = 0; i < points.Count; i++)
        {
            GeoCoordinate a = points[i];
            GeoCoordinate b = points[(i + 1) % points.Count];
            area2 += a.Longitude * b.Latitude - b.Longitude * a.Latitude;
        }
        return area2 / 2d;
    }

    private static double Distance(
        SurveyBoundaryPoint a,
        SurveyBoundaryPoint b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static string CompactSubdivisionStem(string sourceKml)
    {
        string stem = SafeStem(Path.GetFileNameWithoutExtension(sourceKml));

        // Coordinate-range exports already live under a project/workarea folder, so
        // repeating "项目_工区_坐标_" in every child file only makes paths harder
        // to read. Keep the meaningful range/style suffix when that marker exists.
        const string coordinateMarker = "_坐标_";
        int marker = stem.LastIndexOf(
            coordinateMarker,
            StringComparison.OrdinalIgnoreCase);
        if (marker >= 0 && marker + coordinateMarker.Length < stem.Length)
            stem = stem[(marker + coordinateMarker.Length)..];

        stem = stem.Trim(' ', '_', '-', '.');
        if (stem.Length == 0)
            stem = "范围";
        if (stem.Length > 32)
            stem = stem[..32].TrimEnd(' ', '_', '-', '.');
        return stem.Length == 0 ? "范围" : stem;
    }

    private static void DeletePreviousBundleFiles(
        string outputDirectory,
        string stem)
    {
        string[] patterns =
        [
            $"{stem}_子区*.kml",
            $"{stem}_子区*_端点.csv",
            $"{stem}_子区*_航线端点.csv",
            $"{stem}_母区端点.csv",
            $"{stem}_母区_航线端点.csv",
            $"{stem}_分区清单.json",
            $"{stem}_KML分区清单.json",
            "partition_manifest.json",
            "survey_lines.csv"
        ];

        foreach (string pattern in patterns)
        foreach (string path in Directory.EnumerateFiles(
                     outputDirectory,
                     pattern,
                     SearchOption.TopDirectoryOnly))
        {
            File.Delete(path);
        }

        foreach (string directory in Directory.EnumerateDirectories(
                     outputDirectory,
                     "P*",
                     SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(directory);
            if (name.Length == 3 &&
                name[0] == 'P' &&
                int.TryParse(name[1..], out _) &&
                File.Exists(Path.Combine(directory, name + ".kml")))
            {
                Directory.Delete(directory, true);
            }
        }
    }

    private static string SafeStem(string text)
    {
        string stem = string.IsNullOrWhiteSpace(text) ? "KML工区" : text.Trim();
        foreach (char invalid in Path.GetInvalidFileNameChars())
            stem = stem.Replace(invalid, '_');
        return stem;
    }
}

public sealed record KmlSurveySubdivisionResolvedChild(
    string KmlPath,
    string EndpointCsvPath,
    string ManifestPath,
    int Index,
    string PartitionCode,
    double CentralMeridian,
    string Datum,
    IReadOnlyList<SurveySubdivisionCutLine> Cuts,
    bool ContainsFrozenSnakeExtension);

public sealed record KmlSurveySubdivisionPreparation(
    string SourceKml,
    string SourceName,
    double CentralMeridian,
    string Datum,
    SurveyLinePlanResult MotherPlan,
    int SuggestedSubareaCount);

public sealed record KmlSurveySubdivisionChildFile(
    int Index,
    string PartitionCode,
    string KmlPath,
    string EndpointCsvPath,
    int LineCount,
    int SegmentCount,
    double MaxSegmentLengthM);

public sealed record KmlSurveySubdivisionBundle(
    string OutputDirectory,
    string ManifestPath,
    string MotherEndpointCsvPath,
    IReadOnlyList<string> ChildEndpointCsvPaths,
    IReadOnlyList<string> OutputPaths,
    IReadOnlyList<KmlSurveySubdivisionChildFile> Children,
    SurveyLineSubdivisionResult Subdivision);
