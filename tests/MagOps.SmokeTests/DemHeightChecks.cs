using System.Text;
using MagOps.Core.Domain;
using MagOps.Infrastructure.Services;

namespace MagOps.SmokeTests;

internal static partial class Program
{
    private static void RunDemHeightDerivationChecks()
    {
        WithTempRoot(root =>
        {
            string demPath = Path.Combine(root, "已知斜面.tif");
            WriteDemHeightPlane(demPath);
            var sampler = new GeoTiffDemSampler(demPath);
            Equal(115, sampler.SampleBilinear(100.01, 39.99), 1e-8);
            Equal(130, sampler.SampleBilinear(100.015, 39.985), 1e-8);
            var options = new DemHeightOptions
            {
                DemPath = demPath, CompatibilityConfirmed = true, ConfirmedDemSha256 = sampler.ContentSha256,
                ObservationHeightDatum = "合成米制高程", DemHeightDatum = "合成米制高程", CompatibilityEvidence = "已知解析斜面"
            };
            UavMagCanonicalSample Point(int record, double? lon, double? lat, double? altitude) =>
                new(DateTimeOffset.UnixEpoch.AddSeconds(record), lon, lat, altitude, 999, 50000, null, "", null, "", "原始观测.txt", record, []);
            var points = new[] { Point(1, 100.01, 39.99, 165), Point(2, 100.02, 39.98, 135),
                Point(3, 100.01, 39.99, null), Point(4, null, 39.99, 165), Point(5, 110, 39.99, 165) };
            FieldMagFolderAnalysis Analysis(IReadOnlyList<UavMagCanonicalSample> rows, string fingerprint = "known-source") =>
                new() { UavSamples = rows, Fingerprint = fingerprint };
            var analysis = Analysis(points);
            var result = DemHeightService.Calculate(analysis, options);
            Equal(50, result.Points[0].AboveGroundHeightM!.Value, 1e-8);
            Equal(-10, result.Points[1].AboveGroundHeightM!.Value, 1e-8);
            True(result.Points.Select(p => p.Status).SequenceEqual(new[] { DemHeightStatus.Calculated, DemHeightStatus.Calculated,
                DemHeightStatus.MissingAltitude, DemHeightStatus.InvalidCoordinate, DemHeightStatus.OutsideDem }), "DEM point failure classification must preserve valid and negative observations");
            True(result.Points.Select(p => p.InputIndex).SequenceEqual(Enumerable.Range(0, points.Length)) &&
                result.Points.Select(p => p.Source).SequenceEqual(points), "DEM derivation must preserve source objects and row identities");
            Equal(5, result.Summary.InputCount); Equal(2, result.Summary.ValidCount); Equal(1, result.Summary.NegativeCount);
            Equal(20, result.Summary.MeanM!.Value, 1e-8);
            Equal(-7, result.Summary.P05M!.Value, 1e-8); Equal(47, result.Summary.P95M!.Value, 1e-8);

            var unconfirmed = DemHeightService.Calculate(analysis, options with { CompatibilityConfirmed = false });
            True(unconfirmed.Points[0].TerrainElevationM is not null && unconfirmed.Points[0].AboveGroundHeightM is null &&
                unconfirmed.Points[0].Status == DemHeightStatus.DatumUnconfirmed && unconfirmed.Summary.MeanM is null,
                "unconfirmed height datum permits terrain only, without fabricated zero AGL");
            foreach (var missingEvidence in new[] { options with { ObservationHeightDatum = "" }, options with { DemHeightDatum = "" },
                options with { CompatibilityEvidence = "" }, options with { ConfirmedDemSha256 = "wrong" } })
                Equal(DemHeightStatus.DatumUnconfirmed, DemHeightService.Calculate(analysis, missingEvidence).Points[0].Status);

            var higher = DemHeightService.Calculate(Analysis([points[0] with { AltitudeM = 175 }], "higher-source"), options);
            Equal(60, higher.Points[0].AboveGroundHeightM!.Value, 1e-8);
            string shiftedPath = Path.Combine(root, "整体升高.tif"); WriteDemHeightPlane(shiftedPath, 10);
            var shiftedSampler = new GeoTiffDemSampler(shiftedPath);
            var shifted = DemHeightService.Calculate(Analysis([points[0] with { AltitudeM = 175 }], "shifted-source"),
                options with { DemPath = shiftedPath, ConfirmedDemSha256 = shiftedSampler.ContentSha256 });
            Equal(result.Points[0].AboveGroundHeightM!.Value, shifted.Points[0].AboveGroundHeightM!.Value, 1e-8);
            True(higher.Fingerprint != result.Fingerprint && shifted.Fingerprint != result.Fingerprint, "DEM height fingerprints must track source and terrain changes");

            string noDataPath = Path.Combine(root, "空值.tif"); WriteDemHeightPlane(noDataPath, noData: true);
            var noDataSampler = new GeoTiffDemSampler(noDataPath);
            Equal(DemHeightStatus.NoData, DemHeightService.Calculate(Analysis([points[0]]), options with
                { DemPath = noDataPath, ConfirmedDemSha256 = noDataSampler.ContentSha256 }).Points[0].Status);
            Equal(DemHeightStatus.DemUnavailable, DemHeightService.Calculate(Analysis([points[0]]), options with
                { DemPath = Path.Combine(root, "missing.tif") }).Points[0].Status);

            string sourcePath = Path.Combine(root, "原始观测.txt"); File.WriteAllText(sourcePath, "immutable source\n", Encoding.UTF8);
            byte[] original = File.ReadAllBytes(sourcePath), demOriginal = File.ReadAllBytes(demPath);
            var tracked = DemHeightService.Calculate(new FieldMagFolderAnalysis
            {
                Fingerprint = "tracked-source", UavSamples = [points[0] with { SourceFile = sourcePath }],
                Files = [new FieldMagFileScan(sourcePath, FieldMagSensorFamily.UavOpticalPump, FieldMagDataRole.Raw,
                    1, points[0].TimestampUtc, points[0].TimestampUtc, "synthetic", [])
                    { SourceContentSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original)), SourceFileStableId = "source-id" }]
            }, options);
            True(DemHeightService.SourcesAreCurrent(tracked) && tracked.SourceFiles.Single().StableId == "source-id",
                "DEM snapshot must retain the exact imported source hash and stable identity");
            File.AppendAllText(sourcePath, "changed");
            True(!DemHeightService.SourcesAreCurrent(tracked), "source change must invalidate DEM delivery");
            Throws<InvalidOperationException>(() => DemHeightService.Export(tracked, root));
            File.WriteAllBytes(sourcePath, original);
            string output = DemHeightService.Export(result, root);
            Equal("离地高度逐点明细.csv", Path.GetFileName(output));
            string header = File.ReadLines(output).First();
            True(header.All(c => c < 128) && header.Contains("AboveGroundHeightM") && header.StartsWith("InputOrder,"), "DEM CSV must retain English field names and Chinese delivery filename");
            Equal(points.Length + 1, File.ReadAllLines(output).Length);
            True(File.ReadAllBytes(sourcePath).SequenceEqual(original) && File.ReadAllBytes(demPath).SequenceEqual(demOriginal), "DEM export must not modify raw observations or terrain");
            string secondOutput = DemHeightService.Export(result, root);
            True(output != secondOutput && File.Exists(output), "repeated DEM export must retain previous delivery");
            True(DemHeightService.DemIsCurrent(result), "unchanged DEM must remain current");
            WriteDemHeightPlane(demPath, 1);
            True(!DemHeightService.DemIsCurrent(result), "modified DEM must invalidate snapshot");
            Throws<InvalidOperationException>(() => DemHeightService.Export(result, root));
            var staleConfirmation = DemHeightService.Calculate(analysis, options);
            Equal(DemHeightStatus.DatumUnconfirmed, staleConfirmation.Points[0].Status);
            True(staleConfirmation.Fingerprint != result.Fingerprint && staleConfirmation.DemSha256 != result.DemSha256,
                "changed DEM must refresh fingerprint and invalidate prior compatibility confirmation");
        });
    }

    // Independent 4x4 Float32 north-up PixelIsArea fixture: z(c,r)=100+10c+20r.
    private static void WriteDemHeightPlane(string path, float shift = 0, bool noData = false)
    {
        const int count = 14;
        using var memory = new MemoryStream(); using var writer = new BinaryWriter(memory);
        writer.Write((byte)'I'); writer.Write((byte)'I'); writer.Write((ushort)42); writer.Write(8u);
        writer.Write((ushort)count); writer.Write(new byte[count * 12 + 4]);
        uint Data(Action write) { uint offset = (uint)memory.Position; write(); return offset; }
        uint scale = Data(() => { writer.Write(.01); writer.Write(.01); writer.Write(0d); });
        uint tie = Data(() => { foreach (double v in new[] { 0d, 0, 0, 100, 40, 0 }) writer.Write(v); });
        uint keys = Data(() => { foreach (ushort v in new ushort[] { 1, 1, 0, 2, 1025, 0, 1, 1, 2048, 0, 1, 4326 }) writer.Write(v); });
        byte[] noDataText = Encoding.ASCII.GetBytes("-9999\0"); uint empty = Data(() => writer.Write(noDataText));
        uint pixels = Data(() => { for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) writer.Write(noData && c == 1 && r == 1 ? -9999f : 100 + 10 * c + 20 * r + shift); });
        memory.Position = 10;
        void Tag(ushort id, ushort type, uint size, uint value) { writer.Write(id); writer.Write(type); writer.Write(size); writer.Write(value); }
        Tag(256,4,1,4); Tag(257,4,1,4); Tag(258,3,1,32); Tag(259,3,1,1); Tag(262,3,1,1);
        Tag(273,4,1,pixels); Tag(277,3,1,1); Tag(278,4,1,4); Tag(279,4,1,64); Tag(339,3,1,3);
        Tag(33550,12,3,scale); Tag(33922,12,6,tie); Tag(34735,3,12,keys); Tag(42113,2,(uint)noDataText.Length,empty);
        File.WriteAllBytes(path, memory.ToArray());
    }
}
