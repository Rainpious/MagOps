using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using MagOps.Reporting;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed partial class QualityCheckWorkspaceControl
{
    private readonly Label _offsetPreflight = new() { AutoSize = true, MaximumSize = new Size(430, 0), AccessibleName = "偏航距输入检查" };
    private readonly DataGridView _offsetLines = Grid(), _offsetCandidates = Grid();
    private readonly Label _offsetSummary = new() { AutoSize = true, Dock = DockStyle.Top, Text = "尚无偏航距计算结果。" };
    private readonly QualityOffsetPreview _offsetPreview = new() { Dock = DockStyle.Fill };
    private readonly Label _offsetPreviewNote = new() { AutoSize = true, Dock = DockStyle.Top, MaximumSize = new Size(650, 0) };
    private TabControl? _resultTabs;
    private TabPage? _offsetPreviewTab;
    private bool _refreshingOffset;
    private readonly List<Control> _kmlCandidateActions = [];
    private readonly ComboBox _offsetMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top };
    private QualityOffsetReference[] _segmentationReferences = [];
    internal Func<QualityOffsetReference[]?>? ResolveOffsetReview;

    private string OffsetMode => _offsetMode.SelectedIndex switch { 1 => "SEGMENTATION_REFERENCE", 2 => "ACTUAL_FIT", _ => "DESIGN_KML" };
    internal void SetOffsetReview(AuditProductionRequest request)
    {
        SetOffsetReferences(QualityOffsetReferences.FromReview(request));
    }

    internal void SetOffsetReferences(QualityOffsetReference[] references)
    {
        if (references is null || references.Length == 0 || references.Any(r => r is null || string.IsNullOrWhiteSpace(r.Identity)) ||
            references.GroupBy(r => r.Identity).Any(g => g.Count() != 1))
            throw new InvalidDataException("分线参考为空或身份重复，请先核对来源。");
        _segmentationReferences = references;
        _offsetMode.SelectedIndex = 1;
        ResetOffsetMappings();
        InvalidateResult(); RefreshPreflight();
        _status.Text = "已读取嵌入分线参考。双击对应表的参考名称单元格，逐母线选择；读取参考不批准身份或QC。";
    }

    private void ResetOffsetMappings()
    {
        bool loading = _loading;
        _loading = true;
        try
        {
            _routes.Rows.Clear();
            if (_table is not null && OffsetMode == "SEGMENTATION_REFERENCE")
            {
                var columns = ReadRequest().Columns;
                foreach (string identity in _table.Rows.Select(r => QualitySpatialIdentity.Key(r, _table.Headers.Length, columns)).Where(id => id.Length > 0).Distinct())
                    _routes.Rows.Add(identity, "", "", false);
            }
        }
        finally { _loading = loading; }
    }

    private void LoadOffsetReview()
    {
        using var choose = new OpenFileDialog { Filter = "分线核验输入/状态 JSON|*.json" };
        FileInteractions.For(this, "quality-review").Prepare(choose);
        if (choose.ShowDialog(this) != DialogResult.OK) return;
        LoadOffsetReviewFile(choose.FileName);
    }

    private void LoadOffsetReviewFile(string path)
    {
        SetOffsetReview(SegmentationReviewStore.LoadFile(path));
        FileInteractions.For(this, "quality-review").InputSucceeded(path);
    }

    private void ChooseOffsetReference(int rowIndex)
    {
        if (OffsetMode != "SEGMENTATION_REFERENCE" || rowIndex < 0 || _routes.Rows[rowIndex].IsNewRow) return;
        using var dialog = new Form { Text = "选择该母线的分线参考", Size = new Size(760, 280), StartPosition = FormStartPosition.CenterParent };
        var list = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
        list.Items.AddRange(_segmentationReferences.Select(r => (object)$"{r.Name} [{r.Identity}] — {r.Evidence}").ToArray());
        var accept = new Button { Text = "采用此参考", Dock = DockStyle.Bottom, Height = 36 };
        accept.Click += (_, _) => { if (list.SelectedIndex >= 0) dialog.DialogResult = DialogResult.OK; };
        dialog.Controls.Add(list); dialog.Controls.Add(accept);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _routes.Rows[rowIndex].Cells[1].Value = _segmentationReferences[list.SelectedIndex].Identity;
        _routes.Rows[rowIndex].Cells[2].Value = "嵌入分线参考";
        InvalidateResult(); RefreshPreflight();
    }

    private string SpatialKey(string[] row, QualityCheckRequest request) => request.Metrics.Contains("OFFSET")
        ? QualitySpatialIdentity.Key(row, _table!.Headers.Length, request.Columns)
        : QualityTimeReader.SegmentKey(row, _table!.Headers.Length, request.Columns);

    private void AddOffsetViews(TabControl tabs)
    {
        _resultTabs = tabs;
        var lines = new TabPage("偏航距逐母线成果");
        var lineLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        lineLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        lineLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); lineLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        lineLayout.Controls.Add(_offsetSummary, 0, 0); lineLayout.Controls.Add(_offsetLines, 0, 1);
        lineLayout.SizeChanged += (_, _) => _offsetSummary.MaximumSize = new Size(Math.Max(120, lineLayout.ClientSize.Width - 8), 0);
        lines.Controls.Add(lineLayout); tabs.TabPages.Add(lines);
        _offsetPreviewTab = new TabPage("设计对应与空间预览");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        actions.Controls.Add(UiComponents.Button("查找候选", (_, _) => Safe(FindSpatialCandidates)));
        actions.Controls.Add(UiComponents.Button("选中未对应首选", (_, _) =>
        {
            _offsetCandidates.ClearSelection();
            var assigned = _routes.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow && !string.IsNullOrWhiteSpace(r.Cells[1].Value?.ToString()))
                .Select(r => r.Cells[0].Value?.ToString()).ToHashSet();
            foreach (DataGridViewRow row in _offsetCandidates.Rows)
                row.Selected = row.DataBoundItem is QualitySpatialCandidate c && c.Rank == 1 && !assigned.Contains(c.Identity);
        }));
        actions.Controls.Add(UiComponents.Button("采用选中候选", (_, _) => Safe(ApplyOffsetCandidates)));
        _kmlCandidateActions.AddRange(actions.Controls.Cast<Control>());
        actions.Controls.Add(UiComponents.Button("适应范围", (_, _) => _offsetPreview.Fit()));
        layout.Controls.Add(actions, 0, 0); layout.Controls.Add(_offsetCandidates, 0, 1);
        layout.Controls.Add(_offsetPreviewNote, 0, 2); layout.Controls.Add(_offsetPreview, 0, 3);
        layout.SizeChanged += (_, _) => _offsetPreviewNote.MaximumSize = new Size(Math.Max(120, layout.ClientSize.Width - 8), 0);
        _offsetPreviewNote.Text = "选择左侧对应行或上方候选查看。蓝色：实测点；绿色：参考线；橙色：端外；红色：未计算。拖动平移，滚轮缩放。";
        _offsetCandidates.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _offsetCandidates.MultiSelect = true;
        _offsetCandidates.SelectionChanged += (_, _) =>
        {
            if (_offsetCandidates.CurrentRow?.DataBoundItem is QualitySpatialCandidate c)
                ShowOffsetPreview(c.Identity, c.KmlPath, c.Placemark);
        };
        _offsetLines.SelectionChanged += (_, _) =>
        {
            if (_offsetLines.CurrentRow?.Cells["母测线号"].Value is string id)
            {
                var line = _result?.OffsetLines.FirstOrDefault(l => l.Identity == id);
                if (line is not null) ShowOffsetPreview(id, line.KmlPath, line.Placemark);
            }
        };
        _offsetPreviewTab.Controls.Add(layout); tabs.TabPages.Add(_offsetPreviewTab);
    }

    private void UpdateOffsetViews()
    {
        bool kml = OffsetMode == "DESIGN_KML";
        foreach (var action in _kmlCandidateActions) action.Visible = kml;
        _offsetCandidates.Visible = kml;
        if (_offsetCandidates.Parent is TableLayoutPanel layout)
        {
            layout.RowStyles[1].Height = kml ? 30 : 0;
            layout.RowStyles[3].Height = kml ? 70 : 100;
        }
    }

    private void RefreshOffsetPreflight()
    {
        if (_offsetPreflight is null || !_metrics.TryGetValue("OFFSET", out var box) || !box.Checked) return;
        var issues = new List<string>();
        if (_table is null) issues.Add("选择正式测线表格。");
        if (OffsetMode == "DESIGN_KML" && string.IsNullOrWhiteSpace(_kml.Text)) issues.Add("选择设计测线KML或文件夹。");
        if (_columns["mother"].SelectedIndex == 0) issues.Add("建议映射母测线号；未映射时按外部段号/整表FILE计算，不能声称逐母线成果。");
        if (_columns["lon"].SelectedIndex == 0 || _columns["lat"].SelectedIndex == 0 || !_wgs.Checked) issues.Add("映射经纬度并确认WGS84（度）。");
        if (OffsetMode == "ACTUAL_FIT") issues.Add("实测拟合偏离：按母线/线号分组，反映直线离散；不反映设计位置误差，不自动剔除异常点。");
        if (OffsetMode == "SEGMENTATION_REFERENCE" && _segmentationReferences.Length == 0) issues.Add("请复用当前或载入已保存分线参考。");
        if (_table is not null && (OffsetMode != "DESIGN_KML" || _kml.Text.Length > 0))
        {
            try
            {
                var checks = QualityCheckService.CheckOffsetRoutes(ReadRequest(), _table);
                var failed = checks.Where(c => c.Status != "READY").ToArray();
                if (failed.Length > 0) issues.Add($"{checks.Length - failed.Length}/{checks.Length}条设计对应就绪；待处理：" + string.Join("；", failed.Take(4).Select(c => c.Identity + "：" + c.Message)) + (failed.Length > 4 ? "等" : "") + "。其余母线仍可计算。");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or XmlException or ArgumentException)
            { issues.Add("设计来源：" + ex.Message); }
        }
        _offsetPreflight.Text = "偏航距输入：" + (issues.Count > 0 ? string.Join(" ", issues) : "必要字段已齐备。对应缺失仅影响相应母线；大偏差不按匹配容差剔除。");
        _offsetPreflight.ForeColor = issues.Count > 0 ? UiTheme.Warning : UiTheme.Success;
    }

    private void FindSpatialCandidates()
    {
        if (OffsetMode != "DESIGN_KML") throw new InvalidOperationException("空间候选用于设计KML模式；复用分线参考请双击对应行，实测拟合无需候选。");
        if (_table is null || !_wgs.Checked || _columns["lon"].SelectedIndex == 0 || _columns["lat"].SelectedIndex == 0)
            throw new InvalidOperationException("先选择表格、映射经纬度并确认WGS84。");
        var request = ReadRequest();
        var selectedTable = _table with { Rows = _table.Rows.Skip(request.FirstRecord - 1).Take((request.LastRecord ?? _table.Rows.Count) - request.FirstRecord + 1).ToList() };
        var candidates = BusyOperationDialog.Run(FindForm()!, "查找母线设计候选", "按整条实测线比较设计几何；结果需核对后采用…", () =>
            QualityRouteCatalog.SuggestSpatial(QualityRouteCatalog.Read(request.KmlPath), selectedTable, request.Columns));
        _offsetCandidates.DataSource = candidates;
        foreach (var (key, title) in new[] { ("Identity", "母测线号"), ("KmlPath", "设计文件"), ("Placemark", "设计线名"), ("MedianDistanceM", "距离中位数_米"), ("ComputablePointRatio", "抽样可计算点比例"), ("Rank", "候选顺序") })
            if (_offsetCandidates.Columns[key] is { } column) column.HeaderText = title;
        _offsetCandidates.ClearSelection();
        _resultTabs!.SelectedTab = _offsetPreviewTab;
        _status.Text = "空间候选仅为建议，尚未采用。可多选不同母线的候选后一次采用；弯折或整架次路径仍须在对应表核对母线范围。";
    }

    private void ApplyOffsetCandidates()
    {
        if (OffsetMode != "DESIGN_KML") throw new InvalidOperationException("仅设计KML模式可采用KML候选。");
        var candidates = _offsetCandidates.SelectedRows.Cast<DataGridViewRow>().Select(r => r.DataBoundItem).OfType<QualitySpatialCandidate>().ToArray();
        if (candidates.Length == 0) throw new InvalidOperationException("先选择要采用的候选行。");
        if (candidates.GroupBy(c => c.Identity).Any(g => g.Count() > 1)) throw new InvalidOperationException("同一母线选中了多个候选，请保留一个。");
        _loading = true;
        try
        {
            foreach (var c in candidates)
            {
                var row = _routes.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => !r.IsNewRow && r.Cells[0].Value?.ToString() == c.Identity);
                if (row is null) row = _routes.Rows[_routes.Rows.Add(c.Identity, c.Placemark, c.KmlPath, false)];
                else { row.Cells[1].Value = c.Placemark; row.Cells[2].Value = c.KmlPath; row.Cells[3].Value = false; }
            }
        }
        finally { _loading = false; }
        InvalidateResult(); RefreshOffsetPreview();
        _status.Text = $"已采用{candidates.Length}条设计对应。弯折线须核对预览后勾选“已核对母线范围”；整架次多测线路径请改选明确的母线设计几何。";
    }

    private void RefreshOffsetPreview()
    {
        if (_loading || _refreshingOffset || _table is null || _routes.CurrentRow is not { IsNewRow: false } row) return;
        ShowOffsetPreview(row.Cells[0].Value?.ToString() ?? "", row.Cells[2].Value?.ToString() ?? "", row.Cells[1].Value?.ToString() ?? "");
    }

    private void ShowOffsetPreview(string identity, string path, string placemark)
    {
        if (_refreshingOffset || _table is null) return;
        _refreshingOffset = true;
        try
        {
            var columns = new QualityColumns(Longitude: _columns["lon"].SelectedIndex - 1, Latitude: _columns["lat"].SelectedIndex - 1,
                Mother: _columns["mother"].SelectedIndex > 0 ? _columns["mother"].SelectedIndex - 1 : null,
                Segment: _columns["segment"].SelectedIndex > 0 ? _columns["segment"].SelectedIndex - 1 : null);
            var points = new List<QualityOffsetPreview.Point>();
            var reasons = _result?.Details.Where(d => d.Metric == "OFFSET").ToDictionary(d => d.FirstRecord, d => d.ReasonCode);
            int first = (int)_first.Value, last = _last.Value == 0 ? _table.Rows.Count : Math.Min((int)_last.Value, _table.Rows.Count);
            int matched = 0;
            // Select the entire mother first; global row sampling can erase short lines.
            for (int index = first - 1; index < last; index++)
            {
                var row = _table.Rows[index];
                if (QualitySpatialIdentity.Key(row, _table.Headers.Length, columns) != identity) continue;
                matched++;
                if (columns.Longitude is not { } x || columns.Latitude is not { } y || x < 0 || y < 0 || row.Length != _table.Headers.Length) continue;
                if (double.TryParse(row[x], NumberStyles.Float, CultureInfo.InvariantCulture, out double lon) && double.TryParse(row[y], NumberStyles.Float, CultureInfo.InvariantCulture, out double lat) && double.IsFinite(lon) && double.IsFinite(lat) && lon is >= -180 and <= 180 && lat is >= -90 and <= 90)
                    points.Add(new(lon, lat, reasons?.GetValueOrDefault(index + 1) ?? ""));
            }
            var design = new List<QualityOffsetPreview.Point>();
            string error = "";
            try
            {
                if (OffsetMode == "DESIGN_KML" && path.Length == 0 && _kml.Text.Length > 0 && placemark.Length > 0)
                {
                    var hits = QualityRouteCatalog.Read(_kml.Text).Entries.Where(e => e.Placemark == placemark).ToArray();
                    if (hits.Length == 1) path = hits[0].Path;
                }
                if (OffsetMode == "DESIGN_KML" && path.Length > 0)
                {
                    using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                    var doc = XDocument.Load(reader);
                    var lines = doc.Descendants().Where(e => e.Name.LocalName == "Placemark" && e.Elements().Any(n => n.Name.LocalName == "name" && n.Value == placemark))
                        .SelectMany(e => e.Descendants().Where(n => n.Name.LocalName == "LineString")).ToArray();
                    if (lines.Length != 1) throw new InvalidDataException("设计几何不唯一。");
                    foreach (string tuple in (lines[0].Elements().FirstOrDefault(e => e.Name.LocalName == "coordinates")?.Value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var pair = tuple.Split(',');
                        if (pair.Length >= 2 && double.TryParse(pair[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double x) && double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double y) && double.IsFinite(x) && double.IsFinite(y) && x is >= -180 and <= 180 && y is >= -90 and <= 90) design.Add(new(x, y, ""));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or XmlException or InvalidDataException or UnauthorizedAccessException) { error = "；设计预览未完成：" + ex.Message; }
            if (OffsetMode != "DESIGN_KML")
            {
                design.Clear();
                var reference = _result?.OffsetReferenceGeometries.SingleOrDefault(r => r.Identity == identity);
                if (reference is null && OffsetMode == "SEGMENTATION_REFERENCE")
                {
                    string? id = _routes.Rows.Cast<DataGridViewRow>().FirstOrDefault(r => !r.IsNewRow && Convert.ToString(r.Cells[0].Value) == identity)?.Cells[1].Value?.ToString();
                    reference = _segmentationReferences.SingleOrDefault(r => r.Identity == id);
                }
                if (reference is not null) design.AddRange(reference.Vertices.Select(v => new QualityOffsetPreview.Point(v.Longitude, v.Latitude, "")));
            }
            _offsetPreview.SetData(points.ToArray(), design.ToArray());
            _offsetPreviewNote.Text = $"{identity} → {placemark}；范围内{matched}条，有效坐标{points.Count}条，无效坐标{matched - points.Count}条。蓝：实测点，绿：当前参考线，橙：端外，红：未计算。显示数量见图内；缩放后按可见范围重采样，统计保留全部。拖动/滚轮缩放。" + error;
        }
        finally { _refreshingOffset = false; }
    }

    private void PopulateOffsetResults()
    {
        if (_result is null) return;
        var summary = QualityCheckService.OffsetSummary(_result);
        string M(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "—";
        _offsetSummary.Text = summary is null ? "本次未计算偏航距。" :
            QualityCheckService.OffsetBasis(_result.Request) + Environment.NewLine + $"母线可计算{summary.ComputableLines}/{summary.InputLines}条；点可计算{summary.ValidPoints}/{summary.InputPoints}（{summary.ComputablePointRatio:P1}）。" + Environment.NewLine +
            $"点加权均值{M(summary.PointWeightedMeanM)} m；最大{M(summary.MaximumM)} m；RMS {M(summary.RmsM)} m；P95 {M(summary.P95M)} m；母线等权均值{M(summary.EqualLineMeanM)} m。" + Environment.NewLine +
            $"端外{summary.EndOutsidePoints}，未对应{summary.UnmappedPoints}，无效坐标{summary.InvalidCoordinatePoints}，其他缺项{summary.OtherRejectedPoints}；仅可计算范围参考，不自动判定合格。";
        _offsetLines.DataSource = _result.OffsetLines.Select(l => new { 线序 = l.LineOrder, 母测线号 = l.Identity,
            平均偏航距_米 = l.MeanM, 最大偏航距_米 = l.MaximumM, 均方根_米 = l.RmsM, P95_米 = l.P95M,
            输入点数 = l.InputPoints, 有效点数 = l.ValidPoints, 端外点数 = l.EndOutsidePoints, 未对应点数 = l.UnmappedPoints,
            坐标无效点数 = l.InvalidCoordinatePoints, 可计算点比例 = l.ComputablePointRatio, 状态 = StatusOf(l.Status) }).ToArray();
        if (_result.Request.OffsetReferenceMode == "ACTUAL_FIT")
            foreach (DataGridViewColumn column in _offsetLines.Columns) column.HeaderText = column.HeaderText.Replace("偏航距", "拟合偏离");
        if (_result.OffsetLines.FirstOrDefault() is { } first)
            ShowOffsetPreview(first.Identity, first.KmlPath, first.Placemark);
    }

    private static IReadOnlyDictionary<string, byte[]> RenderQualityFigures(QualityCheckResult result)
    {
        var figures = new Dictionary<string, byte[]>(ReportQualityFigures.RenderDynamicNoise(result));
        if (result.OffsetLines.Any(l => l.MeanM is not null)) figures[result.Request.OffsetReferenceMode == "ACTUAL_FIT" ? "实测拟合偏离分布图" : "偏航距分布图"] = QualityOffsetFigure.Render(result);
        return figures;
    }
}
