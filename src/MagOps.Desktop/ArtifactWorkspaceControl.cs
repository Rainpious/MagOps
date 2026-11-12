using System.ComponentModel;
using MagOps.Core.Domain;

namespace MagOps.Desktop;

internal sealed record ArtifactWorkspaceActions(
    Func<Task> ChooseAndImportRawMaterials,
    Func<IEnumerable<string>, Task> ImportRawMaterialPaths,
    Action OpenLastRawImportFolder,
    Action InspectPartialImports,
    Action OpenFolderLayout,
    Action ApplyFilters,
    Action RefreshArtifacts,
    Action EditCurrentArtifact,
    Action ShowCurrentDetail,
    Action RevealCurrentArtifact,
    Action RefreshScopeList,
    Action<string> ScopeChanged,
    Action SetBrowseWorkareaAsCurrent,
    Action EditCurrentNotes,
    Action AssignCurrentWorkarea,
    Action SelectionChanged,
    Action<ArtifactRecord> RevealArtifact,
    Action<ArtifactRecord> ToggleSelected,
    Action<ArtifactRecord, string> EditProperty,
    Action<ArtifactRecord> ShowDetail);

internal sealed class ArtifactWorkspaceControl : UserControl
{
    private readonly ArtifactWorkspaceActions _actions;

    private readonly DataGridView _grid = GridFactory.Create();
    private readonly ComboBox _roleFilter = new();
    private readonly ComboBox _extFilter = new();
    private readonly TextBox _textFilter = new();
    private readonly TextBox _workareaSearch = new();
    private readonly ListBox _scopeList = new();
    private readonly Label _detailTitle = new();
    private readonly RichTextBox _detailText = new();
    private readonly Label _rawImportHealthLabel = new();
    private readonly CheckBox _missingOnly = new();
    private readonly BindingList<ArtifactRecord> _viewRows = new();
    private readonly BindingSource _bindingSource = new();
    private readonly System.Windows.Forms.Timer _filterDebounce = new() { Interval = 180 };
    private readonly ToolTip _toolTip = new();
    private readonly TabControl _contentTabs = new() { Dock = DockStyle.Fill };
    private readonly TextBox _importResult = new()
    {
        Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, WordWrap = false,
        AccessibleName = "原始资料归档结果", Text = "当前项目尚无本次归档结果。"
    };

    private SplitContainer? _outerSplit;
    private SplitContainer? _centerDetailSplit;
    private bool _updatingFilters;
    private bool _updatingScopeList;

    internal ArtifactWorkspaceControl(ArtifactWorkspaceActions actions)
    {
        _actions = actions;
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        BackColor = UiTheme.Workspace;

        Build();
        Disposed += (_, _) =>
        {
            _filterDebounce.Dispose();
            _bindingSource.Dispose();
            _toolTip.Dispose();
        };
    }

    private void Build()
    {
        _filterDebounce.Tick += (_, _) =>
        {
            _filterDebounce.Stop();
            if (!_updatingFilters)
                _actions.ApplyFilters();
        };

        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = UiTheme.PagePadding,
            Margin = Padding.Empty,
            BackColor = UiTheme.Workspace
        };
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        outer.Controls.Add(UiComponents.PageHeader(
            "资料与归档",
            "按项目/工区浏览受管资料，统一完成原始资料复制归档、元数据维护、SHA 校验与文件定位。"), 0, 0);

