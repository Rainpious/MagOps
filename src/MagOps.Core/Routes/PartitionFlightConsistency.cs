using MagOps.Core.Routes;
namespace MagOps.Core.Routes;
/// <summary>Keep snake routing intact; choose whole-sortie reversal maximizing mother-line direction agreement.</summary>
public static class PartitionFlightConsistency
{
    public static Dictionary<string,double[]> Capture(RoutePlanResult plan)
    {
        var result=new Dictionary<string,double[]>(StringComparer.OrdinalIgnoreCase);
        foreach(var sortie in plan.Sorties)
        for(int i=1;i<sortie.Points.Count;i++) {
            var a=sortie.Points[i-1];var b=sortie.Points[i];
            if(a.Line.Equals(b.Line,StringComparison.OrdinalIgnoreCase))result[a.Line]=[b.X-a.X,b.Y-a.Y];
        }
        return result;
    }
    public static void Align(RoutePlanResult plan,IReadOnlyDictionary<string,double[]> reference)
    {
        int disagreements=0;
        foreach(var sortie in plan.Sorties) {
            int same=0,opposite=0;
            for(int i=1;i<sortie.Points.Count;i++){
                var a=sortie.Points[i-1];var b=sortie.Points[i];
                if(!a.Line.Equals(b.Line,StringComparison.OrdinalIgnoreCase)||!reference.TryGetValue(a.Line,out var v)||v.Length!=2)continue;
                double dot=(b.X-a.X)*v[0]+(b.Y-a.Y)*v[1];
                if(dot>0)same++;else if(dot<0)opposite++;
            }
            if(opposite>same) {
                sortie.Points.Reverse();
                sortie.SourceLines.Reverse();
            }
            disagreements+=Math.Min(same,opposite);
        }
        if (plan.Sorties.FirstOrDefault()?.Points.FirstOrDefault() is { } start)
        {
            plan.StartPoint = start;
            plan.ActiveStartEndpoint = start.Endpoint;
        }
        plan.OrderedLines.Clear();
        plan.OrderedLines.AddRange(plan.Sorties.SelectMany(s => s.SourceLines));
        if(disagreements>0)plan.Warnings.Add($"与母区飞行方向仍有 {disagreements} 条不一致；已优先保留蛇形连续连接，请人工核对。");
    }
}
