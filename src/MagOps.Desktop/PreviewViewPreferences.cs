using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MagOps.Desktop;

internal sealed record PreviewViewState(double Zoom, double OffsetX, double OffsetY, DateTimeOffset UpdatedAt);

/// <summary>Optional per-user display state, separate from business selections.</summary>
internal static class PreviewViewPreferences
{
    private static readonly object Gate = new();
    private static string DefaultPath => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "MagOps", "preview-views.json");
    private sealed class Document
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, JsonElement> Views { get; set; } = [];
    }

    internal static string Key(string workspace, string preview, object identity) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { workspace, preview, identity }))));

    internal static PreviewViewState? Load(string key, string? path = null)
    {
        lock (Gate)
        {
            var document = Read(path ?? DefaultPath);
            if (document is null || !document.Views.TryGetValue(key, out var item)) return null;
            try
            {
                var value = item.Deserialize<PreviewViewState>();
                return Valid(value) ? value : null;
            }
            catch (JsonException ex) { Trace.TraceWarning("Invalid preview view entry: {0}", ex.Message); return null; }
        }
    }

    internal static void Save(string key, PreviewViewState state, string? path = null)
    {
        if (!Valid(state)) return;
        lock (Gate)
        {
            path ??= DefaultPath;
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var document = Read(path);
                if (document is null) return; // Preserve unknown future schemas.
                document.Views[key] = JsonSerializer.SerializeToElement(state);
                if (document.Views.Count > 256)
                    foreach (string old in document.Views.Keys.Where(k => k != key).Take(document.Views.Count - 256).ToArray())
                        document.Views.Remove(old);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                File.WriteAllText(temporary, JsonSerializer.Serialize(document));
                File.Move(temporary, path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            { Trace.TraceWarning("Preview preferences could not be saved: {0}", ex.Message); }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Trace.TraceWarning(ex.Message); }
            }
        }
    }

    private static bool Valid(PreviewViewState? value) => value is not null &&
        double.IsFinite(value.Zoom) && value.Zoom is >= .25 and <= 64 &&
        double.IsFinite(value.OffsetX) && Math.Abs(value.OffsetX) <= 128 &&
        double.IsFinite(value.OffsetY) && Math.Abs(value.OffsetY) <= 128;

    private static Document? Read(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            var value = JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
            if (value?.Version != 1 || value.Views is null) return null;
            return value;
        }
        catch (JsonException ex)
        {
            Trace.TraceWarning("Invalid preview preferences: {0}", ex.Message);
            try { if (!File.Exists(path + ".invalid.bak")) File.Copy(path, path + ".invalid.bak"); }
            catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException) { Trace.TraceWarning(copyError.Message); }
            return new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Trace.TraceWarning(ex.Message); return null; }
    }
}
