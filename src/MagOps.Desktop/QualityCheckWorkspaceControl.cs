using System.Diagnostics;
using System.Globalization;
using System.Xml;
using MagOps.Core.Analysis;
using MagOps.Reporting;

namespace MagOps.Desktop;

internal sealed partial class QualityCheckWorkspaceControl : UserControl
{
    private readonly TextBox _source = Box(true), _kml = Box(true), _channel = Box(), _conditions = Box(), _datum = Box(), _agl = Box();
    private readonly Dictionary<string, ComboBox> _columns = new();
    private readonly Label _recognition = new() { AutoSize = true, MaximumSize = new Size(430, 0) };
    private readonly Dictionary<string, CheckBox> _metrics = new();
    private readonly CheckBox _autoInterval = Check("根据时间列自动确定采样间隔"), _advanced = Check("展开范围和高级参数");
    private readonly Label _timeEvidence = new() { AutoSize = true, MaximumSize = new Size(430, 0) };
    private readonly Label _preflight = new() { AutoSize = true, MaximumSize = new Size(430, 0), AccessibleName = "选择参数时显示的阻断项" };
    private readonly List<(string Title, Control Label, Control Value)> _fields = [];
    private readonly DataGridView _groups = Grid(), _parameters = Grid();
    private readonly DataGridView _dynamicLines = Grid();
    private readonly ComboBox _noisePage = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly PictureBox _noisePicture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.White };
    private IReadOnlyDictionary<string, byte[]> _noiseFigures = new Dictionary<string, byte[]>();
    private TextTable? _table;
    private readonly CheckBox _cadence = Check("无相对秒列时，确认按行序等间隔采样"), _segments = Check("确认段号区分采集段/Pass；无段号则范围为同一采集段"),
        _nt = Check("确认所选磁通道的单位为 nT"), _wgs = Check("确认经纬度为 WGS84（度）"), _height = Check("确认实测/KML高程均为米，且绝对高程基准一致"),
        _gradient = Check("动态噪声剔除水平梯度超限点");
    private readonly NumericUpDown _interval = Numeric(.5m, .001m, 3600, 3), _first = Numeric(1, 1, int.MaxValue),
        _last = Numeric(0, 0, int.MaxValue), _limit = Numeric(600, 1, 1000000), _tolerance = Numeric(100, .01m, 100000, 2);
    private readonly DataGridView _routes = new() { Dock = DockStyle.Fill, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill },
        _summary = Grid(), _details = Grid();
    private readonly Label _status = new() { Dock = DockStyle.Fill, AutoSize = true, MaximumSize = new Size(1000, 0), Text = "先选择文件并核对字段。数据记录从1开始，不含表头。" };
    private readonly Button _export, _open, _openLineTable;
    private string _lineTablePath = "";
    private QualityCheckResult? _result;
    private string _sourceHash = "", _output = "";
    private bool _loading;
    private QualityHeaderSuggestion[] _headerSuggestions = [];

    public QualityCheckWorkspaceControl()
    {
        Dock = DockStyle.Fill;
        AccessibleName = "独立质量验算工作区";
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = UiTheme.PagePadding };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(UiComponents.PageHeader("质量验算", "独立读取其他航磁成果，无需项目或前序处理。逐项检查输入，查看参考数值与计算明细；不自动判定合格。"), 0, 0);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        var chooseSource = UiComponents.Button("选择表格（可拖入）…", (_, _) => Safe(ChooseSource));
        FileDropSupport.EnableSingle(chooseSource, LoadSource, p => FileDropSupport.HasExtension(p, ".csv", ".txt", ".tsv"),
            canImport: () => !_loading && Enabled, onError: message => _status.Text = "未导入：" + message);
        actions.Controls.Add(chooseSource);
        actions.Controls.Add(UiComponents.Button("计算所选指标", (_, _) => Safe(Calculate), UiButtonRole.Primary));
        _export = UiComponents.Button("导出本次验算…", (_, _) => Safe(Export)); _export.Enabled = false; actions.Controls.Add(_export);
        _openLineTable = UiComponents.Button("打开逐线成果表", (_, _) => Safe(() =>
        { if (File.Exists(_lineTablePath)) Process.Start(new ProcessStartInfo(_lineTablePath) { UseShellExecute = true });
          else _status.Text = "逐线表尚未导出；先点击“导出本次验算…”，完成后可从这里直接打开。"; }));
        _openLineTable.Enabled = false; actions.Controls.Add(_openLineTable);
        _open = UiComponents.Button("打开结果目录", (_, _) => Safe(() => Process.Start(new ProcessStartInfo(_output) { UseShellExecute = true }))); _open.Enabled = false; actions.Controls.Add(_open);
        root.Controls.Add(actions, 0, 1);
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 47)); body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 53)); body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var scroll = new Panel { AutoScroll = true, Dock = DockStyle.Fill };
        var form = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(4) };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 122)); form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Add(string title, Control control)
        {
            int row = form.RowCount++; form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            form.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new Padding(3, 7, 3, 3) }, 0, row);
            control.AccessibleName = title; control.Dock = DockStyle.Fill; form.Controls.Add(control, 1, row);
            _fields.Add((title, form.GetControlFromPosition(0, row)!, control));
        }
        void Wide(Control control)
        {
            int row = form.RowCount++; form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            form.Controls.Add(control, 0, row); form.SetColumnSpan(control, 2);
            _fields.Add((control.Text, control, control));
        }
        Add("来源文件（可拖入）", _source);
        _source.AccessibleDescription = "可拖入一个CSV、TXT或TSV文件，与选择表格使用同一读取流程。";
        var choices = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        foreach (var item in new[] { ("STATIC", "静态磁噪声"), ("DYNAMIC", "动态磁噪声"), ("OFFSET", "偏航距"), ("HEIGHT", "飞高偏差") })
        {
            bool frozen = item.Item1 is "STATIC" or "HEIGHT";
            var box = Check(item.Item2 + (frozen ? "（正在开发）" : ""));
            box.Enabled = !frozen;
            _metrics.Add(item.Item1, box); choices.Controls.Add(box);
            box.CheckedChanged += (_, _) =>
            {
                if (frozen && box.Checked) { box.Checked = false; return; }
                InvalidateResult(); UpdateFields();
            };
        }
        _metrics["DYNAMIC"].Checked = true; _autoInterval.Checked = true; Wide(choices);
        foreach (var item in new[] { ("mag", "磁通道"), ("segment", "采集段/Pass"), ("task", "采集任务/架次"), ("mother", "母测线号（分组）"), ("source", "原始来源文件"), ("sourceRecord", "原始记录号"), ("time", "相对时间（秒）"), ("timestamp", "日期时刻/日内时刻"), ("date", "日期（配合日内时刻）"), ("lon", "经度（度）"), ("lat", "纬度（度）"), ("alt", "实测海拔（米）") })
        {
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 380 }; combo.Items.Add("（未映射）"); combo.SelectedIndex = 0;
            string key = item.Item1;
            combo.SelectedIndexChanged += (_, _) => { if (key == "mag") RefreshMagneticEvidence(); InvalidateResult(); RefreshTimeEvidence(); }; _columns.Add(item.Item1, combo); Add(item.Item2, combo);
        }
        Wide(_recognition);
        Wide(_preflight);
        Wide(_offsetPreflight);
        Wide(_advanced); Add("开始记录", _first); Add("结束记录（0=全部）", _last);
        Wide(_autoInterval); Wide(_timeEvidence); Add("采样间隔（秒）", _interval); Wide(_cadence); Wide(_segments);
        Add("通道角色/处理阶段", _channel); Wide(_nt); Add("静态设备/运行条件", _conditions);
        Wide(_gradient); Add("梯度限值 nT/km", _limit); Wide(_wgs);
        Wide(UiComponents.HelpLabel("偏航距：可使用设计KML或明确对应的分线参考；无设计时选择实测拟合偏离。只需已分线点表、经纬度及分组，时间、磁值和海拔不必填写。"));
        _offsetMode.Items.AddRange(["设计KML偏航距", "复用分线参考", "实测拟合偏离（无需KML）"]);
        _offsetMode.SelectedIndex = 0;
        _offsetMode.SelectedIndexChanged += (_, _) => { ResetOffsetMappings(); InvalidateResult(); UpdateFields(); RefreshPreflight(); };
        Add("偏航参考方式", _offsetMode);
        Wide(UiComponents.Button("复用当前分线参考", (_, _) => Safe(() => SetOffsetReferences(ResolveOffsetReview?.Invoke()
            ?? throw new InvalidOperationException("尚无当前分线核验；可载入已保存核验JSON，或选择实测拟合模式。")))));
        var loadReview = UiComponents.Button("载入已保存分线参考（可拖入）…", (_, _) => Safe(LoadOffsetReview));
        FileDropSupport.EnableSingle(loadReview, LoadOffsetReviewFile, p => FileDropSupport.HasExtension(p, ".json"),
            canImport: () => !_loading && Enabled, onError: message => _status.Text = message); Wide(loadReview);
        _routes.CellDoubleClick += (_, e) => Safe(() => ChooseOffsetReference(e.RowIndex));
        Wide(UiComponents.HelpLabel("分线参考模式：双击下方对应行选择明确参考；无设计时选择实测拟合偏离。拟合按母线/线号分组，不能衡量整条线相对设计的平移。"));
        var chooseKml = UiComponents.Button("选择设计测线 KML（可拖入）…", (_, _) => Safe(ChooseKml));
        FileDropSupport.EnableSingle(chooseKml, SetKmlSource, p => FileDropSupport.HasExtension(p, ".kml"),
            canImport: () => !_loading && Enabled, onError: message => _status.Text = message); Wide(chooseKml);
        var chooseKmlDirectory = UiComponents.Button("选择 KML 文件夹（可拖入）…", (_, _) => Safe(ChooseKmlDirectory));
        FileDropSupport.EnableDirectory(chooseKmlDirectory, SetKmlSource, () => !_loading && Enabled,
            message => _status.Text = message); Wide(chooseKmlDirectory);
        Wide(UiComponents.Button("更新设计线对应建议", (_, _) => Safe(SuggestRoutes))); Add("设计测线来源 *", _kml);
        _routes.Columns.Add("segment", "母线/外部段号"); _routes.Columns.Add("placemark", "KML线名 / 分线参考ID（双击选）");
        _routes.Columns.Add("kmlpath", "KML文件（重名时指定）");
        _routes.Columns.Add(new DataGridViewCheckBoxColumn { Name = "confirmed", HeaderText = "已核对母线范围" });
        _routes.AccessibleName = "外部段号与设计线人工对应";
        var routeHost = new Panel { Height = 130, Dock = DockStyle.Fill }; routeHost.Controls.Add(_routes); Wide(routeHost);
        Add("空间匹配容差（米）", _tolerance); Wide(_height); Add("高程依据（待核实）", _datum); Add("设计离地高m（可空）", _agl);
        Wide(UiComponents.Button("查找空间候选", (_, _) => Safe(FindSpatialCandidates)));
        Wide(UiComponents.HelpLabel("偏航距大偏差仍参与统计；设计端点外的点单列。静态磁噪声、飞高偏差正在开发。"));
        scroll.Controls.Add(form); body.Controls.Add(scroll, 0, 0);
        var output = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(6, 0, 0, 0) };
        output.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); output.RowStyles.Add(new RowStyle(SizeType.AutoSize)); output.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        output.Controls.Add(_status, 0, 0);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var summary = new TabPage("参考数值"); summary.Controls.Add(_summary); tabs.TabPages.Add(summary);
        var details = new TabPage("逐点/窗口明细"); details.Controls.Add(_details); tabs.TabPages.Add(details); output.Controls.Add(tabs, 0, 1);
        var groups = new TabPage("逐段/母线统计"); groups.Controls.Add(_groups); tabs.TabPages.Add(groups);
        var parameters = new TabPage("报告参数清单"); parameters.Controls.Add(_parameters); tabs.TabPages.Add(parameters);
        var dynamicLines = new TabPage("动态噪声逐线成果"); dynamicLines.Controls.Add(_dynamicLines); tabs.TabPages.Add(dynamicLines);
        var dynamicChart = new TabPage("报告式直方图"); dynamicChart.Controls.Add(_noisePicture); dynamicChart.Controls.Add(_noisePage); tabs.TabPages.Add(dynamicChart);
        AddOffsetViews(tabs);
        _noisePage.SelectedIndexChanged += (_, _) =>
        {
            _noisePicture.Image?.Dispose(); _noisePicture.Image = null;
            if (_noisePage.SelectedItem is string title && _noiseFigures.TryGetValue(title, out var bytes))
            { using var stream = new MemoryStream(bytes); using var image = Image.FromStream(stream); _noisePicture.Image = new Bitmap(image); }
        };
        Disposed += (_, _) => _noisePicture.Image?.Dispose();
        body.Controls.Add(output, 1, 0); root.Controls.Add(body, 0, 2); Controls.Add(root);
        output.SizeChanged += (_, _) => _status.MaximumSize = new Size(Math.Max(120, output.ClientSize.Width - output.Padding.Horizontal - 8), 0);
        form.SizeChanged += (_, _) =>
        {
        foreach (var box in new[] { _cadence, _segments, _nt, _wgs, _height, _gradient })
                box.MaximumSize = new Size(Math.Max(120, form.ClientSize.Width - 12), 0);
            _recognition.MaximumSize = _timeEvidence.MaximumSize = _preflight.MaximumSize = _offsetPreflight.MaximumSize = new Size(Math.Max(120, form.ClientSize.Width - 12), 0);
        };
        foreach (var box in new[] { _channel, _conditions, _datum, _agl }) box.TextChanged += (_, _) => InvalidateResult();
        foreach (var box in new[] { _cadence, _segments, _nt, _wgs, _height, _gradient }) box.CheckedChanged += (_, _) => InvalidateResult();
        foreach (var number in new[] { _first, _last, _interval, _limit, _tolerance }) number.ValueChanged += (_, _) => { InvalidateResult(); RefreshTimeEvidence(); };
        _autoInterval.CheckedChanged += (_, _) => { InvalidateResult(); UpdateFields(); RefreshTimeEvidence(); };
        _gradient.CheckedChanged += (_, _) => UpdateFields(); _advanced.CheckedChanged += (_, _) => UpdateFields();
        _routes.CellValueChanged += (_, e) =>
        {
            if (!_loading && e.RowIndex >= 0 && e.ColumnIndex is 1 or 2 && _routes.Rows[e.RowIndex].Cells[3].Value is true)
                _routes.Rows[e.RowIndex].Cells[3].Value = false;
            InvalidateResult(); RefreshOffsetPreview();
        }; _routes.RowsRemoved += (_, _) => InvalidateResult();
        _routes.SelectionChanged += (_, _) => RefreshOffsetPreview();
        _routes.CurrentCellDirtyStateChanged += (_, _) => { if (_routes.IsCurrentCellDirty) _routes.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        FileDropSupport.EnableSingle(_source, path => LoadSource(path),
            path => FileDropSupport.HasExtension(path, ".csv", ".txt", ".tsv"),
            canImport: () => !_loading && Enabled,
            onError: message => _status.Text = "未导入：" + message);
        UpdateFields();
    }

    private static TextBox Box(bool readOnly = false) => new() { ReadOnly = readOnly, Dock = DockStyle.Fill };
    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(430, 0) };
    private static NumericUpDown Numeric(decimal value, decimal min, decimal max, int decimals = 0) => new() { Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals, Increment = decimals > 0 ? .1m : 1 };
    private static DataGridView Grid() => new() { ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells, Dock = DockStyle.Fill };
    private void Safe(Action action)
    {
        try { action(); }
        catch (Exception ex) { InvalidateResult(); _status.Text = "本次未完成：" + ex.Message; }
    }
    private void InvalidateResult()
    {
        if (_loading || _export is null) return;
        _result = null; _export.Enabled = false; _summary.DataSource = null; _details.DataSource = null; _groups.DataSource = null; _parameters.DataSource = null;
        _dynamicLines.DataSource = null; _noisePage.Items.Clear(); _noiseFigures = new Dictionary<string, byte[]>();
        _offsetLines.DataSource = null;
        _offsetSummary.Text = "输入或参数已改变，请重新计算偏航距。";
        _offsetPreview.SetData([], []);
        _offsetPreviewNote.Text = "输入或参数已改变，请重新选择母线查看当前范围。";
        _offsetCandidates.DataSource = null;
        _noisePicture.Image?.Dispose(); _noisePicture.Image = null;
        _lineTablePath = ""; _openLineTable.Enabled = false;
        _status.Text = "输入或参数已改变，请重新计算。";
        RefreshPreflight();
    }
    private void ChooseSource()
    {
        using var dialog = new OpenFileDialog { Filter = "文本表格|*.csv;*.txt;*.tsv", CheckFileExists = true };
        FileInteractions.For(this, "quality-source").Prepare(dialog, current: _source.Text);
        if (dialog.ShowDialog(this) == DialogResult.OK) LoadSource(dialog.FileName);
    }
    internal void LoadSource(string path)
    {
        var loaded = BusyOperationDialog.Run(FindForm() ?? throw new InvalidOperationException("验算工作区尚未挂载窗口。"), "载入验算表格", "正在读取表头并计算来源指纹…", () =>
        {
            string hash = QualityCheckService.Hash(path); var table = new TextTableAnalysisService().Read(path);
            if (QualityCheckService.Hash(path) != hash) throw new IOException("读取过程中来源变化。");
            return (Hash: hash, Table: table);
        });
        InvalidateResult();
        _loading = true;
        try
        {
            _source.Text = Path.GetFullPath(path); _sourceHash = loaded.Hash;
            _table = loaded.Table;
            foreach (var combo in _columns.Values)
            {
                combo.Items.Clear(); combo.Items.Add("（未映射）");
                for (int i = 0; i < loaded.Table.Headers.Length; i++) combo.Items.Add($"{i + 1}. {loaded.Table.Headers[i]}");
                combo.SelectedIndex = 0;
            }
            var suggestions = QualityHeaderRecognition.Suggest(loaded.Table.Headers);
            _headerSuggestions = suggestions;
            var titles = new Dictionary<string, string> { ["mag"] = "磁通道", ["segment"] = "采集段", ["task"] = "采集任务", ["mother"] = "母测线号", ["source"] = "来源文件", ["sourceRecord"] = "原始记录号", ["time"] = "相对秒", ["timestamp"] = "日期时刻", ["date"] = "日期", ["lon"] = "经度", ["lat"] = "纬度", ["alt"] = "高程" };
            foreach (var suggestion in suggestions)
                if (suggestion.Column is { } index) _columns[suggestion.Field].SelectedIndex = index + 1;
            _recognition.Text = "表头识别建议（可修改；不代替下方确认）：\r\n" + string.Join("\r\n", suggestions.Select(s =>
                titles[s.Field] + "：" + (s.Column is { } index ? loaded.Table.Headers[index] + "；" : "") + s.Note));
            _first.Value = 1; _last.Value = 0; ResetOffsetMappings();
            _offsetCandidates.DataSource = null; _offsetPreview.SetData([], []);
            _cadence.Checked = false; _segments.Checked = false; _nt.Checked = false; _wgs.Checked = false; _height.Checked = false;
            _channel.Text = ""; _conditions.Text = ""; _datum.Text = ""; _agl.Text = "";
            _status.Text = $"已读取{loaded.Table.Rows.Count}条记录，{loaded.Table.Headers.Length}列。请核对字段、采样与来源含义；母线号只用于分组，不作为连续采集段。* 为所选指标的必填项；时间可用相对秒或完整日期时刻。";
        }
        finally { _loading = false; }
        RefreshMagneticEvidence(); RefreshTimeEvidence(); UpdateFields(); RefreshPreflight();
        FileInteractions.For(this, "quality-source").InputSucceeded(path);
    }
    private void RefreshMagneticEvidence()
    {
        if (_loading || _table is null || !_columns.TryGetValue("mag", out var combo)) return;
        int index = combo.SelectedIndex - 1;
        _nt.Checked = index >= 0 && index < _table.Headers.Length && QualityHeaderRecognition.HasExplicitNtUnit(_table.Headers[index]);
    }
    private void ChooseKml()
    {
        using var dialog = new OpenFileDialog { Filter = "设计测线KML|*.kml", CheckFileExists = true };
        FileInteractions.For(this, "quality-kml").Prepare(dialog, current: _kml.Text);
        if (dialog.ShowDialog(this) == DialogResult.OK) SetKmlSource(dialog.FileName);
    }
    private void ChooseKmlDirectory()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择实际使用的设计航线目录（递归读取KML，跳过目录链接）" };
        FileInteractions.For(this, "quality-kml-directory").Prepare(dialog, current: _kml.Text);
        if (dialog.ShowDialog(this) == DialogResult.OK) SetKmlSource(dialog.SelectedPath);
    }
    internal void SetKmlSource(string path)
    {
        if (_table is null) throw new InvalidOperationException("请先选择观测表格。");
        var request = ReadRequest();
        var catalog = QualityRouteCatalog.Read(path);
        var mappings = QualityRouteCatalog.Suggest(catalog, _table.Rows.Select(r => SpatialKey(r, request)).ToArray());
        _offsetMode.SelectedIndex = 0; _kml.Text = path; _offsetCandidates.DataSource = null; _height.Checked = false;
        InvalidateResult(); ApplyRouteCatalog(catalog, mappings);
        if (Directory.Exists(path)) FileInteractions.For(this, "quality-kml-directory").DirectorySucceeded(path);
        else FileInteractions.For(this, "quality-kml").InputSucceeded(path);
    }
    private void SuggestRoutes() => SuggestRoutesCore(null);
    private void SuggestRoutesCore(QualityRouteCatalogResult? loaded)
    {
        if (_table is null) throw new InvalidOperationException("请先选择观测表格。");
        if (OffsetMode != "DESIGN_KML") throw new InvalidOperationException("更新KML对应建议前，请选择设计KML参考方式。");
        var request = ReadRequest();
        var catalog = loaded ?? BusyOperationDialog.Run(FindForm()!, "读取设计测线", "正在递归读取KML并检查唯一对应…", () => QualityRouteCatalog.Read(_kml.Text));
        var segments = _table.Rows.Select(r => SpatialKey(r, request));
        ApplyRouteCatalog(catalog, QualityRouteCatalog.Suggest(catalog, segments.ToArray()));
    }
    private void ApplyRouteCatalog(QualityRouteCatalogResult catalog, QualityRouteMapping[] mappings)
    {
        _routes.Rows.Clear();
        foreach (var mapping in mappings) _routes.Rows.Add(mapping.Segment, mapping.Placemark, mapping.KmlPath, mapping.GeometryConfirmed);
        RefreshOffsetPreview();
        _status.Text = $"发现{catalog.Sources.Length}个KML文件、{catalog.Entries.Length}条设计线。唯一同名对应已列出；其他可点“查找空间候选”预览后确认。" + Environment.NewLine + string.Join(Environment.NewLine, catalog.Warnings);
    }

    private void RefreshTimeEvidence()
    {
        if (_loading || _table is null || !_columns.ContainsKey("date")) return;
        var request = ReadRequest();
        if (request.FirstRecord > _table.Rows.Count || request.LastRecord is { } last && last < request.FirstRecord) { _timeEvidence.Text = "记录范围无效。"; return; }
        var evidence = QualityTimeReader.Read(_table, request.Columns, request.FirstRecord, request.LastRecord);
        _timeEvidence.Text = evidence.Mode + "：" + evidence.Cadence.Note;
        RefreshPreflight();
    }

    private void RefreshPreflight()
    {
        if (_loading || _preflight is null) return;
        RefreshOffsetPreflight();
        var blockers = new List<string>();
        var selected = _metrics.Where(p => p.Value.Checked).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        bool noise = selected.Contains("STATIC") || selected.Contains("DYNAMIC");
        bool spatial = selected.Contains("OFFSET") || selected.Contains("HEIGHT");
        int? Column(string name) => _columns.TryGetValue(name, out var c) && c.SelectedIndex > 0 ? c.SelectedIndex - 1 : null;
        if (selected.Count == 0) blockers.Add("至少选择一个验算指标。");
        if (!noise && !spatial) { _preflight.Text = "参数预检：请选择指标。"; _preflight.ForeColor = UiTheme.TextSecondary; return; }
        if (noise)
        {
            if (_table is null) blockers.Add("先选择CSV/TXT/TSV观测表格。");
            if (Column("mag") is null) blockers.Add("选择磁通道，并确认它是要验算的磁值列。");
            if (!_nt.Checked) blockers.Add("确认磁通道单位为nT。");
            if (selected.Contains("STATIC") && _channel.Text.Trim().Length == 0) blockers.Add("静态噪声：填写通道角色/处理阶段。");
            if (selected.Contains("STATIC") && !_segments.Checked) blockers.Add("静态噪声：确认采集段/Pass边界。");
            if (selected.Contains("STATIC") && _table is not null)
            {
                var request = ReadRequest();
                if (request.Columns.RelativeSeconds is { } relative && (relative == request.Columns.Timestamp || relative == request.Columns.Date))
                    blockers.Add("相对秒误选了日期/日内时刻列；TimeUtc不是相对秒，请清除此映射并选择真正的相对秒列，或使用日期+时刻识别采样间隔。");
                var evidence = QualityTimeReader.Read(_table, request.Columns, request.FirstRecord, request.LastRecord);
                bool manualRow = !request.AutoInterval && request.Columns.RelativeSeconds is null && request.Columns.Timestamp is null;
                if (request.AutoInterval && evidence.Cadence.IntervalSeconds is null)
                    blockers.Add("不能从当前时间字段确定稳定采样间隔。检查相对秒，或映射完整日期时刻；仅有重复/模糊日内时刻时不能计算。没有时间证据时，可关闭自动间隔并依据采集记录填写间隔，再确认行序与采集段连续性。");
                else if (!request.AutoInterval && request.IntervalSeconds <= 0)
                    blockers.Add("填写有效采样间隔（秒）。");
                if (manualRow && !_cadence.Checked)
                    blockers.Add("没有可解析时间列：若确有采集依据，关闭自动间隔，填写间隔并确认按行序等间隔采样；否则暂不能验算噪声。");
            }
            if (selected.Contains("STATIC") && _conditions.Text.Trim().Length == 0) blockers.Add("静态噪声须说明静态试验设备与运行条件。");
            if (selected.Contains("DYNAMIC") && _gradient.Checked && (Column("lon") is null || Column("lat") is null || !_wgs.Checked))
                blockers.Add("梯度筛选需要经纬度字段并确认WGS84坐标。");
        }
        if (spatial)
        {
            if (_table is null) blockers.Add("先选择CSV/TXT/TSV观测表格。");
            if (Column("lon") is null || Column("lat") is null || !_wgs.Checked) blockers.Add("空间指标需要映射经纬度并确认WGS84（度）。");
            if ((selected.Contains("HEIGHT") || OffsetMode == "DESIGN_KML") && _kml.Text.Trim().Length == 0) blockers.Add("选择实际使用的设计KML文件或文件夹。");
            else if (selected.Contains("HEIGHT") || OffsetMode == "DESIGN_KML")
            {
                try
                {
                    if (_table is not null)
                    {
                        var catalog = QualityRouteCatalog.Read(_kml.Text);
                        var request = ReadRequest();
                        var segments = _table.Rows.Skip(request.FirstRecord - 1).Take((request.LastRecord ?? _table.Rows.Count) - request.FirstRecord + 1)
                            .Select(r => SpatialKey(r, request)).Distinct().ToArray();
                        var suggested = QualityRouteCatalog.Suggest(catalog, segments).ToDictionary(m => m.Segment);
                        foreach (var mapping in request.Routes) suggested[mapping.Segment] = mapping;
                        var missing = segments.Where(s => !suggested.TryGetValue(s, out var m) || m.Placemark.Length == 0 && m.KmlPath.Length == 0).ToArray();
                        if (missing.Length > 0 && selected.Contains("HEIGHT")) blockers.Add("需指定设计线对应：" + string.Join("、", missing.Take(5)) + "；可在下方表格选择KML文件和线名。");
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or XmlException)
                { blockers.Add("KML来源无法读取：" + ex.Message); }
            }
        }
        if (selected.Contains("HEIGHT"))
        {
            if (Column("alt") is null) blockers.Add("飞高偏差需要映射实测海拔（米）。");
            if (!_height.Checked || _datum.Text.Trim().Length == 0) blockers.Add("核对实测海拔与KML绝对高程基准，并填写依据；不能从表头自动推断。");
            if (_agl.Text.Trim().Length > 0 && (!double.TryParse(_agl.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double agl) || !double.IsFinite(agl) || agl < 0))
                blockers.Add("设计离地高须为非负数值，或留空。");
        }
        bool blocked = blockers.Count > 0;
        _preflight.Text = blocked ? "计算前需处理：\r\n• " + string.Join("\r\n• ", blockers) : "参数预检通过：当前所选指标的必需字段与确认项已齐备；计算后仍会检查记录级缺失与断口。";
        if (!blocked && selected.Contains("OFFSET")) _preflight.Text = "共同输入检查完成；偏航距各线对应状态见下方。计算后仍检查逐点有效范围。";
        _preflight.ForeColor = blocked ? UiTheme.Warning : UiTheme.Success;
    }

    private void UpdateFields()
    {
        bool Selected(string metric) => _metrics.TryGetValue(metric, out var box) && box.Enabled && box.Checked;
        bool noise = Selected("STATIC") || Selected("DYNAMIC"), spatial = Selected("OFFSET") || Selected("HEIGHT"), height = Selected("HEIGHT");
        if (_fields.Count == 0) return;
        bool Show((string Title, Control Label, Control Value) f)
        {
            var c = f.Value; string title = f.Title;
            if (c == _recognition || c == _source || c == _advanced) return true;
            if (c == _offsetPreflight || c == _offsetMode || title.StartsWith("复用当前") || title.StartsWith("载入已保存") || title.StartsWith("分线参考模式")) return Selected("OFFSET");
            if (c.Controls.Contains(_routes)) return height || Selected("OFFSET") && OffsetMode != "ACTUAL_FIT";
            if (title.StartsWith("采集段") || title.StartsWith("采集任务")) return noise || _advanced.Checked;
            if (c == _interval) return Selected("STATIC") && !_autoInterval.Checked;
            if (c == _autoInterval || c == _cadence || c == _segments) return Selected("STATIC");
            if (title.StartsWith("原始来源") || title.StartsWith("原始记录")) return noise && _advanced.Checked;
            if (c == _channel) return Selected("STATIC") || noise && _advanced.Checked;
            if (title.StartsWith("相对时间") || title.StartsWith("日期")) return Selected("STATIC") || noise && _advanced.Checked;
            if (c == _timeEvidence || c == _nt || title.StartsWith("磁通道")) return noise;
            if (c == _conditions) return Selected("STATIC");
            if (c == _gradient) return Selected("DYNAMIC");
            if (c == _limit) return Selected("DYNAMIC") && _gradient.Checked && _advanced.Checked;
            if (c == _first || c == _last || c == _tolerance) return _advanced.Checked && (c != _tolerance || height);
            if (c == _height || c == _datum || c == _agl || title.StartsWith("实测海拔")) return height;
            if (title.StartsWith("经度") || title.StartsWith("纬度") || c == _wgs) return spatial || Selected("DYNAMIC") && _gradient.Checked;
            if (c == _kml || title.StartsWith("选择设计测线") || title.StartsWith("选择 KML") || title == "查找空间候选" || title == "更新设计线对应建议") return height || Selected("OFFSET") && OffsetMode == "DESIGN_KML";
            if (title.Contains("KML") || title.StartsWith("偏航") || title == "查找空间候选" || title == "更新设计线对应建议" || c.Controls.Contains(_routes)) return spatial;
            return true;
        }
        foreach (var field in _fields)
        {
            bool show = Show(field); field.Label.Visible = show; field.Value.Visible = show;
            if (field.Label is Label label && field.Value != field.Label)
            {
                bool required = field.Value == _columns.GetValueOrDefault("mag") || field.Value == _channel && Selected("STATIC") ||
                    field.Value == _conditions && Selected("STATIC") || field.Value == _interval && Selected("STATIC") && !_autoInterval.Checked ||
                    field.Value == _kml && spatial || field.Value == _datum && height ||
                    (_columns.GetValueOrDefault("lon") == field.Value || _columns.GetValueOrDefault("lat") == field.Value) && (spatial || Selected("DYNAMIC") && _gradient.Checked) ||
                    _columns.GetValueOrDefault("alt") == field.Value && height;
                required |= _columns.GetValueOrDefault("mother") == field.Value && Selected("OFFSET");
                label.Text = field.Title + (required ? " *" : "");
            }
        }
        UpdateOffsetViews();
        _nt.Text = "确认所选磁通道的单位为 nT" + (noise ? " *" : "");
        _segments.Text = "确认段号区分采集段/Pass；无段号则范围为同一采集段" + (Selected("STATIC") ? " *" : "");
        _cadence.Text = "无可解析时间时，确认按行序等间隔采样" + (Selected("STATIC") && !_autoInterval.Checked ? " *" : "");
        _height.Text = "确认实测/KML高程均为米，且绝对高程基准一致" + (height ? " *" : "");
        _wgs.Text = "确认经纬度为 WGS84（度）" + (spatial || Selected("DYNAMIC") && _gradient.Checked ? " *" : "");
        if (_fields.FirstOrDefault(f => f.Value == _tolerance).Label is Label toleranceLabel)
            toleranceLabel.Text = "空间匹配容差（米）" + (spatial ? " *" : "");
        _autoInterval.Text = "根据时间列自动确定采样间隔" + (Selected("STATIC") ? " *" : "");
        _interval.Enabled = !_autoInterval.Checked;
        if (_table is not null && _headerSuggestions.Length > 0)
        {
            var relevant = _headerSuggestions.Where(s => s.Field is "mother" || noise && s.Field is "mag" or "segment" || spatial && s.Field is "lon" or "lat" || height && s.Field == "alt");
            _recognition.Text = "表头识别建议（请核对）：\r\n" + string.Join("\r\n", relevant.Select(s =>
                (s.Column is { } index ? _table.Headers[index] + "：" : s.Field + "：") + s.Note));
        }
    }
    internal QualityCheckRequest ReadRequest()
    {
        _routes.EndEdit();
        int? Column(string key) => _columns[key].SelectedIndex > 0 ? _columns[key].SelectedIndex - 1 : null;
        double? agl = null;
        string heightError = "";
        if (_agl.Text.Trim().Length > 0)
        {
            if (!double.TryParse(_agl.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) || !double.IsFinite(n)) heightError = "设计AGL请输入有限数字或留空。";
            else agl = n;
        }
        return new()
        {
            SourcePath = _source.Text, SourceSha256 = _sourceHash,
            Columns = new(Column("mag"), Column("segment"), Column("time"), Column("lon"), Column("lat"), Column("alt"), Column("timestamp"), Column("date"), Column("mother"), Column("task"), Column("source"), Column("sourceRecord")),
            Metrics = _metrics.Where(p => p.Value.Enabled && p.Value.Checked).Select(p => p.Key).ToArray(), FirstRecord = (int)_first.Value, LastRecord = _last.Value == 0 ? null : (int)_last.Value,
            IntervalSeconds = (double)_interval.Value, AutoInterval = _autoInterval.Checked, ConfirmRowCadence = _cadence.Checked, ConfirmSegmentBoundaries = _segments.Checked,
            MagneticChannelRole = _channel.Text.Trim(), MagneticUnitNtConfirmed = _nt.Checked, TrialConditions = _conditions.Text.Trim(),
            FilterGradient = _gradient.Checked, GradientLimitNtKm = (double)_limit.Value, Wgs84Confirmed = _wgs.Checked,
            OffsetReferenceMode = OffsetMode,
            OffsetReferences = _routes.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow).SelectMany(r =>
                _segmentationReferences.Where(g => g.Identity == Convert.ToString(r.Cells[1].Value)).Select(g => g with { Identity = Convert.ToString(r.Cells[0].Value)?.Trim() ?? "", Evidence = g.Evidence + "；原参考ID=" + g.Identity })).ToArray(),
            KmlPath = _kml.Text, MatchToleranceM = (double)_tolerance.Value, HeightDatumConfirmed = _height.Checked, HeightDatum = _datum.Text.Trim(), DesignAglM = agl, HeightInputError = heightError,
            Routes = OffsetMode != "DESIGN_KML" ? [] : _routes.Rows.Cast<DataGridViewRow>().Where(r => !r.IsNewRow && (r.Cells[1].Value?.ToString()?.Length > 0 || r.Cells[2].Value?.ToString()?.Length > 0)).Select(r => new QualityRouteMapping(r.Cells[0].Value?.ToString()?.Trim() ?? "", r.Cells[1].Value?.ToString()?.Trim() ?? "") { KmlPath = r.Cells[2].Value?.ToString()?.Trim() ?? "", GeometryConfirmed = r.Cells[3].Value is true }).ToArray()
        };
    }
    internal void Calculate()
    {
        QualityCheckRequest request = ReadRequest(); InvalidateResult();
        _result = BusyOperationDialog.Run(FindForm() ?? throw new InvalidOperationException("验算工作区尚未挂载窗口。"), "质量验算", "正在逐项检查输入并计算，缺项只阻断对应指标…", () => new QualityCheckService().Compute(request));
        _summary.DataSource = _result.Metrics.Select(m => new { 指标 = m.Metric == "OFFSET" ? QualityCheckService.OffsetBasis(_result.Request) : NameOf(m.Metric), 统计口径 = m.Metric == "DYNAMIC" ? "全表有效窗口合算Sv（另见逐母线统计）" : "选定记录参考值", 状态 = StatusOf(m.Status), 参考值 = m.Value, 单位 = m.Unit, 有效数量 = m.ValidCount, 剔除数量 = m.RejectedCount, 公式 = m.Formula, 说明 = m.Note }).ToArray();
        _details.DataSource = _result.Details.Take(2000).Select(d => new { 指标 = d.Metric == "OFFSET" ? QualityCheckService.OffsetBasis(_result.Request) : NameOf(d.Metric), 起始记录 = d.FirstRecord, 结束记录 = d.LastRecord, 采集段 = d.Segment, 数值 = d.Value, 原因 = d.Reason.Length == 0 ? "采用" : d.Reason }).ToArray();
        _groups.DataSource = _result.Groups.Select(g => new { 指标 = g.Metric == "OFFSET" ? QualityCheckService.OffsetBasis(_result.Request) : NameOf(g.Metric), 分组 = g.GroupKind == "MOTHER" ? "母测线" : "采集段", 线号 = g.Group, 状态 = StatusOf(g.Status), 参考值 = g.Value, 最小值 = g.Minimum, 最大值 = g.Maximum, 有效数量 = g.ValidCount, 剔除数量 = g.RejectedCount, 说明 = g.Method }).ToArray();
        _parameters.DataSource = QualityCheckService.ReportParameters(_result).Select(p => new { 指标 = p.Metric == "OFFSET" ? QualityCheckService.OffsetBasis(_result.Request) : NameOf(p.Metric), 范围 = p.Scope, 统计口径 = StatisticOf(p.Statistic), 数值 = p.Value, 单位 = p.Unit, 状态 = StatusOf(p.Status), 依据 = p.Evidence }).ToArray();
        _dynamicLines.DataSource = _result.DynamicLines.Select(l => new { 顺序 = l.LineOrder, 母测线号 = l.MotherId, 动态噪声Sv_nT = l.SvNt,
            有效窗口 = l.ValidWindows, 剔除窗口 = l.RejectedWindows, 原始点数 = l.SourceRows, 连续片段数 = l.RunCount,
            首记录 = l.FirstRecord, 状态 = StatusOf(l.Status), 说明 = l.Method }).ToArray();
        if (_dynamicLines.Columns["动态噪声Sv_nT"] is { } noiseColumn) noiseColumn.DefaultCellStyle.Format = "0.######";
        PopulateOffsetResults();
        _noiseFigures = RenderQualityFigures(_result);
        foreach (string title in _noiseFigures.Keys) _noisePage.Items.Add(title);
        if (_noisePage.Items.Count > 0) _noisePage.SelectedIndex = 0;
        if (_summary.Columns["参考值"] is { } valueColumn) valueColumn.DefaultCellStyle.Format = "0.######";
        if (_details.Columns["数值"] is { } detailColumn) detailColumn.DefaultCellStyle.Format = "0.######";
        _status.Text = string.Join(Environment.NewLine, _result.Warnings) + Environment.NewLine + "计算完成；数量单位：磁噪声为五点窗口，偏航/飞高为观测点。";
        var lineParameters = QualityCheckService.ReportParameters(_result).Where(p => p.Metric == "DYNAMIC" && p.Scope == "MAPPED_MOTHERS").ToArray();
        if (lineParameters.Length > 0)
            _status.Text = "动态噪声逐母线汇总：" + string.Join("；", lineParameters.Select(p => StatisticOf(p.Statistic) + "=" + (p.Value?.ToString("G8", CultureInfo.InvariantCulture) ?? "—") + p.Unit)) + Environment.NewLine + _status.Text;
        if (request.Metrics.Contains("DYNAMIC") && _result.DynamicLines.Length == 0)
            _status.Text = "未映射有效母测线号，尚不能生成逐线报告成果；请映射母测线号后重新计算。" + Environment.NewLine + _status.Text;
        if (_result.Details.Length > 2000) _status.Text += Environment.NewLine + $"明细共{_result.Details.Length}条，界面预览前2000条；导出保留全部。可调整记录范围查看其他位置。";
        _export.Enabled = true;
    }
    private void Export()
    {
        if (_result is null) throw new InvalidOperationException("请先完成本次验算。");
        using var dialog = new FolderBrowserDialog { Description = "选择输出父目录（自动新建本次验算目录）" };
        var fileHistory = FileInteractions.For(this, "quality-output", "output"); fileHistory.Prepare(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var result = _result;
        string parent = dialog.SelectedPath;
        _output = BusyOperationDialog.Run(FindForm() ?? throw new InvalidOperationException("验算工作区尚未挂载窗口。"), "导出质量验算", "正在复核来源并写入新目录…", () => QualityCheckService.Export(result, parent, RenderQualityFigures(result)));
        fileHistory.OutputSucceeded(_output, directory: true);
        _open.Enabled = true; _lineTablePath = Path.Combine(_output, result.Request.Metrics.Contains("OFFSET") ? (result.Request.OffsetReferenceMode == "ACTUAL_FIT" ? "实测拟合偏离逐母线统计.csv" : "偏航距逐母线统计.csv") : "动态噪声逐线统计.csv"); _openLineTable.Enabled = File.Exists(_lineTablePath);
        _openLineTable.Text = "打开逐线成果表";
        _status.Text = "已导出本次所选指标的中文成果表、图件和质量验算结果.json；可打开逐线表或结果目录。空值及部分可用范围见表内说明。" + Environment.NewLine + _output;
    }
    private static string NameOf(string metric) => metric switch { "STATIC" => "静态磁噪声", "DYNAMIC" => "动态磁噪声", "OFFSET" => "偏航距", "HEIGHT" => "飞高偏差", "AGL_ESTIMATE" => "实际飞高估计", _ => metric };
    private static string StatusOf(string status) => status switch { "PARTIAL" or "PARTIAL_REFERENCE" => "部分可用", "REFERENCE_ONLY" => "参考数值", "BLOCKED" => "输入缺项/不可用", "INSUFFICIENT" => "有效数据不足", _ => status };
    private static string StatisticOf(string statistic) => statistic switch
    {
        "POOLED_REFERENCE" or "POOLED_WINDOW_REFERENCE" => "全表有效窗口/观测合算参考值", "MIN_OF_LINE_VALUES" => "逐母线最小值", "MAX_OF_LINE_VALUES" => "逐母线最大值",
        "MEAN_OF_LINE_VALUES" => "逐母线算术平均（等权）", "TOTAL_LINE_COUNT" => "母线总数", "VALID_LINE_COUNT" => "有效母线数", _ => statistic
    };
}
