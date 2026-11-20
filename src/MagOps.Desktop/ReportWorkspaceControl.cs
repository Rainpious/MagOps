using MagOps.Reporting;
using System.ComponentModel;
using System.Globalization;
using Microsoft.VisualBasic.FileIO;

namespace MagOps.Desktop;

internal sealed class ReportWorkspaceControl : UserControl
{
    private readonly Dictionary<string, TextBox> _fields = new();
    private readonly RichTextBox _checks = new() { Dock = DockStyle.Fill, ReadOnly = true };
    private readonly Label _context = new() { AutoSize = true, MaximumSize = new Size(1000, 0) };
    private bool _loading;
    private ReportInputs _loaded = new();
    private readonly DataGridView _surveyTasks = new();
    private readonly DataGridView _repeatPairs = new();
    private readonly DataGridView _supplements = new();
    private readonly DataGridView _staticTrials = new();
    private readonly Label _freshness = new() { AutoSize = true, MaximumSize = new Size(1000, 0) };
    public bool HasChanges { get; private set; }
    public event EventHandler? Changed;

    public ReportWorkspaceControl(Action save, Action check, Action generate, Action exportTemplate, Action openOutput, Action rediscover, Action importDesign, Action? qualityCheck = null, Action? shared = null, Action? delivery = null, Action<string>? workingReport = null)
    {
        Dock = DockStyle.Fill;
        _checks.AccessibleName = "报告检查结果与统计";
        _surveyTasks.AccessibleName = "报告母测线设计信息";
        _repeatPairs.AccessibleName = "报告检查线配对";
        _supplements.AccessibleName = "报告人工补录";
        _staticTrials.AccessibleName = "报告独立静态试验";
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = UiTheme.PagePadding };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(UiComponents.PageHeader("报告编写", "选择工作稿模板后，可在 Word 中续写。更新或导出前，请保存并关闭工作稿。"), 0, 0);
        root.Controls.Add(_context, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        void ActionButton(string title, Action action)
        { var button = new Button { Text = title, AutoSize = true }; button.Click += (_, _) => action(); actions.Controls.Add(button); }
        ActionButton("保存资料", save); ActionButton("检查与统计", check); ActionButton("生成 Word 草稿", generate);
        if (delivery != null) ActionButton("生成小结审阅与交付文件", delivery);
        if (workingReport != null)
        {
            var format = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 115, AccessibleName = "工作稿模板" };
            format.Items.AddRange(["平泉", "塔尔曼"]);
            format.SelectedIndex = 0;
            actions.Controls.Add(format);
            foreach (var verb in new[] { "新建", "打开", "更新", "导出" })
            {
                var button = new Button { AutoSize = true };
                void SetLabel() => button.Text = verb + format.SelectedItem + (verb == "导出" ? "交付稿" : "工作稿");
                SetLabel();
                format.SelectedIndexChanged += (_, _) => SetLabel();
                button.Click += (_, _) => workingReport(button.Text);
                actions.Controls.Add(button);
            }
            foreach (var label in new[] { "更新塔尔曼表 4-3 探头参数", "更新塔尔曼日变基站", "更新塔尔曼日变站选址及基值" })
            {
                var button = new Button { Text = label, AutoSize = true, Visible = false };
                format.SelectedIndexChanged += (_, _) => button.Visible = Equals(format.SelectedItem, "塔尔曼");
                button.Click += (_, _) => workingReport(label);
                actions.Controls.Add(button);
            }
        }
        ActionButton("另存内置模板", exportTemplate);
        ActionButton("字段字典", () => { using var dialog = new ReportFieldDictionaryDialog(); dialog.ShowDialog(this); }); ActionButton("打开报告目录", openOutput);
        ActionButton("重新识别已有成果", rediscover);
        if (shared is not null) ActionButton("引用共用资料", shared);
        ActionButton("引用当前设计身份", importDesign);
        if (qualityCheck is not null) ActionButton("独立质量验算", qualityCheck);
        root.Controls.Add(actions, 0, 2);
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            SizeMode = TabSizeMode.Normal,
            Tag = "AllowMultilineTabs",
            Padding = new Point(10, 4)
        };
        var info = new TabPage("资料与来源");
        var form = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(8) };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); info.AutoScroll = true; info.Controls.Add(form);
        void Field(string key, string title, string? kind = null)
        {
            int row = form.RowCount++; form.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            form.Controls.Add(new Label { Text = title, AutoSize = true, Margin = new Padding(3, 8, 3, 3) }, 0, row);
            var box = MakeBox(key); box.AccessibleName = title; form.Controls.Add(box, 1, row);
            if (kind is null) return;
            var browse = new Button { Text = "选择…", AccessibleName = "选择" + title, AutoSize = true };
            browse.Click += (_, _) =>
            {
                if (kind == "folder")
                { using var dialog = new FolderBrowserDialog { Description = "选择包含manifest.json的完整飞行质量评估批次" }; if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath; }
                else
                { using var dialog = new OpenFileDialog { Filter = kind == "csv" ? "正式测线 CSV|*.csv" : kind == "kml" ? "设计边界KML|*.kml" : kind == "quality" ? "质量验算结果|质量验算结果.json;result.json" : "MagOps报告模板|*.docx", CheckFileExists = true }; if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.FileName; }
            };
            form.Controls.Add(browse, 2, row);
        }
        Field(nameof(ReportInputs.ProjectName), "项目名称"); Field(nameof(ReportInputs.WorkareaName), "工区名称");
        Field(nameof(ReportInputs.QualityCheckResultPath), "质量验算结果文件（可选）", "quality");
        form.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(760, 0),
            Text = "报告数据有两种来源：独立验算目录中的“动态噪声逐线统计.csv”按母线列Sv；“动态噪声直方图分箱.csv”复核其报告图。正式报告编译目录中的“母测线统计.csv”列“动态噪声Sv参考值_nT”是正式报告统计口径。若需把独立结果作为参考附表和图，请在此选择其“质量验算结果.json”；该参考不会替换正式统计或自动判定合格。" }, 1, form.RowCount++);
        Field(nameof(ReportInputs.Organization), "编写单位"); Field(nameof(ReportInputs.Author), "编写人");
        Field(nameof(ReportInputs.ReportDate), "报告日期"); Field(nameof(ReportInputs.ProductionCsv), "正式测线 CSV", "csv");
        Field(nameof(ReportInputs.AssessmentDirectory), "飞行评估（可选）", "folder"); Field(nameof(ReportInputs.TemplatePath), "自选模板（可选）", "docx");
        Field(nameof(ReportInputs.MaximumTimeGapSeconds), "连续时间上限秒");
        Field(nameof(ReportInputs.MaximumSpatialGapM), "连续距离上限m");
        Field(nameof(ReportInputs.KmlMatchToleranceM), "飞高匹配容差m");
        Field(nameof(ReportInputs.RepeatMatchToleranceM), "检查配对容差m");
        Field(nameof(ReportInputs.GradientLimitNtKm), "噪声梯度限nT/km");
        Field(nameof(ReportInputs.DesignBoundaryKml), "设计边界（可选）", "kml");
        Field(nameof(ReportInputs.BoundaryDatum), "边界投影椭球"); Field(nameof(ReportInputs.BoundaryCentralMeridian), "边界中央经线°");
        Field(nameof(ReportInputs.BoundaryEvidence), "边界用途/版本依据");
        form.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(760, 0),
            Text = "模板留空使用内置小结模板。自选模板应从内置模板另存后调整，保留变量及内容控件。旧项目成品不能直接当作模板。飞行评估仅作为独立参考，不自动认定与最终CSV范围一致。" }, 1, form.RowCount++);
        tabs.TabPages.Add(info);
        void GridPage(string title, string help, DataGridView grid)
        {
            var page = new TabPage(title);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(new Label { Text = help, AutoSize = true, MaximumSize = new Size(1000, 0) }, 0, 0);
            grid.Dock = DockStyle.Fill; grid.AutoGenerateColumns = true; grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells;
            grid.AllowUserToOrderColumns = false; grid.RowHeadersWidth = 28;
            grid.DataError += (_, e) => { e.ThrowException = false; grid.Rows[e.RowIndex].ErrorText = "请按列类型填写有效值。"; };
            grid.CellValueChanged += (_, _) => MarkChanged(); grid.UserDeletedRow += (_, _) => MarkChanged();
            grid.CurrentCellDirtyStateChanged += (_, _) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            layout.Controls.Add(grid, 0, 1); page.Controls.Add(layout); tabs.TabPages.Add(page);
        }
        GridPage("母测线资料", "仅用于本报告的人工映射，须填写稳定ID、用途和依据并确认。第一条确认KML起终点定义母线正向；反向子线自动倒序。KML有多条线时须填准确线名称。双击KML路径单元格可选文件。", _surveyTasks);
        var loadTasks = new Button { Text = "载入CSV任务待确认", AutoSize = true };
        loadTasks.Click += (_, _) => LoadCsvTasks(); actions.Controls.Add(loadTasks);
        _surveyTasks.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || _surveyTasks.Columns[e.ColumnIndex].DataPropertyName != nameof(ReportSurveyTask.KmlPath)) return;
            using var dialog = new OpenFileDialog { Filter = "测线KML|*.kml", CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) { _surveyTasks.Rows[e.RowIndex].Cells[e.ColumnIndex].Value = dialog.FileName; MarkChanged(); }
        };
        GridPage("重复线配对", "人工指定原测/检查任务与Pass，填写配对依据。排除整条检查线时必须记录理由；保留排除前后结果，不修改生产数据。", _repeatPairs);
        GridPage("报告补录", "类别使用：项目概况、委托单位、任务依据、任务目标、设计参数、人员、设备、规范、基站、现场记录、试验条件、作业方法、处理记录、外部处理、成果来源、成果解释、结论建议。补录按类别进入对应章节；其他类别仅入附表。需来源和确认状态，不覆盖自动统计，实际面积留空。", _supplements);
        GridPage("独立静态试验", "独立选择航空站AIR、地面站GROUND或GPS静态试验及UTC窗口，填写设备和供电条件。CSV需DateUtc、TimeUtc及RawMagNt；GPS另需Longitude、Latitude、AltitudeM。双击路径选文件。", _staticTrials);
        _staticTrials.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 || _staticTrials.Columns[e.ColumnIndex].DataPropertyName != nameof(ReportStaticTrial.CsvPath)) return;
            using var dialog = new OpenFileDialog { Filter = "独立试验CSV|*.csv", CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) { _staticTrials.Rows[e.RowIndex].Cells[e.ColumnIndex].Value = dialog.FileName; MarkChanged(); }
        };
        var figures = new TabPage("成果图件");
        var figureLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
        figureLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        figureLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        figureLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        figureLayout.Controls.Add(new Label { AutoSize = true, Text = "选择已确认的PNG成果图，每行一个完整路径；按列表顺序插入，文件名用作图注。GRD/MAP请先导出PNG。" }, 0, 0);
        var figureBox = MakeBox(nameof(ReportInputs.FigurePaths));
        figureBox.AccessibleName = "报告成果图 PNG 路径（每行一个）";
        figureBox.Multiline = true; figureBox.ScrollBars = ScrollBars.Both; figureBox.Dock = DockStyle.Fill;
        var addFigures = new Button { Text = "添加成果PNG…", AutoSize = true };
        addFigures.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "成果PNG|*.png", Multiselect = true, CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                figureBox.Text = string.Join(Environment.NewLine, figureBox.Lines.Concat(dialog.FileNames).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase));
        };
        figureLayout.Controls.Add(addFigures, 0, 1); figureLayout.Controls.Add(figureBox, 0, 2);
        figures.Controls.Add(figureLayout); tabs.TabPages.Add(figures);
        foreach (var field in new[] { (nameof(ReportInputs.Objectives), "任务目的"), (nameof(ReportInputs.Methods), "方法与设备"),
                     (nameof(ReportInputs.ProcessingDescription), "处理说明"), (nameof(ReportInputs.Interpretation), "成果解释"), (nameof(ReportInputs.Conclusions), "结论建议") })
        {
            var page = new TabPage(field.Item2); var box = MakeBox(field.Item1);
            box.AccessibleName = field.Item2;
            box.Multiline = true; box.AcceptsReturn = true; box.ScrollBars = ScrollBars.Vertical; box.Dock = DockStyle.Fill;
            page.Controls.Add(box); tabs.TabPages.Add(page);
        }
        var checks = new TabPage("检查结果"); checks.Controls.Add(_checks); tabs.TabPages.Add(checks);
        root.Controls.Add(tabs, 0, 3); Controls.Add(root);
        actions.Controls.Add(_freshness);
    }

    private TextBox MakeBox(string key)
    {
        var box = new TextBox { Dock = DockStyle.Fill, MinimumSize = new Size(150, 0) };
        box.TextChanged += (_, _) => MarkChanged();
        _fields.Add(key, box); return box;
    }

    public ReportInputs ReadInputs() => _loaded with
    {
        ProjectName = _fields[nameof(ReportInputs.ProjectName)].Text.Trim(), WorkareaName = _fields[nameof(ReportInputs.WorkareaName)].Text.Trim(),
        Organization = _fields[nameof(ReportInputs.Organization)].Text.Trim(), Author = _fields[nameof(ReportInputs.Author)].Text.Trim(),
        ReportDate = _fields[nameof(ReportInputs.ReportDate)].Text.Trim(), ProductionCsv = _fields[nameof(ReportInputs.ProductionCsv)].Text.Trim(),
        AssessmentDirectory = _fields[nameof(ReportInputs.AssessmentDirectory)].Text.Trim(), TemplatePath = _fields[nameof(ReportInputs.TemplatePath)].Text.Trim(),
        FigurePaths = _fields[nameof(ReportInputs.FigurePaths)].Text.Trim(),
        QualityCheckResultPath = _fields[nameof(ReportInputs.QualityCheckResultPath)].Text.Trim(),
        Objectives = _fields[nameof(ReportInputs.Objectives)].Text, Methods = _fields[nameof(ReportInputs.Methods)].Text,
        ProcessingDescription = _fields[nameof(ReportInputs.ProcessingDescription)].Text, Interpretation = _fields[nameof(ReportInputs.Interpretation)].Text,
        Conclusions = _fields[nameof(ReportInputs.Conclusions)].Text,
        SurveyTasks = ReadRows<ReportSurveyTask>(_surveyTasks), RepeatPairs = ReadRows<ReportRepeatPair>(_repeatPairs),
        Supplements = ReadRows<ReportSupplement>(_supplements),
        StaticTrials = ReadRows<ReportStaticTrial>(_staticTrials),
        DesignBoundaryKml = _fields[nameof(ReportInputs.DesignBoundaryKml)].Text.Trim(), BoundaryDatum = _fields[nameof(ReportInputs.BoundaryDatum)].Text.Trim(),
        BoundaryCentralMeridian = _fields[nameof(ReportInputs.BoundaryCentralMeridian)].Text.Trim(), BoundaryEvidence = _fields[nameof(ReportInputs.BoundaryEvidence)].Text.Trim(),
        MaximumTimeGapSeconds = Numeric(nameof(ReportInputs.MaximumTimeGapSeconds)), MaximumSpatialGapM = Numeric(nameof(ReportInputs.MaximumSpatialGapM)),
        KmlMatchToleranceM = Numeric(nameof(ReportInputs.KmlMatchToleranceM)), RepeatMatchToleranceM = Numeric(nameof(ReportInputs.RepeatMatchToleranceM)),
        GradientLimitNtKm = Numeric(nameof(ReportInputs.GradientLimitNtKm))
    };

    public void LoadInputs(ReportInputs inputs, string context, bool changed = false)
    {
        _loading = true;
        try
        {
            _loaded = inputs;
            foreach (var property in typeof(ReportInputs).GetProperties())
                if (_fields.TryGetValue(property.Name, out var box)) box.Text = Convert.ToString(property.GetValue(inputs), CultureInfo.InvariantCulture) ?? "";
            // Clone mutable grid rows: editing the view must not mutate the saved input object.
            var clone = System.Text.Json.JsonSerializer.Deserialize<ReportInputs>(System.Text.Json.JsonSerializer.Serialize(inputs))!;
            _surveyTasks.DataSource = new BindingList<ReportSurveyTask>(clone.SurveyTasks.ToList());
            _repeatPairs.DataSource = new BindingList<ReportRepeatPair>(clone.RepeatPairs.ToList());
            _supplements.DataSource = new BindingList<ReportSupplement>(clone.Supplements.ToList());
            _staticTrials.DataSource = new BindingList<ReportStaticTrial>(clone.StaticTrials.ToList());
            _context.Text = context;
            _checks.Text = changed ? "报告资料已修改，原检查结果已失效。请重新执行检查与统计。" : "尚未检查当前报告资料。";
            _freshness.Text = changed ? "报告资料已修改，请重新检查已有报告。" : "尚未核对当前工区的已有报告。";
            HasChanges = changed;
        }
        finally { _loading = false; }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void MarkSaved() { HasChanges = false; Changed?.Invoke(this, EventArgs.Empty); }
    public void ShowChecks(string text)
    {
        _checks.Text = text;
        if (_checks.Parent is TabPage page && page.Parent is TabControl tabs) tabs.SelectedTab = page;
    }
    public void ShowFreshness(string text) => _freshness.Text = text;
    public void AddDesignTasks(IEnumerable<ReportSurveyTask> tasks)
    {
        var rows = _surveyTasks.DataSource as BindingList<ReportSurveyTask> ?? throw new InvalidOperationException("请先选择工区。");
        foreach (var task in tasks)
        {
            var current = rows.FirstOrDefault(t => t.TaskId == task.TaskId);
            if (current is null) rows.Add(task);
            else if (!current.Confirmed && string.IsNullOrWhiteSpace(current.MotherId))
            {
                task.KmlPath = current.KmlPath; task.Placemark = current.Placemark;
                task.DesignAglM = current.DesignAglM; task.HeightDatumConfirmed = current.HeightDatumConfirmed;
                rows[rows.IndexOf(current)] = task;
            }
        }
        MarkChanged();
    }
    private void MarkChanged()
    {
        if (_loading) return;
        HasChanges = true;
        _freshness.Text = "报告资料已修改，请重新检查已有报告。";
        _checks.Text = "报告资料已修改，原检查结果已失效。请重新执行检查与统计。";
        Changed?.Invoke(this, EventArgs.Empty);
    }
    private double Numeric(string key) => double.TryParse(_fields[key].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) && value > 0
        ? value : throw new InvalidDataException("质量参数必须填写正的有限数值：" + key);
    private static T[] ReadRows<T>(DataGridView grid)
    {
        if (!grid.EndEdit()) throw new InvalidDataException("请先修正表格中无效的单元格。");
        return grid.DataSource is BindingList<T> rows ? rows.ToArray() : [];
    }
    private void LoadCsvTasks()
    {
        try
        {
            using var reader = new TextFieldParser(_fields[nameof(ReportInputs.ProductionCsv)].Text, System.Text.Encoding.UTF8, true);
            reader.SetDelimiters(","); reader.HasFieldsEnclosedInQuotes = true;
            var header = reader.ReadFields() ?? [];
            int taskColumn = Array.FindIndex(header, h => h.Trim().Trim('\uFEFF') == "LineTaskId");
            if (taskColumn < 0) throw new InvalidDataException("CSV缺少LineTaskId。");
            var rows = _surveyTasks.DataSource as BindingList<ReportSurveyTask> ?? throw new InvalidOperationException("请先选择工区。");
            var known = rows.Select(r => r.TaskId).ToHashSet(StringComparer.Ordinal);
            var found = new HashSet<string>(StringComparer.Ordinal);
            while (!reader.EndOfData)
            {
                var row = reader.ReadFields() ?? [];
                if (row.Length != header.Length) throw new InvalidDataException("CSV列数无效。");
                if (!string.IsNullOrWhiteSpace(row[taskColumn])) found.Add(row[taskColumn]);
            }
            foreach (string id in found.Order(StringComparer.Ordinal)) if (known.Add(id)) rows.Add(new() { TaskId = id });
            MarkChanged();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { MessageBox.Show(this, ex.Message, "载入待确认任务", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
