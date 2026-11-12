namespace MagOps.Desktop;

/// <summary>Display-only navigation; data and geometry stay in their original coordinates.</summary>
internal sealed class PreviewNavigation : IDisposable
{
    private readonly Control _owner;
    private readonly bool _horizontalOnly;
    private string _preferenceKey = "";
    private bool _savePending;
    private bool _disposed;
    private Size _viewport;
    private object? _dataIdentity;
    private static string _workspaceKey = "";
    internal static event Action? WorkspaceChanged;
    internal static string WorkspaceKey
    {
        get => _workspaceKey;
        set
        {
            string next = value ?? "";
            if (_workspaceKey.Equals(next, StringComparison.OrdinalIgnoreCase)) return;
            _workspaceKey = next;
            WorkspaceChanged?.Invoke();
        }
    }
    internal static string? PreferencePathOverride { get; set; }
    private readonly ToolTip _hint = new();
    private readonly ContextMenuStrip _menu = new();
    private double _zoom = 1;
    private PointF _offset;
    private Point? _dragStart;
    private MouseButtons _dragButton;
    private Cursor? _previousCursor;
    private readonly System.Windows.Forms.Timer _settleTimer = new() { Interval = 180 };
    internal bool IsInteracting { get; private set; }

    internal double Zoom => _zoom;
    internal object ExportViewSnapshot() => new { zoom = _zoom, offsetX = _offset.X, offsetY = _offset.Y };
    internal bool IsDragging => _dragStart is not null;

    internal void Translate(float dx, float dy)
    {
        _offset = new PointF(_offset.X + dx, _horizontalOnly ? _offset.Y : _offset.Y + dy);
        _savePending = true;
        _owner.Invalidate();
    }

