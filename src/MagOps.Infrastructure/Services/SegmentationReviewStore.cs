using System.Text.Json;
using MagOps.Core.Domain;

namespace MagOps.Infrastructure.Services;

/// <summary>Review decisions are separate from reusable parameter defaults.
/// Fingerprints are kept as saved; the review/audit authority decides validity.</summary>
public static class SegmentationReviewStore
{
    public const string SavedInputName = "MagOps_分线核验输入.json";
    public const string AutomaticStateName = "MagOps_分线核验状态.json";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static AuditProductionRequest? LoadLatest(string directory, WorkareaScreeningInput? currentScreening,
        FieldMagFolderAnalysis? currentAnalysis = null)
    {
        string? path = new[] { SavedInputName, AutomaticStateName }.Select(n => Path.Combine(directory, n))
            .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        if (path is null) return null;
        var saved = LoadFile(path);
        WorkareaScreeningInput? refreshedScreening = saved.SpatialScreening is { } prior && currentScreening is not null
            ? prior with
            {
                Range = currentScreening.Range,
                Options = currentScreening.Options,
                SourceFingerprint = currentScreening.SourceFingerprint,
                RouteFingerprint = currentScreening.RouteFingerprint
            }
            : currentScreening;
        if (refreshedScreening is { } refreshed)
            refreshedScreening = refreshed with { ContextFingerprint = WorkareaSpatialService.ContextFingerprint(refreshed) };

        string screeningFingerprint = saved.ScreeningFingerprint;
        bool sameReviewIdentity = currentAnalysis is not null && currentScreening is not null &&
            saved.SourceFingerprint == currentAnalysis.Fingerprint &&
            saved.RouteFingerprint == currentScreening.RouteFingerprint;
        bool sameScreeningScope = saved.SpatialScreening is { } savedScreening && currentScreening is not null &&
            savedScreening.Range == currentScreening.Range && savedScreening.Options == currentScreening.Options;
        if (sameReviewIdentity && sameScreeningScope && currentAnalysis is not null && refreshedScreening is not null)
            screeningFingerprint = new WorkareaSpatialService().Screen(currentAnalysis, refreshedScreening).Fingerprint;

        return saved with
        {
            SpatialScreening = refreshedScreening,
            ScreeningFingerprint = screeningFingerprint,
            AllowReviewDraft = true
        };
    }

    public static string? LatestPath(string directory) => new[] { SavedInputName, AutomaticStateName }
        .Select(n => Path.Combine(directory, n)).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();

    public static AuditProductionRequest LoadFile(string path)
    {
        var saved = JsonSerializer.Deserialize<AuditProductionRequest>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("核验状态文件为空：" + path);
        if (saved.ActualRepeatLegIds is null || saved.MissionWindows is null || saved.QualityConfirmations is null || saved.Overrides is null ||
            saved.ConfirmedPassIds is null || saved.ManualIncludedFlightLegIds is null || saved.ManualLegAssignments is null ||
            saved.AnomalyDecisions is null || saved.ProductionAnomalyOptions is null)
            throw new InvalidDataException("核验状态缺少必要集合：" + path);
        saved.ProductionAnomalyOptions.Validate();
        ValidateRestoration(saved.MotherLineRestoration);
        if (saved.TrackSegmentation is { } input)
        {
            if (input.RecognitionOptions is null || input.MatchOptions is null || input.ReferenceGeometry is null || input.WindowPlannedSpeeds is null)
                throw new InvalidDataException("核验状态缺少识别参数：" + path);
            SegmentationParameterStore.Validate(input);
        }
        return saved;
    }

    public static void Save(string path, AuditProductionRequest request)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(request, Json));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void ValidateRestoration(MotherLineRestorationState? state)
    {
        if (state is null) return;
        if (state.Options is null || state.Groups is null || state.RejectedGroupIds is null || state.ProtectedLegIds is null || state.ExcludedLegIds is null ||
            state.UnassignedEvidence is null || state.RetainedIndependentLegIds is null ||
            state.Groups.Any(g => g is null || g.LegIds is null || g.ShortLegIds is null || g.Before is null || g.After is null || g.OriginalNames is null || g.OriginalTaskByLeg is null || g.AnchorLegIds is null || g.OriginalTasks is null))
            throw new InvalidDataException("母测线恢复记录不完整。");
        state.Options.Validate();
    }
}
