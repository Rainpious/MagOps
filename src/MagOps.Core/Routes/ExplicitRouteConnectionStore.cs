using System.Security.Cryptography;
using System.Text.Json;
namespace MagOps.Core.Routes;

public sealed class ExplicitRouteConnection
{
    public string Line { get; set; } = "";
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public bool Matches(RouteEndpoint a, RouteEndpoint b) =>
        string.Equals(Line, a.Line, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Line, b.Line, StringComparison.OrdinalIgnoreCase) &&
        ((Equal(a, X1, Y1) && Equal(b, X2, Y2)) || (Equal(a, X2, Y2) && Equal(b, X1, Y1)));
    private static bool Equal(RouteEndpoint a, double x, double y) =>
        double.IsFinite(x) && double.IsFinite(y) && Math.Abs(a.X - x) <= 1e-8 && Math.Abs(a.Y - y) <= 1e-8;
}

public sealed class ExplicitRouteConnectionDocument
{
    public string Schema { get; set; } = "explicit-connect-temp-1";
    public string SourceSha256 { get; set; } = "";
    public List<ExplicitRouteConnection> Connections { get; set; } = [];
}

public static class ExplicitRouteConnectionStore
{
    public static string Sidecar(string path) => Path.GetFullPath(path) + ".magops-connect.json";
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    public static void Write(string path, List<ExplicitRouteConnection> connections)
    {
        string sidecar = Sidecar(path);
        // Empty records also replace stale CONNECT decisions on a subsequent SPLIT.
        File.WriteAllText(sidecar, JsonSerializer.Serialize(new ExplicitRouteConnectionDocument {
            SourceSha256 = Hash(path), Connections = connections
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static IReadOnlyList<ExplicitRouteConnection> Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return [];
        string sidecar = Sidecar(path);
        if (!File.Exists(sidecar)) return [];
        var doc = JsonSerializer.Deserialize<ExplicitRouteConnectionDocument>(File.ReadAllText(sidecar));
        if (doc is null || doc.Schema != "explicit-connect-temp-1" ||
            !string.Equals(doc.SourceSha256, Hash(path), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("人工连接确认记录与端点文件不符，请重新执行连接/分割并确认；未沿用旧连接许可。");
        return doc.Connections ?? [];
    }
}
