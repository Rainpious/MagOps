using System.Globalization;

namespace MagOps.Infrastructure.Services;

public enum OasisScaleMode { Auto, Full, Locked }

public sealed record OasisMapScale(OasisScaleMode Mode = OasisScaleMode.Auto,
    double Field = 0, double FirstDerivative = 0, double SecondDerivative = 0)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Mode) || Mode == OasisScaleMode.Locked &&
            new[] { Field, FirstDerivative, SecondDerivative }.Any(v => !double.IsFinite(v) || v <= 0))
            throw new InvalidDataException("锁定色标时请为三个量纲各填写一个大于零的有限上限。");
    }

    public string Method(string unit) => Mode switch
    {
        OasisScaleMode.Locked => "手动锁定",
        OasisScaleMode.Auto when unit != "nT" => "P99",
        _ => "全量程"
    };

    public double Candidate(float[] values, string unit)
    {
        if (Mode == OasisScaleMode.Locked)
            return unit switch { "nT" => Field, "nT/m" => FirstDerivative, "nT/m²" => SecondDerivative,
                _ => throw new InvalidDataException($"未知量纲 {unit}") };
        var valid = values.Where(float.IsFinite).Select(Math.Abs);
        if (Method(unit) != "P99") return valid.Max();
        var sorted = valid.ToArray();
        Array.Sort(sorted);
        double index = (sorted.Length - 1) * 0.99;
        int low = (int)index, high = (int)Math.Ceiling(index);
        return sorted[low] + (index - low) * ((double)sorted[high] - sorted[low]);
    }

    public double FinalLimit(double candidate)
    {
        if (Mode == OasisScaleMode.Locked) return candidate;
        if (candidate == 0) return 1;
        double step = Math.Pow(10, Math.Floor(Math.Log10(candidate)));
        if (candidate < 2 * step) step /= 10;
        return Math.Ceiling(candidate / step) * step;
    }
}

public sealed record OasisMapLegend(string Name, string Title, string Unit, double Limit, string Method,
    int ValidCells, int ClippedCells)
{
    public string Caption => Method == "全量程" ? Method :
        string.Create(CultureInfo.InvariantCulture, $"{Method} · 超限 {100d * ClippedCells / ValidCells:F2}%");
}
