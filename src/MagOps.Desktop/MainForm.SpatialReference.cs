using System.Globalization;
using MagOps.Core.Domain;

namespace MagOps.Desktop;

public sealed partial class MainForm
{
    private void RefreshGlobalSpatialReference(WorkspaceRecord workspace)
    {
        string value = workspace.CentralMeridian is { } cm ? cm.ToString("0.########", CultureInfo.InvariantCulture) + "°" : "未确认";
        _globalCentralMeridian.Text = "中央经线：" + value + " · 设置…";
        _centralMeridianStatus.Text = "项目中央经线：" + value;
        if (workspace.CentralMeridian is { } confirmed)
        {
            string text = confirmed.ToString("0.########", CultureInfo.InvariantCulture);
            _routeWorkspace.SetCentralMeridianText(text);
            if (_coordinateWorkspace.GetProjectionKind() == MagOps.Core.Coordinates.CoordinateProjectionKind.GaussKruger)
            {
                _coordinateWorkspace.SetCentralMeridianText(text);
                _coordinateWorkspace.SetAutoMeridian(false);
            }
        }
        else
        {
            _routeWorkspace.SetCentralMeridianText("");
            if (_coordinateWorkspace.GetProjectionKind() == MagOps.Core.Coordinates.CoordinateProjectionKind.GaussKruger)
            {
                _coordinateWorkspace.SetCentralMeridianText("");
                _coordinateWorkspace.SetAutoMeridian(true);
            }
        }
    }

    private void EditProjectCentralMeridian()
    {
        if (!IsProjectInitialized()) throw new InvalidOperationException("请先打开已初始化的项目。");
        var workspace = _workspace.GetWorkspace(Root);
        using var dialog = new Form { Text = "项目中央经线", Size = new Size(500, 260), StartPosition = FormStartPosition.CenterParent, ShowInTaskbar = false };
        var value = new NumericUpDown { Minimum = -180, Maximum = 180, DecimalPlaces = 6, Width = 160, Value = (decimal)(workspace.CentralMeridian ?? 0) };
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        layout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(430, 0), Text = "设置项目空间基准（度）。更改后坐标与航线需要重新计算，已有分线核验和派生成果需复核；原始数据与历史文件保留。" });
        layout.Controls.Add(value);
        layout.Controls.Add(UiComponents.Button("应用项目中央经线", (_, _) => { dialog.DialogResult = DialogResult.OK; dialog.Close(); }));
        dialog.Controls.Add(layout); UiTheme.Apply(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        double cm = (double)value.Value;
        if (workspace.CentralMeridian is { } existing && Math.Abs(existing - cm) < 1e-9) return;
        if (!ConfirmContextSwitch("修改项目中央经线并重新计算坐标与航线")) return;
        ApplyProjectCentralMeridian(cm, "PROJECT_HEADER_CONFIRMED");
        SetStatus("项目中央经线已更新；当前坐标和航线预览已清空，请重算并复核旧分线与派生成果。");
    }

    // Header changes and coordinate discrepancy corrections share invalidation.
    private WorkspaceRecord ApplyProjectCentralMeridian(double cm, string source)
    {
        var updated = _workspace.ConfirmProjectCentralMeridian(Root, cm, source);
        _lastCoordinateBatch = null;
        _lastCoordinateProjection = null;
        _coordinateWorkspace.ShowConversionResults([], []);
        _routePlan = null;
        _routeInference = null;
        _routeLoadedFingerprint = "";
        ClearPartitionRouteWorkspace();
        _routeSubdivisionPreparation = null;
        _routeSubdivisionPreparedFingerprint = "";
        _routeWorkspace.ClearSubdivisionPreview();
        _routeWorkspace.ClearRoutePreview();
        if (_fieldSpatialScreening is { } screening)
            _fieldSpatialScreening = screening with { Range = null, ContextFingerprint = "", RouteFingerprint = "" };
        _fieldAutoAnalysisContextKey = "";
        SaveFieldOperationsSettings();
        RefreshGlobalSpatialReference(updated);
        RefreshProject();
        RefreshArtifactFreshnessStatus();
        return updated;
    }

    private void RefreshArtifactFreshnessStatus()
    {
        IReadOnlyList<ArtifactRecord> changed = _artifacts.RefreshFreshnessStatus(Root);
        if (changed.Count > 0 && _artifactWorkspace.HasGridColumns())
            ApplyArtifactChanges(changed);
    }
}
