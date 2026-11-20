using MagOps.Infrastructure.Services;

namespace MagOps.SmokeTests;

internal static partial class Program
{
    private static void TestCopernicusDem()
    {
        True(new Aw3d30DownloadPreferences().DownloadSource == CopernicusDem.Glo30, "anonymous default");
        var edge = CopernicusDem.Plan(new(67.65, 39, 67.65, 39)).Single();
        True(edge.Latitude == 38 && edge.Longitude == 67 && edge.Resolution == 30, "south edge ownership");
        var arctic = CopernicusDem.Plan(new(179.998, 86.5, 179.998, 86.5), 90).Single();
        True(arctic.Longitude == -180 && arctic.Id.Contains("COG_30_N86_00_W180"), "polar spacing and antimeridian");
        var box = CopernicusDem.Plan(new(-1.1, -1.1, -.9, -.9));
        True(box.Count == 4 && box.Distinct().Count() == 4, "southern and western bbox");
        var sampler = new GeoTiffDemSampler(Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic-cop-dem.tif"));
        sampler.ValidateForHeightCalculation();
        // Synthetic GDAL fixture z = (7*column - 11*row)/4, with four independently compressed blocks.
        foreach (var (x, y) in new[] { (1.1, 2.7), (15.7, 15.8), (30.1, 25.3) })
        {
            double z = sampler.SampleBilinear(10 + (x + .5) * .01, 20 - (y + .5) * .01);
            True(Math.Abs(z - (7*x - 11*y)/4) < 1e-8, "float predictor and bilinear interpolation across blocks");
        }
    }
}
