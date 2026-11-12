using System.Text.Json;
using System.Security.Cryptography;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

/// <summary>Candidate review only. Never rewrites a production CSV or its audit decisions.</summary>
internal sealed class MotherLineProductionWorkspace : UserControl
{
    internal event Action<string>? CandidateSaved;
    private readonly Label _summary = new() { Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly DataGridView _runs = new() { Dock = DockStyle.Fill, ReadOnly = true,
        AllowUserToAddRows = false, AllowUserToDeleteRows = false, AutoGenerateColumns = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false };
    private readonly TextBox _detail = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both };
    private readonly MotherLineProductionPreview _map = new();
    private readonly Button _build = new() { Text = "载入分区清单并生成候选", AutoSize = true };
    private readonly Button _save = new() { Text = "保存候选快照…", AutoSize = true, Enabled = false };
    private readonly Button _exportCsv = new() { Text = "导出母线 CSV 候选…", AutoSize = true, Enabled = false };
    private readonly Button _import = new() { Text = "加入其他子区快照…", AutoSize = true, Enabled = false };
    private readonly Button _importCsv = new() { Text = "导入已有分线 CSV…", AutoSize = true };
    internal Func<(string Project, string Workarea)>? ResolveImportContext;
    private readonly Button _resetImports = new() { Text = "仅保留首个输入", AutoSize = true, Enabled = false };
    private readonly Button _clear = new() { Text = "清空候选", AutoSize = true, Enabled = false };
    private readonly Button _cancel = new() { Text = "取消当前任务", AutoSize = true, Visible = false };
    private readonly Label _activity = new() { AutoSize = true, MaximumSize = new Size(600, 0), AccessibleName = "母线候选后台任务状态" };
    private CancellationTokenSource? _operationCancellation;
    private bool _busy;
    private int _contextVersion;
    private FieldMagFolderAnalysis? _analysis;
    private DiurnalCorrectionExportResult? _export;
    private string _projectId = "", _workareaId = "";
    private PartitionMotherLineIdentityCatalog? _catalog;
    private MotherLineProductionBatch? _batch;
    private IReadOnlyList<MotherLineProductionBatch> _batches = [];
    private MotherLineProductionAssembly? _assembly;

