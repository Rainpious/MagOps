using MagOps.Core.Diurnal;
using MagOps.Core.GroundMag;
using MagOps.Infrastructure.Services;
using System.Security.Cryptography;
using System.Text;

namespace MagOps.Application;

public sealed record GroundDiurnalAuditRow(
    string RawSessionId, string SourceFile, int PhysicalLine, int SourceRecord,
    string SerialNumber, string State, string? LineToken, string? StationToken, string? StationDirection,
    string ClockText, DateTimeOffset? TimestampUtc, string TimeStatus,
    double RawMagNt, double? GradientNtPerM, double? Sq, double? SourceCorrectionNt,
    double? BaseUsedNt, double? DiurnalDifferenceNt, double? ReferenceFieldNt, double? DatumAdjustedNt,
    string? BaseSessionId, DateTimeOffset? BasePreviousUtc, DateTimeOffset? BaseNextUtc,
    int? BasePreviousPhysicalLine, int? BaseNextPhysicalLine, string CorrectionMode, IReadOnlyList<string> Reasons);

public sealed record GroundDiurnalTrialResult(
    string Status, string InterpretationFingerprint, string AlgorithmVersion,
    int SelectedRoverRecordCount, int CorrectedRecordCount, int UncorrectedRecordCount,
    int ExcludedRoverRecordCount, IReadOnlyList<string> BlockedReasons,
    DiurnalBaseValiditySummary? BaseValidity, IReadOnlyList<Gsm19TCanonicalSample>? BaseAudit,
    IReadOnlyList<GroundDiurnalAuditRow>? Records, GsmGroundConfiguration Configuration,
    IReadOnlyList<GsmDiagnostic> InputDiagnostics)
{
    public bool ReadOnly => true;
    public bool ProductionEligible => false;
    public bool RequiresReview => true;
}

