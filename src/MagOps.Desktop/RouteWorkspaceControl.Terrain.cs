using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed partial class RouteWorkspaceControl
{
    private Action? _dismissTerrain;
    internal bool TerrainPending => _dismissTerrain is not null;

    internal void RequestRouteTerrain(RouteTerrainCoverage coverage, string areaName, Action<TerrainSelection> resume)
    {
        _dismissTerrain?.Invoke();
        var previous = Controls.Cast<Control>().Where(c => c.Enabled).ToArray();
        var panel = new FlowLayoutPanel { Name = "RouteTerrainCompletion", Dock = DockStyle.Top, AutoSize = true,
            Padding = new Padding(12), BackColor = SystemColors.ControlLightLight, WrapContents = true };
        var model = new ComboBox { Name = "TerrainModel", DropDownStyle = ComboBoxStyle.DropDownList, Width = 168 };
        model.Items.AddRange([CopernicusDem.Glo30, CopernicusDem.Glo90]);
        model.SelectedIndex = 0;
        var extent = coverage.Extent;
        var status = new Label { AutoSize = true, MaximumSize = new Size(600, 0) };
        var start = UiComponents.Button("补齐并继续生成", (_, _) => { }, UiButtonRole.Primary);
        var cancel = UiComponents.Button("取消", (_, _) => _dismissTerrain?.Invoke());
        panel.Controls.AddRange([new Label { AutoSize = true, Text = $"工区 {areaName} · 地形覆盖不足" },
            model, start, cancel, status]);
        foreach (var control in previous) control.Enabled = false;
        Controls.Add(panel);
        panel.BringToFront();
        CancellationTokenSource? running = null;
        panel.Disposed += (_, _) => running?.Cancel();
        _dismissTerrain = () =>
        {
            running?.Cancel();
            _dismissTerrain = null;
            panel.Dispose();
            foreach (var control in previous) if (!control.IsDisposed) control.Enabled = true;
        };
        void ShowEstimate()
        {
            try
            {
                int resolution = model.SelectedIndex == 0 ? 30 : 90;
                int missing = CopernicusDem.MissingCount(coverage.DownloadExtent(resolution), resolution);
                status.Text = $"{extent.West:F5}～{extent.East:F5}°，{extent.South:F5}～{extent.North:F5}° · 缺 {missing} 幅，按航线逐点复核";
            }
            catch (Exception ex) { status.Text = ex.Message; }
        }
        model.SelectedIndexChanged += (_, _) => ShowEstimate();
        ShowEstimate();
        start.Click += async (_, _) =>
        {
            if (running is not null) return;
            using var work = new CancellationTokenSource();
            running = work;
            start.Enabled = model.Enabled = false;
            status.Text = "正在匹配、补片并核对沿线高程…";
            int resolution = model.SelectedIndex == 0 ? 30 : 90;
            var progress = new Progress<string>(s => { if (!panel.IsDisposed) status.Text = s; });
            try
            {
                var selection = await Task.Run(() => coverage.CompleteAsync(resolution, progress, work.Token), work.Token);
                if (work.IsCancellationRequested || panel.IsDisposed || IsDisposed) return;
                resume(selection);
                _dismissTerrain?.Invoke();
            }
            catch (Exception ex) { if (!panel.IsDisposed) status.Text = $"未完成：{ex.Message}"; }
            finally
            {
                running = null;
                if (!panel.IsDisposed) start.Enabled = model.Enabled = true;
            }
        };
    }
}
