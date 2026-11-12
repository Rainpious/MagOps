using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using MagOps.Core.Geomagnetics;

namespace MagOps.Infrastructure.Services;

public sealed record IgrfConfigurationState(
    string SourcePath,
    string Sha256,
    int EarliestEpochYear,
    int LatestEpochYear,
    bool UsesUserOverride);

/// <summary>
/// Loads the bundled IGRF baseline and manages a validated, reversible user override.
/// The installed application directory is never used as a write target.
/// </summary>
public sealed class IgrfConfigurationService
{
    public const string EmbeddedResourceName =
        "MagOps.Infrastructure.Resources.Geomagnetics.igrf.dat";

    private readonly string _configurationRoot;

    public IgrfConfigurationService(string? configurationRoot = null)
    {
        _configurationRoot = Path.GetFullPath(configurationRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MagOps",
            "config"));
    }

    public string UserOverridePath => Path.Combine(
        _configurationRoot,
        "geomagnetics",
        "igrf.dat");

    public IgrfModelSet LoadCurrent()
    {
        if (File.Exists(UserOverridePath))
            return IgrfDatParser.Load(UserOverridePath);

        byte[] bytes = ReadEmbeddedBytes();
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return IgrfDatParser.Parse(
            Encoding.UTF8.GetString(bytes),
            "embedded://MagOps.Infrastructure/igrf.dat",
            hash);
    }

    public IgrfConfigurationState GetState()
    {
        IgrfModelSet model = LoadCurrent();
        return new IgrfConfigurationState(
            model.SourcePath ?? "embedded://MagOps.Infrastructure/igrf.dat",
            model.SourceSha256 ?? "",
            model.EarliestEpochYear,
            model.LatestEpochYear,
            File.Exists(UserOverridePath));
    }

    public IgrfConfigurationState UpdateFromFile(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        byte[] bytes = File.ReadAllBytes(sourcePath);
        string sourceHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        _ = IgrfDatParser.Parse(Encoding.UTF8.GetString(bytes), sourcePath, sourceHash);

        string directory = Path.GetDirectoryName(UserOverridePath)!;
        Directory.CreateDirectory(directory);
        string temporary = UserOverridePath + ".staging-" + Guid.NewGuid().ToString("N");
        string backup = UserOverridePath + ".previous-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            _ = IgrfDatParser.Load(temporary);
            if (File.Exists(UserOverridePath))
                File.Replace(temporary, UserOverridePath, backup, ignoreMetadataErrors: true);
            else
                File.Move(temporary, UserOverridePath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        return GetState();
    }

    public IgrfConfigurationState RestoreEmbeddedBaseline()
    {
        if (File.Exists(UserOverridePath))
        {
            string archived = UserOverridePath + ".disabled-" +
                              DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" +
                              Guid.NewGuid().ToString("N");
            File.Move(UserOverridePath, archived);
        }
        return GetState();
    }

    private static byte[] ReadEmbeddedBytes()
    {
        Assembly assembly = typeof(IgrfConfigurationService).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new FileNotFoundException("程序内置的 IGRF 系数资源缺失。", EmbeddedResourceName);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
