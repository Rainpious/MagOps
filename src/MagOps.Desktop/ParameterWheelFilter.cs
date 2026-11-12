using System.Runtime.InteropServices;

namespace MagOps.Desktop;

/// <summary>Hovering a parameter must not edit it while the user scrolls a page.</summary>
internal sealed class ParameterWheelFilter : IMessageFilter, IDisposable
{
    private const int MouseWheel = 0x020A;
    private bool _forwarding;

    public ParameterWheelFilter() => System.Windows.Forms.Application.AddMessageFilter(this);
    public void Dispose() => System.Windows.Forms.Application.RemoveMessageFilter(this);

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != MouseWheel || _forwarding) return false;
        // Wheel messages may target the focused window rather than the hovered window.
        long packed = m.LParam.ToInt64();
        var point = new Point(unchecked((short)packed), unchecked((short)(packed >> 16)));
        Control? hovered = Control.FromChildHandle(WindowFromPoint(point));
        return RouteHoveredWheel(hovered, ref m);
    }

    internal bool RouteHoveredWheel(Control? hovered, ref Message m)
    {
        Control? parameter = ParameterOf(hovered);
        if (parameter is null)
        {
            // Windows can address the focused parameter even after the pointer leaves it.
            if (ParameterOf(Control.FromChildHandle(m.HWnd)) is null) return false;
            Forward(hovered, ref m);
            return true;
        }
        if (parameter.ContainsFocus) return false;

        for (Control? parent = parameter.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is not ScrollableControl { AutoScroll: true, VerticalScroll.Visible: true }) continue;
            Forward(parent, ref m);
            break;
        }
        return true; // Protect the value even when the page has no scrollable ancestor.
    }

    private static Control? ParameterOf(Control? control)
    {
        while (control is not null && control is not NumericUpDown && control is not ComboBox)
            control = control.Parent;
        return control;
    }

    private void Forward(Control? target, ref Message m)
    {
        if (target is null || target.IsDisposed) return;
        try
        {
            _forwarding = true;
            SendMessage(target.Handle, m.Msg, m.WParam, m.LParam);
        }
        finally { _forwarding = false; }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
