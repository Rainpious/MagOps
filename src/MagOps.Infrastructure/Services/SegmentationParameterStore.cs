using System.Text.Json;

namespace MagOps.Infrastructure.Services;

/// <summary>User library and per-workarea defaults, separate from immutable review decisions.</summary>
public static class SegmentationParameterStore
{
    public static string LibraryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MagOps", "segmentation-parameter-profiles.json");
    public static string DefaultsPath(string projectRoot, string workarea) => Path.Combine(projectRoot,
        "00_项目管理", "magops", "segmentation-parameters-" + TrackSegmentationReferenceService.Hash(workarea.Replace('\\', '/').ToLowerInvariant())[..16] + ".json");
    public static IReadOnlyDictionary<string, TrackSegmentationInput> LoadLibrary(string? path = null)
    {
        path ??= LibraryPath;
        if (!File.Exists(path)) return new Dictionary<string, TrackSegmentationInput>();
        var profiles = JsonSerializer.Deserialize<Dictionary<string, TrackSegmentationInput>>(File.ReadAllText(path))
            ?? throw new InvalidDataException("参数组文件为空或格式不正确。");
        foreach (var value in profiles.Values) Validate(value);
        return profiles;
    }
    public static void SaveLibrary(string name, TrackSegmentationInput input, string? path = null)
    {
        name = name.Trim();
        if (name.Length == 0 || TrackSegmentationParameterProfiles.Names.Contains(name))
            throw new InvalidDataException("请填写独立的参数组名称，不能覆盖内置组。");
        path ??= LibraryPath;
        var profiles = new Dictionary<string, TrackSegmentationInput>(LoadLibrary(path));
        if (profiles.ContainsKey(name)) throw new InvalidDataException("该名称已存在，请另填名称以保留旧参数组。");
        Validate(input);
        profiles[name] = new() { ParameterProfile = name, ParameterProfileVersion = 1,
            RecognitionOptions = input.RecognitionOptions, MatchOptions = input.MatchOptions, PlannedSpeedMps = input.PlannedSpeedMps };
        Write(path, profiles);
    }
    public static TrackSegmentationInput? LoadDefaults(string path)
    {
        if (!File.Exists(path)) return null;
        var input = JsonSerializer.Deserialize<TrackSegmentationInput>(File.ReadAllText(path))
            ?? throw new InvalidDataException("工区分线参数文件为空。");
        Validate(input); return input;
    }
    public static void SaveDefaults(string path, TrackSegmentationInput input)
    {
        Validate(input);
        // Window ids are batch-specific. They remain in the saved review, not future default batches.
        Write(path, input with { WindowPlannedSpeeds = new Dictionary<string, double>() });
    }
    public static void Validate(TrackSegmentationInput input)
    {
        if (input.ReferenceAuthority is not ("AUTO" or "ASSISTED" or "KML_AUTHORITATIVE"))
            throw new InvalidDataException("参考身份来源无效，请重新选择KML或辅助参考。");
        input.RecognitionOptions.Validate();
        var m = input.MatchOptions;
        WholeTrackGeometryRecoveryService.Validate(m);
        if (new[] { m.MinimumFlightSpeedMps, m.MaximumFlightSpeedMps, m.VerticalMotionHorizontalSpeedMps,
            m.TerminalSearchSpeedMps, m.DepartureAuxiliaryMinimumSpeedMps, m.DepartureAuxiliaryMaximumSpeedMps }.Any(x => !double.IsFinite(x) || x <= 0) ||
            m.MinimumFlightSpeedMps >= m.MaximumFlightSpeedMps || m.DepartureAuxiliaryMinimumSpeedMps >= m.DepartureAuxiliaryMaximumSpeedMps ||
            new[] { m.TerminalStationaryRadiusM, m.TerminalStationarySeconds, m.TerminalVerticalChangeM }
                .Any(x => !double.IsFinite(x) || x <= 0) ||
            m.CoreM > m.CorridorM || m.CorridorM > m.CandidateSearchM ||
            input.PlannedSpeedMps is { } speed && (!double.IsFinite(speed) || speed <= 0 || speed > 100) ||
            input.WindowPlannedSpeeds.Any(p => string.IsNullOrWhiteSpace(p.Key) || !double.IsFinite(p.Value) || p.Value <= 0 || p.Value > 100))
            throw new InvalidDataException("速度必须为有限正数且下限小于上限；核心、容错、搜索通道应依次递增。");
    }
    private static void Write<T>(string path, T data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
