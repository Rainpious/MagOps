using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MagOps.Infrastructure.Storage;

namespace MagOps.Infrastructure.Services;

public sealed record ArtifactDependency(
    string Kind, string Path, string Sha256,
    Dictionary<string, string>? EqualsFields = null,
    string CutoffDate = "", string[]? Fields = null,
    int MinimumCount = 0, string TimestampDateField = "", string DateEquals = "", string TimeZoneId = "UTC");

public sealed record ArtifactDependencyStatus(string Status, string Reason);

/// <summary>Versioned, bounded file/record/query evidence. No registry or UI state is a source.</summary>
public static class ArtifactDependencies
{
    public const string Contract = "independent-export-1";
    public static string Digest(object value) => Digest(JsonSerializer.SerializeToElement(value));
    public static string Digest(JsonElement value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(value))));

    private static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject()
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => JsonSerializer.Serialize(x.Name) + ":" + Canonical(x.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonical)) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(value.GetString()),
        JsonValueKind.Number when value.TryGetDecimal(out decimal number) => number.ToString("G29", System.Globalization.CultureInfo.InvariantCulture),
        _ => value.GetRawText()
    };

    public static string Relative(string root, string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        string relative = System.IO.Path.GetRelativePath(System.IO.Path.GetFullPath(root), full);
        return relative != ".." && !relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar) &&
               !System.IO.Path.IsPathRooted(relative) ? relative.Replace('\\', '/') : full;
    }

    public static string Resolve(string root, string path)
    {
        if (System.IO.Path.IsPathRooted(path)) return System.IO.Path.GetFullPath(path);
        string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path));
        string prefix = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("来源相对路径越出项目。");
        return full;
    }

    public static ArtifactDependency File(string root, string path, string? expectedHash = null) =>
        new("file", Relative(root, path), expectedHash ?? GeneratedArtifactIdentity.Hash(path));

    public static ArtifactDependency SourceSet(string root, string directory, string date = "")
    {
        var d = new ArtifactDependency(date.Length == 0 ? "field_files" : "daily_files",
            Relative(root, directory), "", CutoffDate: date);
        return d with { Sha256 = SourceSetHash(root, d) };
    }
    private static string SourceSetHash(string root, ArtifactDependency d)
    {
        string directory = Resolve(root, d.Path);
        var paths = d.Kind == "field_files" ? FieldMagDataService.SourcePaths(directory) :
            FieldOperationsAnalysisService.DailySourcePaths(directory, DateTime.ParseExact(d.CutoffDate, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        return Digest(paths.Select(x => Relative(root, x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static ArtifactDependency ArtifactReference(string root, string reference) =>
        new("artifact_reference", reference, Digest(ReferenceExists(root, reference)));
    private static bool ReferenceExists(string root, string reference)
    {
        var rows = new JsonStore().Read(ProjectPaths.Json(root, "artifacts.json"), new List<MagOps.Core.Domain.ArtifactRecord>());
        var row = rows.FirstOrDefault(x => x.ArtifactId.Equals(reference, StringComparison.OrdinalIgnoreCase) ||
            x.Path.Equals(reference.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));
        return row is not null ? row.ChangeStatus != "MISSING" : System.IO.File.Exists(Resolve(root, reference));
    }

    public static ArtifactDependency Existence(string root, string path) =>
        new("exists", Relative(root, path), Digest(System.IO.File.Exists(path)));

    public static ArtifactDependency Query(string root, string path,
        Dictionary<string, string>? equals = null, string cutoffDate = "",
        string[]? fields = null, int minimumCount = 0,
        string timestampDateField = "", string dateEquals = "", string timeZoneId = "UTC")
    {
        var dependency = new ArtifactDependency("records", Relative(root, path), "", equals,
            cutoffDate, fields, minimumCount, timestampDateField, dateEquals, timeZoneId);
        return dependency with { Sha256 = RecordsHash(root, dependency) };
    }

    // Compares the exact in-memory records used by a generator with the authority, rather
    // than attaching today's hash to an older in-memory result.
    public static ArtifactDependency Records<T>(string root, string path, IEnumerable<T> expected,
        Dictionary<string, string>? equals = null, string cutoffDate = "", string[]? fields = null,
        int minimumCount = 1)
    {
        var dependency = new ArtifactDependency("records", Relative(root, path), "", equals,
            cutoffDate, fields, minimumCount);
        string expectedHash = HashRows(JsonSerializer.SerializeToElement(expected), dependency);
        if (RecordsHash(root, dependency) != expectedHash)
            throw new InvalidOperationException("来源记录已改变或当前仅为未保存预览，请刷新分析后重新导出。");
        return dependency with { Sha256 = expectedHash };
    }

    public static ArtifactDependency Object(string root, string path, string[] fields)
    {
        var d = new ArtifactDependency("object", Relative(root, path), "", Fields: fields);
        return d with { Sha256 = ObjectHash(root, d) };
    }

    private static string ObjectHash(string root, ArtifactDependency d)
    {
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(Resolve(root, d.Path)));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("来源配置不是对象。");
        return Digest(Project(doc.RootElement, d.Fields));
    }

    private static string RecordsHash(string root, ArtifactDependency d)
    {
        string path = Resolve(root, d.Path);
        using var doc = JsonDocument.Parse(System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "[]");
        return HashRows(doc.RootElement, d);
    }

    private static JsonElement Project(JsonElement row, string[]? fields) =>
        fields is not { Length: > 0 } ? row : JsonSerializer.SerializeToElement(fields
            .ToDictionary(x => x, x => ValueAt(row, x)));

    private static JsonElement ValueAt(JsonElement row, string field)
    {
        foreach (string segment in field.Split('.'))
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(segment, out var next))
                return JsonSerializer.SerializeToElement<object?>(null);
            row = next;
        }
        return row;
    }

    private static bool MatchesTimestampDate(JsonElement row, ArtifactDependency d)
    {
        if (d.TimestampDateField.Length == 0) return true;
        return row.TryGetProperty(d.TimestampDateField, out var field) &&
            DateTimeOffset.TryParse(field.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var stamp) &&
            TimeZoneInfo.ConvertTime(stamp, TimeZoneInfo.FindSystemTimeZoneById(d.TimeZoneId)).ToString("yyyy-MM-dd") == d.DateEquals;
    }

    private static string HashRows(JsonElement rows, ArtifactDependency d)
    {
        if (rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException("来源记录不是数组。");
        var selected = rows.EnumerateArray().Where(row =>
            (d.EqualsFields is null || d.EqualsFields.All(pair => row.TryGetProperty(pair.Key, out var v) &&
                v.ToString().Equals(pair.Value, StringComparison.OrdinalIgnoreCase))) &&
            MatchesTimestampDate(row, d) &&
            (d.CutoffDate.Length == 0 || (row.TryGetProperty("date", out var date) &&
                string.CompareOrdinal(date.GetString(), d.CutoffDate) <= 0)))
            .Select(row => Canonical(Project(row, d.Fields))).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (selected.Length < d.MinimumCount) throw new InvalidDataException("来源记录已缺失。");
        return Digest(JsonSerializer.SerializeToElement(selected));
    }

    public static ArtifactDependencyStatus Check(string root, IReadOnlyList<ArtifactDependency>? dependencies,
        IDictionary<string, string>? fileCache = null)
    {
        if (dependencies is not { Count: > 0 }) return new("UNVERIFIED", "缺少来源依赖证据。");
        string? unknown = null;
        foreach (ArtifactDependency d in dependencies)
        {
            try
            {
                if (d.Sha256 is not { Length: 64 } || !d.Sha256.All(Uri.IsHexDigit))
                    return new("UNVERIFIED", "来源指纹无效。");
                string path = Resolve(root, d.Path);
                string actual;
                if (d.Kind == "file")
                {
                    // File.Exists also returns false on permission failures. Opening distinguishes them.
                    if (fileCache is not null && fileCache.TryGetValue(path, out string? cached)) actual = cached;
                    else
                    {
                        actual = GeneratedArtifactIdentity.Hash(path);
                        if (fileCache is not null) fileCache[path] = actual;
                    }
                }
                else if (d.Kind is "field_files" or "daily_files") actual = SourceSetHash(root, d);
                else if (d.Kind == "exists") actual = Digest(System.IO.File.Exists(path));
                else if (d.Kind == "artifact_reference") actual = Digest(ReferenceExists(root, d.Path));
                else if (d.Kind == "records") actual = RecordsHash(root, d);
                else if (d.Kind == "object") actual = ObjectHash(root, d);
                else return new("UNVERIFIED", "不支持的来源证据类型。");
                if (!actual.Equals(d.Sha256, StringComparison.OrdinalIgnoreCase))
                    return new("STALE", $"来源已变化：{d.Path}");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // External volumes can be offline; absence there is not proof of deletion.
                if (System.IO.Path.IsPathRooted(d.Path)) unknown = $"外部来源离线或缺失：{d.Path}";
                else return new("STALE", $"来源已缺失：{d.Path}");
            }
            catch (InvalidDataException ex) { return new("STALE", ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException)
            { unknown = $"无法核验来源：{d.Path}（{ex.GetType().Name}）"; }
        }
        return unknown is null ? new("CURRENT", "内容与所记录来源一致。") : new("UNVERIFIED", unknown);
    }

    public static string? FindRoot(string artifactPath)
    {
        string? directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(artifactPath));
        while (directory is not null && !ProjectPaths.HasProject(directory))
            directory = Directory.GetParent(directory)?.FullName;
        return directory;
    }
}
