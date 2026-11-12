using System.ComponentModel;
using MagOps.Core.Analysis;

namespace MagOps.Desktop;

internal sealed class ConcaveLineRepairDialog : Form
{
    private readonly ConcaveLineInspection _inspection;
    private readonly BindingList<DecisionRow> _rows = new();
    private readonly DataGridView _grid = new();
    private readonly HashSet<int> _sourceLineNumbers;
    private int _nextLineNumber;

    public ConcaveLineRepairDialog(
        string workareaLabel,
        ConcaveLineInspection inspection,
        IEnumerable<ConcaveLineRule> rememberedRules)
    {
        _inspection = inspection;
        _sourceLineNumbers = inspection.UsedLineNumbers.ToHashSet();
        _nextLineNumber = Math.Max(1, inspection.MaxPositiveLineNumber + 1);

        Dictionary<string, ConcaveLineRule> remembered = (rememberedRules ?? [])
            .GroupBy(x => x.SourceLine ?? "", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);

        foreach (ConcaveLineInspectionItem item in inspection.DisconnectedLines)
        {
            remembered.TryGetValue(item.SourceLine, out ConcaveLineRule? saved);
            _rows.Add(new DecisionRow
            {
                SourceLine = item.SourceLine,
                SourceLineNumber = item.SourceLineNumber,
                SegmentCount = item.SegmentCount,
                SegmentSummary = item.SegmentSummary,
                Gap = $"{item.GapDistance:0.###} {item.GapUnit}",
                Detection = item.SegmentsInferred ? "连续端点对推断" : "空 X/Y 分隔",
                Action = NormalizeAction(saved?.Action),
                FirstOutputLine = item.SourceLine,
                SecondOutputLine = saved?.SecondOutputLine?.Trim() ?? "",
                Notes = saved?.Notes ?? ""
            });
        }
        NormalizeRememberedSplitTargets();

        Text = "凹形断开测线：连接 / 分割";
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimizeBox = false;
        MaximizeBox = true;
        ShowInTaskbar = false;
        MinimumSize = new Size(1040, 640);
        Size = new Size(1280, 780);

        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12),
            Margin = Padding.Empty
        };
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        shell.Controls.Add(new Label
        {
            Text =
                $"当前工作工区：{workareaLabel}\r\n" +
                $"源表：{Path.GetFileName(inspection.SourcePath)}；" +
                $"原始测线 {inspection.SourceLineCount} 条；" +
                $"检测到断开测线 {inspection.DisconnectedLines.Count} 条。\r\n" +
                "每条断线独立选择‘连接为一条’或‘按断段分割’，同一工区、同一文件允许两种处理同时存在。" +
                " 来自 KML 自动生成的首次规则默认使用安全分割；临时版改为连接后需再次确认，可跨过凹口；跨界部分须单独核查，不代表有效覆盖。",
            AutoSize = true,
            MaximumSize = new Size(1200, 0),
            Padding = new Padding(2, 2, 2, 8)
        }, 0, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = Padding.Empty,
            Padding = new Padding(0, 0, 0, 7)
        };
        actions.Controls.Add(MakeButton("选中 → 连接为一条", (_, _) => SetSelectedAction(ConcaveLineActions.Connect)));
        actions.Controls.Add(MakeButton("选中 → 按断段分割", (_, _) => SetSelectedAction(ConcaveLineActions.Split)));
        actions.Controls.Add(MakeButton("全部连接", (_, _) => SetAllAction(ConcaveLineActions.Connect)));
        actions.Controls.Add(MakeButton("全部分割", (_, _) => SetAllAction(ConcaveLineActions.Split)));
        actions.Controls.Add(MakeButton("清除选中规则", (_, _) => SetSelectedAction("")));
        actions.Controls.Add(MakeButton("编辑首个追加编号", (_, _) => EditSelectedSecondLine()));
        actions.Controls.Add(MakeButton("重新分配分割编号", (_, _) => ReallocateAllSplitTargets()));
        shell.Controls.Add(actions, 0, 1);

        ConfigureGrid();
        shell.Controls.Add(_grid, 0, 2);

        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 8, 0, 0)
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(new Label
        {
            Text =
                "连接：保留该设计线所有断段中距离最远的两个外侧端点；本临时版允许已确认的直线跨越凹口，确认记录绑定端点及文件内容；修改端点后须重新确认。" +
                Environment.NewLine +
                "分割：按测线主轴从小坐标侧到大坐标侧统一所有断段顺序；第 1 断段保留原 L 号，第 2 断段使用指定的新 L 号，第 3 段及以后自动分配唯一 L 号。" +
                Environment.NewLine +
                "生成航线时会把第 2、3…侧断段分别放入独立安全分支，并在分支之间结束架次，避免蛇形连接重新跨越凹口。",
            AutoSize = true,
            MaximumSize = new Size(890, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 5, 12, 0)
        }, 0, 0);

        var confirm = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = true,
            AutoScroll = false,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = Padding.Empty
        };
        var ok = MakeButton("保存规则并生成", (_, _) =>
        {
            if (!ValidateBeforeApply()) return;
            DialogResult = DialogResult.OK;
            Close();
        });
        ok.MinimumSize = new Size(126, 36);
        var cancel = MakeButton("取消", (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        });
        cancel.MinimumSize = new Size(88, 36);
        confirm.Controls.Add(ok);
        confirm.Controls.Add(cancel);
        bottom.Controls.Add(confirm, 1, 0);
        shell.Controls.Add(bottom, 0, 3);

        Controls.Add(shell);
        AcceptButton = ok;
        CancelButton = cancel;

        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _rows.Count) return;
            DecisionRow row = _rows[e.RowIndex];
            string next = row.Action switch
            {
                ConcaveLineActions.Connect => ConcaveLineActions.Split,
                ConcaveLineActions.Split => ConcaveLineActions.Connect,
                _ => ConcaveLineActions.Connect
            };
            SetAction(row, next);
            RefreshGrid();
        };

        Shown += (_, _) =>
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            Size = new Size(
                Math.Min(1280, Math.Max(1040, area.Width - 80)),
                Math.Min(780, Math.Max(640, area.Height - 80)));
            RefreshGrid();
            UiLayout.Normalize(this);
        };
        DpiChanged += (_, _) => BeginInvoke((Action)(() => UiLayout.Normalize(this)));
    }

    public IReadOnlyList<ConcaveLineRule> GetRules() =>
        _rows.Select(row => new ConcaveLineRule
        {
            SourceLine = row.SourceLine,
            Action = row.Action,
            SecondOutputLine = row.Action.Equals(ConcaveLineActions.Split, StringComparison.OrdinalIgnoreCase)
                ? row.SecondOutputLine
                : "",
            Notes = row.Notes
        }).ToList();

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.ReadOnly = true;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeColumns = false;
        _grid.AllowUserToResizeRows = false;
        _grid.AllowUserToOrderColumns = false;
        _grid.MultiSelect = true;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.RowHeadersVisible = false;
        _grid.AutoGenerateColumns = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.BackgroundColor = SystemColors.Window;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.DisableResizing;
        _grid.RowTemplate.Height = 30;
        _grid.RowTemplate.Resizable = DataGridViewTriState.False;

        AddColumn(nameof(DecisionRow.SourceLine), "源测线", 65, 72);
        AddColumn(nameof(DecisionRow.SegmentSummary), "断段", 105, 105);
        AddColumn(nameof(DecisionRow.Gap), "断口距离", 90, 95);
        AddColumn(nameof(DecisionRow.Detection), "识别依据", 95, 105);
        AddColumn(nameof(DecisionRow.ActionDisplay), "处理方式", 115, 120);
        AddColumn(nameof(DecisionRow.FirstOutputLine), "第1输出线", 85, 90);
        AddColumn(nameof(DecisionRow.SecondOutputLine), "首个追加线", 85, 90);
        AddColumn(nameof(DecisionRow.Notes), "备注", 130, 120);
        _grid.DataSource = _rows;
    }

    private void AddColumn(string property, string title, float fillWeight, int minimumWidth)
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            DataPropertyName = property,
            HeaderText = title,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = fillWeight,
            MinimumWidth = minimumWidth,
            ReadOnly = true
        });
    }

    private void SetSelectedAction(string action)
    {
        if (_grid.SelectedRows.Count == 0)
        {
            MessageBox.Show(this, "请先选择一条或多条断开测线。", "凹形边界断线处理", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        foreach (DataGridViewRow gridRow in _grid.SelectedRows)
            if (gridRow.DataBoundItem is DecisionRow row) SetAction(row, action);
        RefreshGrid();
    }

    private void SetAllAction(string action)
    {
        foreach (DecisionRow row in _rows) SetAction(row, action);
        RefreshGrid();
    }

    private void SetAction(DecisionRow row, string action)
    {
        row.Action = action;
        if (action.Equals(ConcaveLineActions.Split, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsUsableSecondLine(row, row.SecondOutputLine, allowSameRow: true))
                row.SecondOutputLine = AllocateNextLine();
        }
        else
        {
            row.SecondOutputLine = "";
        }
    }

    private void NormalizeRememberedSplitTargets()
    {
        var reserved = _sourceLineNumbers.ToHashSet();
        foreach (DecisionRow row in _rows)
        {
            if (!row.Action.Equals(ConcaveLineActions.Split, StringComparison.OrdinalIgnoreCase))
            {
                row.SecondOutputLine = "";
                continue;
            }
            if (TryExactLine(row.SecondOutputLine, out int target) && !reserved.Contains(target))
            {
                reserved.Add(target);
                if (target < int.MaxValue) _nextLineNumber = Math.Max(_nextLineNumber, target + 1);
            }
            else
            {
                row.SecondOutputLine = "";
            }
        }
        foreach (DecisionRow row in _rows)
        {
            if (row.Action.Equals(ConcaveLineActions.Split, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(row.SecondOutputLine))
                row.SecondOutputLine = AllocateNextLine(reserved);
        }
    }

    private void ReallocateAllSplitTargets()
    {
        var reserved = _sourceLineNumbers.ToHashSet();
        _nextLineNumber = Math.Max(1, _inspection.MaxPositiveLineNumber + 1);
        foreach (DecisionRow row in _rows)
        {
            if (!row.Action.Equals(ConcaveLineActions.Split, StringComparison.OrdinalIgnoreCase))
            {
                row.SecondOutputLine = "";
                continue;
            }
            row.SecondOutputLine = AllocateNextLine(reserved);
        }
        RefreshGrid();
    }

    private string AllocateNextLine()
    {
        var reserved = _sourceLineNumbers.ToHashSet();
        foreach (DecisionRow row in _rows)
            if (TryExactLine(row.SecondOutputLine, out int existing)) reserved.Add(existing);
        return AllocateNextLine(reserved);
    }

    private string AllocateNextLine(HashSet<int> reserved)
    {
        while (reserved.Contains(_nextLineNumber))
        {
            if (_nextLineNumber == int.MaxValue) throw new InvalidOperationException("无法继续自动分配新的 L 线号。");
            _nextLineNumber++;
        }
        int allocated = _nextLineNumber;
        reserved.Add(allocated);
        if (_nextLineNumber < int.MaxValue) _nextLineNumber++;
        return ConcaveBoundaryLineService.CanonicalLine(allocated);
    }

    private void EditSelectedSecondLine()
    {
        if (_grid.SelectedRows.Count != 1 || _grid.SelectedRows[0].DataBoundItem is not DecisionRow row)
        {
            MessageBox.Show(this, "请只选择一条需要分割的测线。", "编辑首个追加编号", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!row.Action.Equals(ConcaveLineActions.Split, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "只有‘按断段分割’的测线需要首个追加编号。", "编辑首个追加编号", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var values = PromptDialog.Show(this, "编辑首个追加 L 线号",
        [
            new PromptField("line", "首个追加输出线号", row.SecondOutputLine, Required: true),
            new PromptField("notes", "备注", row.Notes)
        ], width: 600,
        note: "必须使用 L 加整数，例如 L77；不得与原始测线号或其它分割结果重复。若该测线有 3 个及以上断段，其余追加线号会继续自动分配。");
        if (values is null) return;

        string candidate = values["line"].Trim();
        if (!IsUsableSecondLine(row, candidate, allowSameRow: true))
        {
            MessageBox.Show(this, $"首个追加线号 {candidate} 不可用。", "线号不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        row.SecondOutputLine = ConcaveBoundaryLineService.CanonicalLine(
            ConcaveBoundaryLineService.ParseExactLineNumber(candidate));
        row.Notes = values["notes"].Trim();
        RefreshGrid();
    }

    private bool ValidateBeforeApply()
    {
        List<DecisionRow> unset = _rows.Where(row =>
            !row.Action.Equals(ConcaveLineActions.Connect, StringComparison.OrdinalIgnoreCase) &&
            !row.Action.Equals(ConcaveLineActions.Split, StringComparison.OrdinalIgnoreCase)).ToList();
        if (unset.Count > 0)
        {
            MessageBox.Show(
                this,
                $"仍有 {unset.Count} 条断开测线未指定处理方式。\r\n例如：{string.Join("、", unset.Take(10).Select(x => x.SourceLine))}",
                "请完成断线规则",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return false;
        }

        var reserved = _sourceLineNumbers.ToHashSet();
        foreach (DecisionRow row in _rows)
        {
            if (!row.Action.Equals(ConcaveLineActions.Split, StringComparison.OrdinalIgnoreCase)) continue;
            if (!TryExactLine(row.SecondOutputLine, out int target) || !reserved.Add(target))
            {
                MessageBox.Show(
                    this,
                    $"{row.SourceLine} 的首个追加线号‘{row.SecondOutputLine}’不可用。请重新分配或指定唯一 L 整数编号。",
                    "分割编号冲突",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return false;
            }
        }
        if (_rows.Any(row => row.Action.Equals(ConcaveLineActions.Connect, StringComparison.OrdinalIgnoreCase)) &&
            MessageBox.Show(this,
                "临时版：连接会以最外侧端点直连，可能穿出工区或跨越孔洞。连接部分不代表有效测区覆盖，请核对地形、障碍与飞行许可。是否按本次选择生成？",
                "确认人工连接范围", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return false;
        return true;
    }

    private bool IsUsableSecondLine(DecisionRow owner, string candidate, bool allowSameRow)
    {
        if (!TryExactLine(candidate, out int target)) return false;
        if (_sourceLineNumbers.Contains(target)) return false;
        foreach (DecisionRow row in _rows)
        {
            if (allowSameRow && ReferenceEquals(row, owner)) continue;
            if (row.Action.Equals(ConcaveLineActions.Split, StringComparison.OrdinalIgnoreCase) &&
                TryExactLine(row.SecondOutputLine, out int other) && other == target)
                return false;
        }
        return true;
    }

    private static bool TryExactLine(string text, out int lineNumber)
    {
        try
        {
            lineNumber = ConcaveBoundaryLineService.ParseExactLineNumber(text);
            return true;
        }
        catch
        {
            lineNumber = 0;
            return false;
        }
    }

    private static string NormalizeAction(string? value)
    {
        string action = (value ?? "").Trim().ToUpperInvariant();
        return action.Equals(ConcaveLineActions.Connect, StringComparison.OrdinalIgnoreCase) ||
               action.Equals(ConcaveLineActions.Split, StringComparison.OrdinalIgnoreCase)
            ? action
            : "";
    }

    private void RefreshGrid()
    {
        BindingContext? context = BindingContext;
        if (context is not null && context[_rows] is CurrencyManager manager)
            manager.Refresh();
        _grid.Invalidate();
    }

    private static Button MakeButton(string text, EventHandler click)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(0, 34),
            Padding = new Padding(7, 2, 7, 2),
            Margin = new Padding(3)
        };
        button.Click += click;
        return button;
    }

    private sealed class DecisionRow
    {
        public string SourceLine { get; set; } = "";
        public int SourceLineNumber { get; set; }
        public int SegmentCount { get; set; }
        public string SegmentSummary { get; set; } = "";
        public string Gap { get; set; } = "";
        public string Detection { get; set; } = "";
        public string Action { get; set; } = "";
        public string FirstOutputLine { get; set; } = "";
        public string SecondOutputLine { get; set; } = "";
        public string Notes { get; set; } = "";

        public string ActionDisplay => Action switch
        {
            ConcaveLineActions.Connect => "连接为同一条",
            ConcaveLineActions.Split => "按断段分割",
            _ => "未设置"
        };
    }
}
