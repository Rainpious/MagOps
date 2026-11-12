using System.Text.Encodings.Web;
using System.Text.Json;

namespace MagOps.Infrastructure.Storage;

public sealed class JsonStore
{
    public JsonSerializerOptions Options { get; } = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public T Read<T>(PathInfo path, T fallback)
    {
        if (!File.Exists(path.FullPath)) return fallback;
        string json = File.ReadAllText(path.FullPath);
        if (string.IsNullOrWhiteSpace(json)) return fallback;
        return JsonSerializer.Deserialize<T>(json, Options) ?? fallback;
    }

    public T Read<T>(string path, T fallback) => Read(new PathInfo(path), fallback);

    public void Write<T>(PathInfo path, T value)
    {
        IDisposable? writeLock = WorkspaceFileLock.IsManagementPath(path.FullPath, out string management)
            ? WorkspaceFileLock.AcquireDirectory(management)
            : null;
        using (writeLock)
        {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path.FullPath)!);
        string temp = path.FullPath + ".tmp";
        string json = JsonSerializer.Serialize(value, Options);
        File.WriteAllText(temp, json, new System.Text.UTF8Encoding(false));
        File.Move(temp, path.FullPath, true);
        }
    }

    public void Write<T>(string path, T value) => Write(new PathInfo(path), value);

    public void AppendJsonLine(string directory, string eventName, object? details = null)
    {
        using var writeLock = WorkspaceFileLock.AcquireDirectory(directory);
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "events.jsonl");
        var payload = new
        {
            timestamp = DateTimeOffset.UtcNow.ToString("O"),
            @event = eventName,
            details = details ?? new { }
        };
        string line = JsonSerializer.Serialize(payload, Options);
        File.AppendAllText(path, line + Environment.NewLine, new System.Text.UTF8Encoding(false));
    }
}

public readonly record struct PathInfo(string FullPath);
