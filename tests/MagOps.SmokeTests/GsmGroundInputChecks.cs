using System.Text.Json;
using MagOps.Application;
using MagOps.Core.GroundMag;
using MagOps.Infrastructure.Services;

internal static class GsmGroundInputChecks
{
    internal static void InputAndTime()
    {
        WithFolder(folder =>
        {
            string basePath = Path.Combine(folder, "base.txt");
            File.WriteAllText(basePath, Base("27 X 26", "135000.0 54500 99\n135010.0 54510 99\n135020.0 54520 99"));
            File.WriteAllText(Path.Combine(folder, "mobile.txt"), Mobile("27 X 26",
                "00003N 0000020 N 55000 -2 49 0 135005.0\n00003N 0000020 N 55002 -3 99 0 135005.0\n00003N 0000040 N 55020 1 99 0 135015.0") + "\n/ID 1\n");
            File.Copy(basePath, Path.Combine(folder, "copy.txt"));
            var raw = new GsmGroundInputReader().ReadFolder(folder);
            Check(raw.Sources.Count == 2 && raw.Sources.Sum(s => s.Sessions.Sum(b => b.Observations.Count)) == 6, "source alias or repeated observation was mishandled");
            Check(raw.Diagnostics.Count(d => d.Code == "EMPTY_SESSION_HEADER") == 1 && raw.Diagnostics.Any(d => d.Code == "DUPLICATE_FILE_ALIAS"), "empty tail / alias diagnosis missing");
            var mobile = raw.Sources.SelectMany(s => s.Sessions).Single(s => s.SurveyType == "g");
            Check(mobile.Observations[0].Sq == 49 && mobile.Observations[0].GradientNtPerM == -2 && mobile.Observations[0].LineToken == "00003N", "raw channels or local tokens changed");
            var resolver = new GsmSessionTimeResolver();
            var unknown = resolver.Resolve(raw, new());
            Check(unknown.UnresolvedTimeRecordCount == 6 && unknown.Sessions.All(s => s.Observations.All(r => r.TimestampUtc is null)), "unknown clock became UTC");
            var mappings = raw.Sources.SelectMany(s => s.Sessions).Select(s => Map(s)).ToArray();
            var config = new GsmGroundConfiguration { TimeMappings = mappings, Selections = [Select(mobile)] };
            var resolved = resolver.Resolve(raw, config);
            Check(resolved.AssumedTimeRecordCount == 6 && resolved.ResolvedTimeRecordCount == 0, "assumed mappings promoted");
            Check(resolved.Sessions.Single(s => s.Session.SurveyType == "g").Observations[0].TimestampUtc ==
                new DateTimeOffset(2026, 10, 27, 5, 50, 5, TimeSpan.Zero), "timezone or clock conversion wrong");
            var confirmed = resolver.Resolve(raw, config with { TimeMappings = mappings.Select(m => m with { Status = "confirmed", ClockOffsetSeconds = 2 }).ToArray() });
            Check(confirmed.ResolvedTimeRecordCount == 6 && confirmed.AssumedTimeRecordCount == 0, "confirmed time counts wrong");
            Check(confirmed.Sessions[0].Session.RawSessionId == resolved.Sessions[0].Session.RawSessionId, "date correction changes raw identity");
            MustReject(() => resolver.Resolve(raw, config with { TimeMappings = [mappings[0], mappings[0]] }));
            MustReject(() => resolver.Resolve(raw, config with { TimeMappings = [mappings[0] with { SourceSha256 = "stale" }] }));
            MustReject(() => resolver.Resolve(raw, config with { TimeMappings = [mappings[0] with { ClockOffsetSeconds = null }] }));
            MustReject(() => resolver.Resolve(raw, config with { TimeMappings = [mappings[0] with { UtcOffsetMinutes = 900 }] }));
            MustReject(() => resolver.Resolve(raw, config with { Selections = [Select(mobile), Select(mobile)] }));
            MustReject(() => resolver.Resolve(raw, config with { Selections = [Select(mobile) with { LastPhysicalLine = 9999 }] }));
            var inspection = new GroundMagInspection().Inspect(folder, includeRecords: true);
            Check(inspection.Ok && inspection.Result!.ParsedRecordCount == 6 && inspection.Result.UnresolvedTimeRecordCount == 6 &&
                inspection.Result.ProductionEligible == false && inspection.Result.SourceCatalog.All(s => s.Sessions.Count == 0), "inspection contract incorrect");
            string configPath = WriteConfig(folder, config);
            var mapped = new GroundMagInspection().Inspect(folder, configPath);
            Check(mapped.Ok && mapped.Result!.InterpretationFingerprint != inspection.Result!.InterpretationFingerprint, "mapping missing from fingerprint");
            File.WriteAllText(configPath, "{\"schemaVersion\":1,\"timeMapings\":[]}");
            Check(!new GroundMagInspection().Inspect(folder, configPath).Ok, "unknown mapping property ignored");
            File.WriteAllText(configPath, "{\"selections\":[null]}");
            Check(!new GroundMagInspection().Inspect(folder, configPath).Ok, "null selection did not return structured failure");
            File.Move(Path.Combine(folder, "mobile.txt"), Path.Combine(folder, "renamed.txt"));
            Check(new GsmGroundInputReader().ReadFolder(folder).Sources.SelectMany(s => s.Sessions)
                .Any(s => s.RawSessionId == mobile.RawSessionId), "rename changed raw session ID");
            File.AppendAllText(basePath, "\n");
            File.AppendAllText(Path.Combine(folder, "copy.txt"), "\n");
            // Source bytes changed; old mappings cannot silently follow a changed acquisition version.
            Check(!new GroundMagInspection().Inspect(folder, WriteConfig(folder, config)).Ok, "changed bytes accepted stale mapping");
        });
        WithFolder(folder =>
        {
            File.WriteAllText(Path.Combine(folder, "input.txt"), Base("27 X 26", "235959.0 54500 99\n240000.0 54501 99\n000010.0 54502 99") +
                "\n" + Base("28 X 26", "120000.0 54503 99\n115900.0 54504 99\n120100.0 54505 99"));
            var raw = new GsmGroundInputReader().ReadFolder(folder);
            var sessions = raw.Sources.Single().Sessions;
            var config = new GsmGroundConfiguration { TimeMappings = sessions.Select(s => Map(s) with { UtcOffsetMinutes = 0 }).ToArray() };
            var result = new GsmSessionTimeResolver().Resolve(raw, config);
            Check(result.Sessions[0].Observations[2].TimestampUtc == new DateTimeOffset(2026, 10, 28, 0, 0, 10, TimeSpan.Zero), "240000 rollover applied twice");
            Check(result.Sessions[1].Observations[0].TimestampUtc!.Value.Day == 28 && result.UnresolvedTimeRecordCount == 2, "cross-block state leak or clock reset guessed");
            Check(raw.Diagnostics.Any(d => d.Code == "MULTIPLE_ACQUISITION_DATES"), "mixed date evidence missing");
        });
        WithFolder(folder =>
        {
            string path = Path.Combine(folder, "input.txt");
            File.WriteAllText(path, Base("27 X 26", "235959.0 54500 99\n000010.0 54501 99"));
            var raw = new GsmGroundInputReader().ReadFolder(folder);
            var s = raw.Sources.Single().Sessions.Single();
            var c = new GsmGroundConfiguration { TimeMappings = [Map(s)] };
            Check(new GsmSessionTimeResolver().Resolve(raw, c).UnresolvedTimeRecordCount == 1, "midnight inferred without permission");
            Check(new GsmSessionTimeResolver().Resolve(raw, c with { TimeMappings = [Map(s) with { AllowMidnightRollover = true }] }).AssumedTimeRecordCount == 2, "explicit rollover policy failed");
            File.WriteAllText(path, Base("", "120000.0 54500 99\n120010.0 NaN 99\n120020.0 54520 99"));
            raw = new GsmGroundInputReader().ReadFolder(folder);
            Check(raw.Sources.Single().Sessions.Single().AcquisitionDate is null &&
                raw.Sources.Single().Sessions.Single().Observations[1].SourceRecord == 3 && raw.Diagnostics.Any(d => d.Code == "MALFORMED_DATA_ROW"), "missing date blocked raw input or malformed row silently renumbered");
            File.WriteAllText(path, Base("08 III 26", "023000.0 54500 99"));
            raw = new GsmGroundInputReader().ReadFolder(folder); s = raw.Sources.Single().Sessions.Single();
            var map = Map(s) with { UtcOffsetMinutes = null, TimeZoneId = "America/New_York" };
            Check(new GsmSessionTimeResolver().Resolve(raw, new() { TimeMappings = [map] }).UnresolvedTimeRecordCount == 1, "DST nonexistent time accepted");
            var fall = Map(s) with { UseHeaderDate = false, DateOverride = new DateOnly(2026, 11, 1),
                UtcOffsetMinutes = null, TimeZoneId = "America/New_York", ClockOffsetSeconds = -3600 };
            Check(new GsmSessionTimeResolver().Resolve(raw, new() { TimeMappings = [fall] }).UnresolvedTimeRecordCount == 1, "DST ambiguous time accepted");
        });
    }