        var rawIntake = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 5,
            RowCount = 1,
            Margin = new Padding(0, 0, 0, 4),
            Padding = new Padding(6),
            BackColor = UiTheme.Surface
        };
        rawIntake.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        rawIntake.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        rawIntake.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        rawIntake.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        rawIntake.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var addRawMaterials = Button(
            "添加原始资料…",
            async (_, _) => await _actions.ChooseAndImportRawMaterials(),
            UiButtonRole.Primary);
        var importGate = new FileDropSupport.Gate();
        FileDropSupport.EnableMultipleAsync(addRawMaterials, paths => _actions.ImportRawMaterialPaths(paths),
            importGate, onError: message => SetRawImportHealth(message, healthy: false));
        rawIntake.Controls.Add(addRawMaterials, 0, 0);

        var dropZone = new Label
        {
            Text = "将原始文件或文件夹拖到这里：复制归档、保留源文件、SHA-256 校验；完全重复文件自动跳过。",
            Dock = DockStyle.Fill,
            AutoSize = false,
            Height = 38,
            TextAlign = ContentAlignment.MiddleLeft,
            BorderStyle = BorderStyle.FixedSingle,
            AllowDrop = true,
            Margin = new Padding(8, 0, 8, 0),
            Padding = new Padding(8, 0, 8, 0)
        };
        FileDropSupport.EnableMultipleAsync(dropZone, paths => _actions.ImportRawMaterialPaths(paths),
            importGate, onError: message => SetRawImportHealth(message, healthy: false), allowDirectories: true);
        rawIntake.Controls.Add(dropZone, 1, 0);
        rawIntake.Controls.Add(Button(
            "打开最近归档",
            (_, _) => _actions.OpenLastRawImportFolder(),
            UiButtonRole.Subtle), 2, 0);
        rawIntake.Controls.Add(Button(
            "检查未完成导入",
            (_, _) => _actions.InspectPartialImports(),
            UiButtonRole.Subtle), 3, 0);
        rawIntake.Controls.Add(Button(
            "目录规则",
            (_, _) => _actions.OpenFolderLayout(),
            UiButtonRole.Subtle), 4, 0);
        outer.Controls.Add(rawIntake, 0, 1);

        _rawImportHealthLabel.AutoSize = false;
        _rawImportHealthLabel.AutoEllipsis = true;
        _rawImportHealthLabel.Dock = DockStyle.Fill;
        _rawImportHealthLabel.Height = 28;
        _rawImportHealthLabel.ForeColor = SystemColors.GrayText;
        _rawImportHealthLabel.Margin = new Padding(4, 0, 4, 4);
        _rawImportHealthLabel.Text = "原始资料导入：未发现未完成批次。";
        outer.Controls.Add(_rawImportHealthLabel, 0, 2);

        var filters = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            AutoScroll = false,
            Margin = new Padding(0, 0, 0, 5)
        };
        filters.Controls.Add(Caption("搜索"));
        _textFilter.Width = 190;
        _textFilter.PlaceholderText = "文件名 / 相对路径 / 备注";
        _textFilter.TextChanged += (_, _) =>
        {
            if (_updatingFilters)
                return;
            _filterDebounce.Stop();
            _filterDebounce.Start();
        };
        filters.Controls.Add(_textFilter);

        filters.Controls.Add(Caption("资料类型", 12));
        _roleFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        _roleFilter.Width = 135;
        _roleFilter.Items.Add(new UiChoice("", "全部类型"));
        foreach (string role in new[]
                 {
                     "raw", "registry", "coordinate", "route", "route_result", "field", "processing",
                     "derived", "figure", "report", "external_software", "project_management",
                     "workarea", "other", "unclassified"
                 })
        {
            _roleFilter.Items.Add(UiText.Choice(role));
        }
        _roleFilter.SelectedIndex = 0;
        _roleFilter.SelectedIndexChanged += (_, _) =>
        {
            if (!_updatingFilters)
                _actions.ApplyFilters();
        };
        filters.Controls.Add(_roleFilter);

        filters.Controls.Add(Caption("扩展名", 12));
        _extFilter.DropDownStyle = ComboBoxStyle.DropDown;
        _extFilter.Width = 120;
        _extFilter.Items.Add("全部扩展名");
        _extFilter.SelectedIndex = 0;
        _extFilter.SelectedIndexChanged += (_, _) =>
        {
            if (!_updatingFilters)
                _actions.ApplyFilters();
        };
        _extFilter.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
                return;
            e.SuppressKeyPress = true;
            _actions.ApplyFilters();
        };
        filters.Controls.Add(_extFilter);

        _missingOnly.Text = "仅显示缺失";
        _missingOnly.AutoSize = true;
        _missingOnly.Margin = new Padding(12, 7, 3, 3);
        _missingOnly.CheckedChanged += (_, _) =>
        {
            if (!_updatingFilters)
                _actions.ApplyFilters();
        };
        filters.Controls.Add(_missingOnly);

        filters.Controls.Add(Button("清除筛选", (_, _) =>
        {
            ClearFilters();
            _actions.ApplyFilters();
        }, UiButtonRole.Subtle));
        filters.Controls.Add(Button("刷新台账", (_, _) => _actions.RefreshArtifacts(), UiButtonRole.Subtle));
        filters.Controls.Add(Button("编辑资料", (_, _) => _actions.EditCurrentArtifact(), UiButtonRole.Subtle));
        filters.Controls.Add(Button("查看详情", (_, _) => _actions.ShowCurrentDetail(), UiButtonRole.Subtle));
        filters.Controls.Add(Button("定位文件", (_, _) => _actions.RevealCurrentArtifact(), UiButtonRole.Subtle));
        outer.Controls.Add(filters, 0, 3);

        BuildGridColumns();
        _bindingSource.DataSource = _viewRows;
        _grid.DataSource = _bindingSource;
        GridFactory.SetEmptyText(
            _grid,
            "暂无符合条件的资料。\r\n可切换左侧工区或调整筛选条件。");
        _grid.CellMouseDoubleClick += GridCellMouseDoubleClick;
        _grid.CellToolTipTextNeeded += GridCellToolTipTextNeeded;
        _grid.SelectionChanged += (_, _) => _actions.SelectionChanged();

        _outerSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel1,
            IsSplitterFixed = true,
            SplitterWidth = 5,
            Panel1MinSize = 25,
            Panel2MinSize = 25,
            Margin = Padding.Empty
        };
        _centerDetailSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            FixedPanel = FixedPanel.Panel2,
            IsSplitterFixed = true,
            SplitterWidth = 5,
            Panel1MinSize = 25,
            Panel2MinSize = 25,
            Margin = Padding.Empty
        };
        _outerSplit.Panel1.Controls.Add(BuildScopePanel());
        _centerDetailSplit.Panel1.Controls.Add(_grid);
        _centerDetailSplit.Panel2.Controls.Add(BuildDetailPanel());
        var ledgerPage = new TabPage("资料台账") { Padding = Padding.Empty };
        ledgerPage.Controls.Add(_centerDetailSplit);
        var resultPage = new TabPage("归档结果") { Padding = new Padding(6) };
        resultPage.Controls.Add(_importResult);
        _contentTabs.TabPages.AddRange([ledgerPage, resultPage]);
        _outerSplit.Panel2.Controls.Add(_contentTabs);
        outer.Controls.Add(_outerSplit, 0, 4);

        Controls.Add(outer);
    }

    private Control BuildScopePanel()
    {
        var group = new GroupBox
        {
            Text = "工区筛选",
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };
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
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _workareaSearch.Dock = DockStyle.Top;
        _workareaSearch.PlaceholderText = "搜索工区（仅筛选资料）…";
        _workareaSearch.Margin = new Padding(0, 0, 0, 6);
        _workareaSearch.TextChanged += (_, _) =>
        {
            if (!_updatingScopeList) _actions.RefreshScopeList();
        };
        layout.Controls.Add(_workareaSearch, 0, 0);

        _scopeList.Dock = DockStyle.Fill;
        _scopeList.IntegralHeight = false;
        _scopeList.HorizontalScrollbar = true;
        _scopeList.BorderStyle = BorderStyle.FixedSingle;
        _scopeList.SelectedIndexChanged += (_, _) =>
        {
            if (_updatingScopeList)
                return;
            if (_scopeList.SelectedItem is ArtifactScopeItem item)
                _actions.ScopeChanged(item.Key);
        };
        layout.Controls.Add(_scopeList, 0, 1);
        const string scopeExplanation = "选择这里只改变资料浏览范围，不会隐式切换当前工作工区。需要切换时，请点击下方按钮。";
        _scopeList.AccessibleName = "资料浏览工区";
        _scopeList.AccessibleDescription = scopeExplanation;
        _toolTip.SetToolTip(_scopeList, scopeExplanation);
        _toolTip.SetToolTip(_workareaSearch, scopeExplanation);
        var setCurrent = Button(
            "设为当前工作工区",
            (_, _) => _actions.SetBrowseWorkareaAsCurrent(),
            UiButtonRole.Subtle);
        setCurrent.Dock = DockStyle.Top;
        layout.Controls.Add(setCurrent, 0, 2);

        group.Controls.Add(layout);
        return group;
    }

    private Control BuildDetailPanel()
    {
        var group = new GroupBox
        {
            Text = "资料详情",
            Dock = DockStyle.Fill,
            Padding = new Padding(8)
        };
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

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        _detailTitle.AutoSize = false;
        _detailTitle.Dock = DockStyle.Top;
        _detailTitle.Height = 32;
        _detailTitle.AutoEllipsis = true;
        _detailTitle.Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold);
        _detailTitle.Text = "未选择资料";
        _detailTitle.TextAlign = ContentAlignment.MiddleLeft;
        layout.Controls.Add(_detailTitle, 0, 0);

        _detailText.Dock = DockStyle.Fill;
        _detailText.ReadOnly = true;
        _detailText.BorderStyle = BorderStyle.None;
        _detailText.BackColor = SystemColors.Window;
        _detailText.DetectUrls = false;
        _detailText.WordWrap = true;
        _detailText.TabStop = false;
        layout.Controls.Add(_detailText, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 6, 0, 0)
        };
        buttons.Controls.Add(Button("编辑资料", (_, _) => _actions.EditCurrentArtifact(), UiButtonRole.Subtle));
        buttons.Controls.Add(Button("编辑备注", (_, _) => _actions.EditCurrentNotes(), UiButtonRole.Subtle));
        buttons.Controls.Add(Button("设置工区归属", (_, _) => _actions.AssignCurrentWorkarea(), UiButtonRole.Subtle));
        buttons.Controls.Add(Button("定位文件", (_, _) => _actions.RevealCurrentArtifact(), UiButtonRole.Subtle));
        layout.Controls.Add(buttons, 0, 2);
        group.Controls.Add(layout);
        return group;
    }

    private void BuildGridColumns()
    {
        _grid.AutoGenerateColumns = false;
        _grid.Columns.Clear();
        _grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _grid.RowTemplate.Height = 28;
        _grid.RowTemplate.Resizable = DataGridViewTriState.False;
        _grid.AllowUserToResizeColumns = false;
        _grid.AllowUserToResizeRows = false;
        _grid.AllowUserToOrderColumns = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

        AddTextColumn(nameof(ArtifactRecord.Path), "相对路径", 265, 190);
        AddTextColumn(nameof(ArtifactRecord.Role), "资料类型", 82, 78);
        AddTextColumn(nameof(ArtifactRecord.ProcessingStage), "处理阶段", 82, 78);
        AddTextColumn(nameof(ArtifactRecord.VersionLabel), "版本", 55, 60);
        AddTextColumn(nameof(ArtifactRecord.QualityStatus), "质量状态", 75, 72);
        AddTextColumn(nameof(ArtifactRecord.FreshnessStatus), "成果有效性", 75, 78);
        _grid.Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = nameof(ArtifactRecord.Selected),
            DataPropertyName = nameof(ArtifactRecord.Selected),
            HeaderText = "选用",
            ThreeState = true,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 48,
            MinimumWidth = 54
        });
        AddTextColumn(nameof(ArtifactRecord.ChangeStatus), "变化", 65, 68);
        AddTextColumn(nameof(ArtifactRecord.ModifiedAt), "修改时间", 95, 110);
    }

    private void AddTextColumn(string property, string header, float fillWeight, int minimumWidth)
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = property,
            DataPropertyName = property,
            HeaderText = header,
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = fillWeight,
            MinimumWidth = minimumWidth
        });
    }

    private void GridCellMouseDoubleClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || e.RowIndex < 0 || e.ColumnIndex < 0)
            return;
        if (_grid.Rows[e.RowIndex].DataBoundItem is not ArtifactRecord row)
            return;

        string property = _grid.Columns[e.ColumnIndex].DataPropertyName;
        if (property == nameof(ArtifactRecord.Path))
        {
            _actions.RevealArtifact(row);
            return;
        }
        if (property == nameof(ArtifactRecord.Selected))
        {
            _actions.ToggleSelected(row);
            return;
        }
        if (IsEditableProperty(property))
        {
            _actions.EditProperty(row, property);
            return;
        }
        if (_centerDetailSplit?.Panel2Collapsed == true)
            _actions.ShowDetail(row);
    }

    private void GridCellToolTipTextNeeded(object? sender, DataGridViewCellToolTipTextNeededEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0)
            return;
        string property = _grid.Columns[e.ColumnIndex].DataPropertyName;
        e.ToolTipText = property switch
        {
            nameof(ArtifactRecord.Path) => "双击：在 Windows 资源管理器中打开所在目录并选中该文件。",
            nameof(ArtifactRecord.Selected) => "双击：切换“选用/不选用”。",
            _ when IsEditableProperty(property) => "双击：编辑该属性。",
            _ => "单击只选择资料；文件事实字段保持只读。"
        };
    }

    private static bool IsEditableProperty(string property) =>
        property is
            nameof(ArtifactRecord.Role) or
            nameof(ArtifactRecord.ProcessingStage) or
            nameof(ArtifactRecord.VersionLabel) or
            nameof(ArtifactRecord.QualityStatus) or
            nameof(ArtifactRecord.Notes);

    private static Label Caption(string text, int left = 3) => new()
    {
        Text = text,
        AutoSize = true,
        Margin = new Padding(left, 9, 3, 3)
    };

    private static Button Button(string text, EventHandler handler, UiButtonRole role = UiButtonRole.Neutral) =>
        UiComponents.Button(text, handler, role);

    private void ClearFilters()
    {
        _updatingFilters = true;
        try
        {
            _textFilter.Clear();
            _roleFilter.SelectedIndex = 0;
            _extFilter.SelectedIndex = _extFilter.Items.Count > 0 ? 0 : -1;
            _missingOnly.Checked = false;
        }
        finally
        {
            _updatingFilters = false;
        }
    }

    internal string GetTextFilter() => _textFilter.Text.Trim();

    internal void ResetForProjectChange()
    {
        _filterDebounce.Stop();
        ClearFilters();
        _updatingScopeList = true;
        try { _workareaSearch.Clear(); }
        finally { _updatingScopeList = false; }
        SetExtensionOptions([], "");
        SetScopeItems([], "");
        ShowRows([], new ArtifactGridState("", "", nameof(ArtifactRecord.Path)));
        ClearDetail();
        _importResult.Text = "当前项目尚无本次归档结果。";
        _contentTabs.SelectedIndex = 0;
        SetRawImportHealth("原始资料导入：当前项目尚未读取，请选择已初始化项目后刷新台账。", healthy: true);
    }

    internal string GetRoleFilterValue() => UiText.SelectedValue(_roleFilter);
    internal string GetRoleFilterDisplay() => _roleFilter.Text;
    internal string GetExtensionFilterText() => _extFilter.Text;
    internal bool GetMissingOnly() => _missingOnly.Checked;
    internal string GetWorkareaSearchText() => _workareaSearch.Text.Trim();
    internal string GetSelectedScopeLabel() =>
        _scopeList.SelectedItem is ArtifactScopeItem item ? item.Label : "全部资料";
    internal string GetSelectedScopeKey() =>
        _scopeList.SelectedItem is ArtifactScopeItem item ? item.Key : "";
    internal ArtifactScopeItem? GetSelectedScopeItem() => _scopeList.SelectedItem as ArtifactScopeItem;

    internal void SetExtensionOptions(IReadOnlyList<string> extensions, string current)
    {
        _updatingFilters = true;
        try
        {
            _extFilter.BeginUpdate();
            _extFilter.Items.Clear();
            _extFilter.Items.Add("全部扩展名");
            foreach (string ext in extensions)
                _extFilter.Items.Add(ext);
            _extFilter.EndUpdate();

            if (string.IsNullOrWhiteSpace(current) ||
                current.Equals("全部扩展名", StringComparison.OrdinalIgnoreCase))
            {
                _extFilter.SelectedIndex = 0;
            }
            else
            {
                _extFilter.Text = current;
            }
        }
        finally
        {
            _updatingFilters = false;
        }
    }

    internal void SetScopeItems(IReadOnlyList<ArtifactScopeItem> items, string currentKey)
    {
        _updatingScopeList = true;
        try
        {
            _scopeList.BeginUpdate();
            _scopeList.Items.Clear();
            foreach (ArtifactScopeItem item in items)
                _scopeList.Items.Add(item);
            _scopeList.EndUpdate();

            int selected = 0;
            for (int i = 0; i < _scopeList.Items.Count; i++)
            {
                if (_scopeList.Items[i] is ArtifactScopeItem item &&
                    item.Key.Equals(currentKey, StringComparison.OrdinalIgnoreCase))
                {
                    selected = i;
                    break;
                }
            }
            _scopeList.SelectedIndex = _scopeList.Items.Count > 0 ? selected : -1;
        }
        finally
        {
            _updatingScopeList = false;
        }
    }

    internal ArtifactGridState CaptureGridState()
    {
        string selectedId =
            _grid.CurrentRow?.DataBoundItem is ArtifactRecord selected
                ? selected.ArtifactId
                : "";
        string firstId = "";
        try
        {
            int first = _grid.FirstDisplayedScrollingRowIndex;
            if (first >= 0 && first < _grid.Rows.Count &&
                _grid.Rows[first].DataBoundItem is ArtifactRecord firstRow)
            {
                firstId = firstRow.ArtifactId;
            }
        }
        catch (InvalidOperationException)
        {
            firstId = "";
        }

        string column =
            _grid.CurrentCell is not null && _grid.CurrentCell.ColumnIndex >= 0
                ? _grid.Columns[_grid.CurrentCell.ColumnIndex].DataPropertyName
                : nameof(ArtifactRecord.Path);
        return new ArtifactGridState(selectedId, firstId, column);
    }

    internal void ShowRows(IReadOnlyList<ArtifactRecord> desired, ArtifactGridState state)
    {
        _viewRows.RaiseListChangedEvents = false;
        try
        {
            _viewRows.Clear();
            foreach (ArtifactRecord row in desired)
                _viewRows.Add(row);
        }
        finally
        {
            _viewRows.RaiseListChangedEvents = true;
        }
        _bindingSource.ResetBindings(false);
        RestoreGridState(state);
    }

    private void RestoreGridState(ArtifactGridState state)
    {
        if (_grid.Rows.Count == 0)
            return;

        int selectedIndex = FindRowIndex(state.SelectedArtifactId);
        if (selectedIndex < 0)
            selectedIndex = 0;

        int columnIndex = _grid.Columns
            .Cast<DataGridViewColumn>()
            .FirstOrDefault(x =>
                x.DataPropertyName.Equals(state.ColumnProperty, StringComparison.OrdinalIgnoreCase))
            ?.Index ?? 0;
        _grid.CurrentCell = _grid.Rows[selectedIndex].Cells[columnIndex];
        _grid.Rows[selectedIndex].Selected = true;

        int firstIndex = FindRowIndex(state.FirstVisibleArtifactId);
        if (firstIndex >= 0)
        {
            try
            {
                _grid.FirstDisplayedScrollingRowIndex = firstIndex;
            }
            catch (InvalidOperationException)
            {
                // Grid may reject this briefly during layout.
            }
        }
    }

    private int FindRowIndex(string artifactId)
    {
        if (string.IsNullOrWhiteSpace(artifactId))
            return -1;
        for (int i = 0; i < _grid.Rows.Count; i++)
        {
            if (_grid.Rows[i].DataBoundItem is ArtifactRecord row &&
                row.ArtifactId.Equals(artifactId, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    internal bool TryGetCurrentArtifact(out ArtifactRecord? row)
    {
        row = _grid.CurrentRow?.DataBoundItem as ArtifactRecord;
        return row is not null;
    }

    internal ArtifactRecord GetCurrentArtifact() =>
        _grid.CurrentRow?.DataBoundItem as ArtifactRecord
        ?? throw new InvalidOperationException("请先选择一条资料记录。");

    internal void SetDetail(string title, string text)
    {
        _detailTitle.Text = title;
        _toolTip.SetToolTip(_detailTitle, title);
        _detailText.Text = text;
    }

    internal void ClearDetail()
    {
        _detailTitle.Text = "未选择资料";
        _toolTip.SetToolTip(_detailTitle, "");
        _detailText.Clear();
    }

    internal void SetRawImportHealth(string text, bool healthy)
    {
        _rawImportHealthLabel.Text = text;
        _toolTip.SetToolTip(_rawImportHealthLabel, text);
        _rawImportHealthLabel.ForeColor = healthy
            ? SystemColors.GrayText
            : UiTheme.Warning;
    }

    internal void ShowImportResult(string text, bool activate)
    {
        _importResult.Text = text;
        if (activate) _contentTabs.SelectedIndex = 1;
    }

    internal bool HasGridColumns() => _grid.Columns.Count > 0;

    internal void NormalizeLayout()
    {
        if (_outerSplit is not null &&
            !_outerSplit.IsDisposed &&
            _outerSplit.ClientSize.Width > 850)
        {
            _outerSplit.SplitterDistance = Math.Clamp(
                220,
                180,
                Math.Max(181, _outerSplit.ClientSize.Width - 560));
        }

        if (_centerDetailSplit is not null &&
            !_centerDetailSplit.IsDisposed)
        {
            bool compact = _centerDetailSplit.ClientSize.Width < 760;
            _centerDetailSplit.Panel2Collapsed = compact;
            if (!compact && _centerDetailSplit.ClientSize.Width > 700)
            {
                int detailWidth = Math.Clamp(
                    _centerDetailSplit.ClientSize.Width / 3,
                    300,
                    380);
                _centerDetailSplit.SplitterDistance = Math.Max(
                    360,
                    _centerDetailSplit.ClientSize.Width -
                    detailWidth -
                    _centerDetailSplit.SplitterWidth);
            }
        }
    }
}

internal sealed record ArtifactScopeItem(
    string Key,
    string Label,
    int Count,
    string WorkareaId = "",
    string WorkareaPath = "")
{
    public bool IsWorkarea => !string.IsNullOrWhiteSpace(WorkareaId);
    public override string ToString() => $"{Label}    {Count}";
}

internal sealed record ArtifactGridState(
    string SelectedArtifactId,
    string FirstVisibleArtifactId,
    string ColumnProperty);
