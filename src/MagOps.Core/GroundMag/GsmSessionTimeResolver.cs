namespace MagOps.Core.GroundMag;

public sealed class GsmSessionTimeResolver
{
    public const string AlgorithmVersion = "gsm-session-time-1";

    public GsmTimeResolution Resolve(GsmGroundInput input, GsmGroundConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.SchemaVersion != 1 || configuration.TimeMappings is null ||
            configuration.Selections is null || configuration.BaseSessionIds is null ||
            configuration.TimeMappings.Any(m => m is null) || configuration.Selections.Any(s => s is null) ||
            configuration.BaseSessionIds.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Unsupported or incomplete ground configuration.");
        var sessions = input.Sources.SelectMany(s => s.Sessions).ToDictionary(s => s.RawSessionId);
        var mappings = new Dictionary<string, GsmTimeMapping>(StringComparer.Ordinal);
        foreach (var mapping in configuration.TimeMappings)
        {
            if (mapping is null || !sessions.TryGetValue(mapping.RawSessionId, out var session) ||
                !session.SourceSha256.Equals(mapping.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
                !mappings.TryAdd(mapping.RawSessionId, mapping))
                throw new ArgumentException("Time mapping has an unknown/duplicate session or stale source SHA.");
            ValidateMapping(mapping);
        }
        var selections = configuration.Selections.GroupBy(s => s.RawSessionId).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var group in selections)
        {
            if (!sessions.TryGetValue(group.Key, out var session))
                throw new ArgumentException("Selection refers to an unknown session.");
            int previousEnd = 0;
            foreach (var selection in group.Value.OrderBy(s => s.FirstPhysicalLine))
            {
                if (!session.SourceSha256.Equals(selection.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
                    selection.FirstPhysicalLine <= previousEnd || selection.LastPhysicalLine < selection.FirstPhysicalLine ||
                    !session.Observations.Any(r => r.PhysicalLine == selection.FirstPhysicalLine) ||
                    !session.Observations.Any(r => r.PhysicalLine == selection.LastPhysicalLine) ||
                    selection.State is not ("normal" or "after-removal" or "historical" or "unknown") ||
                    string.IsNullOrWhiteSpace(selection.Evidence))
                    throw new ArgumentException("Selection is stale, overlapping, out of bounds or missing state/evidence.");
                previousEnd = selection.LastPhysicalLine;
            }
        }
        if (configuration.BaseSessionIds.Distinct(StringComparer.Ordinal).Count() != configuration.BaseSessionIds.Count ||
            configuration.BaseSessionIds.Any(id => !sessions.TryGetValue(id, out var s) || s.SurveyType != "b"))
            throw new ArgumentException("Base selection must contain unique base session IDs.");

        var diagnostics = new List<GsmDiagnostic>();
        var resolved = new List<GsmResolvedSession>();
        foreach (var session in sessions.Values)
        {
            mappings.TryGetValue(session.RawSessionId, out var mapping);
            selections.TryGetValue(session.RawSessionId, out var ranges);
            DateOnly? date = mapping?.DateOverride ?? (mapping?.UseHeaderDate == true ? session.AcquisitionDate : null);
            TimeSpan? previous = null;
            int dayOffset = 0;
            bool timelineUncertain = false;
            bool previousExplicitMidnight = false;
            var rows = new List<GsmResolvedObservation>();
            foreach (var raw in session.Observations)
            {
                var reasons = new List<string>();
                // 240000 is an explicit next-day marker. 000010 after it must not add a second day.
                if (raw.ExplicitNextDayMidnight)
                {
                    if (previousExplicitMidnight)
                    {
                        timelineUncertain = true;
                        reasons.Add("REPEATED_240000");
                    }
                    else dayOffset++;
                }
                else if (previous.HasValue && raw.Clock < previous.Value)
                {
                    bool midnightCandidate = previous.Value >= TimeSpan.FromHours(23) && raw.Clock < TimeSpan.FromHours(1);
                    if (midnightCandidate && mapping?.AllowMidnightRollover == true) dayOffset++;
                    else
                    {
                        timelineUncertain = true;
                        reasons.Add(midnightCandidate ? "MIDNIGHT_REVIEW_REQUIRED" : "CLOCK_BACKWARD");
                    }
                }
                if (previous == raw.Clock && !raw.ExplicitNextDayMidnight) reasons.Add("REPEATED_CLOCK");
                previous = raw.Clock;
                previousExplicitMidnight = raw.ExplicitNextDayMidnight;
                if (mapping is null) reasons.Add("TIME_MAPPING_REQUIRED");
                if (!date.HasValue) reasons.Add("DATE_UNRESOLVED");
                if (timelineUncertain) reasons.Add("TIMELINE_UNRESOLVED");
                DateTimeOffset? timestamp = null;
                if (mapping is not null && date.HasValue && !timelineUncertain)
                {
                    try
                    {
                        var local = date.Value.ToDateTime(TimeOnly.MinValue).AddDays(dayOffset)
                            .Add(raw.Clock).AddSeconds(mapping.ClockOffsetSeconds!.Value);
                        if (mapping.UtcOffsetMinutes.HasValue)
                            timestamp = new DateTimeOffset(local, TimeSpan.FromMinutes(mapping.UtcOffsetMinutes.Value)).ToUniversalTime();
                        else
                        {
                            var zone = TimeZoneInfo.FindSystemTimeZoneById(mapping.TimeZoneId!);
                            if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local)) reasons.Add("DST_TIME_UNRESOLVED");
                            else timestamp = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
                        }
                    }
                    catch (ArgumentException) { reasons.Add("TIME_OUT_OF_RANGE"); }
                }
                string state = ranges?.SingleOrDefault(s => raw.PhysicalLine >= s.FirstPhysicalLine &&
                    raw.PhysicalLine <= s.LastPhysicalLine)?.State ?? "unknown";
                rows.Add(new(raw, timestamp, timestamp.HasValue ? mapping!.Status : "unresolved", state, reasons));
                foreach (string reason in reasons)
                    diagnostics.Add(new(reason, session.SourceFile, raw.PhysicalLine, session.RawSessionId));
            }
            resolved.Add(new(session, rows));
        }
        return new(resolved, diagnostics);
    }

    private static void ValidateMapping(GsmTimeMapping mapping)
    {
        if (mapping.UseHeaderDate == mapping.DateOverride.HasValue ||
            mapping.UtcOffsetMinutes.HasValue == !string.IsNullOrWhiteSpace(mapping.TimeZoneId) ||
            mapping.UtcOffsetMinutes is < -840 or > 840 ||
            !mapping.ClockOffsetSeconds.HasValue || !double.IsFinite(mapping.ClockOffsetSeconds.Value) ||
            mapping.Status is not ("assumed" or "confirmed") || string.IsNullOrWhiteSpace(mapping.Evidence))
            throw new ArgumentException("Mapping requires one date basis, one timezone basis, explicit clock offset, status and evidence.");
        if (!string.IsNullOrWhiteSpace(mapping.TimeZoneId))
        {
            try { _ = TimeZoneInfo.FindSystemTimeZoneById(mapping.TimeZoneId); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            { throw new ArgumentException("Invalid instrument timezone.", ex); }
        }
    }
}