/// <summary>Independent, read-only ground trial. Does not publish/register production artifacts or require flight identities.</summary>
public sealed class GroundDiurnalWorkflow
{
    public const string TrialAlgorithmVersion = "ground-diurnal-trial-1";
    public ApplicationResult<GroundDiurnalTrialResult> Trial(string input, string? mappingPath, bool includeRecords = false, CancellationToken cancellation = default)
    {
        string operation = "op-" + Guid.NewGuid().ToString("N");
        try
        {
            var context = GroundMagInspection.Load(input, mappingPath, cancellation);
            return ApplicationResult<GroundDiurnalTrialResult>.Success(operation, null, Calculate(context, includeRecords, cancellation));
        }
        catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return ApplicationResult<GroundDiurnalTrialResult>.Failure(operation,
                new(ex is IOException or UnauthorizedAccessException ? ApplicationErrorCodes.EnvironmentError : ApplicationErrorCodes.ValidationError, ex.Message));
        }
    }

    internal static GroundDiurnalTrialResult Calculate(GroundMagInspection.GroundInputContext context, bool includeRecords, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var config = context.Configuration;
        if (config.ReferenceFieldNt.HasValue && (!double.IsFinite(config.ReferenceFieldNt.Value) ||
            string.IsNullOrWhiteSpace(config.ReferenceFieldEvidence)))
            throw new ArgumentException("A finite reference field requires explicit method/source evidence; it is not T0.");
        var blocked = new List<string>();
        var selectedBases = context.Resolution.Sessions.Where(s => config.BaseSessionIds.Contains(s.Session.RawSessionId)).ToArray();
        var roverSessions = context.Resolution.Sessions.Where(s => s.Session.SurveyType == "g").ToArray();
        var selectedRovers = roverSessions.SelectMany(s => s.Observations.Where(r =>
            r.SelectionState is "normal" or "after-removal").Select(r => (Session: s.Session, Row: r))).ToArray();
        int allRovers = roverSessions.Sum(s => s.Observations.Count);
        if (selectedBases.Length == 0) blocked.Add("EXPLICIT_BASE_SESSION_REQUIRED");
        if (selectedRovers.Length == 0) blocked.Add("EXPLICIT_ROVER_SELECTION_REQUIRED");
        if (selectedBases.Any(s => s.Observations.Any(r => !r.TimestampUtc.HasValue)))
            blocked.Add("SELECTED_BASE_TIME_UNRESOLVED");
        bool IsParseError(GsmDiagnostic d) => d.Code is "MALFORMED_DATA_ROW" or "UNPARSED_DATA_ROW" or "UNSUPPORTED_COLUMN_MAP";
        if (selectedBases.Any(s => context.Input.Diagnostics.Any(d => IsParseError(d) && d.SourceFile == s.Session.SourceFile &&
                d.PhysicalLine >= s.Session.HeaderLine && d.PhysicalLine < context.Resolution.Sessions
                    .Where(next => next.Session.SourceFile == s.Session.SourceFile && next.Session.HeaderLine > s.Session.HeaderLine)
                    .Select(next => next.Session.HeaderLine).DefaultIfEmpty(int.MaxValue).Min())))
            blocked.Add("SELECTED_BASE_PARSE_ERRORS");
        if (config.Selections.Where(s => s.State is "normal" or "after-removal").Any(s =>
            context.Input.Diagnostics.Any(d => IsParseError(d) && d.SourceFile == context.Resolution.Sessions
                .Single(session => session.Session.RawSessionId == s.RawSessionId).Session.SourceFile &&
                d.PhysicalLine >= s.FirstPhysicalLine && d.PhysicalLine <= s.LastPhysicalLine)))
            blocked.Add("SELECTED_ROVER_PARSE_ERRORS");

        var baseRows = selectedBases.SelectMany(s => s.Observations.Where(r => r.TimestampUtc.HasValue).Select(r =>
            new Gsm19TCanonicalSample(r.TimestampUtc!.Value, r.Observation.MagneticFieldNt, r.Observation.Sq,
                s.Session.SourceFile, r.Observation.PhysicalLine, [])
            {
                SourceFileStableId = s.Session.SourceId, SessionStableId = s.Session.RawSessionId
            })).ToArray();
        var flagged = FieldMagDataService.EvaluateSelectedBaseQuality(baseRows);
        var validity = DiurnalBaseValidityGate.Evaluate(flagged);
        if (validity.AcceptedRecords == 0) blocked.Add("NO_ELIGIBLE_BASE");
        // An unresolved selected Base prevents silently correcting with only its resolvable remainder.
        var eligible = blocked.Any(b => b is "SELECTED_BASE_TIME_UNRESOLVED" or "SELECTED_BASE_PARSE_ERRORS" or "SELECTED_ROVER_PARSE_ERRORS")
            ? [] : DiurnalBaseValidityGate.Accepted(flagged);
        var segments = new List<Gsm19TCanonicalSample[]>();
        foreach (var session in eligible.GroupBy(r => r.SessionStableId))
        {
            var segment = new List<Gsm19TCanonicalSample>();
            foreach (var row in session.OrderBy(r => r.TimestampUtc))
            {
                if (segment.Count > 0 && (row.TimestampUtc - segment[^1].TimestampUtc).TotalSeconds > 999)
                { segments.Add(segment.ToArray()); segment.Clear(); }
                segment.Add(row);
            }
            if (segment.Count > 0) segments.Add(segment.ToArray());
        }

        var assigned = new Dictionary<(string Session, int Line), int>();
        var reasonByRow = new Dictionary<(string Session, int Line), List<string>>();
        foreach (var item in selectedRovers)
        {
            cancellation.ThrowIfCancellationRequested();
            var key = (item.Session.RawSessionId, item.Row.Observation.PhysicalLine);
            var reasons = new List<string>(item.Row.Reasons);
            reasonByRow.Add(key, reasons);
            if (!item.Row.TimestampUtc.HasValue) { reasons.Add("ROVER_TIME_UNRESOLVED"); continue; }
            if (item.Row.Observation.CorrectionNt is not (null or 0)) { reasons.Add("SOURCE_CORRECTION_REVIEW_REQUIRED"); continue; }
            var matches = Enumerable.Range(0, segments.Count).Where(i => item.Row.TimestampUtc >= segments[i][0].TimestampUtc &&
                item.Row.TimestampUtc <= segments[i][^1].TimestampUtc).ToArray();
            if (matches.Length != 1) { reasons.Add(matches.Length == 0 ? "NO_BASE_COVERAGE" : "AMBIGUOUS_BASE_COVERAGE"); continue; }
            assigned.Add(key, matches[0]);
        }
        var corrections = new Dictionary<(string Session, int Line), (DiurnalCorrectedSample Row, string BaseId)>();
        var engine = new DiurnalCorrectionEngine();
        for (int index = 0; index < segments.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            var members = selectedRovers.Where(r => assigned.TryGetValue((r.Session.RawSessionId, r.Row.Observation.PhysicalLine), out int chosen) && chosen == index).ToArray();
            if (members.Length == 0) continue;
            var result = engine.Correct(members.Select(r => new DiurnalRoverSample(r.Row.TimestampUtc!.Value,
                    r.Row.Observation.MagneticFieldNt, r.Session.SourceFile, r.Row.Observation.PhysicalLine)).ToArray(),
                segments[index].Select(r => new DiurnalBaseSample(r.TimestampUtc, r.MagneticFieldNt, r.SourceFile, r.SourceRecord)).ToArray(),
                new DiurnalCorrectionOptions(config.ReferenceFieldNt ?? 0, true, 999, true));
            var sessionByPath = members.Select(m => m.Session).DistinctBy(s => s.RawSessionId)
                .SelectMany(s => s.Observations.Select(r => ((s.SourceFile, r.PhysicalLine), s.RawSessionId)))
                .ToDictionary(p => p.Item1, p => p.RawSessionId);
            foreach (var row in result.Samples)
                corrections.Add((sessionByPath[(row.RoverSourceFile, row.RoverSourceRow)], row.RoverSourceRow),
                    (row, segments[index][0].SessionStableId));
        }
        var audit = new List<GroundDiurnalAuditRow>();
        foreach (var item in selectedRovers)
        {
            cancellation.ThrowIfCancellationRequested();
            var raw = item.Row.Observation;
            var key = (item.Session.RawSessionId, raw.PhysicalLine);
            var reasons = reasonByRow[key];
            DiurnalCorrectedSample? correction = null;
            string? baseId = null;
            if (corrections.TryGetValue(key, out var pair)) { correction = pair.Row; baseId = pair.BaseId; reasons.AddRange(correction.QualityFlags); }
            if (item.Row.TimeStatus == "assumed" || selectedBases.Any(b => b.Session.RawSessionId == baseId && b.Observations.Any(r => r.TimeStatus == "assumed")))
                reasons.Add("ASSUMED_TIME_MAPPING");
            audit.Add(new(item.Session.RawSessionId, item.Session.SourceFile, raw.PhysicalLine, raw.SourceRecord,
                item.Session.SerialNumber, item.Row.SelectionState, raw.LineToken, raw.StationToken, raw.StationDirection,
                raw.ClockText, item.Row.TimestampUtc, item.Row.TimeStatus, raw.MagneticFieldNt, raw.GradientNtPerM,
                raw.Sq, raw.CorrectionNt, correction?.BaseUsedNt, correction?.DiurnalDifferenceNt,
                config.ReferenceFieldNt, config.ReferenceFieldNt.HasValue ? correction?.DatumAdjustedNt : null,
                baseId, correction?.BasePreviousUtc, correction?.BaseNextUtc,
                correction?.BasePreviousSourceRow, correction?.BaseNextSourceRow,
                correction?.Mode.ToString() ?? "Uncorrected", reasons.Distinct().ToArray()));
        }
        int corrected = audit.Count(r => r.DiurnalDifferenceNt.HasValue);
        string status = corrected == 0 ? "BLOCKED_REVIEW" : corrected == audit.Count ? "TRIAL_COMPLETE" : "PARTIAL_TRIAL";
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|",
            context.Fingerprint, TrialAlgorithmVersion, DiurnalCorrectionEngine.AlgorithmVersion,
            FieldMagDataService.IncrementalCacheParserVersion)))).ToLowerInvariant();
        return new(status, fingerprint, TrialAlgorithmVersion + "/" + DiurnalCorrectionEngine.AlgorithmVersion,
            audit.Count, corrected, audit.Count - corrected, allRovers - audit.Count, blocked, validity,
            includeRecords ? flagged : null, includeRecords ? audit : null, config, context.Input.Diagnostics);
    }
}
