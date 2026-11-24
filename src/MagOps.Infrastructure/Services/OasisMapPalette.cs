using System.Drawing;
using System.Globalization;
using System.Text.Json;

namespace MagOps.Infrastructure.Services;

public static class OasisMapPalette
{
    public static string Name { get; }
    public static string Version { get; }
    public static Color Missing { get; }
    public static IReadOnlyList<Color> Colours { get; }

    static OasisMapPalette()
    {
        using var stream = typeof(OasisMapPalette).Assembly.GetManifestResourceStream(
            "MagOps.Infrastructure.Resources.aeromagnetic-rdbu-v1.0.json")!;
        using var json = JsonDocument.Parse(stream);
        var profile = json.RootElement;
        Name = profile.GetProperty("name").GetString()!;
        Version = profile.GetProperty("version").GetString()!;
        Missing = Parse(profile.GetProperty("missing"));
        var anchors = profile.GetProperty("colors_low_to_high").EnumerateArray().Select(Parse).ToArray();
        var positions = profile.GetProperty("positions").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var lut = new Color[256];
        for (int i = 0; i < lut.Length; i++)
        {
            // Put each control colour at its nearest LUT index, including the zero stop at 128.
            int right = Array.FindIndex(positions, p => Math.Round(255 * p, MidpointRounding.AwayFromZero) >= i);
            int left = Math.Max(0, right - 1);
            double start = Math.Round(255 * positions[left], MidpointRounding.AwayFromZero);
            double end = Math.Round(255 * positions[right], MidpointRounding.AwayFromZero);
            double t = left == right ? 0 : (i - start) / (end - start);
            var a = anchors[left]; var b = anchors[right];
            int Mix(int low, int high) => (int)Math.Round(low + t * (high - low), MidpointRounding.AwayFromZero);
            lut[i] = Color.FromArgb(Mix(a.R,b.R), Mix(a.G,b.G), Mix(a.B,b.B));
        }
        Colours = Array.AsReadOnly(lut);
    }

    private static Color Parse(JsonElement hex) => Color.FromArgb(unchecked((int)0xff000000) |
        int.Parse(hex.GetString()!.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
}
