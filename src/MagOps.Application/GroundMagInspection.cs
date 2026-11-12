using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MagOps.Core.GroundMag;
using MagOps.Infrastructure.Services;

namespace MagOps.Application;

public sealed record GroundSessionInspection(
    string RawSessionId, string SourceId, string SourceSha256, string SourceFile,
    int HeaderLine, string HeaderText, string InstrumentModel, string SerialNumber,
    string SurveyType, string AcquisitionDateText, DateOnly? AcquisitionDate,
    string ColumnHeader, double? SensorDistanceCm, int RecordCount, int FirstPhysicalLine, int LastPhysicalLine,
    DateTimeOffset? FirstTimeUtc, DateTimeOffset? LastTimeUtc,
    int ConfirmedTimeCount, int AssumedTimeCount, int UnresolvedTimeCount,
    IReadOnlyDictionary<string, int> SelectionCounts, IReadOnlyList<GsmResolvedObservation>? Records);

public sealed record GroundMagInspectionResult(
    string ParserVersion, string TimeAlgorithmVersion, string InterpretationFingerprint,
    int SourceCount, int ParsedRecordCount, int BaseRecordCount, int RoverRecordCount,
    int ResolvedTimeRecordCount, int AssumedTimeRecordCount, int UnresolvedTimeRecordCount,
    IReadOnlyList<GroundSessionInspection> Sessions,
    IReadOnlyList<GsmRawSource> SourceCatalog, IReadOnlyList<GsmDiagnostic> Diagnostics,
    GsmGroundConfiguration Configuration)
{
    public bool ReadOnly => true;
    public bool ProductionEligible => false;
    public bool RequiresReview => ParsedRecordCount == 0 || UnresolvedTimeRecordCount > 0 ||
        AssumedTimeRecordCount > 0 || Diagnostics.Count > 0 || Sessions.Any(s => s.SelectionCounts.ContainsKey("unknown"));
}

public sealed class GroundMagInspection
{
    internal static readonly JsonSerializerOptions ConfigurationJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public ApplicationResult<GroundMagInspectionResult> Inspect(string input, string? mappingPath = null, bool includeRecords = false, CancellationToken cancellation = default)
    {
        string operation = "op-" + Guid.NewGuid().ToString("N");
        try
        {
            var loaded = Load(input, mappingPath, cancellation);
            return ApplicationResult<GroundMagInspectionResult>.Success(operation, null,
                Summarize(loaded.Input, loaded.Resolution, loaded.Configuration, loaded.Fingerprint, includeRecords));
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or IOException or UnauthorizedAccessException)
        {
            return ApplicationResult<GroundMagInspectionResult>.Failure(operation, new(
                ex is IOException or UnauthorizedAccessException ? ApplicationErrorCodes.EnvironmentError : ApplicationErrorCodes.ValidationError,
                ex.Message));
        }
    }

    internal static GroundInputContext Load(string input, string? mappingPath, CancellationToken cancellation = default)
    {
        var raw = new GsmGroundInputReader().ReadFolder(input, cancellation);
        var configuration = string.IsNullOrWhiteSpace(mappingPath) ? new GsmGroundConfiguration() :
            JsonSerializer.Deserialize<GsmGroundConfiguration>(File.ReadAllText(mappingPath), ConfigurationJson)
                ?? throw new ArgumentException("Ground mapping JSON must contain an object.");
        var resolution = new GsmSessionTimeResolver().Resolve(raw, configuration);
        string evidence = string.Join("\n", raw.Sources.Select(s => s.Sha256).Order(StringComparer.Ordinal)) + "\n" +
            raw.ParserVersion + "\n" + GsmSessionTimeResolver.AlgorithmVersion + "\n" +
            JsonSerializer.Serialize(configuration, ConfigurationJson);
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence))).ToLowerInvariant();
        return new(raw, configuration, resolution, fingerprint);
    }

    private static GroundMagInspectionResult Summarize(GsmGroundInput input, GsmTimeResolution resolution,
        GsmGroundConfiguration configuration, string fingerprint, bool records)
    {
        var sessions = resolution.Sessions.Select(s => new GroundSessionInspection(
            s.Session.RawSessionId, s.Session.SourceId, s.Session.SourceSha256, s.Session.SourceFile,
            s.Session.HeaderLine, s.Session.HeaderText, s.Session.InstrumentModel, s.Session.SerialNumber,
            s.Session.SurveyType, s.Session.AcquisitionDateText, s.Session.AcquisitionDate,
            s.Session.ColumnHeader, s.Session.SensorDistanceCm, s.Observations.Count,
            s.Observations[0].Observation.PhysicalLine, s.Observations[^1].Observation.PhysicalLine,
            s.Observations.Min(r => r.TimestampUtc), s.Observations.Max(r => r.TimestampUtc),
            s.Observations.Count(r => r.TimeStatus == "confirmed"), s.Observations.Count(r => r.TimeStatus == "assumed"),
            s.Observations.Count(r => r.TimeStatus == "unresolved"),
            s.Observations.GroupBy(r => r.SelectionState).ToDictionary(g => g.Key, g => g.Count()),
            records ? s.Observations : null)).ToArray();
        // Catalog contains identities/aliases, not another copy of all observation rows.
        var catalog = input.Sources.Select(s => s with { Sessions = [] }).ToArray();
        return new(input.ParserVersion, GsmSessionTimeResolver.AlgorithmVersion, fingerprint, input.Sources.Count,
            resolution.ParsedRecordCount, sessions.Where(s => s.SurveyType == "b").Sum(s => s.RecordCount),
            sessions.Where(s => s.SurveyType == "g").Sum(s => s.RecordCount), resolution.ResolvedTimeRecordCount,
            resolution.AssumedTimeRecordCount, resolution.UnresolvedTimeRecordCount, sessions, catalog,
            input.Diagnostics.Concat(resolution.Diagnostics).GroupBy(d => (d.Code, d.SourceFile, d.Message))
                .Select(g => g.First() with { PhysicalLine = g.Min(d => d.PhysicalLine),
                    LastPhysicalLine = g.Max(d => d.LastPhysicalLine), Occurrences = g.Sum(d => d.Occurrences) }).ToArray(), configuration);
    }

    internal sealed record GroundInputContext(GsmGroundInput Input, GsmGroundConfiguration Configuration,
        GsmTimeResolution Resolution, string Fingerprint);
}
