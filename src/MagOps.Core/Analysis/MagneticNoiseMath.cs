namespace MagOps.Core.Analysis;

/// <summary>Formula authority shared by report, analysis tool and independent checks.</summary>
public static class MagneticNoiseMath
{
    public const string StaticFormula = "magnetic-static-v1: sample_sd(D4/16)";
    public const string DynamicFormula = "magnetic-dynamic-v1: sample_sd(D4)/sqrt(70)";

    public static double FourthDifference(double a, double b, double c, double d, double e) =>
        a - 4 * b + 6 * c - 4 * d + e;

    public static double? SampleStandardDeviation(IEnumerable<double> values)
    {
        int n = 0;
        double mean = 0, m2 = 0;
        foreach (double value in values)
        {
            if (!double.IsFinite(value)) throw new ArgumentException("标准差输入必须为有限数值。");
            n++;
            double delta = value - mean;
            mean += delta / n;
            m2 += delta * (value - mean);
        }
        return n < 2 ? null : Math.Sqrt(Math.Max(0, m2 / (n - 1)));
    }

    public static double? Noise(IEnumerable<double> differences, bool isStatic)
    {
        double? sd = SampleStandardDeviation(differences);
        return sd / (isStatic ? 16 : Math.Sqrt(70));
    }
}
