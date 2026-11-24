using MagOps.Application;
using System.Globalization;
using System.Drawing.Imaging;
using MagOps.Core.Domain;
using MagOps.Core.Diurnal;
using MagOps.Core.Geomagnetics;
using MagOps.Core.Routes;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private readonly ComboBox _fieldRouteScope = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210, DropDownWidth = 240,
        AccessibleName = "任务进度航线范围" };
    private void BuildFieldOperationsTab()
    {
        FileDropSupport.EnableDirectory(_fieldDataFolder, AdoptFieldDataDirectory,
            () => Enabled, message => SetStatus(message));
        _toolTip.SetToolTip(_fieldDataFolder, "可拖入一个外业数据目录；只设置路径，不自动扫描。");
        var page = new TabPage("外业管理") { Padding = Padding.Empty };
        _fieldOperationsPage = page;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = UiTheme.PagePadding,
            BackColor = UiTheme.Workspace
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildFieldOperationsTopHeader(), 0, 0);

        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Multiline = false,
            Margin = Padding.Empty
        };
        _fieldWorkspaceTabs = tabs;
        tabs.TabPages.Add(BuildFieldDataWorkspacePage());
        tabs.TabPages.Add(BuildFieldProgressWorkspacePage());
        tabs.TabPages.Add(BuildFieldRecordHubPage());
        tabs.TabPages.Add(BuildGroundMagPage());
        tabs.TabPages.Add(BuildIntermediateTablePage());
        tabs.TabPages.Add(BuildExternalPackagesPage());
        tabs.TabPages.Add(BuildDiurnalQualityPage());
        tabs.TabPages.Add(BuildMotherLineProductionPage());
        root.Controls.Add(tabs, 0, 1);

        page.Controls.Add(root);
        RegisterFeaturePage(
            FeatureIds.FieldOperations,
            page,
            activated: () =>
                QueueFeatureActivation(FeatureIds.FieldOperations, AutoAnalyzeFieldOperationsOnActivation),
            contextChanged: _ =>
                QueueFieldOperationsContextRestore());
    }

    private Control BuildFieldOperationsTopHeader()
    {
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = 3,
            Margin = new Padding(0, 0, 0, 6), BackColor = UiTheme.Workspace
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) top.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        top.Controls.Add(UiComponents.PageHeader("外业管理", "按项目、工区和作业日核对数据、任务进度与现场记录。"), 0, 0);
        var sources = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = Padding.Empty };
        _fieldDataFolder.Dock = DockStyle.None;
        _fieldDataFolder.Width = 260 * DeviceDpi / 96;
        _fieldDataFolder.MaximumSize = new Size(260 * DeviceDpi / 96, 0);
        _fieldDataFolder.Margin = new Padding(4, 8, 6, 2);
        _fieldDataFolder.ReadOnly = true;
        _fieldDataFolder.PlaceholderText = "外业数据目录：默认 05_外业记录与采集成果";
        sources.Controls.Add(_fieldDataFolder);
        var extraGsm = Button("补充GSM（可拖入）…", (_, _) => RunUi("补充GSM", () => SelectManualGsm19TFiles()));
        FileDropSupport.EnableMultiple(extraGsm, paths => RunUi("补充GSM", () => SelectManualGsm19TFiles(paths)),
            p => FileDropSupport.HasExtension(p, ".csv", ".txt", ".tsv", ".dat", ".asc", ".xyz", ".log", ".stn"), canImport: () => Enabled);
        sources.Controls.Add(extraGsm);
        var extraUav = Button("补充UAV/GNSS（可拖入）…", (_, _) => RunUi("补充UAV", () => SelectManualUavFiles()));
        FileDropSupport.EnableMultiple(extraUav, paths => RunUi("补充UAV", () => SelectManualUavFiles(paths)),
            p => FileDropSupport.HasExtension(p, ".csv", ".txt", ".tsv", ".dat", ".asc", ".xyz", ".log", ".stn", ".gpx", ".kml"), canImport: () => Enabled);
        sources.Controls.Add(extraUav);
        _toolTip.SetToolTip(extraGsm, "补充文件用于当前进度分析；如需归档入库，请使用“导入新数据”。");
        _toolTip.SetToolTip(extraUav, "补充文件用于当前进度分析；如需归档入库，请使用“导入新数据”。");
        sources.Controls.Add(Button("选择目录…", (_, _) => RunUi("选择项目外业数据目录", SelectFieldDataFolder), UiButtonRole.Subtle));
        var importData = Button("导入新数据（可拖入）…", (_, _) => RunUi("导入外业数据", ImportFieldDataFiles), UiButtonRole.Primary);
        FileDropSupport.EnableMultiple(importData, paths => RunUi("导入外业数据", () => ImportFieldDataFilesCore(paths)),
            p => FileDropSupport.HasExtension(p, ".csv", ".txt", ".tsv", ".dat", ".asc", ".xyz", ".log", ".stn"),
            canImport: () => !_fieldIntakeBusy && Enabled);
        sources.Controls.Add(importData);
        sources.Controls.Add(Button("扫描/核对", (_, _) => RunUiBusy("核对项目外业数据",
            "正在核对外业目录、解析变化文件并更新作业日进度…", AnalyzeSelectedFieldDataAndProgress), UiButtonRole.Subtle));
        _fieldDataFolder.TextChanged += (_, _) => _toolTip.SetToolTip(_fieldDataFolder, _fieldDataFolderFullPath.Length > 0 ? _fieldDataFolderFullPath : _fieldDataFolder.Text);
        top.Controls.Add(sources, 0, 1);
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = Padding.Empty };
        _fieldDataImportRecursive.AutoSize = true;
        _fieldDataImportRecursive.Text = "递归读取子文件夹";
        _fieldDataImportRecursive.Checked = true;
        _fieldDataImportRecursive.Margin = new Padding(4, 8, 4, 2);
        _toolTip.SetToolTip(_fieldDataImportRecursive, "勾选时读取源文件夹所有子目录；取消后手动选择具体文件。");
        options.Controls.Add(_fieldDataImportRecursive);
        options.Controls.Add(Button("工区筛选 / 高级参数…", (_, _) => RunUi("工区数据筛选", EditWorkareaScreening), UiButtonRole.Subtle));
        ConfigureFieldSourceStatusLabel(_fieldBaseStationStatus);
        _fieldBaseStationStatus.Text = "所选作业日：尚未登记日变站。";
        _fieldBaseStationStatus.MaximumSize = new Size(240 * DeviceDpi / 96, 0);
        options.Controls.Add(_fieldBaseStationStatus);
        var baseMenu = new ContextMenuStrip();
        baseMenu.Items.Add("登记/更新…", null, (_, _) => RunUi("登记/更新日变站", RegisterOrUpdateBaseStationForSelectedDate));
        baseMenu.Items.Add("历史记录…", null, (_, _) => RunUi("日变站记录", () => ShowFieldRecordWindow("base", "日变站记录", BuildBaseStationWorkspaceControl)));
        baseMenu.Items.Add("定位日变站", null, (_, _) => RunUi("定位日变站", LocateFieldBaseStationInPreview));
        var baseButton = Button("日变站 ▾", (_, _) => { }, UiButtonRole.Subtle);
        baseButton.Click += (_, _) => baseMenu.Show(baseButton, new Point(0, baseButton.Height));
        baseButton.Disposed += (_, _) => baseMenu.Dispose();
        options.Controls.Add(baseButton);
        options.Controls.Add(Button("适配工区", (_, _) => RunUi("适配工区预览", FitFieldPreviewToWorkarea), UiButtonRole.Subtle));
        top.Controls.Add(options, 0, 2);
        // FlowLayoutPanel's stretched cell can retain its initial preferred
        // height. Measure the actual wrapped controls rather than that cell.
        void FitToolbarRows()
        {
            int available = top.Parent is null ? top.ClientSize.Width : top.Parent.ClientSize.Width - top.Parent.Padding.Horizontal;
            if (available > 200)
            {
                var maximum = new Size(available, 0);
                if (top.MaximumSize != maximum) top.MaximumSize = maximum;
                if (sources.MaximumSize != maximum) sources.MaximumSize = maximum;
                if (options.MaximumSize != maximum) options.MaximumSize = maximum;
            }
            foreach (var row in new[] { (Index: 1, Flow: sources), (Index: 2, Flow: options) })
            {
                int height = row.Flow.Controls.Cast<Control>().Where(c => c.Visible)
                    .Select(c => c.Bottom + c.Margin.Bottom).DefaultIfEmpty(0).Max();
                if (height > 0 && (top.RowStyles[row.Index].SizeType != SizeType.Absolute ||
                    Math.Abs(top.RowStyles[row.Index].Height - height) > 1))
                {
                    top.RowStyles[row.Index].SizeType = SizeType.Absolute;
                    top.RowStyles[row.Index].Height = height;
                }
            }
        }
        top.ParentChanged += (_, _) => FitToolbarRows();
        top.SizeChanged += (_, _) => FitToolbarRows();
        sources.Layout += (_, _) => FitToolbarRows();
        options.Layout += (_, _) => FitToolbarRows();
        return top;
    }

    private TabPage BuildFieldRecordHubPage()
    {
        var page = new TabPage("现场记录")
        {
            Padding = new Padding(0, 6, 0, 0),
            AutoScroll = true
        };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = UiTheme.Workspace
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        root.Controls.Add(BuildFieldRecordHubCard(
            "飞行架次",
            "登记和查看外业飞行架次、人员、设备、航线版本和原始资料证据。",
            "查看飞行架次…",
            () => ShowFieldRecordWindow(
                "flights", "飞行架次记录", BuildFlightWorkspaceControl),
            "新增架次",
            AddFlight), 0, 0);

        root.Controls.Add(BuildFieldRecordHubCard(
            "日变站",
            "日变站登记入口已移至外业管理顶部；此处用于查看完整历史记录和资料证据。",
            "查看日变站记录…",
            () => ShowFieldRecordWindow(
                "base", "日变站记录", BuildBaseStationWorkspaceControl),
            null,
            null), 1, 0);

        root.Controls.Add(BuildFieldRecordHubCard(
            "每日日志",
            "按当前任务进度页选中的项目时区日期填写现场完成情况、异常和下一步安排。",
            "查看每日日志…",
            () => ShowFieldRecordWindow(
                "daily", "每日日志", BuildDailyLogWorkspaceControl),
            "新增 / 更新日志",
            SaveDailyLog), 0, 1);

        root.Controls.Add(BuildFieldRecordHubCard(
            "现场问题",
            "登记影响作业的问题、临时措施、负责人和解决证据。",
            "查看现场问题…",
            () => ShowFieldRecordWindow(
                "issues", "现场问题", BuildIssueWorkspaceControl),
            "新增问题",
            AddIssue), 1, 1);

        page.Controls.Add(root);
        return page;
    }

    private Control BuildFieldRecordHubCard(
        string title,
        string description,
        string viewText,
        Action viewAction,
        string? addText,
        Action? addAction)
    {
        var card = UiComponents.Card(new Padding(14));
        card.Margin = new Padding(4);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(UiComponents.SectionLabel("", title), 0, 0);
        layout.Controls.Add(new Label
        {
            Text = description,
            Dock = DockStyle.Fill,
            AutoSize = true,
            ForeColor = UiTheme.TextSecondary,
            Margin = new Padding(2, 8, 2, 8)
        }, 0, 1);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            AutoScroll = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        actions.Controls.Add(Button(
            viewText,
            (_, _) => RunUi(title, viewAction),
            UiButtonRole.Subtle));
        if (!string.IsNullOrWhiteSpace(addText) &&
            addAction is not null)
        {
            actions.Controls.Add(Button(
                addText,
                (_, _) => RunUi(addText, addAction),
                UiButtonRole.Primary));
        }
        layout.Controls.Add(actions, 0, 2);
        card.Controls.Add(layout);
        return card;
    }

    private TabPage BuildFieldDataWorkspacePage()
    {
        var page = new TabPage("数据识别与质量") { Padding = new Padding(0, 6, 0, 0), AutoScroll = true };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 800,
            MinimumSize = new Size(0, 800),
            ColumnCount = 1,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = UiTheme.Workspace
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 210));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 306));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var summaryCard = UiComponents.Card(new Padding(10));
        summaryCard.Margin = new Padding(0, 0, 0, 8);
        summaryCard.MinimumSize = new Size(0, 198);
        var summaryLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ColumnCount = 1,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        summaryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        summaryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        summaryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        summaryLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        summaryLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        summaryLayout.Controls.Add(UiComponents.SectionLabel("1", "项目数据识别与规范数据集"), 0, 0);

        ConfigureFieldSourceStatusLabel(_fieldDataFolderStatus);
        ConfigureFieldSourceStatusLabel(_fieldDataFingerprintStatus);
        _fieldDataFolderStatus.Text =
            "项目数据识别：默认扫描当前工区“05_外业记录与采集成果”。";
        _fieldDataFingerprintStatus.Text =
            "项目完整规范数据集：等待扫描。";
        summaryLayout.Controls.Add(_fieldDataFolderStatus, 0, 1);
        summaryLayout.Controls.Add(_fieldDataFingerprintStatus, 0, 2);
        summaryLayout.Controls.Add(UiComponents.HelpLabel(
            "项目进度按项目时区划分作业日；UAV / GSM-19T 规范数据各输出一个完整整合文件。MagOps 日变在文件内部按绝对 UTC Date 分组，不跨日插值。"), 0, 3);

        _fieldDataSummary.Dock = DockStyle.Fill;
        _fieldDataSummary.ReadOnly = true;
        _fieldDataSummary.WordWrap = true;
        _fieldDataSummary.MinimumSize = new Size(0, 64);
        _fieldDataSummary.ScrollBars = RichTextBoxScrollBars.Vertical;
        _fieldDataSummary.Text =
            "等待扫描项目数据目录。UAV 依据记录自带绝对时间恢复，项目时区用于项目作业日和进度统计；Raw 原始文件按 record-level 恢复，派生/已拼接文件不重复混入。";
        summaryLayout.Controls.Add(_fieldDataSummary, 0, 4);
        summaryCard.Controls.Add(summaryLayout);
        root.Controls.Add(summaryCard, 0, 0);

        var dateCard = UiComponents.Card(new Padding(10));
        dateCard.Margin = new Padding(0, 0, 0, 8);
        var dateLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        dateLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dateLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        dateLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        dateLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        dateLayout.Controls.Add(UiComponents.SectionLabel("2", "作业日期分类与 19T 覆盖预览（项目时区）"), 0, 0);
        ConfigureFieldSourceStatusLabel(_fieldCoverageStatus);
        _fieldCoverageStatus.Text = "19T 覆盖：等待扫描。绿色 UAV 表示可生产日变，红色表示该时段没有合格 Base；蓝色 19T 表示可插值覆盖，橙色表示被生产门控排除。";
        dateLayout.Controls.Add(_fieldCoverageStatus, 0, 1);
        GridFactory.SetEmptyText(_fieldDateGrid, "尚未识别到可按项目时区作业日分类的 UAV / GSM-19T 记录。");
        _fieldDateGrid.MultiSelect = false;
        _fieldDateGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _fieldDateGrid.SelectionChanged += (_, _) =>
        {
            if (_updatingFieldDateSelection ||
                _fieldDateGrid.CurrentRow?.DataBoundItem is not FieldDateGridRow row ||
                !DateTime.TryParseExact(
                    row.UtcDate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTime date))
                return;

            SelectFieldDate(date, analyze: true);
        };
        dateLayout.Controls.Add(_fieldDateGrid, 0, 2);
        dateLayout.Controls.Add(_fieldCoveragePreview, 0, 3);
        dateCard.Controls.Add(dateLayout);
        root.Controls.Add(dateCard, 0, 1);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
            Panel1MinSize = 0,
            Panel2MinSize = 0,
            Margin = Padding.Empty
        };

        var filesCard = UiComponents.Card(new Padding(10));
        filesCard.Margin = new Padding(0, 0, 4, 0);
        var filesLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        filesLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        filesLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        filesLayout.Controls.Add(UiComponents.SectionLabel("3", "文件识别清单"), 0, 0);
        GridFactory.SetEmptyText(_fieldDataFileGrid, "尚未识别项目数据文件。\r\n日常新增数据优先使用“导入新数据…”，需要核查既有目录时使用“扫描/核对”。");
        filesLayout.Controls.Add(_fieldDataFileGrid, 0, 1);
        filesCard.Controls.Add(filesLayout);
        split.Panel1.Controls.Add(filesCard);

        var qcCard = UiComponents.Card(new Padding(10));
        qcCard.Margin = new Padding(4, 0, 0, 0);
        var qcLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        qcLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        qcLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        qcLayout.Controls.Add(UiComponents.SectionLabel("4", "作业时间轴与质量提醒（诊断保留 UTC）"), 0, 0);
        GridFactory.SetEmptyText(_fieldDataQcGrid, "暂无 QC 结果。\r\n质量规则只标记风险，不自动删除原始记录。 ");
        qcLayout.Controls.Add(_fieldDataQcGrid, 0, 1);
        qcCard.Controls.Add(qcLayout);
        split.Panel2.Controls.Add(qcCard);
        split.HandleCreated += (_, _) => split.BeginInvoke((Action)(() =>
        {
            if (!IsDisposed && !Disposing && !split.IsDisposed) NormalizeFieldDataSplit(split);
        }));
        split.SizeChanged += (_, _) => NormalizeFieldDataSplit(split);
        root.Controls.Add(split, 0, 2);

        var actionsCard = UiComponents.Card(new Padding(10, 7, 10, 7));
        actionsCard.Margin = new Padding(0, 8, 0, 0);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            AutoScroll = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        actions.Controls.Add(Button(
            "导入 DJI 飞行记录…",
            (_, _) => RunUi(
                "导入 DJI Pilot 2 FlightRecord",
                ImportDjiFlightRecordFolder),
            UiButtonRole.Subtle));
        actions.Controls.Add(Button(
            "登记 MCDat（不复制）…",
            (_, _) => RunUi(
                "登记 DJI MCDat",
                RegisterDjiMcdatFiles),
            UiButtonRole.Subtle));
        actions.Controls.Add(Button(
            "强制完整重扫",
            (_, _) => RunUiBusy(
                "强制完整重扫外业数据",
                "正在忽略解析缓存并重新读取全部支持文件，请稍候…",
                () => AnalyzeSelectedFieldDataAndProgress(
                    forceFullScan: true)),
            UiButtonRole.Subtle));
        actions.Controls.Add(Button(
            "生成完整整合数据",
            (_, _) => RunUiBusy(
                "生成完整磁测整合数据",
                "正在把整个工区周期的 UAV 磁测和 GSM-19T 分别整理为单一完整压缩文档（SourceId + GZip），请稍候…",
                ExportCanonicalFieldData),
            UiButtonRole.Subtle));

        _fieldDiurnalDatumNt.Minimum = -1000000m;
        _fieldDiurnalDatumNt.Maximum = 1000000m;
        _fieldDiurnalDatumNt.DecimalPlaces = 3;
        _fieldDiurnalDatumNt.Increment = 1m;
        _fieldDiurnalDatumNt.Value = 0m;
        _fieldDiurnalDatumNt.Width = 96;
        _fieldDiurnalDatumNt.ValueChanged += (_, _) =>
        {
            bool hasNonZeroDatum = _fieldDiurnalDatumNt.Value != 0m;
            _fieldDiurnalDatumConfirmed.Enabled = hasNonZeroDatum;
            _fieldDiurnalDatumConfirmed.Checked = false;
            InvalidateDiurnalQuality();
        };
        actions.Controls.Add(FieldToolbarLabel("Datum nT（默认0）"));
        actions.Controls.Add(_fieldDiurnalDatumNt);
        actions.Controls.Add(Button(
            "查看 Base 统计",
            (_, _) => RunUi(
                "Base 统计",
                ShowDiurnalBaseStatistics),
            UiButtonRole.Subtle));

        _fieldDiurnalDatumConfirmed.AutoSize = true;
        _fieldDiurnalDatumConfirmed.Text = "确认非零 Datum";
        _fieldDiurnalDatumConfirmed.Margin = new Padding(8, 8, 4, 0);
        _fieldDiurnalDatumConfirmed.Checked = false;
        _fieldDiurnalDatumConfirmed.Enabled = false;
        actions.Controls.Add(_fieldDiurnalDatumConfirmed);

        actions.Controls.Add(Button(
            "生成日变",
            (_, _) => RunUiBusy("生成完整日变", "正在核对 Base 有效性并生成完整日变数据…", ExportDiurnalOnlyFieldData),
            UiButtonRole.Primary));
        actions.Controls.Add(Button("查看日变质量", (_, _) => ShowDiurnalQuality(), UiButtonRole.Subtle));
        _diurnalQualityBanner.Controls.Add(_diurnalQualityNotice);
        _diurnalQualityBanner.Controls.Add(Button("查看详情",(_,_)=>ShowDiurnalQuality(),UiButtonRole.Subtle));
        _diurnalQualityBanner.Controls.Add(Button("稍后处理",(_,_)=>_diurnalQualityBanner.Visible=false,UiButtonRole.Subtle));
        actions.Controls.Add(_diurnalQualityBanner);
        actions.Controls.Add(Button(
            "进入分线与处理",
            (_, _) => NavigateFeature(FeatureIds.SegmentationProcessing),
            UiButtonRole.Subtle));

        actions.Controls.Add(Button(
            "导出 GEMLink 对照包",
            (_, _) => RunUiBusy(
                "导出 GEMLink 黄金对照包",
                "正在按 UTC 日期生成仅用于 GEMLink 5.4.8.1 对照验证的兼容数据，请稍候…",
                ExportGemLinkBridgePackage),
            UiButtonRole.Subtle));

        actions.Controls.Add(UiComponents.HelpLabel(
            "生成日变保留完整 Rover 与来源记录，主通道 rigai = Rover - BaseUsed；默认 Datum=0。分线核验、IGRF、T0、dTgz 和 Oasis 处理用 CSV 位于顶部“分线与处理”。"));
        actionsCard.Controls.Add(actions);
        root.Controls.Add(actionsCard, 0, 3);
        actions.Layout += (_, _) =>
        {
            int available = Math.Max(100, page.ClientSize.Width - actionsCard.Padding.Horizontal - 12);
            foreach (Label label in actions.Controls.OfType<Label>())
                if (label.MaximumSize.Width != available) label.MaximumSize = new Size(available, 0);
            int bottom = actions.Controls.Cast<Control>().Where(c => c.Visible)
                .Select(c => c.Bottom + c.Margin.Bottom).DefaultIfEmpty(0).Max();
            int height = bottom + actionsCard.Padding.Vertical;
            if (height > 0 && actionsCard.MinimumSize.Height != height)
            {
                actionsCard.MinimumSize = new Size(0, height);
                root.Height = Math.Max(800, 210 + 306 + 160 + height + 12);
            }
        };

        page.SizeChanged += (_, _) =>
        {
            int width = page.ClientSize.Width - page.Padding.Horizontal;
            if (width > 200 && root.MaximumSize.Width != width) root.MaximumSize = new Size(width, 0);
        };
        page.Controls.Add(root);
        return page;
    }

    private TabPage BuildFieldProgressWorkspacePage()
    {
        var page = new TabPage("任务进度") { Padding = new Padding(0, 6, 0, 0), AutoScroll = true };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 720,
            MinimumSize = new Size(0, 720),
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = UiTheme.Workspace
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var sourceCard = UiComponents.Card(new Padding(10, 7, 10, 7));
        sourceCard.AutoSize = true;
        sourceCard.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        sourceCard.Margin = new Padding(0, 0, 0, 8);
        var sourceLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        sourceLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 5; i++) sourceLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        ConfigureFieldSourceStatusLabel(_fieldRouteSourceStatus);
        ConfigureFieldSourceStatusLabel(_fieldGsm19TStatus);
        ConfigureFieldSourceStatusLabel(_fieldUavMagStatus);
        ConfigureFieldSourceStatusLabel(_fieldDjiFlightStatus);
        _fieldRouteSourceStatus.Text = "设计航线：进入外业管理页后自动读取当前航线工作台、项目设计航线或最近外部导入 KML。";
        _fieldGsm19TStatus.Text = "GSM-19T：等待读取完整规范数据。";
        _fieldUavMagStatus.Text = "无人机磁测：等待读取完整规范数据。";
        _fieldDjiFlightStatus.Text = "DJI 飞行记录：未导入（可选，用于遥控器侧飞行 Session 对账）。";

        _fieldOperationsDate.Format = DateTimePickerFormat.Custom;
        _fieldOperationsDate.CustomFormat = "yyyy-MM-dd";
        _fieldOperationsDate.Value = DateTime.Today;

        _fieldDateSelector.DropDownStyle = ComboBoxStyle.DropDownList;
        _fieldDateSelector.Width = 205;
        _fieldDateSelector.Enabled = false;
        _fieldDateSelector.Items.Add("等待扫描项目数据");
        _fieldDateSelector.SelectedIndex = 0;
        _fieldDateSelector.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingFieldDateSelection ||
                _fieldDateSelector.SelectedItem is not FieldDateChoice choice)
                return;

            _fieldOperationsDate.Value = choice.UtcDate;
            _fieldAutoAnalysisContextKey = "";
            AnalyzeFieldOperations(force: true, quietNoRoute: true);
            SelectFieldDateGridRow(choice.UtcDate);
            RefreshFieldBaseStationContext();
            RefreshFieldCoveragePreview(choice.UtcDate);
        };

        _fieldExpectedDailySorties.Minimum = 1;
        _fieldExpectedDailySorties.Maximum = 100;
        _fieldExpectedDailySorties.Value = 8;
        _fieldExpectedDailySorties.Width = 64;
        _fieldExpectedDailySorties.ValueChanged += (_, _) => MarkFieldRecommendationParametersChanged();

        _fieldAcceptedRelocations.Minimum = 0;
        _fieldAcceptedRelocations.Maximum = 20;
        _fieldAcceptedRelocations.Value = 1;
        _fieldAcceptedRelocations.Width = 64;
        _fieldAcceptedRelocations.ValueChanged += (_, _) => MarkFieldRecommendationParametersChanged();

        var dateBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            AutoScroll = false,
            Margin = new Padding(0, 0, 0, 4),
            Padding = Padding.Empty
        };
        dateBar.Controls.Add(FieldToolbarLabel("查看日期（项目时区）"));
        dateBar.Controls.Add(_fieldDateSelector);
        dateBar.Controls.Add(Button(
            "较早日期",
            (_, _) => SelectAdjacentFieldDate(older: true),
            UiButtonRole.Subtle));
        dateBar.Controls.Add(Button(
            "较晚日期",
            (_, _) => SelectAdjacentFieldDate(older: false),
            UiButtonRole.Subtle));
        dateBar.Controls.Add(FieldToolbarLabel("预计每日架次"));
        dateBar.Controls.Add(_fieldExpectedDailySorties);
        dateBar.Controls.Add(Button(
            "重新计算所选日",
            (_, _) => RunUiBusy(
                "重新计算外业日期进度",
                "正在重新计算所选日期的航迹覆盖与累计进度…",
                () => AnalyzeFieldOperations(force: true, quietNoRoute: false)),
            UiButtonRole.Primary));
        sourceLayout.Controls.Add(dateBar, 0, 0);

        var routeSourceRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        routeSourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        routeSourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        routeSourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        routeSourceRow.Controls.Add(_fieldRouteSourceStatus, 0, 0);

        var addRouteKml = Button(
            "加入航线 KML…",
            (_, _) => RunUi(
                "加入外部航线 KML",
                () => ImportExternalRouteKml(appendToActiveSet: true)),
            UiButtonRole.Subtle);
        addRouteKml.Margin = new Padding(8, 0, 0, 0);
        _toolTip.SetToolTip(
            addRouteKml,
            "默认加入当前外部航线集；可连续加入多个子工区/分块 KML。相同文件内容不会重复加入。");
        FileDropSupport.EnableMultiple(
            addRouteKml,
            files => RunUi(
                "加入外部航线 KML",
                () => ImportExternalRouteKml(
                    appendToActiveSet: true,
                    selectedFiles: files)),
            path => FileDropSupport.HasExtension(path, ".kml"));
        routeSourceRow.Controls.Add(addRouteKml, 1, 0);

        var replaceRouteKml = Button(
            "替换航线集…",
            (_, _) => RunUi(
                "替换外部航线集",
                () => ImportExternalRouteKml(appendToActiveSet: false)),
            UiButtonRole.Subtle);
        replaceRouteKml.Margin = new Padding(6, 0, 0, 0);
        _toolTip.SetToolTip(
            replaceRouteKml,
            "创建一个新的活动航线集并切换到它；旧批次仍保留在项目树中，不会删除。");
        FileDropSupport.EnableMultiple(replaceRouteKml, paths => RunUi("替换外部航线集", () =>
            ImportExternalRouteKml(appendToActiveSet: false, selectedFiles: paths)),
            p => FileDropSupport.HasExtension(p, ".kml"), canImport: () => Enabled);
        routeSourceRow.Controls.Add(replaceRouteKml, 2, 0);
        routeSourceRow.ColumnCount = 4;
        routeSourceRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _fieldRouteScope.Items.AddRange(["整个工区（分区汇总）", "当前子区（已打开）"]);
        _fieldRouteScope.SelectedIndex = 0;
        _fieldRouteScope.SelectionChangeCommitted += (_, _) => RunUi("切换任务进度范围", () =>
        {
            _fieldProjectTimelineContextKey = "";
            AnalyzeProjectFieldTimeline(force: true, quietNoRoute: true);
        });
        routeSourceRow.Controls.Add(_fieldRouteScope, 3, 0);

        sourceLayout.Controls.Add(routeSourceRow, 0, 1);
        sourceLayout.Controls.Add(_fieldGsm19TStatus, 0, 2);
        sourceLayout.Controls.Add(_fieldUavMagStatus, 0, 3);
        sourceLayout.Controls.Add(_fieldDjiFlightStatus, 0, 4);
        sourceCard.Controls.Add(sourceLayout);

        root.Controls.Add(sourceCard, 0, 0);
        root.Controls.Add(BuildFieldOperationsStats(), 0, 1);
        root.Controls.Add(BuildFieldOperationsMainArea(), 0, 2);
        page.Controls.Add(root);
        page.SizeChanged += (_, _) => root.Height = Math.Max(720, page.ClientSize.Height - 4);
        return page;
    }

    private Control BuildFieldOperationsStats()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 6,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 8),
            Padding = Padding.Empty,
            BackColor = UiTheme.Workspace
        };
        for (int i = 0; i < 6; i++)
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 6f));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _fieldPlannedValue.Text = "—";
        _fieldTodayValue.Text = "—";
        _fieldWorkPercentValue.Text = "—";
        _fieldCumulativeValue.Text = "—";
        _fieldTotalPercentValue.Text = "—";
        _fieldRemainingValue.Text = "—";
        layout.Controls.Add(UiComponents.StatTile("设计架次", _fieldPlannedValue, "当前航线"), 0, 0);
        layout.Controls.Add(UiComponents.StatTile("所选日完成", _fieldTodayValue, "项目时区作业日"), 1, 0);
        layout.Controls.Add(UiComponents.StatTile("当天工作百分比", _fieldWorkPercentValue, "实际 / 日目标（可超100%）"), 2, 0);
        layout.Controls.Add(UiComponents.StatTile("累计完成", _fieldCumulativeValue, "同航线记录"), 3, 0);
        layout.Controls.Add(UiComponents.StatTile("项目总完成率", _fieldTotalPercentValue, "累计 / 设计"), 4, 0);
        layout.Controls.Add(UiComponents.StatTile("剩余架次", _fieldRemainingValue, "后续计划"), 5, 0);
        return layout;
    }

    private Control BuildFieldOperationsMainArea()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = UiTheme.Workspace
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 45));

        var progressCard = UiComponents.Card(new Padding(10));
        progressCard.Margin = new Padding(0, 0, 4, 4);
        var progressLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        progressLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        progressLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        progressLayout.Controls.Add(UiComponents.SectionLabel("2", "任务进度"), 0, 0);
        GridFactory.SetEmptyText(
            _fieldProgressGrid,
            "尚未分析所选日期。\r\n项目扫描后会按项目时区作业日逐日估算每个架次进度。");
        _fieldProgressGrid.MultiSelect = false;
        _fieldProgressGrid.SelectionMode =
            DataGridViewSelectionMode.FullRowSelect;
        _fieldProgressGrid.SelectionChanged += (_, _) =>
            UpdateFieldPreviewSelectedSortie();
        _fieldProgressGrid.CellClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
                UpdateFieldPreviewSelectedSortie();
        };
        progressLayout.Controls.Add(_fieldProgressGrid, 0, 1);
        progressCard.Controls.Add(progressLayout);
        layout.Controls.Add(progressCard, 0, 0);

        Control reportCard = BuildFieldOperationsReportCard();
        reportCard.Margin = new Padding(0, 4, 4, 0);
        layout.Controls.Add(reportCard, 0, 1);

        var recommendationCard = UiComponents.Card(new Padding(10));
        recommendationCard.Margin = new Padding(4, 0, 0, 0);
        var recommendationLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        recommendationLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        recommendationLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 82));
        recommendationLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 18));
        recommendationLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var recommendationHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        recommendationHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        recommendationHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        recommendationHeader.Controls.Add(
            UiComponents.SectionLabel("3", "作业进度和后续安排"), 0, 0);

        var imageActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            AutoScroll = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        var showRecommendations = new CheckBox
        {
            Text = "显示推荐起飞点",
            AutoSize = true,
            Checked = false,
            ForeColor = UiTheme.TextSecondary,
            Margin = new Padding(0, 6, 8, 0)
        };
        if (MagOps.Core.Routes.LaunchSitePlanning.Enabled) imageActions.Controls.Add(showRecommendations);
        imageActions.Controls.Add(Button(
            "复制预览图",
            (_, _) => RunUi("复制作业进度预览", CopyFieldProgressPreviewToClipboard),
            UiButtonRole.Subtle));
        imageActions.Controls.Add(Button(
            "导出 JPG",
            (_, _) => RunUi("导出作业进度预览 JPG", ExportFieldProgressPreviewJpg),
            UiButtonRole.Subtle));
        recommendationHeader.Controls.Add(imageActions, 1, 0);
        recommendationLayout.Controls.Add(recommendationHeader, 0, 0);

        _fieldOperationsPreview.Dock = DockStyle.Fill;
        _fieldOperationsPreview.MinimumSize = new Size(240, 180);
        _fieldOperationsPreview.Margin = new Padding(0, 4, 0, 6);
        _fieldOperationsPreview.SortieClicked +=
            SelectFieldProgressSortieFromPreview;
        _fieldOperationsPreview.SelectionCleared +=
            ClearFieldProgressSortieSelectionFromPreview;
        recommendationLayout.Controls.Add(_fieldOperationsPreview, 0, 1);

        _fieldTakeoffGrid.ReadOnly = false;
        _fieldTakeoffGrid.MultiSelect = false;
        _fieldTakeoffGrid.MinimumSize = new Size(0, 118);
        _fieldTakeoffGrid.EditMode = DataGridViewEditMode.EditOnEnter;
        _fieldTakeoffGrid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_fieldTakeoffGrid.IsCurrentCellDirty)
                _fieldTakeoffGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _fieldTakeoffGrid.CellValueChanged += (_, e) =>
        {
            if (e.RowIndex < 0 ||
                e.ColumnIndex < 0 ||
                e.ColumnIndex >= _fieldTakeoffGrid.Columns.Count)
            {
                return;
            }

            DataGridViewColumn column =
                _fieldTakeoffGrid.Columns[e.ColumnIndex];
            if (column.DataPropertyName.Equals(
                    nameof(FieldTakeoffGridRow.Selected),
                    StringComparison.OrdinalIgnoreCase))
            {
                UpdateFieldPreviewVisibleServiceSorties();
            }
        };
        GridFactory.SetEmptyText(
            _fieldTakeoffGrid,
            "暂无后续起飞点安排。\r\n调整“预计每日架次 / 可接受转场次数”后重新分析。 ");
        recommendationLayout.Controls.Add(_fieldTakeoffGrid, 0, 2);

        var takeoffActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            AutoScroll = false,
            Margin = new Padding(0, 6, 0, 0),
            Padding = Padding.Empty
        };
        takeoffActions.Controls.Add(FieldToolbarLabel("可接受转场次数"));
        takeoffActions.Controls.Add(_fieldAcceptedRelocations);
        takeoffActions.Controls.Add(Button(
            "导出所选点坐标",
            (_, _) => RunUi("导出推荐点坐标", ExportSelectedTakeoffCoordinates),
            UiButtonRole.Subtle));
        takeoffActions.Controls.Add(Button(
            "导出所选点 GPX",
            (_, _) => RunUi("导出推荐点 GPX", ExportSelectedTakeoffGpx),
            UiButtonRole.Subtle));
        takeoffActions.Controls.Add(Button(
            "导出连接线 GPX",
            (_, _) => RunUi("导出起飞点连接线 GPX", ExportSelectedTakeoffConnectionGpx),
            UiButtonRole.Subtle));
        recommendationLayout.Controls.Add(takeoffActions, 0, 3);

        void UpdateRecommendationVisibility()
        {
            bool show = showRecommendations.Checked;
            _fieldOperationsPreview.ShowTakeoffRecommendations = show;
            _fieldTakeoffGrid.Visible = show;
            takeoffActions.Visible = show;
            recommendationLayout.RowStyles[1].Height = show ? 82 : 100;
            recommendationLayout.RowStyles[2].SizeType = show ? SizeType.Percent : SizeType.Absolute;
            recommendationLayout.RowStyles[2].Height = show ? 18 : 0;
            recommendationLayout.PerformLayout();
        }
        showRecommendations.CheckedChanged += (_, _) => UpdateRecommendationVisibility();
        UpdateRecommendationVisibility();

        recommendationCard.Controls.Add(recommendationLayout);
        layout.Controls.Add(recommendationCard, 1, 0);
        layout.SetRowSpan(recommendationCard, 2);

        return layout;
    }

    private Control BuildFieldOperationsReportCard()
    {
        var card = UiComponents.Card(new Padding(10));
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(UiComponents.SectionLabel("4", "项目进度简报"), 0, 0);

        var modeRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            AutoScroll = false,
            Margin = new Padding(0, 2, 0, 5),
            Padding = Padding.Empty
        };
        _fieldDailyReportMode.Text = "当天简报";
        _fieldDailyReportMode.AutoSize = true;
        _fieldDailyReportMode.Margin = new Padding(2, 4, 16, 2);
        _fieldProjectReportMode.Text = "总进度简报";
        _fieldProjectReportMode.AutoSize = true;
        _fieldProjectReportMode.Margin = new Padding(2, 4, 8, 2);
        _fieldDailyReportMode.CheckedChanged += (_, _) =>
        {
            if (_fieldDailyReportMode.Checked)
                RefreshFieldProgressReportPreview();
        };
        _fieldProjectReportMode.CheckedChanged += (_, _) =>
        {
            if (_fieldProjectReportMode.Checked)
                RefreshFieldProgressReportPreview();
        };
        modeRow.Controls.Add(_fieldDailyReportMode);
        modeRow.Controls.Add(_fieldProjectReportMode);
        layout.Controls.Add(modeRow, 0, 1);

        _fieldReportSummary.Dock = DockStyle.Fill;
        _fieldReportSummary.ReadOnly = true;
        _fieldReportSummary.WordWrap = true;
        _fieldReportSummary.ScrollBars = RichTextBoxScrollBars.Vertical;
        _fieldReportSummary.Text =
            "等待生成项目时区项目进度。\r\n可在“当天简报 / 总进度简报”之间切换预览。";
        layout.Controls.Add(_fieldReportSummary, 0, 2);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            AutoScroll = false,
            Margin = new Padding(0, 6, 0, 0),
            Padding = Padding.Empty
        };
        actions.Controls.Add(Button(
            "导出当前简报",
            (_, _) => RunUi("导出当前项目进度简报", ExportCurrentFieldProgressReport),
            UiButtonRole.Primary));
        actions.Controls.Add(Button(
            "复制到剪贴板",
            (_, _) => RunUi("复制当前项目进度简报", CopyCurrentFieldProgressReportToClipboard),
            UiButtonRole.Subtle));
        layout.Controls.Add(actions, 0, 3);

        _fieldDailyReportMode.Checked = true;
        card.Controls.Add(layout);
        return card;
    }

    private void NormalizeFieldDataSplit(SplitContainer split)
    {
        if (split.IsDisposed || split.Orientation != Orientation.Vertical)
            return;
        int usable = split.ClientSize.Width - split.SplitterWidth;
        if (usable < 200)
            return;
        int preferred = (int)Math.Round(usable * 0.48d);
        int min = Math.Min(260, Math.Max(0, usable / 3));
        int max = Math.Max(min, usable - min);
        split.SplitterDistance = Math.Clamp(preferred, min, max);
    }

    private static Label FieldToolbarLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = UiTheme.TextSecondary,
        Margin = new Padding(8, 8, 4, 3)
    };

    private static void ConfigureFieldSourceStatusLabel(Label label)
    {
        label.AutoSize = false;
        label.Dock = DockStyle.Fill;
        label.Height = 22;
        label.AutoEllipsis = true;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.ForeColor = UiTheme.TextSecondary;
        label.Margin = new Padding(2, 1, 2, 1);
        label.Padding = Padding.Empty;
    }

    private void AutoAnalyzeFieldOperationsOnActivation()
    {
        try
        {
            // Entering field management stays source-light: restore saved
            // per-workarea parameters and the last successful scan snapshot from
            // 00_项目管理/magops. The source directory itself is not recursively
            // enumerated until the user clicks “扫描/核对”.
            LoadFieldOperationsSettings();
            EnsureDefaultFieldDataFolder();

            string display = string.IsNullOrWhiteSpace(_fieldDataFolderFullPath)
                ? "—"
                : WorkareaRelativePathForFieldUi(_fieldDataFolderFullPath);

            bool restored =
                _fieldMagDataAnalysis is not null ||
                TryRestoreCachedFieldAnalysis(display);
            if (!restored)
                ApplyCachedFieldScanSummary(display);

            RefreshFieldwork();
            RefreshFieldBaseStationContext();

            SetStatus(
                restored
                    ? $"外业管理已恢复上次扫描结果：{display}；尚未核对源文件变化。点击“扫描/核对”可执行增量核对。"
                    : $"外业管理已读取数据目录：{display}；暂无完整扫描快照。需要建立/更新时请点击“扫描/核对”。");
        }
        catch (Exception ex)
        {
            _fieldReportSummary.Text =
                "项目进度：默认外业数据目录读取未完成。\r\n" +
                "后续安排：请确认当前工区及项目目录后重试。";
            SetStatus($"外业数据目录读取未完成：{ex.Message}");
        }
    }

    private void QueueFieldOperationsContextRestore()
    {
        if (_fieldOperationsPage is not null)
            QueueFeatureActivation(FeatureIds.FieldOperations, AutoAnalyzeFieldOperationsOnActivation);
    }

    private void RefreshFieldBaseStationContext()
    {
        _fieldOperationsPreview.SetBaseStation(
            null,
            "");

        if (!IsProjectInitialized())
        {
            _fieldBaseStationStatus.Text =
                "所选作业日：项目尚未初始化。";
            return;
        }

        string date =
            _fieldOperationsDate.Value.Date
                .ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture);
        List<BaseStationLogRecord> stations =
            _fieldwork.ListBaseStations(Root)
                .Where(x =>
                    x.Date.Equals(
                        date,
                        StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x.UpdatedAt, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.RecordId, StringComparer.OrdinalIgnoreCase)
                .ToList();

        if (stations.Count == 0)
        {
            _fieldBaseStationStatus.Text =
                $"{date}：未登记日变站。";
            return;
        }

        BaseStationLogRecord station =
            stations[^1];
        string stationName =
            string.IsNullOrWhiteSpace(station.StationId)
                ? station.RecordId
                : station.StationId.Trim();
        string time =
            string.IsNullOrWhiteSpace(station.Start) &&
            string.IsNullOrWhiteSpace(station.End)
                ? "时间未登记"
                : $"{NormalizeBaseStationTime(station.Start)}–{NormalizeBaseStationTime(station.End)}";
        string sampling =
            string.IsNullOrWhiteSpace(station.SamplingIntervalS)
                ? ""
                : $"｜采样 {station.SamplingIntervalS.Trim()} s";
        string site =
            string.IsNullOrWhiteSpace(station.Site)
                ? ""
                : $"｜{station.Site.Trim()}";

        if (TryParseBaseStationCoordinate(
                station,
                out GeoCoordinate coordinate))
        {
            _fieldBaseStationStatus.Text =
                $"{date}：{stationName}{site}｜{time}{sampling}｜" +
                $"{coordinate.Longitude:0.######}, {coordinate.Latitude:0.######}" +
                (stations.Count > 1
                    ? $"｜当日记录 {stations.Count} 条，显示最近更新"
                    : "");
            _fieldOperationsPreview.SetBaseStation(
                coordinate,
                stationName);
        }
        else
        {
            _fieldBaseStationStatus.Text =
                $"{date}：{stationName}{site}｜{time}{sampling}｜未登记有效经纬度" +
                (stations.Count > 1
                    ? $"｜当日记录 {stations.Count} 条，显示最近更新"
                    : "");
        }
    }

    private static string NormalizeBaseStationTime(
        string value) =>
        string.IsNullOrWhiteSpace(value)
            ? "—"
            : value.Trim();

    private static bool TryParseBaseStationCoordinate(
        BaseStationLogRecord station,
        out GeoCoordinate coordinate)
    {
        coordinate = new GeoCoordinate(0d, 0d);
        bool lonOk =
            double.TryParse(
                station.Longitude,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double longitude) ||
            double.TryParse(
                station.Longitude,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out longitude);
        bool latOk =
            double.TryParse(
                station.Latitude,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double latitude) ||
            double.TryParse(
                station.Latitude,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out latitude);
        if (!lonOk ||
            !latOk ||
            !double.IsFinite(longitude) ||
            !double.IsFinite(latitude) ||
            longitude < -180d ||
            longitude > 180d ||
            latitude < -90d ||
            latitude > 90d)
        {
            return false;
        }

        coordinate =
            new GeoCoordinate(
                longitude,
                latitude);
        return true;
    }

    private void LocateFieldBaseStationInPreview()
    {
        RefreshFieldBaseStationContext();
        if (!_fieldOperationsPreview.HasBaseStation)
        {
            TransientNotice.Show(
                this,
                "所选作业日没有可用于预览的日变站经纬度。请先登记经纬度。",
                "日变站未定位");
            return;
        }

        _fieldOperationsPreview.ShowBaseStationOverview();
        SetStatus(
            "预览已临时适配“工区 + 日变站”全景；日变站较远时航线会缩小。点击“适配工区”恢复正常航线尺度。");
    }

    private void FitFieldPreviewToWorkarea()
    {
        _fieldOperationsPreview.FitWorkarea();
        SetStatus(
            "预览已恢复工区航线尺度；远距离日变站以边缘方向/距离提示显示，不参与自动缩放。");
    }

    private bool _fieldIntakeBusy;
    private void ImportFieldDataFiles() => ImportFieldDataFilesCore(null);
    private void ImportFieldDataFilesCore(IReadOnlyList<string>? droppedPaths)
    {
        if (_fieldIntakeBusy) throw new InvalidOperationException("正在导入外业数据，请稍后重试。");
        _fieldIntakeBusy = true;
        try
        {
        EnsureInitialized();
        EnsureRememberedWorkareaContextLoaded();
        if (string.IsNullOrWhiteSpace(
            CurrentWorkareaRelativePath))
        {
            SelectWorkareaRelativePath(
                "导入外业数据");
        }

        bool recursiveFolderImport =
            droppedPaths is null && _fieldDataImportRecursive.Checked;
        string recursiveSourceFolder = "";
        string[] selectedFiles = droppedPaths?.ToArray() ?? Array.Empty<string>();

        if (droppedPaths is null)
        {
        if (recursiveFolderImport)
        {
            using var folderDialog =
                new FolderBrowserDialog
                {
                    Description =
                        "选择本批新增外业数据的源文件夹；程序会递归读取该文件夹及所有子文件夹中的文件。",
                    ShowNewFolderButton = false
                };
            FileInteractions.For(this, "field-intake").Prepare(folderDialog);
            if (folderDialog.ShowDialog(this) !=
                DialogResult.OK)
            {
                return;
            }

            recursiveSourceFolder =
                folderDialog.SelectedPath;
        }
        else
        {
            using var fileDialog =
                new OpenFileDialog
                {
                    Title = "选择本批新增外业数据",
                    Filter =
                        "外业数据|*.csv;*.txt;*.tsv;*.dat;*.asc;*.xyz;*.log;*.stn|所有文件|*.*",
                    Multiselect = true,
                    CheckFileExists = true,
                    RestoreDirectory = true
                };
            FileInteractions.For(this, "field-intake").Prepare(fileDialog);
            if (fileDialog.ShowDialog(this) !=
                DialogResult.OK)
            {
                return;
            }

            selectedFiles =
                fileDialog.FileNames;
        }

        }
        if (!recursiveFolderImport && (selectedFiles.Length == 0 || selectedFiles.Any(p => !File.Exists(p) ||
            !FileDropSupport.HasExtension(p, ".csv", ".txt", ".tsv", ".dat", ".asc", ".xyz", ".log", ".stn"))))
            throw new InvalidDataException("请选择有效的外业数据文件；混合无效输入未导入。");

        string managedFieldRoot =
            ResolveDefaultFieldDataDirectory();
        FieldDataImportBatchResult? imported =
            null;
        ExecuteWithBusyPrompt(
            "导入外业数据",
            recursiveFolderImport
                ? "正在递归读取源文件夹、计算 SHA-256、复制原始文件、核对重复项并更新项目数据，请稍候…"
                : "正在计算 SHA-256、复制原始文件、核对重复项并更新项目数据，请稍候…",
            () =>
            {
                IReadOnlyList<string> importSources;
                if (recursiveFolderImport)
                {
                    importSources =
                        _fieldDataImport.DiscoverSourceFiles(
                            recursiveSourceFolder,
                            recursive: true);
                    if (importSources.Count == 0)
                    {
                        throw new InvalidOperationException(
                            "所选源文件夹及其子文件夹中没有可导入的文件。");
                    }
                }
                else
                {
                    importSources =
                        selectedFiles;
                }

                imported =
                    _fieldDataImport.ImportFiles(
                        Root,
                        CurrentWorkareaRelativePath,
                        managedFieldRoot,
                        importSources);
                SetFieldDataFolderPath(
                    managedFieldRoot,
                    persistSettings: true);

                if (imported.ImportedCount > 0)
                {
                    AnalyzeSelectedFieldDataAndProgress();
                    if (_fieldMagDataAnalysis is not null)
                    {
                        _fieldDataImport.EnrichFromAnalysis(
                            Root,
                            _fieldMagDataAnalysis);
                    }
                }
            });

        if (imported is null)
            return;

        string detail =
            recursiveFolderImport
                ? $"递归发现 {imported.Items.Count} 个文件：新增入库 {imported.ImportedCount}，内容重复跳过 {imported.DuplicateCount}。"
                : $"本批 {imported.Items.Count} 个文件：新增入库 {imported.ImportedCount}，内容重复跳过 {imported.DuplicateCount}。";
        string coverageDetail = BuildCoverageImportDetail(imported);
        if (!string.IsNullOrWhiteSpace(coverageDetail))
            detail += "\r\n" + coverageDetail;
        SetStatus(
            $"外业数据导入完成：{detail.Replace("\r\n", "；")}");
        TransientNotice.Show(
            this,
            detail,
            "外业数据导入完成");
        if (imported is not null) FileInteractions.For(this, "field-intake").InputSucceeded(imported.Items.Where(x => x.Imported || x.Duplicate).Select(x => x.SourcePath).ToArray());
        }
        finally { _fieldIntakeBusy = false; }

    }

    private void ImportDjiFlightRecordFolder()
    {
        EnsureInitialized();
        EnsureRememberedWorkareaContextLoaded();
        if (string.IsNullOrWhiteSpace(
                CurrentWorkareaRelativePath))
        {
            SelectWorkareaRelativePath(
                "导入 DJI 飞行记录");
        }

        using var dialog =
            new FolderBrowserDialog
            {
                Description =
                    "选择从 DJI Pilot 2 遥控器复制到电脑的 FlightRecord 文件夹；程序会递归发现 DJIFlightRecord_*.txt。",
                ShowNewFolderButton =
                    false
            };
        FileInteractions.For(this, "dji-folder").Prepare(dialog);
        if (dialog.ShowDialog(this) !=
            DialogResult.OK)
        {
            return;
        }

        DjiFlightImportBatchResult? imported =
            null;
        string managedFieldRoot =
            ResolveDefaultFieldDataDirectory();
        ExecuteWithBusyPrompt(
            "导入 DJI 飞行记录",
            "正在读取 DJI v13/v14 Details、计算 SHA-256、复制 FlightRecord 并建立飞行 Session，请稍候…",
            () =>
            {
                imported =
                    _djiFlightRecords.ImportFolder(
                        Root,
                        CurrentWorkareaRelativePath,
                        managedFieldRoot,
                        dialog.SelectedPath);
            });

        if (imported is null)
            return;

        _fieldAutoAnalysisContextKey = "";
        _fieldProjectTimelineContextKey = "";
        if (_fieldMagDataAnalysis is not null)
        {
            AnalyzeProjectFieldTimeline(
                force: true,
                quietNoRoute: true);
        }
        else
        {
            AnalyzeFieldOperations(
                force: true,
                quietNoRoute: true);
        }

        string detail =
            $"DJI FlightRecord：新增 {imported.ImportedCount}，重复 {imported.DuplicateCount}，不支持/失败 {imported.RejectedCount}。";
        SetStatus(detail);
        TransientNotice.Show(
            this,
            detail,
            "DJI 飞行记录导入完成");
        if (imported is not null && imported.ImportedCount + imported.DuplicateCount > 0) FileInteractions.For(this, "dji-folder").DirectorySucceeded(dialog.SelectedPath);
    }

    private void RegisterDjiMcdatFiles()
    {
        EnsureInitialized();
        EnsureRememberedWorkareaContextLoaded();
        if (string.IsNullOrWhiteSpace(
                CurrentWorkareaRelativePath))
        {
            SelectWorkareaRelativePath(
                "登记 DJI MCDat");
        }

        using var dialog =
            new OpenFileDialog
            {
                Title =
                    "选择要登记的 MCDat 精简飞控数据（只计算 SHA/大小，不复制）",
                Filter =
                    "DJI MCDat (*.dat)|*.dat|所有文件 (*.*)|*.*",
                Multiselect =
                    true,
                CheckFileExists =
                    true,
                RestoreDirectory =
                    true
            };
        FileInteractions.For(this, "dji-mcdat").Prepare(dialog);
        if (dialog.ShowDialog(this) !=
            DialogResult.OK)
        {
            return;
        }

        DjiMcdatRegistrationResult? result =
            null;
        ExecuteWithBusyPrompt(
            "登记 DJI MCDat",
            "正在计算大文件 SHA-256 并登记元数据；原始 MCDat 不会复制进项目，请稍候…",
            () =>
            {
                result =
                    _djiFlightRecords.RegisterMcdatFiles(
                        Root,
                        CurrentWorkareaRelativePath,
                        dialog.FileNames);
            });

        if (result is null)
            return;

        string detail =
            $"MCDat：新增登记 {result.Registered.Count}，重复 {result.DuplicateCount}；本操作未复制 DAT 大文件。";
        SetStatus(detail);
        TransientNotice.Show(
            this,
            detail,
            "MCDat 登记完成");
        if (result is not null) FileInteractions.For(this, "dji-mcdat").InputSucceeded(dialog.FileNames);
    }

    private void SelectFieldDataFolder()
    {
        var files = FileInteractions.For(this, "field-data-folder");
        using var dialog = new FolderBrowserDialog { Description = "选择外业数据目录（只设置路径，不自动扫描）", ShowNewFolderButton = false };
        files.Prepare(dialog, current: _fieldDataFolderFullPath, business: ResolveDefaultFieldDataDirectory());
        if (dialog.ShowDialog(this) == DialogResult.OK) AdoptFieldDataDirectory(dialog.SelectedPath);
    }
    private void AdoptFieldDataDirectory(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("外业数据目录不存在。");
        SetFieldDataFolderPath(path, persistSettings: true);
        ApplyCachedFieldScanSummary(WorkareaRelativePathForFieldUi(_fieldDataFolderFullPath));
        FileInteractions.For(this, "field-data-folder").DirectorySucceeded(path);
        SetStatus("已切换外业数据目录，尚未扫描；点击“扫描/核对”后核对变化。");
    }

    private void ImportExternalRouteKml(
        bool appendToActiveSet,
        IReadOnlyList<string>? selectedFiles = null)
    {
        EnsureInitialized();
        string workarea = SelectWorkareaRelativePath(
            appendToActiveSet ? "加入外部航线 KML" : "替换外部航线集");

        IReadOnlyList<string> files;
        if (selectedFiles is null)
        {
            using var dialog = new OpenFileDialog
            {
                Title = appendToActiveSet
                    ? "选择要加入当前航线集的 KML"
                    : "选择新航线集的 KML",
                Filter = "KML 航线 (*.kml)|*.kml|所有文件 (*.*)|*.*",
                Multiselect = true,
                CheckFileExists = true,
                RestoreDirectory = true
            };

            string initial = GetToolOutputInitialDirectory();
            if (Directory.Exists(initial))
                dialog.InitialDirectory = initial;

            FileInteractions.For(this, "external-route-kml").Prepare(dialog);

            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;

            files = dialog.FileNames;
        }
        else
        {
            files = selectedFiles;
        }

        if (files.Count == 0)
            return;

        string importRoot = _folderLayout.ResolveOperationDirectory(
            Root,
            "route_kml_import",
            workarea);

        RouteKmlImportResult? result = null;
        bool appendedToExisting = false;
        FieldRouteSnapshot? imported = null;

        ExecuteWithBusyPrompt(
            appendToActiveSet ? "加入外部航线 KML" : "替换外部航线集",
            appendToActiveSet
                ? "正在校验并加入 KML 到当前航线集，随后重新计算项目外业进度…"
                : "正在建立新的活动航线集，随后重新计算项目外业进度…",
            () =>
            {
                if (appendToActiveSet)
                {
                    FieldRouteSnapshot? active =
                        ResolveFieldOperationsRouteSnapshot();
                    if (active is not null &&
                        active.SourceLabel.Equals(
                            "外部导入航线 KML",
                            StringComparison.OrdinalIgnoreCase) &&
                        Directory.Exists(active.SourcePath))
                    {
                        result = _routeKmlImport.Append(
                            active.SourcePath,
                            files);
                        appendedToExisting = true;
                    }
                    else
                    {
                        result = _routeKmlImport.Import(
                            importRoot,
                            files);
                    }
                }
                else
                {
                    // "Replace" means switch the active route set. Historical
                    // imported batches remain archived in the project tree.
                    result = _routeKmlImport.Import(
                        importRoot,
                        files);
                }

                _workspace.SyncWorkareas(Root);
                RefreshWorkareaContextSelectors();
                RegisterGeneratedBusinessFiles(
                    result.Files,
                    "route_kml_import");

                _fieldPreferredRouteImportDirectory =
                    result.BatchDirectory;
                _fieldAutoAnalysisContextKey = "";
                _fieldProjectTimelineContextKey = "";

                imported =
                    _fieldOperationsAnalysis.TryLoadImportedRouteKmlSnapshot(
                        result.BatchDirectory);
                if (imported is not null)
                {
                    _fieldRouteSourceStatus.Text =
                        $"设计航线：外部导入 KML；{imported.Sorties.Count} 架次；" +
                        $"来源 {ShortPathForFieldUi(imported.SourcePath)}";
                    _toolTip.SetToolTip(
                        _fieldRouteSourceStatus,
                        imported.SourcePath);
                }

                AnalyzeProjectFieldTimeline(
                    force: true,
                    quietNoRoute: true);
                RefreshProject();
            });

        if (result is null)
            return;

        FileInteractions.For(this, "route_kml_import", "output").OutputSucceeded(result.BatchDirectory, directory: true);
        // A new active route set changes the design lineage used by mother candidates.
        InvalidateDiurnalQuality();

        string totalText = imported is null
            ? ""
            : $"；当前航线集共 {imported.Sorties.Count} 架次";
        string actionText = appendedToExisting
            ? "已加入当前航线集"
            : appendToActiveSet
                ? "已建立外部航线集"
                : "已切换为新航线集";

        string detail = result.Items.Count == 0
            ? "所选 KML 与当前航线集已有文件内容相同，未重复加入。"
            : $"{actionText}：新增 {result.Items.Count} 个 KML / {result.RouteGeometryCount} 条航线{totalText}。";

        TransientNotice.Show(
            this,
            detail + "\r\n" +
            $"项目位置：{ShortPathForFieldUi(result.BatchDirectory)}",
            "外部航线集已更新");
        if (result is not null) FileInteractions.For(this, "external-route-kml").InputSucceeded(files.ToArray());
    }

    private void AnalyzeSelectedFieldDataAndProgress() =>
        AnalyzeSelectedFieldDataAndProgress(
            forceFullScan: false);

    private void AnalyzeSelectedFieldDataAndProgress(
        bool forceFullScan)
    {
        EnsureDefaultFieldDataFolder();
        if (!AnalyzeFieldDataFolder(
            force: true,
            quietMissing: false,
            forceFullScan: forceFullScan)) return;
        // Field-data recognition is independent from route progress. A UAV +
        // GSM-19T-only project must still scan, cache, QC and remain eligible for
        // point-level production even when no design/imported route exists yet.
        AnalyzeProjectFieldTimeline(
            force: true,
            quietNoRoute: true);
    }

    private bool TryRestoreCachedFieldAnalysis(
        string displayPath)
    {
        if (string.IsNullOrWhiteSpace(
            _fieldDataFolderFullPath))
        {
            return false;
        }

        string cacheDirectory =
            FieldMagDataService.ResolveIncrementalCacheDirectory(
                Root,
                _fieldDataFolderFullPath);
        if (!FieldMagDataService.TryRestoreIncrementalAnalysis(
            _fieldDataFolderFullPath,
            cacheDirectory,
            out FieldMagCachedAnalysisRestoreResult restored))
        {
            return false;
        }

        _fieldMagDataAnalysis = restored.Analysis;
        _fieldDataAnalysisContextKey =
            restored.SourceSnapshotKey;
        _fieldAutoAnalysisContextKey = "";
        _fieldProjectTimelineContextKey = "";
        ApplyFieldDataAnalysis(
            restored.Analysis);

        DateTimeOffset beijing =
            ProjectTime.Display(restored.UpdatedAtUtc, _projectTimeZone);
        _fieldDataSummary.AppendText(
            "\r\n" +
            $"已从项目扫描快照恢复完整识别结果（{beijing:yyyy-MM-dd HH:mm:ss} 项目时区）。" +
            "本次启动尚未递归核对源文件；如现场数据有新增/修改，请点击“扫描/核对”执行增量核对。");

        if (_fieldDateSelector.SelectedItem is
            FieldDateChoice choice)
        {
            _fieldOperationsDate.Value =
                choice.UtcDate;
            _fieldAutoAnalysisContextKey = "";
            AnalyzeFieldOperations(
                force: true,
                quietNoRoute: true);
        }

        _fieldDataFolderStatus.Text =
            $"扫描快照已恢复：{displayPath}；源文件变化尚未核对。";
        return true;
    }

    private void LoadFieldOperationsSettings()
    {
        if (!IsProjectInitialized())
            return;

        EnsureRememberedWorkareaContextLoaded();
        if (string.IsNullOrWhiteSpace(
            CurrentWorkareaRelativePath))
        {
            return;
        }

        FieldOperationsSettingsRecord? saved =
            _fieldwork.GetFieldOperationsSettings(
                Root,
                CurrentWorkareaRelativePath);
        if (saved is null)
            return;

        _loadingFieldOperationsSettings = true;
        try
        {
            _fieldSpatialScreening = saved.SpatialScreening;
            _demHeightOptions = saved.DemHeight is { } demSettings
                ? demSettings with { DemPath = ResolveFieldDataFolderSetting(demSettings.DemPath) }
                : new();
            _fieldExpectedDailySorties.Value =
                Math.Clamp(
                    saved.ExpectedDailySorties,
                    (int)_fieldExpectedDailySorties.Minimum,
                    (int)_fieldExpectedDailySorties.Maximum);
            _fieldAcceptedRelocations.Value =
                Math.Clamp(
                    saved.AcceptedRelocations,
                    (int)_fieldAcceptedRelocations.Minimum,
                    (int)_fieldAcceptedRelocations.Maximum);

            string restoredFolder =
                ResolveFieldDataFolderSetting(
                    saved.FieldDataFolder);
            if (!string.IsNullOrWhiteSpace(
                    restoredFolder) &&
                Directory.Exists(restoredFolder))
            {
                SetFieldDataFolderPath(
                    restoredFolder,
                    persistSettings: false);
            }
        }
        finally
        {
            _loadingFieldOperationsSettings = false;
        }
    }

    private void SaveFieldOperationsSettings()
    {
        if (_loadingFieldOperationsSettings ||
            !IsProjectInitialized())
        {
            return;
        }

        EnsureRememberedWorkareaContextLoaded();
        if (string.IsNullOrWhiteSpace(
            CurrentWorkareaRelativePath))
        {
            return;
        }

        _fieldwork.SaveFieldOperationsSettings(
            Root,
            new FieldOperationsSettingsRecord
            {
                WorkareaPath =
                    CurrentWorkareaRelativePath,
                FieldDataFolder =
                    EncodeFieldDataFolderSetting(
                        _fieldDataFolderFullPath),
                ExpectedDailySorties =
                    (int)_fieldExpectedDailySorties.Value,
                AcceptedRelocations =
                    (int)_fieldAcceptedRelocations.Value,
                SpatialScreening = _fieldSpatialScreening,
                DemHeight = _demHeightOptions with { DemPath = EncodeFieldDataFolderSetting(_demHeightOptions.DemPath) }
            });
    }

    private string EncodeFieldDataFolderSetting(
        string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        try
        {
            string full =
                Path.GetFullPath(path);
            string project =
                Path.GetFullPath(Root)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
            if (full.Equals(
                project,
                StringComparison.OrdinalIgnoreCase))
            {
                return "project:.";
            }

            string prefix =
                project +
                Path.DirectorySeparatorChar;
            if (full.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
            {
                return
                    "project:" +
                    Path.GetRelativePath(
                        project,
                        full)
                    .Replace('\\', '/');
            }

            return "absolute:" + full;
        }
        catch
        {
            return "";
        }
    }

    private string ResolveFieldDataFolderSetting(
        string setting)
    {
        if (string.IsNullOrWhiteSpace(setting))
            return "";

        if (setting.StartsWith(
            "project:",
            StringComparison.OrdinalIgnoreCase))
        {
            string relative =
                setting["project:".Length..]
                    .Replace(
                        '/',
                        Path.DirectorySeparatorChar);
            return Path.GetFullPath(
                Path.Combine(
                    Root,
                    relative));
        }

        if (setting.StartsWith(
            "absolute:",
            StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFullPath(
                setting["absolute:".Length..]);
        }

        return "";
    }

    private void ApplyCachedFieldScanSummary(
        string displayPath)
    {
        if (string.IsNullOrWhiteSpace(
            _fieldDataFolderFullPath))
        {
            _fieldDataFolderStatus.Text =
                "项目数据识别：尚未确定外业数据目录。";
            _fieldDataFingerprintStatus.Text =
                "项目完整规范数据集：等待扫描。";
            return;
        }

        string cacheDirectory =
            FieldMagDataService.ResolveIncrementalCacheDirectory(
                Root,
                _fieldDataFolderFullPath);
        if (!FieldMagDataService.TryReadIncrementalCacheSummary(
            cacheDirectory,
            out FieldMagIncrementalCacheSummary summary))
        {
            _fieldDataFolderStatus.Text =
                $"数据识别：已读取目录 {displayPath}；暂无可用扫描缓存。";
            _fieldDataFingerprintStatus.Text =
                "项目完整规范数据集：点击“扫描/核对”执行首次详细扫描并建立增量缓存。";
            _fieldDataSummary.Text =
                "当前只读取了外业数据目录，没有递归枚举或解析文件。\r\n" +
                "首次点击“扫描/核对”会建立项目级文件索引和分文件解析缓存；以后扫描只解析变化文件。";
            return;
        }

        DateTimeOffset beijing =
            ProjectTime.Display(summary.UpdatedAtUtc, _projectTimeZone);
        string fingerprint =
            summary.Fingerprint.Length > 16
                ? summary.Fingerprint[..16]
                : summary.Fingerprint;

        _fieldDataFolderStatus.Text =
            $"上次扫描缓存：{summary.IndexedFileCount} 个文件；更新时间（项目时区） {beijing:yyyy-MM-dd HH:mm:ss}。";
        _fieldDataFingerprintStatus.Text =
            $"缓存完整规范数据：UAV {summary.UavRecordCount} 条；Base {summary.BaseRecordCount} 条；" +
            $"作业日 {summary.BeijingDateCount}；QC 风险 {summary.WarningCount}；指纹 {fingerprint}。";
        _fieldDataSummary.Text =
            "已读取上次扫描的轻量摘要；本次尚未递归核对源文件。\r\n" +
            $"上次增量扫描：发现 {summary.LastScan.DiscoveredFiles}，复用 {summary.LastScan.ReusedFiles}，" +
            $"新增 {summary.LastScan.AddedFiles}，修改 {summary.LastScan.ChangedFiles}，删除 {summary.LastScan.RemovedFiles}。\r\n" +
            "点击“扫描项目数据”后只重新解析新增/修改/缓存缺失文件；未变化文件直接复用分文件缓存。";
    }

    private void SetFieldDataFolderPath(
        string path,
        bool persistSettings = true)
    {
        _fieldDataFolderFullPath = string.IsNullOrWhiteSpace(path)
            ? ""
            : Path.GetFullPath(path);
        _fieldDataFolder.Text = string.IsNullOrWhiteSpace(_fieldDataFolderFullPath)
            ? ""
            : WorkareaRelativePathForFieldUi(_fieldDataFolderFullPath);
        _fieldDataAnalysisContextKey = "";
        _fieldAutoAnalysisContextKey = "";
        _fieldProjectTimelineContextKey = "";

        if (persistSettings)
            SaveFieldOperationsSettings();
    }

    private string WorkareaRelativePathForFieldUi(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "—";

        try
        {
            string full = Path.GetFullPath(path);
            if (!string.IsNullOrWhiteSpace(CurrentWorkareaRelativePath))
            {
                string workareaRoot = Path.GetFullPath(
                    Path.Combine(Root, CurrentWorkareaRelativePath))
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (full.Equals(workareaRoot, StringComparison.OrdinalIgnoreCase))
                    return "工区根目录";

                string workareaPrefix = workareaRoot + Path.DirectorySeparatorChar;
                if (full.StartsWith(workareaPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetRelativePath(workareaRoot, full)
                        .Replace('\\', '/');
                }
            }

            string projectRoot = Path.GetFullPath(Root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string projectPrefix = projectRoot + Path.DirectorySeparatorChar;
            if (full.StartsWith(projectPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return $"项目级/{Path.GetRelativePath(projectRoot, full).Replace('\\', '/')}";
            }

            string trimmed = full.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            return $"外部/{Path.GetFileName(trimmed)}";
        }
        catch
        {
            return Path.GetFileName(path);
        }
    }

    private void SelectAdjacentFieldDate(bool older)
    {
        if (_fieldDateSelector.DataSource is not List<FieldDateChoice> choices ||
            choices.Count == 0)
            return;

        int current = Math.Max(0, _fieldDateSelector.SelectedIndex);
        int next = older ? current + 1 : current - 1;
        next = Math.Clamp(next, 0, choices.Count - 1);
        if (next != current)
            _fieldDateSelector.SelectedIndex = next;
    }

    private void EnsureDefaultFieldDataFolder()
    {
        if (!string.IsNullOrWhiteSpace(_fieldDataFolderFullPath) &&
            Directory.Exists(_fieldDataFolderFullPath))
            return;

        string preferred = ResolveDefaultFieldDataDirectory();
        if (!string.IsNullOrWhiteSpace(preferred))
            SetFieldDataFolderPath(preferred);
    }

    private string ResolveDefaultFieldDataDirectory()
    {
        if (!IsProjectInitialized())
            return Directory.Exists(Root) ? Root : Environment.CurrentDirectory;
        EnsureRememberedWorkareaContextLoaded();
        if (string.IsNullOrWhiteSpace(CurrentWorkareaRelativePath))
            return Root;

        // Field management operates on the governed fieldwork tree. The ingestion
        // layer already distinguishes Raw from derived/merged MagOps outputs, so the
        // default can be the complete 05_外业记录与采集成果 hierarchy.
        return _folderLayout.ResolveWorkareaFolder(
            Root,
            CurrentWorkareaRelativePath,
            "workarea.field");
    }

    private bool AnalyzeFieldDataFolder(
        bool force,
        bool quietMissing,
        bool forceFullScan = false)
    {
        if (_processingReview?.HasUnsavedWorkspaceReview == true &&
            !ConfirmContextSwitch("重新核对外业数据")) return false;
        string full = _fieldDataFolderFullPath;
        if (string.IsNullOrWhiteSpace(full))
        {
            if (quietMissing)
                return false;
            throw new InvalidOperationException(
                "请选择包含项目周期 19T 与无人机光泵数据的目录。 ");
        }

        full = Path.GetFullPath(full);
        if (!Directory.Exists(full))
        {
            string display =
                WorkareaRelativePathForFieldUi(full);
            if (quietMissing)
            {
                _fieldDataFolderStatus.Text =
                    $"数据识别：目录不存在：{display}";
                return false;
            }
            throw new DirectoryNotFoundException(
                $"项目外业数据目录不存在：{display}");
        }

        if (!force &&
            _fieldMagDataAnalysis is not null)
        {
            return true;
        }

        string cacheDirectory =
            FieldMagDataService.ResolveIncrementalCacheDirectory(
                Root,
                full);
        FieldMagIncrementalAnalysisResult result =
            _fieldMagDataService.AnalyzeFolderIncremental(
                full,
                cacheDirectory,
                forceFullScan);

        FieldMagFolderAnalysis analysis =
            result.Analysis;
        _fieldMagDataAnalysis = analysis;
        FieldDataConsistencyReport consistency = _fieldDataImport.EnrichFromAnalysis(
            Root,
            analysis,
            fullyParsed: result.Statistics.ReusedFiles == 0);
        if (consistency.RequiresFullReparse && !forceFullScan && result.Statistics.ReusedFiles > 0)
        {
            // Same path/size/timestamp cannot hide changed imported content.
            result = _fieldMagDataService.AnalyzeFolderIncremental(full, cacheDirectory, forceFullScan: true);
            analysis = result.Analysis;
            _fieldMagDataAnalysis = analysis;
            consistency = _fieldDataImport.EnrichFromAnalysis(Root, analysis, fullyParsed: true);
        }
        _fieldDataAnalysisContextKey =
            result.SourceSnapshotKey;
        _fieldAutoAnalysisContextKey = "";
        ApplyFieldDataAnalysis(analysis);
        RefreshArtifactFreshnessStatus();

        FieldMagIncrementalScanStatistics stats =
            result.Statistics;
        string mode =
            forceFullScan
                ? "完整重扫"
                : "增量扫描";
        string cacheWarnings =
            stats.CacheReadFailures == 0 &&
            stats.CacheWriteFailures == 0
                ? ""
                : $"；缓存读失败 {stats.CacheReadFailures} / 写失败 {stats.CacheWriteFailures}";
        string unstable =
            stats.UnstableFiles == 0
                ? ""
                : $"；写入中/变化文件 {stats.UnstableFiles}";

        _fieldDataSummary.AppendText(
            "\r\n" +
            $"{mode}：发现 {stats.DiscoveredFiles}；复用缓存 {stats.ReusedFiles}；" +
            $"新增 {stats.AddedFiles}；修改 {stats.ChangedFiles}；删除 {stats.RemovedFiles}；" +
            $"重新解析 {stats.ParsedFiles}{unstable}{cacheWarnings}。");
        _fieldDataSummary.AppendText($"\r\n入库完整性：核对 {consistency.CheckedFiles} 项；缺失 {consistency.Issues.Count(i => i.Status == "MISSING_FILE")}，" +
            $"内容改变 {consistency.Issues.Count(i => i.Status == "FILE_CHANGED")}，不可读取 {consistency.Issues.Count(i => i.Status == "FILE_UNREADABLE")}。" +
            "报告：00_项目管理/magops/field_data_consistency.json。原始SHA及来源记录保留；失效台账不参与去重。" +
            (consistency.Issues.Count == 0 ? "" : "\r\n" + string.Join("\r\n", consistency.Issues.Take(12).Select(i => $"{i.Status}：{i.RelativePath}"))) +
            (stats.ChangedFiles + stats.RemovedFiles > 0 ? "\r\n输入已变化，相关核验决定和派生成果需按新指纹重新核对。" : ""));

        SetStatus(
            $"{mode}完成：发现 {stats.DiscoveredFiles} 个文件，复用 {stats.ReusedFiles}，" +
            $"重新解析 {stats.ParsedFiles}，删除 {stats.RemovedFiles}；" +
            $"UAV {analysis.UavSamples.Count} 条，GSM-19T {analysis.BaseSamples.Count} 条。 ");
        return true;
    }

    private void ApplyFieldDataAnalysis(FieldMagFolderAnalysis analysis)
    {
        InvalidateDiurnalQuality();
        if (!_preserveProcessingReview && _processingCsvInput is null) ResetProcessingReview();
        analysis = ApplyCurrentWorkareaScreening(analysis);
        _fieldMagDataAnalysis = analysis;
        SaveFieldOperationsSettings();
        _fieldDataFolderStatus.Text =
            $"项目数据识别：{analysis.Files.Count} 个文件；UAV Raw {analysis.UavRawFileCount}；GSM-19T Raw {analysis.BaseRawFileCount}；派生/已拼接 {analysis.DerivedFileCount}。";
        string fingerprint = analysis.Fingerprint.Length > 16 ? analysis.Fingerprint[..16] : analysis.Fingerprint;
        _fieldDataFingerprintStatus.Text =
            $"项目完整规范数据集：UAV {analysis.UavSamples.Count} 条；Base {analysis.BaseSamples.Count} 条；项目时区作业日 {FieldMagDataService.BuildProjectDateSummaries(analysis).Count}；QC 风险 {analysis.WarningCount}；指纹 {fingerprint}。";
        _toolTip.SetToolTip(_fieldDataFolderStatus, WorkareaRelativePathForFieldUi(analysis.SourceDirectory));
        _toolTip.SetToolTip(_fieldDataFingerprintStatus, analysis.Fingerprint);
        if (_fieldMagRawAnalysis is not null && _fieldSpatialScreening is not null)
        {
            var screening = new WorkareaSpatialService().Screen(_fieldMagRawAnalysis, _fieldSpatialScreening);
            _fieldDataFingerprintStatus.Text += $"\r\n当前工区范围：{_fieldSpatialScreening.Range?.Source ?? "未设置，保留待检查"}；" +
                $"已排除 {_fieldMagRawAnalysis.UavSamples.Count - analysis.UavSamples.Count:N0} 个 UAV 样点，原始资料保留，可在高级参数恢复。" +
                (screening.Warnings.Count > 0 ? " " + string.Join("；", screening.Warnings) : "");
        }

        int errors = analysis.QualityIssues.Count(x => x.Severity.Equals("ERROR", StringComparison.OrdinalIgnoreCase));
        int warnings = analysis.QualityIssues.Count(x => x.Severity.Equals("WARNING", StringComparison.OrdinalIgnoreCase));
        int infos = analysis.QualityIssues.Count(x => x.Severity.Equals("INFO", StringComparison.OrdinalIgnoreCase));
        List<DateTimeOffset> displayStamps = analysis.UavSamples.Select(p => p.TimestampUtc)
            .Concat(analysis.BaseSamples.Select(p => p.TimestampUtc)).OrderBy(p => p).ToList();
        string utcRange = displayStamps.Count == 0 ? "—" : FieldProjectRange(displayStamps[0], displayStamps[^1]);
        IReadOnlyList<FieldMagBeijingDateSummary> dateSummaries =
            FieldMagDataService.BuildProjectDateSummaries(analysis);
        _fieldMagCoverage = FieldMagCoverageService.Analyze(analysis);
        Dictionary<DateTime, FieldMagDailyCoverageSummary> coverageByDate =
            _fieldMagCoverage.ToDictionary(x => x.BeijingDate.Date);
        int flightDates = dateSummaries.Count(x => x.HasFlightData);
        int incompleteCoverageDays = _fieldMagCoverage.Count(x => x.HasUav && !x.IsComplete);
        _fieldDataSummary.Text =
            $"项目识别：UAV Raw {analysis.UavRawFileCount} 文件 / 规范 {analysis.UavSamples.Count} 条；GSM-19T Raw {analysis.BaseRawFileCount} 文件 / 规范 {analysis.BaseSamples.Count} 条；派生成果 {analysis.DerivedFileCount} 文件。\r\n" +
            $"作业时间轴（{ProjectTime.Label(_projectTimeZone)}）：{utcRange}；项目时区作业日 {dateSummaries.Count} 个（其中 UAV 飞行日 {flightDates} 个）。QC：错误 {errors} / 警告 {warnings} / 信息 {infos}。\r\n" +
            $"19T 生产覆盖检查：飞行日 {_fieldMagCoverage.Count} 个；完整 {_fieldMagCoverage.Count(x => x.IsComplete)} 个；不完整 {incompleteCoverageDays} 个。覆盖判定严格使用生产 Base Gate、同 UTC 日和 999 s 最大插值空档。";

        PopulateFieldDateNavigation(dateSummaries);

        _fieldDataFileGrid.DataSource = null;
        _fieldDataFileGrid.DataSource = analysis.Files
            .Select(row => new FieldMagFileGridRow(
                FieldRelativePath(analysis.SourceDirectory, row.Path),
                FieldMagDataService.SensorLabel(row.SensorFamily),
                FieldMagDataService.RoleLabel(row.DataRole),
                row.RecordCount,
                FieldProjectRange(row.FirstTimeUtc, row.LastTimeUtc),
                row.Evidence))
            .ToList();
        GridHeaders.Apply(_fieldDataFileGrid, new Dictionary<string, string>
        {
            [nameof(FieldMagFileGridRow.File)] = "文件",
            [nameof(FieldMagFileGridRow.Sensor)] = "传感器",
            [nameof(FieldMagFileGridRow.Role)] = "角色",
            [nameof(FieldMagFileGridRow.Records)] = "记录",
            [nameof(FieldMagFileGridRow.UtcRange)] = "作业时间（项目时区）",
            [nameof(FieldMagFileGridRow.Evidence)] = "识别依据"
        });
        UiLayout.ApplyFillWeights(_fieldDataFileGrid, new Dictionary<string, float>
        {
            [nameof(FieldMagFileGridRow.File)] = 180,
            [nameof(FieldMagFileGridRow.Sensor)] = 86,
            [nameof(FieldMagFileGridRow.Role)] = 92,
            [nameof(FieldMagFileGridRow.Records)] = 62,
            [nameof(FieldMagFileGridRow.UtcRange)] = 155,
            [nameof(FieldMagFileGridRow.Evidence)] = 220
        }, minimumWidth: 55);

        _fieldDateGrid.DataSource = null;
        _fieldDateGrid.DataSource = dateSummaries
            .OrderByDescending(x => x.BeijingDate)
            .Select(x =>
            {
                coverageByDate.TryGetValue(x.BeijingDate.Date, out FieldMagDailyCoverageSummary? coverage);
                return new FieldDateGridRow(
                    x.BeijingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    x.UavFileCount,
                    x.UavRecordCount,
                    x.BaseFileCount,
                    x.BaseRecordCount,
                    coverage is null || !coverage.HasUav
                        ? "—"
                        : coverage.IsComplete
                            ? "100% ✓"
                            : $"{coverage.CoveragePercent:0.0}% · 缺 {coverage.UncoveredUavRecords:N0}",
                    FieldProjectRange(x.FirstTimeUtc, x.LastTimeUtc),
                    x.FlaggedRecordCount);
            })
            .ToList();
        GridHeaders.Apply(_fieldDateGrid, new Dictionary<string, string>
        {
            [nameof(FieldDateGridRow.UtcDate)] = "项目时区日期",
            [nameof(FieldDateGridRow.UavFiles)] = "UAV 文件",
            [nameof(FieldDateGridRow.UavRecords)] = "UAV 记录",
            [nameof(FieldDateGridRow.BaseFiles)] = "Base 文件",
            [nameof(FieldDateGridRow.BaseRecords)] = "Base 记录",
            [nameof(FieldDateGridRow.BaseCoverage)] = "19T覆盖",
            [nameof(FieldDateGridRow.UtcRange)] = "项目时区范围",
            [nameof(FieldDateGridRow.FlaggedRecords)] = "风险记录"
        });
        UiLayout.ApplyFillWeights(_fieldDateGrid, new Dictionary<string, float>
        {
            [nameof(FieldDateGridRow.UtcDate)] = 85,
            [nameof(FieldDateGridRow.UavFiles)] = 65,
            [nameof(FieldDateGridRow.UavRecords)] = 75,
            [nameof(FieldDateGridRow.BaseFiles)] = 65,
            [nameof(FieldDateGridRow.BaseRecords)] = 75,
            [nameof(FieldDateGridRow.BaseCoverage)] = 105,
            [nameof(FieldDateGridRow.UtcRange)] = 160,
            [nameof(FieldDateGridRow.FlaggedRecords)] = 72
        }, minimumWidth: 55);

        _fieldDataQcGrid.DataSource = null;
        _fieldDataQcGrid.DataSource = analysis.QualityIssues
            .Select(row => new FieldDataQcGridRow(row.Severity, row.Code, row.Scope, row.TimeRangeUtc, row.Message))
            .ToList();
        GridHeaders.Apply(_fieldDataQcGrid, new Dictionary<string, string>
        {
            [nameof(FieldDataQcGridRow.Severity)] = "级别",
            [nameof(FieldDataQcGridRow.Code)] = "代码",
            [nameof(FieldDataQcGridRow.Scope)] = "范围",
            [nameof(FieldDataQcGridRow.UtcRange)] = "UTC 范围",
            [nameof(FieldDataQcGridRow.Message)] = "说明"
        });
        UiLayout.ApplyFillWeights(_fieldDataQcGrid, new Dictionary<string, float>
        {
            [nameof(FieldDataQcGridRow.Severity)] = 65,
            [nameof(FieldDataQcGridRow.Code)] = 110,
            [nameof(FieldDataQcGridRow.Scope)] = 70,
            [nameof(FieldDataQcGridRow.UtcRange)] = 150,
            [nameof(FieldDataQcGridRow.Message)] = 300
        }, minimumWidth: 55);

        FieldSourceSummary gsm = FieldMagDataService.ToBaseSourceSummary(analysis);
        FieldSourceSummary uav = FieldMagDataService.ToUavSourceSummary(analysis);
        DiurnalBaseValiditySummary baseValidity =
            DiurnalBaseValidityGate.Evaluate(analysis.BaseSamples);
        _fieldGsm19TStatus.Text =
            $"GSM-19T：规范 {gsm.SampleCount} 条；生产可用 {baseValidity.AcceptedRecords} / 排除 {baseValidity.RejectedRecords}；Raw {analysis.BaseRawFileCount} 文件" + FieldTimeSuffix(gsm.FirstTime, gsm.LastTime);
        _fieldUavMagStatus.Text =
            $"无人机磁测：规范 {uav.SampleCount} 条 / {uav.TrackPointCount} 个有效航迹点；Raw {analysis.UavRawFileCount} 文件" + FieldTimeSuffix(uav.FirstTime, uav.LastTime);
        _fieldDiurnalDatumConfirmed.Checked = false;
        if (_fieldDateSelector.SelectedItem is FieldDateChoice selectedCoverageDate)
            RefreshFieldCoveragePreview(selectedCoverageDate.UtcDate);
        else
            RefreshFieldCoveragePreview(null);
    }

    private void ExportCanonicalFieldData()
    {
        if (_fieldMagDataAnalysis is null)
        {
            EnsureDefaultFieldDataFolder();
            AnalyzeFieldDataFolder(
                force: true,
                quietMissing: false);
        }

        FieldMagFolderAnalysis analysis =
            _fieldMagDataAnalysis
            ?? throw new InvalidOperationException(
                "请先扫描项目外业数据目录。 ");

        string directory =
            EnsureManagedOutputDirectory(
                "field_data_integration",
                "生成完整磁测整合数据");
        WorkspaceRecord project = _workspace.GetWorkspace(Root);
        FieldMagExportResult result =
            _fieldMagDataService
                .WriteIntegratedCanonicalOutputs(
                    directory,
                    analysis,
                    new GeneratedArtifactContext(
                        project.ProjectId,
                        ResolveManagedWorkareaId(CurrentWorkareaRelativePath)));

        RegisterGeneratedBusinessFiles(
            result.Files,
            "field_data_integration");
        FileInteractions.For(this, "field_data_integration", "output").OutputSucceeded(Path.GetDirectoryName(result.ManifestPath)!, directory: true);

        TransientNotice.Show(
            this,
            $"完整整合数据已生成：UAV {analysis.UavSamples.Count} 条；GSM-19T {analysis.BaseSamples.Count} 条。完整表已使用 SourceId + GZip，无按日期拆分、无降采样。",
            "磁测整合完成");
    }

    private void ShowDiurnalBaseStatistics()
    {
        if (_fieldMagDataAnalysis is null)
        {
            EnsureDefaultFieldDataFolder();
            AnalyzeFieldDataFolder(
                force: true,
                quietMissing: false);
        }

        FieldMagFolderAnalysis analysis =
            _fieldMagDataAnalysis
            ?? throw new InvalidOperationException(
                "请先扫描项目外业数据目录。 ");
        DiurnalBaseValiditySummary summary =
            DiurnalBaseValidityGate.Evaluate(analysis.BaseSamples);

        string medianText = summary.AcceptedMedianFieldNt.HasValue
            ? summary.AcceptedMedianFieldNt.Value.ToString(
                "0.###",
                CultureInfo.InvariantCulture) + " nT"
            : "不可用";

        MessageBox.Show(
            this,
            "生产门控 Base 统计：" + Environment.NewLine +
            $"总记录：{summary.TotalRecords}" + Environment.NewLine +
            $"可用：{summary.AcceptedRecords}" + Environment.NewLine +
            $"排除：{summary.RejectedRecords}" + Environment.NewLine +
            $"无效 session：{summary.RejectedSessions}" + Environment.NewLine +
            $"有效 Base 中位数：{medianText}" + Environment.NewLine + Environment.NewLine +
            "注意：该中位数仅用于诊断 Base 水平，不再自动填入 Datum。" + Environment.NewLine +
            "默认日变主通道为 rigai = Rover - BaseUsed（Datum=0）；" +
            "T0 属于后续 dTgz 处理参数，与 Datum 分离。",
            "Base 统计",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private void ConfigureMagneticRepeatReplacement()
    {
        MessageBox.Show(this, "旧 LinePass 重测替换已移除。请在审计执行结果中确认 Pass QC 和沿线区间选择；旧规则不会自动迁移。", "审计生产选择", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private sealed record FieldProductionCentralMeridian(
        double Value,
        string Source);

    private FieldProductionCentralMeridian ResolveFieldProductionCentralMeridian(
        WorkspaceRecord projectWorkspace,
        string workareaRelativePath)
    {
        if (projectWorkspace.CentralMeridian.HasValue &&
            IsUsableCentralMeridian(projectWorkspace.CentralMeridian.Value))
        {
            string source = string.IsNullOrWhiteSpace(projectWorkspace.CentralMeridianSource)
                ? "WORKAREA_SPATIAL_REFERENCE"
                : "WORKAREA_SPATIAL_REFERENCE: " + projectWorkspace.CentralMeridianSource;
            return new FieldProductionCentralMeridian(
                projectWorkspace.CentralMeridian.Value,
                source);
        }

        if (TryReadManagedRouteProjectCentralMeridian(
                workareaRelativePath,
                out double routeCentralMeridian,
                out string routeSource))
        {
            return new FieldProductionCentralMeridian(
                routeCentralMeridian,
                routeSource);
        }

        if (TryReadWorkareaCoordinateCentralMeridian(
                workareaRelativePath,
                out double coordinateCentralMeridian,
                out string coordinateSource))
        {
            return new FieldProductionCentralMeridian(
                coordinateCentralMeridian,
                coordinateSource);
        }

        string current = "";
        while (true)
        {
            Dictionary<string, string>? values = PromptDialog.Show(
                this,
                "填写磁测投影中央经线",
                [
                    new PromptField(
                        "cm",
                        "中央经线 °E",
                        current,
                        Required: true)
                ],
                width: 620,
                height: 300,
                note:
                    "当前工区没有可用的项目航线中央经线，也没有可读取的坐标成果中央经线。" +
                    Environment.NewLine +
                    "请输入本工区用于正式磁测 XY/Oasis CSV 的 GK3 中央经线。" +
                    Environment.NewLine +
                    "该值仅用于本次生产处理，不会由外业生产流程反写 project.json；" +
                    "最终仍会与实际 Rover 数据建议中央经线进行一致性检查。");

            if (values is null)
            {
                throw new OperationCanceledException(
                    "已取消中央经线输入，未生成正式磁测成果。");
            }

            string raw = values["cm"].Trim();
            if (TryParseNumberWithUnit(raw, out double manual) &&
                IsUsableCentralMeridian(manual))
            {
                return new FieldProductionCentralMeridian(
                    manual,
                    "MANUAL_FIELD_PROCESSING");
            }

            MessageBox.Show(
                this,
                "中央经线必须是 -180° 到 180° 之间的有限数字。",
                "中央经线无效",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            current = raw;
        }
    }

    private bool TryReadManagedRouteProjectCentralMeridian(
        string workareaRelativePath,
        out double centralMeridian,
        out string source)
    {
        centralMeridian = 0d;
        source = "";

        string directory = ManagedOutputPreview(
            "route_project",
            workareaRelativePath);
        if (!Directory.Exists(directory))
            return false;

        foreach (FileInfo file in new DirectoryInfo(directory)
                     .EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
                     .OrderByDescending(x => x.LastWriteTimeUtc))
        {
            try
            {
                RouteProjectFile project = _routeProjects.Load(file.FullName);
                if (!IsUsableCentralMeridian(project.CentralMeridian))
                    continue;

                centralMeridian = project.CentralMeridian;
                source = "ROUTE_PROJECT: " + file.Name;
                return true;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
            {
                // Skip stale/incompatible historical route projects and keep
                // walking backward to the next usable managed project.
            }
        }

        return false;
    }

    private bool TryReadWorkareaCoordinateCentralMeridian(
        string workareaRelativePath,
        out double centralMeridian,
        out string source)
    {
        centralMeridian = 0d;
        source = "";

        string directory = ManagedOutputPreview(
            "coordinate_export",
            workareaRelativePath);
        if (!Directory.Exists(directory))
            return false;

        foreach (FileInfo file in new DirectoryInfo(directory)
                     .EnumerateFiles("*.csv", SearchOption.AllDirectories)
                     .OrderByDescending(x => x.LastWriteTimeUtc))
        {
            try
            {
                CoordinateProjectSnapshot snapshot =
                    ReadCoordinateProjectCsv(file.FullName);
                if (!snapshot.Parameters.TryGetValue(
                        "中央经线",
                        out string? raw) ||
                    !TryParseNumberWithUnit(raw, out double value) ||
                    !IsUsableCentralMeridian(value))
                {
                    continue;
                }

                centralMeridian = value;
                source = "WORKAREA_COORDINATE_CSV: " + file.Name;
                return true;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or InvalidDataException or FormatException)
            {
                // Coordinate history is a fallback candidate only. Ignore an
                // unreadable/edited result and continue to older workarea CSVs.
            }
        }

        return false;
    }

    private static bool IsUsableCentralMeridian(double value) =>
        double.IsFinite(value) && value is >= -180d and <= 180d;

    private void ExportNativeDiurnalFieldData()
    {
        if (_processingCsvInput is not null)
            throw new InvalidOperationException("当前为中间 CSV 空间分线，请使用“导出空间分线 CSV”；如需磁场改正，请先载入原始数据核验。");
        if (_fieldMagDataAnalysis is null)
        {
            EnsureDefaultFieldDataFolder();
            AnalyzeFieldDataFolder(
                force: true,
                quietMissing: false);
        }

        FieldMagFolderAnalysis analysis =
            _fieldMagDataAnalysis
            ?? throw new InvalidOperationException(
                "请先扫描项目外业数据目录。 ");

        DiurnalBaseValiditySummary baseValidity =
            DiurnalBaseValidityGate.Evaluate(analysis.BaseSamples);
        if (baseValidity.AcceptedRecords == 0)
        {
            throw new InvalidOperationException(
                "当前项目没有通过生产 Base 有效性门控的 GSM-19T 样本，不能生成正式日变数据。请先检查 Base 原始数据与 QC。 ");
        }
        if (_fieldDiurnalDatumNt.Value != 0m &&
            !_fieldDiurnalDatumConfirmed.Checked)
        {
            throw new InvalidOperationException(
                "当前填写了非零 Datum。主日变通道 rigai 始终等于 Rover - BaseUsed；非零 Datum 只用于额外生成 DatumAdjustedNt。请确认确实需要参考总场后勾选“确认非零 Datum”，或把 Datum 改回 0。 ");
        }

        var options =
            new DiurnalCorrectionOptions(
                DatumNt:
                    (double)_fieldDiurnalDatumNt.Value,
                GemLinkTenthsCompatibility:
                    true,
                MaxInterpolationGapSeconds:
                    999d,
                RejectInterpolationAcrossLargeGap:
                    true);

        string outputDirectory =
            EnsureManagedOutputDirectory(
                "processing_diurnal",
                "生成 MagOps 原生日变数据");
        IgrfModelSet igrfModels =
            GetIgrfModelSet();
        string workarea =
            SelectWorkareaRelativePath("生成 MagOps 原生日变数据");
        WorkspaceRecord projectWorkspace =
            _workspace.ReadWorkareaSpatialContext(Root, workarea);
        FieldProductionCentralMeridian projectionCentralMeridian =
            ResolveFieldProductionCentralMeridian(
                projectWorkspace,
                workarea);

        var preLevelOptions =
            new MagneticPreLevelProcessingOptions(
                T0Mode:
                    _fieldProcessingT0Mode.SelectedIndex switch
                    {
                        0 => MagneticT0Mode.AutoGlobal,
                        2 => MagneticT0Mode.ManualFixed,
                        _ => MagneticT0Mode.AutoByUtcDate
                    },
                ManualT0Nt:
                    (double)_fieldProcessingT0Nt.Value,
                ProjectionCentralMeridianDeg:
                    projectionCentralMeridian.Value,
                ProjectionDatum:
                    "CGCS2000",
                ProjectionFalseEastingM:
                    500000d,
                ProjectionFalseNorthingM:
                    0d,
                ProjectionCentralMeridianSource:
                    projectionCentralMeridian.Source);

        FieldRouteSnapshot? segmentationRoute = ResolveFieldOperationsRouteSnapshot();
        AuditProductionRequest auditRequest = new(analysis.Fingerprint, [], [], [], AllowReviewDraft: true)
        { RouteFingerprint = segmentationRoute?.Fingerprint ?? "" };
        FieldMagFolderAnalysis reviewAnalysis = _fieldMagRawAnalysis ?? analysis;
        WorkareaScreeningInput screeningInput = ResolveWorkareaScreeningInput(reviewAnalysis);
        var screened = new WorkareaSpatialService().Screen(reviewAnalysis, screeningInput);
        auditRequest = auditRequest with { SourceFingerprint = reviewAnalysis.Fingerprint,
            SpatialScreening = screeningInput, ScreeningFingerprint = screened.Fingerprint };
        AuditProductionService? reviewSession = null;
        MagOps.Application.ReviewAnalysisSession? calculationSession = null;
        if (_fieldSurveyLineSegmentation.Checked)
        {
            string parameterDefaultsPath = SegmentationParameterStore.DefaultsPath(Root, CurrentWorkareaRelativePath);
            try { auditRequest = auditRequest with { TrackSegmentation = SegmentationParameterStore.LoadDefaults(parameterDefaultsPath) }; }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException)
            { MessageBox.Show(this, "工区分线默认参数未加载，本次使用初始值：" + ex.Message, "分线参数", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            try
            {
                auditRequest = SegmentationReviewStore.LoadLatest(outputDirectory, screeningInput, reviewAnalysis) ?? auditRequest;
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, "已有核验状态无法读取，原文件保留；本次将重新核验：" + ex.Message,
                    "核验状态", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            if (_processingReview is null || !ProcessingReviewIsCurrent())
            {
                BeginProcessingReview();
                SetStatus("请在分线与处理页检查右侧航段，再生成处理用 CSV。");
                return;
            }
            if (!_processingReview.TryPrepareProcessingRequest()) return;
            auditRequest = _processingReview.ReviewedRequest!;
            reviewSession = _processingReview.ReviewSession;
            calculationSession = _processingReview.CalculationSession;
            SegmentationReviewStore.Save(Path.Combine(outputDirectory, SegmentationReviewStore.AutomaticStateName), auditRequest);
            _processingReview.MarkWorkspaceReviewSaved();
            _fieldSpatialScreening = auditRequest.SpatialScreening;
            SaveFieldOperationsSettings();
            _preserveProcessingReview = true;
            try { ApplyFieldDataAnalysis(reviewAnalysis); }
            finally { _preserveProcessingReview = false; }
        }
        else
            auditRequest = auditRequest with { IncludeUnconfirmedSamples = true };

        InvalidateDiurnalQuality();
        FieldProductionWorkflowResult production =
            _fieldProductionWorkflow.Execute(
                new FieldProductionRequest(
                    Root,
                    outputDirectory,
                    reviewAnalysis,
                    options,
                    igrfModels,
                    preLevelOptions,
                    segmentationRoute?.SurveyLines ?? [],
                    new MagneticSurveyLineSegmentationOptions(Enabled: true),
                    segmentationRoute?.SourceLabel ?? "",
                    segmentationRoute?.Fingerprint ?? "",
                    auditRequest,
                    new GeneratedArtifactContext(
                        projectWorkspace.ProjectId,
                        ResolveManagedWorkareaId(workarea))) { ReviewSession = reviewSession, CalculationSession = calculationSession });

        DiurnalCorrectionExportResult result = production.Export;
        _motherLineProductionWorkspace.SetSnapshot(projectWorkspace.ProjectId,
            ResolveManagedWorkareaId(workarea), reviewAnalysis, result);
        var lineNames = (result.AuditProduction.EffectiveTasks ?? segmentationRoute?.SurveyLines ?? [])
            .ToDictionary(t=>t.SurveyTaskStableId,t=>t.LineName);
        var qualityLines = result.AuditProduction.Execution.Passes.SelectMany(p=>p.Samples.Select(s=>
            new {s.SourceFile,s.SourceRecord,Name=lineNames.GetValueOrDefault(p.SurveyTaskStableId,p.SurveyTaskStableId)}))
            .GroupBy(s=>(s.SourceFile,s.SourceRecord)).ToDictionary(g=>g.Key,g=>string.Join(" / ",g.Select(s=>s.Name).Distinct()));
        ApplyDiurnalQuality(reviewAnalysis,result.Correction,"本次计算输入（非最终CSV选择范围）",qualityLines);
        if (production.RegisteredArtifacts.Count > 0 &&
            _artifactWorkspace.HasGridColumns())
        {
            ApplyArtifactChanges(production.RegisteredArtifacts);
        }

        if (result.ProcessingRecordCount == 0)
        {
            MessageBox.Show(this,
                "本次未生成可用于 Oasis 的处理用 CSV。\n\n" + result.ProcessingOutputWarning +
                "\n\n审核记录和 QC 报告已保存到：\n" + Path.GetDirectoryName(result.ManifestPath),
                "没有可导出的处理数据", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (!result.AuditProduction.IsReviewDraft)
        {
            _latestOasisExport = Path.GetFullPath(result.ProcessingCsvPath);
            _oasisInputIsManual = false;
        }

        int totalUncorrected =
            result.Correction.UncorrectedCount +
            result.ExcludedRoverRecords;

        if (_fieldProcessingT0Mode.SelectedIndex == 0 &&
            result.PreLevel.FixedT0Nt.HasValue)
        {
            decimal t0 = Convert.ToDecimal(result.PreLevel.FixedT0Nt.Value);
            t0 = Math.Min(
                _fieldProcessingT0Nt.Maximum,
                Math.Max(_fieldProcessingT0Nt.Minimum, t0));
            _fieldProcessingT0Nt.Value =
                decimal.Round(
                    t0,
                    _fieldProcessingT0Nt.DecimalPlaces);
        }

        string dtgzMean =
            result.PreLevel.DTgzMeanNt.HasValue
                ? result.PreLevel.DTgzMeanNt.Value.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture)
                : "不可用";
        string t0Description =
            result.PreLevel.T0Mode == "AUTO_BY_UTC_DATE"
                ? $"按 UTC 日自动（{result.PreLevel.Days.Count(x => x.T0Nt.HasValue)} 日；范围 {(result.PreLevel.T0MinNt.HasValue ? result.PreLevel.T0MinNt.Value.ToString("0.###", CultureInfo.InvariantCulture) : "不可用")}–{(result.PreLevel.T0MaxNt.HasValue ? result.PreLevel.T0MaxNt.Value.ToString("0.###", CultureInfo.InvariantCulture) : "不可用")} nT）"
                : result.PreLevel.FixedT0Nt.HasValue
                    ? $"{result.PreLevel.FixedT0Nt.Value:0.###} nT（{result.PreLevel.T0Mode}）"
                    : result.PreLevel.T0Mode;

        string lineSegmentationText = $"分线状态：{(result.AuditProduction.IsReviewDraft ? "包含待确认项" : "已核验")}；{result.LineSegmentation.Summary.SurveyLineRecords} 条已归属测线记录。";
        string repeatReplacementText = result.AuditProduction.IsReviewDraft
            ? "需要注意：" + SummarizeAuditWarnings(result.AuditProduction.BlockedReasons)
            : "已按审计 QC Pass 和区间选择映射磁测样本。";
        TransientNotice.Show(
            this,
            $"完成：有效 Base {result.BaseValidity.AcceptedRecords} / 排除 {result.BaseValidity.RejectedRecords}；Exact {result.Correction.ExactCount}；插值 {result.Correction.InterpolatedCount}；未改正/不参与 {totalUncorrected}。{lineSegmentationText}{repeatReplacementText} 主日变通道为 rigai=Rover-BaseUsed；IGRF {result.PreLevel.IgrfRecords} 条；T0 {t0Description}；dTgz 全体均值 {dtgzMean} nT。处理用 CSV {result.ProcessingRecordCount} 条，X=Easting / Y=Northing，中央经线 {(result.PreLevel.ProjectionCentralMeridianDeg.HasValue ? result.PreLevel.ProjectionCentralMeridianDeg.Value.ToString("0.###", CultureInfo.InvariantCulture) + "°E" : "不可用")}；来源 {result.PreLevel.ProjectionCentralMeridianSource}。",
            result.AuditProduction.IsReviewDraft ? "待复核草稿已导出" : "审计生产成果已导出");
        MessageBox.Show(this,
            $"处理用 CSV 已生成，共 {result.ProcessingRecordCount:N0} 行。\n\n文件位置：\n{result.ProcessingCsvPath}" +
            (result.AuditProduction.IsReviewDraft ? "\n\n当前为待复核草稿，请核对质量及未选择区间后用于处理。" : ""),
            "Oasis 处理用 CSV", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static string SummarizeAuditWarnings(IReadOnlyList<string> warnings)
    {
        var messages = new List<string>();
        if (warnings.Any(x => x.StartsWith("AMBIGUOUS_IDENTITY:", StringComparison.Ordinal) ||
                              x.StartsWith("MISSION_TASK_IDENTITY_UNAVAILABLE:", StringComparison.Ordinal)))
            messages.Add("部分测线任务身份需要确认");
        if (warnings.Any(x => x.StartsWith("SOURCE_REVISION_UNCONFIRMED:", StringComparison.Ordinal)))
            messages.Add("轨迹数据版本已变化，旧确认已清除");
        if (warnings.Any(x => x.StartsWith("ROUTE_REVISION_UNCONFIRMED:", StringComparison.Ordinal)))
            messages.Add("航线任务版本已变化，旧确认已清除");
        if (warnings.Any(x => x.StartsWith("UNRESOLVED_LEGS:", StringComparison.Ordinal) ||
                              x.StartsWith("NO_MISSION_WINDOWS:", StringComparison.Ordinal) ||
                              x.StartsWith("MISSION_TASK_AUTHORITY_REQUIRED:", StringComparison.Ordinal)))
            messages.Add("有飞行航段尚未可靠归属");
        if (warnings.Any(x => x.StartsWith("MANUAL_LEG_ASSIGNMENT_UNAVAILABLE:", StringComparison.Ordinal) ||
                              x.StartsWith("MANUAL_LEG_ASSIGNMENT_INSUFFICIENT_TRACK:", StringComparison.Ordinal)))
            messages.Add("有手动归属决定无法应用，需要重新核验");
        if (warnings.Any(x => x.StartsWith("PASS_REVIEW_REQUIRED:", StringComparison.Ordinal)))
            messages.Add("部分测线归属尚未确认");
        if (warnings.Any(x => x.StartsWith("QC_REVIEW_REQUIRED:", StringComparison.Ordinal)))
            messages.Add("部分候选测线质量尚未确认");
        if (warnings.Any(x => x.StartsWith("PRODUCTION_SELECTION_REVIEW:", StringComparison.Ordinal)))
            messages.Add("部分测线覆盖或生产区间仍需复核");
        if (warnings.Any(x => x.StartsWith("USER_INCLUDED_UNCONFIRMED:", StringComparison.Ordinal)))
            messages.Add("用户已选择将未确认轨迹纳入待复核草稿");
        if (warnings.Any(x => x.StartsWith("USER_INCLUDED_UNCONFIRMED_LEG:", StringComparison.Ordinal)))
            messages.Add("用户已逐段选择将未确认航段纳入待复核草稿");
        if (messages.Count == 0 && warnings.Count > 0)
            messages.Add("部分输入或选择需要复核");
        return messages.Count == 0 ? "无" : string.Join("；", messages.Distinct());
    }

    private void ExportGemLinkBridgePackage()
    {
        if (_fieldMagDataAnalysis is null)
        {
            EnsureDefaultFieldDataFolder();
            AnalyzeFieldDataFolder(
                force: true,
                quietMissing: false);
        }

        FieldMagFolderAnalysis analysis =
            _fieldMagDataAnalysis
            ?? throw new InvalidOperationException(
                "请先扫描项目外业数据目录。 ");

        string directory =
            EnsureManagedOutputDirectory(
                "field_gemlink_bridge",
                "导出 GEMLink 黄金对照包");
        WorkspaceRecord project = _workspace.GetWorkspace(Root);
        GemLinkBridgeExportResult result =
            _fieldMagDataService
                .WriteGemLinkBridgePackage(
                    directory,
                    analysis,
                    _fieldOperationsDate.Value.Date,
                    new GeneratedArtifactContext(
                        project.ProjectId,
                        ResolveManagedWorkareaId(CurrentWorkareaRelativePath)));
        RegisterGeneratedBusinessFiles(
            result.Files,
            "field_gemlink_bridge");
        FileInteractions.For(this, "field_gemlink_bridge", "output").OutputSucceeded(Path.GetDirectoryName(result.ManifestPath)!, directory: true);
        TransientNotice.Show(
            this,
            $"GEMLink 对照包已生成：{result.Jobs.Count} 个 UTC 日期；仅用于黄金对照/人工复核。",
            "GEMLink 对照包完成");
    }

    private static string FieldRelativePath(string root, string path)
    {
        try
        {
            string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            return relative.StartsWith("..", StringComparison.Ordinal) ? Path.GetFileName(path) : relative;
        }
        catch
        {
            return Path.GetFileName(path);
        }
    }

    private static string FieldCanonicalUtcRange(FieldMagFolderAnalysis analysis)
    {
        List<DateTimeOffset> stamps = analysis.UavSamples.Select(x => x.TimestampUtc)
            .Concat(analysis.BaseSamples.Select(x => x.TimestampUtc))
            .OrderBy(x => x)
            .ToList();
        return stamps.Count == 0 ? "—" : FieldUtcRange(stamps[0], stamps[^1]);
    }

    private string FieldProjectRange(
        DateTimeOffset? first,
        DateTimeOffset? last)
    {
        if (!first.HasValue && !last.HasValue)
            return "—";
        if (first.HasValue && last.HasValue)
        {
            DateTimeOffset a = ProjectTime.Display(first.Value, _projectTimeZone);
            DateTimeOffset b = ProjectTime.Display(last.Value, _projectTimeZone);
            return $"{a:MM-dd HH:mm:ss} – {b:MM-dd HH:mm:ss}";
        }

        DateTimeOffset value = ProjectTime.Display(first ?? last ?? default, _projectTimeZone);
        return $"{value:MM-dd HH:mm:ss}";
    }

    private static string FieldUtcRange(DateTimeOffset? first, DateTimeOffset? last)
    {
        if (!first.HasValue && !last.HasValue)
            return "—";
        if (first.HasValue && last.HasValue)
            return $"{first.Value.UtcDateTime:MM-dd HH:mm:ss} – {last.Value.UtcDateTime:MM-dd HH:mm:ss}";
        DateTimeOffset value = first ?? last ?? default;
        return $"{value.UtcDateTime:MM-dd HH:mm:ss}";
    }

    private void MarkFieldRecommendationParametersChanged()
    {
        _fieldAutoAnalysisContextKey = "";
        _fieldProjectTimelineContextKey = "";
        SaveFieldOperationsSettings();

        if (_fieldDailyAnalysis is not null)
        {
            _fieldWorkPercentValue.Text =
                _fieldDailyProgressRecord is null
                    ? "—"
                    : FieldworkDocumentExportService.FormatDayWorkPercent(
                        _fieldDailyProgressRecord,
                        (int)_fieldExpectedDailySorties.Value);
            RefreshFieldProgressReportPreview();
            SetStatus(
                "预计每日架次已改变；工作百分比已按当前目标刷新。点击“重新计算所选日”更新任务进度。 ");
        }
    }

    private void PopulateFieldDateNavigation(IReadOnlyList<FieldMagBeijingDateSummary> dates)
    {
        DateTime? previous = _fieldDateSelector.SelectedItem is FieldDateChoice current
            ? current.UtcDate
            : null;
        DateTime? preferred = previous.HasValue && dates.Any(x => x.BeijingDate == previous.Value.Date)
            ? previous.Value.Date
            : dates.Where(x => x.HasFlightData)
                .Select(x => (DateTime?)x.BeijingDate)
                .LastOrDefault()
                ?? dates.Select(x => (DateTime?)x.BeijingDate).LastOrDefault();

        _updatingFieldDateSelection = true;
        try
        {
            _fieldDateSelector.DataSource = null;
            if (dates.Count == 0)
            {
                _fieldDateSelector.Items.Clear();
                _fieldDateSelector.Items.Add("未识别到数据日期");
                _fieldDateSelector.SelectedIndex = 0;
                _fieldDateSelector.Enabled = false;
                return;
            }

            List<FieldDateChoice> choices = dates
                .OrderByDescending(x => x.BeijingDate)
                .Select(x => new FieldDateChoice(
                    x.BeijingDate,
                    $"{x.BeijingDate:yyyy-MM-dd}  ·  UAV {x.UavRecordCount:N0}  ·  Base {x.BaseRecordCount:N0}"))
                .ToList();
            _fieldDateSelector.DisplayMember = nameof(FieldDateChoice.Display);
            _fieldDateSelector.DataSource = choices;
            _fieldDateSelector.Enabled = true;

            int index = preferred.HasValue
                ? choices.FindIndex(x => x.UtcDate == preferred.Value.Date)
                : 0;
            _fieldDateSelector.SelectedIndex = index >= 0 ? index : 0;
            if (_fieldDateSelector.SelectedItem is FieldDateChoice selected)
                _fieldOperationsDate.Value = selected.UtcDate;
        }
        finally
        {
            _updatingFieldDateSelection = false;
        }
    }

    private void SelectFieldDate(DateTime date, bool analyze)
    {
        date = date.Date;
        _updatingFieldDateSelection = true;
        try
        {
            if (_fieldDateSelector.DataSource is List<FieldDateChoice> choices)
            {
                int index = choices.FindIndex(x => x.UtcDate == date);
                if (index >= 0)
                    _fieldDateSelector.SelectedIndex = index;
            }
            _fieldOperationsDate.Value = date;
            SelectFieldDateGridRow(date);
        }
        finally
        {
            _updatingFieldDateSelection = false;
        }

        _fieldAutoAnalysisContextKey = "";
        if (analyze)
            AnalyzeFieldOperations(force: true, quietNoRoute: true);
        RefreshFieldBaseStationContext();
        RefreshFieldCoveragePreview(date);
    }

    private void RefreshFieldCoveragePreview(DateTime? beijingDate)
    {
        FieldMagDailyCoverageSummary? summary = beijingDate.HasValue
            ? _fieldMagCoverage.FirstOrDefault(x => x.BeijingDate.Date == beijingDate.Value.Date)
            : null;
        _fieldCoveragePreview.Summary = summary;

        if (summary is null || !summary.HasUav)
        {
            _fieldCoverageStatus.Text =
                "19T 覆盖：所选作业日暂无可检查的 UAV 磁测记录。";
            _toolTip.SetToolTip(_fieldCoverageStatus, "");
            return;
        }

        if (summary.IsComplete)
        {
            _fieldCoverageStatus.Text =
                $"19T 覆盖：{summary.CoveragePercent:0.0}%（UAV {summary.CoveredUavRecords:N0}/{summary.UavEligibleRecords:N0}）；生产 Base 有效覆盖完整。";
        }
        else
        {
            string window = summary.FirstUncoveredUtc.HasValue && summary.LastUncoveredUtc.HasValue
                ? $"；缺口窗口 {ProjectTime.Display(summary.FirstUncoveredUtc.Value, _projectTimeZone):HH:mm:ss}–{ProjectTime.Display(summary.LastUncoveredUtc.Value, _projectTimeZone):HH:mm:ss}"
                : "";
            string largest = summary.LargestUncoveredSpanSeconds > 0d
                ? $"；最大连续缺口 {FormatCoverageDuration(summary.LargestUncoveredSpanSeconds)}"
                : "";
            _fieldCoverageStatus.Text =
                $"19T 覆盖：{summary.CoveragePercent:0.0}%（缺 {summary.UncoveredUavRecords:N0}/{summary.UavEligibleRecords:N0} 条，{summary.UncoveredSpanCount} 段{window}{largest}）。红色 UAV 段不会生成生产 rigai/dTgz。";
        }

        string reasons = summary.Reasons.Count == 0
            ? "无"
            : string.Join(", ", summary.Reasons);
        _toolTip.SetToolTip(
            _fieldCoverageStatus,
            $"项目时区作业日：{summary.BeijingDate:yyyy-MM-dd}\r\n" +
            $"UAV 可用记录：{summary.UavEligibleRecords:N0}；可生产日变：{summary.CoveredUavRecords:N0}；缺失：{summary.UncoveredUavRecords:N0}\r\n" +
            $"19T 当日记录：{summary.BaseRecords:N0}；生产可用：{summary.AcceptedBaseRecords:N0}；门控排除：{summary.RejectedBaseRecords:N0}\r\n" +
            $"缺失原因：{reasons}\r\n" +
            "覆盖规则与生产日变一致：Base 必须通过 Validity Gate、与 Rover 同 UTC 日、0.1 秒兼容键无重复，且插值端点间隔不得超过 999 秒。");
    }

    private string BuildCoverageImportDetail(FieldDataImportBatchResult imported)
    {
        HashSet<string> importedPaths = imported.Items
            .Where(x => x.Imported)
            .Select(x => Path.GetFullPath(x.DestinationPath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        HashSet<DateTime> targetDates = new();
        if (_fieldMagDataAnalysis is not null && importedPaths.Count > 0)
        {
            foreach (UavMagCanonicalSample row in _fieldMagDataAnalysis.UavSamples)
            {
                if (importedPaths.Contains(Path.GetFullPath(row.SourceFile)))
                    targetDates.Add(ProjectTime.Date(row.TimestampUtc, _projectTimeZone));
            }
            foreach (Gsm19TCanonicalSample row in _fieldMagDataAnalysis.BaseSamples)
            {
                if (importedPaths.Contains(Path.GetFullPath(row.SourceFile)))
                    targetDates.Add(ProjectTime.Date(row.TimestampUtc, _projectTimeZone));
            }
        }

        FieldMagDailyCoverageSummary? latest = _fieldMagCoverage
            .Where(x => x.HasUav && (targetDates.Count == 0 || targetDates.Contains(x.BeijingDate.Date)))
            .OrderBy(x => x.BeijingDate)
            .LastOrDefault()
            ?? _fieldMagCoverage
                .Where(x => x.HasUav)
                .OrderBy(x => x.BeijingDate)
                .LastOrDefault();
        if (latest is null)
            return "";

        return latest.IsComplete
            ? $"19T覆盖检查 {latest.BeijingDate:yyyy-MM-dd}：100%，UAV {latest.UavEligibleRecords:N0} 条均具备生产日变覆盖。"
            : $"19T覆盖检查 {latest.BeijingDate:yyyy-MM-dd}：{latest.CoveragePercent:0.0}%，缺 {latest.UncoveredUavRecords:N0}/{latest.UavEligibleRecords:N0} 条；请在“数据识别与质量”的覆盖预览中检查红色时段。";
    }

    private static string FormatCoverageDuration(double seconds)
    {
        TimeSpan value = TimeSpan.FromSeconds(Math.Max(0d, seconds));
        if (value.TotalHours >= 1d)
            return $"{(int)value.TotalHours}h {value.Minutes}m";
        if (value.TotalMinutes >= 1d)
            return $"{(int)value.TotalMinutes}m {value.Seconds}s";
        return $"{value.TotalSeconds:0.#}s";
    }

    private void SelectFieldDateGridRow(DateTime date)
    {
        if (_fieldDateGrid.Rows.Count == 0)
            return;

        _updatingFieldDateSelection = true;
        try
        {
            foreach (DataGridViewRow gridRow in _fieldDateGrid.Rows)
            {
                if (gridRow.DataBoundItem is not FieldDateGridRow row ||
                    !DateTime.TryParseExact(
                        row.UtcDate,
                        "yyyy-MM-dd",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out DateTime rowDate) ||
                    rowDate.Date != date.Date)
                    continue;

                gridRow.Selected = true;
                if (gridRow.Cells.Count > 0)
                    _fieldDateGrid.CurrentCell = gridRow.Cells[0];
                break;
            }
        }
        finally
        {
            _updatingFieldDateSelection = false;
        }
    }

    private void AnalyzeProjectFieldTimeline(bool force, bool quietNoRoute)
    {
        if (_fieldMagDataAnalysis is null)
        {
            AnalyzeFieldOperations(force, quietNoRoute);
            return;
        }

        FieldRouteSnapshot? route = ResolveFieldProgressRouteSnapshot();
        if (route is null)
        {
            AnalyzeFieldOperations(force, quietNoRoute);
            return;
        }

        List<DateTime> flightDates = FieldMagDataService.BuildProjectDateSummaries(_fieldMagDataAnalysis)
            .Where(x => x.HasFlightData)
            .Select(x => x.BeijingDate.Date)
            .OrderBy(x => x)
            .ToList();

        if (flightDates.Count == 0)
        {
            _fieldDailyAnalysis = null;
            _fieldDailyProgressRecord = null;
            _fieldProgressGrid.DataSource = null;
            _fieldTakeoffRows.Clear();
            _fieldTakeoffGrid.DataSource = null;
            _fieldOperationsPreview.Analysis = null;
            _fieldOperationsPreview.Invalidate();
            _fieldPlannedValue.Text =
                route.Sorties.Count.ToString(CultureInfo.InvariantCulture);
            _fieldTodayValue.Text = "0";
            _fieldCumulativeValue.Text = "0";
            _fieldRemainingValue.Text = route.Sorties.Count.ToString(CultureInfo.InvariantCulture);
            _fieldWorkPercentValue.Text = "0%";
            _fieldTotalPercentValue.Text = "0%";
            _fieldReportSummary.Text =
                "项目进度：尚未识别到可用于进度计算的 UAV 飞行日。\r\n" +
                "后续安排：请在“数据识别与质量”核查 UAV 时间与 GNSS 航迹。\r\n" +
                "统计口径：项目时区。";
            return;
        }

        string contextKey = string.Join("|",
            Path.GetFullPath(Root),
            CurrentWorkareaRelativePath,
            route.Fingerprint,
            _fieldMagDataAnalysis.Fingerprint,
            _fieldExpectedDailySorties.Value.ToString(CultureInfo.InvariantCulture),
            _fieldAcceptedRelocations.Value.ToString(CultureInfo.InvariantCulture));

        DateTime selectedDate = _fieldDateSelector.SelectedItem is FieldDateChoice selected
            ? selected.UtcDate.Date
            : flightDates[^1];

        if (!force &&
            contextKey.Equals(_fieldProjectTimelineContextKey, StringComparison.Ordinal))
        {
            SelectFieldDate(selectedDate, analyze: true);
            return;
        }

        _updatingFieldDateSelection = true;
        try
        {
            foreach (DateTime date in flightDates)
            {
                _fieldOperationsDate.Value = date;
                _fieldAutoAnalysisContextKey = "";
                AnalyzeFieldOperations(force: true, quietNoRoute: quietNoRoute);
            }

            _fieldOperationsDate.Value = selectedDate;
        }
        finally
        {
            _updatingFieldDateSelection = false;
        }

        _fieldProjectTimelineContextKey = contextKey;
        SelectFieldDate(selectedDate, analyze: true);
        SetStatus(
            $"项目外业进度已按 {flightDates.Count} 个项目时区 UAV 飞行日整理；当前查看 {selectedDate:yyyy-MM-dd}。 ");
    }

    private void AnalyzeFieldOperations(bool force, bool quietNoRoute)
    {
        if (!IsProjectInitialized())
        {
            if (quietNoRoute)
            {
                _fieldRouteSourceStatus.Text = "设计航线：尚未初始化项目。";
                return;
            }
            EnsureInitialized();
        }

        EnsureRememberedWorkareaContextLoaded();
        if (string.IsNullOrWhiteSpace(CurrentWorkareaRelativePath))
        {
            if (quietNoRoute)
            {
                _fieldRouteSourceStatus.Text = "设计航线：请先在顶部选择当前工区。";
                return;
            }
            SelectWorkareaRelativePath("分析外业任务进度");
        }

        WorkareaRecord? workarea = _workspace.GetWorkareas(Root)
            .FirstOrDefault(x => x.Path.Equals(CurrentWorkareaRelativePath, StringComparison.OrdinalIgnoreCase));
        string workareaName = workarea?.DisplayName ?? "";
        if (string.IsNullOrWhiteSpace(workareaName))
            workareaName = Path.GetFileName(CurrentWorkareaRelativePath.Replace('/', Path.DirectorySeparatorChar));

        FieldRouteSnapshot? route = ResolveFieldProgressRouteSnapshot();
        if (route is null)
        {
            _fieldDailyAnalysis = null;
            _fieldDailyProgressRecord = null;
            _fieldRouteSourceStatus.Text = "设计航线：当前工区未发现可用的航线编辑工程或设计航线 KML。";
            _fieldProgressGrid.DataSource = null;
            _fieldTakeoffRows.Clear();
            _fieldOperationsPreview.Analysis = null;
            _fieldOperationsPreview.Invalidate();
            if (quietNoRoute)
            {
                _fieldReportSummary.Text =
                    "项目进度：尚无可对照的设计航线。\r\n" +
                    "后续安排：可在“绘制航线”生成/保存航线，或在本页加入 / 替换航线 KML。\r\n" +
                    "统计口径：项目时区。";
                return;
            }
            throw new InvalidOperationException("当前工区未发现可用于外业进度分析的设计航线。请先完成“绘制航线”并保存设计成果，或在“外业管理”直接导入航线 KML。 ");
        }

        DateTime date = _fieldOperationsDate.Value.Date;
        FieldMagFolderAnalysis? preparedForDate = _fieldMagDataAnalysis is null
            ? null
            : FieldMagDataService.SliceByProjectDate(_fieldMagDataAnalysis, date);

        string contextKey = string.Join("|",
            Path.GetFullPath(Root),
            CurrentWorkareaRelativePath,
            date.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
            route.Fingerprint,
            _fieldMagDataAnalysis?.Fingerprint ?? "",
            _projectTimeZone.Id,
            _fieldExpectedDailySorties.Value.ToString(CultureInfo.InvariantCulture),
            _fieldAcceptedRelocations.Value.ToString(CultureInfo.InvariantCulture),
            string.Join(";", _fieldManualGsm19TFiles),
            string.Join(";", _fieldManualUavFiles));
        if (!force && contextKey.Equals(_fieldAutoAnalysisContextKey, StringComparison.Ordinal))
            return;

        string workareaRoot = Path.Combine(
            Root,
            CurrentWorkareaRelativePath.Replace('/', Path.DirectorySeparatorChar));
        _fieldExportAnalysisDependencies = null;
        var exportSources = CaptureFieldAnalysisSources(route, date, workareaName);
        FieldDailyAnalysisResult analysis = _fieldOperationsAnalysis.Analyze(
            workareaRoot,
            date,
            route,
            _fieldwork.ListFieldDailyProgress(Root)
                .Where(row => row.Workarea.Equals(workareaName, StringComparison.OrdinalIgnoreCase)),
            (int)_fieldExpectedDailySorties.Value,
            (int)_fieldAcceptedRelocations.Value,
            _fieldManualGsm19TFiles,
            _fieldManualUavFiles,
            preparedData: preparedForDate);

        FieldDailyProgressRecord record = _fieldOperationsAnalysis.ToRecord(
            analysis,
            workareaName,
            (int)_fieldExpectedDailySorties.Value,
            (int)_fieldAcceptedRelocations.Value);

        DjiDailyFlightSummary djiSummary =
            _djiFlightRecords.BuildDailySummary(
                Root,
                CurrentWorkareaRelativePath,
                date,
                preparedForDate);
        ApplyDjiDailySummaryToRecord(
            record,
            djiSummary);

        if (_fieldMagDataAnalysis is not null)
        {
            FieldMagFolderAnalysis dailyData = preparedForDate
                ?? FieldMagDataService.SliceByProjectDate(_fieldMagDataAnalysis, date);
            record.FieldDataFolder = _fieldMagDataAnalysis.SourceDirectory;
            record.FieldDataFingerprint = dailyData.Fingerprint;
            record.UavCanonicalSampleCount = dailyData.UavSamples.Count;
            record.BaseCanonicalSampleCount = dailyData.BaseSamples.Count;
            record.FieldDataQualityIssueCount = dailyData.QualityIssues.Count;
            record.GemLinkUtcDates = dailyData.GemLinkJobs
                .Select(x => x.UtcDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .ToList();
        }
        var sourceStatus = ArtifactDependencies.Check(Root, exportSources);
        if (sourceStatus.Status != "CURRENT")
            throw new InvalidOperationException("分析输入已变化，请重新扫描：" + sourceStatus.Reason);
        // A scoped preview must not overwrite the authoritative full-workarea daily record.
        bool previewOnly = _fieldRouteScope.SelectedIndex == 1 || route.IsPartialWorkspace;
        _fieldDailyProgressRecord = previewOnly ? record : _fieldwork.SaveFieldDailyProgress(Root, record);
        _fieldDailyAnalysis = analysis;
        _fieldExportAnalysisDependencies = exportSources;
        _fieldExportAnalysisContext = Path.GetFullPath(Root) + "|" + CurrentWorkareaRelativePath;
        _fieldAutoAnalysisContextKey = contextKey;
        ApplyDjiFlightStatus(djiSummary);
        ApplyFieldOperationsAnalysis(analysis);
        if (!previewOnly) RefreshProject();
        SetStatus(previewOnly ? "当前范围仅预览，不覆盖整个工区的每日进度记录。" :
            analysis.SurveyLineMatchingAvailable
                ? $"外业进度已整理（项目时区）：{analysis.Date:yyyy-MM-dd} 当日完成测线 {analysis.TodayCompletedSurveyLineKm:0.00} km，" +
                  $"累计 {analysis.CompletedSurveyLineKm:0.00}/{analysis.PlannedSurveyLineKm:0.00} km；完成架次 {analysis.CompletedCumulative.Count}/{analysis.Route.Sorties.Count}。"
                : $"外业进度已整理（兼容模式）：{analysis.Date:yyyy-MM-dd} 完成 {analysis.CompletedToday.Count}，累计 {analysis.CompletedCumulative.Count}，剩余 {analysis.RemainingSorties.Count} 架次。");
    }

    private FieldRouteSnapshot? ResolveFieldProgressRouteSnapshot()
    {
        if (_fieldRouteScope.SelectedIndex == 1)
            return _routePlan is { Sorties.Count: > 0 } active
                ? _fieldOperationsAnalysis.FromRoutePlan(active, $"当前子区 {active.PartitionCode}") : null;
        if (_routePartitionContexts.Count > 0)
        {
            var plans = _routePartitionContexts.Values.OrderBy(c => c.Index).Where(c => c.Plan is { Sorties.Count: > 0 })
                .Select(c => c.Plan!).ToArray();
            if (plans.Length > 0)
                return _fieldOperationsAnalysis.FromRoutePlans(plans,
                    _routePartitionContexts.Values.First().Child.ManifestPath,
                    $"整个工区（已生成 {plans.Length}/{_routePartitionContexts.Count} 个子区）") with
                { IsPartialWorkspace = plans.Length != _routePartitionContexts.Count };
            // A pending-only workspace must not fall back to an older generated baseline.
            return null;
        }
        return ResolveFieldOperationsRouteSnapshot();
    }

    private FieldRouteSnapshot? ResolveFieldOperationsRouteSnapshot()
    {
        // Progress accounting and magnetic segmentation both need the richest
        // task identity available. Formal RouteSortie.SourceLines always wins.
        FieldRouteSnapshot? currentPlan = null;
        if (_routePlan is { Sorties.Count: > 0 })
        {
            currentPlan =
                _fieldOperationsAnalysis.FromRoutePlan(
                    _routePlan);
            if (currentPlan.HasSurveyLineIdentity)
                return currentPlan;
        }

        FieldRouteSnapshot? managed = null;
        if (!string.IsNullOrWhiteSpace(
                CurrentWorkareaRelativePath))
        {
            string projectDirectory =
                ManagedOutputPreview(
                    "route_project",
                    CurrentWorkareaRelativePath);
            string exportDirectory =
                ManagedOutputPreview(
                    "route_export",
                    CurrentWorkareaRelativePath);
            string importDirectory =
                ManagedOutputPreview(
                    "route_kml_import",
                    CurrentWorkareaRelativePath);
            managed =
                _fieldOperationsAnalysis.TryLoadManagedRouteSnapshot(
                    projectDirectory,
                    exportDirectory,
                    importDirectory);
            if (managed is { HasSurveyLineIdentity: true })
                return managed;
        }

        // If task progress has already resolved a design-route snapshot, reuse
        // that exact route before looking for another compatibility source. The
        // persisted daily-progress JSON stores task IDs/statistics but not the
        // full line geometry, so the in-memory Route snapshot is the useful
        // source here.
        FieldRouteSnapshot? progressRoute =
            _fieldDailyAnalysis?.Route;
        if (progressRoute is { HasSurveyLineIdentity: true })
            return progressRoute;

        FieldRouteSnapshot? preferredImport = null;
        if (!string.IsNullOrWhiteSpace(
                _fieldPreferredRouteImportDirectory))
        {
            preferredImport =
                _fieldOperationsAnalysis.TryLoadImportedRouteKmlSnapshot(
                    _fieldPreferredRouteImportDirectory);
            if (preferredImport is null)
                _fieldPreferredRouteImportDirectory = "";
            else if (preferredImport.HasSurveyLineIdentity)
                return preferredImport;
        }

        // Compatibility fallback: the field-progress page may only have sortie
        // polylines (for example the 76 externally imported KML tasks visible in
        // the progress preview). Recover survey legs from those design-route
        // geometries conservatively. Ambiguous geometry is rejected instead of
        // silently inventing production lines.
        FieldRouteSnapshot?[] compatibilityCandidates =
        [
            preferredImport,
            progressRoute,
            managed,
            currentPlan
        ];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (FieldRouteSnapshot candidate in compatibilityCandidates
            .Where(x => x is not null)
            .Cast<FieldRouteSnapshot>())
        {
            string key = candidate.Fingerprint + "|" + candidate.SourcePath;
            if (!seen.Add(key))
                continue;

            SurveyLineIdentityRecoveryResult recovered =
                SurveyLineIdentityRecoveryService.Recover(candidate);
            if (recovered.Recovered &&
                recovered.Route.HasSurveyLineIdentity)
            {
                return recovered.Route;
            }
        }

        return preferredImport ?? progressRoute ?? currentPlan ?? managed;
    }

    private void SelectFieldProgressSortieFromPreview(
        string sortieName)
    {
        if (string.IsNullOrWhiteSpace(sortieName))
            return;

        DataGridViewColumn? sortieColumn =
            _fieldProgressGrid.Columns
                .Cast<DataGridViewColumn>()
                .FirstOrDefault(x =>
                    x.DataPropertyName.Equals(
                        nameof(FieldSortieProgressGridRow.SortieName),
                        StringComparison.OrdinalIgnoreCase));

        DataGridViewRow? row =
            _fieldProgressGrid.Rows
                .Cast<DataGridViewRow>()
                .FirstOrDefault(x =>
                    x.DataBoundItem is
                        FieldSortieProgressGridRow item &&
                    item.SortieName.Equals(
                        sortieName,
                        StringComparison.OrdinalIgnoreCase));
        if (row is null)
            return;

        _fieldProgressGrid.ClearSelection();
        row.Selected = true;
        if (sortieColumn is not null)
        {
            _fieldProgressGrid.CurrentCell =
                row.Cells[sortieColumn.Index];
        }
        else if (row.Cells.Count > 0)
        {
            _fieldProgressGrid.CurrentCell =
                row.Cells[0];
        }
    }

    private void ClearFieldProgressSortieSelectionFromPreview()
    {
        _fieldProgressGrid.ClearSelection();
        _fieldProgressGrid.CurrentCell = null;
        _fieldOperationsPreview.SelectedSortieName = "";
    }

    private void UpdateFieldPreviewVisibleServiceSorties()
    {
        _fieldTakeoffGrid.EndEdit();
        _fieldOperationsPreview.SetVisibleServiceRecommendationIds(
            _fieldTakeoffRows
                .Where(x => x.Selected)
                .Select(x => x.RecommendationId));
    }

    private void UpdateFieldPreviewSelectedSortie()
    {
        string selected = "";
        if (_fieldProgressGrid.CurrentRow?.DataBoundItem is
            FieldSortieProgressGridRow row)
        {
            selected = row.SortieName;
        }

        _fieldOperationsPreview.SelectedSortieName =
            selected;
    }

    private static void ApplyDjiDailySummaryToRecord(
        FieldDailyProgressRecord record,
        DjiDailyFlightSummary summary)
    {
        record.DjiFlightCount =
            summary.FlightCount;
        record.DjiFlightDistanceKm =
            summary.TotalDistanceKm;
        record.DjiFlightDurationSeconds =
            summary.TotalDurationSeconds;
        record.DjiMagMatchedFlightCount =
            summary.MagneticMatchedFlightCount;
        record.DjiFlightTimeRangeBeijing =
            summary.BeijingTimeRange;
        record.DjiFlightSessionIds =
            summary.Sessions
                .Select(x => x.SessionId)
                .ToList();
    }

    private void ApplyDjiFlightStatus(
        DjiDailyFlightSummary summary)
    {
        if (summary.FlightCount == 0)
        {
            _fieldDjiFlightStatus.Text =
                "DJI 飞行记录：所选项目时区作业日未导入 FlightRecord；不影响磁测测线完成判定。";
            _toolTip.SetToolTip(
                _fieldDjiFlightStatus,
                "可在“数据识别与质量”中导入 DJI Pilot 2 FlightRecord 文件夹。");
            return;
        }

        _fieldDjiFlightStatus.Text =
            $"DJI 飞行记录：{summary.FlightCount} 次 / {summary.TotalDistanceKm:0.00} km / " +
            $"{summary.BeijingTimeRange}（项目时区）；磁测时间轴对应 " +
            $"{summary.MagneticMatchedFlightCount}/{summary.FlightCount} 次。";
        _toolTip.SetToolTip(
            _fieldDjiFlightStatus,
            string.Join(
                Environment.NewLine,
                summary.Sessions.Select(x =>
                    $"{x.SourceFileName} | {x.InternalStartUtc} | {x.DurationSeconds / 60d:0.0} min | {x.TotalDistanceKm:0.00} km")));
    }

    private void ApplyFieldOperationsAnalysis(FieldDailyAnalysisResult analysis)
    {
        int repeatedSortieCount =
            analysis.RepeatedSortiesCumulative.Count;
        int repeatedTodayCount =
            analysis.RepeatedSortiesToday.Count;
        string repeatSuffix =
            repeatedSortieCount > 0
                ? $"；重复飞行 {repeatedSortieCount} 架次（当日 {repeatedTodayCount}；按实际磁测轨迹时间分段识别，紫色虚线，不重复计完成）"
                : "";

        _fieldRouteSourceStatus.Text =
            analysis.Route.HasSurveyLineIdentity
                ? $"设计航线：{analysis.Route.SourceLabel}；{analysis.Route.Sorties.Count} 架次；" +
                  $"测线 {analysis.Route.SurveyLines.Count} 条 / {analysis.PlannedSurveyLineKm:0.00} km" +
                  $"{repeatSuffix}；来源 {ShortPathForFieldUi(analysis.Route.SourcePath)}"
                : $"设计航线：{analysis.Route.SourceLabel}；{analysis.Route.Sorties.Count} 架次；" +
                  $"未恢复测线任务身份（兼容模式）{repeatSuffix}；来源 {ShortPathForFieldUi(analysis.Route.SourcePath)}";
        _fieldGsm19TStatus.Text =
            $"GSM-19T：{analysis.Gsm19T.Files.Count} 个文件 / {analysis.Gsm19T.SampleCount} 条记录" +
            FieldTimeSuffix(analysis.Gsm19T.FirstTime, analysis.Gsm19T.LastTime);
        _fieldUavMagStatus.Text =
            $"无人机磁测：{analysis.UavMag.Files.Count} 个文件 / {analysis.UavMag.SampleCount} 条记录 / {analysis.UavMag.TrackPointCount} 个航迹点" +
            FieldTimeSuffix(analysis.UavMag.FirstTime, analysis.UavMag.LastTime);
        _toolTip.SetToolTip(_fieldRouteSourceStatus, ShortPathForFieldUi(analysis.Route.SourcePath));
        _toolTip.SetToolTip(_fieldGsm19TStatus, string.Join(Environment.NewLine, analysis.Gsm19T.Files));
        _toolTip.SetToolTip(_fieldUavMagStatus, string.Join(Environment.NewLine, analysis.UavMag.Files));

        _fieldPlannedValue.Text = analysis.Route.Sorties.Count.ToString(CultureInfo.InvariantCulture);
        _fieldTodayValue.Text = analysis.CompletedToday.Count.ToString(CultureInfo.InvariantCulture);
        string dayWorkPercent =
            _fieldDailyProgressRecord is null
                ? "—"
                : FieldworkDocumentExportService.FormatDayWorkPercent(
                    _fieldDailyProgressRecord,
                    (int)_fieldExpectedDailySorties.Value);
        double totalPercent =
            analysis.SurveyLineMatchingAvailable &&
            analysis.PlannedSurveyLineKm > 0d
                ? analysis.CompletedSurveyLineKm /
                    analysis.PlannedSurveyLineKm
                : analysis.Route.Sorties.Count == 0
                    ? 0d
                    : analysis.CompletedCumulative.Count /
                        (double)analysis.Route.Sorties.Count;
        _fieldWorkPercentValue.Text = dayWorkPercent;
        _fieldCumulativeValue.Text = analysis.CompletedCumulative.Count.ToString(CultureInfo.InvariantCulture);
        _fieldTotalPercentValue.Text = totalPercent.ToString("P0", CultureInfo.InvariantCulture);
        _fieldRemainingValue.Text = analysis.RemainingSorties.Count.ToString(CultureInfo.InvariantCulture);

        _fieldProgressGrid.DataSource = null;
        _fieldProgressGrid.DataSource = analysis.SortieProgress
            .OrderBy(x => x.Order)
            .Select(x => new FieldSortieProgressGridRow(
                x.Order,
                x.SortieName,
                x.Status,
                x.CoverageRatio.ToString("P0", CultureInfo.InvariantCulture),
                x.Evidence))
            .ToList();
        GridHeaders.Apply(_fieldProgressGrid, new Dictionary<string, string>
        {
            [nameof(FieldSortieProgressGridRow.Order)] = "序号",
            [nameof(FieldSortieProgressGridRow.SortieName)] = "架次",
            [nameof(FieldSortieProgressGridRow.Status)] = "进度",
            [nameof(FieldSortieProgressGridRow.Coverage)] = "测线完成",
            [nameof(FieldSortieProgressGridRow.Evidence)] = "判断依据"
        });
        UiLayout.ApplyFillWeights(_fieldProgressGrid, new Dictionary<string, float>
        {
            [nameof(FieldSortieProgressGridRow.Order)] = 45,
            [nameof(FieldSortieProgressGridRow.SortieName)] = 80,
            [nameof(FieldSortieProgressGridRow.Status)] = 80,
            [nameof(FieldSortieProgressGridRow.Coverage)] = 70,
            [nameof(FieldSortieProgressGridRow.Evidence)] = 250
        }, minimumWidth: 55);

        UpdateFieldPreviewSelectedSortie();

        _fieldTakeoffRows.RaiseListChangedEvents = false;
        _fieldTakeoffRows.Clear();
        foreach (FieldTakeoffRecommendation recommendation in analysis.Recommendations)
            _fieldTakeoffRows.Add(FieldTakeoffGridRow.From(recommendation));
        _fieldTakeoffRows.RaiseListChangedEvents = true;
        _fieldTakeoffRows.ResetBindings();
        _fieldTakeoffGrid.DataSource = null;
        _fieldTakeoffGrid.DataSource = _fieldTakeoffRows;
        GridHeaders.Apply(_fieldTakeoffGrid, new Dictionary<string, string>
        {
            [nameof(FieldTakeoffGridRow.Selected)] = "显示/导出",
            [nameof(FieldTakeoffGridRow.PlanDay)] = "后续批次",
            [nameof(FieldTakeoffGridRow.RecommendationId)] = "推荐点",
            [nameof(FieldTakeoffGridRow.Longitude)] = "经度",
            [nameof(FieldTakeoffGridRow.Latitude)] = "纬度",
            [nameof(FieldTakeoffGridRow.Sorties)] = "服务架次",
            [nameof(FieldTakeoffGridRow.MeanDistance)] = "平均距离",
            [nameof(FieldTakeoffGridRow.MaxDistance)] = "最远距离"
        });
        foreach (DataGridViewColumn column in _fieldTakeoffGrid.Columns)
            column.ReadOnly = !column.Name.Equals(nameof(FieldTakeoffGridRow.Selected), StringComparison.Ordinal);
        UiLayout.ApplyFillWeights(_fieldTakeoffGrid, new Dictionary<string, float>
        {
            [nameof(FieldTakeoffGridRow.Selected)] = 45,
            [nameof(FieldTakeoffGridRow.PlanDay)] = 72,
            [nameof(FieldTakeoffGridRow.RecommendationId)] = 80,
            [nameof(FieldTakeoffGridRow.Longitude)] = 100,
            [nameof(FieldTakeoffGridRow.Latitude)] = 100,
            [nameof(FieldTakeoffGridRow.Sorties)] = 220,
            [nameof(FieldTakeoffGridRow.MeanDistance)] = 85,
            [nameof(FieldTakeoffGridRow.MaxDistance)] = 85
        }, minimumWidth: 55);

        _fieldOperationsPreview.Analysis = analysis;
        UpdateFieldPreviewVisibleServiceSorties();
        _fieldOperationsPreview.Invalidate();
        RefreshFieldProgressReportPreview();
    }

    private void SelectManualGsm19TFiles(IReadOnlyList<string>? paths = null)
    {
        if (paths is null)
        {
        using var dialog = new OpenFileDialog
        {
            Title = "选择补充 GSM-19T 数据",
            Filter = "磁测文本数据|*.csv;*.txt;*.tsv;*.dat;*.asc;*.xyz;*.log;*.stn|所有文件|*.*",
            Multiselect = true
        };
        FileInteractions.For(this, "field-extra-gsm").Prepare(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
            paths = dialog.FileNames;
        }
        AddDistinctPaths(_fieldManualGsm19TFiles, paths);
        _fieldAutoAnalysisContextKey = "";
        AnalyzeFieldOperations(force: true, quietNoRoute: false);
        FileInteractions.For(this, "field-extra-gsm").InputSucceeded(paths.ToArray());
    }

    private void SelectManualUavFiles(IReadOnlyList<string>? paths = null)
    {
        if (paths is null)
        {
        using var dialog = new OpenFileDialog
        {
            Title = "选择补充无人机磁测 / GNSS 数据",
            Filter = "无人机磁测或轨迹|*.csv;*.txt;*.tsv;*.dat;*.asc;*.xyz;*.log;*.stn;*.gpx;*.kml|所有文件|*.*",
            Multiselect = true
        };
        FileInteractions.For(this, "field-extra-uav").Prepare(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;
            paths = dialog.FileNames;
        }
        AddDistinctPaths(_fieldManualUavFiles, paths);
        _fieldAutoAnalysisContextKey = "";
        AnalyzeFieldOperations(force: true, quietNoRoute: false);
        FileInteractions.For(this, "field-extra-uav").InputSucceeded(paths.ToArray());
    }

    private static void AddDistinctPaths(List<string> target, IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                continue;
            string full = Path.GetFullPath(path);
            if (!target.Contains(full, StringComparer.OrdinalIgnoreCase))
                target.Add(full);
        }
    }

    private List<FieldTakeoffRecommendation> SelectedFieldTakeoffRecommendations()
    {
        if (_fieldDailyAnalysis is null)
            throw new InvalidOperationException("请先扫描项目数据并选择一个数据日期。 ");
        _fieldTakeoffGrid.EndEdit();
        List<string> selectedIds = _fieldTakeoffRows
            .Where(x => x.Selected)
            .Select(x => x.RecommendationId)
            .ToList();
        if (selectedIds.Count == 0)
            throw new InvalidOperationException("请先在推荐起飞点表格勾选至少一个推荐点。 ");
        HashSet<string> selected = selectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return _fieldDailyAnalysis.Recommendations
            .Where(x => selected.Contains(x.RecommendationId))
            .OrderBy(x => x.Order)
            .ToList();
    }

    private List<FieldDailyProgressRecord> CurrentFieldProjectProgressRows(
        FieldDailyProgressRecord current)
    {
        List<FieldDailyProgressRecord> rows = _fieldwork.ListFieldDailyProgress(Root)
            .Where(x =>
                string.CompareOrdinal(x.Date, current.Date) <= 0 &&
                x.Workarea.Equals(
                    current.Workarea,
                    StringComparison.OrdinalIgnoreCase) &&
                x.RouteFingerprint.Equals(
                    current.RouteFingerprint,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    x.ProgressTimeZone,
                    ProjectTime.Basis(_projectTimeZone),
                    StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Date, StringComparer.Ordinal)
            .ToList();

        if (rows.Count == 0)
            rows.Add(current);
        return rows;
    }

    private void RefreshFieldProgressReportPreview()
    {
        if (_fieldDailyProgressRecord is null)
        {
            _fieldReportSummary.Text =
                "等待生成项目时区项目进度。\r\n" +
                "完成项目数据扫描后，可在“当天简报 / 总进度简报”之间切换预览。";
            return;
        }

        if (_fieldProjectReportMode.Checked)
        {
            _fieldReportSummary.Text =
                FieldworkDocumentExportService.BuildProjectProgressPreview(
                    CurrentFieldProjectProgressRows(_fieldDailyProgressRecord),
                    (int)_fieldExpectedDailySorties.Value, ProjectTime.Basis(_projectTimeZone));
        }
        else
        {
            _fieldReportSummary.Text =
                FieldworkDocumentExportService.BuildDailyProgressPreview(
                    _fieldDailyProgressRecord,
                    (int)_fieldExpectedDailySorties.Value);
        }

        _fieldReportSummary.SelectionStart = 0;
        _fieldReportSummary.ScrollToCaret();
    }

    private void ExportCurrentFieldProgressReport()
    {
        if (_fieldProjectReportMode.Checked)
            ExportFieldProjectProgressReport();
        else
            ExportFieldDailyReport();
    }

    private void CopyCurrentFieldProgressReportToClipboard()
    {
        FieldDailyProgressRecord current =
            RequireCurrentFieldProgressRecord();

        int expectedDailySorties =
            Math.Max(1, (int)_fieldExpectedDailySorties.Value);
        string text = _fieldProjectReportMode.Checked
            ? FieldworkDocumentExportService.BuildProjectProgressPreview(
                CurrentFieldProjectProgressRows(current),
                expectedDailySorties, ProjectTime.Basis(_projectTimeZone))
            : FieldworkDocumentExportService.BuildDailyProgressPreview(
                current,
                expectedDailySorties);

        Clipboard.SetText(text);
        TransientNotice.Show(
            this,
            _fieldProjectReportMode.Checked
                ? "项目总进度简报已复制到剪贴板。"
                : "当天进度简报已复制到剪贴板。",
            "复制完成");
    }

    private void CopyFieldProgressPreviewToClipboard()
    {
        if (_fieldDailyAnalysis is null)
            throw new InvalidOperationException("请先扫描项目数据并选择一个作业日期。");

        using Bitmap bitmap = _fieldOperationsPreview.CaptureBitmap();
        using var clipboardImage = new Bitmap(bitmap);
        Clipboard.SetImage(clipboardImage);
        TransientNotice.Show(this, "作业进度预览已复制到剪贴板。", "复制完成");
    }

    private void ExportFieldProgressPreviewJpg()
    {
        if (_fieldDailyAnalysis is null)
            throw new InvalidOperationException("请先扫描项目数据并选择一个作业日期。");

        string path = FieldworkOutputPath(
            "field_daily_progress",
            "导出作业进度预览 JPG",
            $"作业进度和后续安排_{_fieldOperationsDate.Value:yyyyMMdd}_项目时区.jpg");
        using Bitmap bitmap = _fieldOperationsPreview.CaptureBitmap();
        if (!ExportProtected(path, "field_progress_preview", ProgressExportSources(),
            new { view = _fieldOperationsPreview.ExportViewSnapshot(), date = _fieldDailyAnalysis.Date },
            staged => AtomicFileWriter.Write(staged, stream => bitmap.Save(stream, ImageFormat.Jpeg)))) return;

    }

    private void ExportFieldProjectProgressReport()
    {
        FieldDailyProgressRecord current = RequireCurrentFieldProgressRecord();
        List<FieldDailyProgressRecord> rows =
            CurrentFieldProjectProgressRows(current);

        string path = FieldworkOutputPath(
            "field_daily_progress",
            "导出项目外业总进度简报",
            $"项目外业总进度简报_截至_{current.Date}_项目时区.md");
        if (!ExportProtected(path, "field_project_progress", ProgressExportSources(projectReport: true),
            new { expectedDailySorties = (int)_fieldExpectedDailySorties.Value, cutoff = current.Date,
                timeZone = _projectTimeZone.Id, formatVersion = "field-progress-1" },
            staged => FieldworkDocumentExportService.WriteProjectProgress(staged, rows,
                Math.Max(1, (int)_fieldExpectedDailySorties.Value), ProjectTime.Basis(_projectTimeZone)), projectLevel: true)) return;

    }

    private void ExportFieldDailyReport()
    {
        FieldDailyProgressRecord row = RequireCurrentFieldProgressRecord();
        string path = FieldworkOutputPath(
            "field_daily_progress",
            "导出外业当日任务简报",
            $"外业工作进度简报_{row.Date}_项目时区.md");
        if (!ExportProtected(path, "field_daily_progress", ProgressExportSources(),
            new { expectedDailySorties = (int)_fieldExpectedDailySorties.Value, date = row.Date, formatVersion = "field-progress-1" },
            staged => FieldworkDocumentExportService.WriteDailyProgress(staged, row,
                Math.Max(1, (int)_fieldExpectedDailySorties.Value)))) return;

    }

    private void ExportSelectedTakeoffCoordinates()
    {
        List<FieldTakeoffRecommendation> selected = SelectedFieldTakeoffRecommendations();
        string path = FieldworkOutputPath(
            "field_takeoff_export",
            "导出推荐起飞点坐标",
            $"推荐起飞点_{_fieldOperationsDate.Value:yyyyMMdd}.csv");
        if (!ExportProtected(path, "field_takeoff_coordinates", ProgressExportSources(),
            new { selected, formatVersion = "recommendation-csv-1" },
            staged => FieldOperationsAnalysisService.WriteRecommendationCsv(staged, selected))) return;

    }

    private void ExportSelectedTakeoffGpx()
    {
        List<FieldTakeoffRecommendation> selected = SelectedFieldTakeoffRecommendations();
        string path = FieldworkOutputPath(
            "field_takeoff_export",
            "导出推荐起飞点 GPX",
            $"推荐起飞点_{_fieldOperationsDate.Value:yyyyMMdd}.gpx");
        if (!ExportProtected(path, "field_takeoff_gpx", ProgressExportSources(),
            new { selected, formatVersion = "recommendation-gpx-1" },
            staged => SpatialGeometryService.WriteGpx(staged, selected.Select(point => new SpatialGeometry
            {
                Name = point.RecommendationId, Type = "Point", Coordinates = [point.Coordinate]
            })))) return;

    }

    private void ExportSelectedTakeoffConnectionGpx()
    {
        if (_fieldDailyAnalysis is null)
            throw new InvalidOperationException("请先扫描项目数据并选择一个数据日期。 ");
        List<FieldTakeoffRecommendation> selected = SelectedFieldTakeoffRecommendations();
        Dictionary<string, PlannedFieldSortie> byName = _fieldDailyAnalysis.Route.Sorties
            .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var lines = new List<SpatialGeometry>();
        foreach (FieldTakeoffRecommendation point in selected)
        {
            foreach (string sortieName in point.SortieNames)
            {
                if (!byName.TryGetValue(sortieName, out PlannedFieldSortie? sortie) || sortie.Coordinates.Count == 0)
                    continue;
                lines.Add(new SpatialGeometry
                {
                    Name = $"{point.RecommendationId}-{sortie.Name}",
                    Type = "LineString",
                    Coordinates = [point.Coordinate, sortie.Coordinates[0]]
                });
            }
        }
        if (lines.Count == 0)
            throw new InvalidOperationException("所选推荐点没有可导出的架次起始点连接线。 ");

        string path = FieldworkOutputPath(
            "field_takeoff_export",
            "导出推荐起飞点连接线 GPX",
            $"起飞点至架次起点连接线_{_fieldOperationsDate.Value:yyyyMMdd}.gpx");
        if (!ExportProtected(path, "field_takeoff_connection_gpx", ProgressExportSources(),
            new { selected, lines, formatVersion = "connection-gpx-1" },
            staged => SpatialGeometryService.WriteGpx(staged, lines))) return;

    }

    private FieldDailyProgressRecord RequireCurrentFieldProgressRecord()
    {
        if (_fieldDailyProgressRecord is null || _fieldDailyAnalysis is null)
            throw new InvalidOperationException("请先扫描项目数据并选择一个数据日期。 ");
        return _fieldDailyProgressRecord;
    }

    private string FieldTimeSuffix(DateTimeOffset? first, DateTimeOffset? last)
    {
        if (first.HasValue && last.HasValue)
        {
            DateTimeOffset a = ProjectTime.Display(first.Value, _projectTimeZone);
            DateTimeOffset b = ProjectTime.Display(last.Value, _projectTimeZone);
            return $"；项目时区 {a:MM-dd HH:mm}–{b:MM-dd HH:mm}";
        }
        DateTimeOffset? value = first ?? last;
        return value.HasValue
            ? $"；{ProjectTime.Label(_projectTimeZone)} {ProjectTime.Display(value.Value, _projectTimeZone):MM-dd HH:mm}"
            : "";
    }

    private string ShortPathForFieldUi(string path) =>
        WorkareaRelativePathForFieldUi(path);

    private void ResetFieldOperationsContext()
    {
        _fieldExportAnalysisDependencies = null;
        _fieldExportAnalysisContext = "";
        InvalidateDiurnalQuality();
        _fieldRouteScope.SelectedIndex = 0;
        ResetProcessingReview();
        _fieldAutoAnalysisContextKey = "";
        _fieldProjectTimelineContextKey = "";
        _fieldPreferredRouteImportDirectory = "";
        _fieldDataAnalysisContextKey = "";
        _fieldMagDataAnalysis = null;
        _fieldMagRawAnalysis = null;
        _demHeightOptions = new();
        _demHeightSnapshot = null;
        _fieldSpatialScreening = null;
        _fieldDataFolderFullPath = "";
        _fieldDataFolder.Clear();
        _loadingFieldOperationsSettings = true;
        try
        {
            _fieldExpectedDailySorties.Value = 8;
            _fieldAcceptedRelocations.Value = 1;
        }
        finally
        {
            _loadingFieldOperationsSettings = false;
        }
        _fieldDataFileGrid.DataSource = null;
        _fieldDataQcGrid.DataSource = null;
        _fieldDateGrid.DataSource = null;
        _fieldMagCoverage = Array.Empty<FieldMagDailyCoverageSummary>();
        _fieldCoveragePreview.Summary = null;
        _fieldCoverageStatus.Text = "19T 覆盖：等待扫描。";
        _fieldDataSummary.Text = "等待扫描项目数据目录。项目进度按项目时区归类；UTC/QC 技术信息仍保留在本页。";
        _fieldDataFolderStatus.Text = "项目数据识别：默认扫描当前工区“05_外业记录与采集成果”；也可手工选择其他项目数据目录。";
        _fieldDataFingerprintStatus.Text = "项目完整规范数据集：等待扫描。";
        _updatingFieldDateSelection = true;
        try
        {
            _fieldDateSelector.DataSource = null;
            _fieldDateSelector.Items.Clear();
            _fieldDateSelector.Items.Add("等待扫描项目数据");
            _fieldDateSelector.SelectedIndex = 0;
            _fieldDateSelector.Enabled = false;
        }
        finally
        {
            _updatingFieldDateSelection = false;
        }
        _fieldManualGsm19TFiles.Clear();
        _fieldManualUavFiles.Clear();
        _fieldDailyAnalysis = null;
        _fieldDailyProgressRecord = null;
        _fieldProgressGrid.DataSource = null;
        _fieldTakeoffRows.Clear();
        _fieldTakeoffGrid.DataSource = null;
        _fieldOperationsPreview.Analysis = null;
        _fieldOperationsPreview.SelectedSortieName = "";
        _fieldOperationsPreview.SetVisibleServiceRecommendationIds([]);
        _fieldOperationsPreview.Invalidate();
        _fieldPlannedValue.Text = "—";
        _fieldTodayValue.Text = "—";
        _fieldWorkPercentValue.Text = "—";
        _fieldCumulativeValue.Text = "—";
        _fieldTotalPercentValue.Text = "—";
        _fieldRemainingValue.Text = "—";
        _fieldRouteSourceStatus.Text = "设计航线：进入外业管理页后自动读取当前工区设计成果。";
        _fieldGsm19TStatus.Text = "GSM-19T：等待读取完整规范数据。";
        _fieldUavMagStatus.Text = "无人机磁测：等待读取完整规范数据。";
        _fieldBaseStationStatus.Text = "所选作业日：尚未登记日变站。";
        _fieldOperationsPreview.SetBaseStation(null, "");
        _fieldReportSummary.Text =
            "等待生成项目时区项目进度。\r\n可在“当天简报 / 总进度简报”之间切换预览。";
    }
}

internal sealed record FieldMagFileGridRow(
    string File,
    string Sensor,
    string Role,
    int Records,
    string UtcRange,
    string Evidence);

internal sealed record FieldDataQcGridRow(
    string Severity,
    string Code,
    string Scope,
    string UtcRange,
    string Message);

internal sealed record FieldDateGridRow(
    string UtcDate,
    int UavFiles,
    int UavRecords,
    int BaseFiles,
    int BaseRecords,
    string BaseCoverage,
    string UtcRange,
    int FlaggedRecords);

internal sealed record FieldDateChoice(
    DateTime UtcDate,
    string Display);

internal sealed record FieldSortieProgressGridRow(
    int Order,
    string SortieName,
    string Status,
    string Coverage,
    string Evidence);

internal sealed class FieldTakeoffGridRow
{
    public bool Selected { get; set; } = false;
    public string PlanDay { get; set; } = "";
    public string RecommendationId { get; set; } = "";
    public string Longitude { get; set; } = "";
    public string Latitude { get; set; } = "";
    public string Sorties { get; set; } = "";
    public string MeanDistance { get; set; } = "";
    public string MaxDistance { get; set; } = "";

    public static FieldTakeoffGridRow From(FieldTakeoffRecommendation row) => new()
    {
        Selected = false,
        PlanDay = $"批次 {row.PlanDay}",
        RecommendationId = row.RecommendationId,
        Longitude = row.Coordinate.Longitude.ToString("0.########", CultureInfo.InvariantCulture),
        Latitude = row.Coordinate.Latitude.ToString("0.########", CultureInfo.InvariantCulture),
        Sorties = string.Join("、", row.SortieNames),
        MeanDistance = $"{row.MeanDistanceM:0} m",
        MaxDistance = $"{row.MaxDistanceM:0} m"
    };
}
