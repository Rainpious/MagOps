namespace MagOps.Core.Analysis;

public sealed record OasisGdbInspection(
    string Path,
    IReadOnlyList<string> Channels,
    IReadOnlyList<string> Lines,
    IReadOnlyList<string> PlaceholderChannels);

public sealed record OasisChannelMapping(
    string DestinationChannel,
    string SourceChannel,
    string MatchMode);

public sealed record OasisGdbMappingPlan(
    IReadOnlyList<OasisChannelMapping> AutomaticMappings,
    IReadOnlyList<string> UnmatchedDestinationChannels,
    IReadOnlyList<string> UnmatchedSourceChannels,
    IReadOnlyList<string> DestinationPlaceholderChannels,
    IReadOnlyList<string> SourcePlaceholderChannels)
{
    public bool NeedsUserMapping => UnmatchedSourceChannels.Count > 0;
}

/// <summary>
/// Builds safe channel-name mappings for appending one Geosoft GDB into another.
/// Exact channel names are matched first. A single detected Oasis placeholder channel
/// (values such as i--- / i010 / i027) is allowed to match another placeholder channel
/// regardless of its channel name. All other non-matching source channels must be
/// explicitly mapped or ignored by the user.
/// </summary>
public static class OasisGdbHeaderMapping
{
    public static OasisGdbMappingPlan BuildPlan(
        OasisGdbInspection destination,
        OasisGdbInspection source)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(source);

        string[] destinationChannels = NormalizeUnique(destination.Channels, "基准数据库");
        string[] sourceChannels = NormalizeUnique(source.Channels, "追加数据库");

        var destinationByName = destinationChannels.ToDictionary(
            x => x,
            x => x,
            StringComparer.OrdinalIgnoreCase);
        var sourceByName = sourceChannels.ToDictionary(
            x => x,
            x => x,
            StringComparer.OrdinalIgnoreCase);

        var automatic = new List<OasisChannelMapping>();
        var usedDestination = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedSource = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string sourceName in sourceChannels)
        {
            if (!destinationByName.TryGetValue(sourceName, out string? destinationName))
                continue;

            automatic.Add(new OasisChannelMapping(destinationName, sourceName, "同名自动"));
            usedDestination.Add(destinationName);
            usedSource.Add(sourceName);
        }

        string[] destinationPlaceholders = NormalizeSubset(
            destination.PlaceholderChannels,
            destinationByName,
            "基准数据库占位列");
        string[] sourcePlaceholders = NormalizeSubset(
            source.PlaceholderChannels,
            sourceByName,
            "追加数据库占位列");

        string[] remainingDestinationPlaceholders = destinationPlaceholders
            .Where(x => !usedDestination.Contains(x))
            .ToArray();
        string[] remainingSourcePlaceholders = sourcePlaceholders
            .Where(x => !usedSource.Contains(x))
            .ToArray();

        if (remainingDestinationPlaceholders.Length == 1 &&
            remainingSourcePlaceholders.Length == 1)
        {
            string destinationPlaceholder = remainingDestinationPlaceholders[0];
            string sourcePlaceholder = remainingSourcePlaceholders[0];
            automatic.Add(new OasisChannelMapping(
                destinationPlaceholder,
                sourcePlaceholder,
                "占位列自动"));
            usedDestination.Add(destinationPlaceholder);
            usedSource.Add(sourcePlaceholder);
        }

        string[] unmatchedDestination = destinationChannels
            .Where(x => !usedDestination.Contains(x))
            .ToArray();
        string[] unmatchedSource = sourceChannels
            .Where(x => !usedSource.Contains(x))
            .ToArray();

        return new OasisGdbMappingPlan(
            automatic,
            unmatchedDestination,
            unmatchedSource,
            destinationPlaceholders,
            sourcePlaceholders);
    }

    public static IReadOnlyList<OasisChannelMapping> ResolveMappings(
        OasisGdbMappingPlan plan,
        IReadOnlyDictionary<string, string?> manualSourceToDestination)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(manualSourceToDestination);

        var sourceSet = new HashSet<string>(
            plan.UnmatchedSourceChannels,
            StringComparer.OrdinalIgnoreCase);
        var destinationSet = new HashSet<string>(
            plan.UnmatchedDestinationChannels,
            StringComparer.OrdinalIgnoreCase);
        var usedDestination = new HashSet<string>(
            plan.AutomaticMappings.Select(x => x.DestinationChannel),
            StringComparer.OrdinalIgnoreCase);

        var resolved = plan.AutomaticMappings.ToList();
        foreach (string source in plan.UnmatchedSourceChannels)
        {
            if (!TryGetIgnoreCase(manualSourceToDestination, source, out string? destination) ||
                string.IsNullOrWhiteSpace(destination))
            {
                continue;
            }

            destination = destination.Trim();
            if (!destinationSet.Contains(destination))
                throw new InvalidDataException(
                    $"追加数据库表头 {source} 映射到了无效的基准表头：{destination}");
            if (!usedDestination.Add(destination))
                throw new InvalidDataException(
                    $"基准表头 {destination} 被多个追加表头重复映射，请重新选择。");

            resolved.Add(new OasisChannelMapping(destination, source, "用户选择"));
        }

        foreach (string key in manualSourceToDestination.Keys)
        {
            if (!sourceSet.Contains(key))
                throw new InvalidDataException($"未知的追加数据库表头映射：{key}");
        }

        return resolved;
    }

    private static string[] NormalizeUnique(IEnumerable<string> names, string label)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in names)
        {
            string name = (raw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (!seen.Add(name))
                throw new InvalidDataException($"{label}存在重复表头：{name}");
            result.Add(name);
        }
        return result.ToArray();
    }

    private static string[] NormalizeSubset(
        IEnumerable<string> names,
        IReadOnlyDictionary<string, string> channelByName,
        string label)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in names)
        {
            string name = (raw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                continue;
            if (!channelByName.TryGetValue(name, out string? canonical))
                throw new InvalidDataException($"{label}引用了不存在的表头：{name}");
            result.Add(canonical);
        }
        return result.ToArray();
    }

    private static bool TryGetIgnoreCase(
        IReadOnlyDictionary<string, string?> source,
        string key,
        out string? value)
    {
        if (source.TryGetValue(key, out value))
            return true;

        foreach (var pair in source)
        {
            if (!pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                continue;
            value = pair.Value;
            return true;
        }

        value = null;
        return false;
    }
}
