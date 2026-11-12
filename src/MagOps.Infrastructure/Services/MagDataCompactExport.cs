using System.IO.Compression;
using System.Text;

namespace MagOps.Infrastructure.Services;

internal sealed record MagDataSourceFileEntry(
    int SourceId,
    string Sensor,
    string SourceFile,
    string RelativePath,
    int CanonicalRecordCount);

internal sealed class MagDataSourceCatalog
{
    private readonly Dictionary<string, int> _sourceIds;

    public MagDataSourceCatalog(
        IReadOnlyList<MagDataSourceFileEntry> entries)
    {
        Entries = entries;
        _sourceIds = entries.ToDictionary(
            x => x.SourceFile,
            x => x.SourceId,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<MagDataSourceFileEntry> Entries { get; }

    public int GetSourceId(string sourceFile)
    {
        if (_sourceIds.TryGetValue(
                sourceFile,
                out int sourceId))
        {
            return sourceId;
        }

        throw new InvalidDataException(
            $"数据来源未登记到 SourceId 映射：{sourceFile}");
    }

    public string GetSourceIdText(string? sourceFile) =>
        string.IsNullOrWhiteSpace(sourceFile)
            ? ""
            : GetSourceId(sourceFile).ToString(
                System.Globalization.CultureInfo.InvariantCulture);
}

internal static class MagDataCompactExport
{
    public static MagDataSourceCatalog BuildSourceCatalog(
        FieldMagFolderAnalysis analysis)
    {
        var observations =
            analysis.UavSamples
                .Select(x =>
                    new SourceObservation(
                        x.SourceFile,
                        "UAV"))
                .Concat(
                    analysis.BaseSamples.Select(x =>
                        new SourceObservation(
                            x.SourceFile,
                            "GSM19T")))
                .Where(x =>
                    !string.IsNullOrWhiteSpace(
                        x.SourceFile))
                .ToList();

        IReadOnlyList<MagDataSourceFileEntry> entries =
            observations
                .GroupBy(
                    x => x.SourceFile,
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    x => x.Key,
                    StringComparer.OrdinalIgnoreCase)
                .Select(
                    (group, index) =>
                        new MagDataSourceFileEntry(
                            index + 1,
                            string.Join(
                                "+",
                                group
                                    .Select(x => x.Sensor)
                                    .Distinct(
                                        StringComparer.OrdinalIgnoreCase)
                                    .OrderBy(
                                        x => x,
                                        StringComparer.OrdinalIgnoreCase)),
                            group.Key,
                            RelativePath(
                                analysis.SourceDirectory,
                                group.Key),
                            group.Count()))
                .ToList();

        return new MagDataSourceCatalog(
            entries);
    }

    public static void WriteGzipCsv(
        string path,
        Action<TextWriter> write)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(
                Path.GetFullPath(path))!);

        using FileStream file =
            new(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.SequentialScan);
        using var gzip =
            new GZipStream(
                file,
                CompressionLevel.SmallestSize,
                leaveOpen: false);
        using var writer =
            new StreamWriter(
                gzip,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false),
                128 * 1024,
                leaveOpen: false);

        write(writer);
    }

    private static string RelativePath(
        string sourceRoot,
        string sourceFile)
    {
        try
        {
            string root =
                Path.GetFullPath(
                    sourceRoot);
            string full =
                Path.GetFullPath(
                    sourceFile);
            string relative =
                Path.GetRelativePath(
                    root,
                    full);

            if (!relative.Equals(
                    "..",
                    StringComparison.Ordinal) &&
                !relative.StartsWith(
                    ".." +
                    Path.DirectorySeparatorChar,
                    StringComparison.Ordinal) &&
                !Path.IsPathFullyQualified(
                    relative))
            {
                return relative;
            }
        }
        catch
        {
        }

        return sourceFile;
    }

    private sealed record SourceObservation(
        string SourceFile,
        string Sensor);
}
