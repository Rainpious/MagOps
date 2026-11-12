namespace MagOps.Core.Geomagnetics;

/// <summary>
/// Parsed IGRF forward-model epochs from one source file.
/// </summary>
public sealed class IgrfModelSet
{
    private readonly IReadOnlyList<IgrfEpochModel> _epochs;

    internal IgrfModelSet(
        IReadOnlyList<IgrfEpochModel> epochs,
        string? sourcePath,
        string? sourceSha256)
    {
        if (epochs is null || epochs.Count == 0)
            throw new ArgumentException("At least one IGRF epoch is required.", nameof(epochs));

        _epochs = epochs.OrderBy(x => x.EpochYear).ToArray();
        SourcePath = sourcePath;
        SourceSha256 = sourceSha256;
    }

    public IReadOnlyList<IgrfEpochModel> Epochs => _epochs;

    public string? SourcePath { get; }

    public string? SourceSha256 { get; }

    public int EarliestEpochYear => _epochs[0].EpochYear;

    public int LatestEpochYear => _epochs[^1].EpochYear;

    public IgrfEpochModel GetEpoch(int epochYear)
    {
        IgrfEpochModel? model = _epochs.FirstOrDefault(x => x.EpochYear == epochYear);
        return model ?? throw new ArgumentOutOfRangeException(
            nameof(epochYear),
            epochYear,
            $"IGRF epoch {epochYear} is not present in the model set.");
    }

    /// <summary>
    /// Oasis-style automatic model selection: choose the most recent model year
    /// not later than the requested decimal year.
    /// </summary>
    public IgrfEpochModel SelectEpoch(double decimalYear)
    {
        IgrfEpochModel? selected = null;
        foreach (IgrfEpochModel model in _epochs)
        {
            if (model.EpochYear > decimalYear)
                break;
            selected = model;
        }

        return selected ?? throw new ArgumentOutOfRangeException(
            nameof(decimalYear),
            decimalYear,
            $"Date is earlier than the first IGRF epoch {_epochs[0].EpochYear}.");
    }
}