    internal PreviewNavigation(Control owner, bool allowPrimaryDrag = false, bool horizontalOnly = false)
    {
        _owner = owner;
        _horizontalOnly = horizontalOnly;
        _viewport = owner.ClientSize;
        WorkspaceChanged += HandleWorkspaceChanged;
        _settleTimer.Tick += (_, _) =>
        {
            if (_dragStart is not null) return;
            _settleTimer.Stop(); IsInteracting = false; FlushView(); owner.Invalidate();
        };
        owner.TabStop = true;
        _hint.SetToolTip(owner, horizontalOnly
            ? "滚轮缩放时间轴 · 按住鼠标中键拖动 · 左右方向键平移 · +/- 缩放 · Home 恢复全图"
            : allowPrimaryDrag
            ? "滚轮缩放 · 按住鼠标左键或中键拖动 · 右键菜单或 Home 恢复全图"
            : "滚轮缩放 · 按住鼠标中键拖动 · 方向键平移 · +/- 缩放 · Home 恢复全图");
        _menu.Items.Add("恢复全图", null, (_, _) => Reset());
        owner.ContextMenuStrip = _menu;
        owner.AccessibleName ??= horizontalOnly ? "时间轴预览" : "空间预览";
        owner.AccessibleDescription = horizontalOnly
            ? "时间轴预览，滚轮或加减键缩放时间范围，中键或左右方向键平移，Home 恢复全图；视图操作不改变数据。"
            : "滚轮缩放，中键平移；Home 或右键菜单恢复全图；视图操作不改变数据。";
        owner.MouseDown += (_, _) => owner.Focus();
        owner.Resize += (_, _) =>
        {
            Size next = owner.ClientSize;
            if (_viewport.Width > 0 && _viewport.Height > 0 && next.Width > 0 && next.Height > 0)
                _offset = new PointF(_offset.X * next.Width / _viewport.Width,
                    _horizontalOnly ? _offset.Y : _offset.Y * next.Height / _viewport.Height);
            _viewport = next;
            owner.Invalidate();
        };
        owner.GotFocus += (_, _) => owner.Invalidate();
        owner.LostFocus += (_, _) => owner.Invalidate();
        owner.PreviewKeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Left or Keys.Right or Keys.Home ||
                (!_horizontalOnly && e.KeyCode is Keys.Up or Keys.Down))
                e.IsInputKey = true;
        };
        owner.MouseWheel += (_, e) =>
        {
            if (e.Delta == 0 || _dragStart is not null) return;
            ZoomAt(e.Location, Math.Pow(1.2, e.Delta / 120d));
            if (e is HandledMouseEventArgs wheel) wheel.Handled = true;
        };
        owner.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Middle && !(allowPrimaryDrag && e.Button == MouseButtons.Left)) return;
            _dragButton = e.Button;
            _previousCursor = owner.Cursor;
            owner.Cursor = Cursors.SizeAll;
            _dragStart = e.Location;
            owner.Capture = true;
        };
        owner.MouseMove += (_, e) =>
        {
            if (_dragStart is not { } previous) return;
            _offset = new PointF(_offset.X + e.X - previous.X,
                _horizontalOnly ? _offset.Y : _offset.Y + e.Y - previous.Y);
            _dragStart = e.Location;
            _savePending = true;
            BeginInteraction();
            owner.Invalidate();
        };
        owner.MouseUp += (_, e) =>
        {
            if (e.Button != _dragButton) return;
            EndDrag();
            owner.Capture = false;
        };
        owner.MouseCaptureChanged += (_, _) => { if (!owner.Capture) EndDrag(); };
        owner.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Home) Reset();
            else if (e.KeyCode is Keys.Add or Keys.Oemplus or Keys.Subtract or Keys.OemMinus)
                ZoomAt(new Point(owner.ClientSize.Width / 2, owner.ClientSize.Height / 2),
                    e.KeyCode is Keys.Add or Keys.Oemplus ? 1.2 : 1 / 1.2);
            else if (e.KeyCode is Keys.Left or Keys.Right ||
                (!_horizontalOnly && e.KeyCode is Keys.Up or Keys.Down))
            {
                float step = 32 * owner.DeviceDpi / 96f;
                _offset = new PointF(_offset.X + (e.KeyCode == Keys.Left ? step : e.KeyCode == Keys.Right ? -step : 0),
                    _horizontalOnly ? _offset.Y : _offset.Y + (e.KeyCode == Keys.Up ? step : e.KeyCode == Keys.Down ? -step : 0));
                _savePending = true; BeginInteraction(); owner.Invalidate();
            }
            else return;
            e.Handled = true; e.SuppressKeyPress = true;
        };
        owner.Disposed += (_, _) => Dispose();
    }

    // Dispose after content, including early returns. Preserve the original paint clip and
    // transform so fills, cached bitmaps and temporary content clips cannot hide the cue.
    internal FocusCue BeginPaint(Graphics graphics) => new(_owner, graphics);

    internal readonly struct FocusCue : IDisposable
    {
        private readonly Control _owner;
        private readonly Graphics _graphics;
        private readonly System.Drawing.Drawing2D.GraphicsState _state;

        internal FocusCue(Control owner, Graphics graphics)
        {
            _owner = owner;
            _graphics = graphics;
            _state = graphics.Save();
        }

        public void Dispose()
        {
            _graphics.Restore(_state);
            if (_owner.Focused && _owner.ClientSize.Width > 4 && _owner.ClientSize.Height > 4)
                ControlPaint.DrawFocusRectangle(_graphics,
                    Rectangle.Inflate(_owner.ClientRectangle, -2, -2), _owner.ForeColor, _owner.BackColor);
        }
    }

    private void ZoomAt(Point anchor, double factor)
    {
        double next = Math.Clamp(_zoom * factor, .25, 64);
        double ratio = next / _zoom;
        _offset = new PointF((float)(anchor.X - (anchor.X - _offset.X) * ratio),
            _horizontalOnly ? _offset.Y : (float)(anchor.Y - (anchor.Y - _offset.Y) * ratio));
        _zoom = next; _savePending = true; BeginInteraction(); _owner.Invalidate();
    }

    internal PointF Apply(PointF point) => new(
        (float)(point.X * _zoom + _offset.X),
        _horizontalOnly ? point.Y : (float)(point.Y * _zoom + _offset.Y));

    internal PointF Unapply(PointF point) => new(
        (float)((point.X - _offset.X) / _zoom),
        _horizontalOnly ? point.Y : (float)((point.Y - _offset.Y) / _zoom));

    internal void CenterOn(PointF basePoint)
    {
        if (!float.IsFinite(basePoint.X) || !float.IsFinite(basePoint.Y)) return;
        EndDrag();
        _offset = new PointF((float)(_owner.ClientSize.Width / 2d - basePoint.X * _zoom),
            _horizontalOnly ? _offset.Y : (float)(_owner.ClientSize.Height / 2d - basePoint.Y * _zoom));
        _savePending = true; BeginInteraction(); _owner.Invalidate();
    }

    internal void Reset()
    {
        _zoom = 1; _offset = PointF.Empty; EndDrag();
        _savePending = true; FlushView();
        _settleTimer.Stop(); IsInteracting = false;
        _owner.Capture = false;
        _owner.AccessibleDescription = _horizontalOnly
            ? "时间轴预览，全时间范围；左右方向键平移，加减键缩放，Home 恢复全图。"
            : "空间预览，全图；方向键平移，加减键缩放，Home 恢复全图。";
        _owner.Invalidate();
    }

    private void EndDrag()
    {
        _dragStart = null; _dragButton = MouseButtons.None;
        FlushView();
        if (_previousCursor is not null) { _owner.Cursor = _previousCursor; _previousCursor = null; }
    }

    private void BeginInteraction()
    {
        IsInteracting = true; _settleTimer.Stop(); _settleTimer.Start();
        _owner.AccessibleDescription = _horizontalOnly
            ? $"时间轴预览，时间范围缩放 {_zoom:P0}；中键或左右方向键平移，滚轮或加减键缩放，Home 恢复全图。"
            : $"空间预览，缩放 {_zoom:P0}；中键或方向键平移，滚轮或加减键缩放，Home 恢复全图。";
    }

    private void HandleWorkspaceChanged() => SetDataKey(_dataIdentity);

    internal void SetDataKey(object? identity)
    {
        _dataIdentity = identity;
        string next = identity is null ? "" : PreviewViewPreferences.Key(
            WorkspaceKey, _owner.GetType().FullName ?? _owner.GetType().Name, identity);
        if (next == _preferenceKey) return;
        FlushView();
        EndDrag();
        _settleTimer.Stop();
        IsInteracting = false;
        _preferenceKey = next;
        _savePending = false;
        _zoom = 1; _offset = PointF.Empty;
        if (next.Length > 0 && PreviewViewPreferences.Load(next, PreferencePathOverride) is { } saved)
        {
            _zoom = saved.Zoom;
            _offset = new PointF((float)(saved.OffsetX * Math.Max(1, _owner.ClientSize.Width)),
                (float)(saved.OffsetY * Math.Max(1, _owner.ClientSize.Height)));
        }
        _owner.Invalidate();
    }

    private void FlushView()
    {
        if (!_savePending || _preferenceKey.Length == 0) return;
        _savePending = false;
        PreviewViewPreferences.Save(_preferenceKey, new PreviewViewState(_zoom,
            _offset.X / Math.Max(1d, _owner.ClientSize.Width),
            _offset.Y / Math.Max(1d, _owner.ClientSize.Height), DateTimeOffset.UtcNow), PreferencePathOverride);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        WorkspaceChanged -= HandleWorkspaceChanged;
        FlushView();
        _settleTimer.Dispose(); _hint.Dispose(); _menu.Dispose();
    }
}
