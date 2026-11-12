using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed class Aw3d30DownloadDialog : Form
{
    private readonly NumericUpDown _west = Number(-180, 180), _east = Number(-180, 180), _south = Number(-90, 90), _north = Number(-90, 90);
    private readonly TextBox _user = new() { Width = 280 }, _password = new() { Width = 200, UseSystemPasswordChar = true };
    private readonly CheckBox _automatic = new() { Text = "自动下载缺失地形", AutoSize = true };
    private readonly CheckBox _onlyMissing = new() { Text = "仅补缺失", Checked = true, AutoSize = true };
    private readonly TextBox _libraryPath = new() { Width = 500, ReadOnly = true, AccessibleName = "公共库路径", PlaceholderText = "未设置", ShortcutsEnabled = true };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(680, 0) };
    private readonly Button _download = new() { Text = "下载", AutoSize = true };
    private readonly Button _cancel = new() { Text = "关闭", AutoSize = true };
    private readonly FlowLayoutPanel _inputs = new() { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
    private CancellationTokenSource? _work;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool StartDownloadOnShown { get; set; }
    internal bool DownloadCompleted { get; private set; }
    private static NumericUpDown Number(int min, int max) => new() { Minimum = min, Maximum = max, DecimalPlaces = 5, Increment = .01m, Width = 125 };
    public Aw3d30DownloadDialog(double? west = null, double? south = null, double? east = null, double? north = null)
    {
        Text = "AW3D30 地形"; StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi; MinimumSize = new Size(760, 490); ClientSize = new Size(760, 490);
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        Controls.Add(root);
        root.Controls.Add(new Label { Text = Aw3d30DownloadService.Description + "\n下载不自动替换当前工区地形；完成后使用“按当前范围匹配瓦片”。", AutoSize = true, MaximumSize = new Size(690, 0) });
        root.Controls.Add(new TextBox { Text = "来源与条款：https://earth.jaxa.jp/en/data/policy/", ReadOnly = true, Width = 680, BorderStyle = BorderStyle.None });
        root.Controls.Add(_inputs);
        var changeLibrary = new Button { Text = "更改…", AutoSize = true };
        changeLibrary.Click += (_, _) => { try { ChooseLibrary(); } catch (Exception ex) { _status.Text = ex.Message; } };
        AddRow("公共库", _libraryPath, changeLibrary);
        try { RefreshLibraryPath(); } catch (Exception ex) { _status.Text = ex.Message; }
        AddRow("账号", _user, new Label { Text = "密码", AutoSize = true }, _password);
        _inputs.Controls.Add(_automatic);
        var save = new Button { Text = "保存设置", AutoSize = true };
        save.Click += (_, _) => { try { SaveAccount(); _status.Text = "账号与设置已保存在当前 Windows 用户下；下次打开会自动读取。尚未下载数据或验证账号。"; } catch (Exception ex) { _status.Text = ex.Message; } };
        _inputs.Controls.Add(save);
        AddRow("西经度", _west, new Label { Text = "东经度", AutoSize = true }, _east);
        AddRow("南纬度", _south, new Label { Text = "北纬度", AutoSize = true }, _north);
        _inputs.Controls.Add(_onlyMissing);
        var preview = new Button { Text = "预览瓦片", AutoSize = true };
        preview.Click += (_, _) => { try { _status.Text = "将下载：" + string.Join("、", ReadPlan().Select(t => t.Id)); } catch (Exception ex) { _status.Text = ex.Message; } };
        _inputs.Controls.Add(preview);
        var buttons = new FlowLayoutPanel { AutoSize = true }; buttons.Controls.AddRange([_download, _cancel]); root.Controls.Add(buttons); root.Controls.Add(_status);
        _status.Text = "输入 WGS84 经纬度范围；单次最多 25 个一度瓦片。账号仅保存在本机当前 Windows 用户下。";
        try { var login = Aw3d30Credentials.Load(); if (login is not null) { _user.Text = login.UserName; _password.Text = login.Password; } _automatic.Checked = Aw3d30DownloadPreferences.Load().DownloadMissingOnMatch; }
        catch (Exception ex) { _status.Text = ex.Message; }
        if (west.HasValue && south.HasValue && east.HasValue && north.HasValue)
        {
            _west.Value = Math.Clamp((decimal)west.Value, -180, 180); _east.Value = Math.Clamp((decimal)east.Value, -180, 180);
            _south.Value = Math.Clamp((decimal)south.Value, -90, 90); _north.Value = Math.Clamp((decimal)north.Value, -90, 90);
        }
        _download.Click += async (_, _) => await Download();
        _cancel.Click += (_, _) => { if (_work is null) Close(); else _work.Cancel(); };
        FormClosing += (_, e) => { if (_work is not null) { e.Cancel = true; _work.Cancel(); } };
        Shown += async (_, _) => { if (StartDownloadOnShown) await Download(); };
    }
    private void RefreshLibraryPath()
    {
        _libraryPath.Text = new TerrainLibraryService().Settings().DefaultDirectory;
        _libraryPath.Select(0, 0);
    }
    private bool ChooseLibrary()
    {
        var library = new TerrainLibraryService();
        using var folder = new FolderBrowserDialog { Description = "选择公共地形库（不会移动原库文件）", ShowNewFolderButton = true };
        var history = FileInteractions.For(this, "terrain-library", projectLevel: true, global: true);
        history.Prepare(folder, current: library.Settings().DefaultDirectory);
        if (folder.ShowDialog(this) != DialogResult.OK) return false;
        if (File.Exists(TerrainLibraryService.IndexPath(folder.SelectedPath))) library.Reconnect(folder.SelectedPath);
        else library.SetDefault(folder.SelectedPath);
        history.DirectorySucceeded(folder.SelectedPath);
        RefreshLibraryPath();
        _status.Text = "公共库已更改。原库文件和已有工区引用保留。";
        return true;
    }
    private void SaveAccount()
    {
        Aw3d30Credentials.Save(new Aw3d30Login(_user.Text.Trim(), _password.Text));
        new Aw3d30DownloadPreferences { DownloadMissingOnMatch = _automatic.Checked }.Save();
    }
    private void AddRow(string label, params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        row.Controls.Add(new Label { Text = label, Width = 65, Padding = new Padding(0, 6, 0, 0) }); row.Controls.AddRange(controls); _inputs.Controls.Add(row);
    }
    private IReadOnlyList<Aw3d30Tile> ReadPlan() => Aw3d30DownloadService.Plan((double)_west.Value, (double)_south.Value, (double)_east.Value, (double)_north.Value);
    private async Task Download()
    {
        try
        {
            DownloadCompleted = false;
            var tiles = ReadPlan(); var login = new Aw3d30Login(_user.Text.Trim(), _password.Text);
            var library = new TerrainLibraryService();
            if (string.IsNullOrWhiteSpace(library.Settings().DefaultDirectory) && !ChooseLibrary()) return;
            SaveAccount();
            _work = new CancellationTokenSource(); _inputs.Enabled = false; _download.Enabled = false; _cancel.Text = "取消下载";
            var progress = new Progress<string>(s => _status.Text = s);
            var token = _work.Token;
            bool missingOnly = _onlyMissing.Checked;
            var request = new TerrainCoverageRequest((double)_west.Value, (double)_south.Value, (double)_east.Value, (double)_north.Value);
            var assets = await Task.Run(() => missingOnly
                ? new Aw3d30TerrainProvider().EnsureAvailableAsync(request, progress, token)
                : new Aw3d30DownloadService().DownloadAsync(tiles, login, progress, token), token);
            DownloadCompleted = true;
            _status.Text = $"公共库已具备 {assets.Count} 个所需 AW3D30 瓦片（含本地复用）；可关闭此窗口，再匹配或选择所需瓦片。";
        }
        catch (OperationCanceledException) { _status.Text = "下载已取消；完整登记的瓦片保留，当前未完成下载已清理。"; }
        catch (Exception ex) { _status.Text = "下载未完成：" + ex.Message; }
        finally { _work?.Dispose(); _work = null; _inputs.Enabled = true; _download.Enabled = true; _cancel.Text = "关闭"; }
        if (DownloadCompleted && StartDownloadOnShown) { DialogResult = DialogResult.OK; Close(); }
    }
}
