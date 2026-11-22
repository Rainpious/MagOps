using System.Drawing.Drawing2D;
using MagOps.Core.Routes;
using P = MagOps.Core.SurveyPlanning.SurveyBoundaryPoint;

namespace MagOps.Desktop;

internal sealed partial class TieLineDesigner
{
    private Dictionary<string,int> mainCoverage = [];
    private readonly ListBox flightChoice = new() { Dock = DockStyle.Fill, IntegralHeight = false, FormattingEnabled = true, HorizontalScrollbar = true, AccessibleName = "架次列表" };
    private readonly Label flightSummary = new() { AutoSize = true, Padding = new(3,2,3,4) };
    private TieSortie? CurrentFlight => drawFlights.Checked ? flightChoice.SelectedItem as TieSortie : null;

    private void BuildFlightDisplay(TableLayoutPanel panel)
    {
        panel.ColumnStyles.Add(new(SizeType.Percent, 100));
        panel.Controls.Add(new Label { Text = "架次（可点列表或地图航段切换）", AutoSize = true }, 0, 0);
        panel.Controls.Add(flightChoice, 0, 1);
        panel.Controls.Add(flightSummary, 0, 2);
        flightChoice.Format += (_, e) =>
        {
            if (e.ListItem is not TieSortie sortie) return;
            var distance = context.MeasureWorkFlight(sortie);
            e.Value = $"{sortie.Name}   预计飞行 {distance.FlightM / 1000:F2} km   ·   KML {distance.WorkM / 1000:F2} km   " + string.Join(" → ", sortie.Legs.Select(leg =>
                    leg.Pieces > 1 ? $"{leg.MotherName}（{leg.Sequence}/{leg.Pieces}）" : leg.MotherName));
        };
        flightChoice.SelectedIndexChanged += (_, _) =>
        {
            if (flightChoice.SelectedItem is TieSortie flight &&
                !flight.Legs.Any(l => l.MotherId == SelectedLine?.Id))
                list.SelectedIndex = lines.FindIndex(l => l.Id == flight.Legs[0].MotherId);
            UpdateFlightDisplay();
        };
    }

    private RouteCheckLine? SelectedLine => list.SelectedIndex >= 0 && list.SelectedIndex < lines.Count
        ? lines[list.SelectedIndex] : null;
    private readonly List<(RectangleF Bounds, string LineId)> lineLabels = [];

    private RouteCheckLine[] HighlightedLines()
    {
        if (SelectedLine is not { } line) return [];
        var pair = context.PairMembers(lines,line);
        return pair.Length == 0 ? [line] : pair;
    }

    private void RefreshFlightChoices()
    {
        string? previous = (flightChoice.SelectedItem as TieSortie)?.Name;
        flightChoice.BeginUpdate();
        try
        {
            flightChoice.Items.Clear();
            flightChoice.Items.AddRange(flights.ToArray());
            if (flights.Count > 0)
                flightChoice.SelectedIndex = Math.Max(0, flights.FindIndex(f => f.Name == previous));
        }
        finally { flightChoice.EndUpdate(); }
        UpdateFlightDisplay();
    }

    private static string Direction(P from, P to)
    {
        string[] labels = ["北","东北","东","东南","南","西南","西","西北"];
        double angle = Math.Atan2(to.X-from.X,to.Y-from.Y)*180/Math.PI;
        return labels[(int)Math.Floor((angle+382.5)/45)%8];
    }

    private void UpdateFlightDisplay()
    {
        lineCaption.Text = (SelectedLine is { } selected ? $"选中 {selected.Name}" : "切割线") + " · 区内 / 外延 / 总长（m）";
        flightChoice.Enabled = drawFlights.Checked && flightChoice.Items.Count > 0;
        var steps = new List<string>();
        if (CurrentFlight is { } f)
        {
            for (int i=0;i<f.Legs.Length;i++)
            {
                var leg=f.Legs[i];
                if (i>0 && f.Legs[i-1].End != leg.Start)
                    steps.Add("向"+Direction(f.Legs[i-1].End,leg.Start)+"直连");
                steps.Add(leg.MotherName+" 向"+Direction(leg.Start,leg.End));
            }
        }
        flightSummary.Text = string.Join(" → ",steps);
        map.Invalidate();
    }

