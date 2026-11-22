using System.Drawing.Drawing2D;

namespace MagOps.Desktop;

internal sealed partial class TieLineDesigner
{
    private enum MapInk { Boundary, Zero, One, Covered, FocusMain, Group, Other, Extension, Connector, Access, Home, Handle, Cut, Preview }
    private readonly Button legendToggle = new() { Text = "图例  ▴", AutoSize = true, TabStop = true, FlatStyle = FlatStyle.Flat, BackColor = Color.White };
    private readonly TableLayoutPanel legendHost = new() { Dock = DockStyle.Right, ColumnCount = 1, RowCount = 2, BackColor = Color.White };
    private readonly Canvas legendBody = new() { AutoScroll = true, Dock = DockStyle.Fill, BackColor = Color.White };
    private MapInk[] legendItems = [];
    private bool legendExpanded = true;
    private float Unit => map.DeviceDpi / 96f;

    private static (string Label, Color Color, float Width, DashStyle Dash) Style(MapInk key) => key switch
    {
        MapInk.Boundary => ("工区边界", Color.FromArgb(73,89,103), 1.4f, DashStyle.Solid),
        MapInk.Zero => ("零交会", Color.FromArgb(109,75,158), 1.45f, DashStyle.DashDot),
        MapInk.One => ("一次交会", Color.FromArgb(64,116,148), 1.4f, DashStyle.Dash),
        MapInk.Covered => ("至少两次交会", Color.FromArgb(164,178,187), 1.05f, DashStyle.Solid),
        MapInk.FocusMain => ("选中正式测线", Color.FromArgb(35,70,148), 2.5f, DashStyle.Solid),
        MapInk.Group => ("当前执行段", Color.FromArgb(0,107,111), 3.3f, DashStyle.Solid),
        MapInk.Other => ("其他切割线", Color.FromArgb(121,160,161), 1.6f, DashStyle.Solid),
        MapInk.Extension => ("切割线外延", Color.FromArgb(0,107,111), 2.3f, DashStyle.Custom),
        MapInk.Connector => ("连接段", Color.FromArgb(174,32,103), 2.5f, DashStyle.Dash),
        MapInk.Access => ("接入 / 返航", Color.FromArgb(91,103,117), 2f, DashStyle.Dot),
        MapInk.Home => ("起降点", Color.FromArgb(43,109,56), 1.7f, DashStyle.Solid),
        MapInk.Handle => ("整组拖动手柄", Color.FromArgb(37,81,140), 1.7f, DashStyle.Solid),
        MapInk.Cut => ("母线切点", Color.FromArgb(110,55,152), 1.5f, DashStyle.Solid),
        MapInk.Preview => ("编辑预览", Color.DarkOrange, 2f, DashStyle.Solid),
        _ => throw new ArgumentOutOfRangeException(nameof(key))
    };

    private static Pen MapPen(MapInk key, float unit)
    {
        var style = Style(key);
        var pen = new Pen(style.Color, style.Width * unit);
        if (style.Dash == DashStyle.Custom) pen.DashPattern = [6f, 3f];
        else pen.DashStyle = style.Dash;
        return pen;
    }

    private static void DrawMapSymbol(Graphics g, MapInk key, PointF p, float unit)
    {
        float r = unit * (key == MapInk.Cut ? 3.5f : 6.5f);
        using var pen = MapPen(key,unit);
        using var fill = new SolidBrush(key == MapInk.Home ? Color.Honeydew : key == MapInk.Cut ? pen.Color : Color.White);
        if (key == MapInk.Cut) { g.FillRectangle(fill,p.X-r,p.Y-r,2*r,2*r); return; }
        PointF[] shape = key == MapInk.Home
            ? [new(p.X,p.Y-r),new(p.X+r,p.Y+r),new(p.X-r,p.Y+r)]
            : [new(p.X,p.Y-r),new(p.X+r,p.Y),new(p.X,p.Y+r),new(p.X-r,p.Y)];
        g.FillPolygon(fill,shape); g.DrawPolygon(pen,shape);
    }

