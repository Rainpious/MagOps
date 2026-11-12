namespace MagOps.Desktop;

internal static class TransientNotice
{
    public static void Show(
        IWin32Window? owner,
        string message,
        string title = "完成",
        int durationMs = 1000)
    {
        var notice = new TransientNoticeForm(message, title, durationMs);
        notice.PinToOwner(owner);
        notice.Shown += (_, _) =>
            notice.PinToOwner(owner);

        if (owner is null)
            notice.Show();
        else
            notice.Show(owner);
    }

    private sealed class TransientNoticeForm : Form
    {
        private readonly System.Windows.Forms.Timer _timer = new();

        public TransientNoticeForm(string message, string title, int durationMs)
        {
            Text = title;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            MinimizeBox = false;
            MaximizeBox = false;
            ControlBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            MinimumSize = new Size(360, 92);
            Size = new Size(520, 108);
            Padding = new Padding(14);

            var label = new Label
            {
                Text = message,
                Dock = DockStyle.Fill,
                AutoSize = false,
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 2, 6, 2)
            };
            Controls.Add(label);

            _timer.Interval = Math.Clamp(durationMs, 250, 5000);
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                Close();
            };
            Shown += (_, _) => _timer.Start();
            FormClosed += (_, _) => _timer.Dispose();
        }

        public void PinToOwner(IWin32Window? owner)
        {
            Rectangle anchor;
            Screen screen;

            if (owner is Control control && !control.IsDisposed)
            {
                anchor = control.RectangleToScreen(control.ClientRectangle);
                screen = Screen.FromControl(control);
            }
            else if (owner is not null)
            {
                screen = Screen.FromHandle(owner.Handle);
                anchor = screen.WorkingArea;
            }
            else
            {
                screen = Screen.FromPoint(Cursor.Position);
                anchor = screen.WorkingArea;
            }

            Rectangle area = screen.WorkingArea;
            int x = anchor.Left + (anchor.Width - Width) / 2;
            int y = anchor.Top + (anchor.Height - Height) / 2;

            x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - Width));
            y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - Height));

            SetBounds(x, y, Width, Height);
        }
    }
}