    public MotherLineProductionWorkspace()
    {
        Dock = DockStyle.Fill;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = UiTheme.PagePadding };
        root.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.AutoSize)); root.RowStyles.Add(new(SizeType.Absolute, 65));
        root.RowStyles.Add(new(SizeType.Percent, 40)); root.RowStyles.Add(new(SizeType.Percent, 40));
        root.RowStyles.Add(new(SizeType.Percent, 20));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        toolbar.Controls.Add(_importCsv); toolbar.Controls.Add(_build); toolbar.Controls.Add(_import); toolbar.Controls.Add(_resetImports); toolbar.Controls.Add(_clear); toolbar.Controls.Add(_save); toolbar.Controls.Add(_exportCsv);
        _clear.Click += (_, _) => { ClearCandidate(); _summary.Text = "候选已清空，可重新导入和绑定；原文件未改动。"; };
        _importCsv.Click += async (_, _) => await ChooseProcessingCsvAsync();
        toolbar.Controls.Add(_cancel); toolbar.Controls.Add(_activity);
        _cancel.Click += (_, _) => { _operationCancellation?.Cancel(); _activity.Text = "正在取消，保留当前候选…"; };
        toolbar.Controls.Add(UiComponents.HelpLabel("候选核验：蓝=正向，橙=已整段反序，紫=方向未定；箭头指母线点序方向，黑色描边为所选段。滚轮缩放／中键平移／Home 全图。"));
        root.Controls.Add(toolbar, 0, 0); root.Controls.Add(_summary, 0, 1);
        foreach (var (name, text, width) in new[] { ("Line", "母线", 90), ("Order", "母线顺序", 85),
            ("Partition", "子区", 70), ("Direction", "方向处理", 110), ("Count", "点数", 70),
            ("Purpose", "用途", 90), ("Gap", "原LinePass", 80), ("Pass", "来源段 / Pass", 200) })
            _runs.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = name, HeaderText = text, Width = width });
        _runs.AccessibleName = "母线候选子段与方向"; _detail.AccessibleName = "母线候选来源详情";
        root.Controls.Add(_runs, 0, 2); root.Controls.Add(_map, 0, 3); root.Controls.Add(_detail, 0, 4);
        Controls.Add(root);
        _runs.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _runs.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        _runs.SelectionChanged += (_, _) => SelectRun();
        _import.Click += async (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "母线候选输入 JSON|*.json", Multiselect = true, Title = "加入同项目、工区及设计的子区快照" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            await ImportCandidatesAsync(dialog.FileNames);
        };
        _resetImports.Click += (_, _) =>
        {
            if (_batch is not null && _catalog is not null)
                ApplyAssembly(_catalog, [_batch], MotherLineProductionAssemblyService.Build(_projectId, _workareaId, _catalog, [_batch]));
        };
        _build.Click += async (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "分区清单 JSON|*.json", Title = "选择当前设计的分区清单" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            await BuildCandidateAsync(dialog.FileName);
        };
        _save.Click += async (_, _) =>
        {
            using var dialog = new SaveFileDialog { Filter = "母线候选输入 JSON|*.json", FileName = "母线候选快照.json", OverwritePrompt = false };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            await SaveCandidateAsync(dialog.FileName);
        };
        _exportCsv.Click += async (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "选择保存母线 CSV 候选的现有成果目录", ShowNewFolderButton = false };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            await ExportCsvCandidateAsync(dialog.SelectedPath);
        };
        ClearSnapshot();
    }

    internal void SetSnapshot(string projectId, string workareaId, FieldMagFolderAnalysis analysis, DiurnalCorrectionExportResult export)
    {
        ClearSnapshot(); _projectId = projectId; _workareaId = workareaId; _analysis = analysis; _export = export;
        _build.Enabled = !_busy && export.FinalExportedRows.Count > 0;
        _summary.Text = $"本次实际导出 {export.FinalExportedRows.Count:N0} 点。载入同设计分区清单后生成候选；不改原 CSV。";
    }

    internal void ClearSnapshot()
    {
        _contextVersion++; _operationCancellation?.Cancel(); _activity.Text = "";
        _analysis = null; _export = null; _projectId = _workareaId = ""; ClearCandidate(); _build.Enabled = false;
        _summary.Text = "可直接导入已有分线处理 CSV；也可使用本次生成 CSV 的快照。无需重跑分线或改正。";
    }

    private async Task RunOperationAsync<T>(string activity, Func<CancellationToken, T> work, Action<T> apply,
        bool acceptCommittedResult = false)
    {
        if (_busy) return;
        int version = _contextVersion;
        using var cancellation = new CancellationTokenSource();
        _operationCancellation = cancellation; _busy = true;
        _clear.Enabled = _importCsv.Enabled = _build.Enabled = _import.Enabled = _resetImports.Enabled = _save.Enabled = _exportCsv.Enabled = false;
        _cancel.Visible = true; _activity.Text = activity;
        try
        {
            var result = await Task.Run(() => work(cancellation.Token));
            if (!IsDisposed && version == _contextVersion && (!cancellation.IsCancellationRequested || acceptCommittedResult))
            { apply(result); _activity.Text = "已完成"; }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { if (!IsDisposed && version == _contextVersion) _activity.Text = "已取消，保留原候选。"; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException or FormatException)
        { if (!IsDisposed && version == _contextVersion) _activity.Text = "未完成，保留原候选：" + ex.Message; }
        finally
        {
            _operationCancellation = null; _busy = false;
            if (!IsDisposed)
            {
                _cancel.Visible = false;
                _importCsv.Enabled = true;
                _build.Enabled = _export?.FinalExportedRows.Count > 0;
                _import.Enabled = _save.Enabled = _assembly is not null;
                _clear.Enabled = _assembly is not null;
                _exportCsv.Enabled = _assembly is not null;
                _resetImports.Enabled = _batches.Count > 1;
            }
        }
    }

    private async Task ChooseProcessingCsvAsync()
    {
        (string, string) context;
        try { context = ResolveImportContext?.Invoke() ?? (_projectId, _workareaId); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        { _activity.Text = "无法读取项目/工区，请先打开有效项目：" + ex.Message; return; }
        if (string.IsNullOrWhiteSpace(context.Item1) || string.IsNullOrWhiteSpace(context.Item2))
        { _activity.Text = "请先打开已登记的项目和工区。"; return; }
        using var csv = new OpenFileDialog { Filter = "MagOps处理 CSV|*.csv", Title = "选择已有分线处理成果" };
        if (csv.ShowDialog(this) != DialogResult.OK) return;
        var catalog = _catalog;
        string? manifest = null;
        if (catalog is null)
        {
            using var choose = new OpenFileDialog { Filter = "分区清单 JSON|*.json", Title = "选择显式母线身份目录" };
            if (choose.ShowDialog(this) != DialogResult.OK) return;
            manifest = choose.FileName;
        }
        MotherLineCsvInput? input = null;
        int version = _contextVersion;
        await RunOperationAsync("正在读取已有处理CSV…", token =>
        {
            var loaded = catalog ?? PartitionMotherLineIdentityService.ReadManifest(manifest!);
            return (Input: MotherLineProcessingCsvImportService.Read(csv.FileName, token), Catalog: loaded);
        }, result => { input = result.Input; catalog = result.Catalog; });
        if (input is null || catalog is null || IsDisposed || version != _contextVersion) return;
        using var binding = new MotherLineCsvBindingDialog(input, catalog);
        if (binding.ShowDialog(this) != DialogResult.OK || version != _contextVersion) return;
        await ImportProcessingCsvAsync(context.Item1, context.Item2, catalog, input, binding.ReadBindings());
    }

    internal Task ImportProcessingCsvAsync(string project, string workarea, PartitionMotherLineIdentityCatalog catalog,
        MotherLineCsvInput input, IReadOnlyList<MotherLineCsvBinding> bindings)
    {
        var existing = _batches.ToArray(); var oldCatalog = _catalog;
        return RunOperationAsync("正在核验CSV来源并整理母线候选…", token =>
        {
            if (oldCatalog is not null && (project != _projectId || workarea != _workareaId ||
                JsonSerializer.Serialize(catalog) != JsonSerializer.Serialize(oldCatalog)))
                throw new InvalidDataException("不能将不同项目、工区或设计的CSV混入现有候选。");
            var batch = MotherLineProcessingCsvImportService.Capture(project, workarea, catalog, input, bindings, token);
            if (existing.Any(b => b.SourceKind == MotherLineProcessingCsvImportService.SourceKind &&
                b.SnapshotId != batch.SnapshotId && b.Observations.Any(o => o.SourceSha256 == input.Sha256)))
                throw new InvalidDataException("同一CSV已有不同绑定或草稿状态。请先清空候选再重新绑定，原候选已保留。");
            var batches = existing.Any(b => b.SnapshotId == batch.SnapshotId) ? existing : existing.Append(batch).ToArray();
            var assembly = MotherLineProductionAssemblyService.Build(project, workarea, catalog, batches);
            MotherLineCandidateFileService.VerifySources(batches, token);
            return (batch, batches, assembly);
        }, result =>
        {
            _projectId = project; _workareaId = workarea; _batch ??= result.batch;
            ApplyAssembly(catalog, result.batches, result.assembly);
        });
    }

    internal Task BuildCandidateAsync(string manifestPath)
    {
        var analysis = _analysis; var export = _export;
        string project = _projectId, workarea = _workareaId;
        return RunOperationAsync("正在读取分区清单并核验来源…", token =>
        {
            if (analysis is null || export is null) throw new InvalidDataException("请先生成处理用 CSV。");
            var catalog = PartitionMotherLineIdentityService.ReadManifest(manifestPath);
            var batch = MotherLineProductionSnapshotService.Capture(project, workarea, catalog.DesignId,
                analysis, export.AuditProduction, export.FinalExportedRows,
                export.FinalProcessingRows.Count > 0 ? export.FinalProcessingRows : null);
            var assembly = MotherLineProductionAssemblyService.Build(project, workarea, catalog, [batch]);
            MotherLineCandidateFileService.VerifySources([batch], token);
            token.ThrowIfCancellationRequested();
            return (catalog, batch, assembly);
        }, result => { _batch = result.batch; ApplyAssembly(result.catalog, [result.batch], result.assembly); });
    }

    internal Task ImportCandidatesAsync(IReadOnlyList<string> paths)
    {
        var catalog = _catalog; var existing = _batches.ToArray();
        string project = _projectId, workarea = _workareaId;
        return RunOperationAsync("正在读取其他子区并核验来源…", token =>
        {
            if (catalog is null || existing.Length == 0) throw new InvalidDataException("请先生成当前母线候选。");
            return PrepareImports(paths, project, workarea, catalog, existing, token);
        }, result => ApplyAssembly(catalog!, result.Batches, result.Assembly));
    }

    internal Task SaveCandidateAsync(string path)
    {
        var request = _catalog is null ? null : new MotherLineCandidateRequest(_projectId, _workareaId, _catalog, _batches.ToArray());
        return RunOperationAsync("正在核验来源并保存候选…", token =>
        {
            if (request is null) throw new InvalidDataException("尚无可保存候选。");
            WriteCandidate(path, request, token); return path;
        }, saved =>
        {
            _detail.Text = "候选已保存：" + saved + "\r\n可用顶栏“定位最近成果”找到此文件。原生产 CSV 未改动。";
            CandidateSaved?.Invoke(saved);
        }, acceptCommittedResult: true);
    }

    internal Task ExportCsvCandidateAsync(string outputDirectory)
    {
        var assembly = _assembly; var batches = _batches.ToArray();
        return RunOperationAsync("正在生成只读母线 CSV 候选…", token =>
        {
            if (assembly is null) throw new InvalidDataException("尚无可导出的母线候选。");
            MotherLineCandidateFileService.VerifySources(batches, token);
            string fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(assembly)));
            return MotherLineCsvExportService.WriteNew(outputDirectory, assembly, fingerprint, token);
        }, result =>
        {
            _detail.Text = $"母线 CSV 候选已写入：\r\n{result.CsvPath}\r\n" +
                (result.QualityRepeatCsvPath is null ? "" : $"质量重复线独立文件：\r\n{result.QualityRepeatCsvPath}\r\n") +
                $"逐点追溯：{result.TracePath}\r\n清单：{result.ManifestPath}\r\n正式测线点数：{result.Rows:N0}。需核验后使用，不是已批准成果。";
            CandidateSaved?.Invoke(result.CsvPath);
        }, acceptCommittedResult: true);
    }

    protected override void Dispose(bool disposing)
    { if (disposing) { _contextVersion++; _operationCancellation?.Cancel(); } base.Dispose(disposing); }

    private void ClearCandidate()
    {
        _batch = null; _batches = []; _catalog = null; _assembly = null; _runs.DataSource = null; _map.SetAssembly(null);
        _clear.Enabled = _import.Enabled = _resetImports.Enabled = false;
        _save.Enabled = _exportCsv.Enabled = false; _detail.Text = "沿母线设计方向统一排列完整观测记录；保留飞行 Pass、已排除间隙及真实交叠点。";
    }

    internal void BuildCandidate(PartitionMotherLineIdentityCatalog catalog)
    {
        if (_analysis is null || _export is null) throw new InvalidDataException("请先生成处理用 CSV。");
        var batch = MotherLineProductionSnapshotService.Capture(_projectId, _workareaId, catalog.DesignId,
            _analysis, _export.AuditProduction, _export.FinalExportedRows,
            _export.FinalProcessingRows.Count > 0 ? _export.FinalProcessingRows : null);
        var assembly = MotherLineProductionAssemblyService.Build(_projectId, _workareaId, catalog, [batch]);
        MotherLineCandidateFileService.VerifySources([batch]);
        _batch = batch;
        ApplyAssembly(catalog, [batch], assembly);
    }

    private void ApplyAssembly(PartitionMotherLineIdentityCatalog catalog, IReadOnlyList<MotherLineProductionBatch> batches,
        MotherLineProductionAssembly assembly)
    {
        _catalog = catalog; _batches = batches; _assembly = assembly; _import.Enabled = _clear.Enabled = true;
        _resetImports.Enabled = batches.Count > 1;
        _runs.DataSource = assembly.PointSets.SelectMany(s => s.Runs.Select(r => new RunRow(s.LineName, s.LineOrder,
            r.PartitionCode, r.DirectionUndetermined ? "方向未定" : r.Reversed ? "已整段反序" : "保持正向", r.Points.Count,
            s.IsQualityRepeat ? "质量重复线" : "正式测线", r.OriginalLinePass, r.RecognizedPassId, r))).ToArray();
        _map.SetAssembly(assembly); _save.Enabled = _exportCsv.Enabled = true; SelectRun();
        _summary.Text = $"{batches.Count} 个快照；候选 {assembly.PointSets.Count:N0} 个母线点集，{assembly.PointSets.Sum(s => s.Runs.Count):N0} 段；未归属 {assembly.Unresolved.Count:N0} 点，去重 {assembly.DuplicateSourceRecords:N0} 点。" +
            (assembly.ContainsReviewDraft ? " 来源含待复核草稿。" : "") +
            (!assembly.OriginalLineageVerified ? " 含处理CSV：原始血缘、原项目/设计及审核资格未验证。" : "") + " 未归属点不导出；仅核验候选，尚未替换生产成果。";
    }

    internal void ImportCandidates(IReadOnlyList<string> paths)
    {
        if (_catalog is null || _batch is null) throw new InvalidDataException("请先生成当前母线候选。");
        var result = PrepareImports(paths, _projectId, _workareaId, _catalog, _batches, default);
        ApplyAssembly(_catalog, result.Batches, result.Assembly);
    }

    private static (IReadOnlyList<MotherLineProductionBatch> Batches, MotherLineProductionAssembly Assembly) PrepareImports(
        IReadOnlyList<string> paths, string project, string workarea, PartitionMotherLineIdentityCatalog catalog,
        IReadOnlyList<MotherLineProductionBatch> existingBatches, CancellationToken token)
    {
        var batches = existingBatches.ToDictionary(b => b.SnapshotId, StringComparer.Ordinal);
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            var request = MotherLineCandidateFileService.Read(path, project, workarea, catalog, token);
            foreach (var batch in request.Batches)
            {
                if (batches.TryGetValue(batch.SnapshotId, out var existing))
                {
                    if (JsonSerializer.Serialize(existing) != JsonSerializer.Serialize(batch))
                        throw new InvalidDataException("同一快照身份存在内容冲突。");
                }
                else batches.Add(batch.SnapshotId, batch);
            }
        }
        var combined = batches.Values.OrderBy(b => b.SnapshotId, StringComparer.Ordinal).ToArray();
        var assembly = MotherLineProductionAssemblyService.Build(project, workarea, catalog, combined);
        MotherLineCandidateFileService.VerifySources(combined, token);
        token.ThrowIfCancellationRequested();
        return (combined, assembly);
    }

    internal void SaveCandidate(string path)
    {
        if (_batch is null || _catalog is null) throw new InvalidDataException("尚无可保存候选。");
        WriteCandidate(path, new(_projectId, _workareaId, _catalog, _batches), default);
    }

    private static void WriteCandidate(string path, MotherLineCandidateRequest request, CancellationToken token)
    {
        MotherLineCandidateFileService.VerifySources(request.Batches, token);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(request, new JsonSerializerOptions { WriteIndented = true });
        token.ThrowIfCancellationRequested();
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(data);
    }

    private void SelectRun()
    {
        var row = _runs.CurrentRow?.DataBoundItem as RunRow; _map.Selected = row?.Run;
        if (row is null) return;
        var r = row.Run;
        _detail.Text = $"{row.Line} / {r.PartitionCode} / {row.Direction}；点序 {r.Points[0].PointOrder}–{r.Points[^1].PointOrder}；局部回退 {r.BacktrackingM:0.###} m\r\n" +
            $"Pass：{r.RecognizedPassId}；原 LinePass：{r.OriginalLinePass}；快照：{r.SnapshotId}\r\n" +
            string.Join("\r\n", r.Points.SelectMany(p => p.Provenance).Select(p => p.SourceFile).Distinct()) +
            "\r\n反序保留整条记录的原时间和磁值；不插值、不跨缺口补点。";
    }

    private sealed record RunRow(string Line, int Order, string Partition, string Direction, int Count,
        string Purpose, int? Gap, string Pass, MotherLineProductionRun Run);
}

