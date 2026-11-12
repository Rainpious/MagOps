using System.Globalization;
using MagOps.Core.Diurnal;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

/// <summary>
/// Minimal v0.9 production editor for repeat-survey replacement rules.
/// It deliberately stays independent from the global workspace layout: the
/// rules are data contracts first, and this dialog is only their thin editor.
/// </summary>
internal sealed class MagneticRepeatReplacementDialog : Form
{
    private readonly string _sourceDirectory;
    private readonly string _sourceFingerprint;
    private readonly string _workareaPath;
    private readonly string _surveyLineSource;
    private readonly string _surveyLineFingerprint;
    private readonly MagneticSurveyLineSegmentationResult _segmentation;
    private readonly DiurnalCorrectionResult _correction;
    private readonly IReadOnlyList<MagneticSurveyPassCandidate> _catalog;
    private readonly List<MagneticRepeatReplacementRule> _rules;

    private readonly ComboBox _line = new();
    private readonly ComboBox _original = new();
    private readonly ComboBox _replacement = new();
    private readonly RadioButton _wholeLine = new();
    private readonly RadioButton _segment = new();
    private readonly NumericUpDown _startAlong = new();
    private readonly NumericUpDown _endAlong = new();
    private readonly TextBox _reason = new();
    private readonly Label _preview = new();
    private readonly DataGridView _grid = GridFactory.Create();
    private readonly Button _addOrUpdate = new();
    private string _editingRuleId = "";

    public MagneticRepeatReplacementDialog(
        string sourceDirectory,
        string sourceFingerprint,
        string workareaPath,
        string surveyLineSource,
        string surveyLineFingerprint,
        MagneticSurveyLineSegmentationResult segmentation,
        DiurnalCorrectionResult correction,
        IReadOnlyList<MagneticSurveyPassCandidate> catalog,
        IReadOnlyList<MagneticRepeatReplacementRule> existingRules)
    {
        _sourceDirectory = sourceDirectory;
        _sourceFingerprint = sourceFingerprint;
        _workareaPath = workareaPath;
        _surveyLineSource = surveyLineSource;
        _surveyLineFingerprint = surveyLineFingerprint;
        _segmentation = segmentation;
        _correction = correction;
        _catalog = catalog;
        _rules = existingRules.Select(CloneRule).ToList();

        Text = "重测段替换 / Oasis 生产数据选择";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScroll = true;
        MinimumSize = new Size(700, 450);
        Size = new Size(880, 620);

        BuildUi();
        PopulateLines();
        RefreshRuleGrid();
        UiTheme.Apply(this);
        UiLayout.FitFormToWorkingArea(this);
    }