    // Clip to the flown window before drawing arrows. Inside runs can be disjoint in concave areas.
    private static IEnumerable<(MapInk Kind, P From, P To)> FlownParts(TieFlightLeg leg, TieLineDesign design)
    {
        double dx=leg.End.X-leg.Start.X, dy=leg.End.Y-leg.Start.Y, length2=dx*dx+dy*dy;
        if (length2 < 1e-10) yield break;
        double Fraction(P p) => Math.Clamp(((p.X-leg.Start.X)*dx+(p.Y-leg.Start.Y)*dy)/length2,0,1);
        P At(double t) => new(leg.Start.X+t*dx, leg.Start.Y+t*dy);
        var intervals = design.Inside.Select(s=>(Segment:s, Kind:MapInk.Group))
            .Append((design.Approach,MapInk.Extension)).Append((design.Departure,MapInk.Extension))
            .Select(item=> { double a=Fraction(item.Item1.Start), b=Fraction(item.Item1.End);
                return (Low:Math.Min(a,b),High:Math.Max(a,b),Kind:item.Item2); })
            .Where(item=>item.High-item.Low>1e-9).OrderBy(item=>item.Low);
        double cursor=0;
        foreach (var part in intervals)
        {
            if (part.Low>cursor+1e-9) yield return (MapInk.Connector,At(cursor),At(part.Low));
            yield return (part.Kind,At(part.Low),At(part.High));
            cursor=part.High;
        }
        if (cursor<1-1e-9) yield return (MapInk.Connector,At(cursor),leg.End);
    }

    private static bool OverlapsSegment(P a, P b, P start, P end)
    {
        double dx=b.X-a.X, dy=b.Y-a.Y, length=Math.Sqrt(dx*dx+dy*dy);
        if (length<.01) return false;
        double Side(P p) => Math.Abs((p.X-a.X)*dy-(p.Y-a.Y)*dx)/length;
        if (Side(start)>.02 || Side(end)>.02) return false;
        double Along(P p) => ((p.X-a.X)*dx+(p.Y-a.Y)*dy)/length;
        double lo=Along(start), hi=Along(end);
        return Math.Min(length,Math.Max(lo,hi))-Math.Max(0,Math.Min(lo,hi))>.02;
    }

