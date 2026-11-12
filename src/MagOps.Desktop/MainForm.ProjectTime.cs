using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private TimeZoneInfo _projectTimeZone = ProjectTime.Beijing;
    private readonly Button _projectTimeZoneButton = new() { AutoSize = true, Dock = DockStyle.Fill, Text = "北京时间（UTC+08:00） · 设置…" };
    private void EditProjectTimeZone()
    {
        if (!IsProjectInitialized()) throw new InvalidOperationException("请先选择已初始化项目。");
        using var dialog = new Form { Text = "项目时区", Size = new Size(620, 230), StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false };
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label { Text = "工区继承项目时区；作业时间显示和业务日报按此分组。原始时间、日变与 T0 的 UTC 计算保持不变。", AutoSize = true, MaximumSize = new Size(570, 0) }, 0, 0);
        var combo = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "DisplayName" };
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones()) combo.Items.Add(zone);
        combo.SelectedItem = combo.Items.Cast<TimeZoneInfo>().FirstOrDefault(z => z.Id == _projectTimeZone.Id)
            ?? combo.Items.Cast<TimeZoneInfo>().FirstOrDefault(z => z.BaseUtcOffset == _projectTimeZone.BaseUtcOffset && !z.SupportsDaylightSavingTime);
        root.Controls.Add(combo, 0, 1);
        root.Controls.Add(UiComponents.Button("保存项目时区", (_, _) =>
        {
            if (combo.SelectedItem is not TimeZoneInfo zone) return;
            _workspace.SetProjectTimeZone(Root, zone.Id); _projectTimeZone = zone;
            dialog.DialogResult = DialogResult.OK; dialog.Close();
        }, UiButtonRole.Primary), 0, 2);
        dialog.Controls.Add(root); UiTheme.Apply(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _fieldAutoAnalysisContextKey = ""; _fieldProjectTimelineContextKey = "";
        _fieldDailyAnalysis = null; _fieldDailyProgressRecord = null;
        if (_fieldMagRawAnalysis is not null) { ApplyFieldDataAnalysis(_fieldMagRawAnalysis); AnalyzeProjectFieldTimeline(force: true, quietNoRoute: true); }
        RefreshProject();
        SetStatus("项目时区已更新；业务日期需要按新时区重新生成，UTC 核验决定不受显示时区影响。");
    }
}