    private static void Chevron(Graphics g, PointF p, float dx, float dy, Color color, float unit)
    {
        float r = 9f * unit;
        PointF[] path = [new(p.X-r*dx/2-r*dy*.5f,p.Y-r*dy/2+r*dx*.5f),
                        new(p.X+r*dx/2,p.Y+r*dy/2),
                        new(p.X-r*dx/2+r*dy*.5f,p.Y-r*dy/2-r*dx*.5f)];
        using var halo = new Pen(Color.White, 4.5f*unit) { LineJoin = LineJoin.Round };
        using var ink = new Pen(color, 2.1f*unit) { LineJoin = LineJoin.Round };
        g.DrawLines(halo,path); g.DrawLines(ink,path);
    }

    private void InstallMapLegend()
    {
        legendToggle.FlatAppearance.BorderColor = Color.Silver;
        legendToggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        legendBody.Paint += PaintLegend;
        legendHost.RowStyles.Add(new(SizeType.AutoSize));
        legendHost.RowStyles.Add(new(SizeType.Percent,100));
        legendHost.ColumnStyles.Add(new(SizeType.Percent,100));
        legendHost.Controls.Add(legendToggle,0,0);
        legendHost.Controls.Add(legendBody,0,1);
        map.Controls.Add(legendHost);
        void ResizeLegend()
        {
            // Leave room for the count, units, and a scrollbar in short windows.
            legendHost.Width=legendExpanded ? TextRenderer.MeasureText("至少两次交会  999 条",Font).Width
                + (int)Math.Ceiling(61*Unit) + SystemInformation.VerticalScrollBarWidth
                : legendToggle.PreferredSize.Width + legendToggle.Margin.Horizontal;
            legendBody.Visible=legendExpanded;
            map.Invalidate();
        }
        legendToggle.Click += (_,_) =>
        {
            legendExpanded = !legendExpanded;
            legendToggle.Text = legendExpanded ? "图例  ▴" : "图例  ▾";
            ResizeLegend();
        };
        map.DpiChangedAfterParent += (_,_) => ResizeLegend();
        ResizeLegend();
    }

    private float LegendWidth => legendHost.Width;

    private void UpdateLegend(HashSet<MapInk> visible)
    {
        legendBody.Visible=legendExpanded;
        if (!legendExpanded) return;
        var entries=Enum.GetValues<MapInk>().Where(visible.Contains).ToArray();
        int step=(int)Math.Ceiling(Math.Max(23*Unit,Font.Height+3*Unit));
        legendBody.AutoScrollMinSize=new Size(0,step*entries.Length);
        legendItems=entries;
        legendBody.Invalidate();
    }

    private void PaintLegend(object? sender, PaintEventArgs e)
    {
        var g=e.Graphics;
        g.SmoothingMode=SmoothingMode.AntiAlias;
        float u=Unit, x=0, top=legendBody.AutoScrollPosition.Y;
        int step=(int)Math.Ceiling(Math.Max(23*u,Font.Height+3*u));
        using var background = new SolidBrush(Color.FromArgb(249,251,252));
        g.FillRectangle(background,0,top,legendBody.ClientSize.Width,step*legendItems.Length);
        for (int i=0;i<legendItems.Length;i++)
        {
            var kind=legendItems[i]; float cy=top+(i+.5f)*step;
            using var pen = MapPen(kind,u);
            if (kind is MapInk.Home or MapInk.Handle or MapInk.Cut)
                DrawMapSymbol(g,kind,new(x+25*u,cy),u);
            else
            {
                g.DrawLine(pen,x+5*u,cy,x+45*u,cy);
                if (kind is MapInk.Connector or MapInk.Access || kind == MapInk.Group && CurrentFlight != null)
                    Chevron(g,new(x+25*u,cy),1,0,pen.Color,u);
            }
            string label=kind == MapInk.Group && CurrentFlight == null ? "当前线组" : Style(kind).Label;
            int? count=kind switch
            {
                MapInk.Zero=>mainCoverage.Values.Count(n=>n==0),
                MapInk.One=>mainCoverage.Values.Count(n=>n==1),
                MapInk.Covered=>mainCoverage.Values.Count(n=>n>=2),
                _=>null
            };
            if (count.HasValue) label+=$"  {count} 条";
            g.DrawString(label, Font, Brushes.DarkSlateGray, x+49*u,cy-Font.Height/2f);
        }
    }
}
