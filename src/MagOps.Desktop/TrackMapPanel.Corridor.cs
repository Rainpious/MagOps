using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed partial class TrackMapPanel
{
    private PlannedSurveyLineTask? _corridorTask;
    private double _corridorM;
    internal void SetCorridor(PlannedSurveyLineTask? task, double widthM)
    { _corridorTask = task; _corridorM = widthM; Invalidate(); }

    private void DrawCorridor(Graphics graphics)
    {
        if (_corridorTask is not { } t || !_geometry.HasGeometry || _corridorM <= 0) return;
        var view = CurrentViewTransform();
        var a = _geometry.Project(t.Start); var b = _geometry.Project(t.End);
        double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return;
        var polygon = new[] { view.ToRenderScreen((a.X - dy / len * _corridorM, a.Y + dx / len * _corridorM)),
            view.ToRenderScreen((b.X - dy / len * _corridorM, b.Y + dx / len * _corridorM)),
            view.ToRenderScreen((b.X + dy / len * _corridorM, b.Y - dx / len * _corridorM)),
            view.ToRenderScreen((a.X + dy / len * _corridorM, a.Y - dx / len * _corridorM)) };
        using var fill = new SolidBrush(Color.FromArgb(25, 60, 140, 210)); graphics.FillPolygon(fill, polygon);
        using var pen = new Pen(Color.FromArgb(130, 60, 140, 210), 1) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        graphics.DrawPolygon(pen, polygon);
        graphics.DrawString($"参考走廊 ±{_corridorM:0.#}m（非实测补点）", Font, Brushes.SlateGray, 12, Height - Font.Height - 8);
    }
}
