namespace MagOps.Desktop;

internal sealed record RouteAdvancedSettingsState(
    string InitialLine,
    string LineIncrement,
    int CoordModeIndex,
    string HeaderlessOrder,
    string CentralMeridian,
    int PartitionStartSideIndex,
    int StartDirectionIndex,
    int TakeoffAnchorModeIndex,
    int FlightDirectionIndex,
    string TakeoffAnchorRef,
    string StartPoint,
    IReadOnlyList<string> StartPointChoices);

internal sealed class RouteAdvancedSettingsDialog : Form
{
    private readonly TextBox _initialLine = new();
    private readonly TextBox _lineIncrement = new();
    private readonly ComboBox _coordMode = new();
    private readonly ComboBox _headerlessOrder = new();
    private readonly TextBox _centralMeridian = new();
    private readonly ComboBox _partitionStartSide = new();
    private readonly ComboBox _startDirection = new();
    private readonly ComboBox _takeoffAnchorMode = new();
    private readonly ComboBox _flightDirection = new();
    private readonly TextBox _takeoffAnchorRef = new();
    private readonly ComboBox _startPoint = new();

    internal RouteAdvancedSettingsDialog(
        RouteAdvancedSettingsState current)
    {
        Text = "航线高级参数";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Width = 680;
        Height = 520;
        MinimumSize = new Size(600, 450);
        BackColor = UiTheme.Workspace;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(14),
            Margin = Padding.Empty,
            BackColor = UiTheme.Workspace
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(
            UiComponents.HelpLabel(
            "常用作业通常无需修改这些设置。分区起始侧与起始方向控制航线遍历顺序，测线飞行方向单独控制。" +
                "蛇形返航外扩：待修改（已冻结；补充可通行道路计算方法后启用），当前不会为了凑偶数向测区外增加测线。"),
            0,
            0);

        _takeoffAnchorMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _takeoffAnchorMode.Items.AddRange(["自动（兼容旧规则）", "外边界侧", "切割线优先", "自定义点"]);
        _takeoffAnchorMode.SelectedIndex = Math.Clamp(current.TakeoffAnchorModeIndex, 0, _takeoffAnchorMode.Items.Count - 1);
        _flightDirection.DropDownStyle = ComboBoxStyle.DropDownList;
        _flightDirection.Items.AddRange(["自动", "西 → 东", "东 → 西", "南 → 北", "北 → 南"]);
        _flightDirection.SelectedIndex = Math.Clamp(current.FlightDirectionIndex, 0, _flightDirection.Items.Count - 1);
        _takeoffAnchorRef.Text = current.TakeoffAnchorRef;

        var form = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 12,
            Margin = new Padding(0, 12, 0, 0),
            Padding = Padding.Empty
        };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 12; i++)
            form.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _initialLine.Text =
            current.InitialLine;
        _lineIncrement.Text =
            current.LineIncrement;

        _coordMode.DropDownStyle =
            ComboBoxStyle.DropDownList;
        _coordMode.Items.AddRange([
            "自动判断",
            "平面坐标（高斯-克吕格）",
            "经纬度（WGS84）"
        ]);
        _coordMode.SelectedIndex =
            Math.Clamp(
                current.CoordModeIndex,
                0,
                _coordMode.Items.Count - 1);

        _headerlessOrder.DropDownStyle =
            ComboBoxStyle.DropDownList;
        _headerlessOrder.Items.AddRange([
            "Point,Y,X",
            "Point,X,Y",
            "Y,X",
            "X,Y"
        ]);
        int axisIndex =
            _headerlessOrder.Items.IndexOf(
                current.HeaderlessOrder);
        _headerlessOrder.SelectedIndex =
            axisIndex >= 0
                ? axisIndex
                : 0;

        _centralMeridian.Text =
            current.CentralMeridian;

        _partitionStartSide.DropDownStyle =
            ComboBoxStyle.DropDownList;
        _partitionStartSide.Items.AddRange([
            "自动（共同切分边界）",
            "北侧",
            "南侧",
            "西侧",
            "东侧"
        ]);
        _partitionStartSide.SelectedIndex =
            Math.Clamp(
                current.PartitionStartSideIndex,
                0,
                _partitionStartSide.Items.Count - 1);

        _startDirection.DropDownStyle =
            ComboBoxStyle.DropDownList;
        _startDirection.Items.AddRange([
            "西南",
            "西北",
            "东南",
            "东北"
        ]);
        _startDirection.SelectedIndex =
            Math.Clamp(
                current.StartDirectionIndex,
                0,
                _startDirection.Items.Count - 1);

        _startPoint.DropDownStyle =
            ComboBoxStyle.DropDown;
        _startPoint.AutoCompleteMode =
            AutoCompleteMode.SuggestAppend;
        _startPoint.AutoCompleteSource =
            AutoCompleteSource.ListItems;
        foreach (string choice in
                 current.StartPointChoices)
        {
            _startPoint.Items.Add(
                choice);
        }
        _startPoint.Text =
            current.StartPoint;

        AddRow(
            form,
            0,
            "初始线号",
            _initialLine,
            "默认 1。只影响生成测线的编号，不改变几何位置。");
        AddRow(
            form,
            1,
            "线号增量",
            _lineIncrement,
            "默认 1；不能为 0。");
        AddRow(
            form,
            2,
            "坐标模式",
            _coordMode,
            "默认“自动判断”。");
        AddRow(
            form,
            3,
            "无表头坐标轴顺序",
            _headerlessOrder,
            "仅用于无表头端点 CSV/TXT/TSV 的兼容读取。");
        AddRow(
            form,
            4,
            "中央经线 °E",
            _centralMeridian,
            "留空时使用项目值或自动推断。");
        AddRow(
            form,
            5,
            "分区起始侧",
            _partitionStartSide,
            "仅分区航线生效。相邻子区可分别选东/西或北/南，使作业从共同边界侧开始；“自动”保留原共同切分边界逻辑。");
        AddRow(
            form,
            6,
            "起始方向",
            _startDirection,
            "控制所选边界侧上的具体角点偏好。");
        AddRow(
            form,
            7,
            "起始空间点",
            _startPoint,
            "可留空使用自动角点，也可选择/输入明确端点。");

        if (MagOps.Core.Routes.LaunchSitePlanning.Enabled) AddRow(form, 8, "起始锚点模式", _takeoffAnchorMode,
            "自动兼容旧规则；切割线优先使用当前子区切割线；自定义点预留给地图坐标。 ");
        AddRow(form, 9, "测线飞行方向", _flightDirection,
            "控制测线的飞行方向；例如 90° 测线选择“西 → 东”。");
        if (MagOps.Core.Routes.LaunchSitePlanning.Enabled) AddRow(form, 10, "锚点引用", _takeoffAnchorRef,
            "切割线编号或用户备注；留空时使用自动匹配。");

        var snakeExtensionStatus = new Label
        {
            Text = "待修改；补充可通行道路计算方法后启用",
            AutoSize = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = UiTheme.TextSecondary,
            Padding = new Padding(0, 7, 0, 5),
            Margin = Padding.Empty
        };
        AddRow(
            form,
            11,
            "蛇形外扩增线",
            snakeExtensionStatus,
            "当前生产路径已回退：不会为了凑偶数向工区外增加正式测线。该功能冻结，不提供启用开关。");

        root.Controls.Add(
            form,
            0,
            1);

        var buttons =
            new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = true,
                Margin = new Padding(0, 12, 0, 0),
                Padding = Padding.Empty
            };

        var ok =
            UiComponents.Button(
                "应用",
                (_, _) =>
                {
                    DialogResult =
                        DialogResult.OK;
                    Close();
                },
                UiButtonRole.Primary,
                minimumHeight: 36);
        ok.AutoSize = true;
        ok.MinimumSize =
            new Size(96, 36);

        var cancel =
            UiComponents.Button(
                "取消",
                (_, _) =>
                {
                    DialogResult =
                        DialogResult.Cancel;
                    Close();
                },
                UiButtonRole.Subtle,
                minimumHeight: 36);
        cancel.AutoSize = true;
        cancel.MinimumSize =
            new Size(96, 36);

        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        root.Controls.Add(
            buttons,
            0,
            2);

        Controls.Add(root);
        AcceptButton = ok;
        CancelButton = cancel;

        Shown += (_, _) =>
            UiLayout.SizeDialogToOwner(
                this,
                Owner,
                widthRatio: 0.46,
                heightRatio: 0.66,
                minimum: new Size(600, 450),
                maximum: new Size(860, 760));

        UiTheme.Apply(this);
        UiLayout.FitFormToWorkingArea(this);
    }

    internal RouteAdvancedSettingsState GetSettings() =>
        new(
            InitialLine:
                _initialLine.Text.Trim(),
            LineIncrement:
                _lineIncrement.Text.Trim(),
            CoordModeIndex:
                _coordMode.SelectedIndex,
            HeaderlessOrder:
                _headerlessOrder.SelectedItem?.ToString()
                ?? "Point,Y,X",
            CentralMeridian:
                _centralMeridian.Text.Trim(),
            PartitionStartSideIndex:
                _partitionStartSide.SelectedIndex,
            StartDirectionIndex:
                _startDirection.SelectedIndex,
            TakeoffAnchorModeIndex: _takeoffAnchorMode.SelectedIndex,
            FlightDirectionIndex: _flightDirection.SelectedIndex,
            TakeoffAnchorRef: _takeoffAnchorRef.Text.Trim(),
            StartPoint:
                _startPoint.Text.Trim(),
            StartPointChoices:
                _startPoint.Items
                    .Cast<object>()
                    .Select(item =>
                        item?.ToString() ?? "")
                    .Where(item =>
                        !string.IsNullOrWhiteSpace(item))
                    .ToArray());

    private static void AddRow(
        TableLayoutPanel table,
        int row,
        string label,
        Control control,
        string help)
    {
        var left =
            new Label
            {
                Text = label,
                AutoSize = true,
                Dock = DockStyle.Fill,
                TextAlign =
                    ContentAlignment.MiddleLeft,
                ForeColor =
                    UiTheme.TextPrimary,
                Margin =
                    new Padding(0, 7, 8, 3)
            };

        var value =
            new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
        value.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Absolute,
                250));
        value.ColumnStyles.Add(
            new ColumnStyle(
                SizeType.Percent,
                100));

        control.Dock =
            DockStyle.Fill;
        control.Margin =
            new Padding(0, 3, 8, 3);
        value.Controls.Add(
            control,
            0,
            0);

        Label helpLabel =
            UiComponents.HelpLabel(
                help);
        helpLabel.Dock =
            DockStyle.Fill;
        helpLabel.Margin =
            new Padding(0, 6, 0, 0);
        value.Controls.Add(
            helpLabel,
            1,
            0);

        table.Controls.Add(
            left,
            0,
            row);
        table.Controls.Add(
            value,
            1,
            row);
    }
}
