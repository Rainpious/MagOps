namespace MagOps.Core.Geomagnetics;

/// <summary>
/// One epoch block from Oasis/Geosoft-style igrf.dat.
/// The source file stores rows implicitly in n=1..N, m=0..n order.
/// </summary>
public sealed class IgrfEpochModel
{
    private readonly double[,] _g;
    private readonly double[,] _h;
    private readonly double[,] _gDot;
    private readonly double[,] _hDot;

    internal IgrfEpochModel(int epochYear, int maxDegree, IReadOnlyList<IgrfCoefficient> coefficients)
    {
        if (epochYear < 0)
            throw new ArgumentOutOfRangeException(nameof(epochYear));
        if (maxDegree < 1)
            throw new ArgumentOutOfRangeException(nameof(maxDegree));

        EpochYear = epochYear;
        MaxDegree = maxDegree;
        Coefficients = coefficients ?? throw new ArgumentNullException(nameof(coefficients));

        _g = new double[maxDegree + 1, maxDegree + 1];
        _h = new double[maxDegree + 1, maxDegree + 1];
        _gDot = new double[maxDegree + 1, maxDegree + 1];
        _hDot = new double[maxDegree + 1, maxDegree + 1];

        foreach (IgrfCoefficient c in coefficients)
        {
            if (c.Degree < 1 || c.Degree > maxDegree || c.Order < 0 || c.Order > c.Degree)
                throw new ArgumentException("Invalid IGRF degree/order tuple.", nameof(coefficients));

            _g[c.Degree, c.Order] = c.G;
            _h[c.Degree, c.Order] = c.H;
            _gDot[c.Degree, c.Order] = c.GDot;
            _hDot[c.Degree, c.Order] = c.HDot;
        }
    }

    public int EpochYear { get; }

    public int MaxDegree { get; }

    public IReadOnlyList<IgrfCoefficient> Coefficients { get; }

    public IgrfCoefficient GetCoefficient(int degree, int order)
    {
        if (degree < 1 || degree > MaxDegree)
            throw new ArgumentOutOfRangeException(nameof(degree));
        if (order < 0 || order > degree)
            throw new ArgumentOutOfRangeException(nameof(order));

        return new IgrfCoefficient(
            degree,
            order,
            _g[degree, order],
            _h[degree, order],
            _gDot[degree, order],
            _hDot[degree, order]);
    }

    internal double G(int degree, int order) => _g[degree, order];
    internal double H(int degree, int order) => _h[degree, order];
    internal double GDot(int degree, int order) => _gDot[degree, order];
    internal double HDot(int degree, int order) => _hDot[degree, order];
}
