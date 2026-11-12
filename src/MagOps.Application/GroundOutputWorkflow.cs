using System.Globalization;
using System.Text;
using System.Text.Json;
using MagOps.Infrastructure.Services;

namespace MagOps.Application;

public sealed record GroundPointStatistics(string RawSessionId, string SerialNumber, string State,
    string LineToken, string StationToken, string StationDirection, int Count, int CorrectedCount,
    int Non99SqCount, double RawMeanNt, double RawMedianNt, double? DifferenceMeanNt,
    double? DifferenceMedianNt, double? AdjustedMeanNt, double? AdjustedMedianNt);

public sealed record GroundOutputResult(string RunId, string OutputDirectory, string Status,
    string InterpretationFingerprint, int RecordCount, int PointGroupCount, IReadOnlyList<string> Files)
{
    public bool ProductionEligible => false;
    public bool RequiresReview => true;
}

/// <summary>Publishes an immutable review bundle. No guessing time, background or production eligibility.</summary>
public sealed class GroundOutputWorkflow
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public ApplicationResult<GroundOutputResult> Export(string input, string? mappingPath, string output,
        GeneratedArtifactContext? artifactContext = null, CancellationToken cancellation = default)
    {
        string run = "ground-run-" + Guid.NewGuid().ToString("N");
        string? staging = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("需要新的输出目录。");
            output = Path.GetFullPath(output);
            if (File.Exists(output) || Directory.Exists(output)) throw new ArgumentException("输出目录已存在；请选择新目录，避免覆盖历史结果。");
            string rawRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
            if (output.Equals(rawRoot, StringComparison.OrdinalIgnoreCase) || output.StartsWith(rawRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("输出目录不能位于原始输入目录内。");
            var loaded = GroundMagInspection.Load(input, mappingPath, cancellation);
            cancellation.ThrowIfCancellationRequested();
            var trial = GroundDiurnalWorkflow.Calculate(loaded, true, cancellation);
            if (trial.SelectedRoverRecordCount == 0) throw new ArgumentException("没有明确选择的移动记录，请先检查并填写映射/选择配置。");
            var records = trial.Records!;
            var statistics = Statistics(records);
            string parent = Path.GetDirectoryName(output)!;
            Directory.CreateDirectory(parent);
            staging = Path.Combine(parent, "." + Path.GetFileName(output) + "." + run + ".pending");
            Directory.CreateDirectory(staging);
            WriteJson("trial.json", trial);
            WriteJson("mapping.json", loaded.Configuration);
            WriteJson("sources.json", loaded.Input.Sources.Select(s => s with { Sessions = [] }));
            WriteJson("raw-sessions.json", loaded.Input);
            WriteJson("point-statistics.json", statistics);
            WriteCsv("observations.csv", records);
            WriteCsv("point-statistics.csv", statistics);
            var paths = Directory.GetFiles(staging).Order(StringComparer.Ordinal).ToArray();
            var sourcePaths = loaded.Input.Sources.Select(s => s.Path).ToArray();
            var lineage = new GeneratedArtifactLineage(loaded.Input.Sources.Select(s => s.SourceId).ToArray(),
                loaded.Resolution.Sessions.Select(s => s.Session.RawSessionId).ToArray(), [], [], []);
            foreach (string path in paths)
            {
                cancellation.ThrowIfCancellationRequested();
                GeneratedArtifactIdentity.Write(path, "derived", "ground_trial", trial.InterpretationFingerprint,
                    sourcePaths, qualityStatus: "REQUIRES_REVIEW", context: artifactContext, lineage: lineage);
            }
            // Check exact bytes again before commit: the bundle may not describe a source that changed mid-run.
            foreach (var source in loaded.Input.Sources)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!GeneratedArtifactIdentity.Hash(source.Path).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("源文件在计算期间发生变化，请重新读取：" + source.Path);
            }
            var files = Directory.GetFiles(staging).Order(StringComparer.Ordinal).Select(p => new
                { name = Path.GetFileName(p), sha256 = GeneratedArtifactIdentity.Hash(p) }).ToArray();
            WriteJson("run.json", new { schemaVersion = 1, runId = run, createdAtUtc = DateTimeOffset.UtcNow,
                status = trial.Status, productionEligible = false, requiresReview = true,
                trial.InterpretationFingerprint, trial.AlgorithmVersion, context = artifactContext,
                statisticsBasis = "session + instrument + selection-state + line-token + station-token + station-direction; SQ retained; no background interpolation",
                files });
            cancellation.ThrowIfCancellationRequested();
            Directory.Move(staging, output);
            staging = null;
            return ApplicationResult<GroundOutputResult>.Success(run, null,
                new(run, output, trial.Status, trial.InterpretationFingerprint, records.Count, statistics.Count,
                    files.Select(f => Path.Combine(output, f.name)).Append(Path.Combine(output, "run.json")).ToArray()));

            void WriteJson(string name, object data) => File.WriteAllText(Path.Combine(staging!, name), JsonSerializer.Serialize(data, Json), new UTF8Encoding(false));
            void WriteCsv<T>(string name, IReadOnlyList<T> rows)
            {
                var props = typeof(T).GetProperties();
                using var writer = new StreamWriter(Path.Combine(staging!, name), false, new UTF8Encoding(true));
                writer.WriteLine(string.Join(",", props.Select(p => Csv(p.Name))));
                foreach (T row in rows)
                {
                    cancellation.ThrowIfCancellationRequested();
                    writer.WriteLine(string.Join(",", props.Select(p => Csv(p.GetValue(row)))));
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or JsonException or OperationCanceledException)
        {
            return ApplicationResult<GroundOutputResult>.Failure(run, new(ex is OperationCanceledException ? "CANCELLED" :
                ex is IOException or UnauthorizedAccessException ? "ENVIRONMENT_ERROR" : "VALIDATION_ERROR", ex.Message));
        }
        finally
        {
            // Only our unique staging directory is eligible for cleanup; never touch the requested final target.
            if (staging is not null && Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    public static IReadOnlyList<GroundPointStatistics> Statistics(IReadOnlyList<GroundDiurnalAuditRow> rows) => rows
        .Where(r => !string.IsNullOrWhiteSpace(r.LineToken) && !string.IsNullOrWhiteSpace(r.StationToken))
        .GroupBy(r => (r.RawSessionId, r.SerialNumber, r.State, r.LineToken, r.StationToken, r.StationDirection))
        .Select(g => new GroundPointStatistics(g.Key.RawSessionId, g.Key.SerialNumber, g.Key.State,
            g.Key.LineToken!, g.Key.StationToken!, g.Key.StationDirection ?? "", g.Count(),
            g.Count(r => r.DiurnalDifferenceNt.HasValue), g.Count(r => r.Sq != 99),
            g.Average(r => r.RawMagNt), Median(g.Select(r => r.RawMagNt))!.Value,
            Mean(g.Select(r => r.DiurnalDifferenceNt)), Median(g.Select(r => r.DiurnalDifferenceNt)),
            Mean(g.Select(r => r.DatumAdjustedNt)), Median(g.Select(r => r.DatumAdjustedNt)))).ToArray();

    private static double? Mean(IEnumerable<double?> values) { var a = values.Where(v => v.HasValue).Select(v => v!.Value).ToArray(); return a.Length == 0 ? null : a.Average(); }
    private static double? Median(IEnumerable<double> values) => Median(values.Select(v => (double?)v));
    private static double? Median(IEnumerable<double?> values)
    { var a = values.Where(v => v.HasValue).Select(v => v!.Value).Order().ToArray(); return a.Length == 0 ? null : a.Length % 2 == 1 ? a[a.Length / 2] : a[a.Length / 2 - 1] / 2 + a[a.Length / 2] / 2; }
    private static string Csv(object? value)
    {
        string text = value switch { null => "", DateTimeOffset date => date.ToString("O"),
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            IEnumerable<string> list => string.Join(";", list), _ => value.ToString() ?? "" };
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
}