    internal static void Diurnal()
    {
        WithFolder(folder =>
        {
            File.WriteAllText(Path.Combine(folder, "base.txt"), Base("27 X 26", "135000.0 54500 99\n135010.0 54510 99\n135020.0 54520 99"));
            File.WriteAllText(Path.Combine(folder, "mobile.txt"), Mobile("27 X 26",
                "00003N 0000020 N 55000 -2 49 0 135005.0\n00003N 0000020 N 55002 -3 99 0 135005.0\n00003N 0000040 N 55020 1 99 0 135015.0\n00000N 0000000 N 55000 5 99 0 140000.0"));
            var raw = new GsmGroundInputReader().ReadFolder(folder);
            var sessions = raw.Sources.SelectMany(s => s.Sessions).ToArray();
            var b = sessions.Single(s => s.SurveyType == "b"); var g = sessions.Single(s => s.SurveyType == "g");
            var config = new GsmGroundConfiguration { TimeMappings = sessions.Select(Map).ToArray(),
                BaseSessionIds = [b.RawSessionId], Selections = [Select(g) with { LastPhysicalLine = g.Observations[2].PhysicalLine }],
                ReferenceFieldNt = 54600, ReferenceFieldEvidence = "synthetic fixed reference" };
            var workflow = new GroundDiurnalWorkflow();
            var trial = workflow.Trial(folder, WriteConfig(folder, config), true);
            Check(trial.Ok && trial.Result!.CorrectedRecordCount == 3 && trial.Result.ExcludedRoverRecordCount == 1 && trial.Result.Status == "TRIAL_COMPLETE", "independent trial / duplicate rover preservation failed");
            var rows = trial.Result!.Records!;
            Check(rows[0].BaseUsedNt == 54505 && rows[0].DiurnalDifferenceNt == 495 && rows[0].DatumAdjustedNt == 55095 &&
                rows[0].GradientNtPerM == -2 && rows[0].Sq == 49 && rows[0].BasePreviousPhysicalLine == b.Observations[0].PhysicalLine,
                "formula, SQ treatment, gradient or trace wrong");
            Check(rows.All(r => r.Reasons.Contains("ASSUMED_TIME_MAPPING")) && !trial.Result.ProductionEligible, "trial promoted assumed time to production");
            var blocked = workflow.Trial(folder, null, true);
            Check(blocked.Ok && blocked.Result!.Status == "BLOCKED_REVIEW", "trial guessed missing configuration");
            var noReference = workflow.Trial(folder, WriteConfig(folder, config with { ReferenceFieldNt = null, ReferenceFieldEvidence = "" }), true);
            Check(noReference.Result!.Records!.All(r => r.DatumAdjustedNt is null), "default datum masqueraded as corrected total field");
            var missingTime = workflow.Trial(folder, WriteConfig(folder, config with { TimeMappings = [] }), true);
            Check(missingTime.Result!.CorrectedRecordCount == 0 && missingTime.Result.UncorrectedRecordCount == 3, "unknown times silently corrected");
            var uncovered = workflow.Trial(folder, WriteConfig(folder, config with { Selections = [Select(g)] }), true);
            Check(uncovered.Result!.CorrectedRecordCount == 3 && uncovered.Result.UncorrectedRecordCount == 1 &&
                uncovered.Result.Records!.Last().Reasons.Contains("NO_BASE_COVERAGE"), "out-of-range rover acquired fake value");
            var duplicateConfig = config with { BaseSessionIds = [b.RawSessionId, b.RawSessionId] };
            Check(!workflow.Trial(folder, WriteConfig(folder, duplicateConfig)).Ok, "duplicate Base selection accepted");
        });
        WithFolder(folder =>
        {
            File.WriteAllText(Path.Combine(folder, "base.txt"), Base("27 X 26", "135000.0 54500 99\n135000.0 54500 99\n135010.0 54510 99"));
            File.WriteAllText(Path.Combine(folder, "mobile.txt"), Mobile("27 X 26", "00003N 0000020 N 55000 -2 99 0 135005.0"));
            var raw = new GsmGroundInputReader().ReadFolder(folder); var sessions = raw.Sources.SelectMany(s => s.Sessions).ToArray();
            var config = new GsmGroundConfiguration { TimeMappings = sessions.Select(Map).ToArray(),
                BaseSessionIds = [sessions.Single(s => s.SurveyType == "b").RawSessionId], Selections = [Select(sessions.Single(s => s.SurveyType == "g"))] };
            var trial = new GroundDiurnalWorkflow().Trial(folder, WriteConfig(folder, config), true);
            Check(trial.Result!.CorrectedRecordCount == 0 && trial.Result.BaseAudit!.Count(r => r.QualityFlags.Contains("TIMESTAMP_CONFLICT")) == 2, "Base duplicate times silently deduplicated");
            var now = DateTimeOffset.UnixEpoch;
            var flagged = new[] { new Gsm19TCanonicalSample(now, 0, 0, "same", 1, ["BASE_SESSION_INVALID"]) { SessionStableId = "a" },
                new Gsm19TCanonicalSample(now, 0, 0, "same", 2, ["BASE_SESSION_INVALID"]) { SessionStableId = "b" } };
            Check(DiurnalBaseValidityGate.Evaluate(flagged).RejectedSessionIdentityCount == 2 &&
                DiurnalBaseValidityGate.Evaluate(flagged).RejectedSessions == 1, "true session count or legacy reporting compatibility lost");
        });
        WithFolder(folder =>
        {
            string bPath = Path.Combine(folder, "base.txt"), gPath = Path.Combine(folder, "mobile.txt");
            GroundDiurnalTrialResult Run()
            {
                var raw = new GsmGroundInputReader().ReadFolder(folder);
                var sessions = raw.Sources.SelectMany(s => s.Sessions).ToArray();
                var config = new GsmGroundConfiguration { TimeMappings = sessions.Select(s => Map(s) with { UtcOffsetMinutes = 0 }).ToArray(),
                    BaseSessionIds = sessions.Where(s => s.SurveyType == "b").Select(s => s.RawSessionId).ToArray(),
                    Selections = [Select(sessions.Single(s => s.SurveyType == "g"))] };
                var trial = new GroundDiurnalWorkflow().Trial(folder, WriteConfig(folder, config), true);
                Check(trial.Ok, "edge-case trial failed unexpectedly");
                return trial.Result!;
            }
            File.WriteAllText(bPath, Base("27 X 26", "135000.0 54500 99\n141000.0 54510 99"));
            File.WriteAllText(gPath, Mobile("27 X 26", "00003N 0000020 N 55000 -2 99 0 140000.0"));
            Check(Run().CorrectedRecordCount == 0, "trial interpolated across a long gap");
            File.WriteAllText(bPath, Base("27 X 26", "235959.0 54500 99\n240000.0 54510 99"));
            File.WriteAllText(gPath, Mobile("27 X 26", "00003N 0000020 N 55000 -2 99 0 235959.5"));
            var midnight = Run();
            Check(midnight.CorrectedRecordCount == 0 && midnight.Records!.Single().DiurnalDifferenceNt is null,
                "UTC-day isolation was bypassed");
            File.WriteAllText(bPath, Base("27 X 26", "135000.0 54500 99\n135010.0 54510 99") + "\n" +
                Base("27 X 26", "135002.0 54502 99\n135012.0 54512 99"));
            File.WriteAllText(gPath, Mobile("27 X 26", "00003N 0000020 N 55000 -2 99 0 135005.0"));
            Check(Run().Records!.Single().Reasons.Contains("AMBIGUOUS_BASE_COVERAGE"), "overlapping Base sessions silently chosen");
            File.WriteAllText(bPath, Base("27 X 26", "135000.0 54500 99\n135010.0 54510 99"));
            File.WriteAllText(gPath, Mobile("27 X 26", "00003N 0000020 N 55000 -2 99 5 135005.0"));
            Check(Run().Records!.Single().Reasons.Contains("SOURCE_CORRECTION_REVIEW_REQUIRED"), "already corrected source was corrected again");
            File.WriteAllText(bPath, Base("27 X 26", "135000.0 54500 99\n135006.0 broken 99\n135010.0 54510 99"));
            Check(Run().BlockedReasons.Contains("SELECTED_BASE_PARSE_ERRORS"), "corrupt selected Base was silently interpolated");
        });
    }

