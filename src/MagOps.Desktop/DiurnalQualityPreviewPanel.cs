using MagOps.Infrastructure.Services;

namespace MagOps.Desktop;

internal sealed class DiurnalQualityPreviewPanel : Panel
{
    private readonly PreviewNavigation _navigation;
    private (DiurnalQualityPoint Point,double X,double Y)[] _points=[];
    private double _minX,_maxX,_minY,_maxY;
    private DiurnalQualityInterval? _selected;
    public DiurnalQualityPreviewPanel()
    { Dock=DockStyle.Fill;DoubleBuffered=true;BackColor=SystemColors.Window;
        SetStyle(ControlStyles.Selectable,true);
        _navigation=new(this);AccessibleName="日变质量空间预览"; }
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal DiurnalQualityInterval? Selected {get=>_selected;set{_selected=value;Invalidate();}}
    public void SetSnapshot(DiurnalQualitySnapshot? snapshot)
    {
        _selected=null;
        var points=snapshot?.Points.Where(p=>p.Source.Longitude is { } lon && p.Source.Latitude is { } lat &&
            WorkareaSpatialService.ValidCoordinate(lon,lat)).ToArray() ?? [];
        _points=[];
        if(points.Length>0)
        {
            double lon0=points.Average(p=>p.Source.Longitude!.Value),lat0=points.Average(p=>p.Source.Latitude!.Value);
            double factor=111319.49*Math.Cos(lat0*Math.PI/180);
            _points=points.Select(p=>(p,(p.Source.Longitude!.Value-lon0)*factor,(p.Source.Latitude!.Value-lat0)*111319.49)).ToArray();
            _minX=_points.Min(p=>p.X);_maxX=_points.Max(p=>p.X);_minY=_points.Min(p=>p.Y);_maxY=_points.Max(p=>p.Y);
        }
        _navigation.SetDataKey(snapshot is null ? null : new {snapshot.Fingerprint,snapshot.Scope});Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var focusCue = _navigation.BeginPaint(e.Graphics);
        if(_points.Length==0){TextRenderer.DrawText(e.Graphics,"暂无有效空间坐标；时间区间仍可在表中查看。",Font,ClientRectangle,SystemColors.GrayText);return;}
        double scale=Math.Min(Math.Max(1,Width-32)/Math.Max(1,_maxX-_minX),Math.Max(1,Height-32)/Math.Max(1,_maxY-_minY));
        int stride=Math.Max(1,_points.Length/30000);
        using var normal=new SolidBrush(Color.SteelBlue);using var failed=new SolidBrush(Color.DarkOrange);using var excluded=new SolidBrush(SystemColors.GrayText);
        using var outline=new Pen(SystemColors.WindowText,1.5f);
        for(int i=0;i<_points.Length;i++)
        {
            var p=_points[i].Point;bool selected=_selected is { } s && p.Source.SourceFile==s.SourceFile && p.Source.SourceRecord>=s.FirstRecord && p.Source.SourceRecord<=s.LastRecord && p.Source.TimestampUtc>=s.StartUtc && p.Source.TimestampUtc<=s.EndUtc;
            if(!selected && p.Category=="已改正" && i%stride!=0)continue;
            PointF pt=_navigation.Apply(new((float)(Width/2d+(_points[i].X-(_minX+_maxX)/2)*scale),
                (float)(Height/2d-(_points[i].Y-(_minY+_maxY)/2)*scale)));
            e.Graphics.FillEllipse(p.Category=="输入资格排除"?excluded:p.Category=="未改正"?failed:normal,pt.X-2,pt.Y-2,4,4);
            if(selected)e.Graphics.DrawEllipse(outline,pt.X-4,pt.Y-4,8,8);
        }
    }
    protected override void Dispose(bool disposing){if(disposing)_navigation.Dispose();base.Dispose(disposing);}
}