    public IReadOnlyList<MagneticRepeatReplacementRule> Rules =>
        _rules.Select(CloneRule).ToArray();

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(12),
            Margin = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var help = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(1000, 0),
            ForeColor = UiTheme.TextSecondary,
            Text = "这里不删除原始数据。规则只决定最终 MagOps_日变磁测_处理用__MagOps_prelevel_dtgz.csv 使用哪一段观测：原测指定区段被屏蔽，后续重测同区段被选入；重测 pass 在未选区段会主动抑制，避免重复数据同时进入 Oasis。区段使用正式设计测线的 AlongM（沿线距离），不按 CSV 行号拼接。"
        };
        root.Controls.Add(help, 0, 0);

        var editor = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 6,
            RowCount = 5,
            Padding = new Padding(0, 10, 0, 10),
            Margin = Padding.Empty
        };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));

        ConfigureCombo(_line);
        ConfigureCombo(_original);
        ConfigureCombo(_replacement);
        _line.SelectedIndexChanged += (_, _) => PopulateOriginalPasses();
        _original.SelectedIndexChanged += (_, _) => PopulateReplacementPasses();
        _replacement.SelectedIndexChanged += (_, _) => SuggestRange();

        AddField(editor, 0, 0, "正式测线", _line, span: 1);
        AddField(editor, 2, 0, "原测 pass", _original, span: 1);
        AddField(editor, 4, 0, "重测 pass", _replacement, span: 1);

        _wholeLine.AutoSize = true;
        _wholeLine.Text = "整条测线";
        _wholeLine.Margin = new Padding(0, 7, 12, 3);
        _segment.AutoSize = true;
        _segment.Text = "指定 AlongM 区段";
        _segment.Checked = true;
        _segment.Margin = new Padding(0, 7, 12, 3);
        _wholeLine.CheckedChanged += (_, _) => UpdateRangeEnabled();
        _segment.CheckedChanged += (_, _) => UpdateRangeEnabled();
        var modes = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = Padding.Empty
        };
        modes.Controls.Add(_segment);
        modes.Controls.Add(_wholeLine);
        AddField(editor, 0, 1, "替换范围", modes, span: 3);

        ConfigureAlong(_startAlong);
        ConfigureAlong(_endAlong);
        AddField(editor, 0, 2, "起点 AlongM", _startAlong, span: 1);
        AddField(editor, 2, 2, "终点 AlongM", _endAlong, span: 1);

        _reason.Dock = DockStyle.Fill;
        _reason.PlaceholderText = "例如：原测 1250–1680 m 高度异常，采用 9 月 15 日复测段";
        AddField(editor, 0, 3, "替换原因", _reason, span: 5);

        _preview.Dock = DockStyle.Fill;
        _preview.AutoSize = true;
        _preview.ForeColor = UiTheme.TextSecondary;
        _preview.Padding = new Padding(0, 4, 0, 4);
        editor.Controls.Add(_preview, 0, 4);
        editor.SetColumnSpan(_preview, 4);

        var editorButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            Margin = Padding.Empty
        };
        _addOrUpdate.Text = "添加规则";
        ConfigureButton(_addOrUpdate, UiButtonRole.Primary);
        _addOrUpdate.Click += (_, _) => AddOrUpdateRule();
        var previewButton = new Button { Text = "预览检查" };
        ConfigureButton(previewButton, UiButtonRole.Subtle);
        previewButton.Click += (_, _) => PreviewCurrentRule(showMessage: true);
        var clearButton = new Button { Text = "取消编辑" };
        ConfigureButton(clearButton, UiButtonRole.Subtle);
        clearButton.Click += (_, _) => ClearEditor();
        editorButtons.Controls.Add(_addOrUpdate);
        editorButtons.Controls.Add(previewButton);
        editorButtons.Controls.Add(clearButton);
        editor.Controls.Add(editorButtons, 4, 4);
        editor.SetColumnSpan(editorButtons, 2);
        root.Controls.Add(editor, 0, 1);

        ConfigureGrid();
        root.Controls.Add(_grid, 0, 2);

        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            Padding = new Padding(0, 10, 0, 0),
            Margin = Padding.Empty
        };
        var save = new Button { Text = "保存并应用", DialogResult = DialogResult.None };
        ConfigureButton(save, UiButtonRole.Primary);
        save.Click += (_, _) => SaveAndClose();
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        ConfigureButton(cancel, UiButtonRole.Subtle);
        var edit = new Button { Text = "编辑选中规则" };
        ConfigureButton(edit, UiButtonRole.Subtle);
        edit.Click += (_, _) => LoadSelectedRule();
        var toggle = new Button { Text = "启用/停用选中" };
        ConfigureButton(toggle, UiButtonRole.Subtle);
        toggle.Click += (_, _) => ToggleSelectedRule();
        var delete = new Button { Text = "删除选中规则" };
        ConfigureButton(delete, UiButtonRole.Danger);
        delete.Click += (_, _) => DeleteSelectedRule();
        bottom.Controls.Add(save);
        bottom.Controls.Add(cancel);
        bottom.Controls.Add(delete);
        bottom.Controls.Add(toggle);
        bottom.Controls.Add(edit);
        root.Controls.Add(bottom, 0, 3);

        Controls.Add(root);
        CancelButton = cancel;
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = true;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.Columns.Clear();
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Enabled", HeaderText = "状态", FillWeight = 52 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Line", HeaderText = "测线", FillWeight = 75 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Range", HeaderText = "AlongM 范围", FillWeight = 95 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Original", HeaderText = "原测", FillWeight = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Replacement", HeaderText = "重测", FillWeight = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Coverage", HeaderText = "覆盖检查", FillWeight = 180 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Reason", HeaderText = "原因", FillWeight = 130 });
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
                LoadSelectedRule();
        };
    }

    private void PopulateLines()
    {
        string selected = _line.SelectedItem is LineChoice old ? old.LineTaskId : "";
        _line.BeginUpdate();
        _line.Items.Clear();
        foreach (LineChoice item in _catalog
                     .GroupBy(x => x.LineTaskId, StringComparer.OrdinalIgnoreCase)
                     .Select(g => new LineChoice(
                         g.Key,
                         g.Select(x => x.LineName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? g.Key,
                         g.Min(x => x.LineOrder)))
                     .OrderBy(x => x.LineOrder ?? int.MaxValue)
                     .ThenBy(x => x.LineName, StringComparer.OrdinalIgnoreCase))
        {
            _line.Items.Add(item);
        }
        _line.EndUpdate();

        if (_line.Items.Count == 0)
        {
            _preview.Text = "当前分线结果没有可用于重测替换的正式测线 pass。";
            _addOrUpdate.Enabled = false;
            return;
        }

        int index = FindLineIndex(selected);
        _line.SelectedIndex = index >= 0 ? index : 0;
    }

    private void PopulateOriginalPasses()
    {
        if (_line.SelectedItem is not LineChoice line)
            return;

        string selectedSource = (_original.SelectedItem as MagneticSurveyPassCandidate)?.SourceKey ?? "";
        int? selectedPass = (_original.SelectedItem as MagneticSurveyPassCandidate)?.LinePass;
        MagneticSurveyPassCandidate[] passes = _catalog
            .Where(x => x.LineTaskId.Equals(line.LineTaskId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.FirstTimeUtc)
            .ThenBy(x => x.LinePass)
            .ToArray();

        SetCandidates(_original, passes, selectedSource, selectedPass);
        PopulateReplacementPasses();
    }

    private void PopulateReplacementPasses()
    {
        if (_line.SelectedItem is not LineChoice line)
            return;
        MagneticSurveyPassCandidate? original = _original.SelectedItem as MagneticSurveyPassCandidate;
        string selectedSource = (_replacement.SelectedItem as MagneticSurveyPassCandidate)?.SourceKey ?? "";
        int? selectedPass = (_replacement.SelectedItem as MagneticSurveyPassCandidate)?.LinePass;

        MagneticSurveyPassCandidate[] replacements = _catalog
            .Where(x =>
                x.LineTaskId.Equals(line.LineTaskId, StringComparison.OrdinalIgnoreCase) &&
                (original is null || x.FirstTimeUtc > original.LastTimeUtc))
            .OrderBy(x => x.FirstTimeUtc)
            .ThenBy(x => x.LinePass)
            .ToArray();
        SetCandidates(_replacement, replacements, selectedSource, selectedPass);
        SuggestRange();
    }

    private static void SetCandidates(
        ComboBox combo,
        IReadOnlyList<MagneticSurveyPassCandidate> candidates,
        string selectedSource,
        int? selectedPass)
    {
        combo.BeginUpdate();
        combo.Items.Clear();
        foreach (MagneticSurveyPassCandidate item in candidates)
            combo.Items.Add(item);
        combo.EndUpdate();

        int selected = -1;
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is MagneticSurveyPassCandidate pass &&
                pass.SourceKey.Equals(selectedSource, StringComparison.OrdinalIgnoreCase) &&
                (!selectedPass.HasValue || pass.LinePass == selectedPass.Value))
            {
                selected = i;
                break;
            }
        }
        combo.SelectedIndex = selected >= 0 ? selected : combo.Items.Count > 0 ? 0 : -1;
    }

    private void SuggestRange()
    {
        if (_original.SelectedItem is not MagneticSurveyPassCandidate original ||
            _replacement.SelectedItem is not MagneticSurveyPassCandidate replacement)
        {
            _preview.Text = "请选择原测 pass 和时间更晚的重测 pass。";
            return;
        }

        double start = Math.Max(original.MinAlongM, replacement.MinAlongM);
        double end = Math.Min(original.MaxAlongM, replacement.MaxAlongM);
        if (end <= start)
        {
            _preview.Text = "原测与重测在 AlongM 上没有重叠范围，不能替换。";
            return;
        }

        if (string.IsNullOrWhiteSpace(_editingRuleId))
        {
            SetAlongValue(_startAlong, start);
            SetAlongValue(_endAlong, end);
        }
        _preview.Text = $"建议交集：{start:0.#}–{end:0.#} m；原测 {original.MinAlongM:0.#}–{original.MaxAlongM:0.#} m；重测 {replacement.MinAlongM:0.#}–{replacement.MaxAlongM:0.#} m。";
    }

    private void AddOrUpdateRule()
    {
        MagneticRepeatReplacementRule? rule = BuildRuleFromEditor();
        if (rule is null)
            return;

        MagneticRepeatReplacementRuleSummary summary = MagneticRepeatReplacementService.PreviewRule(
            _sourceDirectory,
            _segmentation,
            _correction,
            rule,
            _sourceFingerprint,
            _surveyLineFingerprint);
        if (!summary.CoverageValid)
        {
            MessageBox.Show(
                this,
                "当前规则未通过覆盖完整性检查，不能加入正式生产规则：\r\n\r\n" + summary.Status,
                "重测替换检查未通过",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        int existingIndex = _rules.FindIndex(x =>
            x.RuleId.Equals(_editingRuleId, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            rule.RuleId = _rules[existingIndex].RuleId;
            rule.CreatedAtUtc = _rules[existingIndex].CreatedAtUtc;
            rule.Enabled = _rules[existingIndex].Enabled;
            _rules[existingIndex] = rule;
        }
        else
        {
            _rules.Add(rule);
        }

        ClearEditor();
        RefreshRuleGrid();
    }

    private MagneticRepeatReplacementRule? BuildRuleFromEditor()
    {
        if (_original.SelectedItem is not MagneticSurveyPassCandidate original ||
            _replacement.SelectedItem is not MagneticSurveyPassCandidate replacement)
        {
            MessageBox.Show(
                this,
                "请选择同一正式测线的原测 pass 和后续重测 pass。",
                "缺少 pass",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return null;
        }

        string mode = _wholeLine.Checked
            ? MagneticRepeatReplacementModes.WholeLine
            : MagneticRepeatReplacementModes.Segment;
        double? start = mode == MagneticRepeatReplacementModes.Segment
            ? (double)_startAlong.Value
            : null;
        double? end = mode == MagneticRepeatReplacementModes.Segment
            ? (double)_endAlong.Value
            : null;
        if (start.HasValue && end.HasValue && end.Value <= start.Value)
        {
            MessageBox.Show(
                this,
                "终点 AlongM 必须大于起点 AlongM。",
                "区段无效",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return null;
        }

        return MagneticRepeatReplacementService.CreateRule(
            _workareaPath,
            original,
            replacement,
            mode,
            start,
            end,
            _reason.Text,
            _sourceFingerprint,
            _surveyLineSource,
            _surveyLineFingerprint);
    }

    private void PreviewCurrentRule(bool showMessage)
    {
        MagneticRepeatReplacementRule? rule = BuildRuleFromEditor();
        if (rule is null)
            return;

        MagneticRepeatReplacementRuleSummary summary = MagneticRepeatReplacementService.PreviewRule(
            _sourceDirectory,
            _segmentation,
            _correction,
            rule,
            _sourceFingerprint,
            _surveyLineFingerprint);
        string text = SummaryText(summary);
        _preview.Text = text;
        _preview.ForeColor = summary.CoverageValid ? UiTheme.Success : UiTheme.Danger;
        if (showMessage)
        {
            MessageBox.Show(
                this,
                text,
                summary.CoverageValid ? "覆盖检查通过" : "覆盖检查未通过",
                MessageBoxButtons.OK,
                summary.CoverageValid ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
    }

    private void RefreshRuleGrid()
    {
        string selectedId = SelectedRuleId();
        MagneticRepeatReplacementResult evaluation = MagneticRepeatReplacementService.Evaluate(
            _sourceDirectory,
            _segmentation,
            _correction,
            _rules,
            _sourceFingerprint,
            _surveyLineFingerprint,
            throwOnInvalid: false);
        Dictionary<string, MagneticRepeatReplacementRuleSummary> summaries = evaluation.Summaries
            .ToDictionary(x => x.RuleId, StringComparer.OrdinalIgnoreCase);

        _grid.Rows.Clear();
        foreach (MagneticRepeatReplacementRule rule in _rules
                     .OrderBy(x => x.LineName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.StartAlongM ?? double.MinValue)
                     .ThenBy(x => x.CreatedAtUtc))
        {
            summaries.TryGetValue(rule.RuleId, out MagneticRepeatReplacementRuleSummary? summary);
            string range = rule.RangeMode.Equals(MagneticRepeatReplacementModes.WholeLine, StringComparison.OrdinalIgnoreCase)
                ? "整线"
                : $"{rule.StartAlongM:0.#}–{rule.EndAlongM:0.#} m";
            string status = !rule.Enabled
                ? "停用"
                : summary is null
                    ? "待检查"
                    : summary.CoverageValid
                        ? "通过"
                        : "失败: " + summary.Status;
            int rowIndex = _grid.Rows.Add(
                rule.Enabled ? "启用" : "停用",
                string.IsNullOrWhiteSpace(rule.LineName) ? rule.LineTaskId : rule.LineName,
                range,
                $"Pass {rule.OriginalLinePass} / {rule.OriginalFirstUtc.ToLocalTime():MM-dd HH:mm}",
                $"Pass {rule.ReplacementLinePass} / {rule.ReplacementFirstUtc.ToLocalTime():MM-dd HH:mm}",
                status,
                rule.Reason);
            _grid.Rows[rowIndex].Tag = rule.RuleId;
            if (rule.Enabled && summary is not null && !summary.CoverageValid)
                _grid.Rows[rowIndex].DefaultCellStyle.ForeColor = UiTheme.Danger;
        }

        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            foreach (DataGridViewRow row in _grid.Rows)
            {
                if (row.Tag is string id && id.Equals(selectedId, StringComparison.OrdinalIgnoreCase))
                {
                    row.Selected = true;
                    _grid.CurrentCell = row.Cells[0];
                    break;
                }
            }
        }
    }

    private void LoadSelectedRule()
    {
        string id = SelectedRuleId();
        MagneticRepeatReplacementRule? rule = _rules.FirstOrDefault(x =>
            x.RuleId.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (rule is null)
            return;

        _editingRuleId = rule.RuleId;
        int lineIndex = FindLineIndex(rule.LineTaskId);
        if (lineIndex >= 0)
            _line.SelectedIndex = lineIndex;
        SelectPass(_original, rule.OriginalSourceKey, rule.OriginalLinePass, rule.OriginalFirstUtc, rule.OriginalLastUtc);
        PopulateReplacementPasses();
        SelectPass(_replacement, rule.ReplacementSourceKey, rule.ReplacementLinePass, rule.ReplacementFirstUtc, rule.ReplacementLastUtc);

        _wholeLine.Checked = rule.RangeMode.Equals(MagneticRepeatReplacementModes.WholeLine, StringComparison.OrdinalIgnoreCase);
        _segment.Checked = !_wholeLine.Checked;
        if (rule.StartAlongM.HasValue)
            SetAlongValue(_startAlong, rule.StartAlongM.Value);
        if (rule.EndAlongM.HasValue)
            SetAlongValue(_endAlong, rule.EndAlongM.Value);
        _reason.Text = rule.Reason;
        _addOrUpdate.Text = "更新规则";
        UpdateRangeEnabled();
        _preview.Text = "正在编辑现有规则。更新前可先执行“预览检查”。";
        _preview.ForeColor = UiTheme.TextSecondary;
    }

    private void ToggleSelectedRule()
    {
        string id = SelectedRuleId();
        MagneticRepeatReplacementRule? rule = _rules.FirstOrDefault(x =>
            x.RuleId.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (rule is null)
            return;
        rule.Enabled = !rule.Enabled;
        RefreshRuleGrid();
    }

    private void DeleteSelectedRule()
    {
        string id = SelectedRuleId();
        MagneticRepeatReplacementRule? rule = _rules.FirstOrDefault(x =>
            x.RuleId.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (rule is null)
            return;
        if (MessageBox.Show(
                this,
                $"删除 {rule.LineName} 的这条重测替换规则？\r\n原始磁测数据不会被删除。",
                "删除替换规则",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }
        _rules.Remove(rule);
        if (_editingRuleId.Equals(rule.RuleId, StringComparison.OrdinalIgnoreCase))
            ClearEditor();
        RefreshRuleGrid();
    }

    private void SaveAndClose()
    {
        try
        {
            MagneticRepeatReplacementResult evaluation = MagneticRepeatReplacementService.Evaluate(
                _sourceDirectory,
                _segmentation,
                _correction,
                _rules,
                _sourceFingerprint,
                _surveyLineFingerprint,
                throwOnInvalid: true);
            DialogResult confirm = MessageBox.Show(
                this,
                evaluation.ActiveRuleCount == 0
                    ? "当前没有启用的重测替换规则。保存后 Oasis CSV 将使用正常分线结果。"
                    : $"将保存 {evaluation.ActiveRuleCount} 条启用规则。正式导出时预计屏蔽原测 {evaluation.ExcludedOriginalRecords} 条、选入重测 {evaluation.SelectedReplacementRecords} 条，并在选区外抑制重测 {evaluation.SuppressedReplacementRecords} 条。\r\n\r\n原始/canonical/完整日变审计数据不会被修改。",
                "保存重测替换规则",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Information);
            if (confirm != DialogResult.OK)
                return;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ex.Message,
                "重测替换规则不能应用",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            RefreshRuleGrid();
        }
    }

    private void ClearEditor()
    {
        _editingRuleId = "";
        _reason.Clear();
        _segment.Checked = true;
        _addOrUpdate.Text = "添加规则";
        SuggestRange();
        _preview.ForeColor = UiTheme.TextSecondary;
    }

    private void UpdateRangeEnabled()
    {
        bool enabled = _segment.Checked;
        _startAlong.Enabled = enabled;
        _endAlong.Enabled = enabled;
    }

    private string SelectedRuleId() =>
        _grid.CurrentRow?.Tag as string ?? "";

    private int FindLineIndex(string lineTaskId)
    {
        for (int i = 0; i < _line.Items.Count; i++)
        {
            if (_line.Items[i] is LineChoice line &&
                line.LineTaskId.Equals(lineTaskId, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    private static void SelectPass(
        ComboBox combo,
        string sourceKey,
        int linePass,
        DateTimeOffset first,
        DateTimeOffset last)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is not MagneticSurveyPassCandidate pass)
                continue;
            bool source = pass.SourceKey.Equals(sourceKey, StringComparison.OrdinalIgnoreCase);
            bool passNo = pass.LinePass == linePass;
            bool time = pass.FirstTimeUtc <= last.AddSeconds(2) && first <= pass.LastTimeUtc.AddSeconds(2);
            if (source && (passNo || time))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    private static string SummaryText(MagneticRepeatReplacementRuleSummary summary)
    {
        string coverage = summary.CoverageValid ? "通过" : "未通过";
        string maxGap = summary.ReplacementMaxGapM.HasValue
            ? summary.ReplacementMaxGapM.Value.ToString("0.#", CultureInfo.InvariantCulture) + " m"
            : "不可用";
        string cross = summary.ReplacementMaxCrossTrackM.HasValue
            ? summary.ReplacementMaxCrossTrackM.Value.ToString("0.#", CultureInfo.InvariantCulture) + " m"
            : "不可用";
        return $"覆盖检查{coverage}：AlongM {summary.StartAlongM:0.#}–{summary.EndAlongM:0.#} m；原测有效 {summary.OriginalRecordsInRange} 条；重测有效 {summary.ReplacementRecordsInRange} 条；重测最大内部缺口 {maxGap}；最大横向偏线 {cross}。{summary.Status}";
    }

    private static MagneticRepeatReplacementRule CloneRule(MagneticRepeatReplacementRule x) =>
        new()
        {
            RuleId = x.RuleId,
            WorkareaPath = x.WorkareaPath,
            LineTaskId = x.LineTaskId,
            LineName = x.LineName,
            RangeMode = x.RangeMode,
            StartAlongM = x.StartAlongM,
            EndAlongM = x.EndAlongM,
            OriginalSourceKey = x.OriginalSourceKey,
            OriginalLinePass = x.OriginalLinePass,
            OriginalFirstUtc = x.OriginalFirstUtc,
            OriginalLastUtc = x.OriginalLastUtc,
            ReplacementSourceKey = x.ReplacementSourceKey,
            ReplacementLinePass = x.ReplacementLinePass,
            ReplacementFirstUtc = x.ReplacementFirstUtc,
            ReplacementLastUtc = x.ReplacementLastUtc,
            Reason = x.Reason,
            Enabled = x.Enabled,
            CreatedAtUtc = x.CreatedAtUtc,
            SourceFingerprint = x.SourceFingerprint,
            SurveyLineSource = x.SurveyLineSource,
            SurveyLineFingerprint = x.SurveyLineFingerprint
        };

    private static void ConfigureCombo(ComboBox combo)
    {
        combo.Dock = DockStyle.Fill;
        combo.DropDownStyle = ComboBoxStyle.DropDownList;
        combo.Margin = new Padding(0, 3, 8, 3);
    }

    private static void ConfigureAlong(NumericUpDown control)
    {
        control.Dock = DockStyle.Fill;
        control.Minimum = -10000000m;
        control.Maximum = 10000000m;
        control.DecimalPlaces = 1;
        control.Increment = 10m;
        control.ThousandsSeparator = true;
        control.Margin = new Padding(0, 3, 8, 3);
    }

    private static void SetAlongValue(NumericUpDown control, double value)
    {
        decimal converted;
        try
        {
            converted = Convert.ToDecimal(value);
        }
        catch
        {
            return;
        }
        control.Value = Math.Min(control.Maximum, Math.Max(control.Minimum, converted));
    }

    private static void AddField(
        TableLayoutPanel panel,
        int column,
        int row,
        string labelText,
        Control control,
        int span)
    {
        var label = new Label
        {
            Text = labelText,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 8, 8, 3)
        };
        panel.Controls.Add(label, column, row);
        panel.Controls.Add(control, column + 1, row);
        if (span > 1)
            panel.SetColumnSpan(control, span);
    }

    private static void ConfigureButton(Button button, UiButtonRole role)
    {
        button.AutoSize = true;
        button.MinimumSize = new Size(96, 34);
        button.Margin = new Padding(4, 2, 0, 2);
        UiComponents.SetButtonRole(button, role);
        UiTheme.ApplyButton(button, role);
    }

    private sealed record LineChoice(
        string LineTaskId,
        string LineName,
        int? LineOrder)
    {
        public override string ToString() =>
            string.IsNullOrWhiteSpace(LineName) || LineName.Equals(LineTaskId, StringComparison.OrdinalIgnoreCase)
                ? LineTaskId
                : $"{LineName} ({LineTaskId})";
    }
}