internal sealed class MotherLineProductionPreview : Panel
{
    private readonly PreviewNavigation _navigation;
    private MotherLineProductionAssembly? _assembly;
    private MotherLineProductionRun? _selected;
    internal MotherLineProductionPreview() { Dock = DockStyle.Fill; DoubleBuffered = true; SetStyle(ControlStyles.Selectable, true);
        _navigation = new(this); AccessibleName = "母线候选方向空间预览"; }
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal MotherLineProductionRun? Selected { set { _selected = value; Invalidate(); } }
    internal void SetAssembly(MotherLineProductionAssembly? value) { _assembly = value; _selected = null;
        _navigation.SetDataKey(value); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); using var cue = _navigation.BeginPaint(e.Graphics);
        var runs = _assembly?.PointSets.SelectMany(s => s.Runs).ToArray() ?? [];
        var all = runs.SelectMany(r => r.Points).Select(p => p.Sample).ToArray();
        if (all.Length == 0) { TextRenderer.DrawText(e.Graphics, "暂无已归属母线候选", Font, ClientRectangle, SystemColors.GrayText); return; }
        double minX = all.Min(s => s.Longitude!.Value), maxX = all.Max(s => s.Longitude!.Value),
            minY = all.Min(s => s.Latitude!.Value), maxY = all.Max(s => s.Latitude!.Value);
        double factor = Math.Max(.01, Math.Cos((minY + maxY) / 2 * Math.PI / 180));
        double scale = Math.Min(Math.Max(1, Width - 32) / Math.Max(.00001, (maxX - minX) * factor),
            Math.Max(1, Height - 32) / Math.Max(.00001, maxY - minY));
        PointF Point(MotherLineProductionPoint p) => _navigation.Apply(new(
            (float)(Width / 2d + (p.Sample.Longitude!.Value - (minX + maxX) / 2) * factor * scale),
            (float)(Height / 2d - (p.Sample.Latitude!.Value - (minY + maxY) / 2) * scale)));
        foreach (var r in runs.OrderBy(r => ReferenceEquals(r, _selected)))
        {
            Color direction = r.DirectionUndetermined ? Color.MediumPurple : r.Reversed ? Color.DarkOrange : Color.SteelBlue;
            var points = r.Points.Select(Point).ToArray();
            if (points.Length > 1)
            {
                bool selected = ReferenceEquals(r, _selected);
                if (selected) { using var halo = new Pen(SystemColors.WindowText, 4.5f); e.Graphics.DrawLines(halo, points); }
                using var pen = new Pen(direction, selected ? 2 : 1.5f);
                using var arrow = new System.Drawing.Drawing2D.AdjustableArrowCap(4, 5, true);
                pen.CustomEndCap = arrow;
                e.Graphics.DrawLines(pen, points);
            }
            else
            {
                using var pen = new Pen(direction, 2);
                e.Graphics.DrawEllipse(pen, points[0].X - 2, points[0].Y - 2, 4, 4);
            }
        }
    }
    protected override void Dispose(bool disposing) { if (disposing) _navigation.Dispose(); base.Dispose(disposing); }
}
