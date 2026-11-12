using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

public sealed class MissionTaskSuggestionService
{
    public IReadOnlyList<MissionTimeWindow> Suggest(IReadOnlyList<UavMagCanonicalSample> samples,
        IReadOnlyList<PlannedSurveyLineTask> tasks, IReadOnlyList<MissionTimeWindow> windows,
        WorkareaScreeningInput? screening = null, WholeLegMatchOptions? matchOptions = null)
    {
        var valid = tasks.Where(t => !string.IsNullOrWhiteSpace(t.SurveyTaskStableId))
            .GroupBy(t => t.SurveyTaskStableId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1)
            .Select(g => g.Single()).ToArray();
        if (valid.Length == 0) return windows;
        IEnumerable<UavMagCanonicalSample> candidateSamples = samples;
        if (screening?.Range is { } range)
            candidateSamples = candidateSamples.Where(p => WorkareaSpatialService.TryProject(p, range, out double x, out double y) &&
                range.ContainsBuffered(x, y, screening.Options.BufferM));
        var points = candidateSamples.Where(p => p.Longitude.HasValue && p.Latitude.HasValue &&
                WorkareaSpatialService.ValidCoordinate(p.Longitude.Value, p.Latitude.Value))
            .Select(p => new SurveyTrackPoint(p.TimestampUtc, new(p.Longitude!.Value, p.Latitude!.Value), p.SourceFile, p.SourceRecord)
            { AltitudeM = p.AltitudeM, RawMagNt = p.MagneticFieldNt }).ToArray();
        return windows.Select(window =>
        {
            if (window.TaskScopeSource != "AUTO") return window;
            var windowPoints = points.Where(p => p.TimestampUtc >= window.StartUtc && p.TimestampUtc <= window.EndUtc &&
                (window.SourceFiles.Count == 0 || window.SourceFiles.Contains(p.SourceFile, StringComparer.OrdinalIgnoreCase)))
                .OrderBy(p => p.TimestampUtc).ThenBy(p => p.SourceFile, StringComparer.Ordinal).ThenBy(p => p.SourceRecord).ToList();
            var options = matchOptions ?? new WholeLegMatchOptions();
            var candidates = WholeLegMatcherService.Split(windowPoints, options)
                .SelectMany(segment => EndpointLineRecognizer.Find(segment, valid, options)).ToArray();
            // Use the same continuity rules as review; preserve all competing task candidates.
            return window with { SurveyTaskStableIds = candidates.Select(p => p.Task.SurveyTaskStableId)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray() };
        }).ToArray();
    }
}
