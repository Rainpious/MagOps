using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;
using MagOps.Core.Routes;
using MagOps.Core.SurveyPlanning;

namespace MagOps.Desktop;

internal sealed class PartitionRouteOverviewEntry
{
    public string PartitionCode { get; init; } = "";
    public IReadOnlyList<SurveyBoundaryPoint> Boundary { get; init; } = [];
    public RoutePlanResult? Plan { get; set; }
    public RoutePartitionStartSide StartSide { get; set; } =
        RoutePartitionStartSide.Automatic;
    public RouteStartDirection StartDirection { get; set; } =
        RouteStartDirection.Southwest;
    public double MaxSortieKm { get; set; } = 18d;
    public bool Dirty { get; set; }
    public string Status { get; set; } = "等待生成";

    public int SortieCount => Plan?.Sorties.Count ?? 0;

    public double LongestSortieKm => Plan is null
        ? 0d
        : Plan.Sorties
            .Select(sortie => RouteCoordinateMath.RouteLength(
                sortie.Points,
                Plan.Mode,
                Plan.CentralMeridian) / 1000d)
            .DefaultIfEmpty(0d)
            .Max();
}

internal sealed record PartitionRouteOverviewEdit(
    string PartitionCode,
    RoutePartitionStartSide StartSide,
    RouteStartDirection StartDirection,
    double MaxSortieKm);

/// <summary>
/// Multi-partition route workspace. Every partition keeps its own RoutePlan;
/// this control only presents them together and edits safe per-partition
/// orchestration parameters. Detailed sortie/waypoint edits remain in the
/// existing single-partition route editor.
/// </summary>
internal sealed class PartitionRouteOverviewPanel : UserControl
{
    private readonly DataGridView _grid = GridFactory.Create();
    private readonly PartitionOverviewCanvas _canvas = new();
    private readonly Button _openSelected;
    private readonly Label _summary = new();
    private readonly List<PartitionRouteOverviewEntry> _entries = [];
    private bool _syncing;

    internal event Action<PartitionRouteOverviewEdit>? SettingsChanged;
    internal event Action<string>? OpenPartitionRequested;

    internal PartitionRouteOverviewPanel()
    {
        _grid.AccessibleName = "分区航线总览与参数";
        Dock = DockStyle.Fill;
        Margin = Padding.Empty;
        Padding = Padding.Empty;
        BackColor = UiTheme.Surface;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            Margin = Padding.Empty,
            Padding = new Padding(8, 6, 8, 6),
            BackColor = UiTheme.SurfaceMuted
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _summary.AutoSize = true;
        _summary.Dock = DockStyle.Fill;
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        _summary.ForeColor = UiTheme.TextSecondary;
        _summary.Text = "分区完成后在这里同时检查全部子区航线。";
        header.Controls.Add(_summary, 0, 0);
        _openSelected = UiComponents.Button(
            "进入选中分区精细编辑",
            (_, _) => OpenCurrent(),
            UiButtonRole.Primary,
            minimumWidth: 176,
            minimumHeight: 34);
        header.Controls.Add(_openSelected, 1, 0);
        root.Controls.Add(header, 0, 0);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
            Margin = Padding.Empty,
            BackColor = UiTheme.Border
        };

        ConfigureGrid();
        split.Panel1.Controls.Add(_grid);
        _canvas.Dock = DockStyle.Fill;
        _canvas.PartitionSelected += SelectPartition;
        _canvas.PartitionOpenRequested += code =>
            OpenPartitionRequested?.Invoke(code);
        split.Panel2.Controls.Add(_canvas);
        root.Controls.Add(split, 0, 1);
        Controls.Add(root);

        Resize += (_, _) =>
        {
            const int minLeft = 320;
            const int minRight = 360;
            int available = split.ClientSize.Width - split.SplitterWidth;
            if (available <= minLeft + minRight)
                return;
            split.SplitterDistance = Math.Clamp(
                (int)(available * 0.43),
                minLeft,
                available - minRight);
        };

