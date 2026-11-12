using MagOps.Core.Coordinates;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private void UseCoordinateWorkareaRange()
    {
        if (_lastCoordinateBatch is null || _lastCoordinateBatch.Results.Count < 2)
            throw new InvalidOperationException("请先整理至少两个有效工区坐标。");
        string workarea = SelectWorkareaRelativePath("设置工区空间范围");
        var projection = _lastCoordinateProjection ?? throw new InvalidOperationException("请重新转换坐标以确认矩形使用的投影参数。");
        RequireCurrentCoordinateProjection();
        var range = WorkareaSpatialService.FromCoordinates(workarea, "已确认的工区坐标范围", _lastCoordinateBatch.Results, projection);
        new WorkareaSpatialService().SaveRange(Root, range, _store);
        _fieldSpatialScreening = (_fieldSpatialScreening ?? new(range, new())) with { Range = range, Decisions = [], ExcludedSamples = [] };
        SaveFieldOperationsSettings();
        if (_fieldMagRawAnalysis is not null) { ApplyFieldDataAnalysis(_fieldMagRawAnalysis); AnalyzeProjectFieldTimeline(force: true, quietNoRoute: true); }
        _coordinateWorkspace.SetProjectSourceSummary("当前工区筛选范围已保存；空间筛选和匹配将按新范围重算。");
        SetStatus("已保存当前工区矩形与重心；原始数据保留。");
    }

    private WorkareaScreeningInput ResolveWorkareaScreeningInput(FieldMagFolderAnalysis raw)
    {
        FieldRouteSnapshot? route = ResolveFieldOperationsRouteSnapshot();
        WorkareaSpatialRange? range = new WorkareaSpatialService().LoadRange(Root, CurrentWorkareaRelativePath, _store)
            ?? WorkareaSpatialService.FromTasks(CurrentWorkareaRelativePath, route?.SurveyLines ?? []);
        var input = (_fieldSpatialScreening ?? new(range, new())) with { Range = range, RouteFingerprint = route?.Fingerprint ?? "" };
        string context = WorkareaSpatialService.ContextFingerprint(input);
        if (input.SourceFingerprint != raw.Fingerprint || input.ContextFingerprint != context)
            input = input with { Decisions = [], ExcludedSamples = [] };
        return input with { SourceFingerprint = raw.Fingerprint, ContextFingerprint = context };
    }

    private FieldMagFolderAnalysis ApplyCurrentWorkareaScreening(FieldMagFolderAnalysis raw)
    {
        raw = WorkareaSpatialService.WithTimeZone(raw, _projectTimeZone.Id);
        _fieldMagRawAnalysis = raw;
        CalculateImportedDemHeight(raw);
        _fieldSpatialScreening = ResolveWorkareaScreeningInput(raw);
        var result = new WorkareaSpatialService().Screen(raw, _fieldSpatialScreening);
        _fieldAutoAnalysisContextKey = ""; _fieldProjectTimelineContextKey = "";
        return WorkareaSpatialService.Apply(raw, result, _projectTimeZone.Id);
    }

    private void EditWorkareaScreening()
    {
        if (_fieldMagRawAnalysis is null) AnalyzeFieldDataFolder(force: true, quietMissing: false);
        if (_fieldMagRawAnalysis is null) return;
        using var dialog = new WorkareaScreeningDialog(_fieldMagRawAnalysis, ResolveWorkareaScreeningInput(_fieldMagRawAnalysis));
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.ReviewedInput is null) return;
        _fieldSpatialScreening = dialog.ReviewedInput;
        SaveFieldOperationsSettings(); ApplyFieldDataAnalysis(_fieldMagRawAnalysis);
        AnalyzeProjectFieldTimeline(force: true, quietNoRoute: true);
        SetStatus("工区数据选用已更新；原始资料保留，旧匹配结果需要重新核验。");
    }
}
