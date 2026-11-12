using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private void BeginIntermediateCsvReview()
    {
        EnsureInitialized();
        using var picker = new OpenFileDialog { Title = "选择一个工区的中间成果 CSV（分别核验，不合并）",
            Filter = "表格文本|*.csv;*.txt;*.tsv", Multiselect = false, CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        // Resolve unsaved choices before reading the saved seed for the next source.
        // Reopening the same file must see a just-saved selection, not an older copy.
        if (_processingReview?.HasUnsavedWorkspaceReview == true &&
            !ConfirmContextSwitch("切换到中间 CSV 空间分线")) return;
        var input = BusyOperationDialog.Run(this, "读取中间成果", "正在识别编码、日期时间和经纬度；其他字段原样保留…",
            () => IntermediateCsvSegmentationService.Read(picker.FileName));
        string reviewRoot = Path.Combine(EnsureManagedOutputDirectory("processing_diurnal", "中间成果空间分线"), "中间成果空间分线");
        string parent = IntermediateCsvSegmentationService.EnsureWorkspaceDirectory(reviewRoot, input);
        var seed = new AuditProductionRequest(input.Analysis.Fingerprint, input.Windows, [], [], true)
        { TrackSegmentation = new() { Enabled = true, UseDesignTasks = false, AxisDegrees = 135 } };
        string? snapshotPath = SegmentationReviewStore.LatestPath(parent)
            ?? SegmentationReviewStore.LatestPath(Path.Combine(reviewRoot, input.Sha256));
        var saved = snapshotPath is null ? null : SegmentationReviewStore.LoadFile(snapshotPath);
        if (saved?.SourceFingerprint == input.Analysis.Fingerprint && saved.TrackSegmentation is not null)
            seed = seed with { TrackSegmentation = saved.TrackSegmentation };
        using (var settings = new TrackSegmentationOptionsDialog(input.Analysis,
            seed.TrackSegmentation ?? new() { Enabled = true, AxisDegrees = 135 }, null, true))
        {
            if (settings.ShowDialog(this) != DialogResult.OK || settings.Result is null) return;
            seed = seed with { TrackSegmentation = settings.Result };
        }
        if (OpenIntermediateCsvReview(input, seed, parent) && saved is not null)
            _processingReview!.OfferSavedSnapshot(saved, snapshotPath!);
    }

    private bool OpenIntermediateCsvReview(IntermediateCsvSnapshot input, AuditProductionRequest seed, string parent)
    {
        if (_processingReview?.HasUnsavedWorkspaceReview == true &&
            !ConfirmContextSwitch("切换到中间 CSV 空间分线")) return false;
        var review = new AutomaticSegmentationReviewDialog(input.Analysis, [], seed, "",
            IntermediateCsvSegmentationService.SourceClock, null, true, true)
        { EmbeddedWorkspace = true, ReviewOutputDirectory = parent };
        review.ShowSpatialSourceSummary($"{Path.GetFileName(input.Path)}：有效点 {input.Analysis.UavSamples.Count:N0}，" +
            $"无效记录 {input.InvalidRecords:N0}（导出时列入未纳入记录），时间倒退断口 {input.TimeReversals:N0}，" +
            $"已由相邻记录确认的秒60进位 {input.CarriedClocks:N0}。" +
            "其他列原样保留；自动作业候选需人工复核。" );
        review.ProcessingRequested += () => RunUi("导出空间分线 CSV", () => ExportCurrentIntermediateCsv(true));
        AttachProcessingReview(review, "intermediate:" + input.Sha256, input);
        return true;
    }

    private string? ExportCurrentIntermediateCsv(bool showNotice)
    {
        if (_processingReview is not { IsDisposed: false } review || _processingCsvInput is not { } input ||
            review.ReviewOutputDirectory is not { } parent)
            throw new InvalidOperationException("请先在分线与处理页载入中间 CSV 并核验右侧航段。");
        var selection = review.CaptureSpatialSelection();
        if (selection is null) return null;
        string Write() => IntermediateCsvSegmentationService.Export(input, selection.Value.Audit, selection.Value.Request, parent);
        string result = showNotice
            ? BusyOperationDialog.Run(this, "导出空间分线 CSV", "正在按核验选择筛选原始记录，保留所有业务字段…", Write)
            : Write();
        RegisterGeneratedBusinessFile(result, "intermediate_segmentation");
        SetStatus("空间分线 CSV 已生成：" + result);
        if (showNotice) TransientNotice.Show(this, result, "空间分线完成");
        return result;
    }
}
