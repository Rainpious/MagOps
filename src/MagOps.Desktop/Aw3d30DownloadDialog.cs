using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed class Aw3d30DownloadDialog : Form
{
    private readonly ComboBox _source = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly Label _description = new() { AutoSize = true, MaximumSize = new Size(690, 0) };
    private string SourceName => _source.SelectedItem as string ?? CopernicusDem.Glo30;
    private bool UsesJaxa => SourceName == CopernicusDem.Jaxa;
    private int Resolution => SourceName == CopernicusDem.Glo90 ? 90 : 30;
    private TerrainCoverageRequest Request => new((double)_west.Value, (double)_south.Value, (double)_east.Value, (double)_north.Value);
    private bool _loadedAccount;
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
        Text = "下载地形"; StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi; MinimumSize = new Size(760, 550); ClientSize = new Size(760, 550);
        var root = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        Controls.Add(root);
        root.Controls.Add(_description);
        root.Controls.Add(_inputs);
        _source.Items.AddRange([CopernicusDem.Glo30, CopernicusDem.Glo90, CopernicusDem.Jaxa]);
        AddRow("数据源", _source);
        var changeLibrary = new Button { Text = "更改…", AutoSize = true };
        changeLibrary.Click += (_, _) => { try { ChooseLibrary(); } catch (Exception ex) { _status.Text = ex.Message; } };
        AddRow("公共库", _libraryPath, changeLibrary);
        try { RefreshLibraryPath(); } catch (Exception ex) { _status.Text = ex.Message; }
        AddRow("账号", _user, new Label { Text = "密码", AutoSize = true }, _password);
        _inputs.Controls.Add(_automatic);
        var save = new Button { Text = "保存设置", AutoSize = true };
        save.Click += (_, _) => { try { SaveAccount(); _status.Text = "已保存默认数据源和自动下载设置。"; } catch (Exception ex) { _status.Text = ex.Message; } };
        _inputs.Controls.Add(save);
        AddRow("西经度", _west, new Label { Text = "东经度", AutoSize = true }, _east);
        AddRow("南纬度", _south, new Label { Text = "北纬度", AutoSize = true }, _north);
        _inputs.Controls.Add(_onlyMissing);
        var preview = new Button { Text = "预览瓦片", AutoSize = true };
        preview.Click += (_, _) => { try { _status.Text = "将下载：" + string.Join("、", UsesJaxa ? ReadPlan().Select(t => t.Id) : CopernicusDem.Plan(Request, Resolution).Select(t => t.Id)); } catch (Exception ex) { _status.Text = ex.Message; } };
        _inputs.Controls.Add(preview);
        var buttons = new FlowLayoutPanel { AutoSize = true }; buttons.Controls.AddRange([_download, _cancel]); root.Controls.Add(buttons); root.Controls.Add(_status);
        _source.SelectedIndexChanged += (_, _) =>
        {
            _user.Parent!.Visible = UsesJaxa;
            _onlyMissing.Visible = UsesJaxa;
            _description.Text = UsesJaxa ? Aw3d30DownloadService.Description + "\nhttps://earth.jaxa.jp/en/data/policy/"
                : SourceName + " · 匿名下载 · DSM 地表高程 · WGS84 / EGM2008 · 米\n" + CopernicusDem.Catalog;
            if (UsesJaxa && !_loadedAccount)
            {
                try
                {
                    var login = Aw3d30Credentials.Load();
                    if (login is not null) { _user.Text = login.UserName; _password.Text = login.Password; }
                    _loadedAccount = true;
                }
                catch (Exception ex) { _status.Text = ex.Message; }
            }
        };
        _status.Text = "输入 WGS84 经纬度范围。单次最多 25 个一度瓦片。";
        _source.SelectedIndex = 0;
        try
        {
            var settings = Aw3d30DownloadPreferences.Load();
            if (_source.Items.Contains(settings.DownloadSource)) _source.SelectedItem = settings.DownloadSource;
            _automatic.Checked = settings.DownloadMissingOnMatch;
        }
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
        if (UsesJaxa) Aw3d30Credentials.Save(new Aw3d30Login(_user.Text.Trim(), _password.Text));
        new Aw3d30DownloadPreferences { DownloadSource = SourceName, DownloadMissingOnMatch = _automatic.Checked }.Save();
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
            bool jaxa = UsesJaxa; int resolution = Resolution; string product = SourceName;
            var request = Request;
            var tiles = jaxa ? ReadPlan() : [];
            if (!jaxa) CopernicusDem.Plan(request, resolution);
            var login = jaxa ? new Aw3d30Login(_user.Text.Trim(), _password.Text) : null;
            var library = new TerrainLibraryService();
            if (string.IsNullOrWhiteSpace(library.Settings().DefaultDirectory) && !ChooseLibrary()) return;
            SaveAccount();
            _work = new CancellationTokenSource(); _inputs.Enabled = false; _download.Enabled = false; _cancel.Text = "取消下载";
            var progress = new Progress<string>(s => _status.Text = s);
            var token = _work.Token;
            bool missingOnly = _onlyMissing.Checked;
            var assets = await Task.Run(() => !jaxa ? CopernicusDem.EnsureAsync(request, resolution, progress, token) : missingOnly
                ? new Aw3d30TerrainProvider().EnsureAvailableAsync(request, progress, token)
                : new Aw3d30DownloadService().DownloadAsync(tiles, login!, progress, token), token);
            DownloadCompleted = true;
            _status.Text = $"公共库已具备 {assets.Count} 个所需 {product} 瓦片（含本地复用）；可关闭此窗口，再匹配或选择所需瓦片。";
        }
        catch (OperationCanceledException) { _status.Text = "下载已取消；完整登记的瓦片保留，当前未完成下载已清理。"; }
        catch (Exception ex) { _status.Text = "下载未完成：" + ex.Message; }
        finally { _work?.Dispose(); _work = null; _inputs.Enabled = true; _download.Enabled = true; _cancel.Text = "关闭"; }
        if (DownloadCompleted && StartDownloadOnShown) { DialogResult = DialogResult.OK; Close(); }
    }
}
