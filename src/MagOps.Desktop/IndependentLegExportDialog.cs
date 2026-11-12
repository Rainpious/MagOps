using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

/// <summary>One explicit disposition for remaining selected, unidentified observations.</summary>
internal sealed class IndependentLegExportDialog : Form
{
    public string Command { get; private set; } = "REVIEW";
    public IndependentLegExportDialog(IReadOnlyList<FlightLeg> remaining, AuditProductionResult audit,
        MotherLineRestorationState state, FieldMagFolderAnalysis analysis)
    {
        Text = "导出前核对：仍有未归属航段"; Width = 1200; Height = 780; MinimumSize = new(1000, 680);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new(10) };
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Percent, 40)); root.RowStyles.Add(new(SizeType.Percent, 60)); root.RowStyles.Add(new(SizeType.AutoSize));
        var passIds = audit.Execution.Passes.Select(p => p.FlightLegId).ToHashSet();
        int included = audit.Execution.Legs.Count(l => passIds.Contains(l.FlightLegId) && audit.ManualIncludedFlightLegIds.Contains(l.FlightLegId));
        root.Controls.Add(new Label { AutoSize = true, MaximumSize = new(1100, 0), Text = $"已归属并显式纳入{included}段；以下{remaining.Count}个纳入段仍没有母线身份，会以独立u线号导出。请选择批量处理方式；返回整理为默认操作。业务数据不会因未归属自动丢弃。" }, 0, 0);
        var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, BackgroundColor = Color.White };
        grid.Columns.Add("Leg", "稳定航段"); grid.Columns.Add("Source", "来源记录"); grid.Columns.Add("Points", "观测数"); grid.Columns.Add("Reason", "未归属原因");
        grid.Columns[0].Width = 210; grid.Columns[1].Width = 240; grid.Columns[2].Width = 90; grid.Columns[3].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        foreach (var l in remaining)
        {
            var e = state.UnassignedEvidence.FirstOrDefault(e => e.LegId == l.FlightLegId);
            int row = grid.Rows.Add(l.FlightLegId, string.Join(";", l.Samples.GroupBy(p => p.SourceFile).Select(g => $"{g.Key}:{g.Min(p => p.SourceRecord)}–{g.Max(p => p.SourceRecord)}")), l.Samples.Count,
                e?.Reason ?? l.ReviewReason);
            grid.Rows[row].Tag = l;
        }
        root.Controls.Add(grid, 0, 1);
        var map = new TrackMapPanel { Dock = DockStyle.Fill }; root.Controls.Add(map, 0, 2);
        grid.SelectionChanged += (_, _) =>
        {
            var selected = grid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Tag).OfType<FlightLeg>().ToArray();
            map.SetData(analysis, audit.EffectiveTasks ?? [], audit.Execution, selected.FirstOrDefault(), selectedLegs: selected,
                acceptedLegIds: audit.ManualIncludedFlightLegIds.ToHashSet());
        };
        map.SetData(analysis, audit.EffectiveTasks ?? [], audit.Execution, remaining.FirstOrDefault(), selectedLegs: remaining,
            acceptedLegIds: audit.ManualIncludedFlightLegIds.ToHashSet());
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        void Add(string text, string command) { var b = new Button { Text = text, AutoSize = true }; b.Click += (_, _) => { Command = command; DialogResult = DialogResult.OK; Close(); }; actions.Controls.Add(b); }
        Add("返回母线整理", "REVIEW"); Add($"明确保留{remaining.Count}段为独立航段并导出", "KEEP"); Add($"将这{remaining.Count}段移出本次成果", "EXCLUDE");
        root.Controls.Add(actions, 0, 3); Controls.Add(root);
    }
}
