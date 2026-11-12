using MagOps.Core.Diurnal;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private TabControl? _fieldWorkspaceTabs;
    private TabPage? _diurnalQualityPage;
    private DiurnalQualitySnapshot? _diurnalQuality;
    private readonly Label _diurnalQualitySummary = new() { Dock=DockStyle.Fill, AutoEllipsis=true, Height=55 };
    private readonly Label _diurnalQualityNotice = new() { AutoSize=true, MaximumSize=new Size(600,0) };
    private readonly FlowLayoutPanel _diurnalQualityBanner = new() { Dock=DockStyle.Top, AutoSize=true, WrapContents=true, Visible=false };
    private readonly DataGridView _diurnalQualityGrid = new() { Dock=DockStyle.Fill, ReadOnly=true, AllowUserToAddRows=false,
        AllowUserToDeleteRows=false, AutoGenerateColumns=false, MultiSelect=false, SelectionMode=DataGridViewSelectionMode.FullRowSelect };
    private readonly ComboBox _diurnalQualityFilter = new() { DropDownStyle=ComboBoxStyle.DropDownList, Width=170 };
    private readonly DiurnalQualityPreviewPanel _diurnalQualityMap = new();
    private readonly MotherLineProductionWorkspace _motherLineProductionWorkspace = new();
    private readonly TextBox _diurnalQualityDetail = new() { Dock=DockStyle.Fill, ReadOnly=true, Multiline=true, ScrollBars=ScrollBars.Both };

    private TabPage BuildDiurnalQualityPage()
    {
        var page=new TabPage("日变质量"); _diurnalQualityPage=page;
        _diurnalQualityFilter.AccessibleName="日变质量区间筛选";
        _diurnalQualityGrid.AccessibleName="日变质量时间区间";
        _diurnalQualityDetail.AccessibleName="日变质量来源与原因";
        var root=new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=1, RowCount=5, Padding=UiTheme.PagePadding };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute,58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent,45));
        root.RowStyles.Add(new RowStyle(SizeType.Percent,35));
        root.RowStyles.Add(new RowStyle(SizeType.Percent,20));
        var toolbar=new FlowLayoutPanel { Dock=DockStyle.Fill, AutoSize=true, WrapContents=true };
        _diurnalQualityFilter.Items.AddRange(["异常与资格排除","仅未改正","仅输入资格排除","全部区间"]);
        _diurnalQualityFilter.SelectedIndex=0;
        _diurnalQualityFilter.SelectedIndexChanged+=(_,_)=>RefreshDiurnalQualityRows();
        toolbar.Controls.Add(_diurnalQualityFilter);
        toolbar.Controls.Add(Button("返回外业数据",(_,_)=>_fieldWorkspaceTabs!.SelectedIndex=0,UiButtonRole.Subtle));
        toolbar.Controls.Add(UiComponents.HelpLabel("区间时间为 UTC；地图：蓝=已改正，橙=未改正，灰=资格排除，黑圈=所选区间。滚轮缩放／中键平移／Home 全图。"));
        root.Controls.Add(toolbar,0,0); root.Controls.Add(_diurnalQualitySummary,0,1);
        foreach(var column in new[] { ("Category","类别",110),("Reason","原因",240),("StartUtc","开始 UTC",160),
            ("EndUtc","结束 UTC",160),("Count","点数",65),("Line","测线",140),("SourceFile","来源",240) })
            _diurnalQualityGrid.Columns.Add(new DataGridViewTextBoxColumn {DataPropertyName=column.Item1,HeaderText=column.Item2,Width=column.Item3});
        _diurnalQualityGrid.SelectionChanged+=(_,_)=>SelectDiurnalQualityInterval();
        root.Controls.Add(_diurnalQualityGrid,0,2);root.Controls.Add(_diurnalQualityMap,0,3);root.Controls.Add(_diurnalQualityDetail,0,4);
        page.Controls.Add(root); InvalidateDiurnalQuality();return page;
    }

    private TabPage BuildMotherLineProductionPage()
    {
        _motherLineProductionWorkspace.ResolveImportContext = () =>
            (_workspace.GetWorkspace(Root).ProjectId, ResolveManagedWorkareaId(CurrentWorkareaRelativePath));
        _motherLineProductionWorkspace.CandidateSaved += RememberLatestGeneratedBusinessFile;
        var page = new TabPage("母线候选") { Padding = Padding.Empty };
        page.Controls.Add(_motherLineProductionWorkspace);
        return page;
    }

    private void ShowDiurnalQuality()
    {
        NavigateFeature(FeatureIds.FieldOperations);
        if(_diurnalQualityPage is not null) _fieldWorkspaceTabs!.SelectedTab=_diurnalQualityPage;
        _diurnalQualityBanner.Visible=false;
    }

    private void InvalidateDiurnalQuality()
    {
        _motherLineProductionWorkspace.ClearSnapshot();
        _diurnalQuality=null;_diurnalQualityGrid.DataSource=null;_diurnalQualityMap.SetSnapshot(null);
        _diurnalQualityDetail.Text="生成日变或处理用 CSV 后可查看本次质量；仅显示计算快照，不修改数据或选择范围。";
        _diurnalQualitySummary.Text="尚无当前计算快照；来源、Datum 或工区变化后需重新生成。";
        _diurnalQualityBanner.Visible=false;
    }

    private void ApplyDiurnalQuality(FieldMagFolderAnalysis analysis, DiurnalCorrectionResult correction, string scope,
        IReadOnlyDictionary<(string SourceFile,int Record),string>? lines=null)
    {
        _diurnalQuality=DiurnalQualityService.Build(analysis,correction,scope,lines);
        var q=_diurnalQuality;
        _diurnalQualitySummary.Text=$"{scope}：{q.Total:N0} 点；合格输入 {q.Eligible:N0}，已改正 {q.Corrected:N0}，未改正 {q.Uncorrected:N0}；SensorStatus=F {q.SensorExcluded:N0}，缺磁值 {q.MissingMagnetic:N0}。\r\n暂定提醒：未改正≥100点且占合格输入≥1%；仅控制提醒，不是质量验收或生产阈值。";
        _diurnalQualityMap.SetSnapshot(q);RefreshDiurnalQualityRows();
        _diurnalQualityNotice.Text=$"日变质量提示：{q.Uncorrected:N0} 个合格输入未改正。可查看时间、原因和空间范围；当前流程可继续。";
        _diurnalQualityBanner.Visible=q.Attention;
    }

    private void RefreshDiurnalQualityRows()
    {
        if(_diurnalQuality is not { } q) return;
        _diurnalQualityGrid.DataSource=q.Intervals.Where(r=>_diurnalQualityFilter.SelectedIndex switch
        {1=>r.Category=="未改正",2=>r.Category=="输入资格排除",3=>true,_=>r.Category!="已改正"}).ToArray();
        SelectDiurnalQualityInterval();
    }

    private void SelectDiurnalQualityInterval()
    {
        var interval=_diurnalQualityGrid.CurrentRow?.DataBoundItem as DiurnalQualityInterval;
        _diurnalQualityMap.Selected=interval;
        _diurnalQualityDetail.Text=interval is null ? "当前筛选没有区间；正常计算不会自动打开本页。" :
            $"{interval.Category}：{interval.Reason}\r\nUTC {interval.StartUtc:yyyy-MM-dd HH:mm:ss.fff} — {interval.EndUtc:yyyy-MM-dd HH:mm:ss.fff}\r\n来源：{interval.SourceFile}\r\n记录 {interval.FirstRecord}–{interval.LastRecord}；{interval.Count:N0} 点；{interval.Line}\r\n灰色资格排除未参与插值，不计为插值失败。地图缺坐标的记录仍保留在区间表。";
    }
}
