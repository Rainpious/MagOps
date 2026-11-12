namespace MagOps.Desktop;

internal sealed partial class MotherLineRestorationDialog
{
    private ToolStrip? _categoryNavigation;
    private Action? _refreshSelectionSummary;
    internal string? CurrentGroupId => (_groups.CurrentRow?.Tag as MagOps.Infrastructure.Services.MotherLineRestoreGroup)?.Id;

    internal void RestoreCurrentGroup(string? id)
    {
        if (id is null) return;
        var row = _groups.Rows.Cast<DataGridViewRow>().FirstOrDefault(r =>
            r.Tag is MagOps.Infrastructure.Services.MotherLineRestoreGroup group && group.Id == id);
        if (row is not null) _groups.CurrentCell = row.Cells["State"];
    }

    private void ConfigureCategoryNavigation()
    {
        if (_categoryNavigation is not null) return;
        var filters = (FlowLayoutPanel)_root.GetControlFromPosition(0, 1)!;
        _filter.Visible = false;
        var navigation = _categoryNavigation = new ToolStrip
        {
            Name = "MotherLineCategories", GripStyle = ToolStripGripStyle.Hidden,
            Dock = DockStyle.None, AutoSize = true, CanOverflow = true,
            RenderMode = ToolStripRenderMode.System, TabStop = true
        };
        void Button(string label, int index)
        {
            var item = new ToolStripButton(label) { Tag = new[] { index }, DisplayStyle = ToolStripItemDisplayStyle.Text };
            item.Click += (_, _) => _filter.SelectedIndex = index;
            navigation.Items.Add(item);
        }
        void Group(string label, int index, params (string Label, int Index)[] children)
        {
            var item = new ToolStripSplitButton(label) { Tag = children.Select(c => c.Index).Append(index).ToArray() };
            item.ButtonClick += (_, _) => _filter.SelectedIndex = index;
            foreach (var child in children)
                item.DropDownItems.Add(child.Label, null, (_, _) => _filter.SelectedIndex = child.Index);
            navigation.Items.Add(item);
        }
        Button("断开测线", 0);
        Button("连接碎段", 3);
        Group("特殊复核", 9, ("端部延伸", 2), ("重复覆盖", 4), ("竞争 / 冲突", 5), ("所有分组", 1));
        Button("未归属", 6);
        Group("已处理", 10, ("已应用", 7), ("已排除 / 撤销", 8));
        filters.Controls.Add(navigation);
        filters.Controls.SetChildIndex(navigation, 0);
        UpdateCategoryNavigation();
    }

    private void UpdateCategoryNavigation()
    {
        if (_categoryNavigation is null) return;
        foreach (ToolStripItem item in _categoryNavigation.Items)
        {
            bool active = item.Tag is int[] indices && indices.Contains(_filter.SelectedIndex);
            item.BackColor = active ? UiTheme.SurfaceMuted : SystemColors.Control;
            item.ForeColor = active ? UiTheme.TextPrimary : UiTheme.TextSecondary;
            if (item is ToolStripButton button) button.Checked = active;
            item.AccessibleName = item.Text + (active ? "（当前分类）" : "");
        }
    }
}