    private static GsmTimeMapping Map(GsmRawSession s) => new()
    {
        RawSessionId = s.RawSessionId, SourceSha256 = s.SourceSha256, UseHeaderDate = true,
        UtcOffsetMinutes = 480, ClockOffsetSeconds = 0, Status = "assumed", Evidence = "synthetic test only"
    };
    private static GsmObservationSelection Select(GsmRawSession s) => new()
    {
        RawSessionId = s.RawSessionId, SourceSha256 = s.SourceSha256, FirstPhysicalLine = s.Observations[0].PhysicalLine,
        LastPhysicalLine = s.Observations[^1].PhysicalLine, State = "normal", Evidence = "synthetic selection"
    };
    private static string Base(string date, string rows) => $"/Gem Systems GSM-19T 4046488 v7.0\n/ID 4 file 1 survey.b {date}\n/time nT sq\n{rows}\n";
    private static string Mobile(string date, string rows) => $"/Gem Systems GSM-19TG 53010332 v7.0\n/ID 1 file 3 survey.g {date}\n/00100 cm sensor distance\n/X Y nT nT/m sq cor-nT time\n{rows}\n";
    private static string WriteConfig(string folder, GsmGroundConfiguration config)
    {
        string path = Path.Combine(folder, "mapping.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return path;
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void MustReject(Action action)
    {
        try { action(); } catch (ArgumentException) { return; }
        throw new InvalidOperationException("Expected mapping validation failure.");
    }
    private static void WithFolder(Action<string> action)
    {
        string path = Path.Combine(Path.GetTempPath(), "magops-ground-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try { action(path); } finally { Directory.Delete(path, true); }
    }
}
