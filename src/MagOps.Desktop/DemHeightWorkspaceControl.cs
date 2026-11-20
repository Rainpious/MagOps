using System.Security.Cryptography;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

/// <summary>Pre-recognition calculation and read-only review. Owns no production decisions.</summary>
internal sealed class DemHeightWorkspaceControl : UserControl
{
    private readonly TextBox _path = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly TextBox _observationDatum = new() { Width = 160, PlaceholderText = "基准及来源" };
    private readonly TextBox _demDatum = new() { Width = 160, PlaceholderText = "DEM 基准" };
    private readonly TextBox _evidence = new() { Width = 300, PlaceholderText = "坐标、高程基准及米单位的核对依据" };
    private readonly CheckBox _confirmed = new() { AutoSize = true, Text = "已核对坐标、高程基准及米单位相容" };
    private readonly CheckBox _automatic = new() { AutoSize = true, Text = "导入后自动计算", Checked = true };
    private readonly Label _summary = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Label _detail = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly ComboBox _filter = new() { Width = 180, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _source = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly DataGridView _grid = new() { Dock = DockStyle.Fill, VirtualMode = true, ReadOnly = true,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoGenerateColumns = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, RowHeadersVisible = false };
    private readonly DemHeightPreviewPanel _preview = new();
    private readonly TableLayoutPanel _settings = new() { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 4 };
    private readonly SplitContainer _content = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
    private readonly Button _calculate;
    private readonly Button _export;
    private readonly FieldMagFolderAnalysis _analysis;
    private CancellationTokenSource? _work;
    private DemHeightSnapshot? _snapshot;
    private DemHeightPoint[] _rows = [];
    private string _confirmedHash = "";
    private bool _loading;
    private bool _configurationDirty;
    private bool _confirmationPending;
    private string[] _sourcePaths = [];
    internal event Action<DemHeightOptions, DemHeightSnapshot>? CalculationCompleted;
    internal event Action<DemHeightOptions>? OptionsChanged;
    internal event Action<string>? ExportCompleted;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string OutputDirectory { get; set; } = "";

    internal DemHeightWorkspaceControl(FieldMagFolderAnalysis analysis, DemHeightOptions options, DemHeightSnapshot? snapshot)
    {
        _analysis = analysis;
        Dock = DockStyle.Fill;
        Name = "DemHeightWorkspace";
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(8) };
        root.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.AutoSize));
        root.RowStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 48));
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        _calculate = UiComponents.Button("计算离地高度", async (_, _) => await CalculateAsync(), UiButtonRole.Primary);
        _export = UiComponents.Button("导出明细", async (_, _) => await ExportAsync());
        actions.Controls.AddRange([_calculate, _export, _automatic]);
        actions.Controls.Add(UiComponents.Button("下载地形", (_, _) => { using var dialog = new Aw3d30DownloadDialog(); dialog.ShowDialog(this); }));
        actions.Controls.Add(UiComponents.HelpLabel("基于 DEM 的估计；不依赖分线、KML 或磁通道，不改变原始海拔和测线归属。"));
        var choose = UiComponents.Button("选择 DEM（可拖入）…", (_, _) => ChooseDem());
        foreach (var target in new Control[] { choose, _path })
            FileDropSupport.EnableMultiple(target, paths => ChooseDemFiles(paths),
                p => FileDropSupport.HasExtension(p, ".tif", ".tiff", ".json"),
                canImport: () => _work is null && _settings.Enabled, onError: message => _summary.Text = message);
        _path.AccessibleName = "离地高度 DEM 路径";
        _observationDatum.AccessibleName = "实测海拔基准及来源";
        _demDatum.AccessibleName = "DEM 高程基准";
        _evidence.AccessibleName = "坐标、高程和单位相容依据";
        _settings.ColumnStyles.Add(new(SizeType.AutoSize));
        _settings.ColumnStyles.Add(new(SizeType.AutoSize));
        _settings.ColumnStyles.Insert(1, new(SizeType.Percent, 50));
        _settings.ColumnStyles.Add(new(SizeType.Percent, 50));
        foreach (int row in Enumerable.Range(0, 4)) _settings.RowStyles.Add(new(SizeType.AutoSize));
        _settings.Controls.Add(new Label { Text = "DEM 文件", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        _settings.Controls.Add(_path, 1, 0); _settings.SetColumnSpan(_path, 2); _settings.Controls.Add(choose, 3, 0);
        _observationDatum.Dock = _demDatum.Dock = _evidence.Dock = DockStyle.Fill;
        _settings.Controls.Add(new Label { Text = "实测海拔基准", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        _settings.Controls.Add(_observationDatum, 1, 1);
        _settings.Controls.Add(new Label { Text = "DEM 高程基准", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 1);
        _settings.Controls.Add(_demDatum, 3, 1);
        _settings.Controls.Add(new Label { Text = "相容依据", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        _settings.Controls.Add(_evidence, 1, 2); _settings.SetColumnSpan(_evidence, 3);
        _settings.Controls.Add(_confirmed, 0, 3); _settings.SetColumnSpan(_confirmed, 4);
        _path.ShortcutsEnabled = true; // Home/End, selection and Ctrl+C expose the complete read-only path.
        _path.AccessibleDescription = "单波段 EPSG:4326 GeoTIFF（未压缩条带或 Copernicus COG）；不自动转换基准。";
        var filters = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        _filter.Items.AddRange(["全部记录", "已计算", "缺项记录", "负离地高度"]); _filter.SelectedIndex = 0;
        var view = new ComboBox { Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
        view.Items.AddRange(["空间着色", "高度剖面"]); view.SelectedIndex = 0;
        view.SelectedIndexChanged += (_, _) => _preview.SetProfile(view.SelectedIndex == 1);
        filters.Controls.AddRange([_filter, _source, view]);
        filters.Controls.Add(UiComponents.HelpLabel("蓝→橙：飞高由低到高；红：负值；灰：缺项。剖面不跨来源、缺项或时间断口连线。"));
        _filter.SelectedIndexChanged += (_, _) => RefreshRows();
        _source.SelectedIndexChanged += (_, _) => RefreshRows();
        foreach (var c in new[] { ("输入序号", 75), ("来源文件", 190), ("源记录", 75), ("时间 UTC", 170),
            ("海拔 m", 95), ("地形高程 m", 105), ("离地高度 m", 105), ("状态", 170) })
            _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = c.Item1, Width = c.Item2, SortMode = DataGridViewColumnSortMode.NotSortable });
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        foreach (DataGridViewColumn column in _grid.Columns) { column.FillWeight = column.Width; column.MinimumWidth = 65; }
        _grid.CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _rows.Length) return;
            var p = _rows[e.RowIndex];
            e.Value = e.ColumnIndex switch
            {
                0 => p.InputIndex + 1, 1 => Path.GetFileName(p.Source.SourceFile), 2 => p.Source.SourceRecord,
                3 => p.Source.TimestampUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                4 => Format(p.Source.AltitudeM), 5 => Format(p.TerrainElevationM), 6 => Format(p.AboveGroundHeightM),
                _ => DemHeightService.Describe(p.Status)
            };
        };
        _grid.CurrentCellChanged += (_, _) => RefreshSelectedPoint();
        root.Controls.Add(actions, 0, 0); root.Controls.Add(_settings, 0, 1); root.Controls.Add(_summary, 0, 2);
        _content.Size = new Size(800, 320); _content.Panel1MinSize = 80; _content.Panel2MinSize = 70; _content.SplitterDistance = 170;
        _content.AccessibleName = "高度预览与明细分隔，可拖动调整";
        _content.Panel1.Controls.Add(_preview); _content.Panel2.Controls.Add(_grid);
        root.Controls.Add(filters, 0, 3); root.Controls.Add(_content, 0, 4); root.Controls.Add(_detail, 0, 5);
        root.SizeChanged += (_, _) => _summary.MaximumSize = new Size(Math.Max(1, root.ClientSize.Width - root.Padding.Horizontal - 6), 0);
        Controls.Add(root);
        foreach (var input in new[] { _observationDatum, _demDatum, _evidence }) input.TextChanged += (_, _) => SettingsEdited(true);
        _confirmed.CheckedChanged += (_, _) =>
        {
            if (!_loading) { _confirmationPending = _confirmed.Checked; if (!_confirmed.Checked) _confirmedHash = ""; }
            SettingsEdited(false);
        };
        _automatic.CheckedChanged += (_, _) => SettingsEdited(false);
        LoadOptions(options);
        SetSnapshot(snapshot);
    }

    private static string Format(double? value) => value?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) ?? "—";
    private DemHeightOptions ReadOptions() => new()
    {
        DemPath = _path.Text, ObservationHeightDatum = _observationDatum.Text.Trim(), DemHeightDatum = _demDatum.Text.Trim(),
        CompatibilityEvidence = _evidence.Text.Trim(), CompatibilityConfirmed = _confirmed.Checked,
        CalculateOnImport = _automatic.Checked, ConfirmedDemSha256 = _confirmedHash
    };
    private void LoadOptions(DemHeightOptions options)
    {
        _loading = true;
        _path.Text = options.DemPath; _observationDatum.Text = options.ObservationHeightDatum;
        _demDatum.Text = options.DemHeightDatum; _evidence.Text = options.CompatibilityEvidence;
        _confirmed.Checked = options.CompatibilityConfirmed; _automatic.Checked = options.CalculateOnImport;
        _confirmedHash = options.ConfirmedDemSha256; _confirmationPending = false; _loading = false;
    }
    private void SettingsEdited(bool revokeConfirmation)
    {
        if (_loading) return;
        if (revokeConfirmation) { _loading = true; _confirmed.Checked = false; _confirmedHash = ""; _loading = false; }
        _configurationDirty = true; _export.Enabled = false;
        _summary.Text = "设置已变更，请重新计算；旧快照不再作为当前分线参考。";
        OptionsChanged?.Invoke(ReadOptions());
    }
    private void ChooseDem() => ChooseDemFiles(null);
    private void ChooseDemFiles(IReadOnlyList<string>? paths)
    {
        var files = FileInteractions.For(this, "terrain-import");
        try
        {
            var library=new TerrainLibraryService();
            if(string.IsNullOrWhiteSpace(library.Settings().DefaultDirectory)) {
                using var folder=new FolderBrowserDialog{Description="设置默认公共地形资料库",ShowNewFolderButton=true};
                var libraryHistory = FileInteractions.For(this, "terrain-library", projectLevel: true, global: true);
                libraryHistory.Prepare(folder, current: library.Settings().DefaultDirectory);
                if(folder.ShowDialog(this)!=DialogResult.OK)return;library.SetDefault(folder.SelectedPath);
                libraryHistory.DirectorySucceeded(folder.SelectedPath);
            }
            if (paths is null)
            {
                using var dialog = new OpenFileDialog { Filter = "地形瓦片或集合|*.terrain.json;*.tif;*.tiff", CheckFileExists = true, Multiselect=true };
                files.Prepare(dialog, business: library.Settings().DefaultDirectory);
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                paths = dialog.FileNames;
            }
            if (paths.Count == 0) return;
            TerrainSelection selection;
            if(paths.Count==1 && paths[0].EndsWith(".terrain.json",StringComparison.OrdinalIgnoreCase))
                selection=TerrainLibraryService.ReadSelection(paths[0]);
            else {
                if(paths.Any(p=>p.EndsWith(".json",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException("瓦片集合请单独选择。");
                using var choose=new TerrainTileSelectionDialog(library.Import(paths));
                if(choose.ShowDialog(this)!=DialogResult.OK)return;
                selection=library.Select(choose.SelectedAssets,true);
            }
            string manifest=Path.Combine(Path.GetDirectoryName(TerrainLibraryService.SettingsPath)!,"selections",Guid.NewGuid().ToString("N")+".terrain.json");
            if(!string.IsNullOrWhiteSpace(selection.PortableDirectory)) {
                selection=library.Select(library.ImportPortableSelection(paths[0]),selection.OverlapOrderConfirmed);
            }
            TerrainLibraryService.SaveSelection(manifest,selection);new TerrainSetSampler(manifest).ValidateForHeightCalculation();
            _path.Text=manifest;SettingsEdited(true);
            files.InputSucceeded(paths.ToArray());
        }
        catch(Exception ex) when(ex is IOException or InvalidOperationException or UnauthorizedAccessException) {
            MessageBox.Show(this,ex.Message,"地形导入失败",MessageBoxButtons.OK,MessageBoxIcon.Warning);
        }
    }
    private async Task CalculateAsync()
    {
        if (_work is not null) return;
        var options = ReadOptions();
        bool bindNewConfirmation = _confirmationPending;
        _work = new(); var token = _work.Token;
        _calculate.Enabled = false; _export.Enabled = false; _settings.Enabled = false; _automatic.Enabled = false;
        _summary.Text = "正在读取 DEM 并计算离地高度…";
        try
        {
            var snapshot = await Task.Run(() =>
            {
                if (bindNewConfirmation && options.CompatibilityConfirmed && File.Exists(options.DemPath))
                {
                    options = options with { ConfirmedDemSha256 = new TerrainSetSampler(options.DemPath).ContentSha256 };
                }
                return DemHeightService.Calculate(_analysis, options, token);
            }, token);
            if (IsDisposed || token.IsCancellationRequested) return;
            bool staleConfirmation = snapshot.Options.CompatibilityConfirmed &&
                !string.Equals(snapshot.Options.ConfirmedDemSha256, snapshot.DemSha256, StringComparison.OrdinalIgnoreCase);
            LoadOptions(staleConfirmation ? snapshot.Options with { CompatibilityConfirmed = false, ConfirmedDemSha256 = "" } : snapshot.Options);
            _configurationDirty = false; SetSnapshot(snapshot);
            CalculationCompleted?.Invoke(ReadOptions(), snapshot);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!IsDisposed) _summary.Text = "计算未完成，分线可继续：" + ex.Message; }
        finally
        {
            _work?.Dispose(); _work = null;
            if (!IsDisposed) { _calculate.Enabled = true; _settings.Enabled = true; _automatic.Enabled = true; }
        }
    }
    internal void SetSnapshot(DemHeightSnapshot? snapshot)
    {
        if (snapshot?.SourceFingerprint != _analysis.Fingerprint) snapshot = null;
        _snapshot = snapshot;
        _export.Enabled = snapshot is { DemSha256.Length: > 0 } && !_configurationDirty;
        var q = snapshot?.Summary;
        _summary.Text = q is null ? "尚未计算。支持单波段 EPSG:4326 GeoTIFF（未压缩条带或 Copernicus COG）；未确认基准时仅提供地形高程。离地高度缺项不阻断分线。" :
            $"原始输入 {q.InputCount:N0} 点；地形可采样 {snapshot!.Points.Count(p => p.TerrainElevationM.HasValue):N0} 点；离地高度可计算 {q.ValidCount:N0} 点；负值 {q.NegativeCount:N0} 点。\r\n" +
            (q.ValidCount == 0 ? "暂无可计算离地高度。" + PendingConditions(snapshot) :
                $"平均 {Format(q.MeanM)} m；最小 {Format(q.MinM)} m；最大 {Format(q.MaxM)} m。") + "\r\n" +
            (snapshot!.Notice.Length > 0 ? snapshot.Notice : "全导入点集，包含非作业轨迹；仅供分线参考。负值保留，请检查数据及基准。") +
            " " + string.Join("；", snapshot.Points.Where(p => p.Status != DemHeightStatus.Calculated).GroupBy(p => p.Status)
                .Select(g => $"{DemHeightService.Describe(g.Key)} {g.Count():N0}"));
        _sourcePaths = snapshot?.Points.Select(p => p.Source.SourceFile).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        _loading = true; _source.Items.Clear(); _source.Items.Add("全部来源");
        foreach (var path in _sourcePaths) _source.Items.Add(Path.GetFileName(path));
        _source.SelectedIndex = 0; _loading = false; RefreshRows();
    }
    private static string PendingConditions(DemHeightSnapshot snapshot)
    {
        var o = snapshot.Options;
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(o.DemPath)) missing.Add("未选择DEM");
        if (string.IsNullOrWhiteSpace(o.ObservationHeightDatum)) missing.Add("未填写实测海拔基准");
        if (string.IsNullOrWhiteSpace(o.DemHeightDatum)) missing.Add("未填写DEM高程基准");
        if (string.IsNullOrWhiteSpace(o.CompatibilityEvidence)) missing.Add("未填写相容依据");
        if (!o.CompatibilityConfirmed) missing.Add("尚未勾选基准及米单位相容确认");
        if (snapshot.DemSha256.Length == 0) missing.Add("DEM未能读取，请核对路径及格式");
        else if (!string.Equals(o.ConfirmedDemSha256, snapshot.DemSha256, StringComparison.OrdinalIgnoreCase))
            missing.Add("确认未绑定当前DEM内容");
        return missing.Count == 0 ? "请核对缺海拔、越界或NoData记录。" : string.Join("；", missing) + "；核对依据后重新计算。";
    }
    private void RefreshRows()
    {
        if (_loading) return;
        string? source = _source.SelectedIndex > 0 ? _sourcePaths[_source.SelectedIndex - 1] : null;
        _rows = _snapshot?.Points.Where(p => (source is null || p.Source.SourceFile.Equals(source, StringComparison.OrdinalIgnoreCase)) &&
            (_filter.SelectedIndex switch { 1 => p.Status == DemHeightStatus.Calculated, 2 => p.Status != DemHeightStatus.Calculated,
                3 => p.AboveGroundHeightM < 0, _ => true })).ToArray() ?? [];
        _grid.RowCount = _rows.Length; _grid.Invalidate(); _preview.SetPoints(_rows, _snapshot?.Fingerprint ?? "");
        RefreshSelectedPoint();
    }
    private void RefreshSelectedPoint()
    {
        int index = _grid.CurrentCell?.RowIndex ?? -1;
        var p = index >= 0 && index < _rows.Length ? _rows[index] : null;
        _preview.SelectPoint(p);
        _detail.Text = p is null ? "选择记录查看来源。" :
            $"来源：{p.Source.SourceFile}；记录 {p.Source.SourceRecord}；经纬度 {p.Source.Longitude}, {p.Source.Latitude}。\r\n" +
            $"{DemHeightService.Describe(p.Status)}；海拔 {Format(p.Source.AltitudeM)} m－地形 {Format(p.TerrainElevationM)} m＝离地高度 {Format(p.AboveGroundHeightM)} m。";
    }
    private async Task ExportAsync()
    {
        if (_work is not null || _snapshot is null || _configurationDirty) return;
        var snapshot = _snapshot;
        _work = new(); _settings.Enabled = false; _automatic.Enabled = false; _calculate.Enabled = false; _export.Enabled = false;
        try
        {
            var path = await Task.Run(() => DemHeightService.Export(snapshot, OutputDirectory));
            if (!IsDisposed) { _summary.Text = "已导出：" + path; ExportCompleted?.Invoke(path); }
        }
        catch (Exception ex) { if (!IsDisposed) _summary.Text = "导出未完成：" + ex.Message; }
        finally
        {
            _work?.Dispose(); _work = null;
            if (!IsDisposed) { _settings.Enabled = true; _automatic.Enabled = true; _calculate.Enabled = true; _export.Enabled = true; }
        }
    }
    internal async Task VerifyCurrentAsync()
    {
        if (_work is not null || _snapshot is not { } snapshot || _configurationDirty) return;
        bool current = await Task.Run(() => DemHeightService.DemIsCurrent(snapshot) && DemHeightService.SourcesAreCurrent(snapshot));
        if (IsDisposed || !ReferenceEquals(snapshot, _snapshot)) return;
        if (!current)
        {
            _loading = true; _confirmed.Checked = false; _confirmedHash = ""; _loading = false;
            _configurationDirty = true; SetSnapshot(null);
            _summary.Text = "DEM 或原始来源已改变/不可读取，旧结果已停用。请核对 DEM，或重新扫描原始数据后计算；原有分线可继续。";
            OptionsChanged?.Invoke(ReadOptions());
        }
    }
    protected override void Dispose(bool disposing)
    { if (disposing) _work?.Cancel(); base.Dispose(disposing); }
}
