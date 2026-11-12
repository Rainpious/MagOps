using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using System.Xml.Linq;

namespace MagOps.Infrastructure.Services;

public sealed record RouteKmlImportItem(
    string SourcePath,
    string ImportedPath,
    string FileName,
    string Sha256,
    int RouteGeometryCount)
{
    public DateTimeOffset? SourceLastWriteUtc { get; init; }
    public DateTimeOffset? MeasurementTimeUtc { get; init; }
}

public sealed record RouteKmlImportResult(
    string BatchDirectory,
    string ManifestPath,
    DateTimeOffset ImportedAtUtc,
    IReadOnlyList<RouteKmlImportItem> Items)
{
    /// <summary>Route geometries added by this operation.</summary>
    public int RouteGeometryCount => Items.Sum(x => x.RouteGeometryCount);

    /// <summary>Files changed by this operation: new KML files plus manifest.</summary>
    public IReadOnlyList<string> Files =>
        Items.Select(x => x.ImportedPath)
            .Append(ManifestPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>
/// Imports route KML files designed outside MagOps into an appendable project route set.
/// KML bytes are copied unchanged. RoutePlanner is never called.
/// </summary>
public sealed class RouteKmlImportService
{
    public const string ManifestFileName = "外部航线KML导入_manifest.json";

    private sealed record ValidatedSource(
        string Path,
        string Sha256,
        int RouteGeometryCount,
        DateTimeOffset SourceLastWriteUtc,
        DateTimeOffset MeasurementTimeUtc);

    public RouteKmlImportResult Import(
        string importRootDirectory,
        IEnumerable<string> sourcePaths,
        DateTimeOffset? importedAt = null)
    {
        if (string.IsNullOrWhiteSpace(importRootDirectory))
            throw new ArgumentException("外部航线 KML 导入目录不能为空。", nameof(importRootDirectory));

        string importRoot = Path.GetFullPath(importRootDirectory);
        List<ValidatedSource> validated = ValidateSources(sourcePaths);
        DateTimeOffset stamp = importedAt ?? DateTimeOffset.Now;
        string batchName = stamp.ToString("yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture);
        string batchDirectory = UniqueBatchDirectory(importRoot, batchName);

        // Complete validation before the first directory write.
        Directory.CreateDirectory(batchDirectory);
        var added = new List<RouteKmlImportItem>();
        try
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CopyValidated(batchDirectory, validated, usedNames, added);
            WriteManifest(
                batchDirectory,
                stamp.ToUniversalTime(),
                stamp.ToUniversalTime(),
                added);

            return new RouteKmlImportResult(
                batchDirectory,
                Path.Combine(batchDirectory, ManifestFileName),
                stamp.ToUniversalTime(),
                added);
        }
        catch
        {
            try
            {
                if (Directory.Exists(batchDirectory))
                    Directory.Delete(batchDirectory, recursive: true);
            }
            catch
            {
                // Preserve the original exception.
            }
            throw;
        }
    }

    /// <summary>
    /// Reads the current external KML collection manifest defensively. Exact
    /// duplicate content is collapsed by SHA-256 even for batches created by
    /// older MagOps versions.
    /// </summary>
    public static IReadOnlyList<RouteKmlImportItem> InspectBatch(
        string batchDirectory)
    {
        if (string.IsNullOrWhiteSpace(batchDirectory) ||
            !Directory.Exists(batchDirectory))
        {
            return [];
        }

        return ReadExistingItems(
                Path.GetFullPath(batchDirectory))
            .GroupBy(
                x => x.Sha256,
                StringComparer.OrdinalIgnoreCase)
            .Select(group =>
                group
                    .OrderBy(x =>
                        x.MeasurementTimeUtc ??
                        x.SourceLastWriteUtc ??
                        DateTimeOffset.MinValue)
                    .ThenBy(x =>
                        x.FileName,
                        StringComparer.OrdinalIgnoreCase)
                    .First())
            .OrderBy(x =>
                x.MeasurementTimeUtc ??
                x.SourceLastWriteUtc ??
                DateTimeOffset.MinValue)
            .ThenBy(x =>
                x.FileName,
                StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Adds KML files to an existing external route collection. Existing copied
    /// KML files are never rewritten. Exact-content duplicates are skipped.
    /// </summary>
    public RouteKmlImportResult Append(
        string batchDirectory,
        IEnumerable<string> sourcePaths,
        DateTimeOffset? importedAt = null)
    {
        if (string.IsNullOrWhiteSpace(batchDirectory))
            throw new ArgumentException("当前外部航线集目录不能为空。", nameof(batchDirectory));

        string batch = Path.GetFullPath(batchDirectory);
        if (!Directory.Exists(batch))
            throw new DirectoryNotFoundException($"当前外部航线集不存在：{batch}");

        List<ValidatedSource> validated = ValidateSources(sourcePaths);
        List<RouteKmlImportItem> existing = ReadExistingItems(batch);
        HashSet<string> existingHashes = existing
            .Select(x => x.Sha256)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        List<ValidatedSource> newSources = validated
            .Where(x => !existingHashes.Contains(x.Sha256))
            .ToList();

        string manifest = Path.Combine(batch, ManifestFileName);
        DateTimeOffset createdAt = ReadImportedAt(manifest)
            ?? new DateTimeOffset(Directory.GetCreationTimeUtc(batch));
        DateTimeOffset updatedAt = (importedAt ?? DateTimeOffset.Now).ToUniversalTime();

        if (newSources.Count == 0)
        {
            return new RouteKmlImportResult(
                batch,
                manifest,
                createdAt.ToUniversalTime(),
                Array.Empty<RouteKmlImportItem>());
        }

        byte[]? previousManifest = File.Exists(manifest)
            ? File.ReadAllBytes(manifest)
            : null;
        var added = new List<RouteKmlImportItem>();
        var usedNames = existing
            .Select(x => x.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            CopyValidated(batch, newSources, usedNames, added);
            List<RouteKmlImportItem> all = existing.Concat(added).ToList();
            WriteManifest(
                batch,
                createdAt.ToUniversalTime(),
                updatedAt,
                all);

            return new RouteKmlImportResult(
                batch,
                manifest,
                createdAt.ToUniversalTime(),
                added);
        }
        catch
        {
            foreach (RouteKmlImportItem item in added)
            {
                try
                {
                    if (File.Exists(item.ImportedPath))
                        File.Delete(item.ImportedPath);
                }
                catch
                {
                    // Preserve original exception.
                }
            }

            try
            {
                if (previousManifest is not null)
                    File.WriteAllBytes(manifest, previousManifest);
                else if (File.Exists(manifest))
                    File.Delete(manifest);
            }
            catch
            {
                // Preserve original exception.
            }

            throw;
        }
    }

    private static List<ValidatedSource> ValidateSources(
        IEnumerable<string> sourcePaths)
    {
        List<string> sources = sourcePaths
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sources.Count == 0)
            throw new InvalidOperationException("请选择至少一个航线 KML 文件。");

        var validated = new List<ValidatedSource>();
        var seenHashes =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (string source in sources)
        {
            if (!File.Exists(source))
                throw new FileNotFoundException("待导入的航线 KML 不存在。", source);
            if (!Path.GetExtension(source).Equals(".kml", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"只支持导入 KML 航线文件：{Path.GetFileName(source)}");

            int routeCount = CountRouteGeometries(source);
            if (routeCount == 0)
            {
                throw new InvalidDataException(
                    $"KML 中未发现可作为航线的 LineString / gx:Track：{Path.GetFileName(source)}");
            }

            string sha =
                ArtifactService.Sha256(source);
            if (!seenHashes.Add(sha))
            {
                // Same bytes == same file content for route-import purposes.
                // Do not create another logical route merely because the source
                // path or filename differs.
                continue;
            }

            DateTimeOffset lastWriteUtc =
                new(
                    File.GetLastWriteTimeUtc(source),
                    TimeSpan.Zero);
            DateTimeOffset measurementTimeUtc =
                ResolveMeasurementTimeUtc(
                    source,
                    lastWriteUtc);

            validated.Add(
                new ValidatedSource(
                    source,
                    sha,
                    routeCount,
                    lastWriteUtc,
                    measurementTimeUtc));
        }

        return validated;
    }

    private static DateTimeOffset ResolveMeasurementTimeUtc(
        string source,
        DateTimeOffset fallbackUtc)
    {
        try
        {
            XDocument doc =
                XDocument.Load(source);
            List<DateTimeOffset> times =
                doc.Descendants()
                    .Where(x =>
                        x.Name.LocalName.Equals(
                            "when",
                            StringComparison.OrdinalIgnoreCase))
                    .Select(x =>
                    {
                        bool parsed =
                            DateTimeOffset.TryParse(
                                x.Value.Trim(),
                                CultureInfo.InvariantCulture,
                                DateTimeStyles.AssumeUniversal |
                                DateTimeStyles.AdjustToUniversal,
                                out DateTimeOffset value);
                        return (
                            Parsed: parsed,
                            Value: value);
                    })
                    .Where(x => x.Parsed)
                    .Select(x =>
                        x.Value.ToUniversalTime())
                    .ToList();
            if (times.Count > 0)
                return times.Max();
        }
        catch
        {
            // KML geometry validation is handled separately. Timestamp metadata
            // is optional and must never block an otherwise valid route import.
        }

        return fallbackUtc.ToUniversalTime();
    }

    private static int CountRouteGeometries(string source)
    {
        List<SpatialGeometry> geometries;
        try
        {
            geometries = SpatialGeometryService.ParseKml(source);
        }
        catch (Exception ex) when (
            ex is InvalidDataException or FormatException or System.Xml.XmlException)
        {
            throw new InvalidDataException(
                $"航线 KML 无法解析：{Path.GetFileName(source)}；{ex.Message}",
                ex);
        }

        return geometries.Count(x =>
            (x.Type is "LineString" or "Track") &&
            x.Coordinates.Count >= 2);
    }

    private static void CopyValidated(
        string batchDirectory,
        IReadOnlyList<ValidatedSource> sources,
        HashSet<string> usedNames,
        List<RouteKmlImportItem> added)
    {
        foreach (ValidatedSource source in sources)
        {
            string destinationName =
                UniqueFileName(Path.GetFileName(source.Path), usedNames);
            string destination =
                Path.Combine(batchDirectory, destinationName);

            File.Copy(source.Path, destination, overwrite: false);
            string destinationHash = ArtifactService.Sha256(destination);
            if (!source.Sha256.Equals(
                destinationHash,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    $"KML 复制后哈希不一致，已停止导入：{Path.GetFileName(source.Path)}");
            }

            added.Add(new RouteKmlImportItem(
                source.Path,
                destination,
                destinationName,
                destinationHash,
                source.RouteGeometryCount)
            {
                SourceLastWriteUtc =
                    source.SourceLastWriteUtc,
                MeasurementTimeUtc =
                    source.MeasurementTimeUtc
            });
        }
    }

    private static List<RouteKmlImportItem> ReadExistingItems(
        string batchDirectory)
    {
        string manifest = Path.Combine(batchDirectory, ManifestFileName);
        var result = new List<RouteKmlImportItem>();
        var knownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (File.Exists(manifest))
        {
            try
            {
                using JsonDocument doc =
                    JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.TryGetProperty("files", out JsonElement files) &&
                    files.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement entry in files.EnumerateArray())
                    {
                        string name = entry.TryGetProperty("imported_file", out JsonElement imported)
                            ? imported.GetString() ?? ""
                            : "";
                        if (string.IsNullOrWhiteSpace(name))
                            continue;

                        string importedPath = Path.Combine(batchDirectory, name);
                        if (!File.Exists(importedPath))
                            continue;

                        string source = entry.TryGetProperty("source_path", out JsonElement src)
                            ? src.GetString() ?? importedPath
                            : importedPath;
                        string sha = entry.TryGetProperty("sha256", out JsonElement hash)
                            ? hash.GetString() ?? ""
                            : "";
                        if (string.IsNullOrWhiteSpace(sha))
                            sha = ArtifactService.Sha256(importedPath);

                        int routeCount = entry.TryGetProperty(
                                "route_geometry_count",
                                out JsonElement count) &&
                            count.TryGetInt32(out int parsed)
                            ? parsed
                            : CountRouteGeometries(importedPath);

                        DateTimeOffset fallbackWrite =
                            new(
                                File.GetLastWriteTimeUtc(importedPath),
                                TimeSpan.Zero);
                        DateTimeOffset? sourceLastWrite =
                            ReadOptionalUtc(
                                entry,
                                "source_last_write_utc");
                        DateTimeOffset? measurementTime =
                            ReadOptionalUtc(
                                entry,
                                "measurement_time_utc");

                        result.Add(new RouteKmlImportItem(
                            source,
                            importedPath,
                            name,
                            sha,
                            routeCount)
                        {
                            SourceLastWriteUtc =
                                sourceLastWrite ??
                                fallbackWrite,
                            MeasurementTimeUtc =
                                measurementTime ??
                                ResolveMeasurementTimeUtc(
                                    importedPath,
                                    sourceLastWrite ??
                                    fallbackWrite)
                        });
                        knownFiles.Add(name);
                    }
                }
            }
            catch (JsonException)
            {
                // Reconstruct from copied KML files below.
            }
        }

        foreach (string path in Directory.EnumerateFiles(
            batchDirectory,
            "*.kml",
            SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(path);
            if (knownFiles.Contains(name))
                continue;

            DateTimeOffset lastWrite =
                new(
                    File.GetLastWriteTimeUtc(path),
                    TimeSpan.Zero);
            result.Add(new RouteKmlImportItem(
                path,
                path,
                name,
                ArtifactService.Sha256(path),
                CountRouteGeometries(path))
            {
                SourceLastWriteUtc =
                    lastWrite,
                MeasurementTimeUtc =
                    ResolveMeasurementTimeUtc(
                        path,
                        lastWrite)
            });
        }

        return result
            .OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DateTimeOffset? ReadOptionalUtc(
        JsonElement entry,
        string propertyName)
    {
        if (!entry.TryGetProperty(
                propertyName,
                out JsonElement value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset parsed))
        {
            return parsed.ToUniversalTime();
        }

        return null;
    }

    private static DateTimeOffset? ReadImportedAt(string manifestPath)
    {
        if (!File.Exists(manifestPath))
            return null;
        try
        {
            using JsonDocument doc =
                JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (doc.RootElement.TryGetProperty(
                    "imported_at_utc",
                    out JsonElement value) &&
                DateTimeOffset.TryParse(
                    value.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset stamp))
            {
                return stamp;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static void WriteManifest(
        string batchDirectory,
        DateTimeOffset importedAtUtc,
        DateTimeOffset updatedAtUtc,
        IReadOnlyList<RouteKmlImportItem> items)
    {
        string manifest = Path.Combine(batchDirectory, ManifestFileName);
        var payload = new
        {
            schema_version = "magops-route-kml-import-3",
            imported_at_utc = importedAtUtc.ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture),
            updated_at_utc = updatedAtUtc.ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture),
            import_mode = "copy_only",
            collection_mode = "appendable",
            note = "外部航线 KML 按字节复制进入项目树；可继续加入多组 KML。未调用 RoutePlanner，未重绘、重采样或重写 KML；“替换航线集”只切换当前活动批次，不删除历史批次。",
            route_geometry_count = items.Sum(x => x.RouteGeometryCount),
            files = items.Select((x, index) => new
            {
                order = index + 1,
                source_path = x.SourcePath,
                imported_file = x.FileName,
                sha256 = x.Sha256,
                source_last_write_utc =
                    x.SourceLastWriteUtc?.ToUniversalTime()
                        .ToString("O", CultureInfo.InvariantCulture),
                measurement_time_utc =
                    x.MeasurementTimeUtc?.ToUniversalTime()
                        .ToString("O", CultureInfo.InvariantCulture),
                route_geometry_count = x.RouteGeometryCount
            }).ToArray()
        };

        File.WriteAllText(
            manifest,
            JsonSerializer.Serialize(
                payload,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
                }),
            new UTF8Encoding(true));
    }

    private static string UniqueBatchDirectory(
        string root,
        string baseName)
    {
        string candidate = Path.Combine(root, baseName);
        if (!Directory.Exists(candidate) && !File.Exists(candidate))
            return candidate;

        for (int i = 2; i < 1000; i++)
        {
            candidate = Path.Combine(root, $"{baseName}_{i:D2}");
            if (!Directory.Exists(candidate) && !File.Exists(candidate))
                return candidate;
        }

        throw new IOException("无法为外部航线 KML 创建唯一导入批次目录。");
    }

    private static string UniqueFileName(
        string fileName,
        HashSet<string> usedNames)
    {
        string safeName = Path.GetFileName(fileName);
        if (usedNames.Add(safeName))
            return safeName;

        string stem = Path.GetFileNameWithoutExtension(safeName);
        string extension = Path.GetExtension(safeName);
        for (int i = 2; i < 1000; i++)
        {
            string candidate = $"{stem}_{i:D2}{extension}";
            if (usedNames.Add(candidate))
                return candidate;
        }

        throw new IOException($"无法为导入 KML 生成唯一文件名：{safeName}");
    }
}