        UiTheme.Apply(this);
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.Margin = Padding.Empty;
        // GridFactory.Create() is read-only by default. This overview is one of
        // the few grids that intentionally edits safe per-partition route
        // parameters, so opt the grid itself back into editing while keeping
        // identity/result columns explicitly read-only below.
        _grid.ReadOnly = false;
        _grid.EditMode = DataGridViewEditMode.EditOnEnter;
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToOrderColumns = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;

        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "PartitionCode",
            HeaderText = "分区",
            ReadOnly = true,
            FillWeight = 54
        });
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "StartSide",
            HeaderText = "起始侧",
            DataSource = new[]
            {
                "自动（共同切分边界）", "北侧", "南侧", "西侧", "东侧"
            },
            FlatStyle = FlatStyle.Flat,
            FillWeight = 105
        });
        _grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            Name = "StartDirection",
            HeaderText = "角点偏好",
            DataSource = new[] { "西南", "西北", "东南", "东北" },
            FlatStyle = FlatStyle.Flat,
            FillWeight = 80
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "MaxSortieKm",
            HeaderText = "架次上限 km",
            FillWeight = 74
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "SortieCount",
            HeaderText = "架次数",
            ReadOnly = true,
            FillWeight = 52
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "LongestKm",
            HeaderText = "最长 km",
            ReadOnly = true,
            FillWeight = 62
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Status",
            HeaderText = "状态",
            ReadOnly = true,
            FillWeight = 100
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Dirty",
            HeaderText = "修改",
            ReadOnly = true,
            FillWeight = 54
        });

        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += (_, e) =>
        {
            if (_syncing || e.RowIndex < 0 || e.ColumnIndex < 1 || e.ColumnIndex > 3)
                return;
            EmitEdit(e.RowIndex);
        };
        _grid.SelectionChanged += (_, _) =>
        {
            string code = CurrentPartitionCode();
            if (code.Length > 0)
                _canvas.SelectedPartitionCode = code;
        };
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0)
                OpenCurrent();
        };
        _grid.DataError += (_, e) =>
        {
            e.ThrowException = false;
        };
    }

    internal void SetEntries(
        IReadOnlyList<PartitionRouteOverviewEntry> entries,
        string selectedPartitionCode = "")
    {
        _syncing = true;
        try
        {
            _entries.Clear();
            _entries.AddRange(entries.OrderBy(entry => entry.PartitionCode, StringComparer.OrdinalIgnoreCase));
            _grid.Rows.Clear();
            foreach (PartitionRouteOverviewEntry entry in _entries)
                AddRow(entry);
        }
        finally
        {
            _syncing = false;
        }

        string selected = selectedPartitionCode;
        if (selected.Length == 0 && _entries.Count > 0)
            selected = _entries[0].PartitionCode;
        SelectPartition(selected);
        _canvas.SetEntries(_entries);
        _summary.Text = _entries.Count == 0
            ? "尚未建立分区航线总览。"
            : $"共 {_entries.Count} 个分区；可直接修改起始侧 / 角点偏好 / 架次上限。双击地图或表格进入单区精细编辑。";
        _openSelected.Enabled = _entries.Count > 0;
    }

    internal void UpdateEntry(PartitionRouteOverviewEntry entry)
    {
        int index = _entries.FindIndex(item =>
            item.PartitionCode.Equals(entry.PartitionCode, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return;

        _entries[index] = entry;
        _syncing = true;
        try
        {
            WriteRow(_grid.Rows[index], entry);
        }
        finally
        {
            _syncing = false;
        }
        _canvas.SetEntries(_entries);
    }

    internal void ClearEntries()
    {
        _syncing = true;
        try
        {
            _entries.Clear();
            _grid.Rows.Clear();
        }
        finally
        {
            _syncing = false;
        }
        _canvas.SetEntries([]);
        _summary.Text = "尚未建立分区航线总览。";
        _openSelected.Enabled = false;
    }

    private void AddRow(PartitionRouteOverviewEntry entry)
    {
        int index = _grid.Rows.Add();
        WriteRow(_grid.Rows[index], entry);
    }

    private static void WriteRow(
        DataGridViewRow row,
        PartitionRouteOverviewEntry entry)
    {
        row.Tag = entry.PartitionCode;
        row.Cells["PartitionCode"].Value = entry.PartitionCode;
        row.Cells["StartSide"].Value = DisplayStartSide(entry.StartSide);
        row.Cells["StartDirection"].Value = DisplayStartDirection(entry.StartDirection);
        row.Cells["MaxSortieKm"].Value = entry.MaxSortieKm.ToString("0.###", CultureInfo.InvariantCulture);
        row.Cells["SortieCount"].Value = entry.SortieCount;
        row.Cells["LongestKm"].Value = entry.Plan is null
            ? "—"
            : entry.LongestSortieKm.ToString("0.00", CultureInfo.InvariantCulture);
        row.Cells["Status"].Value = entry.Status;
        row.Cells["Dirty"].Value = entry.Dirty ? "已修改" : "已生成";
    }

    private void EmitEdit(int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _grid.Rows.Count)
            return;
        DataGridViewRow row = _grid.Rows[rowIndex];
        string code = row.Tag?.ToString() ?? "";
        if (code.Length == 0)
            return;

        string sideText = row.Cells["StartSide"].Value?.ToString() ?? "自动（共同切分边界）";
        string directionText = row.Cells["StartDirection"].Value?.ToString() ?? "西南";
        string maxText = row.Cells["MaxSortieKm"].Value?.ToString() ?? "";
        if (!double.TryParse(
                maxText,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double maxKm) ||
            !double.IsFinite(maxKm) ||
            maxKm <= 0d)
        {
            PartitionRouteOverviewEntry? existing = _entries.FirstOrDefault(item =>
                item.PartitionCode.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                _syncing = true;
                try
                {
                    row.Cells["MaxSortieKm"].Value = existing.MaxSortieKm.ToString("0.###", CultureInfo.InvariantCulture);
                }
                finally
                {
                    _syncing = false;
                }
            }
            return;
        }

        SettingsChanged?.Invoke(new PartitionRouteOverviewEdit(
            code,
            ParseStartSide(sideText),
            ParseStartDirection(directionText),
            maxKm));
    }

    private void OpenCurrent()
    {
        string code = CurrentPartitionCode();
        if (code.Length > 0)
            OpenPartitionRequested?.Invoke(code);
    }

    internal string OverviewSelection => _canvas.SelectedPartitionCode;

    private string CurrentPartitionCode() =>
        _grid.CurrentRow?.Tag?.ToString() ?? "";

    private void SelectPartition(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return;
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (!string.Equals(row.Tag?.ToString(), code, StringComparison.OrdinalIgnoreCase))
                continue;
            if (row.Cells.Count > 0)
                _grid.CurrentCell = row.Cells[0];
            break;
        }
        _canvas.SelectedPartitionCode = code;
    }

    internal static string DisplayStartSide(RoutePartitionStartSide side) => side switch
    {
        RoutePartitionStartSide.North => "北侧",
        RoutePartitionStartSide.South => "南侧",
        RoutePartitionStartSide.West => "西侧",
        RoutePartitionStartSide.East => "东侧",
        _ => "自动（共同切分边界）"
    };

    internal static RoutePartitionStartSide ParseStartSide(string text) =>
        (text?.Trim() ?? "") switch
        {
            "北侧" => RoutePartitionStartSide.North,
            "南侧" => RoutePartitionStartSide.South,
            "西侧" => RoutePartitionStartSide.West,
            "东侧" => RoutePartitionStartSide.East,
            _ => RoutePartitionStartSide.Automatic
        };

    internal static string DisplayStartDirection(RouteStartDirection direction) => direction switch
    {
        RouteStartDirection.Northwest => "西北",
        RouteStartDirection.Southeast => "东南",
        RouteStartDirection.Northeast => "东北",
        _ => "西南"
    };

    internal static RouteStartDirection ParseStartDirection(string text) =>
        (text?.Trim() ?? "") switch
        {
            "西北" => RouteStartDirection.Northwest,
            "东南" => RouteStartDirection.Southeast,
            "东北" => RouteStartDirection.Northeast,
            _ => RouteStartDirection.Southwest
        };

    private sealed class PartitionOverviewCanvas : Panel
    {
        private readonly List<PartitionRouteOverviewEntry> _entries = [];
        private readonly PreviewNavigation _navigation;
        private readonly Dictionary<string, PointF[]> _screenPolygons =
            new(StringComparer.OrdinalIgnoreCase);
        private string _selectedPartitionCode = "";

        internal event Action<string>? PartitionSelected;
        internal event Action<string>? PartitionOpenRequested;

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal string SelectedPartitionCode
        {
            get => _selectedPartitionCode;
            set
            {
                if (_selectedPartitionCode.Equals(value ?? "", StringComparison.OrdinalIgnoreCase))
                    return;
                _selectedPartitionCode = value ?? "";
                Invalidate();
            }
        }

        internal PartitionOverviewCanvas()
        {
            SetStyle(ControlStyles.Selectable, true);
            _navigation = new PreviewNavigation(this);
            DoubleBuffered = true;
            BackColor = UiTheme.Surface;
            BorderStyle = BorderStyle.FixedSingle;
            Cursor = Cursors.Hand;
            Resize += (_, _) => Invalidate();
        }

        internal void SetEntries(IReadOnlyList<PartitionRouteOverviewEntry> entries)
        {
            _navigation.SetDataKey(entries.Count == 0 ? null : entries.Select(e => new { e.PartitionCode, e.Boundary }).ToArray());
            _entries.Clear();
            _entries.AddRange(entries);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var focusCue = _navigation.BeginPaint(e.Graphics);
            _screenPolygons.Clear();
            if (_entries.Count == 0 || !TryCreateTransform(out Func<SurveyBoundaryPoint, PointF> screen))
            {
                TextRenderer.DrawText(
                    e.Graphics,
                    "执行 KML 分区后，将在此同时显示全部 Pxx 边界、测线、航线和首段方向。",
                    Font,
                    new Rectangle(20, 20, Math.Max(0, Width - 40), 70),
                    UiTheme.TextSecondary,
                    TextFormatFlags.WordBreak | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (PartitionRouteOverviewEntry entry in _entries)
            {
                bool selected = entry.PartitionCode.Equals(
                    _selectedPartitionCode,
                    StringComparison.OrdinalIgnoreCase);
                PointF[] polygon = entry.Boundary.Select(screen).ToArray();
                if (polygon.Length >= 3)
                {
                    _screenPolygons[entry.PartitionCode] = polygon;
                    using var fill = new SolidBrush(selected
                        ? Color.FromArgb(34, UiTheme.Accent)
                        : Color.FromArgb(12, UiTheme.Accent));
                    using var border = new Pen(
                        selected ? UiTheme.Accent : UiTheme.BorderStrong,
                        selected ? 2.5f : 1.2f);
                    e.Graphics.FillPolygon(fill, polygon);
                    e.Graphics.DrawPolygon(border, polygon);
                }

                if (entry.Plan is not null)
                {
                    using var surveyPen = new Pen(UiTheme.BorderStrong, selected ? 1.1f : 0.8f);
                    foreach (string line in entry.Plan.Inference.OrderedLines)
                    {
                        if (!entry.Plan.Inference.Grouped.TryGetValue(line, out RouteEndpoint[]? pair) || pair.Length < 2)
                            continue;
                        e.Graphics.DrawLine(
                            surveyPen,
                            screen(new SurveyBoundaryPoint(pair[0].X, pair[0].Y)),
                            screen(new SurveyBoundaryPoint(pair[1].X, pair[1].Y)));
                    }

                    using var routePen = new Pen(
                        selected ? UiTheme.Accent : Color.FromArgb(72, 113, 172),
                        selected ? 2.2f : 1.2f)
                    {
                        LineJoin = LineJoin.Round
                    };
                    foreach (RouteSortie sortie in entry.Plan.Sorties)
                    {
                        PointF[] points = sortie.Points
                            .Select(point => screen(new SurveyBoundaryPoint(point.X, point.Y)))
                            .ToArray();
                        if (points.Length >= 2)
                            e.Graphics.DrawLines(routePen, points);
                    }

                    DrawStart(e.Graphics, screen, entry.Plan, selected);
                }

                if (entry.Boundary.Count > 0)
                {
                    var center = new SurveyBoundaryPoint(
                        entry.Boundary.Average(point => point.X),
                        entry.Boundary.Average(point => point.Y));
                    PointF labelPoint = screen(center);
                    string label = $"{entry.PartitionCode}  起飞:{DisplayStartSide(entry.StartSide)}";
                    TextRenderer.DrawText(
                        e.Graphics,
                        label,
                        Font,
                        new Point((int)labelPoint.X + 4, (int)labelPoint.Y + 4),
                        selected ? UiTheme.Accent : UiTheme.TextPrimary,
                        UiTheme.Surface,
                        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                }
            }
        }

        private static void DrawStart(
            Graphics graphics,
            Func<SurveyBoundaryPoint, PointF> screen,
            RoutePlanResult plan,
            bool selected)
        {
            RouteSortie? firstSortie = plan.Sorties.FirstOrDefault(sortie => sortie.Points.Count >= 2);
            if (firstSortie is null)
                return;
            RouteEndpoint from = firstSortie.Points[0];
            RouteEndpoint? to = firstSortie.Points.Skip(1).FirstOrDefault(point =>
                Math.Abs(point.X - from.X) > 1e-12 || Math.Abs(point.Y - from.Y) > 1e-12);
            if (to is null)
                return;

            PointF a = screen(new SurveyBoundaryPoint(from.X, from.Y));
            PointF b = screen(new SurveyBoundaryPoint(to.X, to.Y));
            float dx = b.X - a.X;
            float dy = b.Y - a.Y;
            float length = MathF.Sqrt(dx * dx + dy * dy);
            if (length < 1f)
                return;
            float ux = dx / length;
            float uy = dy / length;
            float nx = -uy;
            float ny = ux;
            using var brush = new SolidBrush(selected ? UiTheme.Accent : Color.FromArgb(85, 96, 112));
            const float marker = 4.5f;
            graphics.FillPolygon(brush,
            [
                new PointF(a.X + ux * marker, a.Y + uy * marker),
                new PointF(a.X - ux * marker + nx * marker * 0.7f, a.Y - uy * marker + ny * marker * 0.7f),
                new PointF(a.X - ux * marker - nx * marker * 0.7f, a.Y - uy * marker - ny * marker * 0.7f)
            ]);
            float arrowDistance = Math.Min(22f, Math.Max(10f, length * 0.25f));
            PointF tip = new(a.X + ux * arrowDistance, a.Y + uy * arrowDistance);
            graphics.FillPolygon(brush,
            [
                tip,
                new PointF(tip.X - ux * 6f + nx * 3f, tip.Y - uy * 6f + ny * 3f),
                new PointF(tip.X - ux * 6f - nx * 3f, tip.Y - uy * 6f - ny * 3f)
            ]);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            string? hit = HitTest(e.Location);
            if (hit is not null)
                PartitionSelected?.Invoke(hit);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;
            string? hit = HitTest(e.Location);
            if (hit is not null)
                PartitionOpenRequested?.Invoke(hit);
        }

        private string? HitTest(Point point)
        {
            foreach (KeyValuePair<string, PointF[]> pair in _screenPolygons.Reverse())
            {
                using var path = new GraphicsPath();
                path.AddPolygon(pair.Value);
                if (path.IsVisible(point))
                    return pair.Key;
            }
            return null;
        }

        private bool TryCreateTransform(out Func<SurveyBoundaryPoint, PointF> screen)
        {
            screen = static _ => PointF.Empty;
            SurveyBoundaryPoint[] points = _entries
                .SelectMany(entry => entry.Boundary)
                .ToArray();
            if (points.Length == 0)
                return false;

            double minX = points.Min(point => point.X);
            double maxX = points.Max(point => point.X);
            double minY = points.Min(point => point.Y);
            double maxY = points.Max(point => point.Y);
            double spanX = Math.Max(maxX - minX, 1e-9);
            double spanY = Math.Max(maxY - minY, 1e-9);
            const float margin = 34f;
            double plotWidth = Math.Max(1d, Width - margin * 2d);
            double plotHeight = Math.Max(1d, Height - margin * 2d);
            double scale = Math.Max(1e-12, Math.Min(plotWidth / spanX, plotHeight / spanY));
            double usedWidth = spanX * scale;
            double usedHeight = spanY * scale;
            double left = (Width - usedWidth) / 2d;
            double top = (Height - usedHeight) / 2d;
            screen = point => _navigation.Apply(new PointF(
                (float)(left + (point.X - minX) * scale),
                (float)(Height - top - (point.Y - minY) * scale)));
            return true;
        }
    }
}