    private void PaintMap(object? sender, PaintEventArgs e)
    {
        lineLabels.Clear();
        var rings=context.Region.Rings().ToArray();
        var points=rings.SelectMany(r=>r).Concat(lines.Where(l=>l.Design!=null)
            .SelectMany(l=>new[]{l.Design!.Approach.Start,l.Design.Departure.End})).ToArray();
        double x0=points.Min(p=>p.X), x1=points.Max(p=>p.X), y0=points.Min(p=>p.Y), y1=points.Max(p=>p.Y);
        float u=Unit, width=Math.Max(1,map.Width-LegendWidth);
        scale=Math.Min((width-52*u)/(x1-x0+1),(map.Height-62*u)/(y1-y0+1));
        if (scale<=0) return;
        xmin=(x0+x1-width/scale)/2; ymax=(y0+y1+map.Height/scale)/2;
        PointF Px(P p)=>new((float)((p.X-xmin)*scale),(float)((ymax-p.Y)*scale));
        var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias;
        var used=new HashSet<MapInk>();
        var ink=new Dictionary<MapInk,Pen>();
        Pen PenFor(MapInk key)
        {
            used.Add(key);
            if (!ink.TryGetValue(key,out var pen)) ink[key]=pen=MapPen(key,u);
            return pen;
        }
        var busy=new List<RectangleF>();
        var flight=CurrentFlight;
        var focus=HighlightedLines().Select(l=>l.Id).ToHashSet();
        var byId=lines.DistinctBy(l=>l.Id).ToDictionary(l=>l.Id);
        var markers=new List<(MapInk Kind,P Point)>();
        var labels=new List<(string Text,PointF Anchor,MapInk Kind,string? LineId)>();
        void Mark(MapInk kind,P p)
        {
            markers.Add((kind,p)); var q=Px(p); busy.Add(new(q.X-8*u,q.Y-8*u,16*u,16*u)); used.Add(kind);
        }
        void Stroke(MapInk kind,P a,P b,bool direction=false,bool halo=false,bool inactive=false,float offset=0)
        {
            var start=Px(a); var end=Px(b);
            float dx=end.X-start.X, dy=end.Y-start.Y, length=MathF.Sqrt(dx*dx+dy*dy);
            if (length<.4f) return;
            if (offset != 0)
            {
                float ox=-dy/length*offset, oy=dx/length*offset;
                start.X+=ox; end.X+=ox; start.Y+=oy; end.Y+=oy;
            }
            var pen=PenFor(kind);
            using var faint = inactive ? (Pen)pen.Clone() : null;
            if (faint!=null)
            {
                faint.Color=Style(MapInk.Other).Color;
                faint.Width=1.4f*u;
            }
            if (halo) { using var white=new Pen(Color.White,pen.Width+2.5f*u); g.DrawLine(white,start,end); }
            g.DrawLine(faint??pen,start,end);
            if (!direction || length<24*u) return;
            int count=Math.Max(1,(int)Math.Round(length/(120*u)));
            for (int i=0;i<count;i++)
            {
                float t=(i+.5f)/count;
                var q=new PointF(start.X+t*dx,start.Y+t*dy);
                if (busy.Any(r=>r.Contains(q))) continue;
                Chevron(g,q,dx/length,dy/length,pen.Color,u);
                busy.Add(new(q.X-7*u,q.Y-7*u,14*u,14*u));
            }
        }

        try
        {
            using var tint=new SolidBrush(Color.FromArgb(240,245,243));
            foreach (var poly in context.Region.Polygons)
            {
                using var path=new GraphicsPath(FillMode.Alternate);
                path.AddPolygon(poly.OuterRing.Select(Px).ToArray());
                foreach (var hole in poly.Holes) path.AddPolygon(hole.Select(Px).ToArray());
                g.FillPath(tint,path); g.DrawPath(PenFor(MapInk.Boundary),path);
            }
            string? activeMain=net.SelectedIndex>=0 && net.SelectedIndex<displayedMains.Length ? displayedMains[net.SelectedIndex].LineId : null;
            foreach (var m in context.Mains.OrderBy(m=>m.LineId==activeMain))
            {
                if (!mainCoverage.TryGetValue(m.LineId,out int count)) continue;
                Stroke(m.LineId==activeMain ? MapInk.FocusMain : count switch {0=>MapInk.Zero,1=>MapInk.One,_=>MapInk.Covered},m.Geometry.Start,m.Geometry.End);
            }
            foreach (var line in lines.OrderBy(l=>focus.Contains(l.Id)))
            {
                if (line.Design is not { } d) continue;
                var entireLeg=flight?.Legs.Any(leg=>leg.MotherId==line.Id &&
                    Math.Min(leg.FromFraction,leg.ToFraction)<=1e-9 && Math.Max(leg.FromFraction,leg.ToFraction)>=1-1e-9)==true;
                bool active=focus.Contains(line.Id) && (flight == null || entireLeg);
                var lineInk=active ? MapInk.Group : MapInk.Other;
                foreach (var s in d.Inside) Stroke(lineInk,s.Start,s.End);
                foreach (var s in new[]{d.Approach,d.Departure}) Stroke(MapInk.Extension,s.Start,s.End,inactive:!active);
                if (drawFlights.Checked)
                    foreach (double f in d.CutFractions)
                        Mark(MapInk.Cut,new(d.Approach.Start.X+(d.Departure.End.X-d.Approach.Start.X)*f,
                            d.Approach.Start.Y+(d.Departure.End.Y-d.Approach.Start.Y)*f));
            }
            if (flight is { Legs.Length: >0 })
            {
                var home=flight.LaunchRecovery; Mark(MapInk.Home,home);
                foreach (var leg in flight.Legs)
                {
                    if (leg.FromFraction is >0 and <1) Mark(MapInk.Cut,leg.Start);
                    if (leg.ToFraction is >0 and <1) Mark(MapInk.Cut,leg.End);
                }
                void Link(MapInk kind,P a,P b,string caption)
                {
                    bool overlaps = kind == MapInk.Access && flight.Legs.Any(leg=>OverlapsSegment(a,b,leg.Start,leg.End));
                    Stroke(kind,a,b,true,true,offset:overlaps ? 6*u : 0);
                    var p=Px(a); var q=Px(b);
                    if (Math.Abs(p.X-q.X)+Math.Abs(p.Y-q.Y)>55*u)
                        labels.Add((caption,new((p.X+q.X)/2,(p.Y+q.Y)/2),kind,null));
                }
                Link(MapInk.Access,home,flight.Legs[0].Start,"接入");
                Link(MapInk.Access,flight.Legs[^1].End,home,"返航");
                for (int i=0;i<flight.Legs.Length;i++)
                {
                    var leg=flight.Legs[i];
                    if (i>0)
                    {
                        var prior=flight.Legs[i-1];
                        string caption=prior.ToFraction is 0 or 1 ? Direction(prior.Start,prior.End)+"端直连" : "段间直连";
                        Link(MapInk.Connector,prior.End,leg.Start,caption);
                    }
                    if (!byId.TryGetValue(leg.MotherId,out var line) || line.Design==null) continue;
                    foreach (var part in FlownParts(leg,line.Design))
                        Stroke(part.Kind,part.From,part.To,true,true);
                    labels.Add((leg.MotherName+" 向"+Direction(leg.Start,leg.End),Px(new(
                        leg.Start.X*.68+leg.End.X*.32,leg.Start.Y*.68+leg.End.Y*.32)),MapInk.Group,leg.MotherId));
                }
                labels.Add(("起降",Px(home),MapInk.Home,null));
            }
            foreach (var line in lines.Where(l=>focus.Contains(l.Id) && l.Design!=null && flight?.Legs.Any(leg=>leg.MotherId==l.Id)!=true))
                labels.Add((line.Name,Px(line.Design!.Departure.Start),MapInk.Group,line.Id));

            if (advancedOpen && preview?.Design is { } draft)
            {
                foreach (var s in draft.Inside.Prepend(draft.Approach).Append(draft.Departure)) Stroke(MapInk.Preview,s.Start,s.End);
                foreach (var h in draft.Crossings) { var q=Px(h.Point); g.FillEllipse(Brushes.DarkOrange,q.X-2*u,q.Y-2*u,4*u,4*u); }
            }
            foreach (var mark in markers.Distinct().OrderBy(m=>m.Kind==MapInk.Home)) DrawMapSymbol(g,mark.Kind,Px(mark.Point),u);
            if (HighlightedLines().Length==2)
            {
                PaintPairHandles(g,Px,u); used.Add(MapInk.Handle);
                var q=Px(Center(HighlightedLines())); busy.Add(new(q.X-8*u,q.Y-8*u,16*u,16*u));
            }
            foreach (var label in labels.OrderByDescending(l=>l.LineId != null))
            {
                var size=g.MeasureString(label.Text, Font); var p=label.Anchor;
                foreach (float dy in new[]{-size.Height-5*u,5*u,-size.Height-17*u})
                {
                    var places=new[]{new RectangleF(p.X+11*u,p.Y+dy,size.Width,size.Height),new RectangleF(p.X-11*u-size.Width,p.Y+dy,size.Width,size.Height)};
                    var rect=places.FirstOrDefault(r=>r.Left>=0 && r.Right<=width && r.Top>=0 && r.Bottom<map.Height && !busy.Any(b=>b.IntersectsWith(r)));
                    if (rect.IsEmpty) continue;
                    using var brush=new SolidBrush(Style(label.Kind).Color);
                    g.FillRectangle(Brushes.White,rect); g.DrawString(label.Text,Font,brush,rect.Location);
                    if (label.LineId is { } id) lineLabels.Add((rect,id));
                    busy.Add(rect); break;
                }
            }
            UpdateLegend(used);
        }
        finally { foreach (var pen in ink.Values) pen.Dispose(); }
    }
}
