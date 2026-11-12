using System.Text.Json;

namespace MagOps.Desktop;

internal sealed partial class AutomaticSegmentationReviewDialog
{
    private TableLayoutPanel? _taskScopePanel;
    private readonly Label _taskScopeSummary = new() { AutoSize = true };
    private List<WindowDraft>? _taskScopeBackup;
    private bool _applyingTaskScope;
    private readonly Label _scopeBadge = new() { AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
    private static Button MenuButton(string title, params (string Text, Action Run)[] actions)
    {
        return UiComponents.Button(title, (sender, _) =>
        {
            var anchor = (Control)sender!;
            var menu = new ContextMenuStrip();
            foreach (var action in actions) menu.Items.Add(action.Text, null, (_, _) => action.Run());
            menu.Closed += (_, _) => DisposeMenuAfterClose(menu, anchor);
            menu.Show(anchor, new Point(0, anchor.Height));
        });
    }

    internal static void DisposeMenuAfterClose(ContextMenuStrip menu, Control owner)
    {
        // WinForms' modal-menu filter still references the active drop-down while
        // processing the mouse event that closes it. Dispose on the next UI turn.
        if (!owner.IsDisposed && owner.IsHandleCreated)
        {
            try
            {
                owner.BeginInvoke((Action)(() => { if (!menu.IsDisposed) menu.Dispose(); }));
                return;
            }
            catch (InvalidOperationException) { }
        }
        if (!menu.IsDisposed) menu.Dispose();
    }
    private readonly TextBox _legDetails = new() { Dock = DockStyle.Fill, ReadOnly = true,
        Multiline = true, ScrollBars = ScrollBars.Vertical, Text = "选择航段查看时间、方向、来源和识别依据。" };
    private static string ColumnPreferencesPath => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "MagOps", "review-columns.json");

    private void ToggleTaskScope(TableLayoutPanel root)
    {
        if (_taskScopePanel!.Visible) { CancelTaskScope(root); return; }
        _windowGrid.EndEdit();
        _taskScopeBackup = _windows.Select(w =>
        {
            var copy = new WindowDraft(w.Id, w.StartUtc, w.EndUtc) { TaskScopeSource = w.TaskScopeSource,
                SourceFiles = w.SourceFiles, PhysicalFlightId = w.PhysicalFlightId };
            copy.TaskIds.UnionWith(w.TaskIds); return copy;
        }).ToList();
        _taskScopeSummary.Text = $"{_windows.Count} 个飞行窗口 · " +
            (_windows.Any(w => w.TaskScopeSource == "MANUAL") ? "手动范围" : "自动范围");
        root.RowStyles[1].Height = 260;
        _taskScopePanel.Visible = true;
        NotifyWorkspaceStateChanged();
    }

    private void ApplyTaskScope(TableLayoutPanel root)
    {
        _windowGrid.EndEdit();
        try
        {
            foreach (DataGridViewRow row in _windowGrid.Rows)
                if (row.Tag is WindowDraft window)
                    (window.StartUtc, window.EndUtc) = ParseWindowTimes(row);
        }
        catch (FormatException ex) { _status.Text = ex.Message; return; }
        var previous = _audit;
        _applyingTaskScope = true;
        try { AnalyzeExecution(); }
        finally { _applyingTaskScope = false; }
        if (ReferenceEquals(previous, _audit)) return;
        _taskScopeBackup = null;
        _taskScopePanel!.Visible = false; root.RowStyles[1].Height = 0;
        NotifyWorkspaceStateChanged();
    }

    private void CancelTaskScope(TableLayoutPanel root)
    {
        _windowGrid.CancelEdit(); _taskList.CancelEdit();
        if (_taskScopeBackup is not null)
        { _windows.Clear(); _windows.AddRange(_taskScopeBackup); RefreshWindowGrid(); LoadTasksForSelectedWindow(); }
        _taskScopeBackup = null;
        _taskScopePanel!.Visible = false; root.RowStyles[1].Height = 0;
        NotifyWorkspaceStateChanged();
    }

    private void ShowColumnSettings(Control anchor)
    {
        var menu = new ContextMenuStrip();
        foreach (DataGridViewColumn column in _pendingLegGrid.Columns)
        {
            var item = new ToolStripMenuItem(column.HeaderText) { Checked = column.Visible, CheckOnClick = true };
            item.CheckedChanged += (_, _) =>
            {
                _pendingLegGrid.Columns[column.Name]!.Visible = item.Checked;
                _acceptedLegGrid.Columns[column.Name]!.Visible = item.Checked;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(ColumnPreferencesPath)!);
                    var preferences = _pendingLegGrid.Columns.Cast<DataGridViewColumn>()
                        .ToDictionary(c => c.Name, c => c.Visible);
                    preferences["_directionDefaultVisibleV2"] = true;
                    File.WriteAllText(ColumnPreferencesPath, JsonSerializer.Serialize(preferences));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { _status.Text = "显示设置仅在本次生效：" + ex.Message; }
            };
            menu.Items.Add(item);
        }
        menu.Closed += (_, _) => DisposeMenuAfterClose(menu, anchor);
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    private void RestoreColumnPreferences(DataGridView grid)
    {
        try
        {
            if (!File.Exists(ColumnPreferencesPath)) return;
            var columns = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(ColumnPreferencesPath));
            foreach (DataGridViewColumn column in grid.Columns)
                if (columns?.TryGetValue(column.Name, out bool visible) == true) column.Visible = visible;
            // Restore this important reference for preferences saved before the default changed.
            if (columns?.ContainsKey("_directionDefaultVisibleV2") != true)
                grid.Columns["Direction"]!.Visible = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private readonly Dictionary<MagOps.Core.Domain.FlightLeg, double> _legLengths = new(ReferenceEqualityComparer.Instance);
    private double LegLength(ReviewLegRow value)
    {
        if (_legLengths.TryGetValue(value.Leg, out double cached)) return cached;
        double distance = 0;
        for (int i = 1; i < value.Leg.Samples.Count; i++)
        {
            var a = value.Leg.Samples[i - 1]; var b = value.Leg.Samples[i];
            double x = (b.Longitude - a.Longitude) * 111320 * Math.Cos((a.Latitude + b.Latitude) * Math.PI / 360);
            double y = (b.Latitude - a.Latitude) * 111320;
            distance += Math.Sqrt(x * x + y * y);
        }
        _legLengths[value.Leg] = distance;
        return distance;
    }
}
