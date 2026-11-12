namespace MagOps.Core.GroundMag;

public sealed record GsmDiagnostic(string Code, string SourceFile, int PhysicalLine, string Message)
{
    public int Occurrences { get; init; } = 1;
    public int LastPhysicalLine { get; init; } = PhysicalLine;
}

public sealed record GsmRawObservation(
    string RawSessionId, int PhysicalLine, int SourceRecord, string RawLine,
    string ClockText, TimeSpan Clock, bool ExplicitNextDayMidnight,
    double MagneticFieldNt, double? Sq, double? GradientNtPerM, double? CorrectionNt,
    string? LineToken, string? StationToken, string? StationDirection);

public sealed record GsmRawSession(
    string RawSessionId, string SourceId, string SourceSha256, string SourceFile,
    int HeaderLine, string HeaderText, string InstrumentModel, string SerialNumber,
    string SurveyType, string AcquisitionDateText, DateOnly? AcquisitionDate,
    string ColumnHeader, double? SensorDistanceCm, IReadOnlyList<GsmRawObservation> Observations);

public sealed record GsmSourceAlias(string Path, string Sha256);

public sealed record GsmRawSource(
    string SourceId, string Sha256, string Path, IReadOnlyList<GsmSourceAlias> Aliases,
    string Encoding, IReadOnlyList<GsmRawSession> Sessions);

public sealed record GsmGroundInput(
    string ParserVersion, IReadOnlyList<GsmRawSource> Sources, IReadOnlyList<GsmDiagnostic> Diagnostics);

/// <summary>No timezone, date or zero clock correction is implied by omission.</summary>
public sealed record GsmTimeMapping
{
    public string RawSessionId { get; init; } = "";
    public string SourceSha256 { get; init; } = "";
    public bool UseHeaderDate { get; init; }
    public DateOnly? DateOverride { get; init; }
    public int? UtcOffsetMinutes { get; init; }
    public string? TimeZoneId { get; init; }
    public double? ClockOffsetSeconds { get; init; }
    public string Status { get; init; } = "assumed";
    public string Evidence { get; init; } = "";
    public bool AllowMidnightRollover { get; init; }
}

public sealed record GsmObservationSelection
{
    public string RawSessionId { get; init; } = "";
    public string SourceSha256 { get; init; } = "";
    public int FirstPhysicalLine { get; init; }
    public int LastPhysicalLine { get; init; }
    public string State { get; init; } = "unknown";
    public string Evidence { get; init; } = "";
}

public sealed record GsmGroundConfiguration
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<GsmTimeMapping> TimeMappings { get; init; } = [];
    public IReadOnlyList<GsmObservationSelection> Selections { get; init; } = [];
    public IReadOnlyList<string> BaseSessionIds { get; init; } = [];
    public double? ReferenceFieldNt { get; init; }
    public string ReferenceFieldEvidence { get; init; } = "";
}

public sealed record GsmResolvedObservation(
    GsmRawObservation Observation, DateTimeOffset? TimestampUtc, string TimeStatus,
    string SelectionState, IReadOnlyList<string> Reasons);

public sealed record GsmResolvedSession(
    GsmRawSession Session, IReadOnlyList<GsmResolvedObservation> Observations);

public sealed record GsmTimeResolution(
    IReadOnlyList<GsmResolvedSession> Sessions, IReadOnlyList<GsmDiagnostic> Diagnostics)
{
    public int ParsedRecordCount => Sessions.Sum(s => s.Observations.Count);
    public int ResolvedTimeRecordCount => Sessions.Sum(s => s.Observations.Count(r => r.TimeStatus == "confirmed"));
    public int AssumedTimeRecordCount => Sessions.Sum(s => s.Observations.Count(r => r.TimeStatus == "assumed"));
    public int UnresolvedTimeRecordCount => ParsedRecordCount - ResolvedTimeRecordCount - AssumedTimeRecordCount;
}
