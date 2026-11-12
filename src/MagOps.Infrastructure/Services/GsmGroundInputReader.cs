using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MagOps.Core.GroundMag;

namespace MagOps.Infrastructure.Services;

/// <summary>Reads immutable acquisition blocks, without canonical UTC conversion, QC deletion or value deduplication.</summary>
public sealed class GsmGroundInputReader
{
    public const string ParserVersion = "gsm-ground-reader-2";
    private static readonly Regex Model = new(@"\bGSM-19(TG|T)\b[\t ]+(?<serial>\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex Header = new(@"^/ID\b.*?\bfile\s+(?<file>\d+)\s*survey\.(?<type>[bg])(?:\s+(?<day>\d{1,2})\s+(?<month>[IVX]+)\s+(?<year>\d{4}|\d{2}))?(?:\s|$)", RegexOptions.IgnoreCase);
    private static readonly string[] Months = ["I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X", "XI", "XII"];
    private static readonly HashSet<string> Extensions = new([".txt", ".b", ".g", ".stn", ".dat", ".csv", ".tsv", ".asc", ".xyz", ".log"], StringComparer.OrdinalIgnoreCase);

    public GsmGroundInput ReadFolder(string directory, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new ArgumentException("An existing raw acquisition directory is required.");
        var sources = new Dictionary<string, GsmRawSource>(StringComparer.Ordinal);
        var diagnostics = new List<GsmDiagnostic>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string path in Directory.EnumerateFiles(Path.GetFullPath(directory), "*", options)
                     .Where(p => Extensions.Contains(Path.GetExtension(p))).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            cancellation.ThrowIfCancellationRequested();
            byte[] bytes = File.ReadAllBytes(path);
            string sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            string text;
            string encoding = "UTF-8";
            try { text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF'); }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                text = Encoding.GetEncoding("GB18030", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes);
                encoding = "GB18030";
                diagnostics.Add(new("ENCODING_FALLBACK", path, 0, encoding));
            }
            string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            string id = "gsm-file-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant()[..32];
            if (sources.TryGetValue(id, out var existing))
            {
                sources[id] = existing with { Aliases = [.. existing.Aliases, new(path, sha)] };
                diagnostics.Add(new(existing.Sha256 == sha ? "DUPLICATE_FILE_ALIAS" : "NORMALIZED_COPY_ALIAS", path, 0,
                    "Same decoded acquisition content; counted once. Alias retains its exact-byte SHA."));
                continue;
            }
            var sessions = Parse(normalized, path, id, sha, diagnostics, cancellation);
            if (sessions.Count == 0)
            {
                diagnostics.Add(new("NO_ACQUISITION_RECORDS", path, 0, "No supported nonempty original GSM acquisition blocks."));
                continue;
            }
            if (sessions.Select(s => s.AcquisitionDate).Where(d => d.HasValue).Distinct().Count() > 1)
                diagnostics.Add(new("MULTIPLE_ACQUISITION_DATES", path, 0,
                    "Dates are retained per acquisition block; no whole-file UTC date is inferred."));
            var nameDate = Regex.Match(Path.GetFileName(path), @"\d{4}-\d{2}-\d{2}");
            if (nameDate.Success && DateOnly.TryParseExact(nameDate.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var fileDate))
                foreach (var session in sessions.Where(s => s.AcquisitionDate.HasValue && s.AcquisitionDate != fileDate))
                    diagnostics.Add(new("ACQUISITION_FILENAME_DATE_CONFLICT", path, session.HeaderLine,
                        $"Header={session.AcquisitionDate:yyyy-MM-dd}; filename={fileDate:yyyy-MM-dd}; neither is confirmed."));
            sources.Add(id, new(id, sha, path, [], encoding, sessions));
        }
        return new(ParserVersion, sources.Values.ToArray(), diagnostics);
    }

