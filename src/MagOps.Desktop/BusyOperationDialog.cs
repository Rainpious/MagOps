using System.Drawing;
using System.Windows.Forms;

namespace MagOps.Desktop;

/// <summary>
/// Small modeless wait prompt used before long synchronous desktop operations.
/// It is painted before work starts so large folder scans/imports never look like
/// an unresponsive click with no feedback.
/// </summary>
internal sealed class BusyOperationDialog : Form
{
    private readonly Label _detail;

    private BusyOperationDialog(string title, string message)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ControlBox = false;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.Dpi;
        UseWaitCursor = true;
        Padding = new Padding(18, 16, 18, 16);
        ClientSize = new Size(470, 126);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var heading = new Label
        {
            Text = "正在处理，请稍候…",
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8)
        };
        var detail = new Label
        {
            Text = message,
            AutoSize = true,
            MaximumSize = new Size(430, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 12)
        };
        _detail = detail;
        var progress = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 18,
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 28,
            UseWaitCursor = true
        };

        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(detail, 0, 1);
        layout.Controls.Add(progress, 0, 2);
        Controls.Add(layout);
    }

    public static BusyOperationDialog ShowFor(
        Form owner,
        Control centerSurface,
        string title,
        string message)
    {
        var dialog = new BusyOperationDialog(title, message);
        dialog.CenterOver(centerSurface, owner);
        dialog.Show(owner);
        dialog.BringToFront();
        dialog.Refresh();

        // Give Windows one message-loop turn so the prompt is visibly painted
        // before the long synchronous work begins.
        System.Windows.Forms.Application.DoEvents();
        return dialog;
    }

    /// <summary>Runs captured, control-free work off the UI thread while a modal
    /// prompt keeps painting and prevents concurrent edits. No partial cancellation
    /// is offered for operations without transactional cancellation support.</summary>
    public static T Run<T>(Form owner, string title, string message, Func<T> work)
    {
        // Headless construction/checks have no message loop or visible owner.
        if (!owner.IsHandleCreated || !owner.Visible) return work();
        using var dialog = new BusyOperationDialog(title, message);
        dialog.CenterOver(owner, owner);
        T result = default!;
        Exception? failure = null;
        bool completed = false;
        dialog.FormClosing += (_, e) => { if (!completed) e.Cancel = true; };
        dialog.Shown += async (_, _) =>
        {
            try { result = await Task.Run(work); }
            catch (Exception ex) { failure = ex; }
            finally { completed = true; dialog.Close(); }
        };
        dialog.ShowDialog(owner);
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    public static T RunWithProgress<T>(Form owner, string title, string message,
        Func<Action<string>, T> work)
    {
        if (!owner.IsHandleCreated || !owner.Visible) return work(_ => { });
        using var dialog = new BusyOperationDialog(title, message);
        dialog.CenterOver(owner, owner);
        T result = default!;
        Exception? failure = null;
        bool completed = false;
        dialog.FormClosing += (_, e) => { if (!completed) e.Cancel = true; };
        dialog.Shown += async (_, _) =>
        {
            // Progress is created on the UI synchronization context; workers never read controls.
            var progress = new Progress<string>(value =>
            {
                if (!dialog.IsDisposed && !dialog.Disposing) dialog._detail.Text = value;
            });
            try { result = await Task.Run(() => work(value => ((IProgress<string>)progress).Report(value))); }
            catch (Exception ex) { failure = ex; }
            finally { completed = true; dialog.Close(); }
        };
        dialog.ShowDialog(owner);
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    private void CenterOver(Control? centerSurface, Form owner)
    {
        Control surface =
            centerSurface is not null &&
            !centerSurface.IsDisposed &&
            centerSurface.Visible
                ? centerSurface
                : owner;

        Rectangle target;
        try
        {
            target = surface.RectangleToScreen(surface.ClientRectangle);
        }
        catch
        {
            target = owner.RectangleToScreen(owner.ClientRectangle);
        }

        if (target.Width <= 0 || target.Height <= 0)
            target = owner.RectangleToScreen(owner.ClientRectangle);

        int x = target.Left + (target.Width - Width) / 2;
        int y = target.Top + (target.Height - Height) / 2;

        Rectangle workingArea = Screen.FromRectangle(target).WorkingArea;
        x = Math.Clamp(
            x,
            workingArea.Left,
            Math.Max(workingArea.Left, workingArea.Right - Width));
        y = Math.Clamp(
            y,
            workingArea.Top,
            Math.Max(workingArea.Top, workingArea.Bottom - Height));

        SetBounds(
            x,
            y,
            Width,
            Height,
            BoundsSpecified.Location);
    }
}