    private static IReadOnlyList<GsmRawSession> Parse(string text, string path, string sourceId, string sha,
        List<GsmDiagnostic> diagnostics, CancellationToken cancellation)
    {
        var sessions = new List<GsmRawSession>();
        string model = "", serial = "";
        Block? block = null;
        void Flush()
        {
            if (block is null) return;
            if (block.Rows.Count == 0)
                diagnostics.Add(new("EMPTY_SESSION_HEADER", path, block.Line, block.Header));
            else
                sessions.Add(new(block.Id, sourceId, sha, path, block.Line, block.Header,
                    block.Model, block.Serial, block.Type, block.DateText, block.Date,
                    block.Columns, block.Distance, block.Rows.ToArray()));
        }
        string[] lines = text.Split('\n');
        int record = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            string raw = lines[i], line = raw.Trim();
            int physicalLine = i + 1;
            var instrument = Model.Match(line);
            if (instrument.Success)
            {
                // A device banner is also a hard boundary; do not attach later records to an earlier device.
                Flush(); block = null;
                model = instrument.Value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();
                serial = instrument.Groups["serial"].Value;
                continue;
            }
            if (line.StartsWith("/ID", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                var match = Header.Match(line);
                string type = match.Success ? match.Groups["type"].Value.ToLowerInvariant() : "";
                DateOnly? date = null;
                string dateText = "";
                if (match.Success && match.Groups["day"].Success)
                {
                    dateText = $"{match.Groups["day"].Value} {match.Groups["month"].Value} {match.Groups["year"].Value}";
                    int month = Array.IndexOf(Months, match.Groups["month"].Value.ToUpperInvariant()) + 1;
                    int year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
                    if (year < 100) year += 2000;
                    try { date = new DateOnly(year, month, int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture)); }
                    catch (ArgumentOutOfRangeException) { diagnostics.Add(new("INVALID_ACQUISITION_DATE", path, physicalLine, dateText)); }
                }
                else diagnostics.Add(new("INCOMPLETE_SESSION_HEADER", path, physicalLine, line));
                string sessionId = "gsm-raw-session-v1-" + Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(sourceId + "|" + physicalLine.ToString(CultureInfo.InvariantCulture)))).ToLowerInvariant()[..32];
                block = new(sessionId, physicalLine, line, model, serial, type, dateText, date);
                continue;
            }
            if (block is null)
            {
                if (line.Length > 0 && char.IsDigit(line[0]))
                    diagnostics.Add(new("UNASSOCIATED_NUMERIC_LINE", path, physicalLine, "Numeric line outside acquisition block."));
                continue;
            }
            var distance = Regex.Match(line, @"^/(?<cm>\d+(?:\.\d+)?)\s+cm\s+sensor distance", RegexOptions.IgnoreCase);
            if (distance.Success && Number(distance.Groups["cm"].Value, out double distanceCm)) block.Distance = distanceCm;
            if (Regex.IsMatch(line, @"^/(?:time|X)(?:\s|$)", RegexOptions.IgnoreCase))
            {
                if (block.Columns.Length > 0) diagnostics.Add(new("REPEATED_COLUMN_HEADER", path, physicalLine, line));
                block.Columns = line;
                string[] tokens = line.TrimStart('/').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                block.ValidColumns = block.Type == "b"
                    ? tokens.Select(t => t.ToLowerInvariant()).SequenceEqual(new[] { "time", "nt", "sq" })
                    : block.Type == "g" && tokens.Select(t => t.ToLowerInvariant()).SequenceEqual(new[] { "x", "y", "nt", "nt/m", "sq", "cor-nt", "time" });
                if (!block.ValidColumns) diagnostics.Add(new("UNSUPPORTED_COLUMN_MAP", path, physicalLine, line));
                continue;
            }
            if (Regex.IsMatch(line, @"(?i)\b(Tune|ZeroRatio)\b"))
                diagnostics.Add(new("INSTRUMENT_PARAMETER_REVIEW", path, physicalLine, line + "; retained without an invented acceptance threshold."));
            if (Regex.IsMatch(line, @"(?i)\b(NoValue|NaN|NULL)\b"))
                diagnostics.Add(new("NO_VALUE", path, physicalLine, "Missing measurement token; never converted to zero."));
            if (line.Length == 0 || line.StartsWith('/')) continue;
            if (!char.IsDigit(line[0]) && line[0] is not ('+' or '-')) continue;
            record++;
            string[] fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            bool ground = block.Type == "g";
            bool validModel = ground ? block.Model == "GSM-19TG" : block.Type == "b" && block.Model == "GSM-19T";
            if (!validModel || !block.ValidColumns || fields.Length != (ground ? 8 : 3))
            {
                diagnostics.Add(new("UNPARSED_DATA_ROW", path, physicalLine, "Unknown device/role, missing column map or wrong field count."));
                continue;
            }
            int magIndex = ground ? 3 : 1, sqIndex = ground ? 5 : 2;
            string clockText = fields[ground ? 7 : 0];
            if (!TryClock(clockText, out var clock, out bool midnight) ||
                !Number(fields[magIndex], out double mag) || !Number(fields[sqIndex], out double sq) ||
                (ground && (!Regex.IsMatch(fields[0], @"^\d+[NS]$", RegexOptions.IgnoreCase) ||
                    !Regex.IsMatch(fields[1], @"^\d+$") || !Regex.IsMatch(fields[2], @"^[NSEW]$", RegexOptions.IgnoreCase) ||
                    !Number(fields[4], out _) || !Number(fields[6], out _))))
            {
                diagnostics.Add(new("MALFORMED_DATA_ROW", path, physicalLine, "Invalid clock, field value or local coordinate token."));
                continue;
            }
            block.Rows.Add(new(block.Id, physicalLine, record, raw, clockText, clock, midnight, mag, sq,
                ground ? double.Parse(fields[4], CultureInfo.InvariantCulture) : null,
                ground ? double.Parse(fields[6], CultureInfo.InvariantCulture) : null,
                ground ? fields[0] : null, ground ? fields[1] : null, ground ? fields[2] : null));
            if (sq is < 0 or > 100) diagnostics.Add(new("SQ_OUT_OF_RANGE", path, physicalLine, "Original SQ retained; requires review."));
            if (ground && double.Parse(fields[6], CultureInfo.InvariantCulture) != 0)
                diagnostics.Add(new("SOURCE_CORRECTION_REVIEW_REQUIRED", path, physicalLine, "Nonzero cor-nT retained; no automatic second correction."));
        }
        Flush();
        foreach (var session in sessions.Where(s => s.SurveyType == "b"))
            foreach (var group in session.Observations.GroupBy(r => r.ClockText).Where(g => g.Count() > 1))
                diagnostics.Add(new("REPEATED_BASE_CLOCK", path, group.First().PhysicalLine,
                    "Repeated local Base clock; original rows retained. Resolved UTC/session QC determines eligibility.")
                    { Occurrences = group.Count(), LastPhysicalLine = group.Last().PhysicalLine });
        return sessions;
    }

    private static bool Number(string token, out double value) =>
        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static bool TryClock(string token, out TimeSpan clock, out bool midnight)
    {
        clock = default; midnight = false;
        if (!Regex.IsMatch(token, @"^\d{6}(?:\.\d{1,7})?$")) return false;
        int hour = int.Parse(token[..2], CultureInfo.InvariantCulture), minute = int.Parse(token[2..4], CultureInfo.InvariantCulture);
        decimal second = decimal.Parse(token[4..], CultureInfo.InvariantCulture);
        if (hour == 24 && minute == 0 && second == 0) { midnight = true; return true; }
        if (hour > 23 || minute > 59 || second >= 60) return false;
        clock = new TimeSpan(0, hour, minute, 0).Add(TimeSpan.FromTicks((long)(second * TimeSpan.TicksPerSecond)));
        return true;
    }

    private sealed class Block(string id, int line, string header, string model, string serial,
        string type, string dateText, DateOnly? date)
    {
        public string Id { get; } = id;
        public int Line { get; } = line;
        public string Header { get; } = header;
        public string Model { get; } = model;
        public string Serial { get; } = serial;
        public string Type { get; } = type;
        public string DateText { get; } = dateText;
        public DateOnly? Date { get; } = date;
        public string Columns { get; set; } = "";
        public bool ValidColumns { get; set; }
        public double? Distance { get; set; }
        public List<GsmRawObservation> Rows { get; } = [];
    }
}
