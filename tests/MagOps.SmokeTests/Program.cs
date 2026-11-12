using MagOps.Application;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using MagOps.Core.Coordinates;
using MagOps.Core.Domain;
using MagOps.Core.Diurnal;
using MagOps.Core.Geomagnetics;
using MagOps.Core.Services;
using MagOps.Core.Routes;
using MagOps.Core.SurveyPlanning;
using MagOps.Core.Analysis;
using MagOps.Infrastructure.Services;
using MagOps.Infrastructure.Storage;

namespace MagOps.SmokeTests;

internal static partial class Program
{
    private static int _passed;
    private static void TestTrackOnlySegmentation() => RunTrackOnlySegmentationChecks();
    private static void TestDemHeightDerivation() => RunDemHeightDerivationChecks();
    private static void TestIndependentProjectMeridianSuggestion() => RunIndependentProjectMeridianSuggestion();
    private static void TestProjectConfigurationMigration() => RunProjectConfigurationMigration();

    private static int Main()
    {
        Environment.SetEnvironmentVariable(
            "MAGOPS_FOLDER_LAYOUT",
            Path.Combine(Path.GetTempPath(), "magops-smoke-embedded-layout-" + Guid.NewGuid().ToString("N") + ".json"));
        var tests = new (string Name, Action Test)[]
        {
            ("Track-only segmentation, reference classification and production replay", TestTrackOnlySegmentation),
            ("DMS hyphen format", TestDmsHyphen),
            ("DMS hemisphere and six-column input", TestDmsHemisphereAndSixColumns),
            ("DMS point-id longitude latitude table paste", TestDmsPointIdTablePaste),
            ("DMS headerless vertical point-id table paste", TestDmsHeaderlessVerticalPointIdTablePaste),
            ("DMS auto-detect overrides stale decimal mode", TestDmsAutoDetectOverridesStaleDecimalMode),
            ("Gauss-Kruger roundtrip", TestGaussKrugerRoundtrip),
            ("Coordinate axis semantics", TestCoordinateAxisOrder),
            ("IGRF Oasis coefficient-file parse", TestIgrfOasisCoefficientFile),
            ("IGRF embedded baseline and reversible configuration update", TestIgrfConfigurationService),
            ("IGRF spherical-harmonic synthesis", TestIgrfSphericalHarmonicSynthesis),
            ("Batch central-meridian warning", TestBatchCentralMeridian),
            ("Independent geographic suggestion with confirmed project meridian", TestIndependentProjectMeridianSuggestion),
            ("Project configuration naming, legacy migration and read-only discovery", TestProjectConfigurationMigration),
            ("Project state machine", TestStateMachine),
            ("Workspace/tasks/gates persistence", TestWorkspace),
            ("Remembered current workarea context", TestRememberedWorkareaContext),
            ("Workarea identity authority and alias ambiguity", TestWorkareaIdentityAuthority),
            ("Moved project and existing workarea folder relink", TestMovedProjectAndWorkareaFolderRelink),
            ("Field operations settings persistence", TestFieldOperationsSettingsPersistence),
            ("Concave endpoint connect and split in one file", TestConcaveEndpointConnectSplit),
            ("Concave line-rule profile persistence", TestConcaveLineRuleProfilePersistence),
            ("Artifact scan/hash/change tracking", TestArtifacts),
            ("Artifact workarea model and stable move identity", TestArtifactWorkareaModel),
            ("Folder layout lazy creation", TestFolderLayoutLazyCreation),
            ("Folder authority explicit migration", TestFolderAuthorityMigration),
            ("External route KML import and field discovery", TestExternalRouteKmlImport),
            ("Configured field outputs stay out of raw-data discovery", TestConfiguredFieldOutputsExcludedFromDiscovery),
            ("Raw material archive import", TestRawMaterialImport),
            ("Fieldwork folder mappings", TestFieldworkFolderMappings),
            ("Fieldwork business document export", TestFieldworkDocumentExport),
            ("Atomic overwrite for fieldwork business exports", TestAtomicFieldworkExports),
            ("Field magnetic folder classification and canonical merge", TestFieldMagCanonicalMerge),
            ("Field magnetic incremental scan cache", TestFieldMagIncrementalScanCache),
            ("Field data import catalog", TestFieldDataImportCatalog),
            ("Field data recursive folder discovery", TestFieldDataRecursiveFolderDiscovery),
            ("Survey line unique track matching", TestSurveyLineUniqueTrackMatching),
            ("Survey line identity recovery from route points", TestSurveyLineIdentityRecoveryFromRoutePoints),
            ("Survey line identity fallback from field-progress route geometry", TestSurveyLineIdentityRecoveryFromFieldProgressGeometry),
            ("DJI Pilot2 v14 summary parser", TestDjiPilot2V14SummaryParser),
            ("DJI Pilot2 import dedupe and magnetic session match", TestDjiPilot2ImportAndMatch),
            ("Field magnetic UTC grouping with integrated project outputs", TestFieldMagProjectDatePartitioning),
            ("Field Beijing progress dates and leadership report", TestFieldBeijingProgressDatesAndLeadershipReport),
            ("GSM-19T native Roman date headers", TestGsm19TNativeRomanDateHeaders),
            ("GSM rover roles and conflicting acquisition dates", TestGsmRoverRolesAndDateConflicts),
            ("GSM ground raw input and explicit session time mapping", GsmGroundInputChecks.InputAndTime),
            ("Ground diurnal independent trial and source audit", GsmGroundInputChecks.Diurnal),
            ("GSM-19T header-aware nT column mapping", TestGsm19THeaderAwareColumnMapping),
            ("GSM-19T first-class session identity", TestGsm19TFirstClassSessionIdentity),
            ("GEMLink compatibility bridge and QC exports", TestFieldMagUtcBridgeAndExports),
            ("GEMLink time compatibility primitives", TestGemLinkTimeCompatibility),
            ("Native diurnal exact interpolation and Datum", TestNativeDiurnalMath),
            ("Native diurnal UTC-date isolation and no-overlap rejection", TestNativeDiurnalUtcIsolation),
            ("Native diurnal duplicate Base time policy", TestNativeDiurnalDuplicateBasePolicy),
            ("Native diurnal production Base validity and long-gap rejection", TestNativeDiurnalProductionBaseValidity),
            ("Daily UAV/19T production coverage preview", TestDailyUavBaseCoveragePreview),
            ("GSM-19T 240000 canonical rollover", TestGsm19T240000Rollover),
            ("Field magnetic integrated and native diurnal exports", TestFieldMagIntegratedAndNativeDiurnalExports),
            ("Field magnetic compact SourceId GZip storage", TestFieldMagCompactSourceIdGzipStorage),
            ("Native diurnal lightweight processing CSV", TestNativeDiurnalLightweightProcessingCsv),
            ("Empty processing export preserves audit without publishing Oasis CSV", TestEmptyDiurnalProcessingOutput),
            ("Native diurnal rigai IGRF dTgz processing semantics", TestNativeDiurnalRigaiIgrfDTgzSemantics),
            ("Magnetic CSV UTC date and time columns", TestMagneticCsvUtcDateTimeColumns),
            ("Equipment checklist", TestEquipmentChecklist),
            ("GPAM-M350-RM equipment catalog and checklist", TestGpamEquipmentCatalogAndChecklist),
            ("Coordinate CSV/CRS registry", TestCoordinateRegistry),
            ("Oasis PLY isolation and project central-meridian authority", TestOasisPlyAndProjectCentralMeridian),
            ("Coordinate KML range templates and export", TestCoordinateKmlRangeExport),
            ("KML/GPX exchange and geometry audit", TestSpatialExchange),
            ("synthetic boundary acceptance fixture", TestSyntheticBoundaryAcceptance),
            ("Region MultiPolygon and hole geometry", TestRegionGeometry),
            ("Operational production run queries", TestOperationalProductionQueries),
            ("Route manifest adapter", TestRouteManifest),
            ("Route v25 L-number spatial ordering", TestRouteV25SpatialOrdering),
            ("Partition traversal start stays on outer survey lines", TestPartitionTraversalStartSafety),
            ("Complex pin partition uses local operation tree", TestComplexPinPartitionOperationTree),
            ("Route v25 17.99/18.00/18.01 km boundary", TestRouteLengthBoundaries),
            ("Route v25 snake split", TestRouteV25Split),
            ("Route v25 KML/CSV export (KMZ frozen)", TestRouteV25Export),
            ("Pre-segmentation DEM height derivation, datum and delivery", TestDemHeightDerivation),
            ("Route 020537 path does not force central meridian", TestRouteV25Calibrated020537),
            ("Route v25 BAT project JSON compatibility", TestRouteV25ProjectCompatibility),
            ("Overlong mother line execution segments stay protected", TestOverlongExecutionSegments),
            ("Route application workflow", TestRouteApplicationWorkflow),
            ("Stable route identity survives save and reversal", TestStableRouteIdentity),
            ("Whole-leg shadow separates two Missions in one source", TestWholeLegMissionWindows),
            ("Endpoint recognition boundaries, anomalies and repeated execution", EndpointRecognitionChecks.Run),
            ("Same-second KML turns, stationary events and transit purpose boundaries", StationaryPurposeChecks.Run),
            ("Production point anomaly exclusion and review boundary", ProductionAnomalyChecks.Run),
            ("Review recognition reuse and complete-result equivalence", ReviewReuseChecks.Run),
            ("Review decisions without UI: batch, persistence and explicit empty selection", ReviewDecisionChecks.Run),
            ("Review session: recomputation plan, stale results, immutable requests and retry", ReviewSessionChecks.Run),
            ("Local/reverse-hit candidates preserve identity, scope and continuous evidence", LocalHitCandidateChecks.Run),
            ("Whole-track geometry recovery: wide recall, competition and source conservation", WholeTrackGeometryRecoveryChecks.Run),
            ("Flight quality evidence, provenance, missing data and independent reports", FlightQualityAssessmentChecks.Run),
            ("Mission task authority migrates legacy names only for review", TestMissionTaskAuthority),
            ("Automatic flight-window inference", TestAutomaticFlightWindowInference),
            ("Current workarea squared-distance screening and reversible exclusions", SpatialAndTimeChecks.Screening),
            ("Automatic Mission task suggestions and overlapping-source isolation", SpatialAndTimeChecks.Suggestions),
            ("Project time zone persistence, business-day grouping and DST boundaries", SpatialAndTimeChecks.TimeZones),
            ("Pass union and interval production selection", TestPassUnionAndSelection),
            ("Atomic output bundle rollback and snapshot", TestAtomicOutputBundle),
            ("Generated artifact identity integrity and atomic export", TestGeneratedArtifactIdentity),
            ("Project spatial-reference changes invalidate derived artifact freshness", ArtifactFreshnessChecks.Run),
            ("Field production application workflow", TestFieldProductionWorkflow),
            ("Concurrent workspace read-modify-write", TestWorkspaceTransaction),
            ("Partition route identity merge keeps mother line names", TestPartitionRouteIdentityMerge),
            ("Partition production lineage preserves child, mother, repeat and source identity", PartitionProductionLineageChecks.Run),
            ("Mother production candidate assembly preserves direction, overlap, passes and provenance", MotherLineProductionAssemblyChecks.Run),
            ("Existing processing CSV import preserves values and explicit mother bindings", MotherLineProcessingCsvImportChecks.Run),
            ("CLI real segmentation candidates, desktop decisions, diff and stale-context rejection", SegmentationCandidateWorkflowChecks.Run),
            ("Partition mother identities bind design, survive save and isolate same names", PartitionMotherIdentityChecks.Run),
            ("Sand dynamic-noise fourth difference", TestDynamicNoise),
            ("Sand text-table group average", TestGroupAverage),
            ("Sand elevation CSV to absolute KML", TestElevationKml)
        };

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("MagOps C# smoke tests");
        Console.WriteLine("=====================");

        foreach (var (name, test) in tests)
        {
            try
            {
                test();
                _passed++;
                Console.WriteLine($"[PASS] {name}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[FAIL] {name}");
                Console.Error.WriteLine(ex);
                Console.Error.WriteLine();
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{_passed}/{tests.Length} passed.");
        return _passed == tests.Length ? 0 : 1;
    }


    private static void TestWorkareaIdentityAuthority()
    {
        var alpha = new WorkareaRecord
        {
            WorkareaId = "wa-alpha-stable",
            SubareaId = "legacy-alpha",
            DisplayName = "Alpha Display",
            Path = "10_工区/renamed-alpha"
        };
        var beta = new WorkareaRecord
        {
            WorkareaId = "wa-beta-stable",
            SubareaId = "legacy-beta",
            DisplayName = "Beta Display",
            Path = "10_工区/beta"
        };
        var rows = new List<WorkareaRecord> { alpha, beta };

        WorkareaIdentityResolution canonical = WorkareaIdentityResolver.Resolve(rows, "WA-ALPHA-STABLE");
        Equal(WorkareaIdentityMatchKind.CanonicalId, canonical.MatchKind);
        Equal("wa-alpha-stable", canonical.Workarea?.WorkareaId ?? "");
        True(!canonical.UsedCompatibilityAlias, "canonical WorkareaId must not be reported as an alias");

        WorkareaIdentityResolution legacy = WorkareaIdentityResolver.Resolve(rows, "legacy-alpha");
        Equal(WorkareaIdentityMatchKind.LegacySubareaId, legacy.MatchKind);
        Equal("wa-alpha-stable", legacy.Workarea?.WorkareaId ?? "");
        True(legacy.UsedCompatibilityAlias, "legacy SubareaId must be compatibility-only");

        WorkareaIdentityResolution display = WorkareaIdentityResolver.Resolve(rows, "Alpha Display");
        Equal(WorkareaIdentityMatchKind.DisplayName, display.MatchKind);
        Equal("wa-alpha-stable", display.Workarea?.WorkareaId ?? "");

        alpha.DisplayName = "Renamed Display";
        alpha.Path = "10_工区/moved-again";
        WorkareaIdentityResolution afterMove = WorkareaIdentityResolver.Resolve(rows, "wa-alpha-stable");
        Equal(WorkareaIdentityMatchKind.CanonicalId, afterMove.MatchKind);
        Equal("wa-alpha-stable", afterMove.Workarea?.WorkareaId ?? "");

        beta.DisplayName = "Renamed Display";
        WorkareaIdentityResolution ambiguous = WorkareaIdentityResolver.Resolve(rows, "Renamed Display");
        Equal(WorkareaIdentityMatchKind.Ambiguous, ambiguous.MatchKind);
        True(!ambiguous.Found, "ambiguous display aliases must never select the first workarea implicitly");
        Equal(2, ambiguous.CandidateWorkareaIds.Count);

        WorkareaIdentityResolution missing = WorkareaIdentityResolver.Resolve(rows, "missing-area");
        Equal(WorkareaIdentityMatchKind.NotFound, missing.MatchKind);
        True(!missing.Found, "unknown selectors must not synthesize a workarea identity");
    }

    private static void TestDmsHyphen()
    {
        double value = CoordinateService.ParseDms("116-15-00", "lon");
        Equal(116.25, value, 1e-12);
        string text = CoordinateService.FormatDms(value, "lon");
        True(text.StartsWith("116°15′00."), $"unexpected DMS: {text}");
    }

    private static void TestDmsHemisphereAndSixColumns()
    {
        double lon = CoordinateService.ParseDms("E116°15′00″", "lon");
        double lat = CoordinateService.ParseDms("N39°54′00″", "lat");
        Equal(116.25, lon, 1e-12);
        Equal(39.9, lat, 1e-12);

        var pair = CoordinateService.SplitDmsPairFields("116\t15\t00\t39\t54\t00");
        True(pair.HasValue, "six-column DMS was not recognized");
        Equal(116.25, CoordinateService.ParseDms(pair!.Value.Longitude, "lon"), 1e-12);
        Equal(39.9, CoordinateService.ParseDms(pair.Value.Latitude, "lat"), 1e-12);

        Throws<FormatException>(() => CoordinateService.ParseDms("N116°15′00″", "lon"));
    }

    private static void TestDmsPointIdTablePaste()
    {
        string tabular = string.Join("\n", new[]
        {
            "点号\t经度\t纬度",
            "1\t111° 14' 1.93\"\t47° 45' 41.76\"",
            "2\t111° 18' 0.75\"\t47° 45' 41.76\"",
            "3\t111° 18' 0.75\"\t47° 44' 51.81\"",
            "4\t111° 18' 0.35\"\t47° 44' 51.81\"",
            "5\t111° 17' 56.42\"\t47° 44' 55.84\"",
            "6\t111° 17' 56.12\"\t47° 44' 55.71\""
        });

        var batch = CoordinateService.ConvertBatch(
            tabular,
            CoordinateInputMode.Dms,
            new ProjectionParameters(),
            autoCentralMeridian: true);

        Equal(6, batch.Results.Count);
        Equal("1", batch.Results[0].PointId ?? "");
        Equal("6", batch.Results[^1].PointId ?? "");
        Equal(111d + 14d / 60d + 1.93d / 3600d, batch.Results[0].Longitude, 1e-12);
        Equal(47d + 45d / 60d + 41.76d / 3600d, batch.Results[0].Latitude, 1e-12);
        Equal(111d + 17d / 60d + 56.12d / 3600d, batch.Results[^1].Longitude, 1e-12);
        Equal(47d + 44d / 60d + 55.71d / 3600d, batch.Results[^1].Latitude, 1e-12);

        string vertical = string.Join("\n", new[]
        {
            "点号", "经度", "纬度",
            "1", "111° 14' 1.93\"", "47° 45' 41.76\"",
            "2", "111° 18' 0.75\"", "47° 45' 41.76\""
        });

        var verticalPairs = CoordinateBatchParser.ParseRows(
            vertical,
            CoordinateInputMode.Dms);

        Equal(2, verticalPairs.Count);
        Equal("1", verticalPairs[0].PointId ?? "");
        Equal("2", verticalPairs[1].PointId ?? "");
        Equal(
            111d + 18d / 60d + 0.75d / 3600d,
            CoordinateService.ParseDms(verticalPairs[1].First, "lon"),
            1e-12);
    }

    private static void TestDmsHeaderlessVerticalPointIdTablePaste()
    {
        string raw = string.Join("\n", new[]
        {
            "1",
            "111° 14' 1.93\"",
            "47° 45' 41.76\"",
            "2",
            "111° 18' 0.75\"",
            "47° 45' 41.76\"",
            "3",
            "111° 18' 0.75\"",
            "47° 44' 51.81\"",
            "4",
            "111° 18' 0.35\"",
            "47° 44' 51.81\"",
            "5",
            "111° 17' 56.42\"",
            "47° 44' 55.84\"",
            "6",
            "111° 17' 56.12\"",
            "47° 44' 55.71\""
        });

        var pairs = CoordinateBatchParser.ParseRows(
            raw,
            CoordinateInputMode.Dms);

        Equal(6, pairs.Count);
        Equal("1", pairs[0].PointId ?? "");
        Equal("6", pairs[^1].PointId ?? "");
        Equal(
            111d + 14d / 60d + 1.93d / 3600d,
            CoordinateService.ParseDms(pairs[0].First, "lon"),
            1e-12);
        Equal(
            47d + 44d / 60d + 55.71d / 3600d,
            CoordinateService.ParseDms(pairs[^1].Second, "lat"),
            1e-12);

        var batch = CoordinateService.ConvertBatch(
            raw,
            CoordinateInputMode.Dms,
            new ProjectionParameters(),
            autoCentralMeridian: true);

        Equal(6, batch.Results.Count);
        Equal("1", batch.Results[0].PointId ?? "");
        Equal("6", batch.Results[^1].PointId ?? "");
    }


    private static void TestDmsAutoDetectOverridesStaleDecimalMode()
    {
        string raw = string.Join("\n", new[]
        {
            "1",
            "111° 14' 1.93\"",
            "47° 45' 41.76\"",
            "2",
            "111° 18' 0.75\"",
            "47° 45' 41.76\"",
            "3",
            "111° 18' 0.75\"",
            "47° 44' 51.81\"",
            "4",
            "111° 18' 0.35\"",
            "47° 44' 51.81\"",
            "5",
            "111° 17' 56.42\"",
            "47° 44' 55.84\"",
            "6",
            "111° 17' 56.12\"",
            "47° 44' 55.71\""
        });

        Equal(
            CoordinateInputMode.Dms,
            CoordinateBatchParser.ResolveInputMode(
                raw,
                CoordinateInputMode.DecimalDegrees));

        var batch = CoordinateService.ConvertBatch(
            raw,
            CoordinateInputMode.DecimalDegrees,
            new ProjectionParameters(),
            autoCentralMeridian: true);

        Equal(6, batch.Results.Count);
        Equal("1", batch.Results[0].PointId ?? "");
        Equal("6", batch.Results[^1].PointId ?? "");
        Equal(111d + 14d / 60d + 1.93d / 3600d, batch.Results[0].Longitude, 1e-12);
        Equal(47d + 44d / 60d + 55.71d / 3600d, batch.Results[^1].Latitude, 1e-12);
    }


    private static void TestGaussKrugerRoundtrip()
    {
        const double lon = 116.25;
        const double lat = 39.9;
        const double cm = 117;
        var xy = CoordinateService.GaussKrugerForward(lon, lat, cm);
        var ll = CoordinateService.GaussKrugerInverse(xy.Easting, xy.Northing, cm);
        Equal(lon, ll.Longitude, 1e-7);
        Equal(lat, ll.Latitude, 1e-7);
    }

    private static void TestCoordinateAxisOrder()
    {
        var gis = CoordinateService.Convert(
            "116.25", "39.9", CoordinateInputMode.DecimalDegrees,
            new ProjectionParameters(CentralMeridian: 117, AxisOrder: XyAxisOrder.EastingNorthing));
        Equal(gis.Easting, gis.X, 1e-8);
        Equal(gis.Northing, gis.Y, 1e-8);

        var survey = CoordinateService.Convert(
            "116.25", "39.9", CoordinateInputMode.DecimalDegrees,
            new ProjectionParameters(CentralMeridian: 117, AxisOrder: XyAxisOrder.NorthingEasting));
        Equal(survey.Northing, survey.X, 1e-8);
        Equal(survey.Easting, survey.Y, 1e-8);

        var back = CoordinateService.Convert(
            survey.X.ToString("R", CultureInfo.InvariantCulture),
            survey.Y.ToString("R", CultureInfo.InvariantCulture),
            CoordinateInputMode.ProjectedXY,
            new ProjectionParameters(CentralMeridian: 117, AxisOrder: XyAxisOrder.NorthingEasting));
        Equal(116.25, back.Longitude, 1e-7);
        Equal(39.9, back.Latitude, 1e-7);

        var areaPoints = new[]
        {
            new CoordinateConversionResult(0, 0, "", "", "", "", 0, 0, 0, 0, "", 117, "CGCS2000"),
            new CoordinateConversionResult(0, 0, "", "", "", "", 0, 0, 100, 0, "", 117, "CGCS2000"),
            new CoordinateConversionResult(0, 0, "", "", "", "", 0, 0, 100, 100, "", 117, "CGCS2000"),
            new CoordinateConversionResult(0, 0, "", "", "", "", 0, 0, 0, 100, "", 117, "CGCS2000")
        };
        Equal(10_000d, CoordinateGeometryMath.PolygonAreaSquareMeters(areaPoints), 1e-8);

        var precision = CoordinateService.Convert(
            "116.25",
            "39.9",
            CoordinateInputMode.DecimalDegrees,
            new ProjectionParameters(
                CentralMeridian: 117,
                AxisOrder: XyAxisOrder.EastingNorthing),
            decimalPlaces: 8);
        Equal("116.25000000", precision.LongitudeDecimal);
        Equal("39.90000000", precision.LatitudeDecimal);
        Equal(39, CoordinateService.GetGk3ZoneNumber(117));

        double zonedEasting = CoordinateService.AddGk3ZoneNumberToEasting(
            precision.Easting,
            117);
        Equal(39_000_000d + precision.Easting, zonedEasting, 1e-8);

        var zonedGis = CoordinateRegistryService.CoordinateOutputValues(
            precision,
            XyAxisOrder.EastingNorthing,
            117,
            includeZoneNumber: true);
        Equal(zonedEasting, zonedGis.X, 1e-8);
        Equal(precision.Northing, zonedGis.Y, 1e-8);

        var zonedSurvey = CoordinateRegistryService.CoordinateOutputValues(
            precision,
            XyAxisOrder.NorthingEasting,
            117,
            includeZoneNumber: true);
        Equal(precision.Northing, zonedSurvey.X, 1e-8);
        Equal(zonedEasting, zonedSurvey.Y, 1e-8);
    }

    private static void TestIgrfOasisCoefficientFile()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "config",
            "geomagnetics",
            "igrf.dat");
        True(File.Exists(path), $"IGRF model file missing from test output: {path}");

        IgrfModelSet models = IgrfDatParser.Load(path);
        Equal(17, models.Epochs.Count);
        Equal(1945, models.EarliestEpochYear);
        Equal(2025, models.LatestEpochYear);
        Equal(
            "8d63fc6433515be61211a122eb3c1fae5f46aa2a6a34f6addcf5cf927dc928d4",
            models.SourceSha256 ?? "");
        Equal(8, models.GetEpoch(1945).MaxDegree);
        Equal(10, models.GetEpoch(1990).MaxDegree);
        Equal(13, models.GetEpoch(2025).MaxDegree);
        Equal(104, models.GetEpoch(2025).Coefficients.Count);

        IgrfCoefficient c10 = models.GetEpoch(2025).GetCoefficient(1, 0);
        IgrfCoefficient c11 = models.GetEpoch(2025).GetCoefficient(1, 1);
        Equal(-29350.0, c10.G, 1e-12);
        Equal(12.6, c10.GDot, 1e-12);
        Equal(-1410.3, c11.G, 1e-12);
        Equal(4545.5, c11.H, 1e-12);
    }

    private static void TestIgrfConfigurationService()
    {
        WithTempRoot(root =>
        {
            var configuration = new IgrfConfigurationService(Path.Combine(root, "user-config"));
            IgrfConfigurationState baseline = configuration.GetState();
            True(!baseline.UsesUserOverride && baseline.SourcePath.StartsWith("embedded://", StringComparison.Ordinal),
                "IGRF loads from the assembly resource when no user override exists");
            Equal("8d63fc6433515be61211a122eb3c1fae5f46aa2a6a34f6addcf5cf927dc928d4", baseline.Sha256);

            string candidate = Path.Combine(root, "igrf-update.dat");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "config", "geomagnetics", "igrf.dat"), candidate);
            File.AppendAllText(candidate, "\n/ user update fixture\n", Encoding.UTF8);
            IgrfConfigurationState updated = configuration.UpdateFromFile(candidate);
            True(updated.UsesUserOverride && File.Exists(configuration.UserOverridePath),
                "validated IGRF update is installed in the user configuration directory");
            True(updated.Sha256 != baseline.Sha256, "installed override reports its own content hash");

            string invalid = Path.Combine(root, "invalid-igrf.dat");
            File.WriteAllText(invalid, "not an IGRF coefficient table", Encoding.UTF8);
            Throws<FormatException>(() => configuration.UpdateFromFile(invalid));
            Equal(updated.Sha256, configuration.GetState().Sha256);

            IgrfConfigurationState restored = configuration.RestoreEmbeddedBaseline();
            True(!restored.UsesUserOverride && restored.SourcePath.StartsWith("embedded://", StringComparison.Ordinal),
                "restoring the bundled baseline switches runtime loading back to embedded data");
            Equal(baseline.Sha256, restored.Sha256);
            True(Directory.GetFiles(Path.GetDirectoryName(configuration.UserOverridePath)!, "igrf.dat.disabled-*").Length == 1,
                "restoring bundled data preserves the former user override as a reversible backup");
        });
    }

    private static void TestIgrfSphericalHarmonicSynthesis()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "config",
            "geomagnetics",
            "igrf.dat");
        IgrfModelSet models = IgrfDatParser.Load(path);

        GeomagneticFieldResult equator =
            IgrfSphericalHarmonicEngine.Evaluate(
                models,
                latitudeDeg: 0.0,
                longitudeDeg: 0.0,
                heightMeters: 0.0,
                decimalYear: 2025.0);
        Equal(27456.62, equator.NorthNt, 2.0);
        Equal(-1926.55, equator.EastNt, 2.0);
        Equal(-15997.35, equator.DownNt, 2.0);
        Equal(31835.40, equator.TotalNt, 2.0);
        Equal(-4.0137, equator.DeclinationDeg, 0.02);
        Equal(-30.1657, equator.InclinationDeg, 0.02);

        GeomagneticFieldResult survey =
            IgrfSphericalHarmonicEngine.Evaluate(
                models,
                latitudeDeg: 47.7616,
                longitudeDeg: 111.2339,
                heightMeters: 0.0,
                dateUtc: new DateOnly(2026, 9, 13));
        Equal(2025, survey.ModelEpochYear);
        Equal(13, survey.MaxDegree);
        Equal(21872.9811, survey.NorthNt, 0.02);
        Equal(-2887.3851, survey.EastNt, 0.02);
        Equal(54763.0497, survey.DownNt, 0.02);
        Equal(59040.2905, survey.TotalNt, 0.02);
        Equal(-7.5199606, survey.DeclinationDeg, 1e-5);
        Equal(68.0566622, survey.InclinationDeg, 1e-5);

        Throws<ArgumentOutOfRangeException>(() =>
            IgrfSphericalHarmonicEngine.Evaluate(
                models,
                latitudeDeg: 47.0,
                longitudeDeg: 111.0,
                heightMeters: 0.0,
                decimalYear: 2031.0));
    }

    private static IgrfModelSet LoadIgrfModels()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory,
            "config",
            "geomagnetics",
            "igrf.dat");
        True(File.Exists(path), $"IGRF model file missing from test output: {path}");
        return IgrfDatParser.Load(path);
    }

    private static void TestBatchCentralMeridian()
    {
        var batch = CoordinateService.ConvertBatch(
            "112.0,39.0\n118.0,39.0",
            CoordinateInputMode.DecimalDegrees,
            new ProjectionParameters(),
            autoCentralMeridian: true);
        Equal(2, batch.Results.Count);
        True(batch.SuggestedMeridians.Count > 1, "cross-zone suggestions were not detected");
        True(batch.Warning.Contains("跨越多个三度带"), "cross-zone warning missing");
    }

    private static void TestStateMachine()
    {
        StateMachine.Validate("PRECHECK", "ACTIVE");
        Throws<InvalidOperationException>(() => StateMachine.Validate("ACTIVE", "BLOCKED"));
        StateMachine.Validate("ACTIVE", "BLOCKED", "现场设备未通过测试");
        Throws<InvalidOperationException>(() => StateMachine.Validate("RELEASED", "ACTIVE", "test"));
    }

    private static void TestWorkspace()
    {
        WithTempRoot(root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "10_工区", "A区"));
            var store = new JsonStore();
            var ws = new WorkspaceService(store);
            ws.Initialize(root, "回归测试项目");

            var areas = ws.GetWorkareas(root);
            Equal(1, areas.Count);
            Equal("A区", areas[0].DisplayName);

            WorkareaRecord registered = ws.RegisterWorkarea(root, "B区");
            Equal("10_工区/B区", registered.Path);
            True(Directory.Exists(Path.Combine(root, "10_工区", "B区")), "RegisterWorkarea must create the base workarea directory");
            var syncedAreas = ws.SyncWorkareas(root);
            True(syncedAreas.Any(x => x.DisplayName == "B区"), "registered lazy workarea lost during sync");

            var task = ws.AddTask(root, "检查控制点", "A区", "Tester", "2026-09-05", "P0", "golden");
            True(task.TaskId.StartsWith("TASK-"), "task id");
            Equal(1, ws.GetTasks(root).Count);

            ws.SetGate(root, "source", "PASSED", "源资料核对通过", ["01_原始资料/source.csv"], "Tester");
            var loaded = ws.GetWorkspace(root);
            Equal("PASSED", loaded.Gates["source"]);

            ws.SetProjectStatus(root, "ACTIVE");
            Equal("ACTIVE", ws.GetWorkspace(root).Status);
        });
    }

    private static void TestRememberedWorkareaContext()
    {
        WithTempRoot(root =>
        {
            string contextPath = Path.Combine(root, "_user-context-test.json");
            var store = new JsonStore();
            var context = new UserContextService(store, contextPath);

            context.SaveCurrentWorkarea(root, @"10_工区\A区");
            Equal("10_工区/A区", context.Load(root).CurrentWorkareaPath);

            context.SaveCurrentWorkarea(root, "10_工区/B区/");
            Equal("10_工区/B区", context.Load(root).CurrentWorkareaPath);

            string otherRoot = Path.Combine(root, "other-project");
            Directory.CreateDirectory(otherRoot);
            context.SaveCurrentWorkarea(otherRoot, "10_工区/C区");
            Equal("10_工区/B区", context.Load(root).CurrentWorkareaPath);
            Equal("10_工区/C区", context.Load(otherRoot).CurrentWorkareaPath);

            context.SaveCurrentWorkarea(root, "");
            Equal("", context.Load(root).CurrentWorkareaPath);
            Equal("10_工区/C区", context.Load(otherRoot).CurrentWorkareaPath);
            True(File.Exists(contextPath), "local user-context file was not written");

            File.WriteAllText(contextPath, "{broken-user-context", new UTF8Encoding(false));
            Equal("", context.Load(root).CurrentWorkareaPath);
            context.SaveCurrentWorkarea(root, "10_工区/A区");
            Equal("10_工区/A区", context.Load(root).CurrentWorkareaPath);
        });
    }

    private static void TestMovedProjectAndWorkareaFolderRelink()
    {
        WithTempRoot(root =>
        {
            string oldParent = Path.Combine(root, "old-parent");
            string project = Path.Combine(oldParent, "Project-A");
            string oldArea = Path.Combine(project, "10_工区", "A区");
            Directory.CreateDirectory(oldArea);

            var store = new JsonStore();
            var workspace = new WorkspaceService(store);
            workspace.Initialize(project, "移动恢复测试");
            WorkareaRecord area = workspace.GetWorkareas(project).Single();
            workspace.SetWorkareaStatus(project, area.WorkareaId, "ACTIVE");

            string newParent = Path.Combine(root, "renamed-parent");
            Directory.Move(oldParent, newParent);
            string movedProject = Path.Combine(newParent, "Project-A");
            string movedArea = Path.Combine(movedProject, "10_工区", "A区");

            Equal(
                Path.GetFullPath(movedProject),
                WorkspaceService.FindInitializedProjectRoot(movedArea)!);

            WorkspaceRecord relinkedProject = workspace.RelinkProjectRoot(movedProject);
            Equal(Path.GetFullPath(movedProject), relinkedProject.Root);

            string renamedArea = Path.Combine(movedProject, "10_工区", "A区_改名");
            Directory.Move(movedArea, renamedArea);
            WorkareaRecord relinkedArea = workspace.AttachExistingWorkareaFolder(
                movedProject,
                renamedArea,
                area.WorkareaId);

            Equal(area.WorkareaId, relinkedArea.WorkareaId);
            Equal("ACTIVE", relinkedArea.Status);
            Equal("10_工区/A区_改名", relinkedArea.Path);
            Equal("A区_改名", relinkedArea.DisplayName);

            string extra = Path.Combine(movedProject, "10_工区", "B区");
            Directory.CreateDirectory(extra);
            WorkareaRecord attached = workspace.AttachExistingWorkareaFolder(
                movedProject,
                extra);
            Equal("10_工区/B区", attached.Path);
            Equal("B区", attached.DisplayName);

            string outside = Path.Combine(root, "outside-area");
            Directory.CreateDirectory(outside);
            Throws<InvalidOperationException>(() =>
                workspace.AttachExistingWorkareaFolder(movedProject, outside));
        });
    }

    private static void TestFieldOperationsSettingsPersistence()
    {
        WithTempRoot(root =>
        {
            var store = new JsonStore();
            var workspace = new WorkspaceService(store);
            workspace.Initialize(
                root,
                "Field Settings Test");
            var field =
                new FieldworkService(
                    store,
                    workspace);

            var saved =
                field.SaveFieldOperationsSettings(
                    root,
                    new FieldOperationsSettingsRecord
                    {
                        WorkareaPath =
                            "10_工区/020537工区",
                        FieldDataFolder =
                            "project:10_工区/020537工区/05_外业记录与采集成果",
                        ExpectedDailySorties = 17,
                        AcceptedRelocations = 2
                    });

            Equal(17, saved.ExpectedDailySorties);
            Equal(2, saved.AcceptedRelocations);
            True(
                File.Exists(
                    ProjectPaths.Json(
                        root,
                        "field_operations_settings.json")),
                "field operation settings file missing");

            var reopenedStore =
                new JsonStore();
            var reopenedWorkspace =
                new WorkspaceService(
                    reopenedStore);
            var reopenedField =
                new FieldworkService(
                    reopenedStore,
                    reopenedWorkspace);

            FieldOperationsSettingsRecord? loaded =
                reopenedField.GetFieldOperationsSettings(
                    root,
                    "10_工区/020537工区");
            True(
                loaded is not null,
                "field operation settings not reloadable");
            Equal(
                17,
                loaded!.ExpectedDailySorties);
            Equal(
                2,
                loaded.AcceptedRelocations);
            Equal(
                "project:10_工区/020537工区/05_外业记录与采集成果",
                loaded.FieldDataFolder);
        });
    }

    private static void TestConcaveEndpointConnectSplit()
    {
        WithTempRoot(root =>
        {
            string source = Path.Combine(root, "concave_endpoints.csv");
            File.WriteAllText(
                source,
                "Line,X,Y,Tag\n" +
                "L1,0,0,a\n" +
                "L1,10,0,b\n" +
                "L2,0,10,c\n" +
                "L2,4,10,d\n" +
                "L2,,,\n" +
                "L2,6,10,e\n" +
                "L2,10,10,f\n" +
                // Deliberately write the high-coordinate fragment first.
                // SPLIT must normalize physical sides rather than trusting row order.
                "L3,6,20,i\n" +
                "L3,10,20,j\n" +
                "L3,,,\n" +
                "L3,0,20,g\n" +
                "L3,4,20,h\n",
                new UTF8Encoding(false));

            var service = new ConcaveBoundaryLineService();
            True(
                service.TryInspectConcaveInputBeforeNumericParse(
                    source,
                    out ConcaveLineInspection? autoInspection),
                "recognized Line/X/Y endpoint table should be pre-inspectable");
            True(autoInspection is not null, "auto inspection missing");
            Equal(2, autoInspection!.DisconnectedLines.Count);

            ConcaveLineInspection inspection = service.Inspect(source);
            Equal(3, inspection.SourceLineCount);
            Equal(2, inspection.DisconnectedLines.Count);
            Equal("L2", inspection.DisconnectedLines[0].SourceLine);
            Equal("2 + 2", string.Join(" + ", inspection.DisconnectedLines[0].SegmentPointCounts));
            Equal("L3", inspection.DisconnectedLines[1].SourceLine);
            True(
                inspection.SourceGeometryFingerprint.Length == 64,
                "source geometry fingerprint missing");
            True(
                inspection.DisconnectedLines.All(item =>
                    item.GeometryFingerprint.Length == 64),
                "per-line segment geometry fingerprint missing");

            string output = Path.Combine(root, "repaired.csv");
            var rules = new List<ConcaveLineRule>
            {
                new() { SourceLine = "L2", Action = ConcaveLineActions.Connect },
                new()
                {
                    SourceLine = "L3",
                    Action = ConcaveLineActions.Split,
                    SecondOutputLine = "L4"
                }
            };

            IReadOnlyList<SurveyBoundaryPoint> concaveBoundary =
            [
                new(0, 0),
                new(10, 0),
                new(10, 20),
                new(6, 20),
                new(6, 5),
                new(4, 5),
                new(4, 20),
                new(0, 20)
            ];
            True(
                !RouteBoundarySafetyService.IsSegmentInsideOrOnBoundary(
                    new SurveyBoundaryPoint(0, 10),
                    new SurveyBoundaryPoint(10, 10),
                    concaveBoundary),
                "concave-notch crossing segment was incorrectly accepted");
            True(
                RouteBoundarySafetyService.IsSegmentInsideOrOnBoundary(
                    new SurveyBoundaryPoint(0, 10),
                    new SurveyBoundaryPoint(4, 10),
                    concaveBoundary),
                "valid in-boundary segment was incorrectly rejected");
            Throws<InvalidOperationException>(() => service.Process(
                source,
                Path.Combine(root, "unsafe_connect.csv"),
                rules,
                concaveBoundary,
                "测试凹形边界"));

            var staleRules = rules.Select(rule => rule.Clone()).ToList();
            staleRules[0].GeometryFingerprint =
                "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
            Throws<InvalidOperationException>(() => service.Process(
                source,
                Path.Combine(root, "stale_rule.csv"),
                staleRules));

            var unsafePlan = new RoutePlanResult
            {
                Mode = "projected",
                CentralMeridian = 117d,
                Sorties =
                [
                    new RouteSortie
                    {
                        Name = "A01",
                        Points =
                        [
                            new RouteEndpoint { Line = "L2", X = 0, Y = 10 },
                            new RouteEndpoint { Line = "L2", X = 10, Y = 10 }
                        ],
                        SourceLines = ["L2"]
                    }
                ]
            };
            Throws<InvalidOperationException>(() =>
                RouteBoundarySafetyService.EnsurePlanInsideBoundary(
                    unsafePlan,
                    concaveBoundary,
                    "测试凹形边界"));

            ConcaveLineProcessResult result = service.Process(source, output, rules);
            Equal(3, result.SourceLineCount);
            Equal(4, result.OutputLineCount);
            Equal(1, result.ConnectedLineCount);
            Equal(1, result.SplitLineCount);
            Equal(8, result.OutputDataRowCount);

            TextTable repaired = new TextTableAnalysisService().Read(output);
            Equal(8, repaired.Rows.Count);
            True(repaired.Rows.All(row => row[1].Length > 0 && row[2].Length > 0),
                "processed route endpoint table must not contain blank X/Y separators");

            string[] lines = repaired.Rows.Select(row => row[0]).ToArray();
            Equal(2, lines.Count(x => x == "L1"));
            Equal(2, lines.Count(x => x == "L2"));
            Equal(2, lines.Count(x => x == "L3"));
            Equal(2, lines.Count(x => x == "L4"));

            string[] l2x = repaired.Rows.Where(row => row[0] == "L2").Select(row => row[1]).ToArray();
            Equal("0", l2x[0]);
            Equal("10", l2x[1]);
            Equal("g,h", string.Join(",", repaired.Rows.Where(row => row[0] == "L3").Select(row => row[3])));
            Equal("i,j", string.Join(",", repaired.Rows.Where(row => row[0] == "L4").Select(row => row[3])));

            True(
                !service.TryInspectConcaveInputBeforeNumericParse(
                    output,
                    out _),
                "clean processed output must fall through to the normal v25 reader");
            Equal(0, service.Inspect(output).DisconnectedLines.Count);

            IReadOnlyList<RawRoutePoint> cleanedRaw =
                DelimitedTextReader.ReadRawRouteCsv(output, "Point,Y,X");
            Equal(8, cleanedRaw.Count);

            // v0.5.4 regression: a SPLIT output must not be immediately
            // re-connected by the normal v25 snake transition.
            var splitPlans = new ConcaveSplitRoutePlanService();
            string sidecar = splitPlans.WriteContext(output, result);
            True(File.Exists(sidecar), "SPLIT sidecar context missing");
            True(
                splitPlans.TryLoadContext(
                    output,
                    out ConcaveSplitRouteContext? splitContext),
                "SPLIT sidecar context could not be reloaded");
            True(splitContext is not null, "SPLIT context is null");

            RoutePlanResult basePlan = new RoutePlanner().BuildFromCsv(
                output,
                new RoutePlanOptions
                {
                    CoordinateMode = RouteCoordinateMode.Geographic,
                    HeaderlessOrder = "Point,Y,X",
                    MaxSortieKm = 10000,
                    StartDirection = RouteStartDirection.Southwest,
                    KmzAglM = 50,
                    KmzSpeedMps = 12
                });

            ConcaveSplitPlanApplication safe =
                splitPlans.Apply(
                    basePlan,
                    splitContext!);

            Equal(1, safe.ProtectedSplitPairs);
            Equal(2, safe.RouteBranches);
            True(
                safe.Plan.Sorties.Count >= 2,
                "SPLIT-safe routing must create a branch boundary");

            foreach (RouteSortie sortie in safe.Plan.Sorties)
            {
                bool hasSecondSide = sortie.SourceLines.Contains(
                    "L4",
                    StringComparer.OrdinalIgnoreCase);
                bool hasOtherSide = sortie.SourceLines.Any(line =>
                    !line.Equals(
                        "L4",
                        StringComparison.OrdinalIgnoreCase));

                True(
                    !(hasSecondSide && hasOtherSide),
                    "a sortie mixed the protected second SPLIT side with the primary branch");
            }

            True(
                safe.Plan.Sorties.All(sortie =>
                    !(sortie.SourceLines.Contains(
                          "L3",
                          StringComparer.OrdinalIgnoreCase) &&
                      sortie.SourceLines.Contains(
                          "L4",
                          StringComparer.OrdinalIgnoreCase))),
                "the two halves of a SPLIT line were put back into one sortie");

            // v0.78 regression: SPLIT is no longer limited to exactly two
            // disconnected segments. Segment 1 keeps the source line, segment
            // 2 uses the requested first extra line, and later segments are
            // allocated automatically into their own protected branches.
            string threeSegmentSource = Path.Combine(root, "three_segments.csv");
            File.WriteAllText(
                threeSegmentSource,
                "Line,X,Y\n" +
                "L5,0,30\nL5,2,30\nL5,,,\n" +
                "L5,4,30\nL5,6,30\nL5,,,\n" +
                "L5,8,30\nL5,10,30\n",
                new UTF8Encoding(false));
            string threeSegmentOutput = Path.Combine(root, "three_segments_repaired.csv");
            ConcaveLineProcessResult threeResult = service.Process(
                threeSegmentSource,
                threeSegmentOutput,
                [new ConcaveLineRule
                {
                    SourceLine = "L5",
                    Action = ConcaveLineActions.Split,
                    SecondOutputLine = "L20"
                }]);
            Equal(3, threeResult.OutputLineCount);
            Equal(3, threeResult.Mappings.Single().OutputLineLabels.Count);
            Equal("L5,L20,L21", string.Join(",", threeResult.Mappings.Single().OutputLineLabels));

            string threeSidecar = splitPlans.WriteContext(
                threeSegmentOutput,
                threeResult);
            True(File.Exists(threeSidecar), "three-segment SPLIT sidecar missing");
            True(
                splitPlans.TryLoadContext(
                    threeSegmentOutput,
                    out ConcaveSplitRouteContext? threeContext),
                "three-segment SPLIT sidecar could not be reloaded");
            RoutePlanResult threeBase = new RoutePlanner().BuildFromCsv(
                threeSegmentOutput,
                new RoutePlanOptions
                {
                    CoordinateMode = RouteCoordinateMode.Geographic,
                    HeaderlessOrder = "Point,Y,X",
                    MaxSortieKm = 10000,
                    StartDirection = RouteStartDirection.Southwest,
                    KmzAglM = 50,
                    KmzSpeedMps = 12
                });
            ConcaveSplitPlanApplication threeSafe = splitPlans.Apply(
                threeBase,
                threeContext!);
            Equal(1, threeSafe.ProtectedSplitPairs);
            Equal(3, threeSafe.RouteBranches);
            True(
                threeSafe.Plan.Sorties.All(sortie =>
                    sortie.SourceLines.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1),
                "three disconnected segments were mixed back into one route branch");

            string headerless = Path.Combine(root, "headerless.csv");
            File.WriteAllText(
                headerless,
                "L1,0,0\nL1,10,0\n",
                new UTF8Encoding(false));
            True(
                !service.TryInspectConcaveInputBeforeNumericParse(
                    headerless,
                    out _),
                "headerless v25-compatible files must fall through to the normal reader");

            Throws<InvalidOperationException>(() => service.Process(
                source,
                Path.Combine(root, "collision.csv"),
                [
                    new ConcaveLineRule { SourceLine = "L2", Action = ConcaveLineActions.Connect },
                    new ConcaveLineRule
                    {
                        SourceLine = "L3",
                        Action = ConcaveLineActions.Split,
                        SecondOutputLine = "L1"
                    }
                ]));
        });
    }

    private static void TestConcaveLineRuleProfilePersistence()
    {
        WithTempRoot(root =>
        {
            var store = new JsonStore();
            var profiles = new ConcaveLineRuleProfileService(store);

            const string workareaId = "WA-A";
            const string workareaPath = "10_工区/A区";
            const string partition = "P01";
            const string sourceFingerprintA =
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string sourceFingerprintB =
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

            profiles.Save(
                root,
                workareaId,
                workareaPath,
                partition,
                sourceFingerprintA,
                [
                    new ConcaveLineRule
                    {
                        SourceLine = "L8",
                        Action = ConcaveLineActions.Connect,
                        Notes = "仅当前几何允许",
                        GeometryFingerprint =
                            "1111111111111111111111111111111111111111111111111111111111111111"
                    },
                    new ConcaveLineRule
                    {
                        SourceLine = "L9",
                        Action = ConcaveLineActions.Split,
                        SecondOutputLine = "L77",
                        GeometryFingerprint =
                            "2222222222222222222222222222222222222222222222222222222222222222"
                    }
                ]);

            ConcaveLineRuleProfile a = profiles.Load(
                root,
                workareaId,
                workareaPath,
                partition,
                sourceFingerprintA);
            Equal(2, a.Rules.Count);
            Equal(ConcaveLineActions.Connect, a.Rules[0].Action);
            Equal(ConcaveLineActions.Split, a.Rules[1].Action);
            Equal("L77", a.Rules[1].SecondOutputLine);
            Equal(partition, a.PartitionCode);
            Equal(sourceFingerprintA, a.SourceGeometryFingerprint);

            Equal(
                0,
                profiles.Load(
                    root,
                    workareaId,
                    workareaPath,
                    "P02",
                    sourceFingerprintA).Rules.Count);
            Equal(
                0,
                profiles.Load(
                    root,
                    workareaId,
                    workareaPath,
                    partition,
                    sourceFingerprintB).Rules.Count);

            profiles.Save(
                root,
                "WA-B",
                "10_工区/B区",
                "P01",
                sourceFingerprintA,
                [
                    new ConcaveLineRule
                    {
                        SourceLine = "L39",
                        Action = ConcaveLineActions.Split,
                        SecondOutputLine = "L80",
                        GeometryFingerprint =
                            "3333333333333333333333333333333333333333333333333333333333333333"
                    }
                ]);

            Equal(
                2,
                profiles.Load(
                    root,
                    workareaId,
                    workareaPath,
                    partition,
                    sourceFingerprintA).Rules.Count);
            Equal(
                1,
                profiles.Load(
                    root,
                    "WA-B",
                    "10_工区/B区",
                    "P01",
                    sourceFingerprintA).Rules.Count);
        });
    }

    private static void TestArtifacts()
    {
        WithTempRoot(root =>
        {
            Directory.CreateDirectory(Path.Combine(root, "01_原始资料"));
            string file = Path.Combine(root, "01_原始资料", "source.csv");
            File.WriteAllText(file, "x,y\n1,2\n", new UTF8Encoding(false));

            var store = new JsonStore();
            var ws = new WorkspaceService(store);
            ws.Initialize(root, "Artifact Test");
            var artifacts = new ArtifactService(store, ws);

            var first = artifacts.Scan(root, withHash: true);
            Equal(1, first.ScannedCount);
            var row = artifacts.List(root).Single(x => x.Name == "source.csv");
            Equal("raw", row.Role);
            Equal("COMPUTED", row.HashStatus);
            True(!string.IsNullOrWhiteSpace(row.Sha256), "sha256 missing");
            True(!string.IsNullOrWhiteSpace(row.ArtifactId), "artifact id missing");
            Equal("PROJECT", row.WorkareaAssignmentStatus);
            Equal("", row.WorkareaRef);

            artifacts.Update(
                root,
                row.Path,
                role: "processing",
                selected: true,
                versionLabel: "v2");
            row = artifacts.List(root).Single(x => x.Name == "source.csv");
            Equal("processing", row.Role);
            Equal(true, row.Selected ?? false);
            Equal("v2", row.VersionLabel);

            File.AppendAllText(file, "3,4\n");
            artifacts.Scan(root, withHash: true);
            row = artifacts.List(root).Single(x => x.Name == "source.csv");
            Equal("CHANGED_SINCE_LAST_SCAN", row.ChangeStatus);
        });
    }

    private static void TestArtifactWorkareaModel()
    {
        WithTempRoot(root =>
        {
            string aDir = Path.Combine(root, "10_工区", "A区");
            string bDir = Path.Combine(root, "10_工区", "B区");
            Directory.CreateDirectory(aDir);
            Directory.CreateDirectory(bDir);

            var store = new JsonStore();
            var ws = new WorkspaceService(store);
            ws.Initialize(root, "Artifact Workarea Test");

            List<WorkareaRecord> workareas = ws.GetWorkareas(root);
            WorkareaRecord a = workareas.Single(x => x.DisplayName == "A区");
            WorkareaRecord b = workareas.Single(x => x.DisplayName == "B区");

            string aFile = Path.Combine(aDir, "source.dat");
            File.WriteAllText(aFile, "stable-content", new UTF8Encoding(false));
            string rootFile = Path.Combine(root, "10_工区", "README.md");
            File.WriteAllText(rootFile, "not a workarea", new UTF8Encoding(false));

            var artifacts = new ArtifactService(store, ws);
            artifacts.Scan(root, withHash: true);

            ArtifactRecord source = artifacts.List(root).Single(x => x.Name == "source.dat");
            Equal("ASSIGNED", source.WorkareaAssignmentStatus);
            Equal(a.WorkareaId, source.WorkareaRef);
            string stableId = source.ArtifactId;

            ArtifactRecord readme = artifacts.List(root).Single(x => x.Name == "README.md");
            Equal("REVIEW_REQUIRED", readme.WorkareaAssignmentStatus);
            Equal("", readme.WorkareaRef);

            string moved = Path.Combine(bDir, "source_moved.dat");
            File.Move(aFile, moved);

            // A normal scan may happen before the operator runs a full-hash
            // consistency scan. Stable move reconciliation must still work.
            artifacts.Scan(root, withHash: false);
            artifacts.Scan(root, withHash: true);

            ArtifactRecord movedRow = artifacts.List(root).Single(x => x.Name == "source_moved.dat");
            Equal(stableId, movedRow.ArtifactId);
            Equal("MOVED", movedRow.ChangeStatus);
            Equal("ASSIGNED", movedRow.WorkareaAssignmentStatus);
            Equal(b.WorkareaId, movedRow.WorkareaRef);
            True(
                movedRow.PreviousPaths.Any(x => x.EndsWith("10_工区/A区/source.dat", StringComparison.OrdinalIgnoreCase)),
                "previous path lineage missing");

            ArtifactModelMigrationResult migration = artifacts.EnsureV03Metadata(root);
            Equal(0, migration.NewIds);
        });
    }

    private static void TestFolderLayoutLazyCreation()
    {
        WithTempRoot(root =>
        {
            string editablePath = Path.Combine(root, "user-config", "project-folder-layout.v1.json");
            var editableLayout = new FolderLayoutService(editablePath);
            editableLayout.EnsureEditableConfigurationFile();
            True(File.Exists(editablePath), "opening layout configuration materializes the embedded default in a writable user location");
            True(new FolderLayoutService(editablePath).Load().Operations.Count > 0,
                "materialized layout configuration remains schema-valid");
            string originalLayout = File.ReadAllText(editablePath);
            editableLayout.EnsureEditableConfigurationFile();
            Equal(originalLayout, File.ReadAllText(editablePath));

            string? previousEnvironment = Environment.GetEnvironmentVariable("MAGOPS_FOLDER_LAYOUT");
            try
            {
                string environmentPath = Path.Combine(root, "environment-config", FolderLayoutService.ConfigFileName);
                Environment.SetEnvironmentVariable("MAGOPS_FOLDER_LAYOUT", environmentPath);
                var environmentLayout = new FolderLayoutService();
                Equal(Path.GetFullPath(environmentPath), environmentLayout.ConfigurationPath);
                True(environmentLayout.Load().Operations.Count > 0,
                    "missing environment override reads embedded defaults without creating files");
                True(!File.Exists(environmentPath), "reading layout must not materialize its editable copy");
                Equal(Path.GetFullPath(editablePath), editableLayout.ConfigurationPath);
                environmentLayout.EnsureEditableConfigurationFile();
                Equal(originalLayout, File.ReadAllText(environmentPath));
                File.WriteAllText(environmentPath, "invalid-layout-fixture");
                bool invalidRejected = false;
                try { _ = environmentLayout.Load(); }
                catch (JsonException) { invalidRejected = true; }
                True(invalidRejected, "invalid user override is reported instead of silently losing customization");
                True(editableLayout.Load().Operations.Count > 0,
                    "explicit configuration has precedence over an invalid environment override");
            }
            finally
            {
                Environment.SetEnvironmentVariable("MAGOPS_FOLDER_LAYOUT", previousEnvironment);
            }

            var folders = new FolderLayoutService();
            string workarea = "10_工区/A区";

            string terrainPreview = folders.ResolveOperationDirectory(
                root, "project_terrain");
            True(terrainPreview.EndsWith(
                    Path.Combine("01_项目依据与公共资料", "地形资料"),
                    StringComparison.OrdinalIgnoreCase),
                $"project terrain folder mapping incorrect: {terrainPreview}");
            True(!Directory.Exists(terrainPreview),
                "project terrain directory must stay lazy until a terrain file is archived");

            string preview = folders.ResolveOperationDirectory(
                root, "route_export", workarea);
            True(!Directory.Exists(preview), "ResolveOperationDirectory must not create folders");
            True(!Directory.Exists(Path.Combine(root, "10_工区")),
                "business folders should not exist before a write");

            string created = folders.EnsureOperationDirectory(
                root, "route_export", workarea);
            True(Directory.Exists(created), "EnsureOperationDirectory did not create target");
            True(created.EndsWith(
                    Path.Combine("04_航线设计与任务文件", "05_设计航线"),
                    StringComparison.OrdinalIgnoreCase),
                $"route folder mapping incorrect: {created}");

            string coordinatePreview = folders.ResolveOperationDirectory(
                root,
                "coordinate_export",
                workarea);
            True(
                coordinatePreview.EndsWith(
                    Path.Combine("03_坐标与空间资料", "01_坐标转换CSV"),
                    StringComparison.OrdinalIgnoreCase),
                $"coordinate_export folder mapping incorrect: {coordinatePreview}");

            string excelPreview = folders.ResolveOperationDirectory(
                root,
                "processing_excel",
                workarea);
            True(
                excelPreview.EndsWith(
                    Path.Combine("06_数据处理", "Excel处理"),
                    StringComparison.OrdinalIgnoreCase),
                $"processing_excel folder mapping incorrect: {excelPreview}");
            True(
                !Directory.Exists(excelPreview),
                "Excel处理目录 must stay lazy until an actual output write");

            string oasisProcessingPreview = folders.ResolveOperationDirectory(
                root,
                "processing_oasis",
                workarea);
            True(
                oasisProcessingPreview.EndsWith(
                    Path.Combine("06_数据处理", "Oasis处理"),
                    StringComparison.OrdinalIgnoreCase),
                $"processing_oasis folder mapping incorrect: {oasisProcessingPreview}");
            True(
                !Directory.Exists(oasisProcessingPreview),
                "Oasis处理目录 must stay lazy until an actual output write");

            string coordinateKmlPreview = folders.ResolveOperationDirectory(
                root,
                "coordinate_kml_range",
                workarea);
            True(
                coordinateKmlPreview.EndsWith(
                    Path.Combine("03_坐标与空间资料", "02_范围KML"),
                    StringComparison.OrdinalIgnoreCase),
                "coordinate_kml_range folder mapping incorrect");

            string coordinateSpatialPreview = folders.ResolveOperationDirectory(
                root,
                "coordinate_spatial_export",
                workarea);
            True(
                coordinateSpatialPreview.EndsWith(
                    Path.Combine("03_坐标与空间资料", "03_空间文件"),
                    StringComparison.OrdinalIgnoreCase),
                "coordinate_spatial_export folder mapping incorrect");

            string oasisPlyPreview = folders.ResolveOperationDirectory(
                root,
                "coordinate_oasis_ply",
                workarea);
            True(
                oasisPlyPreview.EndsWith(
                    Path.Combine(
                        "04_航线设计与任务文件",
                        "01_PLY工区边界"),
                    StringComparison.OrdinalIgnoreCase),
                $"Oasis PLY folder mapping incorrect: {oasisPlyPreview}");
            True(
                !Directory.Exists(oasisPlyPreview),
                "coordinate_oasis_ply preview must remain lazy");

            string preprocessPreview = folders.ResolveOperationDirectory(
                root, "route_preprocess", workarea);
            True(!Directory.Exists(preprocessPreview),
                "route_preprocess preview must not create its folder");
            True(preprocessPreview.EndsWith(
                    Path.Combine("04_航线设计与任务文件", "03_连接处理"),
                    StringComparison.OrdinalIgnoreCase),
                "route_preprocess folder mapping incorrect");

            string endpointPreview = folders.ResolveOperationDirectory(
                root, "route_endpoint_design", workarea);
            True(endpointPreview.EndsWith(
                    Path.Combine("04_航线设计与任务文件", "02_自动航线端点"),
                    StringComparison.OrdinalIgnoreCase),
                "route_endpoint_design folder mapping incorrect");

            DateTimeOffset splitTimestamp = new(
                2026, 9, 10, 8, 9, 10, 123, TimeSpan.Zero);
            string splitPreview = folders.ResolveOperationDirectory(
                root,
                "route_kml_subdivision",
                workarea,
                splitTimestamp);
            True(
                splitPreview.EndsWith(
                    Path.Combine(
                        "04_航线设计与任务文件",
                        "02_KML分区"),
                    StringComparison.OrdinalIgnoreCase),
                "route_kml_subdivision folder mapping incorrect");

            string routeProjectPreview = folders.ResolveOperationDirectory(
                root, "route_project", workarea);
            True(routeProjectPreview.EndsWith(
                    Path.Combine("04_航线设计与任务文件", "04_编辑工程"),
                    StringComparison.OrdinalIgnoreCase),
                "route_project folder mapping incorrect");

            string routeImportPreview = folders.ResolveOperationDirectory(
                root, "route_kml_import", workarea);
            True(routeImportPreview.EndsWith(
                    Path.Combine(
                        "04_航线设计与任务文件",
                        "05_设计航线",
                        "03_外部导入KML"),
                    StringComparison.OrdinalIgnoreCase),
                $"route_kml_import folder mapping incorrect: {routeImportPreview}");
            True(
                !Directory.Exists(routeImportPreview),
                "route_kml_import preview must remain lazy until an actual import");

            FolderLayoutDocument doc = folders.Load();
            True(doc.WorkareaFolders.All(x =>
                    !x.RelativePath.Equals("09_GIS工程与软件项目", StringComparison.OrdinalIgnoreCase)),
                "new workarea layout must not define a standalone 09_GIS folder");
        });
    }

    private static void TestFolderAuthorityMigration()
    {
        WithTempRoot(root =>
        {
            var service = new FolderLayoutMigrationService();
            string legacy = Path.Combine(root, "legacy", "processing");
            Directory.CreateDirectory(legacy);
            File.WriteAllText(Path.Combine(legacy, "a.txt"), "A");

            FolderMigrationItem item = new(
                "legacy/processing",
                "10_工区/W01/06_数据处理",
                FolderMigrationAction.MoveDirectory,
                "explicit legacy layout migration");

            FolderMigrationPlan preview = service.Preview(root, [item]);
            Equal(1, preview.Items.Count);
            Equal(0, preview.Conflicts.Count);
            True(preview.CanExecute, "explicit migration should be executable");
            True(File.Exists(Path.Combine(legacy, "a.txt")),
                "preview must never move files");

            Throws<InvalidOperationException>(() => service.Execute(preview, confirmed: false));
            True(File.Exists(Path.Combine(legacy, "a.txt")),
                "unconfirmed migration must never move files");

            FolderMigrationResult result = service.Execute(preview, confirmed: true);
            Equal(1, result.MovedDirectories);
            Equal(1, result.MovedFiles);
            True(File.Exists(Path.Combine(root, "10_工区", "W01", "06_数据处理", "a.txt")),
                "confirmed migration target missing");
            True(File.Exists(Path.Combine(result.BackupRoot, "legacy", "processing", "a.txt")),
                "explicit migration backup missing");

            string legacy2 = Path.Combine(root, "legacy2");
            string target2 = Path.Combine(root, "10_工区", "W02");
            Directory.CreateDirectory(legacy2);
            Directory.CreateDirectory(target2);
            File.WriteAllText(Path.Combine(legacy2, "same.txt"), "source");
            File.WriteAllText(Path.Combine(target2, "same.txt"), "target");

            FolderMigrationPlan conflict = service.Preview(root,
            [
                new FolderMigrationItem("legacy2", "10_工区/W02",
                    FolderMigrationAction.MergeDirectory, "collision test")
            ]);
            Equal(1, conflict.Conflicts.Count);
            True(!conflict.CanExecute, "collision must block migration");
            Throws<InvalidOperationException>(() => service.Execute(conflict, confirmed: true));
            Equal("source", File.ReadAllText(Path.Combine(legacy2, "same.txt")));
            Equal("target", File.ReadAllText(Path.Combine(target2, "same.txt")));

            Throws<InvalidDataException>(() => service.Preview(root,
            [
                new FolderMigrationItem("../escape", "10_工区/W03",
                    FolderMigrationAction.MoveDirectory, "unsafe")
            ]));

            // A late conflict after Preview must be detected under the write lock
            // before any item in the confirmed plan is moved.
            string lateA = Path.Combine(root, "legacy-late-a");
            string lateB = Path.Combine(root, "legacy-late-b");
            Directory.CreateDirectory(lateA);
            Directory.CreateDirectory(lateB);
            File.WriteAllText(Path.Combine(lateA, "a.txt"), "a");
            File.WriteAllText(Path.Combine(lateB, "b.txt"), "b");
            FolderMigrationPlan latePlan = service.Preview(root,
            [
                new FolderMigrationItem("legacy-late-a", "target-late-a",
                    FolderMigrationAction.MoveDirectory, "late-a"),
                new FolderMigrationItem("legacy-late-b", "target-late-b",
                    FolderMigrationAction.MoveDirectory, "late-b")
            ]);
            Directory.CreateDirectory(Path.Combine(root, "target-late-b"));
            File.WriteAllText(Path.Combine(root, "target-late-b", "b.txt"), "conflict");
            Throws<InvalidOperationException>(() => service.Execute(latePlan, confirmed: true));
            True(File.Exists(Path.Combine(lateA, "a.txt")) &&
                 File.Exists(Path.Combine(lateB, "b.txt")),
                "post-preview conflict caused a partial folder migration");

            // Overlapping trees inside the same confirmed migration plan are rejected
            // before backup/move.  A's target is inside B's source tree, so executing
            // both operations would make their result order-dependent.
            FolderMigrationPlan overlap = service.Preview(root,
            [
                new FolderMigrationItem("legacy-late-a", "legacy-late-b/nested",
                    FolderMigrationAction.MoveDirectory, "overlap-a"),
                new FolderMigrationItem("legacy-late-b", "target-overlap-b",
                    FolderMigrationAction.MoveDirectory, "overlap-b")
            ]);
            Throws<InvalidOperationException>(() => service.Execute(overlap, confirmed: true));
            True(File.Exists(Path.Combine(lateA, "a.txt")) &&
                 File.Exists(Path.Combine(lateB, "b.txt")),
                "overlapping migration plan moved source content");
        });
    }


    private static void TestRawMaterialImport()
    {
        WithTempRoot(root =>
        {
            var store = new JsonStore();
            var ws = new WorkspaceService(store);
            ws.Initialize(root, "Raw Import Test");

            string incoming = Path.Combine(root, "_incoming");
            Directory.CreateDirectory(incoming);
            string source = Path.Combine(incoming, "original.dat");
            File.WriteAllBytes(source, Enumerable.Range(0, 256).Select(i => (byte)i).ToArray());

            var folders = new FolderLayoutService();
            string workarea = "10_工区/A区";
            string rawPreview = folders.ResolveWorkareaFolder(
                root, workarea, "workarea.raw");
            True(!Directory.Exists(rawPreview), "raw business folder was pre-created");

            var importer = new RawMaterialImportService(folders);
            RawImportBatchResult result = importer.ImportAsync(
                    root, workarea, [source])
                .GetAwaiter().GetResult();

            True(File.Exists(source), "source file must be preserved");
            Equal("COMPLETED", result.Status);
            Equal(1, result.ArchivedCount);
            True(File.Exists(result.ManifestPath), "raw import manifest missing");
            string sanitizedManifest = Path.Combine(result.DestinationDirectory, "导入清单_脱敏.json");
            True(File.Exists(sanitizedManifest), "sanitized raw import manifest missing");
            string sanitizedText = File.ReadAllText(sanitizedManifest);
            True(!sanitizedText.Contains("source_path", StringComparison.OrdinalIgnoreCase),
                "sanitized manifest must not expose absolute source paths");
            string archived = Path.Combine(
                root,
                result.Items[0].ArchivedRelativePath.Replace('/', Path.DirectorySeparatorChar));
            True(File.Exists(archived), "archived copy missing");
            Equal(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source))).ToLowerInvariant(),
                result.Items[0].Sha256);

            var artifacts = new ArtifactService(store, ws);
            artifacts.RegisterRawImportBatch(root, result);
            var row = artifacts.List(root)
                .Single(x => x.Path.Equals(result.Items[0].ArchivedRelativePath, StringComparison.OrdinalIgnoreCase));
            Equal("raw", row.Role);
            Equal("raw_import", row.SourceKind);
            Equal("COMPUTED", row.HashStatus);
            Equal(result.Items[0].Sha256, row.Sha256 ?? "");
            Equal("ASSIGNED", row.WorkareaAssignmentStatus);
            True(!string.IsNullOrWhiteSpace(row.WorkareaRef), "raw import workarea ref missing");

            var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [row.Sha256!.ToLowerInvariant()] = row.Path
            };
            RawImportBatchResult duplicate = importer.ImportAsync(
                    root, workarea, [source], existing)
                .GetAwaiter().GetResult();
            Equal(0, duplicate.ArchivedCount);
            Equal(1, duplicate.SkippedDuplicateCount);
        });
    }

    private static void TestFieldworkFolderMappings()
    {
        WithTempRoot(root =>
        {
            var folders = new FolderLayoutService();
            string workarea = "10_工区/A区";

            var expected = new Dictionary<string, string>
            {
                ["equipment_checklist_export"] = Path.Combine("05_外业记录与采集成果", "出发整备", "设备清点"),
                ["equipment_test_export"] = Path.Combine("05_外业记录与采集成果", "出发整备", "设备测试"),
                ["flight_evidence"] = Path.Combine("05_外业记录与采集成果", "飞行架次"),
                ["base_station_evidence"] = Path.Combine("05_外业记录与采集成果", "日变站"),
                ["daily_log_export"] = Path.Combine("05_外业记录与采集成果", "每日日志"),
                ["issue_evidence"] = Path.Combine("05_外业记录与采集成果", "问题与处置"),
                ["field_data_integration"] = Path.Combine("06_数据处理", "01_磁测数据整理"),
                ["processing_diurnal"] = Path.Combine("06_数据处理", "02_日变改正"),
                ["field_gemlink_bridge"] = Path.Combine("06_数据处理", "90_GEMLink兼容"),
                ["field_qc_export"] = Path.Combine("05_外业记录与采集成果", "质量检查")
            };

            foreach (var pair in expected)
            {
                string preview = folders.ResolveOperationDirectory(root, pair.Key, workarea);
                True(preview.EndsWith(pair.Value, StringComparison.OrdinalIgnoreCase),
                    $"unexpected fieldwork mapping for {pair.Key}: {preview}");
                True(!Directory.Exists(preview), $"{pair.Key} should be lazy before write");
            }
        });
    }

    private static void TestFieldworkDocumentExport()
    {
        WithTempRoot(root =>
        {
            string output = Path.Combine(root, "05_外业记录与采集成果", "飞行架次", "flight.md");
            var row = new FlightBatchRecord
            {
                BatchId = "FLIGHT-001",
                Date = "2026-09-04",
                Workarea = "A区",
                Aircraft = "DJI M400",
                Magnetometer = "GSM-19T",
                Status = "COMPLETED",
                Result = "现场记录",
                UpdatedAt = "2026-09-04T12:00:00+08:00"
            };

            FieldworkDocumentExportService.WriteFlight(output, row);
            True(File.Exists(output), "fieldwork business document missing");
            string text = File.ReadAllText(output);
            True(text.Contains("已完成"), "business document should localize status");
            True(text.Contains("FLIGHT-001"), "flight id missing from export");

            var store = new JsonStore();
            var workspace = new WorkspaceService(store);
            workspace.Initialize(root, "Base Station Update Test");
            var fieldwork = new FieldworkService(store, workspace);
            BaseStationLogRecord station = fieldwork.AddBaseStation(
                root,
                "2026-09-04",
                "BASE-01",
                "initial",
                "108.1000",
                "47.3000",
                "1200",
                "CGCS2000",
                "UTC",
                "1",
                "00:00",
                "08:00");
            BaseStationLogRecord updatedStation = fieldwork.UpdateBaseStation(
                root,
                station.RecordId,
                "2026-09-04",
                "BASE-01",
                "updated",
                "108.2000",
                "47.4000",
                "1205",
                "CGCS2000",
                "UTC",
                "1",
                "00:10",
                "08:10");
            Equal(station.RecordId, updatedStation.RecordId);
            Equal("updated", updatedStation.Site);
            Equal("108.2000", updatedStation.Longitude);
            Equal(1, fieldwork.ListBaseStations(root).Count);

            var takeoffRecommendations = new[]
            {
                new FieldTakeoffRecommendation(
                    1,
                    2,
                    "D02-P01",
                    new GeoCoordinate(108.1, 47.3),
                    ["架次35", "架次36"],
                    120,
                    220)
            };
            string takeoffCsv = Path.Combine(root, "takeoff.csv");
            FieldOperationsAnalysisService.WriteRecommendationCsv(
                takeoffCsv,
                takeoffRecommendations);
            string takeoffCsvText = File.ReadAllText(takeoffCsv);
            True(
                takeoffCsvText.StartsWith("后续批次,推荐点", StringComparison.Ordinal),
                "takeoff recommendation CSV must use batch terminology");
            True(
                takeoffCsvText.Contains("批次 2", StringComparison.Ordinal),
                "takeoff recommendation CSV batch label missing");
            True(
                !takeoffCsvText.Contains("计划日", StringComparison.Ordinal),
                "takeoff recommendation export must not imply a real work day");

        });
    }

    private static void TestAtomicFieldworkExports()
    {
        WithTempRoot(root =>
        {
            string path = Path.Combine(root, "fieldwork", "report.md");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "previous-content");
            FieldworkDocumentExportService.WriteDailyProgress(path, new FieldDailyProgressRecord
            {
                Date = "2026-09-04",
                Workarea = "A区",
                RouteFingerprint = "route",
                PlannedSortieCount = 1,
                TodayCompletedSorties = ["架次01"],
                CompletedSorties = ["架次01"],
                RemainingSorties = [],
                ExpectedDailySorties = 1,
                ProgressTimeZone = FieldMagDataService.BeijingProgressTimeZone,
                DailyCompletionPercent = 100,
                TotalCompletionPercent = 100,
                ProgressSummary = "完成",
                NextActionSummary = "无"
            });
            True(File.ReadAllText(path).Contains("外业工作进度简报", StringComparison.Ordinal),
                "fieldwork export should install complete replacement content");

            string atomicPath = Path.Combine(root, "fieldwork", "atomic.txt");
            File.WriteAllText(atomicPath, "stable");
            Throws<InvalidOperationException>(() => AtomicFileWriter.Write(atomicPath, _ =>
                throw new InvalidOperationException("injected write failure")));
            Equal("stable", File.ReadAllText(atomicPath));
            True(!Directory.EnumerateFiles(Path.GetDirectoryName(atomicPath)!, "*.staging-*", SearchOption.TopDirectoryOnly).Any(),
                "failed atomic export must clean its staging file");
        });
    }

    private static void TestExternalRouteKmlImport()
    {
        WithTempRoot(root =>
        {
            string sourceDirectory = Path.Combine(root, "outside");
            Directory.CreateDirectory(sourceDirectory);

            string first = SpatialGeometryService.WriteKml(
                Path.Combine(sourceDirectory, "外部架次01.kml"),
                [
                    new SpatialGeometry
                    {
                        Name = "外部架次01",
                        Type = "LineString",
                        Coordinates =
                        [
                            new GeoCoordinate(116.0000, 39.9000),
                            new GeoCoordinate(116.0100, 39.9100)
                        ]
                    }
                ]);

            string exactDuplicate =
                Path.Combine(
                    sourceDirectory,
                    "外部架次01_同内容副本.kml");
            File.Copy(
                first,
                exactDuplicate,
                overwrite: false);

            string second = SpatialGeometryService.WriteKml(
                Path.Combine(sourceDirectory, "外部组合航线.kml"),
                [
                    new SpatialGeometry
                    {
                        Name = "组合A",
                        Type = "LineString",
                        Coordinates =
                        [
                            new GeoCoordinate(116.0200, 39.9200),
                            new GeoCoordinate(116.0300, 39.9300)
                        ]
                    },
                    new SpatialGeometry
                    {
                        Name = "组合B",
                        Type = "LineString",
                        Coordinates =
                        [
                            new GeoCoordinate(116.0400, 39.9400),
                            new GeoCoordinate(116.0500, 39.9500)
                        ]
                    }
                ]);

            string third = SpatialGeometryService.WriteKml(
                Path.Combine(sourceDirectory, "子工区补充航线.kml"),
                [
                    new SpatialGeometry
                    {
                        Name = "子工区补充",
                        Type = "LineString",
                        Coordinates =
                        [
                            new GeoCoordinate(116.0600, 39.9600),
                            new GeoCoordinate(116.0700, 39.9700)
                        ]
                    }
                ]);

            string laterRepeat =
                SpatialGeometryService.WriteKml(
                    Path.Combine(
                        sourceDirectory,
                        "外部架次01_后测.kml"),
                    [
                        new SpatialGeometry
                        {
                            Name = "外部架次01",
                            Type = "LineString",
                            Coordinates =
                            [
                                new GeoCoordinate(116.0030, 39.9000),
                                new GeoCoordinate(116.0130, 39.9100)
                            ]
                        }
                    ]);

            byte[] firstBefore = File.ReadAllBytes(first);
            byte[] secondBefore = File.ReadAllBytes(second);
            byte[] thirdBefore = File.ReadAllBytes(third);

            var folders = new FolderLayoutService();
            string importRoot = folders.ResolveOperationDirectory(
                root,
                "route_kml_import",
                "10_工区/A区");
            True(!Directory.Exists(importRoot), "route import root should remain lazy before write");

            DateTimeOffset stamp = new(2026, 9, 11, 8, 9, 10, 123, TimeSpan.Zero);
            File.SetLastWriteTimeUtc(
                first,
                stamp.AddHours(-2).UtcDateTime);
            File.SetLastWriteTimeUtc(
                exactDuplicate,
                stamp.AddHours(-2).UtcDateTime);
            File.SetLastWriteTimeUtc(
                laterRepeat,
                stamp.AddHours(2).UtcDateTime);

            var importer = new RouteKmlImportService();
            RouteKmlImportResult imported = importer.Import(
                importRoot,
                [first, exactDuplicate, second],
                stamp);

            True(Directory.Exists(imported.BatchDirectory), "route import batch directory missing");
            Equal(2, imported.Items.Count);
            Equal(3, imported.RouteGeometryCount);
            Equal(3, imported.Files.Count); // two unchanged KML + one manifest
            True(imported.Files.All(File.Exists), "route import output file missing");

            RouteKmlImportItem firstItem = imported.Items.Single(x =>
                x.SourcePath.Equals(Path.GetFullPath(first), StringComparison.OrdinalIgnoreCase));
            RouteKmlImportItem secondItem = imported.Items.Single(x =>
                x.SourcePath.Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase));
            True(firstBefore.SequenceEqual(File.ReadAllBytes(firstItem.ImportedPath)),
                "first imported KML must be byte-identical");
            True(secondBefore.SequenceEqual(File.ReadAllBytes(secondItem.ImportedPath)),
                "second imported KML must be byte-identical");

            string manifestText = File.ReadAllText(imported.ManifestPath);
            True(manifestText.Contains("copy_only", StringComparison.Ordinal),
                "route import manifest must record copy-only mode");
            True(manifestText.Contains("未调用 RoutePlanner", StringComparison.Ordinal),
                "route import manifest must preserve no-redraw boundary");
            True(manifestText.Contains("appendable", StringComparison.Ordinal),
                "route import manifest must mark the route collection appendable");
            True(
                manifestText.Contains(
                    "measurement_time_utc",
                    StringComparison.Ordinal),
                "route import manifest must persist measurement time metadata");
            True(
                RouteKmlImportService
                    .InspectBatch(imported.BatchDirectory)
                    .Count == 2,
                "same-content KML selected under another filename must collapse by SHA");

            RouteKmlImportResult appended = importer.Append(
                imported.BatchDirectory,
                [third],
                stamp.AddMinutes(5));
            Equal(imported.BatchDirectory, appended.BatchDirectory);
            Equal(1, appended.Items.Count);
            Equal(1, appended.RouteGeometryCount);
            True(
                thirdBefore.SequenceEqual(
                    File.ReadAllBytes(appended.Items[0].ImportedPath)),
                "appended KML must remain byte-identical");

            RouteKmlImportResult duplicateAppend = importer.Append(
                imported.BatchDirectory,
                [third],
                stamp.AddMinutes(6));
            Equal(0, duplicateAppend.Items.Count);

            RouteKmlImportResult laterRepeatAppend =
                importer.Append(
                    imported.BatchDirectory,
                    [laterRepeat],
                    stamp.AddMinutes(7));
            Equal(1, laterRepeatAppend.Items.Count);
            True(
                !laterRepeatAppend.Items[0].Sha256.Equals(
                    firstItem.Sha256,
                    StringComparison.OrdinalIgnoreCase),
                "different repeated measurement must not be collapsed as the same file");

            var fieldAnalysis = new FieldOperationsAnalysisService();
            FieldRouteSnapshot? exact = fieldAnalysis.TryLoadImportedRouteKmlSnapshot(
                imported.BatchDirectory);
            True(exact is not null, "imported route batch was not readable by field operations");
            Equal("外部导入航线 KML", exact!.SourceLabel);
            Equal(5, exact.Sorties.Count);
            Equal(0, exact.RepeatedMeasurements.Count);
            PlannedFieldSortie firstNamedTask =
                exact.Sorties.Single(x =>
                    x.Name.Equals(
                        "外部架次01",
                        StringComparison.OrdinalIgnoreCase));
            PlannedFieldSortie secondNamedTask =
                exact.Sorties.Single(x =>
                    x.Name.Equals(
                        "外部架次01_02",
                        StringComparison.OrdinalIgnoreCase));
            True(
                !firstNamedTask.SourceSha256.Equals(
                    secondNamedTask.SourceSha256,
                    StringComparison.OrdinalIgnoreCase),
                "different-SHA same-name KML files must remain separate formal tasks");
            True(
                secondNamedTask.MeasurementTimeUtc >
                firstNamedTask.MeasurementTimeUtc,
                "same-name formal task ordering should remain deterministic by source time");

            string emptyWorkarea =
                Path.Combine(
                    root,
                    "empty-field-progress");
            Directory.CreateDirectory(
                emptyWorkarea);
            FieldDailyAnalysisResult logicalProgress =
                fieldAnalysis.Analyze(
                    emptyWorkarea,
                    new DateTime(2026, 9, 11),
                    exact,
                    [],
                    expectedDailySorties: 2,
                    acceptedRelocations: 0);
            Equal(
                5,
                logicalProgress.Route.Sorties.Count);
            Equal(
                5,
                logicalProgress.RemainingSorties.Count);
            True(
                logicalProgress.RemainingSorties.Contains(
                    "外部架次01_02",
                    StringComparer.OrdinalIgnoreCase),
                "different-SHA same-name KML must remain an independent planned task");

            var secondTaskSamples =
                new List<UavMagCanonicalSample>();
            for (int i = 0; i <= 150; i++)
            {
                double t =
                    i / 150d;
                secondTaskSamples.Add(
                    new UavMagCanonicalSample(
                        new DateTimeOffset(
                            2026, 9, 11,
                            1, 0, 0,
                            TimeSpan.Zero)
                            .AddSeconds(i),
                        116.0030 +
                            (116.0130 - 116.0030) * t,
                        39.9000 +
                            (39.9100 - 39.9000) * t,
                        null,
                        null,
                        50000d,
                        null,
                        "A",
                        20,
                        "OK",
                        "right-group-flight.txt",
                        i + 1,
                        []));
            }

            var secondTaskData =
                new FieldMagFolderAnalysis
                {
                    SourceDirectory =
                        emptyWorkarea,
                    Fingerprint =
                        "right-group-track",
                    UavSamples =
                        secondTaskSamples
                };
            FieldDailyAnalysisResult secondTaskProgress =
                fieldAnalysis.Analyze(
                    emptyWorkarea,
                    new DateTime(2026, 9, 11),
                    exact,
                    [],
                    expectedDailySorties: 2,
                    acceptedRelocations: 0,
                    preparedData: secondTaskData);
            FieldSortieProgressEstimate firstTaskProgress =
                secondTaskProgress.SortieProgress.Single(x =>
                    x.SortieName.Equals(
                        "外部架次01",
                        StringComparison.OrdinalIgnoreCase));
            FieldSortieProgressEstimate secondNamedTaskProgress =
                secondTaskProgress.SortieProgress.Single(x =>
                    x.SortieName.Equals(
                        "外部架次01_02",
                        StringComparison.OrdinalIgnoreCase));
            True(
                !firstTaskProgress.CompletedToday,
                "track along the second formal task must not complete the first same-name task");
            True(
                secondNamedTaskProgress.CompletedToday,
                "track along the second formal task should complete its own planned task");

            var repeatedFlightSamples =
                new List<UavMagCanonicalSample>();
            DateTimeOffset pass1 =
                new(
                    2026, 9, 11,
                    2, 0, 0,
                    TimeSpan.Zero);
            DateTimeOffset pass2 =
                pass1.AddMinutes(30);

            for (int pass = 0; pass < 2; pass++)
            {
                DateTimeOffset passStart =
                    pass == 0
                        ? pass1
                        : pass2;
                string sourceName =
                    pass == 0
                        ? "flight-pass-1.txt"
                        : "flight-pass-2.txt";

                for (int i = 0; i <= 150; i++)
                {
                    double t =
                        i / 150d;
                    repeatedFlightSamples.Add(
                        new UavMagCanonicalSample(
                            passStart.AddSeconds(i),
                            116.0000 +
                                (116.0100 - 116.0000) * t,
                            39.9000 +
                                (39.9100 - 39.9000) * t,
                            null,
                            null,
                            50000d,
                            null,
                            "A",
                            20,
                            "OK",
                            sourceName,
                            i + 1,
                            []));
                }
            }

            var repeatedFlightData =
                new FieldMagFolderAnalysis
                {
                    SourceDirectory =
                        emptyWorkarea,
                    Fingerprint =
                        "two-time-separated-passes",
                    UavSamples =
                        repeatedFlightSamples
                };
            FieldDailyAnalysisResult repeatedFlightProgress =
                fieldAnalysis.Analyze(
                    emptyWorkarea,
                    new DateTime(2026, 9, 11),
                    exact,
                    [],
                    expectedDailySorties: 2,
                    acceptedRelocations: 0,
                    preparedData: repeatedFlightData);

            Equal(
                1,
                repeatedFlightProgress.CompletedToday.Count);
            True(
                repeatedFlightProgress.CompletedToday.Contains(
                    "外部架次01",
                    StringComparer.OrdinalIgnoreCase),
                "two passes of one task must still count as one completed planned task");
            Equal(
                1,
                repeatedFlightProgress.RepeatedSortiesToday.Count);
            Equal(
                "外部架次01",
                repeatedFlightProgress.RepeatedSortiesToday[0]);
            Equal(
                1,
                repeatedFlightProgress.RepeatedPassesToday.Count);
            True(
                repeatedFlightProgress.RepeatedPassesToday[0].StartUtc >=
                pass2,
                "later time-separated pass should be the repeated-flight evidence");
            True(
                repeatedFlightProgress.SortieProgress
                    .Single(x =>
                        x.SortieName.Equals(
                            "外部架次01",
                            StringComparison.OrdinalIgnoreCase))
                    .Evidence
                    .Contains(
                        "重复飞行",
                        StringComparison.Ordinal),
                "repeat-flight diagnostic missing from task evidence");

            FieldRouteSnapshot? managed = fieldAnalysis.TryLoadManagedRouteSnapshot(
                Path.Combine(root, "missing-route-project"),
                Path.Combine(root, "missing-route-export"),
                importRoot);
            True(managed is not null, "managed route discovery did not find imported batch");
            Equal("外部导入航线 KML", managed!.SourceLabel);
            Equal(5, managed.Sorties.Count);
            Equal(0, managed.RepeatedMeasurements.Count);
        });
    }

    private static void TestConfiguredFieldOutputsExcludedFromDiscovery()
    {
        WithTempRoot(root =>
        {
            string? previousEnvironment = Environment.GetEnvironmentVariable("MAGOPS_FOLDER_LAYOUT");
            try
            {
                string configPath = Path.Combine(root, "custom-layout.json");
                Environment.SetEnvironmentVariable("MAGOPS_FOLDER_LAYOUT", configPath);
                var layouts = new FolderLayoutService();
                FolderLayoutDocument layout = layouts.Load();
                layout.WorkareaFolders.Single(x => x.Key == "workarea.field").RelativePath = "05_外业_改名";
                layout.Operations.Single(x => x.Key == "field_daily_progress").Subfolder = "导出/{yyyyMMdd}/进度简报";
                File.WriteAllText(configPath, JsonSerializer.Serialize(layout));

                string workareaRoot = Path.Combine(root, "workarea");
                string rawDirectory = layouts.ResolveWorkareaFolderFromRoot(workareaRoot, "workarea.raw");
                string fieldDirectory = layouts.ResolveWorkareaFolderFromRoot(workareaRoot, "workarea.field");
                DateTime date = new(2026, 10, 22);
                string generatedDirectory = layouts.ResolveOperationDirectory(
                    root,
                    "field_daily_progress",
                    "workarea",
                    new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date)));
                Directory.CreateDirectory(rawDirectory);
                Directory.CreateDirectory(generatedDirectory);

                string rawFile = Path.Combine(rawDirectory, "M350_20261022_raw.csv");
                string generatedFile = Path.Combine(generatedDirectory, "M350_20261022_export.csv");
                const string content = "Timestamp,Longitude,Latitude,TMI\n" +
                                       "2026-10-22T08:00:00Z,116.0,39.9,50000\n" +
                                       "2026-10-22T08:00:01Z,116.001,39.901,50001\n";
                File.WriteAllText(rawFile, content);
                File.WriteAllText(generatedFile, content);
                string evidenceDirectory = layouts.ResolveOperationDirectory(root, "flight_evidence", "workarea");
                Directory.CreateDirectory(evidenceDirectory);
                string evidenceFile = Path.Combine(evidenceDirectory, "M350_20261022_flight.csv");
                File.WriteAllText(evidenceFile, content);

                var route = new FieldRouteSnapshot(
                    "synthetic-route",
                    "layout-regression",
                    "layout-route-fingerprint",
                    DateTime.Now,
                    [new PlannedFieldSortie(1, "A01", [
                        new GeoCoordinate(116.0, 39.9),
                        new GeoCoordinate(116.01, 39.91)
                    ])]);
                FieldDailyAnalysisResult analysis = new FieldOperationsAnalysisService().Analyze(
                    workareaRoot,
                    date,
                    route,
                    [],
                    expectedDailySorties: 1,
                    acceptedRelocations: 0);

                True(analysis.UavMag.Files.Contains(Path.GetFullPath(rawFile), StringComparer.OrdinalIgnoreCase),
                    "configured raw UAV source must remain discoverable");
                True(!analysis.UavMag.Files.Contains(Path.GetFullPath(generatedFile), StringComparer.OrdinalIgnoreCase),
                    "renamed MagOps output directory must not feed its export back into raw UAV discovery");
                True(analysis.UavMag.Files.Contains(Path.GetFullPath(evidenceFile), StringComparer.OrdinalIgnoreCase),
                    "managed flight evidence must remain eligible as raw observations");
                True(fieldDirectory.EndsWith("05_外业_改名", StringComparison.OrdinalIgnoreCase),
                    "test must exercise the renamed effective field root");
            }
            finally
            {
                Environment.SetEnvironmentVariable("MAGOPS_FOLDER_LAYOUT", previousEnvironment);
            }
        });
    }

    private static void TestFieldMagCanonicalMerge()
    {
        WithTempRoot(root =>
        {
            string folder = CreateFieldMagFixture(root);
            var service = new FieldMagDataService();
            FieldMagFolderAnalysis analysis = service.AnalyzeFolder(folder);

            Equal(1, analysis.UavRawFileCount);
            Equal(2, analysis.BaseRawFileCount);
            Equal(3, analysis.DerivedFileCount);
            Equal(3, analysis.UavSamples.Count);
            Equal(5, analysis.BaseSamples.Count);
            Equal(new DateTime(2026, 9, 8), analysis.UavSamples[0].TimestampUtc.UtcDateTime.Date);
            Equal(new DateTime(2026, 9, 9), analysis.UavSamples[^1].TimestampUtc.UtcDateTime.Date);
            Equal("F", analysis.UavSamples[^1].SensorStatus);
            True(
                analysis.Files.Any(x =>
                    Path.GetFileName(x.Path).Equals("after_allT.txt", StringComparison.OrdinalIgnoreCase) &&
                    x.SensorFamily == FieldMagSensorFamily.UavOpticalPump &&
                    x.DataRole == FieldMagDataRole.Aggregate),
                "UAV aggregate result was not classified separately");
            True(
                analysis.Files.Any(x =>
                    Path.GetFileName(x.Path).Equals("0909_rigaiAll.txt", StringComparison.OrdinalIgnoreCase) &&
                    x.SensorFamily == FieldMagSensorFamily.DiurnalCorrection &&
                    x.DataRole == FieldMagDataRole.DiurnalMerged &&
                    x.RecordCount == 2),
                "rigai merged result classification/count missing");
            True(
                !analysis.UavSamples.Any(x => Math.Abs((x.MagneticFieldNt ?? 0d) - 59099d) < 1e-9),
                "aggregate UAV data must not be mixed back in when Raw is present");
            True(
                analysis.Files.Any(x =>
                    Path.GetFileName(x.Path).Equals("affter_19T.txt", StringComparison.OrdinalIgnoreCase) &&
                    x.SensorFamily == FieldMagSensorFamily.Gsm19T &&
                    x.DataRole == FieldMagDataRole.Aggregate),
                "GSM-19T aggregate result was not classified separately");
            True(
                !analysis.BaseSamples.Any(x => Math.Abs(x.MagneticFieldNt - 59999d) < 1e-9),
                "aggregate Base data must not be mixed back in when Raw is present");
            True(analysis.QualityIssues.Any(x => x.Code == "UAV_SENSOR_F"), "UAV F-status QC missing");
            True(analysis.QualityIssues.Any(x => x.Code == "GEMLINK_LONG_GAP"), "Base >999 s QC missing");
            True(analysis.QualityIssues.Any(x => x.Code == "BASE_LOW_SQ"), "Base low-SQ QC missing");
            True(analysis.QualityIssues.Any(x => x.Code == "BASE_MAG_JUMP"), "Base magnetic-jump QC missing");
            True(analysis.QualityIssues.Any(x => x.Code == "CROSS_UTC_DATE"), "cross-UTC-date marker missing");
            Equal(2, analysis.GemLinkJobs.Count);
        });
    }

    private static void TestFieldMagIncrementalScanCache()
    {
        WithTempRoot(root =>
        {
            string folder =
                CreateFieldMagFixture(root);
            string cache =
                Path.Combine(
                    root,
                    "field-cache");

            var service =
                new FieldMagDataService();

            FieldMagIncrementalAnalysisResult first =
                service.AnalyzeFolderIncremental(
                    folder,
                    cache);
            Equal(6, first.Statistics.DiscoveredFiles);
            Equal(0, first.Statistics.ReusedFiles);
            Equal(6, first.Statistics.AddedFiles);
            Equal(6, first.Statistics.ParsedFiles);
            Equal(3, first.Analysis.UavSamples.Count);
            Equal(5, first.Analysis.BaseSamples.Count);
            foreach (var scan in first.Analysis.Files)
                Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(scan.Path))),
                    scan.SourceContentSha256);
            True(
                File.Exists(
                    Path.Combine(
                        cache,
                        "index.json")),
                "incremental cache index missing");
            True(
                File.Exists(
                    Path.Combine(
                        cache,
                        "summary.json")),
                "incremental cache summary missing");
            True(
                File.Exists(
                    Path.Combine(
                        cache,
                        "analysis.json.gz")),
                "incremental analysis snapshot missing");

            True(
                FieldMagDataService.TryRestoreIncrementalAnalysis(
                    folder,
                    cache,
                    out FieldMagCachedAnalysisRestoreResult restored),
                "incremental analysis snapshot not restorable");
            Equal(
                first.Analysis.Fingerprint,
                restored.Analysis.Fingerprint);
            Equal(
                first.Analysis.UavSamples.Count,
                restored.Analysis.UavSamples.Count);
            Equal(
                first.Analysis.BaseSamples.Count,
                restored.Analysis.BaseSamples.Count);
            Equal(first.Analysis.GsmSessions.Count, restored.Analysis.GsmSessions.Count);
            True(first.Analysis.Files.Select(x => x.SourceContentSha256)
                .SequenceEqual(restored.Analysis.Files.Select(x => x.SourceContentSha256)),
                "analysis snapshot lost exact source content hashes");
            True(first.Analysis.GsmSessions.Select(x => x.SessionStableId)
                    .SequenceEqual(restored.Analysis.GsmSessions.Select(x => x.SessionStableId)),
                "incremental analysis snapshot lost GSM session identity");
            Equal(
                first.SourceSnapshotKey,
                restored.SourceSnapshotKey);

            FieldMagIncrementalAnalysisResult second =
                service.AnalyzeFolderIncremental(
                    folder,
                    cache);
            Equal(6, second.Statistics.DiscoveredFiles);
            Equal(6, second.Statistics.ReusedFiles);
            Equal(0, second.Statistics.ParsedFiles);
            True(first.Analysis.Files.Select(x => x.SourceContentSha256)
                .SequenceEqual(second.Analysis.Files.Select(x => x.SourceContentSha256)),
                "parsed-file cache lost exact source content hashes");
            Equal(
                first.Analysis.Fingerprint,
                second.Analysis.Fingerprint);
            True(first.Analysis.GsmSessions.Select(x => x.SessionStableId)
                    .SequenceEqual(second.Analysis.GsmSessions.Select(x => x.SessionStableId)),
                "parsed-file cache reuse changed GSM session identity");

            string changedPath =
                Path.Combine(
                    folder,
                    "uav_raw_01.txt");
            File.AppendAllText(
                changedPath,
                "\n",
                new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(
                changedPath,
                DateTime.UtcNow.AddSeconds(2));

            FieldMagIncrementalAnalysisResult changed =
                service.AnalyzeFolderIncremental(
                    folder,
                    cache);
            Equal(5, changed.Statistics.ReusedFiles);
            Equal(1, changed.Statistics.ChangedFiles);
            Equal(1, changed.Statistics.ParsedFiles);
            Equal(3, changed.Analysis.UavSamples.Count);
            True(first.Analysis.Files.Single(x => x.Path == changedPath).SourceContentSha256 !=
                changed.Analysis.Files.Single(x => x.Path == changedPath).SourceContentSha256,
                "content hash must change even when added newline does not change observations");
            True(first.Analysis.Fingerprint != changed.Analysis.Fingerprint,
                "analysis fingerprint must include exact parsed source versions");
            Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(changedPath))),
                changed.Analysis.Files.Single(x => x.Path == changedPath).SourceContentSha256);

            string addedPath =
                Path.Combine(
                    folder,
                    "notes.log");
            File.WriteAllText(
                addedPath,
                "field note without magnetometer records",
                new UTF8Encoding(false));

            FieldMagIncrementalAnalysisResult added =
                service.AnalyzeFolderIncremental(
                    folder,
                    cache);
            Equal(7, added.Statistics.DiscoveredFiles);
            Equal(6, added.Statistics.ReusedFiles);
            Equal(1, added.Statistics.AddedFiles);
            Equal(1, added.Statistics.ParsedFiles);

            File.Delete(addedPath);
            FieldMagIncrementalAnalysisResult removed =
                service.AnalyzeFolderIncremental(
                    folder,
                    cache);
            Equal(6, removed.Statistics.DiscoveredFiles);
            Equal(6, removed.Statistics.ReusedFiles);
            Equal(1, removed.Statistics.RemovedFiles);
            Equal(0, removed.Statistics.ParsedFiles);

            True(
                FieldMagDataService.TryReadIncrementalCacheSummary(
                    cache,
                    out FieldMagIncrementalCacheSummary summary),
                "incremental cache summary not readable");
            Equal(6, summary.IndexedFileCount);
            Equal(3, summary.UavRecordCount);
            Equal(5, summary.BaseRecordCount);

            FieldMagIncrementalAnalysisResult full =
                service.AnalyzeFolderIncremental(
                    folder,
                    cache,
                    forceFullScan: true);
            True(
                full.Statistics.ForceFullScan,
                "force-full-scan marker missing");
            Equal(0, full.Statistics.ReusedFiles);
            Equal(6, full.Statistics.ChangedFiles);
            Equal(6, full.Statistics.ParsedFiles);
            Equal(
                removed.Analysis.Fingerprint,
                full.Analysis.Fingerprint);
        });
    }

    private static void TestFieldDataImportCatalog()
    {
        WithTempRoot(root =>
        {
            var store = new JsonStore();
            var workspace = new WorkspaceService(store);
            workspace.Initialize(root, "Import Catalog Test");

            string sourceDirectory =
                Path.Combine(root, "_source");
            Directory.CreateDirectory(sourceDirectory);
            string source =
                Path.Combine(
                    sourceDirectory,
                    "uav_new.txt");
            File.WriteAllText(
                source,
                "sample field data",
                new UTF8Encoding(false));

            string destination =
                Path.Combine(
                    root,
                    "10_工区",
                    "A",
                    "05_外业记录与采集成果");
            var importer =
                new FieldDataImportService(store);
            FieldDataImportBatchResult first =
                importer.ImportFiles(
                    root,
                    "10_工区/A",
                    destination,
                    [source]);
            Equal(1, first.ImportedCount);
            Equal(0, first.DuplicateCount);
            True(
                File.Exists(
                    first.Items[0].DestinationPath),
                "imported raw field file missing");

            FieldDataImportBatchResult second =
                importer.ImportFiles(
                    root,
                    "10_工区/A",
                    destination,
                    [source]);
            Equal(0, second.ImportedCount);
            Equal(1, second.DuplicateCount);

            List<FieldDataCatalogRecord> catalog =
                importer.List(root);
            Equal(1, catalog.Count);
            True(
                catalog[0].RelativePath.StartsWith(
                    "project:",
                    StringComparison.OrdinalIgnoreCase),
                "project field import should use project-relative catalog path");
            Equal(
                first.Items[0].Sha256,
                catalog[0].Sha256);

            File.Delete(first.Items[0].DestinationPath);
            importer.EnrichFromAnalysis(root, new FieldMagFolderAnalysis());
            Equal("MISSING_FILE", importer.List(root)[0].Status);
            FieldDataImportBatchResult restored = importer.ImportFiles(
                root, "10_工区/A", destination, [source]);
            Equal(1, restored.ImportedCount);
            Equal(0, restored.DuplicateCount);
            True(File.Exists(restored.Items[0].DestinationPath), "deleted catalog copy must be reimportable");
            Equal(2, importer.List(root).Count);
            Equal(1, importer.ImportFiles(root, "10_工区/A", destination, [source]).DuplicateCount);


            File.Delete(restored.Items[0].DestinationPath);
            FieldDataImportBatchResult withoutScan = importer.ImportFiles(
                root, "10_工区/A", destination, [source]);
            Equal(1, withoutScan.ImportedCount);
            Equal(0, withoutScan.DuplicateCount);
            Equal(2, importer.List(root).Count(x => x.Status == "MISSING_FILE"));
            True(File.Exists(source), "catalog reconciliation must preserve the source file");

            File.Copy(source, first.Items[0].DestinationPath);
            importer.EnrichFromAnalysis(root, new FieldMagFolderAnalysis());
            Equal("IMPORTED", importer.List(root)[0].Status);
            Equal(1, importer.ImportFiles(root, "10_工区/A", destination, [source]).DuplicateCount);
            File.WriteAllText(first.Items[0].DestinationPath, "altered field data");
            File.WriteAllText(withoutScan.Items[0].DestinationPath, "altered field data");
            var integrity = importer.EnrichFromAnalysis(root, new FieldMagFolderAnalysis());
            Equal(2, integrity.Issues.Count(i => i.Status == "FILE_CHANGED"));
            True(integrity.RequiresFullReparse, "changed imported content must invalidate a potentially reused scan cache");
            True(importer.EnrichFromAnalysis(root, new FieldMagFolderAnalysis()).RequiresFullReparse,
                "an interrupted full reparse must remain pending on the next integrity scan");
            True(!importer.EnrichFromAnalysis(root, new FieldMagFolderAnalysis(), fullyParsed: true).RequiresFullReparse,
                "only a completed fresh parse can acknowledge changed input");
            True(!importer.EnrichFromAnalysis(root, new FieldMagFolderAnalysis()).RequiresFullReparse,
                "unchanged content must reuse the acknowledged fresh parse");
            var reimportChanged = importer.ImportFiles(root, "10_工区/A", destination, [source]);
            Equal(1, reimportChanged.ImportedCount);
            Equal(0, reimportChanged.DuplicateCount);
            Equal(first.Items[0].Sha256, importer.List(root)[0].Sha256);
            True(File.ReadAllText(source) == "sample field data", "integrity check must preserve original source bytes");
        });
    }

    private static void TestFieldDataRecursiveFolderDiscovery()
    {
        WithTempRoot(root =>
        {
            string sourceDirectory =
                Path.Combine(root, "_recursive_source");
            string childDirectory =
                Path.Combine(sourceDirectory, "flight_A");
            string nestedDirectory =
                Path.Combine(childDirectory, "nested");
            Directory.CreateDirectory(nestedDirectory);

            string rootFile =
                Path.Combine(sourceDirectory, "root.txt");
            string childFile =
                Path.Combine(childDirectory, "uav.csv");
            string nestedFile =
                Path.Combine(nestedDirectory, "raw.bin");
            File.WriteAllText(rootFile, "root", new UTF8Encoding(false));
            File.WriteAllText(childFile, "child", new UTF8Encoding(false));
            File.WriteAllText(nestedFile, "nested", new UTF8Encoding(false));

            var importer =
                new FieldDataImportService(new JsonStore());
            IReadOnlyList<string> topOnly =
                importer.DiscoverSourceFiles(
                    sourceDirectory,
                    recursive: false);
            IReadOnlyList<string> recursive =
                importer.DiscoverSourceFiles(
                    sourceDirectory,
                    recursive: true);

            Equal(1, topOnly.Count);
            Equal(Path.GetFullPath(rootFile), topOnly[0]);
            Equal(3, recursive.Count);
            True(
                recursive.Contains(
                    Path.GetFullPath(childFile),
                    StringComparer.OrdinalIgnoreCase),
                "recursive field import did not discover first-level child file");
            True(
                recursive.Contains(
                    Path.GetFullPath(nestedFile),
                    StringComparer.OrdinalIgnoreCase),
                "recursive field import did not discover nested file with arbitrary extension");
        });
    }

    private static void TestSurveyLineUniqueTrackMatching()
    {
        var lines =
            new List<PlannedSurveyLineTask>
            {
                new(
                    1,
                    1,
                    "SL-A",
                    "架次01",
                    "L001",
                    new GeoCoordinate(108.000000, 47.000000),
                    new GeoCoordinate(108.000000, 47.009000),
                    1000d) { SurveyTaskStableId = "task-SL-A" },
                new(
                    2,
                    1,
                    "SL-B",
                    "架次01",
                    "L002",
                    new GeoCoordinate(108.000265, 47.000000),
                    new GeoCoordinate(108.000265, 47.009000),
                    1000d) { SurveyTaskStableId = "task-SL-B" }
            };

        var track =
            new List<SurveyTrackPoint>();
        DateTimeOffset start =
            new(
                2026,
                9,
                10,
                1,
                0,
                0,
                TimeSpan.Zero);
        for (int i = 0; i <= 100; i++)
        {
            double t = i / 100d;
            track.Add(
                new SurveyTrackPoint(
                    start.AddSeconds(i),
                    new GeoCoordinate(
                        108.000000,
                        47.000000 +
                        0.009000 * t),
                    "uav.txt",
                    i + 1));
        }

        var service =
            new SurveyLineProgressService();
        IReadOnlyList<FieldSurveyLineProgressEstimate> result =
            service.Analyze(
                lines,
                track,
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase),
                [new MissionTimeWindow(
                    "mission-unique-track",
                    "架次01",
                    start,
                    start.AddSeconds(100),
                    "flight-unique-track")
                {
                    SurveyTaskStableIds = ["task-SL-A", "task-SL-B"]
                }]);

        FieldSurveyLineProgressEstimate a =
            result.Single(x => x.TaskId == "SL-A");
        FieldSurveyLineProgressEstimate b =
            result.Single(x => x.TaskId == "SL-B");

        True(
            a.CompletedToday,
            "true survey line should be completed");
        True(
            a.CoverageRatio >= 0.90d,
            "true survey line coverage too low");
        True(
            !b.CompletedToday,
            "adjacent 20 m survey line must not be completed by line A track");
        True(
            b.CoverageRatio < 0.20d,
            "adjacent survey line received too much foreign-track coverage");
        True(
            a.MatchToleranceM < 10d,
            "20 m line spacing should dynamically constrain cross-track tolerance");

        // Regression: field progress downsamples high-rate QuSpin rows to one
        // track point per second. The key must include SourceFile; otherwise two
        // overlapping UAV files silently collapse into one and genuine flown
        // survey lines disappear from progress.
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            "magops_multisource_progress_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var overlappingSamples = new List<UavMagCanonicalSample>();
            for (int i = 0; i <= 100; i++)
            {
                double t = i / 100d;
                DateTimeOffset timestamp = start.AddSeconds(i);
                overlappingSamples.Add(new UavMagCanonicalSample(
                    timestamp,
                    108.000000,
                    47.000000 + 0.009000 * t,
                    1200d,
                    null,
                    59000d,
                    50d,
                    "3D",
                    18,
                    "T",
                    "flight-a.txt",
                    i + 1,
                    []));
                overlappingSamples.Add(new UavMagCanonicalSample(
                    timestamp,
                    108.000265,
                    47.000000 + 0.009000 * t,
                    1200d,
                    null,
                    59010d,
                    50d,
                    "3D",
                    18,
                    "T",
                    "flight-b.txt",
                    i + 1,
                    []));
            }

            var prepared = new FieldMagFolderAnalysis
            {
                SourceDirectory = tempRoot,
                Fingerprint = "MULTI_SOURCE_PROGRESS",
                UavSamples = overlappingSamples
            };
            var route = new FieldRouteSnapshot(
                "",
                "smoke",
                "route-fp",
                DateTime.Now,
                [
                    new PlannedFieldSortie(
                        1,
                        "架次01",
                        [
                            new GeoCoordinate(108.000000, 47.000000),
                            new GeoCoordinate(108.000000, 47.009000),
                            new GeoCoordinate(108.000265, 47.009000),
                            new GeoCoordinate(108.000265, 47.000000)
                        ])
                ])
            {
                SurveyLines = lines
            };

            var buildTrack = typeof(FieldOperationsAnalysisService).GetMethod(
                "BuildSurveyTrackPoints",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                ?? throw new InvalidOperationException("field-progress track builder missing");
            var overlapTrack = (IReadOnlyList<SurveyTrackPoint>)(buildTrack.Invoke(
                null,
                [prepared, FieldMagDataService.ToUavSourceSummary(prepared)])
                ?? throw new InvalidOperationException("field-progress track builder returned null"));
            Equal(202, overlapTrack.Count);
            Equal(101, overlapTrack.Count(x =>
                x.SourceFile.Equals("flight-a.txt", StringComparison.OrdinalIgnoreCase)));
            Equal(101, overlapTrack.Count(x =>
                x.SourceFile.Equals("flight-b.txt", StringComparison.OrdinalIgnoreCase)));
            True(overlapTrack
                    .GroupBy(x => x.TimestampUtc)
                    .All(group => group.Select(x => x.SourceFile)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2),
                "field-progress downsampling collapsed overlapping UAV source files");
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, true);
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }

    private static void TestSurveyLineIdentityRecoveryFromRoutePoints()
    {
        static RouteEndpoint Point(
            string line,
            double lon,
            double lat,
            int endpoint) =>
            new()
            {
                Line = line,
                Endpoint = endpoint,
                X = lon,
                Y = lat,
                Label = $"{line}-{endpoint}",
                Pid = $"{line}-{endpoint}"
            };

        var sortie =
            new RouteSortie
            {
                Name = "架次01",
                SourceLines = [],
                Points =
                [
                    Point("L001", 108.000000, 47.000000, 0),
                    Point("L001", 108.000000, 47.010000, 1),
                    Point("L002", 108.000300, 47.010000, 0),
                    Point("L002", 108.000300, 47.000000, 1)
                ]
            };
        var plan =
            new RoutePlanResult
            {
                SourcePath = "",
                Mode = "geographic",
                CentralMeridian = 0d,
                Sorties = [sortie]
            };

        var service =
            new FieldOperationsAnalysisService();
        FieldRouteSnapshot first =
            service.FromRoutePlan(plan);
        Equal(2, first.SurveyLines.Count);
        True(
            first.HasSurveyLineIdentity,
            "Point.Line fallback did not recover survey-line identity");

        string[] firstIds =
            first.SurveyLines
                .OrderBy(x => x.LineName)
                .Select(x => x.TaskId)
                .ToArray();

        sortie.Points.Reverse();
        FieldRouteSnapshot reversed =
            service.FromRoutePlan(plan);
        Equal(2, reversed.SurveyLines.Count);

        string[] reversedIds =
            reversed.SurveyLines
                .OrderBy(x => x.LineName)
                .Select(x => x.TaskId)
                .ToArray();

        Equal(firstIds[0], reversedIds[0]);
        Equal(firstIds[1], reversedIds[1]);
    }

    private static void TestSurveyLineIdentityRecoveryFromFieldProgressGeometry()
    {
        const double lon0 = 108.0000000d;
        const double lat0 = 47.0000000d;
        double lonPerM = 1d / (111320d * Math.Cos(lat0 * Math.PI / 180d));
        double latPerM = 1d / 111320d;

        GeoCoordinate P(double eastM, double northM) =>
            new(lon0 + eastM * lonPerM, lat0 + northM * latPerM);

        var straight = new PlannedFieldSortie(
            1,
            "架次01",
            [P(0, 0), P(250, 0), P(500, 0), P(750, 0), P(1000, 0)])
        {
            LogicalName = "L001"
        };

        var snake = new PlannedFieldSortie(
            2,
            "架次02",
            [
                P(0, 20),
                P(1000, 20),
                P(1000, 40),
                P(0, 40),
                P(0, 60),
                P(1000, 60)
            ]);

        var route = new FieldRouteSnapshot(
            "external-kml-batch",
            "外部导入航线 KML",
            "progress-route-fingerprint",
            DateTime.Now,
            [straight, snake]);

        SurveyLineIdentityRecoveryResult recovered =
            SurveyLineIdentityRecoveryService.Recover(route);

        True(recovered.Recovered, "field-progress route geometry was not recovered");
        Equal("GEOMETRY_RECOVERED", recovered.Mode);
        Equal(4, recovered.Route.SurveyLines.Count);
        True(recovered.Route.HasSurveyLineIdentity, "recovered route must expose survey-line identity");
        True(
            recovered.Route.SourceLabel.Contains("任务进度几何恢复测线", StringComparison.Ordinal),
            "recovered route source label must disclose compatibility inference");
        True(
            recovered.Route.SurveyLines.Any(x => x.LineName.Equals("架次01", StringComparison.OrdinalIgnoreCase)),
            "straight progress task should retain its visible task name");
        Equal(
            3,
            recovered.Route.SurveyLines.Count(x =>
                x.SortieName.Equals("架次02", StringComparison.OrdinalIgnoreCase)));

        var ambiguous = new PlannedFieldSortie(
            1,
            "弯折任务",
            [P(0, 0), P(100, 0), P(100, 100), P(0, 100), P(0, 0)]);
        var ambiguousRoute = new FieldRouteSnapshot(
            "ambiguous",
            "任务进度",
            "ambiguous-fingerprint",
            DateTime.Now,
            [ambiguous]);
        SurveyLineIdentityRecoveryResult rejected =
            SurveyLineIdentityRecoveryService.Recover(ambiguousRoute);
        True(!rejected.Recovered, "ambiguous route must not be guessed as production survey lines");

        // Regression: a long shallow/oblique connector is still transit. The old
        // fixed 18-degree axis gate treated the connector as survey-line geometry,
        // merged both parallel legs into one bent run and rejected the whole
        // sortie. This synthetic geometry mirrors the uploaded 架次35 case:
        // two long E-W survey legs separated by 100 m, joined by a ~1.9-degree
        // connector spanning several kilometres.
        var obliqueConnector = new PlannedFieldSortie(
            1,
            "架次35_斜接",
            [
                P(0, 200),
                P(6800, 200),
                P(3800, 300),
                P(0, 300)
            ]);
        var obliqueRoute = new FieldRouteSnapshot(
            "oblique-connector",
            "外部导入航线 KML",
            "oblique-connector-fingerprint",
            DateTime.Now,
            [obliqueConnector]);
        SurveyLineIdentityRecoveryResult obliqueRecovered =
            SurveyLineIdentityRecoveryService.Recover(obliqueRoute);

        True(
            obliqueRecovered.Recovered,
            "shallow oblique connector must not cause the whole sortie to lose survey-line identity");
        Equal(
            2,
            obliqueRecovered.Route.SurveyLines.Count);
        True(
            obliqueRecovered.Route.SurveyLines.All(x => x.LengthM > 3000d),
            "oblique-connector recovery must retain the two long parallel survey legs, not the connector");
    }

    private static void TestDjiPilot2V14SummaryParser()
    {
        WithTempRoot(root =>
        {
            string file =
                Path.Combine(
                    root,
                    "DJIFlightRecord_2026-09-10_[01-02-03].txt");
            File.WriteAllBytes(
                file,
                BuildSyntheticDjiV14(
                    new DateTimeOffset(
                        2026, 9, 10,
                        0, 29, 49, 440,
                        TimeSpan.Zero),
                    durationMs: 1_887_300,
                    distanceKm: 6.294f));

            var service =
                new DjiPilotFlightRecordService(
                    new JsonStore());
            DjiFlightRecordSummary summary =
                service.ParseSummary(file);

            Equal(14, summary.FormatVersion);
            Equal("Matrice 400", summary.AircraftName);
            Equal("17.2.5", summary.AppVersion);
            Equal(
                "2026-09-10 01:02:03",
                summary.FilenameTimeText);
            True(
                Math.Abs(
                    summary.DurationSeconds -
                    1887.3d) < 0.001d,
                "DJI v14 duration parse mismatch");
            True(
                Math.Abs(
                    summary.TotalDistanceKm -
                    6.294d) < 0.001d,
                "DJI v14 distance parse mismatch");
            Equal(
                new DateTimeOffset(
                    2026, 9, 10,
                    0, 29, 49, 440,
                    TimeSpan.Zero),
                summary.StartUtc);
        });
    }

    private static void TestDjiPilot2ImportAndMatch()
    {
        WithTempRoot(root =>
        {
            var store = new JsonStore();
            var workspace =
                new WorkspaceService(store);
            workspace.Initialize(
                root,
                "DJI Session Test");

            string sourceDir =
                Path.Combine(
                    root,
                    "_dji_source");
            Directory.CreateDirectory(
                sourceDir);
            string source =
                Path.Combine(
                    sourceDir,
                    "DJIFlightRecord_2026-09-10_[01-02-03].txt");
            DateTimeOffset start =
                new(
                    2026, 9, 10,
                    0, 29, 49, 440,
                    TimeSpan.Zero);
            File.WriteAllBytes(
                source,
                BuildSyntheticDjiV14(
                    start,
                    durationMs: 1_800_000,
                    distanceKm: 6.25f));

            string destination =
                Path.Combine(
                    root,
                    "10_工区",
                    "A",
                    "05_外业记录与采集成果");

            var service =
                new DjiPilotFlightRecordService(
                    store);
            DjiFlightImportBatchResult first =
                service.ImportFiles(
                    root,
                    "10_工区/A",
                    destination,
                    [source]);
            Equal(1, first.ImportedCount);
            Equal(0, first.DuplicateCount);

            DjiFlightImportBatchResult second =
                service.ImportFiles(
                    root,
                    "10_工区/A",
                    destination,
                    [source]);
            Equal(0, second.ImportedCount);
            Equal(1, second.DuplicateCount);

            List<DjiFlightSessionRecord> sessions =
                service.ListSessions(root);
            Equal(1, sessions.Count);
            Equal("2026-09-10", sessions[0].BeijingDate);
            Equal(
                DjiPilotFlightRecordService.ParserVersion,
                sessions[0].ParserVersion);

            var samples =
                new List<UavMagCanonicalSample>();
            for (int i = 0; i < 12; i++)
            {
                samples.Add(
                    new UavMagCanonicalSample(
                        start.AddSeconds(30 + i),
                        108d,
                        47d,
                        null,
                        null,
                        50000d,
                        null,
                        "A",
                        20,
                        "OK",
                        "uav.txt",
                        i + 1,
                        []));
            }

            var mag =
                new FieldMagFolderAnalysis
                {
                    SourceDirectory =
                        destination,
                    Fingerprint =
                        "dji-match",
                    UavSamples =
                        samples
                };

            DjiDailyFlightSummary daily =
                service.BuildDailySummary(
                    root,
                    "10_工区/A",
                    new DateTime(
                        2026, 9, 10),
                    mag);
            Equal(1, daily.FlightCount);
            Equal(1, daily.MagneticMatchedFlightCount);
            True(
                Math.Abs(
                    daily.TotalDistanceKm -
                    6.25d) < 0.001d,
                "DJI daily distance mismatch");
            True(
                daily.BeijingTimeRange.Length > 0,
                "DJI Beijing time range missing");

            var reportRow =
                new FieldDailyProgressRecord
                {
                    Date = "2026-09-10",
                    ProgressTimeZone =
                        FieldMagDataService.BeijingProgressTimeZone,
                    PlannedSortieCount = 10,
                    TodayCompletedSorties = ["S01"],
                    CompletedSorties = ["S01"],
                    RemainingSorties = ["S02"],
                    DjiFlightCount = daily.FlightCount,
                    DjiFlightDistanceKm = daily.TotalDistanceKm,
                    DjiFlightDurationSeconds = daily.TotalDurationSeconds,
                    DjiMagMatchedFlightCount =
                        daily.MagneticMatchedFlightCount,
                    DjiFlightTimeRangeBeijing =
                        daily.BeijingTimeRange,
                    ProgressSummary = "测试",
                    NextActionSummary = "测试"
                };
            string reportPreview =
                FieldworkDocumentExportService.BuildDailyProgressPreview(
                    reportRow,
                    expectedDailySorties: 2);
            True(
                reportPreview.Contains(
                    "DJI 实际飞行 1 次｜飞行距离 6.25 km"),
                "DJI daily leadership preview missing flight reconciliation");

            FieldMagFolderAnalysis magneticScan =
                new FieldMagDataService().AnalyzeFolder(
                    destination);
            True(
                magneticScan.Files.All(x =>
                    !Path.GetFileName(x.Path).StartsWith(
                        "DJIFlightRecord_",
                        StringComparison.OrdinalIgnoreCase)),
                "DJI binary FlightRecord leaked into magnetic text discovery");

            string fakeMcdat =
                Path.Combine(
                    sourceDir,
                    "2026-09-10_09-09-15_FLY021.DAT");
            File.WriteAllBytes(
                fakeMcdat,
                Encoding.ASCII.GetBytes(
                    "DJI_LOG_V3 test"));
            DjiMcdatRegistrationResult mcdat =
                service.RegisterMcdatFiles(
                    root,
                    "10_工区/A",
                    [fakeMcdat]);
            Equal(1, mcdat.Registered.Count);
            True(
                File.Exists(fakeMcdat),
                "MCDat registration must not move/delete source file");
        });
    }

    private static byte[] BuildSyntheticDjiV14(
        DateTimeOffset startUtc,
        int durationMs,
        float distanceKm)
    {
        byte[] info =
            new byte[436];
        WriteDjiFixedString(
            info,
            0,
            20,
            "Map Loading");
        WriteDjiFixedString(
            info,
            40,
            20,
            "Map Loading");

        BinaryPrimitives.WriteInt32LittleEndian(
            info.AsSpan(83, 4),
            100);
        BinaryPrimitives.WriteInt32LittleEndian(
            info.AsSpan(87, 4),
            12345);
        BinaryPrimitives.WriteInt64LittleEndian(
            info.AsSpan(91, 8),
            startUtc.ToUnixTimeMilliseconds());
        WriteDjiDouble(
            info.AsSpan(99, 8),
            108.1d);
        WriteDjiDouble(
            info.AsSpan(107, 8),
            47.2d);
        WriteDjiSingle(
            info.AsSpan(115, 4),
            distanceKm);
        BinaryPrimitives.WriteInt32LittleEndian(
            info.AsSpan(119, 4),
            durationMs);
        WriteDjiSingle(
            info.AsSpan(123, 4),
            120f);
        WriteDjiSingle(
            info.AsSpan(127, 4),
            24f);
        WriteDjiSingle(
            info.AsSpan(131, 4),
            8.1f);

        info[271] = 181;
        WriteDjiFixedString(
            info,
            280,
            32,
            "Matrice 400");
        WriteDjiFixedString(
            info,
            312,
            16,
            "TESTAIRCRAFT");
        WriteDjiFixedString(
            info,
            344,
            16,
            "TESTRC");
        WriteDjiFixedString(
            info,
            360,
            16,
            "TESTBATTERY");
        info[376] = 2;
        info[377] = 17;
        info[378] = 2;
        info[379] = 5;

        byte[] decoded =
            new byte[
                1 +
                2 +
                info.Length +
                2];
        decoded[0] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(
            decoded.AsSpan(1, 2),
            (ushort)info.Length);
        info.CopyTo(
            decoded,
            3);

        ushort auxSize =
            (ushort)(
                decoded.Length +
                1);
        int recordsOffset =
            100 +
            3 +
            auxSize;
        byte[] result =
            new byte[recordsOffset];

        BinaryPrimitives.WriteUInt64LittleEndian(
            result.AsSpan(0, 8),
            (ulong)recordsOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(
            result.AsSpan(8, 2),
            (ushort)info.Length);
        result[10] = 14;

        result[100] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(
            result.AsSpan(101, 2),
            auxSize);

        byte firstByte = 0x9b;
        result[103] = firstByte;
        ulong magic =
            unchecked(
                0x123456789ABCDEF0UL *
                firstByte);
        Span<byte> magicBytes =
            stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(
            magicBytes,
            magic);
        ulong keyValue =
            DjiTestCrc64Jones(
                firstByte,
                magicBytes);
        Span<byte> key =
            stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(
            key,
            keyValue);

        for (int i = 0; i < decoded.Length; i++)
        {
            result[104 + i] =
                (byte)(
                    decoded[i] ^
                    key[i % 8]);
        }

        return result;
    }

    private static ulong DjiTestCrc64Jones(
        ulong crc,
        ReadOnlySpan<byte> data)
    {
        const ulong poly =
            0x95AC9329AC4BC9B5UL;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0;
                 bit < 8;
                 bit++)
            {
                crc =
                    (crc >> 1) ^
                    ((crc & 1UL) != 0
                        ? poly
                        : 0UL);
            }
        }
        return crc;
    }

    private static void WriteDjiFixedString(
        byte[] target,
        int offset,
        int length,
        string value)
    {
        byte[] bytes =
            Encoding.UTF8.GetBytes(value);
        Array.Copy(
            bytes,
            0,
            target,
            offset,
            Math.Min(
                length,
                bytes.Length));
    }

    private static void WriteDjiSingle(
        Span<byte> target,
        float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(
            target,
            BitConverter.SingleToInt32Bits(value));

    private static void WriteDjiDouble(
        Span<byte> target,
        double value) =>
        BinaryPrimitives.WriteInt64LittleEndian(
            target,
            BitConverter.DoubleToInt64Bits(value));

    private static void TestFieldMagProjectDatePartitioning()
    {
        WithTempRoot(root =>
        {
            string folder = CreateFieldMagFixture(root);
            var service = new FieldMagDataService();
            FieldMagFolderAnalysis analysis = service.AnalyzeFolder(folder);

            IReadOnlyList<FieldMagDateSummary> dates =
                FieldMagDataService.BuildDateSummaries(analysis);
            Equal(2, dates.Count);

            FieldMagDateSummary d0908 = dates.Single(x =>
                x.UtcDate == new DateTime(2026, 9, 8));
            Equal(1, d0908.UavRecordCount);
            Equal(1, d0908.BaseRecordCount);
            True(d0908.HasFlightData, "09/08 must be recognized as a UAV flight date");

            FieldMagDateSummary d0909 = dates.Single(x =>
                x.UtcDate == new DateTime(2026, 9, 9));
            Equal(2, d0909.UavRecordCount);
            Equal(4, d0909.BaseRecordCount);

            FieldMagFolderAnalysis slice =
                FieldMagDataService.SliceByUtcDate(
                    analysis,
                    new DateTime(2026, 9, 9));
            Equal(2, slice.UavSamples.Count);
            Equal(4, slice.BaseSamples.Count);
            True(
                slice.UavSamples.All(x =>
                    x.TimestampUtc.UtcDateTime.Date ==
                    new DateTime(2026, 9, 9)),
                "UAV date slice leaked another UTC date");
            True(
                slice.BaseSamples.All(x =>
                    x.TimestampUtc.UtcDateTime.Date ==
                    new DateTime(2026, 9, 9)),
                "Base date slice leaked another UTC date");

            string output = Path.Combine(root, "project_canonical");
            FieldMagExportResult exported =
                service.WriteIntegratedCanonicalOutputs(
                    output,
                    analysis);

            string uavIntegrated =
                Path.Combine(
                    output,
                    "UAV磁测_整合.csv.gz");
            string baseIntegrated =
                Path.Combine(
                    output,
                    "GSM19T_日变站_整合.csv.gz");

            True(
                File.Exists(uavIntegrated),
                "full-project UAV integrated output missing");
            True(
                File.Exists(baseIntegrated),
                "full-project Base integrated output missing");
            True(
                !Directory.Exists(
                    Path.Combine(
                        output,
                        "20260908")),
                "production integrated data must not be physically split by UTC date");
            Equal(
                4,
                ReadGzipLines(
                    uavIntegrated).Length);
            Equal(
                6,
                ReadGzipLines(
                    baseIntegrated).Length);

            string manifest =
                File.ReadAllText(
                    exported.ManifestPath);
            True(
                manifest.Contains(
                    "magops-field-mag-integrated-6",
                    StringComparison.Ordinal),
                "integrated field-data manifest schema missing");
            True(
                manifest.Contains(
                    "\"physical_partitioning\": \"none\"",
                    StringComparison.Ordinal),
                "integrated field data must state no physical date partitioning");
            True(
                manifest.Contains(
                    "UTC date inside MagOps native correction engine",
                    StringComparison.Ordinal),
                "internal UTC-date correction boundary missing");
        });
    }

    private static void TestFieldBeijingProgressDatesAndLeadershipReport()
    {
        WithTempRoot(root =>
        {
            DateTimeOffset beforeMidnightBeijing =
                new(2026, 9, 9, 15, 59, 0, TimeSpan.Zero);
            DateTimeOffset afterMidnightBeijing =
                new(2026, 9, 9, 16, 1, 0, TimeSpan.Zero);

            var analysis = new FieldMagFolderAnalysis
            {
                SourceDirectory = root,
                Fingerprint = "beijing-boundary",
                UavSamples =
                [
                    new UavMagCanonicalSample(
                        beforeMidnightBeijing,
                        116.0,
                        39.0,
                        null,
                        null,
                        50000,
                        null,
                        "A",
                        20,
                        "A",
                        Path.Combine(root, "uav1.txt"),
                        1,
                        []),
                    new UavMagCanonicalSample(
                        afterMidnightBeijing,
                        116.1,
                        39.1,
                        null,
                        null,
                        50001,
                        null,
                        "A",
                        20,
                        "A",
                        Path.Combine(root, "uav2.txt"),
                        1,
                        [])
                ]
            };

            IReadOnlyList<FieldMagBeijingDateSummary> dates =
                FieldMagDataService.BuildBeijingDateSummaries(analysis);
            Equal(2, dates.Count);
            Equal(
                new DateTime(2026, 9, 9),
                dates[0].BeijingDate);
            Equal(
                new DateTime(2026, 9, 10),
                dates[1].BeijingDate);
            Equal(1, dates[0].UavRecordCount);
            Equal(1, dates[1].UavRecordCount);

            FieldMagFolderAnalysis slice =
                FieldMagDataService.SliceByBeijingDate(
                    analysis,
                    new DateTime(2026, 9, 10));
            Equal(1, slice.UavSamples.Count);
            Equal(afterMidnightBeijing, slice.UavSamples[0].TimestampUtc);

            var day1 = new FieldDailyProgressRecord
            {
                Date = "2026-09-09",
                Workarea = "A区",
                RouteFingerprint = "route-x",
                PlannedSortieCount = 10,
                TodayCompletedSorties = ["架次01", "架次02", "架次03"],
                CompletedSorties = ["架次01", "架次02", "架次03"],
                RemainingSorties = ["架次04", "架次05", "架次06", "架次07", "架次08", "架次09", "架次10"],
                ExpectedDailySorties = 4,
                ProgressTimeZone = FieldMagDataService.BeijingProgressTimeZone,
                DailyCompletionPercent = 75,
                TotalCompletionPercent = 30,
                ProgressSummary = "当日完成 3 架次，累计完成 3 架次。",
                NextActionSummary = "继续推进剩余架次。"
            };
            var day2 = new FieldDailyProgressRecord
            {
                Date = "2026-09-10",
                Workarea = "A区",
                RouteFingerprint = "route-x",
                PlannedSortieCount = 10,
                TodayCompletedSorties = ["架次04", "架次05"],
                CompletedSorties = ["架次01", "架次02", "架次03", "架次04", "架次05"],
                RemainingSorties = ["架次06", "架次07", "架次08", "架次09", "架次10"],
                ExpectedDailySorties = 4,
                ProgressTimeZone = FieldMagDataService.BeijingProgressTimeZone,
                DailyCompletionPercent = 50,
                TotalCompletionPercent = 50,
                ProgressSummary = "当日完成 2 架次，累计完成 5 架次。",
                NextActionSummary = "下一作业日继续完成剩余 5 架次。",
                DataSummary = "THIS TECHNICAL DATA STATUS MUST NOT APPEAR"
            };

            string dailyPath = Path.Combine(root, "daily.md");
            FieldworkDocumentExportService.WriteDailyProgress(dailyPath, day2);
            string dailyText = File.ReadAllText(dailyPath);
            True(dailyText.Contains("北京时间"), "daily leadership report must state Beijing time");
            True(dailyText.Contains("当天工作百分比"), "daily work percentage missing");
            True(!dailyText.Contains("当日计划完成率"), "deprecated daily planned completion percentage must not be exported");
            True(dailyText.Contains("项目总完成率"), "total completion percentage missing");
            True(!dailyText.Contains("数据状态"), "daily leadership report must omit detailed data status");
            True(!dailyText.Contains("THIS TECHNICAL"), "daily leadership report leaked technical data summary");

            string projectPath = Path.Combine(root, "project.md");
            FieldworkDocumentExportService.WriteProjectProgress(
                projectPath,
                [day1, day2]);
            string projectText = File.ReadAllText(projectPath);
            True(projectText.Contains("每日工作进度概述"), "project daily overview missing");
            True(projectText.Contains("50%"), "project total percentage missing");
            True(!projectText.Contains("THIS TECHNICAL"), "project report leaked technical data details");

            string dailyPreview =
                FieldworkDocumentExportService.BuildDailyProgressPreview(day2);
            True(dailyPreview.Contains("当天简报"), "daily report preview mode missing");
            True(dailyPreview.Contains("项目总完成率 50%"), "daily preview total percentage missing");

            string projectPreview =
                FieldworkDocumentExportService.BuildProjectProgressPreview([day1, day2]);
            True(projectPreview.Contains("总进度简报"), "project report preview mode missing");
            True(projectPreview.Contains("每日工作进度概述"), "project preview daily overview missing");
            True(projectPreview.Contains("2026-09-09"), "project preview first day missing");
            True(projectPreview.Contains("2026-09-10"), "project preview second day missing");

            day2.SurveyLineMatchingAvailable = true;
            day2.PlannedSurveyLineKm = 100d;
            day2.TodayCompletedSurveyLineKm = 8.5d;
            day2.CompletedSurveyLineKm = 50d;
            day2.RemainingSurveyLineKm = 50d;
            day2.TotalCompletionPercent = 50d;
            string kmPreview =
                FieldworkDocumentExportService.BuildDailyProgressPreview(day2);
            True(
                kmPreview.Contains("当日完成测线 8.50 km"),
                "leadership preview should report completed survey-line kilometers");
            True(
                kmPreview.Contains("累计完成 50.00 km / 100.00 km"),
                "leadership preview should report cumulative survey-line kilometers");
            True(
                kmPreview.Contains("当天工作百分比 21.3%"),
                "survey-line daily work percentage should use today km / derived daily-km target");

            day2.TodayCompletedSurveyLineKm = 50d;
            string overTargetPreview =
                FieldworkDocumentExportService.BuildDailyProgressPreview(day2);
            True(
                overTargetPreview.Contains("当天工作百分比 125%"),
                "daily work percentage must be allowed to exceed 100 percent");
            day2.TodayCompletedSurveyLineKm = 8.5d;

            string changedTargetPreview =
                FieldworkDocumentExportService.BuildProjectProgressPreview(
                    [day1, day2],
                    expectedDailySorties: 6);
            True(
                changedTargetPreview.Contains("2026-09-09  当日 3 架次；当天工作百分比 50%"),
                "changed daily target did not refresh first historical work percentage");
            True(
                changedTargetPreview.Contains("2026-09-10  当日 2 架次；当日完成测线 8.50 km；当天工作百分比 14.2%"),
                "changed daily target did not refresh second historical work percentage");
            True(
                !changedTargetPreview.Contains("计划完成率"),
                "project preview must not show deprecated daily planned completion rate");

            string changedTargetPath =
                Path.Combine(root, "project_changed_target.md");
            FieldworkDocumentExportService.WriteProjectProgress(
                changedTargetPath,
                [day1, day2],
                expectedDailySorties: 6);
            string changedTargetText =
                File.ReadAllText(changedTargetPath);
            True(
                changedTargetText.Contains("| 2026-09-09 | 0.00 km | 50% | 0.00 km | 30% | 3 | 3 |"),
                "exported project report did not refresh first historical work percentage");
            True(
                changedTargetText.Contains("| 2026-09-10 | 8.50 km | 14.2% | 50.00 km | 50% | 2 | 5 |"),
                "exported project report did not refresh second historical work percentage");
            True(
                !changedTargetText.Contains("当日计划完成率"),
                "project export must not contain deprecated daily planned completion rate");
        });
    }

    private static void TestGsmRoverRolesAndDateConflicts()
    {
        WithTempRoot(root =>
        {
            string folder = Path.Combine(root, "gsm_role_date_guard");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "rover_diurnal.txt"),
                "/Gem Systems GSM-19TG 53010332\n/ID 1 file 1 survey.g   27 X 26\n" +
                "/X Y nT nT/m sq cor-nT time\n00003N 0000020 N 54598.61 -5.50 99 0.00 144104.0\n");
            File.WriteAllText(Path.Combine(folder, "base.txt"),
                "/Gem Systems GSM-19T 4046488\n/ID 4 file 09survey.b   15 II 00\n/time nT sq\n011432.0 54582.91 99\n" +
                // Ensure date checking is not limited to the initial header probe.
                new string(' ', 9000) + "\n/ID 4 file 11survey.b   27 X 26\n/time nT sq\n134832.0 54592.26 99\n");
            var analysis = new FieldMagDataService().AnalyzeFolder(folder);
            Equal(1, analysis.BaseRawFileCount);
            Equal(0, analysis.BaseSamples.Count);
            var rover = analysis.Files.Single(x => Path.GetFileName(x.Path) == "rover_diurnal.txt");
            Equal(FieldMagSensorFamily.Gsm19TG, rover.SensorFamily);
            Equal(0, rover.RecordCount);
            True(rover.Warnings.Any(x => x.Contains("尚不支持", StringComparison.Ordinal)), "unsupported rover must be explicit");
            var bas = analysis.Files.Single(x => Path.GetFileName(x.Path) == "base.txt");
            True(bas.Warnings.Any(x => x.Contains("多个采集日期", StringComparison.Ordinal)), "conflicting dates must not form a false absolute timeline");
            var inspection = new ReadOnlyAnalysis().InspectMagneticData(folder);
            True(inspection.Ok, "inspection completion must remain successful");
            True(inspection.Result is { HasReadableRecords: false, RequiresReview: true }, "inspection must not imply usable observations");

            File.WriteAllText(Path.Combine(folder, "base.txt"),
                "/Gem Systems GSM-19T 4046488\n/ID 4 file 1 survey.b   27 X 26\n/time nT sq\n134832.0 54592.26 99\n" +
                "/ID 4 file 2 survey.b   27 X 26\n/time nT sq\n134842.0 54597.30 99\n");
            Equal(2, new FieldMagDataService().AnalyzeFolder(folder).BaseSamples.Count);
        });
    }

    private static void TestGsm19TNativeRomanDateHeaders()
    {
        WithTempRoot(root =>
        {
            string folder = Path.Combine(root, "gsm_native_headers");
            Directory.CreateDirectory(folder);

            File.WriteAllText(
                Path.Combine(folder, "14.txt"),
                "/Gem Systems GSM-19T 50610428 v7.0 13 III 2025 M t-e2@v7b\r\n" +
                "/ID 1 file 14 survey.b   09 IX 26\r\n" +
                "/BL50 17 IV 2024\r\n" +
                "/time nT sq\r\n" +
                "235950.0  59146.71 99\r\n",
                new UTF8Encoding(false));

            // Real continuation files can lose the /ID and GEM banner while
            // retaining the native "file N survey.b DD RomanMonth YY" line.
            File.WriteAllText(
                Path.Combine(folder, "15.txt"),
                " file 15 survey.b   10 IX 26\r\n" +
                "/time nT sq\r\n" +
                "000000.0  59146.68 99\r\n",
                new UTF8Encoding(false));

            File.WriteAllText(
                Path.Combine(folder, "16.txt"),
                "/Gem Systems GSM-19T 50610428 v7.0 13 III 2025 M t-e2@v7b\r\n" +
                "/ID 1 file 16 survey.b   10 IX 26\r\n" +
                "/time nT sq\r\n" +
                "080400.0  59085.38 99\r\n" +
                // Malformed numeric-looking source row must be reported, not guessed/repaired.
                "083500.0  59159.49240.0  59158.79 99\r\n",
                new UTF8Encoding(false));

            // Real 2026 field data can omit the separator between the file index
            // and survey.b. This must still resolve 15 IX 26 as 2026-09-15.
            File.WriteAllText(
                Path.Combine(folder, "07_no_space.txt"),
                "/Gem Systems GSM-19T 2025027 v7.0 23 IV 2009 M t-e2.v7\r\n" +
                "/ID 2025027 file 07survey.b   15 IX 26\r\n" +
                "/datum  54000.00\r\n" +
                "/time nT sq\r\n" +
                "010702.0  58141.46 99\r\n",
                new UTF8Encoding(false));

            var service = new FieldMagDataService();
            FieldMagFolderAnalysis analysis = service.AnalyzeFolder(folder);

            Equal(4, analysis.BaseRawFileCount);
            Equal(4, analysis.BaseSamples.Count);

            FieldMagFileScan file14 = analysis.Files.Single(x =>
                Path.GetFileName(x.Path).Equals("14.txt", StringComparison.OrdinalIgnoreCase));
            FieldMagFileScan file15 = analysis.Files.Single(x =>
                Path.GetFileName(x.Path).Equals("15.txt", StringComparison.OrdinalIgnoreCase));
            FieldMagFileScan file16 = analysis.Files.Single(x =>
                Path.GetFileName(x.Path).Equals("16.txt", StringComparison.OrdinalIgnoreCase));
            FieldMagFileScan file07NoSpace = analysis.Files.Single(x =>
                Path.GetFileName(x.Path).Equals("07_no_space.txt", StringComparison.OrdinalIgnoreCase));

            Equal(new DateTime(2026, 9, 9), file14.FirstTimeUtc!.Value.UtcDateTime.Date);
            Equal(new DateTime(2026, 9, 10), file15.FirstTimeUtc!.Value.UtcDateTime.Date);
            Equal(new DateTime(2026, 9, 10), file16.FirstTimeUtc!.Value.UtcDateTime.Date);
            Equal(new DateTime(2026, 9, 15), file07NoSpace.FirstTimeUtc!.Value.UtcDateTime.Date);
            Equal(1, file14.RecordCount);
            Equal(1, file15.RecordCount);
            Equal(1, file16.RecordCount);
            Equal(1, file07NoSpace.RecordCount);
            True(
                file16.Warnings.Any(x =>
                    x.Contains("疑似 GSM-19T 数据行", StringComparison.Ordinal) &&
                    x.Contains("未自动猜测或修复", StringComparison.Ordinal)),
                "malformed GSM-19T source row warning missing");
        });
    }

    private static void TestGsm19THeaderAwareColumnMapping()
    {
        WithTempRoot(root =>
        {
            string folder = Path.Combine(root, "gsm_header_map");
            Directory.CreateDirectory(folder);

            // No GEM banner on purpose: classification and parsing must both
            // respect the explicit /time ... nT ... header. The cor-nT column
            // must never be mistaken for the measured total-field nT column.
            File.WriteAllText(
                Path.Combine(folder, "19.txt"),
                "/ID 09/11/26\n" +
                "/time cor-nT nT sq\n" +
                "101920.0 0.0 59132.50 99\n" +
                "101930.0 0.0 59132.70 98\n",
                new UTF8Encoding(false));

            FieldMagFolderAnalysis analysis =
                new FieldMagDataService().AnalyzeFolder(folder);

            Equal(1, analysis.BaseRawFileCount);
            Equal(2, analysis.BaseSamples.Count);
            Equal(59132.50, analysis.BaseSamples[0].MagneticFieldNt, 1e-9);
            Equal(99d, analysis.BaseSamples[0].Sq!.Value, 1e-9);
            Equal(59132.70, analysis.BaseSamples[1].MagneticFieldNt, 1e-9);
            True(
                analysis.BaseSamples.All(DiurnalBaseValidityGate.IsEligible),
                "valid header-mapped GSM-19T samples should pass the production Base gate");
        });
    }

    private static void TestGsm19TFirstClassSessionIdentity()
    {
        WithTempRoot(root =>
        {
            string folder = Path.Combine(root, "gsm_sessions");
            Directory.CreateDirectory(folder);
            string source = Path.Combine(folder, "base.txt");
            File.WriteAllText(source,
                "/Gem Systems GSM-19T\n" +
                "/ID 09/11/26\n" +
                "/time nT sq\n" +
                "100000.0 59130.0 99\n" +
                "100010.0 59130.1 99\n" +
                // >999 s is a deterministic acquisition-block boundary.
                "110000.0 59131.0 99\n" +
                "110010.0 59131.1 99\n" +
                // Explicit repeated column header starts another auditable block.
                "/time nT sq\n" +
                "110020.0 59131.2 99\n",
                new UTF8Encoding(false));

            var service = new FieldMagDataService();
            FieldMagFolderAnalysis first = service.AnalyzeFolder(folder);
            Equal(3, first.GsmSessions.Count);
            Equal(5, first.BaseSamples.Count);
            True(
                first.Files.Single().SourceFileStableId.StartsWith("gsm-file-", StringComparison.Ordinal),
                "GSM source file stable identity missing");
            Equal(3, first.GsmSessions.Select(x => x.SessionStableId).Distinct().Count());
            True(first.GsmSessions.All(x =>
                x.SourceFileStableId == first.Files.Single().SourceFileStableId &&
                x.RecordCount > 0 &&
                x.QcStatus is "PASS" or "REVIEW" or "INVALID"),
                "session identity/QC summary incomplete");
            True(first.BaseSamples.All(x =>
                !string.IsNullOrWhiteSpace(x.SourceFileStableId) &&
                !string.IsNullOrWhiteSpace(x.SessionStableId) &&
                x.SessionOrdinal is >= 1 and <= 3),
                "sample-to-session membership missing");
            True(first.GsmSessions[1].BoundaryEvidence.Any(x => x.Kind == GsmSessionBoundaryKind.LongGap),
                "long-gap session boundary evidence missing");
            True(first.GsmSessions[2].BoundaryEvidence.Any(x => x.Kind == GsmSessionBoundaryKind.ExplicitHeader),
                "explicit-header session boundary evidence missing");

            string fileStable = first.Files.Single().SourceFileStableId;
            string[] sessionStable = first.GsmSessions.Select(x => x.SessionStableId).ToArray();

            // File path/name is display provenance only. Same immutable content keeps identity.
            string renamedFolder = Path.Combine(root, "renamed");
            Directory.CreateDirectory(renamedFolder);
            File.Copy(source, Path.Combine(renamedFolder, "moved-name.txt"));
            FieldMagFolderAnalysis moved = service.AnalyzeFolder(renamedFolder);
            Equal(fileStable, moved.Files.Single().SourceFileStableId);
            True(sessionStable.SequenceEqual(moved.GsmSessions.Select(x => x.SessionStableId)),
                "renaming/moving immutable GSM source changed machine identity");

            // Session QC must operate per first-class session, not SourceFile+UTC date.
            string badFolder = Path.Combine(root, "session_qc");
            Directory.CreateDirectory(badFolder);
            File.WriteAllText(Path.Combine(badFolder, "same-day.txt"),
                "/ID 09/11/26\n/time nT sq\n" +
                "100000.0 59130.0 99\n100010.0 59130.1 99\n" +
                "/time nT sq\n" +
                "100020.0 0.0 1\n100030.0 0.0 1\n",
                new UTF8Encoding(false));
            FieldMagFolderAnalysis qc = service.AnalyzeFolder(badFolder);
            Equal(2, qc.GsmSessions.Count);
            Equal("PASS", qc.GsmSessions[0].QcStatus);
            Equal("INVALID", qc.GsmSessions[1].QcStatus);
            True(qc.BaseSamples.Where(x => x.SessionOrdinal == 1)
                    .All(x => !x.QualityFlags.Contains("BASE_SESSION_INVALID")),
                "valid session was contaminated by another block in same file/day");
            True(qc.BaseSamples.Where(x => x.SessionOrdinal == 2)
                    .All(x => x.QualityFlags.Contains("BASE_SESSION_INVALID")),
                "invalid first-class session was not blocked");
        });
    }

    private static void TestFieldMagUtcBridgeAndExports()
    {
        WithTempRoot(root =>
        {
            string folder = CreateFieldMagFixture(root);
            var service = new FieldMagDataService();
            FieldMagFolderAnalysis analysis = service.AnalyzeFolder(folder);
            var exportWorkspace = new WorkspaceService(new JsonStore());
            var exportStore = new JsonStore();
            exportWorkspace = new WorkspaceService(exportStore);
            var exportProject = exportWorkspace.Initialize(root, "export-freshness");
            var artifactLedger = new ArtifactService(exportStore, exportWorkspace);
            var exportContext = new GeneratedArtifactContext(exportProject.ProjectId, "");
            new FieldDataImportService(new JsonStore()).EnrichFromAnalysis(root, analysis);

            string canonicalDirectory = Path.Combine(root, "canonical");
            FieldMagExportResult canonical =
                service.WriteIntegratedCanonicalOutputs(
                    canonicalDirectory,
                    analysis, exportContext);
            Equal(6, canonical.Files.Count);
            True(canonical.Files.All(File.Exists), "canonical output file missing");
            True(canonical.Files.All(x => GeneratedArtifactIdentity.Inspect(x).Status == "VERIFIED" &&
                GeneratedArtifactIdentity.InspectSourceFreshnessStatus(x) == "CURRENT"),
                "integrated bundle must carry verified source-bound sidecars after atomic commit");
            var canonicalArtifactIds = canonical.Files.ToDictionary(
                x => Path.GetFileName(x)!,
                x => GeneratedArtifactIdentity.Inspect(x).Metadata!.ArtifactId,
                StringComparer.OrdinalIgnoreCase);
            foreach (string file in canonical.Files)
                artifactLedger.RegisterGeneratedFile(root, file, "field_data_integration");
            string coverageAll = canonical.Files.Single(x => x.EndsWith("19T覆盖检查_每日摘要.csv", StringComparison.OrdinalIgnoreCase));
            True(File.ReadAllText(coverageAll).Contains("CoveragePercent", StringComparison.Ordinal), "daily Base coverage summary missing");
            string uavAll = canonical.Files.Single(x => x.EndsWith("UAV磁测_整合.csv.gz", StringComparison.OrdinalIgnoreCase));
            string baseAll = canonical.Files.Single(x => x.EndsWith("GSM19T_日变站_整合.csv.gz", StringComparison.OrdinalIgnoreCase));
            Equal(4, ReadGzipLines(uavAll).Length); // header + three Raw records, including F
            Equal(6, ReadGzipLines(baseAll).Length); // header + five Base records
            string canonicalManifest = File.ReadAllText(canonical.ManifestPath);
            True(canonicalManifest.Contains("magops-field-mag-integrated-6"), "integrated manifest schema missing");

            string canonicalUavHash = GeneratedArtifactIdentity.Hash(uavAll);
            FieldMagExportResult canonicalAgain =
                service.WriteIntegratedCanonicalOutputs(canonicalDirectory, analysis, exportContext);
            Equal(canonicalUavHash, GeneratedArtifactIdentity.Hash(
                canonicalAgain.Files.Single(x => x.EndsWith("UAV磁测_整合.csv.gz", StringComparison.OrdinalIgnoreCase))));
            foreach (string file in canonicalAgain.Files)
            {
                Equal(canonicalArtifactIds[Path.GetFileName(file)],
                    GeneratedArtifactIdentity.Inspect(file).Metadata!.ArtifactId);
                artifactLedger.RegisterGeneratedFile(root, file, "field_data_integration");
            }
            string sourcePath = analysis.Files[0].Path;
            byte[] originalSource = File.ReadAllBytes(sourcePath);
            try
            {
                File.AppendAllText(sourcePath, "\n# changed-after-analysis\n", new UTF8Encoding(false));
                True(artifactLedger.RefreshFreshnessStatus(root)
                        .Where(x => canonicalAgain.Files.Contains(x.Path, StringComparer.OrdinalIgnoreCase))
                        .All(x => x.SourceFreshnessStatus == "STALE"),
                    "integrated exports should become stale in the ledger when an analyzed source changes");
            }
            finally
            {
                File.WriteAllBytes(sourcePath, originalSource);
            }
            True(artifactLedger.RefreshFreshnessStatus(root)
                    .Where(x => canonicalAgain.Files.Contains(x.Path, StringComparer.OrdinalIgnoreCase))
                    .All(x => x.SourceFreshnessStatus == "CURRENT"),
                "restoring exact analyzed source bytes should restore integrated export freshness");
            True(Directory.Exists(canonicalDirectory + ".previous"),
                "integrated production bundle must retain the previous complete snapshot");
            True(!Directory.GetDirectories(root, "canonical.staging-*").Any(),
                "integrated production staging leaked");

            string bridgeDirectory = Path.Combine(root, "bridge");
            GemLinkBridgeExportResult bridge = service.WriteGemLinkBridgePackage(
                bridgeDirectory,
                analysis,
                new DateTime(2026, 9, 9), exportContext);
            Equal(2, bridge.Jobs.Count);
            True(bridge.Files.All(File.Exists), "GEMLink bridge output file missing");
            True(bridge.Files.All(x => GeneratedArtifactIdentity.Inspect(x).Status == "VERIFIED" &&
                GeneratedArtifactIdentity.InspectSourceFreshnessStatus(x) == "CURRENT"),
                "GEMLink bundle must carry verified source-bound sidecars");
            var bridgeArtifactIds = bridge.Files.ToDictionary(
                x => Path.GetFileName(x)!,
                x => GeneratedArtifactIdentity.Inspect(x).Metadata!.ArtifactId,
                StringComparer.OrdinalIgnoreCase);
            foreach (string file in bridge.Files)
                artifactLedger.RegisterGeneratedFile(root, file, "field_gemlink_bridge");
            GemLinkBridgeExportResult bridgeAgain = service.WriteGemLinkBridgePackage(
                bridgeDirectory,
                analysis,
                new DateTime(2026, 9, 9), exportContext);
            foreach (string file in bridgeAgain.Files)
            {
                Equal(bridgeArtifactIds[Path.GetFileName(file)],
                    GeneratedArtifactIdentity.Inspect(file).Metadata!.ArtifactId);
                artifactLedger.RegisterGeneratedFile(root, file, "field_gemlink_bridge");
            }

            string rover0908 = Path.Combine(bridgeDirectory, "Rover_20260908_MAGOPS.csv");
            string rover0909 = Path.Combine(bridgeDirectory, "Rover_20260909_MAGOPS.csv");
            Equal(2, File.ReadAllLines(rover0908).Length); // header + one usable T record
            Equal(2, File.ReadAllLines(rover0909).Length); // F record excluded from Rover bridge
            True(
                bridge.Jobs.Single(x => x.UtcDate == new DateTime(2026, 9, 9)).Warnings.Any(x => x.Contains(">999", StringComparison.Ordinal)),
                "long Base gap was not carried into the UTC-date bridge job");

            string bridgeManifest = File.ReadAllText(bridge.ManifestPath);
            True(bridgeManifest.Contains("magops-gemlink-bridge-1"), "bridge manifest schema missing");
            True(bridgeManifest.Contains("黄金对照/兼容核查"), "bridge must be marked as compatibility/golden validation only");
            string unrelated = Path.Combine(bridgeDirectory, "operator-notes.txt");
            File.WriteAllText(unrelated, "keep this local note");
            FieldMagFolderAnalysis oneDay = FieldMagDataService.SliceByUtcDate(analysis, new DateTime(2026, 9, 9));
            service.WriteGemLinkBridgePackage(bridgeDirectory, oneDay, new DateTime(2026, 9, 9), exportContext);
            True(!File.Exists(rover0908) && !File.Exists(rover0908 + GeneratedArtifactIdentity.Suffix),
                "replacing GEMLink bundle removes obsolete verified date members and sidecars");
            True(File.Exists(Path.Combine(bridgeDirectory + ".previous", Path.GetFileName(rover0908))),
                "previous GEMLink bundle retains removed date members");
            Equal("keep this local note", File.ReadAllText(unrelated));
            string blockedSidecar = Path.Combine(bridgeDirectory, "Rover_20260908_MAGOPS.csv" + GeneratedArtifactIdentity.Suffix);
            Directory.CreateDirectory(blockedSidecar);
            var beforeFailure = Directory.GetFiles(bridgeDirectory).ToDictionary(
                x => Path.GetFileName(x), GeneratedArtifactIdentity.Hash);
            Throws<UnauthorizedAccessException>(() => service.WriteGemLinkBridgePackage(
                bridgeDirectory, analysis, new DateTime(2026, 9, 9), exportContext));
            foreach (var previousFile in beforeFailure)
                Equal(previousFile.Value, GeneratedArtifactIdentity.Hash(Path.Combine(bridgeDirectory, previousFile.Key)));
            True(Directory.Exists(blockedSidecar) && !Directory.GetDirectories(root, "bridge.staging-*").Any(),
                "failed GEMLink staging preserves the prior bundle and removes its staging directory");
        });
    }

    private static void TestGemLinkTimeCompatibility()
    {
        True(
            GemLinkTimeCompatibility.TryParseGemLinkTime(
                "12:34:56.78",
                out int colon),
            "colon GEMLink time must parse");
        Equal(452967, colon);

        True(
            GemLinkTimeCompatibility.TryParseGemLinkTime(
                "123456.78",
                out int compact),
            "compact GEMLink time must parse");
        Equal(colon, compact);

        True(
            GemLinkTimeCompatibility.TryParseGemLinkTime(
                "240000.0",
                out int midnight),
            "240000.0 must be accepted");
        Equal(0, midnight);

        True(
            !GemLinkTimeCompatibility.TryParseGemLinkTime(
                "25:00:00",
                out _),
            "invalid hour must be rejected");
        True(
            !GemLinkTimeCompatibility.TryParseGemLinkTime(
                "12:61:00",
                out _),
            "invalid minute must be rejected");
        True(
            !GemLinkTimeCompatibility.TryParseGemLinkTime(
                "12:00:61",
                out _),
            "invalid second must be rejected");

        DateTimeOffset stamp =
            new DateTimeOffset(
                2026,
                9,
                9,
                12,
                0,
                0,
                TimeSpan.Zero)
            .AddMilliseconds(199);
        Equal(
            432001,
            GemLinkTimeCompatibility
                .ToTenthsOfSecondSinceUtcMidnight(
                    stamp));
    }

    private static void TestNativeDiurnalMath()
    {
        var engine =
            new DiurnalCorrectionEngine();

        DateTimeOffset t0 =
            new(
                2026,
                9,
                9,
                12,
                0,
                0,
                TimeSpan.Zero);
        DateTimeOffset t5 =
            t0.AddSeconds(5)
                .AddMilliseconds(90);
        DateTimeOffset t10 =
            t0.AddSeconds(10);

        DiurnalCorrectionResult result =
            engine.Correct(
                [
                    new DiurnalRoverSample(
                        t0.AddMilliseconds(90),
                        50100d,
                        "rover.csv",
                        1),
                    new DiurnalRoverSample(
                        t5,
                        50100d,
                        "rover.csv",
                        2)
                ],
                [
                    new DiurnalBaseSample(
                        t0,
                        50000d,
                        "base.txt",
                        1),
                    new DiurnalBaseSample(
                        t10,
                        50010d,
                        "base.txt",
                        2)
                ],
                new DiurnalCorrectionOptions(
                    DatumNt: 50000d,
                    GemLinkTenthsCompatibility: true,
                    MaxInterpolationGapSeconds: 5d));

        Equal(1, result.ExactCount);
        Equal(1, result.InterpolatedCount);
        Equal(0, result.UncorrectedCount);

        DiurnalCorrectedSample exact =
            result.Samples.Single(x =>
                x.Mode ==
                DiurnalCorrectionMode.Exact);
        Equal(
            100d,
            exact.DiurnalDifferenceNt!.Value,
            1e-9);
        Equal(
            50100d,
            exact.DatumAdjustedNt!.Value,
            1e-9);

        DiurnalCorrectedSample interpolated =
            result.Samples.Single(x =>
                x.Mode ==
                DiurnalCorrectionMode.Interpolated);
        Equal(
            50005d,
            interpolated.BaseUsedNt!.Value,
            1e-9);
        Equal(
            95d,
            interpolated.DiurnalDifferenceNt!.Value,
            1e-9);
        Equal(
            50095d,
            interpolated.DatumAdjustedNt!.Value,
            1e-9);
        True(
            interpolated.QualityFlags.Contains(
                "BASE_GAP_LARGE"),
            "large interpolation gap must be calculated but flagged");
    }

    private static void TestNativeDiurnalUtcIsolation()
    {
        var engine =
            new DiurnalCorrectionEngine();

        DateTimeOffset midnight =
            new(
                2026,
                9,
                9,
                0,
                0,
                0,
                TimeSpan.Zero);

        DiurnalCorrectionResult result =
            engine.Correct(
                [
                    new DiurnalRoverSample(
                        midnight,
                        50100d,
                        "rover.csv",
                        1),
                    new DiurnalRoverSample(
                        midnight.AddHours(12)
                            .AddSeconds(5),
                        50100d,
                        "rover.csv",
                        2)
                ],
                [
                    // Previous-day Base must never be used to interpolate
                    // the 00:00:00 current-day Rover sample.
                    new DiurnalBaseSample(
                        midnight.AddMilliseconds(-100),
                        50000d,
                        "base_prev.txt",
                        1),
                    new DiurnalBaseSample(
                        midnight.AddMilliseconds(100),
                        50001d,
                        "base_today.txt",
                        1),
                    new DiurnalBaseSample(
                        midnight.AddHours(12),
                        50000d,
                        "base_today.txt",
                        2),
                    new DiurnalBaseSample(
                        midnight.AddHours(12)
                            .AddSeconds(10),
                        50010d,
                        "base_today.txt",
                        3)
                ],
                new DiurnalCorrectionOptions());

        DiurnalCorrectedSample atMidnight =
            result.Samples.Single(x =>
                x.RoverSourceRow == 1);
        Equal(
            DiurnalCorrectionMode.Uncorrected,
            atMidnight.Mode);
        True(
            atMidnight.QualityFlags.Contains(
                "NO_BASE_BEFORE"),
            "cross-UTC-date Base sample must not be used");

        DiurnalCorrectedSample midday =
            result.Samples.Single(x =>
                x.RoverSourceRow == 2);
        Equal(
            DiurnalCorrectionMode.Interpolated,
            midday.Mode);
        Equal(
            50005d,
            midday.BaseUsedNt!.Value,
            1e-9);

        DiurnalCorrectionResult noOverlap =
            engine.Correct(
                [
                    new DiurnalRoverSample(
                        midnight.AddHours(18),
                        50100d,
                        "late_rover.csv",
                        1)
                ],
                [
                    new DiurnalBaseSample(
                        midnight.AddHours(1),
                        50000d,
                        "early_base.txt",
                        1)
                ],
                new DiurnalCorrectionOptions());

        Equal(
            1,
            noOverlap.UncorrectedCount);
        True(
            noOverlap.Samples[0]
                .QualityFlags
                .Contains("NO_OVERLAP"),
            "fully disjoint Base/Rover time ranges must reject correction");
    }

    private static void TestNativeDiurnalDuplicateBasePolicy()
    {
        var engine =
            new DiurnalCorrectionEngine();
        DateTimeOffset t =
            new(
                2026,
                9,
                9,
                12,
                0,
                0,
                TimeSpan.Zero);

        DiurnalCorrectionResult result =
            engine.Correct(
                [
                    new DiurnalRoverSample(
                        t.AddMilliseconds(50),
                        50100d,
                        "rover.csv",
                        1)
                ],
                [
                    new DiurnalBaseSample(
                        t.AddMilliseconds(10),
                        50000d,
                        "base.txt",
                        1),
                    new DiurnalBaseSample(
                        t.AddMilliseconds(90),
                        50005d,
                        "base.txt",
                        2)
                ],
                new DiurnalCorrectionOptions());

        Equal(
            1,
            result.UncorrectedCount);
        Equal(
            "DUPLICATE_BASE_TIME_REJECTED",
            result.Days[0].Status);
        True(
            result.Samples[0]
                .QualityFlags
                .Contains(
                    "DUPLICATE_BASE_TIME"),
            "unknown GEMLink duplicate-key behavior must not be guessed");
    }

    private static void TestNativeDiurnalProductionBaseValidity()
    {
        WithTempRoot(root =>
        {
            string folder = Path.Combine(root, "production_base_gate");
            Directory.CreateDirectory(folder);

            File.WriteAllText(
                Path.Combine(folder, "uav.txt"),
                "# QuSpin's total-field magnetometer-5Hz\n" +
                "UTCData,UTCTime,Longitude,Latitude,Altitude,Mag,Signal,GpsStatus,Satellites,SensorStatus\n" +
                "2026-09-11,10:19:24.000,108.0840912,47.3212395,1417.6,59012.945,20,3D,15,T\n",
                new UTF8Encoding(false));

            File.WriteAllText(
                Path.Combine(folder, "base_morning.txt"),
                "/Gem Systems GSM-19T\n/ID 09/11/26\n/time nT sq\n" +
                "053830.0 59130.0 99\n" +
                "053840.0 59131.0 99\n",
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(folder, "19.txt"),
                "/Gem Systems GSM-19T\n/ID 09/11/26\n/time nT sq\n" +
                "064620.0 37560.79 19\n" +
                "064630.0 0.0 1\n" +
                "064640.0 0.0 1\n" +
                "101920.0 0.0 1\n" +
                "101930.0 0.0 1\n",
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(folder, "base_evening.txt"),
                "/Gem Systems GSM-19T\n/ID 09/11/26\n/time nT sq\n" +
                "225830.0 59140.0 99\n" +
                "225840.0 59141.0 99\n",
                new UTF8Encoding(false));

            FieldMagFolderAnalysis analysis =
                new FieldMagDataService().AnalyzeFolder(folder);
            DiurnalBaseValiditySummary validity =
                DiurnalBaseValidityGate.Evaluate(analysis.BaseSamples);

            Equal(9, validity.TotalRecords);
            Equal(4, validity.AcceptedRecords);
            Equal(5, validity.RejectedRecords);
            Equal(1, validity.RejectedSessions);
            True(validity.AcceptedMedianFieldNt.HasValue, "accepted Base median missing");
            Equal(59135.5, validity.AcceptedMedianFieldNt!.Value, 1e-9);

            IReadOnlyList<Gsm19TCanonicalSample> badSession =
                analysis.BaseSamples
                    .Where(x => Path.GetFileName(x.SourceFile).Equals("19.txt", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            Equal(5, badSession.Count);
            True(
                badSession.All(x => x.QualityFlags.Contains("BASE_SESSION_INVALID", StringComparer.OrdinalIgnoreCase)),
                "zero/low-SQ Base session must be blocked as a whole");

            var exporter = new DiurnalCorrectionExportService();
            DiurnalCorrectionResult result = exporter.Correct(
                analysis,
                new DiurnalCorrectionOptions(
                    DatumNt: 59135.5,
                    GemLinkTenthsCompatibility: true,
                    MaxInterpolationGapSeconds: 999d,
                    RejectInterpolationAcrossLargeGap: true));

            Equal(1, result.Samples.Count);
            Equal(1, result.UncorrectedCount);
            True(
                !result.Samples[0].CorrectedMagNt.HasValue,
                "production mode must not fabricate a corrected value across a long Base gap");
            True(
                result.Samples[0].QualityFlags.Contains("BASE_GAP_TOO_LARGE", StringComparer.OrdinalIgnoreCase),
                "production long-gap rejection flag missing");
            double? baseGapSeconds = result.Samples[0].BaseGapSeconds;
            True(
                baseGapSeconds is > 60000d,
                "expected the invalid 19.txt session to be removed before interpolation");
        });
    }

    private static void TestDailyUavBaseCoveragePreview()
    {
        DateTimeOffset T(int minute, int second) =>
            new(2026, 9, 11, 0, minute, second, TimeSpan.Zero);

        IReadOnlyList<UavMagCanonicalSample> uav =
        [
            new UavMagCanonicalSample(T(0, 5), 108d, 47d, 1400d, 100d, 59000d, 20d, "3D", 15, "T", "uav-a.txt", 1, []),
            new UavMagCanonicalSample(T(0, 15), 108d, 47d, 1400d, 100d, 59001d, 20d, "3D", 15, "T", "uav-a.txt", 2, []),
            new UavMagCanonicalSample(T(20, 0), 108d, 47d, 1400d, 100d, 59002d, 20d, "3D", 15, "T", "uav-b.txt", 1, []),
            new UavMagCanonicalSample(T(20, 10), 108d, 47d, 1400d, 100d, 59003d, 20d, "3D", 15, "T", "uav-b.txt", 2, [])
        ];

        IReadOnlyList<Gsm19TCanonicalSample> baseSamples =
        [
            new Gsm19TCanonicalSample(T(0, 0), 59120d, 99d, "base.txt", 1, []),
            new Gsm19TCanonicalSample(T(0, 30), 59121d, 99d, "base.txt", 2, []),
            new Gsm19TCanonicalSample(T(20, 0), 0d, 1d, "bad.txt", 1, ["BASE_VALUE_OUT_OF_RANGE", "LOW_SQ", "BASE_SESSION_INVALID"]),
            new Gsm19TCanonicalSample(T(40, 0), 59122d, 99d, "base.txt", 3, [])
        ];

        IReadOnlyList<FieldMagDailyCoverageSummary> summaries =
            FieldMagCoverageService.Analyze(uav, baseSamples);
        Equal(1, summaries.Count);
        FieldMagDailyCoverageSummary summary = summaries[0];
        Equal(new DateTime(2026, 9, 11), summary.BeijingDate);
        Equal(4, summary.UavEligibleRecords);
        Equal(2, summary.CoveredUavRecords);
        Equal(2, summary.UncoveredUavRecords);
        Equal(50d, summary.CoveragePercent, 1e-9);
        Equal(4, summary.BaseRecords);
        Equal(3, summary.AcceptedBaseRecords);
        Equal(1, summary.RejectedBaseRecords);
        Equal("PARTIAL", summary.Status);
        True(
            summary.Reasons.Contains("BASE_GAP_TOO_LARGE", StringComparer.OrdinalIgnoreCase),
            "coverage preview must expose the production long-gap reason");
        True(
            summary.Segments.Any(x => x.Kind == FieldMagCoverageSegmentKind.UavUncovered),
            "coverage preview must include red/uncovered UAV segments");
        True(
            summary.Segments.Any(x => x.Kind == FieldMagCoverageSegmentKind.BaseRejected),
            "coverage preview must include rejected Base segments");
    }

    private static void TestGsm19T240000Rollover()
    {
        WithTempRoot(root =>
        {
            string folder =
                Path.Combine(
                    root,
                    "base240000");
            Directory.CreateDirectory(
                folder);

            File.WriteAllText(
                Path.Combine(
                    folder,
                    "base.txt"),
                "/Gem Systems GSM-19T\n" +
                "/ID 09/09/26\n" +
                "/time nT sq\n" +
                "235959.9 50000.0 99\n" +
                "240000.0 50001.0 99\n" +
                "000000.1 50002.0 99\n",
                new UTF8Encoding(false));

            FieldMagFolderAnalysis analysis =
                new FieldMagDataService()
                    .AnalyzeFolder(
                        folder);

            Equal(
                3,
                analysis.BaseSamples.Count);
            Equal(
                new DateTime(
                    2026,
                    9,
                    9),
                analysis.BaseSamples[0]
                    .TimestampUtc
                    .UtcDateTime
                    .Date);
            Equal(
                new DateTime(
                    2026,
                    9,
                    10),
                analysis.BaseSamples[1]
                    .TimestampUtc
                    .UtcDateTime
                    .Date);
            Equal(
                new DateTime(
                    2026,
                    9,
                    10),
                analysis.BaseSamples[2]
                    .TimestampUtc
                    .UtcDateTime
                    .Date);
            Equal(
                TimeSpan.Zero,
                analysis.BaseSamples[1]
                    .TimestampUtc
                    .UtcDateTime
                    .TimeOfDay);
            Equal(2, analysis.GsmSessions.Count);
            True(analysis.GsmSessions[1].BoundaryEvidence.Any(x =>
                    x.Kind == GsmSessionBoundaryKind.UtcDateRollover),
                "UTC rollover did not create auditable GSM session boundary");
            Equal(1, analysis.BaseSamples[0].SessionOrdinal);
            Equal(2, analysis.BaseSamples[1].SessionOrdinal);
            Equal(2, analysis.BaseSamples[2].SessionOrdinal);
        });
    }

    private static void TestFieldMagIntegratedAndNativeDiurnalExports()
    {
        WithTempRoot(root =>
        {
            string folder =
                CreateFieldMagFixture(
                    root);
            var service =
                new FieldMagDataService();
            FieldMagFolderAnalysis analysis =
                service.AnalyzeFolder(
                    folder);

            string integratedDirectory =
                Path.Combine(
                    root,
                    "06_数据处理",
                    "01_磁测数据整理");
            FieldMagExportResult integrated =
                service.WriteIntegratedCanonicalOutputs(
                    integratedDirectory,
                    analysis);

            True(
                integrated.Files.All(
                    File.Exists),
                "integrated outputs missing");

            string uavGzip =
                Path.Combine(
                    integratedDirectory,
                    "UAV磁测_整合.csv.gz");
            string baseGzip =
                Path.Combine(
                    integratedDirectory,
                    "GSM19T_日变站_整合.csv.gz");

            True(
                File.Exists(uavGzip),
                "compact UAV integrated output missing");
            True(
                File.Exists(baseGzip),
                "compact GSM19T integrated output missing");
            True(
                !File.Exists(
                    Path.Combine(
                        integratedDirectory,
                        "UAV磁测_整合.csv")),
                "verbose uncompressed UAV full file must not be written");
            True(
                !Directory.Exists(
                    Path.Combine(
                        integratedDirectory,
                        "20260909")),
                "integrated production data must not create UTC-date directories");

            Equal(
                4,
                ReadGzipLines(
                    uavGzip).Length);
            Equal(
                6,
                ReadGzipLines(
                    baseGzip).Length);

            string diurnalDirectory =
                Path.Combine(
                    root,
                    "06_数据处理",
                    "02_日变改正");
            var exporter =
                new DiurnalCorrectionExportService();
            DiurnalCorrectionExportResult diurnal =
                exporter.WriteNativeCorrection(
                    diurnalDirectory,
                    analysis,
                    new DiurnalCorrectionOptions(
                        DatumNt: 0d,
                        GemLinkTenthsCompatibility: true,
                        MaxInterpolationGapSeconds: 999d),
                    LoadIgrfModels(),
                    new MagneticPreLevelProcessingOptions(),
                     auditRequest: ReviewDraftRequest(analysis));

            True(
                diurnal.Files.All(
                    File.Exists),
                "native diurnal outputs missing");
            Equal(
                1,
                diurnal.Correction.ExactCount);
            Equal(
                1,
                diurnal.Correction.UncorrectedCount);
            Equal(
                1,
                diurnal.ExcludedRoverRecords);

            string[] correctedLines =
                ReadGzipLines(
                    diurnal.CorrectedCsvPath);
            Equal(
                4,
                correctedLines.Length);
            True(
                correctedLines.Any(x =>
                    x.Contains(
                        ",Exact,",
                        StringComparison.Ordinal)),
                "corrected output must contain exact correction row");
            True(
                correctedLines.Any(x =>
                    x.Contains(
                        "ROVER_SENSOR_F",
                        StringComparison.Ordinal)),
                "F-status Rover row must remain in complete output with QC flag");

            True(
                File.Exists(
                    diurnal.ProcessingCsvPath),
                "lightweight processing CSV missing");
            Equal(
                1,
                diurnal.ProcessingRecordCount);

            string manifest =
                File.ReadAllText(
                    diurnal.ManifestPath);
            True(
                manifest.Contains(
                    "magops-native-diurnal-10",
                    StringComparison.Ordinal),
                "current native diurnal manifest schema missing");
            True(
                manifest.Contains(
                    "\"calculation_grouping\": \"UTC Date\"",
                    StringComparison.Ordinal),
                "native diurnal UTC-date grouping missing");
            True(
                manifest.Contains(
                    "\"physical_output_partitioning\": \"none\"",
                    StringComparison.Ordinal),
                "native diurnal output must remain one complete dataset");
            True(
                manifest.Contains(
                    "\"temporal_downsampling\": false",
                    StringComparison.Ordinal),
                "compact output must explicitly preserve original sampling");
        });
    }

    private static void TestFieldMagCompactSourceIdGzipStorage()
    {
        WithTempRoot(root =>
        {
            string folder =
                CreateFieldMagFixture(
                    root);
            FieldMagFolderAnalysis analysis =
                new FieldMagDataService()
                    .AnalyzeFolder(
                        folder);

            string output =
                Path.Combine(
                    root,
                    "compact");
            Directory.CreateDirectory(
                output);
            string legacyUav =
                Path.Combine(
                    output,
                    "UAV磁测_整合.csv");
            string legacyBase =
                Path.Combine(
                    output,
                    "GSM19T_日变站_整合.csv");
            File.WriteAllText(
                legacyUav,
                "legacy-large-uav");
            File.WriteAllText(
                legacyBase,
                "legacy-large-base");

            FieldMagExportResult result =
                new FieldMagDataService()
                    .WriteIntegratedCanonicalOutputs(
                        output,
                        analysis);

            True(
                !File.Exists(legacyUav),
                "legacy verbose UAV output must be removed");
            True(
                !File.Exists(legacyBase),
                "legacy verbose Base output must be removed");

            string uav =
                Path.Combine(
                    output,
                    "UAV磁测_整合.csv.gz");
            string[] lines =
                ReadGzipLines(
                    uav);

            True(
                HasGzipMagic(uav),
                "UAV integrated data must be a real GZip stream");
            True(
                lines[0].Contains(
                    "SourceId",
                    StringComparison.Ordinal),
                "compact UAV header must contain SourceId");
            True(
                !lines[0].Contains(
                    "SourceFile",
                    StringComparison.Ordinal),
                "compact UAV rows must not repeat SourceFile paths");

            string manifest =
                File.ReadAllText(
                    result.ManifestPath);
            True(
                manifest.Contains(
                    "magops-field-mag-integrated-6",
                    StringComparison.Ordinal),
                "compact integrated manifest schema missing");
            True(
                manifest.Contains(
                    "\"source_files\"",
                    StringComparison.Ordinal),
                "SourceId manifest map missing");
            True(
                manifest.Contains(
                    "\"source_path_per_row\": false",
                    StringComparison.Ordinal),
                "manifest must declare source paths are not repeated per row");
            True(
                manifest.Contains(
                    "\"numeric_precision_reduction\": false",
                    StringComparison.Ordinal),
                "manifest must declare precision is preserved");

            using JsonDocument document =
                JsonDocument.Parse(
                    manifest);
            JsonElement sources =
                document.RootElement
                    .GetProperty(
                        "source_files");
            True(
                sources.GetArrayLength() >= 2,
                "source map must contain UAV and GSM19T sources");
        });
    }

    private static void TestNativeDiurnalLightweightProcessingCsv()
    {
        WithTempRoot(root =>
        {
            string folder =
                CreateFieldMagFixture(
                    root);
            FieldMagFolderAnalysis analysis =
                new FieldMagDataService()
                    .AnalyzeFolder(
                        folder);

            string diurnalDirectory =
                Path.Combine(
                    root,
                    "diurnal");
            Directory.CreateDirectory(
                diurnalDirectory);
            string legacyFull =
                Path.Combine(
                    diurnalDirectory,
                    "MagOps_日变改正_完整.csv");
            File.WriteAllText(
                legacyFull,
                "legacy-large-diurnal");

            DiurnalCorrectionExportResult result =
                new DiurnalCorrectionExportService()
                    .WriteNativeCorrection(
                        diurnalDirectory,
                        analysis,
                        new DiurnalCorrectionOptions(),
                        LoadIgrfModels(),
                        new MagneticPreLevelProcessingOptions(),
                     auditRequest: ReviewDraftRequest(analysis));

            True(
                File.Exists(legacyFull),
                "blocked review export must preserve the previous formal output");
            True(result.AuditProduction.IsReviewDraft, "missing audit evidence must produce an explicit review draft");

            True(
                result.CorrectedCsvPath.EndsWith(
                    ".csv.gz",
                    StringComparison.OrdinalIgnoreCase),
                "complete audit output must be compressed");
            True(
                HasGzipMagic(
                    result.CorrectedCsvPath),
                "complete audit output must be a real GZip stream");

            string[] full =
                ReadGzipLines(
                    result.CorrectedCsvPath);
            True(
                full[0].Contains(
                    "RoverSourceId",
                    StringComparison.Ordinal),
                "full audit output must use RoverSourceId");
            True(
                full[0].Contains(
                    "BasePrevSourceId",
                    StringComparison.Ordinal),
                "full audit output must use Base source IDs");
            True(
                !full[0].Contains(
                    "SourceFile",
                    StringComparison.Ordinal),
                "full audit output must not repeat source paths");
            True(
                !full[0].Contains(
                    "BasePrevUtc",
                    StringComparison.Ordinal),
                "recoverable BasePrevUtc must not be repeated in compact full rows");
            True(
                full[0].Contains(
                    "DiurnalDifferenceNt",
                    StringComparison.Ordinal),
                "full audit output must expose explicit rigai/diurnal-difference semantics");
            True(
                full[0].Contains(
                    "DatumAdjustedNt",
                    StringComparison.Ordinal),
                "full audit output must separate optional Datum-adjusted reference value");
            True(
                !full[0].Contains(
                    "CorrectedMagNt",
                    StringComparison.Ordinal),
                "ambiguous CorrectedMagNt export column must be removed");

            string[] processing =
                File.ReadAllLines(
                    result.ProcessingCsvPath);
            Equal(
                "LineName,LineOrder,LinePass,DateUtc,TimeUtc,LineTaskId,Longitude,Latitude,X,Y,AltitudeM,LaserAltitudeM,RawMagNt,BaseUsedNt,rigai,Tof,Inc,Dec,T0,dTgz",
                processing[0]);
            string[] firstProcessingFields = processing[1].Split(',');
            Equal("", firstProcessingFields[0]);
            Equal("", firstProcessingFields[1]);
            Equal("", firstProcessingFields[2]);
            Equal("", firstProcessingFields[5]);
            Equal(
                result.ProcessingRecordCount + 1,
                processing.Length);
            True(
                !processing[0].Contains(
                    "Source",
                    StringComparison.OrdinalIgnoreCase),
                "processing CSV must not contain audit/source columns");
            True(
                !processing[0].Contains(
                    "Qc",
                    StringComparison.OrdinalIgnoreCase),
                "processing CSV must stay lightweight");
        });
    }

    private static void TestEmptyDiurnalProcessingOutput()
    {
        WithTempRoot(root =>
        {
            FieldMagFolderAnalysis analysis = new FieldMagDataService().AnalyzeFolder(CreateFieldMagFixture(root));
            var exporter = new DiurnalCorrectionExportService();
            var models = LoadIgrfModels();
            string output = Path.Combine(root, "empty_selection");
            DiurnalCorrectionExportResult Export(FieldMagFolderAnalysis input, AuditProductionRequest request) =>
                exporter.WriteNativeCorrection(output, input, new DiurnalCorrectionOptions(), models,
                    new MagneticPreLevelProcessingOptions(), auditRequest: request);

            var nonempty = Export(analysis, ReviewDraftRequest(analysis));
            True(nonempty.ProcessingRecordCount > 0 && File.Exists(nonempty.ProcessingCsvPath),
                "fixture first publishes a nonempty processing result");
            string original = File.ReadAllText(nonempty.ProcessingCsvPath);
            var empty = Export(analysis, ReviewDraftRequest(analysis) with { IncludeUnconfirmedSamples = false });
            Equal(0, empty.ProcessingRecordCount);
            Equal("", empty.ProcessingCsvPath);
            Equal("", empty.T0SummaryCsvPath);
            True(!File.Exists(nonempty.ProcessingCsvPath), "old processing CSV must not masquerade as the latest empty result");
            True(!empty.Files.Any(p => p.Contains("__MagOps_prelevel_dtgz", StringComparison.Ordinal)),
                "no header-only processing CSV or sidecar may be published or registered");
            Equal(original, File.ReadAllText(Path.Combine(Path.GetDirectoryName(nonempty.ProcessingCsvPath)! + ".previous",
                Path.GetFileName(nonempty.ProcessingCsvPath))));
            True(File.Exists(empty.CorrectedCsvPath) && File.Exists(empty.ManifestPath),
                "full correction audit remains available when no production rows were selected");
            using var manifest = JsonDocument.Parse(File.ReadAllText(empty.ManifestPath));
            Equal("NO_PROCESSING_ROWS", manifest.RootElement.GetProperty("processing_output_status").GetString()!);
            Equal(JsonValueKind.Null, manifest.RootElement.GetProperty("storage").GetProperty("processing_output").ValueKind);
            True(empty.ProcessingOutputWarning.Contains("没有记录进入生产选择"), "zero selection identifies the review gate");
            string qc = empty.Files.Single(p => p.EndsWith(".md", StringComparison.Ordinal));
            True(File.ReadAllText(qc).Contains("未生成可用于 Oasis"), "QC must explicitly explain the absent result");

            var missingBase = new FieldMagFolderAnalysis
            {
                SourceDirectory = analysis.SourceDirectory,
                Fingerprint = analysis.Fingerprint + "-missing-base",
                UavSamples = analysis.UavSamples,
                BaseSamples = []
            };
            var uncorrected = Export(missingBase, ReviewDraftRequest(missingBase));
            True(uncorrected.AuditProduction.SelectedSamples.Count > 0 && uncorrected.ProcessingRecordCount == 0,
                "selected input without Base coverage is also unable to produce processing rows");
            True(uncorrected.ProcessingOutputWarning.Contains("日变数据及其时间覆盖"),
                "missing correction evidence is distinguished from an empty production selection");
        });
    }

    private static void TestNativeDiurnalRigaiIgrfDTgzSemantics()
    {
        WithTempRoot(root =>
        {
            string folder = Path.Combine(root, "prelevel_semantics");
            Directory.CreateDirectory(folder);

            File.WriteAllText(
                Path.Combine(folder, "uav.txt"),
                "# QuSpin's total-field magnetometer-5Hz\n" +
                "UTCData,UTCTime,Longitude,Latitude,Altitude,Mag,Signal,GpsStatus,Satellites,SensorStatus\n" +
                "2026-09-06,06:51:00.000,108.1164474,47.2873688,1452.3,59147.379,20,3D,15,T\n" +
                "2026-09-06,06:51:03.000,108.1165474,47.2873688,1452.4,59147.579,20,3D,15,T\n" +
                "2026-09-07,06:51:00.000,108.1164474,47.2873688,1452.3,59280.000,20,3D,15,T\n" +
                "2026-09-07,06:51:03.000,108.1165474,47.2873688,1452.4,59280.300,20,3D,15,T\n",
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(folder, "base.txt"),
                "/Gem Systems GSM-19T\n/ID 09/06/26\n/time nT sq\n" +
                "065100.0 59166.930 99\n" +
                "065103.0 59166.950 99\n",
                new UTF8Encoding(false));
            File.WriteAllText(
                Path.Combine(folder, "base2.txt"),
                "/Gem Systems GSM-19T\n/ID 09/07/26\n/time nT sq\n" +
                "065100.0 59150.000 99\n" +
                "065103.0 59150.100 99\n",
                new UTF8Encoding(false));

            FieldMagFolderAnalysis analysis =
                new FieldMagDataService().AnalyzeFolder(folder);
            string output = Path.Combine(root, "diurnal_prelevel");
            DiurnalCorrectionExportResult result =
                new DiurnalCorrectionExportService().WriteNativeCorrection(
                    output,
                    analysis,
                    new DiurnalCorrectionOptions(
                        DatumNt: 59152.13,
                        GemLinkTenthsCompatibility: true,
                        MaxInterpolationGapSeconds: 999d,
                        RejectInterpolationAcrossLargeGap: true),
                    LoadIgrfModels(),
                    new MagneticPreLevelProcessingOptions(
                        T0Mode: MagneticT0Mode.AutoByUtcDate),
                     auditRequest: ReviewDraftRequest(analysis));

            Equal(4, result.ProcessingRecordCount);
            Equal(4, result.PreLevel.IgrfRecords);
            Equal("AUTO_BY_UTC_DATE", result.PreLevel.T0Mode);
            Equal(2, result.PreLevel.Days.Count);
            True(
                result.PreLevel.Days.All(x =>
                    x.DTgzMeanNt is >= -0.001 and <= 0.001),
                "daily automatic T0 must center every UTC-day dTgz near zero");
            True(
                result.PreLevel.T0MinNt.HasValue &&
                result.PreLevel.T0MaxNt.HasValue &&
                result.PreLevel.T0MaxNt.Value - result.PreLevel.T0MinNt.Value > 100d,
                "daily automatic T0 fixture must prove different UTC days can use different T0 values");
            True(
                File.Exists(result.T0SummaryCsvPath),
                "UTC-day T0 summary output missing");
            Equal(3, File.ReadAllLines(result.T0SummaryCsvPath).Length);

            string[] rows = File.ReadAllLines(result.ProcessingCsvPath);
            Equal(
                "LineName,LineOrder,LinePass,DateUtc,TimeUtc,LineTaskId,Longitude,Latitude,X,Y,AltitudeM,LaserAltitudeM,RawMagNt,BaseUsedNt,rigai,Tof,Inc,Dec,T0,dTgz",
                rows[0]);
            string[] first = rows[1].Split(',');
            Equal("", first[0]);
            Equal("", first[1]);
            Equal("", first[2]);
            Equal("2026-09-06", first[3]);
            Equal("06:51:00.000", first[4]);

            double longitude = double.Parse(first[6], CultureInfo.InvariantCulture);
            double latitude = double.Parse(first[7], CultureInfo.InvariantCulture);
            double x = double.Parse(first[8], CultureInfo.InvariantCulture);
            double y = double.Parse(first[9], CultureInfo.InvariantCulture);
            double raw = double.Parse(first[12], CultureInfo.InvariantCulture);
            double baseUsed = double.Parse(first[13], CultureInfo.InvariantCulture);
            double rigai = double.Parse(first[14], CultureInfo.InvariantCulture);
            double tof = double.Parse(first[15], CultureInfo.InvariantCulture);
            double inc = double.Parse(first[16], CultureInfo.InvariantCulture);
            double dec = double.Parse(first[17], CultureInfo.InvariantCulture);
            double t0 = double.Parse(first[18], CultureInfo.InvariantCulture);
            double dtgz = double.Parse(first[19], CultureInfo.InvariantCulture);

            double projectionCentralMeridian =
                result.PreLevel.ProjectionCentralMeridianDeg
                ?? throw new InvalidOperationException(
                    "pre-levelling output must have one shared projection central meridian");
            var projected = CoordinateService.GaussKrugerForward(
                longitude,
                latitude,
                projectionCentralMeridian,
                result.PreLevel.ProjectionDatum,
                result.PreLevel.ProjectionFalseEastingM,
                result.PreLevel.ProjectionFalseNorthingM);
            Equal(projected.Easting, x, 0.002);
            Equal(projected.Northing, y, 0.002);
            True(
                result.PreLevel.ProjectionCentralMeridianSource.Contains(
                    "AUTO_GK3",
                    StringComparison.Ordinal),
                "standalone processing must auto-select one GK3 central meridian when project CM is absent");

            Equal(raw - baseUsed, rigai, 0.002);
            Equal(rigai + t0 - tof, dtgz, 0.003);
            True(tof is > 50000d and < 70000d, "IGRF Tof magnitude is unreasonable");
            True(inc is > -90d and < 90d, "IGRF Inc must be an angle");
            True(dec is > -180d and < 180d, "IGRF Dec must be an angle");

            string[] secondDay = rows
                .Skip(1)
                .Select(x => x.Split(','))
                .First(x => x[3] == "2026-09-07");
            double secondDayT0 = double.Parse(secondDay[18], CultureInfo.InvariantCulture);
            True(
                Math.Abs(secondDayT0 - t0) > 100d,
                "AUTO_BY_UTC_DATE must write the corresponding UTC-day T0 into each row");

            string[] audit = ReadGzipLines(result.CorrectedCsvPath);
            string[] auditFirst = audit[1].Split(',');
            double auditRigai = double.Parse(auditFirst[8], CultureInfo.InvariantCulture);
            double auditDatumAdjusted = double.Parse(auditFirst[10], CultureInfo.InvariantCulture);
            Equal(rigai, auditRigai, 0.002);
            Equal(rigai + 59152.13, auditDatumAdjusted, 0.003);

            string explicitOutput = Path.Combine(root, "diurnal_prelevel_project_cm");
            DiurnalCorrectionExportResult explicitProjectionResult =
                new DiurnalCorrectionExportService().WriteNativeCorrection(
                    explicitOutput,
                    analysis,
                    new DiurnalCorrectionOptions(
                        DatumNt: 0d,
                        GemLinkTenthsCompatibility: true,
                        MaxInterpolationGapSeconds: 999d,
                        RejectInterpolationAcrossLargeGap: true),
                    LoadIgrfModels(),
                    new MagneticPreLevelProcessingOptions(
                        T0Mode: MagneticT0Mode.AutoByUtcDate,
                        ProjectionCentralMeridianDeg: 108d,
                        ProjectionCentralMeridianSource: "PROJECT_WORKSPACE: ROUTE_CONFIRMED"),
                     auditRequest: ReviewDraftRequest(analysis));
            Equal(108d,
                explicitProjectionResult.PreLevel.ProjectionCentralMeridianDeg
                ?? throw new InvalidOperationException("explicit project central meridian was lost"),
                0.000001);
            Equal(108d,
                explicitProjectionResult.PreLevel.ProcessingDataSuggestedCentralMeridianDeg
                ?? throw new InvalidOperationException("processing-data central-meridian suggestion missing"),
                0.000001);
            True(
                explicitProjectionResult.PreLevel.ProjectionCentralMeridianConsistent,
                "matching project/data central meridian must be marked consistent");
            string[] explicitFirst =
                File.ReadAllLines(explicitProjectionResult.ProcessingCsvPath)[1].Split(',');
            var explicitProjected = CoordinateService.GaussKrugerForward(
                longitude,
                latitude,
                108d,
                "CGCS2000",
                500000d,
                0d);
            Equal(
                explicitProjected.Easting,
                double.Parse(explicitFirst[8], CultureInfo.InvariantCulture),
                0.002);
            Equal(
                explicitProjected.Northing,
                double.Parse(explicitFirst[9], CultureInfo.InvariantCulture),
                0.002);

            DiurnalCorrectionExportResult staleProjectResult =
                new DiurnalCorrectionExportService().WriteNativeCorrection(
                    Path.Combine(root, "diurnal_prelevel_stale_project_cm"),
                    analysis,
                    new DiurnalCorrectionOptions(
                        DatumNt: 0d,
                        GemLinkTenthsCompatibility: true,
                        MaxInterpolationGapSeconds: 999d,
                        RejectInterpolationAcrossLargeGap: true),
                    LoadIgrfModels(),
                    new MagneticPreLevelProcessingOptions(
                        T0Mode: MagneticT0Mode.AutoByUtcDate,
                        ProjectionCentralMeridianDeg: 111d,
                        ProjectionCentralMeridianSource: "PROJECT_WORKSPACE: STALE"),
                     auditRequest: ReviewDraftRequest(analysis));
            Equal(
                108d,
                staleProjectResult.PreLevel.ProjectionCentralMeridianDeg
                    ?? throw new InvalidOperationException("auto GK3 central meridian was lost"),
                0.000001);
            True(
                !staleProjectResult.PreLevel.ProjectionCentralMeridianConsistent,
                "stale project/data central-meridian mismatch must remain auditable");
            True(
                staleProjectResult.PreLevel.ProjectionCentralMeridianSource.Contains(
                    "AUTO_GK3_FROM_FINAL_PROCESSING_DATA",
                    StringComparison.Ordinal),
                "final processing data must be the production projection authority");

            DiurnalCorrectionExportResult globalResult =
                new DiurnalCorrectionExportService().WriteNativeCorrection(
                    Path.Combine(root, "diurnal_prelevel_global"),
                    analysis,
                    new DiurnalCorrectionOptions(
                        DatumNt: 0d,
                        GemLinkTenthsCompatibility: true,
                        MaxInterpolationGapSeconds: 999d,
                        RejectInterpolationAcrossLargeGap: true),
                    LoadIgrfModels(),
                    new MagneticPreLevelProcessingOptions(
                        T0Mode: MagneticT0Mode.AutoGlobal),
                     auditRequest: ReviewDraftRequest(analysis));
            Equal("AUTO_GLOBAL_MEAN_CENTERED", globalResult.PreLevel.T0Mode);
            True(globalResult.PreLevel.FixedT0Nt.HasValue, "global automatic T0 must expose one fixed T0");
            True(
                globalResult.PreLevel.Days
                    .Where(x => x.T0Nt.HasValue)
                    .Select(x => x.T0Nt!.Value)
                    .Distinct()
                    .Count() == 1,
                "AUTO_GLOBAL must use one T0 across every UTC day");

            DiurnalCorrectionExportResult manualResult =
                new DiurnalCorrectionExportService().WriteNativeCorrection(
                    Path.Combine(root, "diurnal_prelevel_manual"),
                    analysis,
                    new DiurnalCorrectionOptions(
                        DatumNt: 0d,
                        GemLinkTenthsCompatibility: true,
                        MaxInterpolationGapSeconds: 999d,
                        RejectInterpolationAcrossLargeGap: true),
                    LoadIgrfModels(),
                    new MagneticPreLevelProcessingOptions(
                        T0Mode: MagneticT0Mode.ManualFixed,
                        ManualT0Nt: 59255d),
                     auditRequest: ReviewDraftRequest(analysis));
            Equal("MANUAL_FIXED", manualResult.PreLevel.T0Mode);
            Equal(59255d,
                manualResult.PreLevel.FixedT0Nt
                ?? throw new InvalidOperationException("manual fixed T0 was lost"),
                0.000001);
            string[] manualFirst =
                File.ReadAllLines(manualResult.ProcessingCsvPath)[1].Split(',');
            Equal(59255d, double.Parse(manualFirst[18], CultureInfo.InvariantCulture), 0.001);
            Equal(
                double.Parse(manualFirst[14], CultureInfo.InvariantCulture) + 59255d -
                double.Parse(manualFirst[15], CultureInfo.InvariantCulture),
                double.Parse(manualFirst[19], CultureInfo.InvariantCulture),
                0.003);
        });
    }

    private static void TestMagneticCsvUtcDateTimeColumns()
    {
        WithTempRoot(root =>
        {
            string folder =
                CreateFieldMagFixture(
                    root);
            FieldMagFolderAnalysis analysis =
                new FieldMagDataService()
                    .AnalyzeFolder(
                        folder);

            string integratedDirectory =
                Path.Combine(
                    root,
                    "integrated");
            new FieldMagDataService()
                .WriteIntegratedCanonicalOutputs(
                    integratedDirectory,
                    analysis);

            string[] uav =
                ReadGzipLines(
                    Path.Combine(
                        integratedDirectory,
                        "UAV磁测_整合.csv.gz"));
            string[] baseRows =
                ReadGzipLines(
                    Path.Combine(
                        integratedDirectory,
                        "GSM19T_日变站_整合.csv.gz"));

            True(
                uav[0].StartsWith(
                    "DateUtc,TimeUtc,",
                    StringComparison.Ordinal),
                "UAV complete output must split UTC date and time");
            True(
                baseRows[0].StartsWith(
                    "DateUtc,TimeUtc,",
                    StringComparison.Ordinal),
                "GSM19T complete output must split UTC date and time");
            True(
                !uav[0].Contains(
                    "TimestampUtc",
                    StringComparison.Ordinal),
                "combined TimestampUtc column must not remain in UAV output");
            True(
                !baseRows[0].Contains(
                    "TimestampUtc",
                    StringComparison.Ordinal),
                "combined TimestampUtc column must not remain in Base output");

            DiurnalCorrectionExportResult diurnal =
                new DiurnalCorrectionExportService()
                    .WriteNativeCorrection(
                        Path.Combine(
                            root,
                            "diurnal"),
                        analysis,
                        new DiurnalCorrectionOptions(),
                        LoadIgrfModels(),
                        new MagneticPreLevelProcessingOptions(),
                     auditRequest: ReviewDraftRequest(analysis));

            string[] audit =
                ReadGzipLines(
                    diurnal.CorrectedCsvPath);
            string[] processing =
                File.ReadAllLines(
                    diurnal.ProcessingCsvPath);

            True(
                audit[0].StartsWith(
                    "DateUtc,TimeUtc,",
                    StringComparison.Ordinal),
                "native diurnal audit output must split UTC date and time");
            True(
                processing[0].StartsWith(
                    "LineName,LineOrder,LinePass,DateUtc,TimeUtc,",
                    StringComparison.Ordinal),
                "processing CSV must split UTC date and time");

            string[] firstColumns =
                processing[1].Split(',');
            Equal(
                "2026-09-09",
                firstColumns[3]);
            True(
                firstColumns[4].Contains(
                    ":",
                    StringComparison.Ordinal),
                "TimeUtc must be a standalone clock-time field");
        });
    }

    private static string CreateFieldMagFixture(string root)
    {
        string folder = Path.Combine(root, "0909");
        Directory.CreateDirectory(folder);

        string uavRaw = Path.Combine(folder, "uav_raw_01.txt");
        File.WriteAllText(
            uavRaw,
            "# QuSpin's total-field magnetometer-5Hz\n" +
            "UTCData,UTCTime,Longitude,Latitude,Altitude,Mag,Signal,GpsStatus,Satellites,SensorStatus\n" +
            // Three records deliberately share one physical line. The parser must recover by timestamp record boundaries.
            "2026-09-08,23:59:59.800,116.000000,39.900000,100,58900.0,20,3D,15,T" +
            "2026-09-09,00:00:00.000,116.000010,39.900010,101,58901.0,21,3D,15,T" +
            "2026-09-09,00:00:00.200,116.000020,39.900020,102,58902.0,22,3D,15,F\n",
            new UTF8Encoding(false));

        File.WriteAllText(
            Path.Combine(folder, "after_allT.txt"),
            "# QuSpin's total-field magnetometer-5Hz\n" +
            "UTCData,UTCTime,Longitude,Latitude,Altitude,Mag,Signal,GpsStatus,Satellites,SensorStatus\n" +
            "2026-09-09,00:00:00.400,116.000030,39.900030,103,59099.0,22,3D,15,T\n",
            new UTF8Encoding(false));

        File.WriteAllText(
            Path.Combine(folder, "11.txt"),
            "/Gem Systems GSM-19T\n/ID 09/08/26\n/time nT sq\n235950.0 59100.00 99\n",
            new UTF8Encoding(false));
        File.WriteAllText(
            Path.Combine(folder, "12.txt"),
            "/Gem Systems GSM-19T\n/ID 09/09/26\n/time nT sq\n" +
            "000000.0 59101.00 99\n" +
            "050000.0 59102.00 99\n" +
            "070000.0 58460.00 79\n" +
            "070010.0 59140.00 99\n",
            new UTF8Encoding(false));

        File.WriteAllText(
            Path.Combine(folder, "affter_19T.txt"),
            "/Gem Systems GSM-19T\n/ID 09/09/26\n/time nT sq\n" +
            "080000.0 59999.00 99\n",
            new UTF8Encoding(false));

        File.WriteAllText(
            Path.Combine(folder, "0909_rigaiAll.txt"),
            "2026-09-09,00:00:00.000,58901.0,-100.0,i010\n" +
            "2026-09-09,00:00:00.200,58902.0,-99.0,i+++\n",
            new UTF8Encoding(false));
        return folder;
    }

    private static void TestEquipmentChecklist()
    {
        WithTempRoot(root =>
        {
            EquipmentCatalog defaults =
                EquipmentCatalogService.Defaults();

            True(
                defaults.ModelsByCategory.TryGetValue(
                    "无人机",
                    out List<string>? droneModels),
                "drone model catalog missing");
            Equal(2, droneModels!.Count);
            Equal(
                EquipmentCatalogService.DjiMatrice400,
                droneModels[0]);
            Equal(
                EquipmentCatalogService.DjiMatrice350Rtk,
                droneModels[1]);
            True(
                !droneModels.Contains(
                    "DJ400",
                    StringComparer.OrdinalIgnoreCase),
                "legacy DJ400 alias should not appear in the model dropdown");
            True(
                !droneModels.Contains(
                    "DJ350",
                    StringComparer.OrdinalIgnoreCase),
                "legacy DJ350 alias should not appear in the model dropdown");

            EquipmentChecklistTemplate? m400 =
                EquipmentTemplates.Find("DJ400");
            True(m400 is not null, "M400 template missing");
            Equal(19, m400!.Items.Count);
            Equal(
                12,
                m400.Items.Count(x =>
                    x.Requirement ==
                    EquipmentTemplates.Required));
            Equal(
                7,
                m400.Items.Count(x =>
                    x.Requirement ==
                    EquipmentTemplates.Optional));

            True(
                m400.Items.Any(x =>
                    x.ItemId ==
                    "m400_2510f_propellers"),
                "official M400 2510F propeller item missing");
            True(
                m400.Items.All(x =>
                    x.ItemId !=
                    "pump_box_bracket"),
                "standalone magnetic-sensor metal bracket returned");
            True(
                m400.Items.All(x =>
                    x.ItemId !=
                    "replacement_screws"),
                "duplicate replacement-screws item returned");
            True(
                m400.Items.All(x =>
                    x.ItemId !=
                    "export_storage"),
                "non-official export-storage item returned");
            True(
                m400.Items.All(x =>
                    x.ItemId !=
                    "power_cable"),
                "BS100 power cable should remain an accessory note, not a separate M400 item");

            EquipmentChecklistItem m400Aircraft =
                m400.Items.Single(x =>
                    x.ItemId == "aircraft");
            True(
                m400Aircraft.Check.Contains(
                    "航磁仪器金属架"),
                "M400 aircraft note lost the first-install magnetic bracket rule");

            EquipmentChecklistItem lidarCover =
                m400.Items.Single(x =>
                    x.ItemId == "lidar_cover");
            True(
                lidarCover.Check.Contains(
                    "飞行前确认保护罩已取下"),
                "rotating LiDAR cover pre-flight removal check missing");

            EquipmentChecklistItem m400Tools =
                m400.Items.Single(x =>
                    x.ItemId == "screwdriver");
            Equal(
                "维修工具套装（Screws and Tools）",
                m400Tools.Name);

            EquipmentChecklistItem tb100 =
                m400.Items.Single(x =>
                    x.ItemId == "tb100");
            Equal(3, tb100.DefaultQuantity);
            Equal(1, tb100.MinQuantity);

            EquipmentChecklistTemplate? gsm =
                EquipmentTemplates.Find("GSM-19T");
            True(gsm is not null, "GSM-19T template missing");
            Equal(11, gsm!.Items.Count);
            Equal(
                8,
                gsm.Items.Count(x =>
                    x.Requirement ==
                    EquipmentTemplates.Required));
            Equal(
                3,
                gsm.Items.Count(x =>
                    x.Requirement ==
                    EquipmentTemplates.Optional));
            True(
                gsm.Items.All(x =>
                    x.ItemId != "battery"),
                "GSM-19T internal battery must not remain a separate checklist item");
            EquipmentChecklistItem gsmConsole =
                gsm.Items.Single(x =>
                    x.ItemId == "console");
            True(
                gsmConsole.Check.Contains("内置电池"),
                "GSM-19T internal battery status must be folded into the console check");

            EquipmentChecklistTemplate? m350 =
                EquipmentTemplates.Find("DJ350");
            True(m350 is not null, "M350 RTK alias did not resolve");
            Equal(
                "dji-matrice-350-rtk",
                m350!.TemplateId);
            Equal(15, m350.Items.Count);
            Equal(
                9,
                m350.Items.Count(x =>
                    x.Requirement ==
                    EquipmentTemplates.Required));
            Equal(
                6,
                m350.Items.Count(x =>
                    x.Requirement ==
                    EquipmentTemplates.Optional));
            True(
                m350.Items.Any(x =>
                    x.ItemId ==
                    "m350_2110s_propeller"),
                "official M350 2110s propeller item missing");
            True(
                m350.Items.All(x =>
                    x.ItemId !=
                    "m350_paired_battery_sticker"),
                "M350 paired-battery sticker must not remain a separate checklist item");

            EquipmentChecklistItem tb65 =
                m350.Items.Single(x =>
                    x.ItemId == "m350_tb65");
            Equal(2, tb65.DefaultQuantity);
            Equal(2, tb65.MinQuantity);
            Equal("块", tb65.QuantityUnit);
            True(
                tb65.Check.Contains("电池配组贴纸"),
                "M350 paired-battery sticker check must be folded into the TB65 row");

            EquipmentChecklistItem m350Power =
                m350.Items.Single(x =>
                    x.ItemId == "m350_power_cable");
            Equal(
                "按销售地区实物",
                m350Power.Expected);

            var store = new JsonStore();
            var ws = new WorkspaceService(store);
            ws.Initialize(root, "Equipment Test");
            var field = new FieldworkService(store, ws);

            EquipmentRecord eq = field.AddEquipment(
                root,
                EquipmentCatalogService.DjiMatrice400,
                "M400-01");

            ChecklistView view = field.GetChecklist(
                root,
                eq.RecordId);
            True(
                view.Available,
                "M400 checklist unavailable");
            Equal(12, view.RequiredTotal);
            Equal(7, view.OptionalTotal);

            string[] required = view.Items
                .Where(x =>
                    x.Item.Requirement ==
                    EquipmentTemplates.Required)
                .Select(x =>
                    x.Item.ItemId)
                .ToArray();

            Dictionary<string, int> quantities =
                view.Items
                    .Where(x =>
                        x.Item.QuantityEditable)
                    .ToDictionary(
                        x => x.Item.ItemId,
                        x => x.Item.DefaultQuantity);

            view = field.SaveChecklist(
                root,
                eq.RecordId,
                required,
                quantities);
            Equal("PASSED", view.Status);

            ChecklistView reopenedChecklist = field.GetChecklist(
                root,
                eq.RecordId);
            Equal("PASSED", reopenedChecklist.Status);
            Equal(
                required.Length,
                reopenedChecklist.Items.Count(x => x.Checked));

            IReadOnlyList<EquipmentTestDefinition> aircraftTests =
                EquipmentTestProfiles.For(view.Equipment);
            True(
                aircraftTests.All(x =>
                    !x.TestType.Contains("数据记录", StringComparison.Ordinal) &&
                    !x.TestType.Contains("数据导出", StringComparison.Ordinal) &&
                    !x.TestType.Contains("存储", StringComparison.Ordinal)),
                "aircraft test profile must test flight functions instead of data-recording functions");

            foreach (EquipmentTestDefinition definition in aircraftTests)
            {
                field.RecordEquipmentTest(
                    root,
                    eq.RecordId,
                    definition.TestType,
                    "PASSED",
                    "回归测试");
            }

            EquipmentRecord loaded =
                field.ListEquipment(root).Single();
            Equal("PASSED", loaded.ChecklistStatus);
            Equal("PASSED", loaded.TestStatus);
            Equal("PASSED", loaded.ReleaseStatus);

            List<EquipmentTestRecord> latestTests = field.ListEquipmentTests(
                root,
                eq.RecordId);
            Equal(
                aircraftTests.Count,
                latestTests.Count);
            string testReport = Path.Combine(root, "equipment_test_summary.md");
            FieldworkDocumentExportService.WriteEquipmentTestSummary(
                testReport,
                loaded,
                latestTests);
            string testReportText = File.ReadAllText(testReport);
            True(
                aircraftTests.All(definition =>
                    testReportText.Contains(definition.TestType, StringComparison.Ordinal)),
                "equipment test table report lost one or more test types");

            // A historical DJ350 equipment record must still resolve to the
            // new official Matrice 350 RTK template.
            EquipmentRecord legacyM350 =
                field.AddEquipment(
                    root,
                    "DJ350",
                    "M350-LEGACY");
            ChecklistView legacyM350View =
                field.GetChecklist(
                    root,
                    legacyM350.RecordId);
            True(
                legacyM350View.Available,
                "legacy DJ350 equipment lost its M350 RTK checklist");
            Equal(9, legacyM350View.RequiredTotal);
            Equal(6, legacyM350View.OptionalTotal);
        });
    }

    private static void TestGpamEquipmentCatalogAndChecklist()
    {
        WithTempRoot(root =>
        {
            EquipmentCatalog defaults = EquipmentCatalogService.Defaults();
            True(
                defaults.Categories.Contains(
                    EquipmentCatalogService.MagnetometerSensorCategory),
                "磁测探头 category missing");
            True(
                !defaults.Categories.Contains(
                    EquipmentCatalogService.LegacyPumpCategory),
                "legacy 光泵探头 category should not remain in defaults");
            True(
                !defaults.Categories.Contains("总场磁力仪"),
                "总场磁力仪 should not remain in equipment registration categories");
            True(
                !defaults.Categories.Contains("采集器"),
                "采集器 should not remain in equipment registration categories");

            True(
                defaults.ModelsByCategory.TryGetValue(
                    EquipmentCatalogService.MagnetometerSensorCategory,
                    out List<string>? sensorModels),
                "磁测探头 model list missing");
            Equal(1, sensorModels!.Count);
            Equal(EquipmentCatalogService.GpamM350Rm, sensorModels[0]);

            EquipmentChecklistTemplate? template =
                EquipmentTemplates.Find(
                    EquipmentCatalogService.GpamM350Rm);
            True(
                template is not null,
                "GPAM-M350-RM checklist template missing");

            EquipmentRecord gpamProfileEquipment = new()
            {
                Model = EquipmentCatalogService.GpamM350Rm,
                Category = EquipmentCatalogService.MagnetometerSensorCategory
            };
            IReadOnlyList<EquipmentTestDefinition> gpamTests =
                EquipmentTestProfiles.For(gpamProfileEquipment);
            True(
                gpamTests.Any(x => x.TestType == "数据记录") &&
                gpamTests.Any(x => x.TestType == "数据导出"),
                "GPAM test profile must keep actual acquisition/data-recording functions");

            Equal(17, template!.Items.Count);
            Equal(
                8,
                template.Items.Count(x =>
                    x.Requirement == EquipmentTemplates.Required));
            Equal(
                9,
                template.Items.Count(x =>
                    x.Requirement == EquipmentTemplates.Optional));

            string[] requiredIds =
            [
                "gpam_probe_assembly",
                "gpam_carbon_extension",
                "gpam_data_acquisition_box",
                "gpam_bluetooth_terminal",
                "gpam_probe_signal_cable",
                "gpam_power_cable",
                "gpam_green_clips",
                "gpam_transport_case"
            ];
            string[] optionalIds =
            [
                "gpam_safe_battery",
                "gpam_battery_charger",
                "gpam_dc_adapter",
                "gpam_usb_cable",
                "gpam_mount_clamps",
                "gpam_strap",
                "gpam_spare_fasteners",
                "gpam_hex_tool",
                "gpam_case_keys"
            ];

            foreach (string id in requiredIds)
            {
                Equal(
                    EquipmentTemplates.Required,
                    template.Items.Single(x =>
                        x.ItemId == id).Requirement);
            }

            foreach (string id in optionalIds)
            {
                Equal(
                    EquipmentTemplates.Optional,
                    template.Items.Single(x =>
                        x.ItemId == id).Requirement);
            }

            string[] removedIds =
            [
                "gpam_radio_module",
                "gpam_pads",
                "gpam_spare_parts_bag"
            ];
            foreach (string id in removedIds)
            {
                True(
                    template.Items.All(x => x.ItemId != id),
                    $"removed GPAM item survived: {id}");
            }

            EquipmentChecklistItem dataBox = template.Items
                .Single(x =>
                    x.ItemId == "gpam_data_acquisition_box");
            Equal("数据盒子", dataBox.Category);
            Equal("数据采集器", dataBox.Name);
            True(
                dataBox.Check.Contains("GPS/北斗"),
                "GPAM data-acquisition functional check is missing GNSS verification");

            EquipmentChecklistItem probeCable = template.Items
                .Single(x =>
                    x.ItemId == "gpam_probe_signal_cable");
            Equal("探头延长线", probeCable.Category);

            EquipmentChecklistItem greenClips = template.Items
                .Single(x =>
                    x.ItemId == "gpam_green_clips");
            Equal("塑料卡扣", greenClips.Category);
            Equal("管夹/限位卡扣", greenClips.Name);

            True(
                template.Items.All(x =>
                    !x.Category.Contains(
                        "数传电台",
                        StringComparison.Ordinal) &&
                    !x.Category.Contains(
                        "泡棉",
                        StringComparison.Ordinal) &&
                    !x.Category.Equals(
                        "备件包",
                        StringComparison.Ordinal)),
                "removed GPAM generic types survived");

            EquipmentChecklistItem probe = template.Items
                .Single(x =>
                    x.ItemId == "gpam_probe_assembly");
            Equal("探头与结构件", probe.Section);
            Equal("磁力仪探头/航磁探杆", probe.Category);
            Equal("航磁光泵探头总成", probe.Name);
            True(
                probe.Check.Contains("航空插头针脚"),
                "probe checklist content missing");

            EquipmentChecklistItem battery = template.Items
                .Single(x =>
                    x.ItemId == "gpam_safe_battery");
            Equal(2, battery.DefaultQuantity);
            Equal(2, battery.MinQuantity);
            Equal("块", battery.QuantityUnit);
            Equal(
                EquipmentTemplates.Optional,
                battery.Requirement);

            True(
                template.Items.All(x => x.QuantityEditable),
                "all current GPAM items should expose quantity spinners");

            var store = new JsonStore();
            var ws = new WorkspaceService(store);
            ws.Initialize(root, "GPAM Equipment Test");
            var field = new FieldworkService(store, ws);

            EquipmentRecord equipment = field.AddEquipment(
                root,
                EquipmentCatalogService.GpamM350Rm,
                "GPAM-01",
                equipmentType: "航磁磁测探头",
                category:
                    EquipmentCatalogService.MagnetometerSensorCategory);

            ChecklistView view = field.GetChecklist(
                root,
                equipment.RecordId);
            True(
                view.Available,
                "GPAM checklist should be available");
            Equal(8, view.RequiredTotal);
            Equal(9, view.OptionalTotal);

            string[] checkedRequired = view.Items
                .Where(x =>
                    x.Item.Requirement ==
                    EquipmentTemplates.Required)
                .Select(x => x.Item.ItemId)
                .ToArray();
            Dictionary<string, int> quantities = view.Items
                .Where(x => x.Item.QuantityEditable)
                .ToDictionary(
                    x => x.Item.ItemId,
                    x => x.Item.DefaultQuantity);

            view = field.SaveChecklist(
                root,
                equipment.RecordId,
                checkedRequired,
                quantities);
            Equal("PASSED", view.Status);
            Equal(8, view.RequiredChecked);
            Equal(0, view.OptionalChecked);

            string checklistCsv =
                Path.Combine(root, "gpam_checklist.csv");
            field.ExportEquipmentChecklistCsv(
                root,
                equipment.RecordId,
                checklistCsv);
            string exportedChecklist =
                File.ReadAllText(checklistCsv);

            True(
                exportedChecklist.StartsWith(
                    "设备型号,设备编号,步骤,分区,类型,名称,必备/可选,数量,核验/测试状态,清点内容/测试说明,记录时间",
                    StringComparison.Ordinal),
                "equipment checklist CSV business columns are outdated");
            True(
                exportedChecklist.Contains("数据盒子"),
                "renamed GPAM data-box type missing from checklist export");
            True(
                exportedChecklist.Contains("探头延长线"),
                "renamed GPAM probe-extension type missing from checklist export");
            True(
                !exportedChecklist.Contains("数传电台"),
                "removed radio item survived checklist export");
            True(
                !exportedChecklist.Contains("泡棉"),
                "removed pad item survived checklist export");
            True(
                !exportedChecklist.Contains("零星备用件包"),
                "removed spare-parts bag survived checklist export");

            string legacyCatalogPath =
                ProjectPaths.Json(
                    root,
                    "equipment_catalog.json");
            store.Write(
                legacyCatalogPath,
                new EquipmentCatalog
                {
                    Categories =
                    [
                        EquipmentCatalogService.LegacyPumpCategory,
                        "无人机",
                        "总场磁力仪",
                        "采集器"
                    ],
                    ModelsByCategory =
                        new Dictionary<string, List<string>>
                        {
                            [EquipmentCatalogService.LegacyPumpCategory] =
                            [
                                "固定型号光泵探头（待补录具体型号）"
                            ],
                            ["无人机"] =
                            [
                                "DJ400",
                                "DJ350",
                                "M350 RTK"
                            ],
                            ["总场磁力仪"] = ["Legacy Total Field"],
                            ["采集器"] = ["Legacy Logger"]
                        }
                });

            var catalogService =
                new EquipmentCatalogService(store);
            EquipmentCatalog migrated =
                catalogService.Load(root);

            True(
                migrated.Categories.Contains(
                    EquipmentCatalogService.MagnetometerSensorCategory),
                "legacy category was not normalized to 磁测探头");
            True(
                !migrated.Categories.Contains(
                    EquipmentCatalogService.LegacyPumpCategory),
                "legacy 光泵探头 category survived normalization");
            True(
                !migrated.Categories.Contains("总场磁力仪"),
                "hidden 总场磁力仪 category survived old catalog load");
            True(
                !migrated.Categories.Contains("采集器"),
                "hidden 采集器 category survived old catalog load");

            True(
                migrated.ModelsByCategory[
                    EquipmentCatalogService.MagnetometerSensorCategory]
                    .Contains(EquipmentCatalogService.GpamM350Rm),
                "GPAM model missing after legacy catalog normalization");
            True(
                !migrated.ModelsByCategory[
                    EquipmentCatalogService.MagnetometerSensorCategory]
                    .Contains("固定型号光泵探头（待补录具体型号）"),
                "legacy placeholder model survived normalization");

            List<string> migratedDrones =
                migrated.ModelsByCategory["无人机"];
            True(
                migratedDrones.Contains(
                    EquipmentCatalogService.DjiMatrice400),
                "official M400 model missing after catalog migration");
            True(
                migratedDrones.Contains(
                    EquipmentCatalogService.DjiMatrice350Rtk),
                "official M350 RTK model missing after catalog migration");
            True(
                !migratedDrones.Contains(
                    "DJ400",
                    StringComparer.OrdinalIgnoreCase),
                "legacy DJ400 alias survived catalog normalization");
            True(
                !migratedDrones.Contains(
                    "DJ350",
                    StringComparer.OrdinalIgnoreCase),
                "legacy DJ350 alias survived catalog normalization");

            Throws<InvalidOperationException>(() =>
                catalogService.AddModel(
                    root,
                    "采集器",
                    "Should Not Be Added"));
        });
    }

    private static void TestCoordinateRegistry()
    {
        WithTempRoot(root =>
        {
            var store = new JsonStore();
            var ws = new WorkspaceService(store);
            ws.Initialize(root, "Coordinate Registry Test");
            var registry = new CoordinateRegistryService(store, ws);

            registry.RegisterCrs(root, new CrsDefinitionRecord
            {
                Name = "CGCS2000-GK3-117",
                Kind = "projected",
                Datum = "CGCS2000",
                CentralMeridian = 117,
                FalseEastingM = 500000,
                FalseNorthingM = 0,
                Unit = "metre",
                AxisOrder = "x,y"
            });
            Equal(1, registry.CrsList(root).Count);

            string csv = Path.Combine(root, "coords.csv");
            File.WriteAllText(csv,
                "coordinate_id,x_m,y_m,z_m,notes\n" +
                "P001,500000,4419000,100.5,projected\n",
                new UTF8Encoding(true));

            var rows = registry.ReadCoordinateCsv(csv, "UTM 50", "terrain_elevation_m", "coords.csv");
            Equal(1, rows.Count);
            Equal("P001", rows[0].CoordinateId);
            Equal(500000d, rows[0].XM!.Value, 1e-12);
            Equal(4419000d, rows[0].YM!.Value, 1e-12);
            Equal("x,y", rows[0].AxisOrder);

            registry.Register(root, rows, "coords.csv");
            var summary = registry.Summary(root);
            Equal(1, summary.Count);
            Equal(0, summary.MissingCrs);
            True(summary.ByHeightType["terrain_elevation_m"] == 1, "height type summary");

            Throws<InvalidOperationException>(() => registry.Register(root, rows, "coords.csv"));

            string exported = Path.Combine(root, "coords_export.csv");
            registry.WriteCoordinateCsv(exported, registry.List(root));
            True(File.Exists(exported), "coordinate CSV export missing");
            True(File.ReadAllText(exported).Contains("x_m"), "coordinate CSV header not compatible");
        });
    }

    private static void TestOasisPlyAndProjectCentralMeridian()
    {
        WithTempRoot(root =>
        {
            BatchConversionResult batch = CoordinateService.ConvertBatch(
                "116.25,39.9\n116.30,39.91",
                CoordinateInputMode.DecimalDegrees,
                new ProjectionParameters(
                    CentralMeridian: 117,
                    AxisOrder: XyAxisOrder.EastingNorthing),
                autoCentralMeridian: false);

            string output = Path.Combine(
                root,
                "10_工区",
                "A区",
                "04_航线设计与任务文件",
                "01_PLY工区边界",
                "boundary.ply");

            var store = new JsonStore();
            var ws = new WorkspaceService(store);
            ws.Initialize(root, "PLY Test");

            var registry = new CoordinateRegistryService(store, ws);
            registry.ExportCleanXyPly(output, batch);

            byte[] bytes = File.ReadAllBytes(output);
            True(
                bytes.Length >= 3 &&
                !(bytes[0] == 0xEF &&
                  bytes[1] == 0xBB &&
                  bytes[2] == 0xBF),
                "Oasis PLY must be UTF-8 without BOM");

            string[] lines = File.ReadAllLines(output);
            Equal(2, lines.Length);
            True(
                lines.All(line => line.Count(ch => ch == '\t') == 1),
                "each PLY line must contain exactly one TAB separator");
            True(
                lines.All(line => !line.Contains(',')),
                "PLY output must not contain CSV commas");
            True(
                lines.All(line =>
                    line.Split('\t').Length == 2 &&
                    double.TryParse(
                        line.Split('\t')[0],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out _) &&
                    double.TryParse(
                        line.Split('\t')[1],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out _)),
                "PLY output must contain clean numeric X/Y only");

            WorkspaceRecord loaded = ws.GetWorkspace(root);
            True(
                !loaded.CentralMeridian.HasValue,
                "coordinate/Oasis PLY export must not mutate project central meridian");

            WorkspaceRecord confirmed = ws.ConfirmProjectCentralMeridian(
                root,
                117d,
                "ROUTE_CONFIRMED: boundary-route.csv");
            Equal(117.0, confirmed.CentralMeridian!.Value, 1e-12);
            True(
                confirmed.CentralMeridianSource.StartsWith(
                    "ROUTE_CONFIRMED",
                    StringComparison.Ordinal),
                "project central-meridian provenance must come from explicit route confirmation");
            True(
                !string.IsNullOrWhiteSpace(confirmed.CentralMeridianConfirmedAt),
                "project central-meridian confirmation time missing");

            // Reopen the persisted project, then restore the previous route cache.
            // Matching loads reuse confirmation; stale automatic cache yields to
            // project authority, while a manual conflicting input still needs review.
            WorkspaceRecord reopened = ws.GetWorkspace(root);
            string persistedBeforeReuse = File.ReadAllText(ProjectPaths.Project(root));
            Equal(117d, RouteCentralMeridianPolicy.ReuseForRouteLoad(reopened, "117", false)!.Value, 1e-12);
            Equal(117d, RouteCentralMeridianPolicy.ReuseForRouteLoad(reopened, "114", true)!.Value, 1e-12);
            Equal(117d, RouteCentralMeridianPolicy.ReuseForRouteLoad(reopened, "", false)!.Value, 1e-12);
            True(RouteCentralMeridianPolicy.ReuseForRouteLoad(reopened, "114", false) is null,
                "manual route conflicts must still require explicit confirmation");
            True(RouteCentralMeridianPolicy.ReuseForRouteLoad(reopened, "NaN", false) is null,
                "invalid manual route values cannot silently reuse confirmation");
            Equal(persistedBeforeReuse, File.ReadAllText(ProjectPaths.Project(root)));
            True(RouteCentralMeridianPolicy.ReuseForRouteLoad(
                new WorkspaceRecord { CentralMeridian = 117 }, "117", true) is null,
                "a numeric cache without confirmation provenance is not project authority");

            File.WriteAllText(
                ProjectPaths.Project(root),
                File.ReadAllText(ProjectPaths.Project(root))
                    .Replace(
                        "ROUTE_CONFIRMED: boundary-route.csv",
                        "Oasis PLY：10_工区/A区/04_航线设计与任务文件/01_PLY工区边界/boundary.ply"));
            WorkspaceRecord invalidated =
                ws.InvalidateLegacyCoordinateDerivedCentralMeridian(root);
            True(
                !invalidated.CentralMeridian.HasValue,
                "legacy coordinate-derived project central meridian must be invalidated");
            True(RouteCentralMeridianPolicy.ReuseForRouteLoad(invalidated, "117", true) is null,
                "invalidated coordinate-derived meridians must never be reused by startup restoration");
            True(
                invalidated.CentralMeridianSource.StartsWith(
                    "LEGACY_COORDINATE_DERIVED_INVALIDATED",
                    StringComparison.Ordinal),
                "legacy invalidation provenance missing");

            // Golden regression from the user-supplied Oasis sample
            // 1/XY范围.ply: spacing=100 m, azimuth=0°, initial L1, increment=1.
            // Production rule: the first positive-length south/west line is L1
            // regardless of PLY vertex order. Parity-only outward extension is
            // frozen until passable-road / field-access calculation is added.
            string oasisSample = Path.Combine(root, "oasis-reference-utf16.ply");
            File.WriteAllText(
                oasisSample,
                "577165.177\t4663732.037\r\n" +
                "577187.109\t4661722.941\r\n" +
                "580130.699\t4661755.681\r\n" +
                "580107.93\t4663764.779\r\n" +
                "577165.177\t4663732.037\r\n",
                Encoding.Unicode);

            var survey = new PlySurveyLineService();
            SurveyLinePlanResult surveyPlan = survey.Plan(
                oasisSample,
                new SurveyLinePlanOptions
                {
                    SpacingM = 100,
                    AzimuthDegrees = 0,
                    InitialLineNumber = 1,
                    LineNumberIncrement = 1
                });

            // v0.9.2 production rollback: odd-grid outward completion is
            // frozen until a passable-road / field-access calculation exists.
            // Default planning therefore keeps only the lines required by the
            // actual workarea and never enlarges the survey grid for parity.
            Equal(29, surveyPlan.Lines.Count);
            Equal(1, surveyPlan.Lines[0].LineNumber);
            Equal(29, surveyPlan.Lines[^1].LineNumber);
            Equal(0, surveyPlan.CoverageExtensionLineCount);
            True(
                surveyPlan.Lines.All(line => !line.IsCoverageExtension),
                "production mother grid must not contain parity-extension lines while the feature is frozen");
            Equal(0, surveyPlan.DisconnectedLineCount);
            Equal(58, surveyPlan.EndpointCount);

            SurveyLineSegment firstSegment = surveyPlan.Lines[0].Segments.Single();
            Equal(577265.177, firstSegment.Start.X, 0.001);
            Equal(4661723.809, firstSegment.Start.Y, 0.01);
            Equal(577265.177, firstSegment.End.X, 0.001);
            Equal(4663733.149, firstSegment.End.Y, 0.01);

            SurveyLineSegment lastOriginalSegment = surveyPlan.Lines[^1].Segments.Single();
            Equal(580065.177, lastOriginalSegment.Start.X, 0.001);
            Equal(4661754.953, lastOriginalSegment.Start.Y, 0.01);
            Equal(580065.177, lastOriginalSegment.End.X, 0.001);
            Equal(4663764.303, lastOriginalSegment.End.Y, 0.01);

            // Keep the former additive implementation behind an explicit flag
            // only, so future road-access redesign can reuse it without letting
            // the Desktop production workflow accidentally enable it today.
            SurveyLinePlanResult experimentalExtensionPlan = survey.Plan(
                oasisSample,
                new SurveyLinePlanOptions
                {
                    SpacingM = 100,
                    AzimuthDegrees = 0,
                    InitialLineNumber = 1,
                    LineNumberIncrement = 1,
                    ExtendForEvenSnakeCompletion = true
                });
            Equal(30, experimentalExtensionPlan.Lines.Count);
            Equal(1, experimentalExtensionPlan.CoverageExtensionLineCount);
            True(
                experimentalExtensionPlan.Lines[^1].IsCoverageExtension,
                "explicit experimental parity extension should remain additive while frozen from production");
            for (int i = 0; i < surveyPlan.Lines.Count; i++)
            {
                Equal(surveyPlan.Lines[i].LineNumber, experimentalExtensionPlan.Lines[i].LineNumber);
                Equal(surveyPlan.Lines[i].Segments.Count, experimentalExtensionPlan.Lines[i].Segments.Count);
                for (int j = 0; j < surveyPlan.Lines[i].Segments.Count; j++)
                {
                    SurveyLineSegment expected = surveyPlan.Lines[i].Segments[j];
                    SurveyLineSegment actual = experimentalExtensionPlan.Lines[i].Segments[j];
                    Equal(expected.Start.X, actual.Start.X, 0.001);
                    Equal(expected.Start.Y, actual.Start.Y, 0.001);
                    Equal(expected.End.X, actual.End.X, 0.001);
                    Equal(expected.End.Y, actual.End.Y, 0.001);
                }
            }

            // Reversing and rotating the exact same polygon must preserve both
            // the mother line numbers and their geometry.
            string reorderedSample = Path.Combine(root, "oasis-reference-reordered.ply");
            File.WriteAllText(
                reorderedSample,
                "580130.699\t4661755.681\n" +
                "577187.109\t4661722.941\n" +
                "577165.177\t4663732.037\n" +
                "580107.93\t4663764.779\n" +
                "580130.699\t4661755.681\n",
                new UTF8Encoding(false));
            SurveyLinePlanResult reorderedPlan = survey.Plan(
                reorderedSample,
                new SurveyLinePlanOptions
                {
                    SpacingM = 100,
                    AzimuthDegrees = 0,
                    InitialLineNumber = 1,
                    LineNumberIncrement = 1
                });
            Equal(surveyPlan.Lines.Count, reorderedPlan.Lines.Count);
            Equal(surveyPlan.CoverageExtensionLineCount, reorderedPlan.CoverageExtensionLineCount);
            for (int i = 0; i < surveyPlan.Lines.Count; i++)
            {
                Equal(surveyPlan.Lines[i].LineNumber, reorderedPlan.Lines[i].LineNumber);
                Equal(surveyPlan.Lines[i].Segments.Count, reorderedPlan.Lines[i].Segments.Count);
                for (int j = 0; j < surveyPlan.Lines[i].Segments.Count; j++)
                {
                    SurveyLineSegment expected = surveyPlan.Lines[i].Segments[j];
                    SurveyLineSegment actual = reorderedPlan.Lines[i].Segments[j];
                    Equal(expected.Start.X, actual.Start.X, 0.001);
                    Equal(expected.Start.Y, actual.Start.Y, 0.001);
                    Equal(expected.End.X, actual.End.X, 0.001);
                    Equal(expected.End.Y, actual.End.Y, 0.001);
                }
            }

            var subdivisionPlanner = new SurveyLineSubdivisionPlanner();
            var overlapMother = new SurveyLinePlanResult
            {
                Boundary = [new(0,0), new(1000,0), new(1000,1000), new(0,1000)],
                Options = new SurveyLinePlanOptions { SpacingM = 100, AzimuthDegrees = 0 },
                Lines = [new SurveyPlannedLine { LineNumber = 7, Segments = [new(new(500,0), new(500,1000))] }]
            };
            foreach (var cut in new[] {
                new SurveySubdivisionCutLine(new(-100,500), new(1100,500)),
                new SurveySubdivisionCutLine(new(0,250), new(1000,750)) })
            {
                var overlap = subdivisionPlanner.Subdivide(overlapMother, [cut], SurveySubdivisionAxis.Custom, 100);
                var spans = overlap.Subareas.Select(b => b.Lines.Single().Segments.Single()).OrderBy(s => s.Start.Y).ToArray();
                Equal(100, spans[0].End.Y - spans[1].Start.Y, 0.001);
                Equal(0, spans[0].Start.Y, 0.001);
                Equal(1000, spans[1].End.Y, 0.001);
                True(overlap.Subareas.All(b => b.Lines.Single().LineNumber == 7), "overlap must preserve mother line numbers");
                var disabled = subdivisionPlanner.Subdivide(overlapMother, [cut], SurveySubdivisionAxis.Custom);
                Equal(1000, disabled.Subareas.Sum(b => b.Lines.Single().Segments.Single().Length), 0.001);
                var operations = new SurveySubdivisionOperationPlan { Operations = [new("CUT01", "ROOT", cut)] };
                var operationOverlap = subdivisionPlanner.SubdivideByOperations(overlapMother, operations, 100);
                Equal(1100, operationOverlap.Subareas.Sum(b => b.Lines.Single().Segments.Single().Length), 0.001);
            }
            var clippedOverlap = subdivisionPlanner.Subdivide(overlapMother,
                [new(new(-100,20), new(1100,20))], SurveySubdivisionAxis.Custom, 100);
            True(clippedOverlap.Subareas.SelectMany(b => b.Lines).SelectMany(l => l.Segments)
                .All(s => s.Start.Y >= 0 && s.End.Y <= 1000), "overlap must not cross mother endpoints");
            Throws<ArgumentOutOfRangeException>(() => subdivisionPlanner.Subdivide(overlapMother,
                [new(new(-100,500),new(1100,500))], SurveySubdivisionAxis.Custom, double.NaN));
            SurveyLineSubdivisionResult subdivision =
                subdivisionPlanner.Subdivide(surveyPlan, 2);
            Equal(2, subdivision.Subareas.Count);
            double motherMaxSegment = surveyPlan.Lines
                .SelectMany(line => line.Segments)
                .Max(segment => segment.Length);
            double childMaxSegment = subdivision.Subareas
                .SelectMany(block => block.Lines)
                .SelectMany(line => line.Segments)
                .Max(segment => segment.Length);
            True(
                childMaxSegment < motherMaxSegment * 0.55,
                "KML-style subdivision must shorten mother survey lines rather than regenerate them");
            True(
                subdivision.Subareas
                    .SelectMany(block => block.Lines)
                    .All(child => surveyPlan.Lines.Any(
                        mother => mother.LineNumber == child.LineNumber)),
                "subarea line numbers must come from the mother survey-line plan");
            True(
                subdivision.Subareas.All(block =>
                    block.Lines.All(line => !line.IsCoverageExtension)),
                "production subdivision must not contain frozen parity-extension lines");

            IReadOnlyList<SurveySubdivisionCutLine> xCuts =
                subdivisionPlanner.CreateEqualCuts(
                    surveyPlan,
                    SurveySubdivisionAxis.ProjectedX,
                    3);
            Equal(2, xCuts.Count);
            SurveyLineSubdivisionResult xSubdivision = subdivisionPlanner.Subdivide(
                surveyPlan,
                xCuts,
                SurveySubdivisionAxis.ProjectedX);
            Equal(3, xSubdivision.Subareas.Count);

            IReadOnlyList<SurveySubdivisionCutLine> yCuts =
                subdivisionPlanner.CreateEqualCuts(
                    surveyPlan,
                    SurveySubdivisionAxis.ProjectedY,
                    3);
            SurveyLineSubdivisionResult ySubdivision = subdivisionPlanner.Subdivide(
                surveyPlan,
                yCuts,
                SurveySubdivisionAxis.ProjectedY);
            Equal(3, ySubdivision.Subareas.Count);
            True(
                ySubdivision.Subareas
                    .SelectMany(block => block.Lines)
                    .All(child => surveyPlan.Lines.Any(
                        mother => mother.LineNumber == child.LineNumber)),
                "X/Y subdivision must clip mother lines rather than re-plan child lines");

            double cutY = (surveyPlan.Boundary.Min(point => point.Y) +
                           surveyPlan.Boundary.Max(point => point.Y)) / 2d;
            double minSurveyX = surveyPlan.Boundary.Min(point => point.X) - 1000d;
            double maxSurveyX = surveyPlan.Boundary.Max(point => point.X) + 1000d;
            SurveyLineSubdivisionResult manualSubdivision = subdivisionPlanner.Subdivide(
                surveyPlan,
                [new SurveySubdivisionCutLine(
                    new SurveyBoundaryPoint(minSurveyX, cutY),
                    new SurveyBoundaryPoint(maxSurveyX, cutY))],
                SurveySubdivisionAxis.Custom);
            Equal(2, manualSubdivision.Subareas.Count);
            Equal(1, manualSubdivision.Cuts.Count);

            // Concave U-shaped polygon: preserve the two real segments rather
            // than silently CONNECTing them. Writer emits a blank separator row
            // so the existing CONNECT/SPLIT preprocessor can take over.
            string concavePly = Path.Combine(root, "concave.ply");
            File.WriteAllText(
                concavePly,
                "500000\t5000010\n500010\t5000010\n500010\t5000000\n" +
                "500006\t5000000\n500006\t5000006\n500004\t5000006\n" +
                "500004\t5000000\n500000\t5000000\n",
                new UTF8Encoding(false));
            SurveyLinePlanResult concavePlan = survey.Plan(
                concavePly,
                new SurveyLinePlanOptions
                {
                    SpacingM = 2,
                    AzimuthDegrees = 90,
                    InitialLineNumber = 1,
                    LineNumberIncrement = 1
                });
            SurveyPlannedLine splitLine = concavePlan.Lines.First(x =>
                x.Segments.Count > 1);
            Equal(2, splitLine.Segments.Count);

            string endpointCsv = Path.Combine(root, "generated-endpoints.csv");
            survey.WriteEndpointCsv(endpointCsv, concavePlan);
            string endpointText = File.ReadAllText(endpointCsv);
            True(endpointText.Contains("Line,X,Y"), "generated endpoint CSV header missing");
            True(
                endpointText.Contains($"{splitLine.LineLabel},,"),
                "concave separator row missing");

            var concaveService = new ConcaveBoundaryLineService();
            True(
                concaveService.TryInspectConcaveInputBeforeNumericParse(
                    endpointCsv,
                    out ConcaveLineInspection? generatedInspection) &&
                generatedInspection is not null &&
                generatedInspection.DisconnectedLines.Any(x =>
                    x.SourceLine == splitLine.LineLabel),
                "generated PLY endpoints must feed the existing CONNECT/SPLIT preprocessor");
        });
    }

    private static void TestCoordinateKmlRangeExport()
    {
        WithTempRoot(root =>
        {
            var exporter = new KmlRangeExportService();

            KmlRangeFormat sky = exporter.LoadBuiltInFormat(
                KmlRangeExportService.SkyBlueTemplateKey);
            Equal("#87CEEB", sky.LineColorHex);
            Equal("#87CEEB", sky.FillColorHex);
            Equal(3.0, sky.LineWidth, 1e-12);
            Equal(40, sky.FillOpacityPercent);
            True(
                sky.Fill &&
                sky.Outline &&
                sky.Tessellate,
                "built-in sky template flags");
            Equal("clampToGround", sky.AltitudeMode);
            Equal(
                "边缘-天青_填充-天青",
                KmlRangeExportService.FormatStyleSuffix(sky));

            KmlRangeFormat redYellow =
                exporter.LoadBuiltInFormat(
                    KmlRangeExportService.RedYellowTemplateKey);
            Equal("#FF0000", redYellow.LineColorHex);
            Equal("#FFFF00", redYellow.FillColorHex);
            Equal(3.0, redYellow.LineWidth, 1e-12);
            Equal(40, redYellow.FillOpacityPercent);
            Equal(
                "边缘-红色_填充-黄色",
                KmlRangeExportService.FormatStyleSuffix(redYellow));

            string output = Path.Combine(
                root,
                "测试项目_测试工区_坐标_边缘-天青_填充-天青.kml");

            exporter.WriteRange(
                output,
                [
                    new GeoCoordinate(116.0, 39.0),
                    new GeoCoordinate(116.1, 39.0),
                    new GeoCoordinate(116.1, 39.1),
                    new GeoCoordinate(116.0, 39.1)
                ],
                sky,
                new KmlRangeExportMetadata(
                    "测试项目",
                    "A区",
                    "坐标转换结果",
                    "待核查",
                    117));

            True(
                File.Exists(output),
                "KML range output missing");

            XDocument doc = XDocument.Load(output);

            string lineColor = doc
                .Descendants()
                .First(x => x.Name.LocalName == "LineStyle")
                .Elements()
                .First(x => x.Name.LocalName == "color")
                .Value;

            string fillColor = doc
                .Descendants()
                .First(x => x.Name.LocalName == "PolyStyle")
                .Elements()
                .First(x => x.Name.LocalName == "color")
                .Value;

            Equal("FFEBCE87", lineColor);
            Equal("66EBCE87", fillColor);

            string coordinateText = doc
                .Descendants()
                .First(x => x.Name.LocalName == "coordinates")
                .Value;

            string[] rows = coordinateText.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries);

            Equal(5, rows.Length);
            Equal(rows[0], rows[^1]);

            XElement rootElement = doc.Root
                ?? throw new InvalidDataException("KML root missing");
            Equal("kml", rootElement.Name.LocalName);
            Equal("", rootElement.Name.NamespaceName);

            True(
                !doc.Descendants().Any(x =>
                    x.Name.LocalName is
                        "Folder" or
                        "description" or
                        "ExtendedData" or
                        "SchemaData" or
                        "styleUrl"),
                "DJI minimal KML contains unsupported metadata elements");

            XElement placemark = doc
                .Descendants()
                .First(x => x.Name.LocalName == "Placemark");
            True(
                placemark.Elements().Any(x =>
                    x.Name.LocalName == "Style"),
                "DJI minimal KML must use inline Style");

            string rawKml = File.ReadAllText(output);
            True(
                !rawKml.Contains("xmlns", StringComparison.OrdinalIgnoreCase),
                "DJI minimal KML must not declare XML namespaces");
            True(
                rawKml.Contains("<visibility>1</visibility>", StringComparison.Ordinal),
                "DJI minimal KML visibility missing");

            var subdivisionService = new KmlSurveySubdivisionService();
            KmlSurveySubdivisionPreparation preparation =
                subdivisionService.Prepare(
                    output,
                    117,
                    "WGS84",
                    new SurveyLinePlanOptions
                    {
                        SpacingM = 1000,
                        AzimuthDegrees = 90,
                        InitialLineNumber = 1,
                        LineNumberIncrement = 1
                    },
                    maxSortieKm: 18);
            string splitDirectory = Path.Combine(root, "kml-split");
            _ = subdivisionService.WriteSubdivision(
                preparation,
                3,
                splitDirectory);
            KmlSurveySubdivisionBundle splitBundle =
                subdivisionService.WriteSubdivision(
                    preparation,
                    2,
                    splitDirectory);
            Equal(2, splitBundle.Children.Count);
            Equal("P01", splitBundle.Children[0].PartitionCode);
            Equal("P02", splitBundle.Children[1].PartitionCode);
            Equal(
                Path.Combine("P01", "P01.kml"),
                Path.GetRelativePath(splitDirectory, splitBundle.Children[0].KmlPath));
            Equal(
                Path.Combine("P01", "lines.csv"),
                Path.GetRelativePath(splitDirectory, splitBundle.Children[0].EndpointCsvPath));
            True(
                !Directory.Exists(Path.Combine(splitDirectory, "P03")),
                "re-splitting to fewer children must remove stale partition directories");
            Equal(
                "partition_manifest.json",
                Path.GetFileName(splitBundle.ManifestPath));
            Equal(
                "survey_lines.csv",
                Path.GetFileName(splitBundle.MotherEndpointCsvPath));
            True(File.Exists(splitBundle.ManifestPath), "KML subdivision manifest missing");
            True(File.Exists(splitBundle.MotherEndpointCsvPath), "mother endpoint CSV missing");
            True(
                splitBundle.ChildEndpointCsvPaths.All(File.Exists),
                "child endpoint CSV missing");
            True(
                splitBundle.Children.All(child => File.Exists(child.KmlPath)),
                "child KML missing");

            string partitionManifestText = File.ReadAllText(splitBundle.ManifestPath);
            var overlapBundle = subdivisionService.WriteSubdivision(preparation,
                subdivisionService.CreateEqualCuts(preparation, SurveySubdivisionAxis.AlongSurveyLine, 2),
                SurveySubdivisionAxis.AlongSurveyLine, Path.Combine(root, "kml-split-overlap"), 100);
            using (var overlapManifest = JsonDocument.Parse(File.ReadAllText(overlapBundle.ManifestPath)))
            {
                True(overlapManifest.RootElement.GetProperty("survey_line_overlap_enabled").GetBoolean(), "overlap flag must persist");
                Equal(100, overlapManifest.RootElement.GetProperty("survey_line_overlap_length_m").GetDouble(), 0.001);
            }
            foreach (var child in overlapBundle.Children)
            {
                var allowed = subdivisionService.ReadProjectedBoundary(child.KmlPath, preparation.CentralMeridian, preparation.Datum);
                var plan = new RoutePlanner().BuildFromCsv(child.EndpointCsvPath, new RoutePlanOptions
                { CoordinateMode = RouteCoordinateMode.Projected, CentralMeridian = preparation.CentralMeridian, MaxSortieKm = 18 });
                RouteBoundarySafetyService.EnsurePlanInsideBoundary(plan, allowed, "overlap child");
                True(subdivisionService.TryResolveGeneratedChild(child.KmlPath, out var restored) && restored is not null,
                    "overlap child must restore lineage from manifest");
            }
            using JsonDocument partitionManifest = JsonDocument.Parse(partitionManifestText);
            JsonElement partitionRoot = partitionManifest.RootElement;
            True(
                (partitionRoot.GetProperty("derivation_rule").GetString() ?? "")
                    .Contains("mother survey lines are numbered once", StringComparison.Ordinal),
                "KML subdivision mother-line identity rule missing from manifest");
            Equal(
                "south/west -> north/east; polygon vertex order does not affect numbering",
                partitionRoot.GetProperty("numbering_rule").GetString() ?? "");
            Equal(
                "PENDING_MODIFICATION",
                partitionRoot.GetProperty("snake_completion_status").GetString() ?? "");
            True(
                (partitionRoot.GetProperty("snake_completion_enable_condition").GetString() ?? "")
                    .Contains("passable-road / field-access calculation", StringComparison.Ordinal),
                "frozen snake completion enable condition missing from manifest");
            True(
                (partitionRoot.GetProperty("snake_completion_rule").GetString() ?? "")
                    .Contains("no extra survey line is added outside the workarea", StringComparison.Ordinal),
                "frozen snake-completion rule missing from manifest");
            True(
                !partitionRoot.GetProperty("line_parameters")
                    .GetProperty("extend_for_even_snake_completion")
                    .GetBoolean(),
                "production subdivision must keep parity-extension disabled");
            Equal(
                "P01/P01.kml",
                partitionRoot.GetProperty("children")[0]
                    .GetProperty("kml")
                    .GetString() ?? "");

            True(
                subdivisionService.TryResolveGeneratedChild(
                    splitBundle.Children[1].KmlPath,
                    out KmlSurveySubdivisionResolvedChild? resolvedChild),
                "generated child KML lineage could not resolve back to mother-clipped endpoints");
            True(resolvedChild is not null, "resolved child KML metadata missing");
            Equal("P02", resolvedChild!.PartitionCode);
            True(resolvedChild.Cuts.Count > 0, "partition split-cut lineage missing");
            Equal(
                Path.GetFullPath(splitBundle.Children[1].EndpointCsvPath),
                Path.GetFullPath(resolvedChild.EndpointCsvPath));
            Equal(117d, resolvedChild.CentralMeridian, 1e-12);
            True(
                !resolvedChild.ContainsFrozenSnakeExtension,
                "new v0.9.2 partition bundle must not contain the frozen outward extension");

            // Both child workareas must resolve their preferred start from the
            // shared cut boundary rather than opposite outside edges. This keeps
            // field launch activity on the same physical side after subdivision.
            var routePlanner = new RoutePlanner();
            foreach (RouteStartDirection startDirection in Enum.GetValues<RouteStartDirection>())
            {
                var sharedStarts = new List<RouteEndpoint>();
                foreach (KmlSurveySubdivisionChildFile childFile in splitBundle.Children)
                {
                    True(
                        subdivisionService.TryResolveGeneratedChild(
                            childFile.KmlPath,
                            out KmlSurveySubdivisionResolvedChild? childLineage) &&
                        childLineage is not null,
                        "child partition lineage missing during shared-start test");
                    IReadOnlyList<RawRoutePoint> rawChild =
                        DelimitedTextReader.ReadRawRouteCsv(childFile.EndpointCsvPath);
                    RouteLineInference childInference = routePlanner.InferSpatialLines(rawChild);
                    List<RouteEndpoint> cutCandidates = childInference.Endpoints
                        .Where(point => childLineage!.Cuts.Any(cut =>
                        {
                            double dx = cut.End.X - cut.Start.X;
                            double dy = cut.End.Y - cut.Start.Y;
                            double lengthSquared = dx * dx + dy * dy;
                            if (lengthSquared <= 1e-18)
                                return false;
                            double t = (
                                (point.X - cut.Start.X) * dx +
                                (point.Y - cut.Start.Y) * dy) / lengthSquared;
                            t = Math.Clamp(t, 0d, 1d);
                            double closestX = cut.Start.X + t * dx;
                            double closestY = cut.Start.Y + t * dy;
                            double ex = point.X - closestX;
                            double ey = point.Y - closestY;
                            return Math.Sqrt(ex * ex + ey * ey) <= 0.25d;
                        }))
                        .ToList();
                    True(cutCandidates.Count > 0, "partition has no survey-line endpoint on shared cut");
                    sharedStarts.Add(RoutePlanner.GetTraversalStartPointFromCandidates(
                        childInference.Grouped,
                        childInference.OrderedLines,
                        cutCandidates,
                        startDirection,
                        "projected"));
                }
                Equal(sharedStarts[0].X, sharedStarts[1].X, 0.01);
                Equal(sharedStarts[0].Y, sharedStarts[1].Y, 0.01);
            }

            // Explicit partition-side selection must also let adjacent child
            // workareas choose each other: west child -> East, east child ->
            // West. This is the production control used when two crews launch
            // simultaneously from the same shared field boundary.
            var explicitSharedStarts = new List<RouteEndpoint>();
            for (int childIndex = 0; childIndex < splitBundle.Children.Count; childIndex++)
            {
                KmlSurveySubdivisionChildFile childFile = splitBundle.Children[childIndex];
                IReadOnlyList<RawRoutePoint> rawChild =
                    DelimitedTextReader.ReadRawRouteCsv(childFile.EndpointCsvPath);
                RouteLineInference childInference = routePlanner.InferSpatialLines(rawChild);
                RoutePartitionStartSide side = childIndex == 0
                    ? RoutePartitionStartSide.East
                    : RoutePartitionStartSide.West;
                explicitSharedStarts.Add(RoutePlanner.GetTraversalStartPointFromSide(
                    childInference.Grouped,
                    childInference.OrderedLines,
                    side,
                    RouteStartDirection.Southwest,
                    "projected"));
            }
            Equal(explicitSharedStarts[0].X, explicitSharedStarts[1].X, 0.01);
            Equal(explicitSharedStarts[0].Y, explicitSharedStarts[1].Y, 0.01);

            // The same contract must work for vertically adjacent workareas:
            // lower child -> North, upper child -> South. Keep the existing
            // Southwest corner preference only as the secondary selector along
            // that explicitly requested cardinal side.
            var lowerEndpoints = new List<RouteEndpoint>
            {
                new() { Line = "L001", Endpoint = 0, X = 116.0000, Y = 39.0000, Pid = "LOW-SW" },
                new() { Line = "L001", Endpoint = 1, X = 116.0100, Y = 39.0000, Pid = "LOW-SE" },
                new() { Line = "L002", Endpoint = 0, X = 116.0000, Y = 39.0100, Pid = "LOW-NW" },
                new() { Line = "L002", Endpoint = 1, X = 116.0100, Y = 39.0100, Pid = "LOW-NE" }
            };
            var upperEndpoints = new List<RouteEndpoint>
            {
                new() { Line = "L003", Endpoint = 0, X = 116.0000, Y = 39.0100, Pid = "UP-SW" },
                new() { Line = "L003", Endpoint = 1, X = 116.0100, Y = 39.0100, Pid = "UP-SE" },
                new() { Line = "L004", Endpoint = 0, X = 116.0000, Y = 39.0200, Pid = "UP-NW" },
                new() { Line = "L004", Endpoint = 1, X = 116.0100, Y = 39.0200, Pid = "UP-NE" }
            };
            RouteEndpoint lowerNorth = RoutePlanner.GetStartPointFromSide(
                lowerEndpoints,
                RoutePartitionStartSide.North,
                RouteStartDirection.Southwest,
                "geographic");
            RouteEndpoint upperSouth = RoutePlanner.GetStartPointFromSide(
                upperEndpoints,
                RoutePartitionStartSide.South,
                RouteStartDirection.Southwest,
                "geographic");
            Equal(lowerNorth.X, upperSouth.X, 1e-9);
            Equal(lowerNorth.Y, upperSouth.Y, 1e-9);
            Equal(39.0100, lowerNorth.Y, 1e-9);
        });
    }

    private static void TestSyntheticBoundaryAcceptance()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "fixtures", "synthetic_boundary.kml");
        True(File.Exists(fixture), "synthetic acceptance fixture missing from SmokeTests output");
        List<SpatialGeometry> geometries = SpatialGeometryService.ParseKml(fixture);
        Equal(1, geometries.Count);
        SpatialGeometry polygon = geometries.Single();
        Equal("Polygon", polygon.Type);
        True(polygon.Closed, "synthetic acceptance boundary must remain closed");
        Equal(5, polygon.Coordinates.Count);
        double minLon = polygon.Coordinates.Min(x => x.Longitude);
        double maxLon = polygon.Coordinates.Max(x => x.Longitude);
        double minLat = polygon.Coordinates.Min(x => x.Latitude);
        double maxLat = polygon.Coordinates.Max(x => x.Latitude);
        Equal(116, minLon, 1e-9);
        Equal(116.02, maxLon, 1e-9);
        Equal(39, minLat, 1e-9);
        Equal(39.01, maxLat, 1e-9);
        Equal("PASSED", SpatialGeometryService.AuditGeometry(polygon).Status);
    }

    private static void TestSpatialExchange()
    {
        WithTempRoot(root =>
        {
            var store = new JsonStore();
            var ws = new WorkspaceService(store);
            ws.Initialize(root, "Spatial Test");
            var registry = new CoordinateRegistryService(store, ws);

            var geometries = new[]
            {
                new SpatialGeometry
                {
                    Name = "test-line",
                    Type = "LineString",
                    Coordinates =
                    [
                        new GeoCoordinate(116.20, 39.90, 100),
                        new GeoCoordinate(116.21, 39.91, 101),
                        new GeoCoordinate(116.22, 39.92, 102)
                    ]
                }
            };

            string kml = Path.Combine(root, "line.kml");
            string gpx = Path.Combine(root, "line.gpx");
            SpatialGeometryService.WriteKml(kml, geometries);
            var kmlAudit = registry.AuditSpatialFile(kml);
            Equal("PASSED", kmlAudit.Status);
            Equal(1, kmlAudit.GeometryCount);

            var roundtrip = registry.ConvertSpatialFile(kml, gpx);
            Equal("PASSED", roundtrip.Status);
            Equal(0, roundtrip.DifferenceCount);
            True(File.Exists(gpx), "GPX conversion missing");

            string polygon = Path.Combine(root, "bad_polygon.kml");
            SpatialGeometryService.WriteKml(polygon,
            [
                new SpatialGeometry
                {
                    Name = "open-polygon",
                    Type = "Polygon",
                    Coordinates =
                    [
                        new GeoCoordinate(116.0, 39.0),
                        new GeoCoordinate(116.1, 39.0),
                        new GeoCoordinate(116.1, 39.1)
                    ],
                    Closed = false
                }
            ]);
            var badAudit = registry.AuditSpatialFile(polygon);
            Equal("BLOCKED", badAudit.Status);
            True(badAudit.Errors.Any(x => x.Contains("未闭合")), "open polygon was not blocked");
        });
    }

    private static void TestOperationalProductionQueries()
    {
        string root = Path.Combine(Path.GetTempPath(),
            "magops-operational-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new JsonStore();
            var workspace = new WorkspaceService(store);
            workspace.Initialize(root, "Operational Test");
            WorkspaceRecord ws = workspace.GetWorkspace(root);
            WorkareaRecord wa = workspace.RegisterWorkarea(root, "W01");
            var artifacts = new ArtifactService(store, workspace);
            var runs = new ProcessingRunService(store, workspace, artifacts);
            string output = Path.Combine(root, "result.txt");
            File.WriteAllText(output, "result");
            ArtifactRecord artifact = artifacts.RegisterGeneratedFile(
                root, output, "test");
            var context = new GeneratedArtifactContext(
                ws.ProjectId, wa.WorkareaId);
            ProcessingRunRecord run = runs.Begin(
                root, "test_run", context);
            ProcessingRunRecord completed = runs.Complete(
                root, run.ProcessingRunId, [artifact.ArtifactId]);

            var queries = new OperationalQueries(store);
            ApplicationResult<ProductionOverview> overview =
                queries.GetProductionOverview(
                    new ApplicationRequest(root, WorkareaId: wa.WorkareaId));
            True(overview.Ok && overview.Result is not null,
                "production overview query failed");
            Equal(1, overview.Result!.RunCount);
            Equal(1, overview.Result.CompletedRunCount);
            Equal(1, overview.Result.ArtifactCount);
            Equal(completed.ProcessingRunId,
                artifacts.List(root).Single(x => x.ArtifactId == artifact.ArtifactId)
                    .ProcessingRunId);
            True(completed.ArtifactIds.SequenceEqual([artifact.ArtifactId]),
                "completed ProcessingRun did not own the exact Artifact set");

            ProcessingRunRecord regeneratedRun = runs.Begin(
                root, "regenerated_test_run", context);
            ProcessingRunRecord regenerated = runs.Complete(
                root, regeneratedRun.ProcessingRunId, [artifact.ArtifactId]);
            Equal("COMPLETED", regenerated.Status);
            True(completed.ArtifactIds.Contains(
                    artifact.ArtifactId, StringComparer.OrdinalIgnoreCase),
                "historical ProcessingRun lost stable Artifact membership");
            Equal(regenerated.ProcessingRunId,
                artifacts.List(root).Single(x => x.ArtifactId == artifact.ArtifactId)
                    .ProcessingRunId);

            WorkareaRecord otherWa = workspace.RegisterWorkarea(root, "W02");
            string otherOutput = Path.Combine(root, "other-result.txt");
            File.WriteAllText(otherOutput, "other");
            ArtifactRecord otherArtifact = artifacts.RegisterGeneratedFile(
                root, otherOutput, "test");
            ProcessingRunRecord otherRun = runs.Begin(
                root, "other_test_run",
                new GeneratedArtifactContext(ws.ProjectId, otherWa.WorkareaId));
            runs.Complete(root, otherRun.ProcessingRunId, [otherArtifact.ArtifactId]);
            ApplicationResult<ProductionOverview> scoped =
                queries.GetProductionOverview(
                    new ApplicationRequest(root, WorkareaId: wa.WorkareaId));
            True(scoped.Ok && scoped.Result is not null,
                "workarea-scoped production overview failed");
            Equal(2, scoped.Result!.RunCount);
            Equal(1, scoped.Result.ArtifactCount);

            ProcessingRunRecord failedRun = runs.Begin(
                root, "failed_test_run", context);
            ProcessingRunRecord failed = runs.Fail(
                root, failedRun.ProcessingRunId,
                new InvalidOperationException("injected failure"));
            Equal("FAILED", failed.Status);
            True(!string.IsNullOrWhiteSpace(failed.FailureCode),
                "failed ProcessingRun lost failure code");

            ApplicationResult<ProductionRunSummary> detail =
                queries.GetProductionRun(
                    new ApplicationRequest(root, WorkareaId: wa.WorkareaId),
                    completed.ProcessingRunId);
            True(detail.Ok && detail.Result is not null,
                "production run query failed");
            Equal(1, detail.Result!.Artifacts.Count);
            Equal(artifact.ArtifactId, detail.Result.Artifacts[0].ArtifactId);

            // Break the backlink deliberately: query must reject contradictory lineage.
            List<ArtifactRecord> rows = artifacts.List(root);
            rows.Single(x => x.ArtifactId == artifact.ArtifactId).ProcessingRunId =
                "run_wrong";
            store.Write(ProjectPaths.Json(root, "artifacts.json"), rows);
            ApplicationResult<ProductionRunSummary> broken =
                queries.GetProductionRun(
                    new ApplicationRequest(root, WorkareaId: wa.WorkareaId),
                    completed.ProcessingRunId);
            True(!broken.Ok, "broken ProcessingRun/Artifact lineage was accepted");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void TestRegionGeometry()
    {
        var outer = new[]
        {
            new SurveyBoundaryPoint(0,0), new SurveyBoundaryPoint(10,0),
            new SurveyBoundaryPoint(10,10), new SurveyBoundaryPoint(0,10)
        };
        var hole = new[]
        {
            new SurveyBoundaryPoint(4,4), new SurveyBoundaryPoint(6,4),
            new SurveyBoundaryPoint(6,6), new SurveyBoundaryPoint(4,6)
        };
        var island = new[]
        {
            new SurveyBoundaryPoint(20,0), new SurveyBoundaryPoint(24,0),
            new SurveyBoundaryPoint(24,4), new SurveyBoundaryPoint(20,4)
        };
        var region = new SurveyRegionGeometry(
        [
            new SurveyRegionPolygon(outer, [hole]),
            new SurveyRegionPolygon(island, [])
        ]);

        True(SurveyRegionGeometryMath.ContainsOrOnBoundary(
            region, new SurveyBoundaryPoint(2,2)), "outer polygon point rejected");
        True(!SurveyRegionGeometryMath.ContainsOrOnBoundary(
            region, new SurveyBoundaryPoint(5,5)), "hole interior was treated as valid Region");
        True(SurveyRegionGeometryMath.ContainsOrOnBoundary(
            region, new SurveyBoundaryPoint(22,2)), "second polygon point rejected");
        True(!RouteBoundarySafetyService.IsSegmentInsideOrOnBoundary(
            new SurveyBoundaryPoint(2,5), new SurveyBoundaryPoint(8,5), region, 1e-8),
            "same-line segment crossing a hole was accepted");
        True(!RouteBoundarySafetyService.IsSegmentInsideOrOnBoundary(
            new SurveyBoundaryPoint(8,2), new SurveyBoundaryPoint(22,2), region, 1e-8),
            "segment crossing the gap between MultiPolygon components was accepted");

        SurveyLinePlanResult plan = new SurveyLinePlanner().Plan(
            region,
            new SurveyLinePlanOptions
            {
                SpacingM = 2,
                AzimuthDegrees = 0,
                InitialLineNumber = 1,
                LineNumberIncrement = 1
            });
        True(plan.Lines.SelectMany(x => x.Segments).Any(),
            "Region survey planning produced no segments");
        True(plan.Lines.SelectMany(x => x.Segments).All(segment =>
            SurveyRegionGeometryMath.ContainsOrOnBoundary(region,
                new SurveyBoundaryPoint(
                    (segment.Start.X + segment.End.X)/2d,
                    (segment.Start.Y + segment.End.Y)/2d), 1e-7)),
            "Region survey planning produced a segment outside Region");

        True(!SurveyLineSubdivisionPlanner.SupportsRegionTopology(
                region, out string topologyReason) &&
             topologyReason.Contains("MultiPolygon/holes", StringComparison.Ordinal),
            "complex Region partition capability was not reported explicitly");
        Throws<NotSupportedException>(() =>
            new SurveyLineSubdivisionPlanner().Subdivide(plan, region, 2));

        string regionJson = Path.Combine(Path.GetTempPath(),
            "magops-region-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var persistence = new SurveyRegionPersistenceService();
            persistence.Save(regionJson, region);
            SurveyRegionGeometry restored = persistence.Load(regionJson);
            Equal(2, restored.Polygons.Count);
            Equal(1, restored.Polygons[0].Holes.Count);
            True(!SurveyRegionGeometryMath.ContainsOrOnBoundary(
                    restored, new SurveyBoundaryPoint(5,5)),
                "Region persistence lost hole topology");
        }
        finally
        {
            if (File.Exists(regionJson)) File.Delete(regionJson);
        }


        string kml = Path.Combine(Path.GetTempPath(),
            "magops-region-" + Guid.NewGuid().ToString("N") + ".kml");
        try
        {
            SpatialGeometryService.WriteKmlPolygons(kml,
            [
                new SpatialPolygon("A",
                [
                    new GeoCoordinate(110.0000, 30.0000),
                    new GeoCoordinate(110.0100, 30.0000),
                    new GeoCoordinate(110.0100, 30.0100),
                    new GeoCoordinate(110.0000, 30.0100)
                ],
                [
                    new[]
                    {
                        new GeoCoordinate(110.0040, 30.0040),
                        new GeoCoordinate(110.0060, 30.0040),
                        new GeoCoordinate(110.0060, 30.0060),
                        new GeoCoordinate(110.0040, 30.0060)
                    }
                ]),
                new SpatialPolygon("B",
                [
                    new GeoCoordinate(110.0200, 30.0000),
                    new GeoCoordinate(110.0240, 30.0000),
                    new GeoCoordinate(110.0240, 30.0040),
                    new GeoCoordinate(110.0200, 30.0040)
                ], [])
            ]);
            var kmlService = new KmlSurveySubdivisionService();
            IReadOnlyList<SpatialPolygon> parsed = kmlService.ReadRegionPolygons(kml);
            Equal(2, parsed.Count);
            Equal(1, parsed[0].InnerRings.Count);
            SurveyRegionGeometry projectedRegion =
                kmlService.ReadProjectedRegion(kml, 111d, "WGS84");
            Equal(2, projectedRegion.Polygons.Count);
            Equal(1, projectedRegion.Polygons[0].Holes.Count);
            Throws<NotSupportedException>(() =>
                kmlService.ReadProjectedBoundary(kml, 111d, "WGS84"));
            Throws<NotSupportedException>(() =>
                kmlService.ReadPrimaryPolygon(kml));
        }
        finally
        {
            if (File.Exists(kml)) File.Delete(kml);
        }
    }

    private static void TestRouteManifest()
    {
        WithTempRoot(root =>
        {
            string source = Path.Combine(root, "source.txt");
            string output = Path.Combine(root, "output.kml");
            File.WriteAllText(source, "source", Encoding.UTF8);
            File.WriteAllText(output, "<kml xmlns=\"http://www.opengis.net/kml/2.2\"></kml>", Encoding.UTF8);

            var store = new JsonStore();
            var ws = new WorkspaceService(store);
            WorkspaceRecord routeWorkspace = ws.Initialize(root, "Route Adapter Test");
            var artifacts = new ArtifactService(store, ws);
            var routes = new RouteResultService(store, ws, artifacts);

            string sourceHash = ArtifactService.Sha256(source);
            string outputHash = ArtifactService.Sha256(output);
            string manifest = Path.Combine(root, "route_result.json");
            var manifestPayload = new Dictionary<string, object?>
            {
                ["schema_version"] = "route-result-1",
                ["algorithm_version"] = "golden-1",
                ["created_at"] = "2026-09-04T00:00:00Z",
                ["command"] = "test",
                ["status"] = "PASSED",
                ["project_id"] = "",
                ["workarea_id"] = "",
                ["subarea_id"] = "",
                ["route_id"] = "",
                ["sortie_id"] = "",
                ["source_paths"] = new[] { "source.txt" },
                ["input_sha256"] = new Dictionary<string, string> { ["source.txt"] = sourceHash },
                ["parameters"] = new Dictionary<string, object?>(),
                ["summary"] = new Dictionary<string, object?>(),
                ["warnings"] = Array.Empty<string>(),
                ["errors"] = Array.Empty<string>(),
                ["output_paths"] = new[] { "output.kml" },
                ["output_sha256"] = new Dictionary<string, string> { ["output.kml"] = outputHash },
                ["confirmed_by"] = "",
                ["confirmed_at"] = ""
            };
            File.WriteAllText(manifest, JsonSerializer.Serialize(manifestPayload), Encoding.UTF8);

            var result = routes.Import(root, manifest);
            Equal("PASSED", result.Status);
            Equal(0, result.Errors.Count);

            ArtifactRecord routeArtifact = artifacts.List(root).Single(x => x.Name == "output.kml");
            True(routeArtifact.ArtifactId.StartsWith("art-", StringComparison.OrdinalIgnoreCase),
                "route artifact stable id format");
            Equal("PROJECT", routeArtifact.WorkareaAssignmentStatus);

            manifestPayload["project_id"] = "foreign-project";
            File.WriteAllText(manifest, JsonSerializer.Serialize(manifestPayload), Encoding.UTF8);
            Throws<InvalidDataException>(() => routes.Import(root, manifest));
            manifestPayload["project_id"] = routeWorkspace.ProjectId;
            File.WriteAllText(manifest, JsonSerializer.Serialize(manifestPayload), Encoding.UTF8);

            File.WriteAllText(output, "changed", Encoding.UTF8);
            var blocked = routes.Import(root, manifest);
            Equal("BLOCKED", blocked.Status);
            True(blocked.Errors.Any(x => x.Contains("输出文件哈希不一致")), "expected hash failure");
        });
    }

    private static void TestRouteV25SpatialOrdering()
    {
        var planner = new RoutePlanner();
        var points = new[]
        {
            new RawRoutePoint(116.0000, 39.9000, "0/L99", 1),
            new RawRoutePoint(116.0000, 39.9400, "1/L99", 2),
            new RawRoutePoint(116.0200, 39.9000, "0/L1", 3),
            new RawRoutePoint(116.0200, 39.9400, "1/L1", 4),
            new RawRoutePoint(116.0400, 39.9000, "0/L50", 5),
            new RawRoutePoint(116.0400, 39.9400, "1/L50", 6)
        };

        RouteLineInference inferred = planner.InferSpatialLines(points);
        Equal("L", inferred.MatchMode);
        Equal(3, inferred.OrderedLines.Count);

        // v25 contract: L number is only for endpoint pairing; the route order is spatial.
        Equal("L99", inferred.OrderedLines[0]);
        Equal("L1", inferred.OrderedLines[1]);
        Equal("L50", inferred.OrderedLines[2]);
    }

    private static void TestPartitionTraversalStartSafety()
    {
        WithTempRoot(root =>
        {
            string csv = Path.Combine(root, "partition-start-side.csv");
            var rows = new List<string> { "Line,X,Y" };
            double[] eastLengths = [80d, 120d, 520d, 140d, 90d];
            for (int i = 0; i < eastLengths.Length; i++)
            {
                string line = $"L{i + 1:D3}";
                double north = 4_400_000d + i * 100d;
                rows.Add($"{line},{500_000d.ToString(CultureInfo.InvariantCulture)},{north.ToString(CultureInfo.InvariantCulture)}");
                rows.Add($"{line},{(500_000d + eastLengths[i]).ToString(CultureInfo.InvariantCulture)},{north.ToString(CultureInfo.InvariantCulture)}");
            }
            File.WriteAllLines(csv, rows, new UTF8Encoding(true));

            var planner = new RoutePlanner();
            RawRoutePoint[] raw = DelimitedTextReader.ReadRawRouteCsv(csv).ToArray();
            RouteLineInference inference = planner.InferSpatialLines(raw);
            Equal(5, inference.OrderedLines.Count);

            RouteEndpoint unsafeEast = RoutePlanner.GetStartPointFromSide(
                inference.Endpoints,
                RoutePartitionStartSide.East,
                RouteStartDirection.Southeast,
                "projected");
            True(
                !unsafeEast.Line.Equals(inference.OrderedLines[0], StringComparison.OrdinalIgnoreCase) &&
                !unsafeEast.Line.Equals(inference.OrderedLines[^1], StringComparison.OrdinalIgnoreCase),
                "synthetic regression fixture no longer exposes the old interior-line side-selection bug");

            RouteEndpoint safeEast = RoutePlanner.GetTraversalStartPointFromSide(
                inference.Grouped,
                inference.OrderedLines,
                RoutePartitionStartSide.East,
                RouteStartDirection.Southeast,
                "projected");
            True(
                safeEast.Line.Equals(inference.OrderedLines[0], StringComparison.OrdinalIgnoreCase) ||
                safeEast.Line.Equals(inference.OrderedLines[^1], StringComparison.OrdinalIgnoreCase),
                "explicit partition side selected an interior survey line");

            RouteEndpoint interiorCutCandidate = inference.Grouped[inference.OrderedLines[2]][1];
            RouteEndpoint safeCut = RoutePlanner.GetTraversalStartPointFromCandidates(
                inference.Grouped,
                inference.OrderedLines,
                [interiorCutCandidate],
                RouteStartDirection.Southeast,
                "projected");
            True(
                safeCut.Line.Equals(inference.OrderedLines[0], StringComparison.OrdinalIgnoreCase) ||
                safeCut.Line.Equals(inference.OrderedLines[^1], StringComparison.OrdinalIgnoreCase),
                "shared-cut constraint selected an interior survey line and would create a wrap connector");

            RoutePlanResult plan = planner.BuildFromCsv(
                csv,
                new RoutePlanOptions
                {
                    CoordinateMode = RouteCoordinateMode.Projected,
                    CentralMeridian = 117d,
                    MaxSortieKm = 100d,
                    StartDirection = RouteStartDirection.Southeast,
                    PartitionStartSide = RoutePartitionStartSide.East
                });
            True(
                plan.StartPoint.Line.Equals(plan.Inference.OrderedLines[0], StringComparison.OrdinalIgnoreCase) ||
                plan.StartPoint.Line.Equals(plan.Inference.OrderedLines[^1], StringComparison.OrdinalIgnoreCase),
                "RoutePlanner.BuildFromCsv did not apply partition side before traversal ordering");

            string[] forward = plan.Inference.OrderedLines.ToArray();
            string[] reverse = plan.Inference.OrderedLines.AsEnumerable().Reverse().ToArray();
            True(
                plan.OrderedLines.SequenceEqual(forward, StringComparer.OrdinalIgnoreCase) ||
                plan.OrderedLines.SequenceEqual(reverse, StringComparer.OrdinalIgnoreCase),
                "partition traversal order wrapped through an internal line instead of walking from one grid edge");
        });
    }

    private static void TestRouteLengthBoundaries()
    {
        static double LongitudeDeltaForDistance(double metres) =>
            metres / RouteCoordinateMath.EarthRadiusM * 180.0 / Math.PI;

        static double Length(double metres)
        {
            var points = new[]
            {
                new RouteEndpoint { X = 0, Y = 0, Pid = "P001-0" },
                new RouteEndpoint { X = LongitudeDeltaForDistance(metres), Y = 0, Pid = "P001-1" }
            };
            return RouteCoordinateMath.RouteLength(points, "geographic", 0);
        }

        double a = Length(17_990);
        double b = Length(18_000);
        double c = Length(18_010);
        Equal(17_990, a, 0.01);
        Equal(18_000, b, 0.01);
        Equal(18_010, c, 0.01);
        True(a <= 18_000, "17.99 km must be accepted");
        True(b <= 18_000 + 1e-6, "18.00 km boundary must be accepted");
        True(c > 18_000, "18.01 km must be over-limit");
    }

    private static void TestRouteV25Split()
    {
        var grouped = new Dictionary<string, RouteEndpoint[]>();
        var lines = new List<string>();

        // Four ~4 km north-south lines with ~1.7 km east-west spacing near 40N:
        // two-line snake is under 18 km, four-line snake is over 18 km.
        for (int i = 0; i < 4; i++)
        {
            string line = $"L{i + 1}";
            double lon = 116.0 + i * 0.020;
            grouped[line] =
            [
                new RouteEndpoint { Line = line, Endpoint = 0, X = lon, Y = 39.90, Pid = $"P{i + 1:D3}-0", Label = $"0/{line}" },
                new RouteEndpoint { Line = line, Endpoint = 1, X = lon, Y = 39.936, Pid = $"P{i + 1:D3}-1", Label = $"1/{line}" }
            ];
            lines.Add(line);
        }

        List<RouteSortie> sorties = RoutePlanner.SplitSorties(
            grouped, lines, 0, 18_000, "geographic", 0);

        Equal(2, sorties.Count);
        Equal(2, sorties[0].SourceLines.Count);
        Equal(2, sorties[1].SourceLines.Count);
        True(sorties.All(s => RouteCoordinateMath.RouteLength(s.Points, "geographic", 0) <= 18_000),
            "expected both 2-line sorties to fit 18 km");
    }

    private static void TestRouteV25Export()
    {
        WithTempRoot(root =>
        {
            string source = Path.Combine(root, "route.csv");
            File.WriteAllText(source,
                "0/L99,39.900000,116.000000\n" +
                "1/L99,39.936000,116.000000\n" +
                "0/L1,39.900000,116.020000\n" +
                "1/L1,39.936000,116.020000\n",
                new UTF8Encoding(true));

            var planner = new RoutePlanner();
            RoutePlanResult plan = planner.BuildFromCsv(source, new RoutePlanOptions
            {
                CoordinateMode = RouteCoordinateMode.Automatic,
                HeaderlessOrder = "Point,Y,X",
                MaxSortieKm = 18,
                StartDirection = RouteStartDirection.Southwest,
                KmzAglM = 50,

                // Ordinary route generation and KML/CSV export must not
                // depend on the frozen real-time terrain-following speed.
                KmzSpeedMps = 0
            });

            string output = Path.Combine(
                root,
                "10_工区",
                "A区",
                "04_航线设计与任务文件",
                "05_设计航线");

            string terrain = Path.Combine(root, "terrain.tif");
            File.WriteAllBytes(
                terrain,
                Convert.FromBase64String(
                    "SUkqAAgAAAASAAABBAABAAAACAAAAAEBBAABAAAACAAAAAIBAwABAAAAEAAAAAMBAwABAAAAAQAAAAYBAwABAAAAAQAAABEBBAAIAAAA5gAAABUBAwABAAAAAQAAABYBBAABAAAAAQAAABcBAwAIAAAABgEAABoBBQABAAAAFgEAABsBBQABAAAAHgEAACgBAwABAAAAAQAAADEBAgAMAAAAJgEAAFMBAwABAAAAAgAAAA6DDAADAAAAMgEAAIKEDAAGAAAASgEAAK+HAwAYAAAAegEAAIGkAgAGAAAAqgEAAAAAAACwAQAAwAEAANABAADgAQAA8AEAAAACAAAQAgAAIAIAABAAEAAQABAAEAAQABAAEAABAAAAAQAAAAEAAAABAAAAdGlmZmZpbGUucHkAexSuR+F6hD97FK5H4XqEPwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAH4XrUbj+XEB7FK5H4fpDQAAAAAAAAAAAAQABAAAABQAABAAAAQACAAEEAAABAAEAAAgAAAEA5hAECAAAAQApIwYIAAABAI4jLTk5OTkAZABlAGYAZwBoAGkAagBrAGYAZwBoAGkAagBrAGwAbQBoAGkAagBrAGwAbQBuAG8AagBrAGwAbQBuAG8AcABxAGwAbQBuAG8AcABxAHIAcwBuAG8AcABxAHIAcwB0AHUAcABxAHIAcwB0AHUAdgB3AHIAcwB0AHUAdgB3AHgAeQA="));

            var dem = new GeoTiffDemSampler(terrain);
            Equal(4326, dem.Info.Epsg);
            Equal(8, dem.Info.Width);
            Equal(8, dem.Info.Height);
            True(
                dem.SampleBilinear(116.0, 39.9) is > 90 and < 140,
                "synthetic GeoTIFF sample out of expected range");
            True(dem.CanSampleBilinear(116.0, 39.9), "DEM coverage probe should include route start");
            True(!dem.CanSampleBilinear(120.0, 45.0), "DEM coverage probe should reject outside point");

            var exporter = new RouteExportService();
            RouteExportBundle bundle = exporter.ExportAllSeparated(
                plan,
                output,
                "A区",
                true,
                true,
                false,
                terrain,
                50);
            Equal(plan.Sorties.Count * 2, bundle.OutputPaths.Count);

            Equal("A01", plan.Sorties[0].Name);
            string kml = Path.Combine(output, "01_KML", "A01.kml");
            string csv = Path.Combine(output, "02_CSV", "A01.csv");
            True(File.Exists(kml), "KML missing");
            True(File.Exists(csv), "CSV missing");
            True(
                !Directory.Exists(Path.Combine(output, "03_KMZ_冻结")),
                "KMZ folder must remain absent while KMZ export is frozen");

            // A failed bundle rebuild must leave the complete previous route export untouched.
            string beforeKmlHash = GeneratedArtifactIdentity.Hash(kml);
            string beforeCsvHash = GeneratedArtifactIdentity.Hash(csv);
            RoutePlanResult invalidPlan = planner.BuildFromCsv(source, new RoutePlanOptions
            {
                CoordinateMode = RouteCoordinateMode.Automatic,
                HeaderlessOrder = "Point,Y,X",
                MaxSortieKm = 18,
                StartDirection = RouteStartDirection.Southwest,
                KmzAglM = 50,
                KmzSpeedMps = 0
            });
            invalidPlan.Sorties[^1].Points[0].Label = "";
            Throws<InvalidOperationException>(() => exporter.ExportAllSeparated(
                invalidPlan, output, "A区", true, true, false, terrain, 50));
            Equal(beforeKmlHash, GeneratedArtifactIdentity.Hash(kml));
            Equal(beforeCsvHash, GeneratedArtifactIdentity.Hash(csv));
            True(!Directory.GetDirectories(Path.GetDirectoryName(output)!, Path.GetFileName(output) + ".staging-*").Any(),
                "failed route staging leaked");

            XDocument routeKml = XDocument.Load(kml);
            XElement routeRoot = routeKml.Root
                ?? throw new InvalidDataException("route KML root missing");
            Equal("kml", routeRoot.Name.LocalName);
            Equal("", routeRoot.Name.NamespaceName);
            True(
                !routeKml.Descendants().Any(x =>
                    x.Name.LocalName is
                        "Folder" or
                        "description" or
                        "SchemaData" or
                        "styleUrl" or
                        "StyleMap" or
                        "PolyStyle" or
                        "tessellate"),
                "route KML must stay aligned with the verified DJI-readable route structure");
            XElement routePlacemark = routeKml
                .Descendants()
                .First(x => x.Name.LocalName == "Placemark");
            Equal(
                "A01",
                routePlacemark.Elements()
                    .First(x => x.Name.LocalName == "name")
                    .Value);
            True(
                routePlacemark.Elements().Any(x =>
                    x.Name.LocalName == "Style"),
                "route KML must use inline Style");
            True(
                routePlacemark.Elements().Any(x =>
                    x.Name.LocalName == "LineString"),
                "route KML must contain LineString");
            XElement routeLineStyle = routeKml
                .Descendants()
                .First(x => x.Name.LocalName == "LineStyle");
            string routeLineColor = routeLineStyle
                .Elements()
                .First(x => x.Name.LocalName == "color")
                .Value;
            string routeLineWidth = routeLineStyle
                .Elements()
                .First(x => x.Name.LocalName == "width")
                .Value;
            Equal("ff80ff80", routeLineColor);
            Equal("0.800000011920929", routeLineWidth);

            XElement routeExtendedData = routeKml
                .Descendants()
                .First(x => x.Name.LocalName == "ExtendedData");
            XElement routeLinePattern = routeExtendedData
                .Elements()
                .First(x =>
                    x.Name.LocalName == "Data" &&
                    (string?)x.Attribute("name") == "LinePattern");
            Equal(
                "65535",
                routeLinePattern.Elements()
                    .First(x => x.Name.LocalName == "value")
                    .Value);

            XElement routeLineString = routeKml
                .Descendants()
                .First(x => x.Name.LocalName == "LineString");
            Equal(
                "absolute",
                routeLineString.Elements()
                    .First(x => x.Name.LocalName == "altitudeMode")
                    .Value);

            string routeCoordinateText = routeLineString.Elements()
                .First(x => x.Name.LocalName == "coordinates")
                .Value;
            string[] routeCoordinateRows = routeCoordinateText.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries);
            True(routeCoordinateRows.Length >= 2, "route KML must contain at least two coordinates");
            True(
                routeCoordinateRows.All(row => row.Split(',').Length == 3),
                "route KML coordinates must remain lon,lat,alt triples");
            True(
                routeCoordinateRows.Length > plan.Sorties[0].Points.Count,
                "DJI route KML must densify long v25 endpoint segments");

            var routeCoordinates = routeCoordinateRows
                .Select(row =>
                {
                    string[] parts = row.Split(',');
                    True(
                        parts[0].Contains('.') && parts[0].Split('.')[1].Length == 6,
                        "route KML longitude must use exactly 6 decimals");
                    True(
                        parts[1].Contains('.') && parts[1].Split('.')[1].Length == 6,
                        "route KML latitude must use exactly 6 decimals");
                    True(
                        parts[2].Contains('.') && parts[2].Split('.')[1].Length == 2,
                        "route KML absolute altitude must use exactly 2 decimals");
                    return (
                        Lon: double.Parse(parts[0], CultureInfo.InvariantCulture),
                        Lat: double.Parse(parts[1], CultureInfo.InvariantCulture),
                        Alt: double.Parse(parts[2], CultureInfo.InvariantCulture));
                })
                .ToArray();

            True(
                routeCoordinates.All(point => point.Alt is > 140 and < 200),
                "route KML altitude must be DEM terrain + 50 m AGL");
            for (int i = 0; i < routeCoordinates.Length - 1; i++)
            {
                double spacingM = RouteCoordinateMath.Haversine(
                    routeCoordinates[i].Lon,
                    routeCoordinates[i].Lat,
                    routeCoordinates[i + 1].Lon,
                    routeCoordinates[i + 1].Lat);
                True(
                    spacingM <= RouteExportService.DjiRouteKmlMaxSpacingM + 0.10,
                    $"route KML spacing too large: {spacingM:F3} m");
            }

            string rawRouteKml = File.ReadAllText(kml);
            True(
                rawRouteKml.StartsWith(
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n",
                    StringComparison.Ordinal),
                "route KML must preserve the verified uppercase UTF-8 declaration and LF newline");
            True(
                !rawRouteKml.Contains(
                    "xmlns",
                    StringComparison.OrdinalIgnoreCase),
                "route KML must not declare XML namespaces");

            string flatKml = Path.Combine(output, "01_KML", "架次01_无高程.kml");
            exporter.WriteSortieKmlWithoutElevation(
                flatKml,
                plan.Sorties[0],
                1,
                plan.Mode,
                plan.CentralMeridian);
            XDocument flatRouteKml = XDocument.Load(flatKml);
            True(
                !flatRouteKml.Descendants().Any(x => x.Name.LocalName == "altitudeMode"),
                "no-elevation route KML must not contain altitudeMode");
            string[] flatCoordinateRows = flatRouteKml
                .Descendants()
                .First(x => x.Name.LocalName == "coordinates")
                .Value
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            True(
                flatCoordinateRows.All(row => row.Split(',').Length == 2),
                "no-elevation route KML must contain lon,lat pairs only");
            True(
                rawRouteKml.Contains(
                    "<visibility>1</visibility>",
                    StringComparison.Ordinal),
                "route KML visibility missing");

            string selectiveRoot = Path.Combine(root, "selective");
            RouteExportDirectoryLayout selective =
                exporter.PrepareSeparatedOutputDirectories(
                    selectiveRoot,
                    "A区",
                    createKml: true,
                    createCsv: false,
                    createKmz: false);
            True(Directory.Exists(selective.KmlDirectory), "selective KML directory missing");
            True(!Directory.Exists(selective.CsvDirectory), "CSV directory must be lazy when not requested");
            True(!Directory.Exists(selective.KmzDirectory), "KMZ directory must remain lazy while frozen");

            // v25 CSV has no header and uses original point name, latitude, longitude.
            string firstCsvLine = File.ReadLines(csv).First().TrimStart('\uFEFF');
            True(firstCsvLine.StartsWith("0/L99,", StringComparison.Ordinal) ||
                 firstCsvLine.StartsWith("1/L99,", StringComparison.Ordinal),
                 "v25 CSV should begin with original point name, not a header row");

            // KMZ is frozen and must not be a compile-time resource dependency.
            // Calling the frozen template entry point must fail explicitly.
            Throws<NotSupportedException>(
                () => RouteExportService.LoadEmbeddedDjiTemplate());
        });
    }

    private static void TestRouteV25Calibrated020537()
    {
        var controls = new[]
        {
            (Lon: 99.19250000, Lat: 45.23000000, XNorth: 5010523.233, YEasting: 515117.169),
            (Lon: 99.34944444, Lat: 45.23000000, XNorth: 5010564.615, YEasting: 527442.132),
            (Lon: 99.34944444, Lat: 45.19583333, XNorth: 5006767.470, YEasting: 527458.569),
            (Lon: 99.19250000, Lat: 45.19583333, XNorth: 5006726.088, YEasting: 515126.223)
        };

        foreach (var c in controls)
        {
            var en = RouteCoordinateMath.ResolveProjectedEN(c.XNorth, c.YEasting);
            Equal("X=N / Y=E", en.Axis);
            var ll = RouteCoordinateMath.GkToWgs84(en.E, en.N, 99.0);
            Equal(c.Lon, ll.Lon, 2e-7);
            Equal(c.Lat, ll.Lat, 2e-7);
        }

        double? inferred = RouteCoordinateMath.InferCentralMeridian(
        [
            new RawRoutePoint(5010523.233, 515117.169, "0/L1", 1),
            new RawRoutePoint(5006726.088, 515126.223, "1/L1", 2)
        ],
        Path.Combine("D:", "020537", "route.csv"));
        True(!inferred.HasValue, "020537 project/folder identifier must not force 99E");

        double? explicitCm = RouteCoordinateMath.InferCentralMeridian(
        [
            new RawRoutePoint(5010523.233, 515117.169, "0/L1", 1),
            new RawRoutePoint(5006726.088, 515126.223, "1/L1", 2)
        ],
        Path.Combine("D:", "020537", "route_CM108.csv"));
        Equal(108.0, explicitCm!.Value, 1e-12);
    }

    private static void TestRouteV25ProjectCompatibility()
    {
        WithTempRoot(root =>
        {
            string project = Path.Combine(root, "legacy_v25_project.json");
            string json = """
            {
              "csv_path": "D:\\\\020537\\\\route.csv",
              "coord_mode": "自动判断",
              "headerless_order": "Point,Y,X",
              "axis_order_text": "Point,Y,X",
              "central_meridian": "99",
              "max_sortie_km": 18,
              "output_dir": "D:\\\\020537\\\\out",
              "start_direction": "西南",
              "selected_start_pid": "P001-0",
              "active_start_ep": 0,
              "kmz_agl": "50",
              "kmz_speed": "12",
              "sorties": [
                {
                  "Name": "Sortie 01",
                  "Points": [
                    { "Line": "L1", "Endpoint": 0, "X": 5010523.233, "Y": 515117.169, "Label": "0/L1", "Pid": "P001-0" },
                    { "Line": "L1", "Endpoint": 1, "X": 5006726.088, "Y": 515126.223, "Label": "1/L1", "Pid": "P001-1" }
                  ],
                  "SourceLines": ["L1"]
                }
              ]
            }
            """;
            File.WriteAllText(project, json, new UTF8Encoding(false));

            var service = new RouteProjectService();
            RouteProjectFile loaded = service.Load(project);
            Equal(99.0, loaded.CentralMeridian, 1e-12);
            Equal(50.0, loaded.KmzAglM, 1e-12);
            Equal(12.0, loaded.KmzSpeedMps, 1e-12);
            Equal("自动", loaded.PartitionStartSide);
            Equal(1, loaded.Sorties.Count);
            Equal("P001-0", loaded.Sorties[0].Points[0].Pid);

            string roundtrip = Path.Combine(root, "roundtrip.json");
            // Reuse a minimal typed plan only to verify the output representation.
            var plan = new RoutePlanResult
            {
                SourcePath = loaded.CsvPath,
                Mode = "projected",
                CentralMeridian = loaded.CentralMeridian,
                StartPoint = loaded.Sorties[0].Points[0],
                ActiveStartEndpoint = 0,
                Sorties = loaded.Sorties,
                Options = new RoutePlanOptions
                {
                    MaxSortieKm = loaded.MaxSortieKm,
                    KmzAglM = loaded.KmzAglM,
                    KmzSpeedMps = loaded.KmzSpeedMps
                },
                PartitionCode = "P02",
                PartitionManifestPath = Path.Combine(root, "partition_manifest.json")
            };
            plan.Sorties[0].Name = "P02-A01";
            service.Save(roundtrip, plan, loaded.OutputDirectory,
                loaded.HeaderlessOrder, loaded.CoordMode, loaded.StartDirection,
                "东侧");
            string saved = File.ReadAllText(roundtrip);
            True(saved.Contains("\"central_meridian\": \"99\""), "CM should remain BAT-compatible string JSON");
            True(saved.Contains("\"kmz_agl\": \"50\""), "AGL should remain BAT-compatible string JSON");
            True(saved.Contains("\"kmz_speed\": \"12\""), "speed should remain BAT-compatible string JSON");
            RouteProjectFile partitionRoundtrip = service.Load(roundtrip);
            Equal("P02", partitionRoundtrip.PartitionCode);
            Equal("东侧", partitionRoundtrip.PartitionStartSide);
            Equal("P02-A01", partitionRoundtrip.Sorties[0].Name);
            Equal(
                Path.GetFullPath(plan.PartitionManifestPath),
                Path.GetFullPath(partitionRoundtrip.PartitionManifestPath));
        });
    }

    private static void TestPartitionRouteIdentityMerge()
    {
        WithTempRoot(root =>
        {
            string routeProjectDirectory = Path.Combine(root, "route-projects");
            Directory.CreateDirectory(routeProjectDirectory);
            var service = new RouteProjectService();

            RoutePlanResult BuildPartition(
                string partitionCode,
                double startLon,
                double endLon)
            {
                var l1a = new RouteEndpoint
                {
                    Line = "L001", Endpoint = 0,
                    X = startLon, Y = 39.0000,
                    Label = "L001", Pid = partitionCode + "-L001-0"
                };
                var l1b = new RouteEndpoint
                {
                    Line = "L001", Endpoint = 1,
                    X = endLon, Y = 39.0000,
                    Label = "L001", Pid = partitionCode + "-L001-1"
                };
                var l2a = new RouteEndpoint
                {
                    Line = "L002", Endpoint = 0,
                    X = startLon, Y = 39.0010,
                    Label = "L002", Pid = partitionCode + "-L002-0"
                };
                var l2b = new RouteEndpoint
                {
                    Line = "L002", Endpoint = 1,
                    X = endLon, Y = 39.0010,
                    Label = "L002", Pid = partitionCode + "-L002-1"
                };
                var sortie = new RouteSortie
                {
                    Name = partitionCode + "-A01",
                    Points = [l1a, l1b, l2b, l2a],
                    SourceLines = ["L001", "L002"]
                };
                return new RoutePlanResult
                {
                    SourcePath = Path.Combine(root, partitionCode + "-lines.csv"),
                    Mode = "geographic",
                    CentralMeridian = 0d,
                    StartPoint = l1a,
                    ActiveStartEndpoint = 0,
                    Sorties = [sortie],
                    PartitionCode = partitionCode,
                    PartitionManifestPath = Path.Combine(root, "partition_manifest.json"),
                    Options = new RoutePlanOptions
                    {
                        CoordinateMode = RouteCoordinateMode.Geographic,
                        MaxSortieKm = 18d
                    }
                };
            }

            RoutePlanResult p01 = BuildPartition("P01", 116.0000, 116.0100);
            RoutePlanResult p02 = BuildPartition("P02", 116.0100, 116.0200);
            File.WriteAllText(p01.SourcePath, "P01", Encoding.UTF8);
            File.WriteAllText(p02.SourcePath, "P02", Encoding.UTF8);

            service.Save(
                Path.Combine(routeProjectDirectory, "demo_P01_外业测线任务基准.json"),
                p01,
                Path.Combine(root, "route-out", "P01"),
                "Point,Y,X",
                "经纬度",
                "西南",
                "自动（共同切分边界）");
            service.Save(
                Path.Combine(routeProjectDirectory, "demo_P02_外业测线任务基准.json"),
                p02,
                Path.Combine(root, "route-out", "P02"),
                "Point,Y,X",
                "经纬度",
                "西南",
                "自动（共同切分边界）");

            var field = new FieldOperationsAnalysisService();
            FieldRouteSnapshot? snapshot = field.TryLoadManagedRouteSnapshot(
                routeProjectDirectory,
                Path.Combine(root, "missing-route-export"));
            True(snapshot is not null, "partition route baselines did not merge");
            True(snapshot!.HasSurveyLineIdentity, "merged partition route lost formal survey-line identity");
            Equal(2, snapshot.Sorties.Count);
            Equal("P01-A01", snapshot.Sorties[0].Name);
            Equal("P02-A01", snapshot.Sorties[1].Name);
            Equal(4, snapshot.SurveyLines.Count);

            PlannedSurveyLineTask p01L1 = snapshot.SurveyLines.Single(x =>
                x.PartitionCode == "P01" && x.LineName == "L001");
            PlannedSurveyLineTask p02L1 = snapshot.SurveyLines.Single(x =>
                x.PartitionCode == "P02" && x.LineName == "L001");
            Equal("L001", p01L1.LineName);
            Equal("L001", p02L1.LineName);
            Equal(p01L1.MotherLineTaskId, p02L1.MotherLineTaskId);
            True(
                !p01L1.TaskId.Equals(p02L1.TaskId, StringComparison.OrdinalIgnoreCase),
                "clipped partition segments must have distinct physical task identities");
            Equal(p01L1.TaskId, p01L1.SegmentId);
            Equal(p02L1.TaskId, p02L1.SegmentId);
            True(
                snapshot.SurveyLines.All(x => x.LineName is "L001" or "L002"),
                "formal partition routes must not invent sortie-scoped names such as P01-A01_L001");
        });
    }

    private static void TestDynamicNoise()
    {
        WithTempRoot(root =>
        {
            string csv = Path.Combine(root, "noise.csv");
            File.WriteAllText(csv,
                "T\n0\n1\n4\n9\n16\n25\n36\n",
                new UTF8Encoding(true));

            var service = new DynamicNoiseService();
            DynamicNoiseResult result = service.Compute(
                csv,
                tColumnIndex: 0,
                groupColumnIndex: null,
                xColumnIndex: null,
                yColumnIndex: null,
                coordModeRequested: "自动判断",
                useGradientFilter: false,
                gradientLimitNtKm: 600);

            Equal(1, result.Summary.ValidLineCount);
            Equal(0.0, result.Lines.Single().Sv, 1e-12);
            True(result.Lines.Single().BN >= 2, "expected at least two valid fourth-difference values");
        });
    }

    private static void TestGroupAverage()
    {
        WithTempRoot(root =>
        {
            string csv = Path.Combine(root, "avg.csv");
            File.WriteAllText(csv,
                "Line,T\nL10,1\nL2,2\nL10,3\nL2,bad\n",
                new UTF8Encoding(true));

            var service = new TextTableAnalysisService();
            GroupAverageResult result = service.ComputeGroupAverage(csv, 1, 2, 2);
            Equal(2, result.Rows.Count);
            Equal("L2", result.Rows[0].Group);
            Equal("L10", result.Rows[1].Group);
            Equal(2.0, result.Rows[0].Average!.Value, 1e-12);
            Equal(2.0, result.Rows[1].Average!.Value, 1e-12);
            Equal(1, result.SkippedValueCount);

            // v0.72 Excel处理: same header names may appear in a different order.
            // The merge must keep table A's header order, append A then B, and
            // reorder B by header name instead of silently mixing columns.
            string tableA = Path.Combine(root, "table-a.csv");
            string tableB = Path.Combine(root, "table-b.csv");
            string merged = Path.Combine(root, "merged.csv");
            File.WriteAllText(tableA, "A,B,C\n1,2,3\n", new UTF8Encoding(true));
            File.WriteAllText(tableB, "C,A,B\n6,4,5\n", new UTF8Encoding(true));

            var mergeService = new SameHeaderTableMergeService();
            TableHeaderComparison comparison = mergeService.CompareHeaders(tableA, tableB);
            True(comparison.Compatible, "same header set should be merge-compatible");
            True(!comparison.SameOrder, "test requires different source column order");

            SameHeaderMergeResult merge = mergeService.Merge(tableA, tableB, merged);
            Equal(2L, merge.TotalDataRows);
            True(merge.SecondTableReordered, "table B should be reordered to table A header order");
            TextTable mergedTable = service.Read(merged);
            Equal("A", mergedTable.Headers[0]);
            Equal("B", mergedTable.Headers[1]);
            Equal("C", mergedTable.Headers[2]);
            Equal("1", mergedTable.Rows[0][0]);
            Equal("2", mergedTable.Rows[0][1]);
            Equal("3", mergedTable.Rows[0][2]);
            Equal("4", mergedTable.Rows[1][0]);
            Equal("5", mergedTable.Rows[1][1]);
            Equal("6", mergedTable.Rows[1][2]);

            string mismatch = Path.Combine(root, "table-mismatch.csv");
            File.WriteAllText(mismatch, "A,B,D\n7,8,9\n", new UTF8Encoding(true));
            TableHeaderComparison blocked = mergeService.CompareHeaders(tableA, mismatch);
            True(!blocked.Compatible, "different header fields must block merge");
            True(blocked.OnlyInFirst.Contains("C"), "missing C mismatch not reported");
            True(blocked.OnlyInSecond.Contains("D"), "extra D mismatch not reported");

            // v0.73 Oasis处理: exact channel names match automatically, while the
            // special i--- / i### placeholder channel may have different names.
            // Any other different-name channel must remain a user decision.
            var gdbA = new OasisGdbInspection(
                "A.gdb",
                ["UTCData", "UTCTime", "daiding", "sdf"],
                ["D0"],
                ["sdf"]);
            var gdbB = new OasisGdbInspection(
                "B.gdb",
                ["UTCTime", "UTCData", "rigai", "zi"],
                ["D0"],
                ["zi"]);
            OasisGdbMappingPlan gdbPlan = OasisGdbHeaderMapping.BuildPlan(gdbA, gdbB);
            Equal(3, gdbPlan.AutomaticMappings.Count);
            Equal(1, gdbPlan.UnmatchedDestinationChannels.Count);
            Equal("daiding", gdbPlan.UnmatchedDestinationChannels[0]);
            Equal(1, gdbPlan.UnmatchedSourceChannels.Count);
            Equal("rigai", gdbPlan.UnmatchedSourceChannels[0]);
            True(
                gdbPlan.AutomaticMappings.Any(x =>
                    x.SourceChannel == "zi" && x.DestinationChannel == "sdf" && x.MatchMode == "占位列自动"),
                "Oasis placeholder channel should match by content role instead of header name");

            IReadOnlyList<OasisChannelMapping> resolvedGdb = OasisGdbHeaderMapping.ResolveMappings(
                gdbPlan,
                new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["rigai"] = "daiding"
                });
            Equal(4, resolvedGdb.Count);
            True(
                resolvedGdb.Any(x =>
                    x.SourceChannel == "rigai" && x.DestinationChannel == "daiding" && x.MatchMode == "用户选择"),
                "manual Oasis channel mapping missing");
        });
    }

    private static void TestElevationKml()
    {
        WithTempRoot(root =>
        {
            string csv = Path.Combine(root, "elevation.csv");
            File.WriteAllText(csv,
                "Distance,X,Y,Z\n0,116.0,39.9,100\n100,116.001,39.9,101\n",
                new UTF8Encoding(true));

            var service = new ElevationProfileService();
            IReadOnlyList<ElevationPoint> points = service.Read(csv);
            Equal(2, points.Count);

            string kml = Path.Combine(root, "elevation.kml");
            service.WriteKml(kml, points, 50, "test", "elevation.csv");
            string xml = File.ReadAllText(kml);
            True(xml.Contains("<altitudeMode>absolute</altitudeMode>"), "absolute altitude mode missing");
            True(xml.Contains(",150.000"), "terrain + offset altitude missing");
            True(xml.Contains(",151.000"), "terrain + offset altitude missing");
        });
    }

    private static string[] ReadGzipLines(
        string path)
    {
        using FileStream file =
            File.OpenRead(path);
        using var gzip =
            new GZipStream(
                file,
                CompressionMode.Decompress);
        using var reader =
            new StreamReader(
                gzip,
                Encoding.UTF8);
        string text =
            reader.ReadToEnd();

        return text
            .Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal)
            .Replace(
                '\r',
                '\n')
            .Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool HasGzipMagic(
        string path)
    {
        using FileStream file =
            File.OpenRead(path);
        return file.ReadByte() == 0x1f &&
               file.ReadByte() == 0x8b;
    }

    private static AuditProductionRequest ReviewDraftRequest(FieldMagFolderAnalysis analysis) =>
        new AuditProductionRequest(analysis.Fingerprint, [], [], [], AllowReviewDraft: true)
        { IncludeUnconfirmedSamples = true };

    private static void WithTempRoot(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "MagOps_CSharp_Smoke", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void TestComplexPinPartitionOperationTree()
    {
        var planner = new SurveyLinePlanner();
        SurveyLinePlanResult mother = planner.Plan(
            [
                new SurveyBoundaryPoint(0, 0),
                new SurveyBoundaryPoint(1000, 0),
                new SurveyBoundaryPoint(1000, 1000),
                new SurveyBoundaryPoint(0, 1000)
            ],
            new SurveyLinePlanOptions { SpacingM = 100, AzimuthDegrees = 0 });
        var subdivision = new SurveyLineSubdivisionPlanner();
        SurveySubdivisionOperationPlan plan = subdivision.CreatePinTemplate(
            mother, SurveySubdivisionTemplate.PinTopOneBottomTwo);
        SurveyLineSubdivisionResult result = subdivision.SubdivideByOperations(mother, plan);
        Equal(3, result.Subareas.Count);
        Equal(2, plan.Operations.Count);
        Equal("R02", plan.Operations[1].TargetRegionId);
        var motherNumbers = mother.Lines.Select(x => x.LineNumber).ToHashSet();
        foreach (SurveyLineSubdivisionBlock child in result.Subareas)
            True(child.Lines.All(x => motherNumbers.Contains(x.LineNumber)), "复杂分区不得对子区重新编号母测线。");
    }

    private static void TestStableRouteIdentity()
    {
        WithTempRoot(root =>
        {
            string path = Path.Combine(root, "route.json");
            var sortie = new RouteSortie
            {
                Name = "Mission16", SourceLines = ["L40-3"],
                Points =
                [
                    new RouteEndpoint { Line = "L40-3", X = 0, Y = 0 },
                    new RouteEndpoint { Line = "L40-3", X = 100, Y = 0 }
                ]
            };
            var plan = new RoutePlanResult { Sorties = [sortie] };
            var writer = new RouteProjectService();
            writer.Save(path, plan, root, "Point,Y,X", "自动判断", "西南", "自动");
            RouteTaskReference first = writer.Load(path).Sorties[0].TaskReferences.Single();
            string firstId = first.SurveyTaskStableId;
            string firstLineId = first.SurveyLineStableId;
            string firstMotherId = first.MotherLineStableId;
            True(firstId.StartsWith("task-"), "an opaque identity should be created at save time");

            sortie.Points.Reverse();
            writer.Save(path, plan, root, "Point,Y,X", "自动判断", "西南", "自动");
            RouteTaskReference reversed = writer.Load(path).Sorties[0].TaskReferences.Single();
            Equal(firstId, reversed.SurveyTaskStableId);
            Equal(firstLineId, reversed.SurveyLineStableId);
            Equal(firstMotherId, reversed.MotherLineStableId);

            // Display labels are not identity. A rename with unchanged geometry must
            // inherit the same opaque IDs instead of minting a new business object.
            sortie.SourceLines[0] = "Display-Renamed";
            sortie.Points[0].Line = "Display-Renamed";
            sortie.Points[1].Line = "Display-Renamed";
            writer.Save(path, plan, root, "Point,Y,X", "自动判断", "西南", "自动");
            RouteTaskReference renamed = writer.Load(path).Sorties[0].TaskReferences.Single();
            Equal(firstId, renamed.SurveyTaskStableId);
            Equal(firstLineId, renamed.SurveyLineStableId);
            Equal(firstMotherId, renamed.MotherLineStableId);
            Equal("Display-Renamed", renamed.LineName);

            // A geometry revision attached to the same stable task also keeps identity.
            sortie.Points[1].X += 5;
            writer.Save(path, plan, root, "Point,Y,X", "自动判断", "西南", "自动");
            RouteTaskReference revised = writer.Load(path).Sorties[0].TaskReferences.Single();
            Equal(firstId, revised.SurveyTaskStableId);
            Equal(firstLineId, revised.SurveyLineStableId);
            Equal(firstMotherId, revised.MotherLineStableId);
            True(!string.IsNullOrEmpty(revised.MotherLineStableId),
                "mother identity must be carried alongside task identity");
        });
    }

    private static void TestWholeLegMissionWindows()
    {
        const double metre = 1d / 111319.49d;
        DateTimeOffset start = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);
        PlannedSurveyLineTask Task(string name, string mission, int order, double latitude) =>
            new(order, order, name, mission, name,
                new GeoCoordinate(0, latitude), new GeoCoordinate(200 * metre, latitude), 200)
            { SurveyTaskStableId = "task-" + name };
        var tasks = new[] { Task("L40-3", "Mission16", 1, 0),
            Task("L41-3", "Mission17", 2, 0.001) };
        var points = new List<SurveyTrackPoint>();
        for (int mission = 0; mission < 2; mission++)
        for (int i = 0; i <= 20; i++)
            points.Add(new SurveyTrackPoint(start.AddSeconds(mission * 120 + i * 3),
                new GeoCoordinate(i * 10 * metre, mission * 0.001),
                "Source19", mission * 100 + i));
        // Transit crosses a design line with the wrong heading and is not a pass.
        for (int i = 0; i < 5; i++)
            points.Add(new SurveyTrackPoint(start.AddSeconds(70 + i * 2),
                new GeoCoordinate(50 * metre, (i - 2) * 10 * metre), "Source19", 300 + i));
        var windows = new[]
        {
            new MissionTimeWindow("mission-16", "Mission16", start,
                start.AddSeconds(66), "flight-19") { SurveyTaskStableIds = ["task-L40-3"] },
            new MissionTimeWindow("mission-17", "Mission17", start.AddSeconds(120),
                start.AddSeconds(186), "flight-19") { SurveyTaskStableIds = ["task-L41-3"] }
        };
        SurveyExecutionResult result = new WholeLegMatcherService().Match(tasks, points, windows);
        Equal(2, result.Missions.Count);
        Equal(1, result.PhysicalFlights.Count);
        Equal(2, result.Passes.Count);
        True(result.Passes.Any(p => p.SurveyTaskStableId == "task-L40-3"), "L40-3 lost");
        True(result.Passes.Any(p => p.SurveyTaskStableId == "task-L41-3"), "L41-3 lost");
        True(result.Passes.All(p => p.DurationSec == 60), "short boundary pass should survive");
        True(result.Legs.Any(l => l.Classification == "UNRESOLVED"),
            "unassigned transit should remain visible for review");
        var renamedWindows = new[]
        {
            windows[0] with { PlannedSortieName = "Display name changed" },
            windows[1] with { PlannedSortieName = "Another display name" }
        };
        SurveyExecutionResult renamed = new WholeLegMatcherService().Match(tasks, points, renamedWindows);
        Equal(2, renamed.Passes.Count);
        True(renamed.Passes.Any(p => p.SurveyTaskStableId == "task-L40-3"),
            "Mission matching must not depend on sortie display name");
    }

    private static void TestMissionTaskAuthority()
    {
        const double metre = 1d / 111319.49d;
        DateTimeOffset start = new(2026, 9, 19, 10, 0, 0, TimeSpan.Zero);
        var tasks = new[]
        {
            new PlannedSurveyLineTask(1, 1, "legacy-L1", "LegacySortie", "L1",
                new GeoCoordinate(0, 0), new GeoCoordinate(200 * metre, 0), 200)
                { SurveyTaskStableId = "task-stable-L1" }
        };
        var points = Enumerable.Range(0, 21).Select(i => new SurveyTrackPoint(
            start.AddSeconds(i * 3), new GeoCoordinate(i * 10 * metre, 0), "Source19", i)).ToArray();
        var direct = new MissionTimeWindow("mission-direct", "renamed display", start,
            start.AddSeconds(60), "flight-1") { SurveyTaskStableIds = ["task-stable-L1"] };
        SurveyExecutionResult directResult = new WholeLegMatcherService().Match(tasks, points, [direct]);
        Equal("task-stable-L1", directResult.Passes.Single().SurveyTaskStableId);
        Equal("renamed display", directResult.Missions.Single().PlannedSortieName);
        Equal("task-stable-L1", directResult.Missions.Single().SurveyTaskStableIds.Single());

        // Audit compatibility may map an old sortie name, but must mark the result as review-only.
        var analysis = new FieldMagFolderAnalysis
        {
            Fingerprint = "fixture",
            UavSamples = Enumerable.Range(0, 21).Select(i => new UavMagCanonicalSample(
                start.AddSeconds(i * 3), i * 10 * metre, 0d, 100d, null, 50000d, null,
                "A", 10, "T", "Source19", i, [])).ToArray()
        };
        // A no-sample fixture cannot reach production; this test targets validation ordering.
        var legacy = new AuditProductionRequest("fixture",
            [new MissionTimeWindow("legacy", "LegacySortie", start, start.AddSeconds(60), "flight-1")],
            [], [], AllowReviewDraft: true);
        AuditProductionResult migrated = new AuditProductionService().Analyze(analysis, tasks, legacy);
        True(migrated.BlockedReasons.Any(x => x.StartsWith("LEGACY_MISSION_TASK_MIGRATION_REVIEW:", StringComparison.Ordinal)),
            "legacy sortie-name migration must be explicit review debt");
        Equal("task-stable-L1", migrated.Execution.Missions.Single().SurveyTaskStableIds.Single());

        const string passId = "mission-confirm/leg-0001/pass";
        var reviewRequest = new AuditProductionRequest("fixture",
            [new MissionTimeWindow("mission-confirm", "", start, start.AddSeconds(60), "flight-1")
                { SurveyTaskStableIds = ["task-stable-L1"] }],
            [new PassQualityConfirmation(passId, "PASS", "operator")], [], AllowReviewDraft: true)
        {
            RouteFingerprint = "route-v1",
            RecognitionVersion = WholeLegMatcherService.AlgorithmVersion,
            RequirePassConfirmation = true
        };
        AuditProductionResult notConfirmed = new AuditProductionService().Analyze(
            analysis, tasks, reviewRequest, "route-v1");
        Equal(0, notConfirmed.SelectedSamples.Count);
        True(notConfirmed.BlockedReasons.Any(x => x.StartsWith("PASS_REVIEW_REQUIRED:", StringComparison.Ordinal)),
            "QC and line attribution confirmation must remain separate");

        AuditProductionResult confirmed = new AuditProductionService().Analyze(
            analysis, tasks, reviewRequest with { ConfirmedPassIds = [passId] }, "route-v1");
        True(confirmed.SelectedSamples.Count > 0,
            "a user-confirmed, QC-passed pass should reach the production selector");
        AuditProductionResult algorithmChanged = new AuditProductionService().Analyze(
            analysis, tasks, reviewRequest with { RecognitionVersion = "audit-whole-leg-3", ConfirmedPassIds = [passId] }, "route-v1");
        Equal(0, algorithmChanged.SelectedSamples.Count);
        True(algorithmChanged.BlockedReasons.Any(x => x.StartsWith("RECOGNITION_VERSION_CHANGED:", StringComparison.Ordinal)),
            "new recognition must not reuse earlier algorithm decisions against sequential leg IDs");
        AuditProductionResult manuallyAssigned = new AuditProductionService().Analyze(
            analysis, tasks, reviewRequest with
            {
                ConfirmedPassIds = [passId],
                ManualLegAssignments = [new ManualFlightLegAssignment(
                    "mission-confirm/leg-0001", "task-stable-L1", "operator review")]
            }, "route-v1");
        True(manuallyAssigned.Execution.Legs.Single().ReviewReason.StartsWith("USER_ASSIGNED:", StringComparison.Ordinal),
            "manual leg reassignment must be retained as a separate review decision");
        AuditProductionResult manuallyIncludedLeg = new AuditProductionService().Analyze(
            analysis, tasks, reviewRequest with
            {
                ManualIncludedFlightLegIds = ["mission-confirm/leg-0001"]
            }, "route-v1");
        Equal(21, manuallyIncludedLeg.SelectedSamples.Count);
        True(manuallyIncludedLeg.BlockedReasons.Any(x => x.StartsWith("USER_INCLUDED_UNCONFIRMED_LEG:", StringComparison.Ordinal)),
            "an explicitly included unconfirmed leg must remain marked as a review draft");
        AuditProductionResult stale = new AuditProductionService().Analyze(
            analysis, tasks, reviewRequest with { ConfirmedPassIds = [passId] }, "route-v2");
        Equal(0, stale.SelectedSamples.Count);
        True(stale.BlockedReasons.Any(x => x.StartsWith("ROUTE_REVISION_UNCONFIRMED:", StringComparison.Ordinal)),
            "route changes must invalidate prior confirmations");

        AuditProductionResult manuallyIncluded = new AuditProductionService().Analyze(
            analysis, tasks, new AuditProductionRequest("fixture", [], [], [])
            { RouteFingerprint = "route-v1", IncludeUnconfirmedSamples = true }, "route-v1");
        Equal(21, manuallyIncluded.SelectedSamples.Count);
        True(manuallyIncluded.BlockedReasons.Any(x => x.StartsWith("USER_INCLUDED_UNCONFIRMED:", StringComparison.Ordinal)),
            "explicitly included unconfirmed samples must leave a visible warning");

        var unknown = new AuditProductionRequest("fixture",
            [new MissionTimeWindow("unknown", "", start, start.AddSeconds(60), "flight-1")
                { SurveyTaskStableIds = ["task-does-not-exist"] }], [], [], AllowReviewDraft: true);
        AuditProductionResult unknownResult = new AuditProductionService().Analyze(analysis, tasks, unknown);
        True(unknownResult.IsReviewDraft,
            "an unknown task identity must remain reviewable instead of aborting the workflow");
        True(unknownResult.BlockedReasons.Any(x => x.StartsWith("MISSION_TASK_IDENTITY_UNAVAILABLE:", StringComparison.Ordinal)),
            "unknown Mission task identities must be reported as review warnings");

        var missingIdentityTask = tasks[0] with { SurveyTaskStableId = "" };
        AuditProductionResult missingIdentityResult = new AuditProductionService().Analyze(
            analysis, [missingIdentityTask], new AuditProductionRequest("fixture", [], [], []));
        True(missingIdentityResult.IsReviewDraft,
            "missing task identities must not prevent a review draft");
        True(missingIdentityResult.BlockedReasons.Any(x => x.StartsWith("AMBIGUOUS_IDENTITY:", StringComparison.Ordinal)),
            "missing task identities must be visible to the operator");
    }

    private static void TestAutomaticFlightWindowInference()
    {
        var samples = new List<UavMagCanonicalSample>();
        DateTimeOffset start = new(2026, 9, 20, 3, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 6; i++)
            samples.Add(new(start.AddSeconds(i * 10), 108d + i * 0.0001d, 47d, 1200d, null, 59000d, null, "A", 10, "T", "uav-a.txt", i, []));
        for (int i = 0; i < 6; i++)
            samples.Add(new(start.AddMinutes(20).AddSeconds(i * 10), 108d + i * 0.0001d, 47d, 1200d, null, 59000d, null, "A", 10, "T", "uav-b.txt", i, []));
        FlightWindowInferenceResult result = new FlightWindowInferenceService().Infer(samples);
        Equal(2, result.Candidates.Count);
        True(result.Candidates.All(x => x.Status.StartsWith("INFERRED", StringComparison.Ordinal)),
            "automatic windows must remain inferred review candidates");
        True(result.Candidates[1].Window.StartUtc > result.Candidates[0].Window.EndUtc,
            "a long telemetry gap must split candidate windows");
    }

    private static void TestPassUnionAndSelection()
    {
        Equal(MagneticT0Mode.AutoGlobal, new MagneticPreLevelProcessingOptions().T0Mode);
        var first = new CoverageInterval(0, 0.54);
        var second = new CoverageInterval(0.51, 1);
        SurveyCompletionResult complete = SurveyCoverage.Calculate("task", 1000,
            [first, second]);
        Equal("COMPLETE", complete.Status);
        Equal(1d, complete.CoverageRatio, 1e-9);
        SurveyCompletionResult gap = SurveyCoverage.Calculate("task", 1000,
            [new CoverageInterval(0, 0.47), new CoverageInterval(0.54, 1)]);
        Equal("COMPLETE_GAP_WARNING", gap.Status);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SurveyLinePass Pass(string id, CoverageInterval coverage, DateTimeOffset end) =>
            new(id, id, "mission", "task", [coverage], 1, 2, 1,
                coverage.End - coverage.Start, 0, 0, 0, 60, 0.9,
                "PASS", false, end, ["Source19"]);
        var a = Pass("p1", first, now.AddMinutes(-10));
        var b = Pass("p2", second, now);
        ProductionSelectionResult automatic = SurveyProductionSelection.Select("task", [a, b]);
        Equal("READY", automatic.Status);
        True(automatic.Entries.Any(e => e.PassId == "p2" &&
            e.Interval.Start <= 0.51 && e.Interval.End >= 0.54),
            "later QC pass should win overlap");
        ProductionSelectionResult overridden = SurveyProductionSelection.Select("task", [a, b],
            [new ProductionOverride("task", new CoverageInterval(0.51, 0.54), "p1", "reviewer")]);
        True(overridden.Entries.Any(e => e.PassId == "p1" &&
            e.SelectionReason == "USER_OVERRIDE"), "explicit override should win");
    }

    private static void TestFieldProductionWorkflow()
    {
        WithTempRoot(root =>
        {
            var store = new JsonStore();
            var workspace = new WorkspaceService(store);
            WorkspaceRecord project = workspace.Initialize(root, "Workflow test");
            WorkareaRecord workarea = workspace.RegisterWorkarea(root, "Workflow Workarea");
            var artifacts = new ArtifactService(store, workspace);
            var workflow = new FieldProductionWorkflow(
                new AuditProductionService(),
                new DiurnalCorrectionExportService(),
                artifacts,
                new ProcessingRunService(store, workspace, artifacts));

            FieldMagFolderAnalysis analysis =
                new FieldMagDataService().AnalyzeFolder(CreateFieldMagFixture(root));
            string output = Path.Combine(
                root,
                workarea.Path.Replace('/', Path.DirectorySeparatorChar),
                "06_数据处理",
                "01_日变改正");

            var request = new FieldProductionRequest(
                root,
                output,
                analysis,
                new DiurnalCorrectionOptions(
                    DatumNt: 0d,
                    GemLinkTenthsCompatibility: true,
                    MaxInterpolationGapSeconds: 999d),
                LoadIgrfModels(),
                new MagneticPreLevelProcessingOptions(),
                [],
                new MagneticSurveyLineSegmentationOptions(Enabled: true),
                "",
                "",
                ReviewDraftRequest(analysis),
                new GeneratedArtifactContext(project.ProjectId, workarea.WorkareaId));

            FieldProductionWorkflowResult result = workflow.Execute(request);
            True(result.Export.AuditProduction.IsReviewDraft,
                "fixture without survey tasks must remain an explicit review draft");
            True(result.Export.Files.All(File.Exists),
                "workflow must return committed production bundle paths");
            True(result.RegisteredArtifacts.Count > 0,
                "workflow must register generated artifacts");
            True(result.RegisteredArtifacts.All(x =>
                    x.WorkareaRef.Equals(workarea.WorkareaId, StringComparison.OrdinalIgnoreCase)),
                "workflow artifact registration must preserve canonical workarea identity");
            True(result.RegisteredArtifacts.All(x =>
                    x.WorkareaAssignmentStatus.Equals("ASSIGNED", StringComparison.OrdinalIgnoreCase)),
                "workflow artifacts must be assigned by verified context");
            True(result.RegisteredArtifacts.All(x => x.SourceFileStableIds.Count > 0),
                "artifact registry lost GSM source-file stable lineage");
            True(result.RegisteredArtifacts.All(x => x.GsmSessionStableIds.Count > 0),
                "artifact registry lost GSM session stable lineage");
            ArtifactIdentityInspection fieldIdentity = GeneratedArtifactIdentity.Inspect(
                result.Export.ProcessingCsvPath);
            Equal(2, fieldIdentity.Metadata!.SchemaVersion);
            True(fieldIdentity.Metadata.Lineage!.SourceFileStableIds.Count > 0,
                "field production sidecar lost GSM source-file stable lineage");
            True(fieldIdentity.Metadata.Lineage.GsmSessionStableIds.Count > 0,
                "field production sidecar lost GSM session stable lineage");

            var reviewSession = new AuditProductionService();
            reviewSession.Analyze(analysis, request.SurveyLines, request.AuditRequest, request.SurveyLineFingerprint);
            var reusedProduction = workflow.Execute(request with
                { OutputDirectory = Path.Combine(root, "reuse-production"), ReviewSession = reviewSession });
            True(reviewSession.RecognitionRuns == 1 && reviewSession.RecognitionReuses == 1,
                "formal production must reuse valid live recognition while recomputing selection checks");
            Equal(File.ReadAllText(result.Export.ProcessingCsvPath), File.ReadAllText(reusedProduction.Export.ProcessingCsvPath));
            var liveSession = new ReviewAnalysisSession(new ReviewDecisionState());
            liveSession.Calculate(liveSession.Prepare(analysis, request.SurveyLines, request.AuditRequest, request.SurveyLineFingerprint, true));
            var serialProduction = workflow.Execute(request with
                { OutputDirectory = Path.Combine(root, "serial-session-production"), CalculationSession = liveSession });
            True(liveSession.Service.RecognitionRuns == 1 && liveSession.Service.RecognitionReuses == 1,
                "formal production must use the serialized session and still recheck production selection");
            Equal(File.ReadAllText(result.Export.ProcessingCsvPath), File.ReadAllText(serialProduction.Export.ProcessingCsvPath));

            CheckProductionPorts(request, artifacts, new ProcessingRunService(store, workspace, artifacts));

            var invalidContext = request with
            {
                OutputDirectory = Path.Combine(root, "invalid-context"),
                ArtifactContext = GeneratedArtifactContext.Unavailable
            };
            Throws<InvalidOperationException>(() => workflow.Execute(invalidContext));
            True(!Directory.Exists(invalidContext.OutputDirectory + ".review-draft"),
                "invalid artifact context must fail before export");

            var unknownWorkarea = request with
            {
                OutputDirectory = Path.Combine(root, "unknown-workarea"),
                ArtifactContext = new GeneratedArtifactContext(project.ProjectId, "missing-workarea")
            };
            Throws<InvalidOperationException>(() => workflow.Execute(unknownWorkarea));
            True(!Directory.Exists(unknownWorkarea.OutputDirectory + ".review-draft"),
                "unknown WorkareaId must fail before export");

            var outside = request with
            {
                OutputDirectory = Path.Combine(Path.GetTempPath(), "MagOps-workflow-outside-" + Guid.NewGuid().ToString("N"))
            };
            try
            {
                Throws<InvalidOperationException>(() => workflow.Execute(outside));
            }
            finally
            {
                if (Directory.Exists(outside.OutputDirectory))
                    Directory.Delete(outside.OutputDirectory, recursive: true);
                if (Directory.Exists(outside.OutputDirectory + ".review-draft"))
                    Directory.Delete(outside.OutputDirectory + ".review-draft", recursive: true);
            }
        });
    }

    private static void TestGeneratedArtifactIdentity()
    {
        WithTempRoot(root =>
        {
            var store = new JsonStore();
            var workspace = new WorkspaceService(store);
            WorkspaceRecord project = workspace.Initialize(root, "Identity test");
            WorkareaRecord workarea = workspace.RegisterWorkarea(root, "Identity Workarea");
            var artifactContext = new GeneratedArtifactContext(project.ProjectId, workarea.WorkareaId);
            var analysis = new FieldMagDataService().AnalyzeFolder(CreateFieldMagFixture(root));
            string output = Path.Combine(root, "identity-export");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "unrelated.txt"), "keep");
            var exporter = new DiurnalCorrectionExportService();
            var options = new DiurnalCorrectionOptions(DatumNt: 0d, GemLinkTenthsCompatibility: true, MaxInterpolationGapSeconds: 999d);
            var first = exporter.WriteNativeCorrection(output, analysis, options, LoadIgrfModels(),
                     auditRequest: ReviewDraftRequest(analysis), artifactContext: artifactContext);
            True(first.Files.All(File.Exists), "final result paths must exist after staging commits");
            string visualization = first.Files.Single(x => x.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
            True(File.ReadAllText(visualization).Contains("审计轨迹审查", StringComparison.Ordinal),
                "audit track visualization must be emitted");
            True(File.ReadAllText(visualization).Contains("REVIEW_DRAFT", StringComparison.Ordinal),
                "visualization must expose the audit status");
            True(first.Files.All(x => Path.GetFileName(x).Contains("__MagOps_", StringComparison.Ordinal)), "all managed output names need stage tags");
            Equal("keep", File.ReadAllText(Path.Combine(output, "unrelated.txt")));
            var identity = GeneratedArtifactIdentity.Inspect(first.ProcessingCsvPath);
            Equal("VERIFIED", identity.Status);
            Equal("processed_magnetic", identity.Metadata!.Role);
            Equal("prelevel_dtgz", identity.Metadata.ProcessingStage);
            True(identity.Metadata.SourceFiles.Count > 0, "source provenance missing");
            Equal(project.ProjectId, identity.Metadata.ProjectId);
            Equal(workarea.WorkareaId, identity.Metadata.WorkareaId);
            Equal("WORKAREA", identity.Metadata.ContextStatus);
            Equal("REVIEW_DRAFT", identity.Metadata.QualityStatus);
            foreach (string file in first.Files.Where(x => !x.EndsWith(GeneratedArtifactIdentity.Suffix)))
                Equal("VERIFIED", GeneratedArtifactIdentity.Inspect(file).Status);
            using (var manifest = JsonDocument.Parse(File.ReadAllText(first.ManifestPath)))
            {
                JsonElement artifactContextJson = manifest.RootElement.GetProperty("artifact_context");
                Equal(project.ProjectId, artifactContextJson.GetProperty("project_id").GetString());
                Equal(workarea.WorkareaId, artifactContextJson.GetProperty("workarea_id").GetString());
                Equal("WORKAREA", artifactContextJson.GetProperty("context_status").GetString());
                JsonElement productionLineage = manifest.RootElement.GetProperty("production_lineage");
                True(productionLineage.GetProperty("source_file_stable_ids").GetArrayLength() > 0,
                    "diurnal manifest missing GSM source-file stable lineage");
                True(productionLineage.GetProperty("gsm_session_stable_ids").GetArrayLength() > 0,
                    "diurnal manifest missing GSM session stable lineage");
                foreach (var entry in manifest.RootElement.GetProperty("outputs").EnumerateArray())
                    True(File.Exists(Path.Combine(Path.GetDirectoryName(first.ManifestPath)!, entry.GetProperty("file").GetString()!)), "manifest references obsolete name: " + entry.GetProperty("file").GetString());
            }
            var artifacts = new ArtifactService(store, workspace);
            var registered = artifacts.RegisterGeneratedFile(root, first.ProcessingCsvPath, "processing_diurnal");
            Equal(identity.Metadata.ArtifactId, registered.ArtifactId);
            Equal("processing", registered.Role);
            Equal("prelevel_dtgz", registered.ProcessingStage);
            Equal(workarea.WorkareaId, registered.WorkareaRef);
            Equal("ASSIGNED", registered.WorkareaAssignmentStatus);
            string content = File.ReadAllText(first.ProcessingCsvPath);
            var second = exporter.WriteNativeCorrection(output, analysis, options, LoadIgrfModels(),
                     auditRequest: ReviewDraftRequest(analysis), artifactContext: artifactContext);
            Equal(content, File.ReadAllText(second.ProcessingCsvPath));
            Equal(identity.Metadata.ArtifactId, GeneratedArtifactIdentity.Inspect(second.ProcessingCsvPath).Metadata!.ArtifactId);
            Equal(identity.Metadata.ArtifactId, artifacts.RegisterGeneratedFile(root, second.ProcessingCsvPath, "processing_diurnal").ArtifactId);
            string historyDirectory = Path.GetDirectoryName(first.ProcessingCsvPath)! + ".previous";
            Equal(content, File.ReadAllText(Path.Combine(historyDirectory, Path.GetFileName(first.ProcessingCsvPath))));
            string history = Path.Combine(historyDirectory, Path.GetFileName(first.ProcessingCsvPath));
            True(GeneratedArtifactIdentity.Inspect(history).IsHistoricalVersion, "verified snapshot must be historical");
            True(!GeneratedArtifactIdentity.Inspect(second.ProcessingCsvPath).IsHistoricalVersion, "current output must remain current");
            artifacts.Scan(root, withHash: true);
            True(!artifacts.List(root).Any(x => x.Path.Contains("identity-export.previous/", StringComparison.Ordinal)),
                "history must not pollute current registry");
            Throws<InvalidOperationException>(() => artifacts.RegisterGeneratedFile(root, history));

            string renamed = Path.Combine(root, "renamed.csv");
            File.Copy(second.ProcessingCsvPath, renamed);
            File.Copy(second.ProcessingCsvPath + GeneratedArtifactIdentity.Suffix, renamed + GeneratedArtifactIdentity.Suffix);
            Equal(identity.Metadata.ArtifactId, GeneratedArtifactIdentity.Inspect(renamed).Metadata!.ArtifactId);
            string registryHash = GeneratedArtifactIdentity.Hash(ProjectPaths.Json(root, "artifacts.json"));
            Throws<InvalidOperationException>(() => artifacts.RegisterGeneratedFile(root, renamed));
            Throws<InvalidOperationException>(() => artifacts.Scan(root, withHash: true));
            Equal(registryHash, GeneratedArtifactIdentity.Hash(ProjectPaths.Json(root, "artifacts.json")));
            File.AppendAllText(renamed, "changed");
            Equal("HASH_MISMATCH", GeneratedArtifactIdentity.Inspect(renamed).Status);
            File.WriteAllText(renamed + GeneratedArtifactIdentity.Suffix, "{}");
            Equal("INVALID_METADATA", GeneratedArtifactIdentity.Inspect(renamed).Status);
            File.Delete(renamed + GeneratedArtifactIdentity.Suffix);
            Equal("UNTAGGED", GeneratedArtifactIdentity.Inspect(renamed).Status);
            string unpaired = Path.Combine(root, "sample__MagOps_prelevel_dtgz.csv");
            File.WriteAllText(unpaired, "x,y\n1,2\n");
            Equal("MISSING_METADATA", GeneratedArtifactIdentity.Inspect(unpaired).Status);

            string unknownContext = Path.Combine(root, "unknown-context.txt");
            File.WriteAllText(unknownContext, "identity context");
            GeneratedArtifactIdentity.Write(
                unknownContext, "processing_summary", "identity_context_test",
                "fixture", [], context: new GeneratedArtifactContext(project.ProjectId, "missing-workarea"));
            Throws<InvalidOperationException>(() => artifacts.RegisterGeneratedFile(root, unknownContext));

            // An invalid previous identity must block replacement and preserve every file.
            string reviewOutput = Path.GetDirectoryName(second.CorrectedCsvPath)!;
            string collision = Path.Combine(reviewOutput, Path.GetFileName(second.CorrectedCsvPath) + GeneratedArtifactIdentity.Suffix);
            File.Delete(collision);
            Directory.CreateDirectory(collision);
            var before = Directory.GetFiles(reviewOutput).ToDictionary(path => Path.GetFileName(path), GeneratedArtifactIdentity.Hash);
            Throws<InvalidDataException>(() => exporter.WriteNativeCorrection(output, analysis, options, LoadIgrfModels(),
                     auditRequest: ReviewDraftRequest(analysis), artifactContext: artifactContext));
            var after = Directory.GetFiles(reviewOutput).ToDictionary(path => Path.GetFileName(path), GeneratedArtifactIdentity.Hash);
            Equal(before.Count, after.Count);
            foreach (var pair in before) Equal(pair.Value, after[pair.Key]);
            True(!Directory.GetDirectories(root, "identity-export.staging-*").Any(), "failed staging leaked");
        });
    }

    private static void TestAtomicOutputBundle()
    {
        WithTempRoot(root =>
        {
            string target = Path.Combine(root, "bundle");
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "result.txt"), "old");
            Throws<InvalidOperationException>(() => AtomicDirectoryBundle.Execute(
                target,
                stage => { File.WriteAllText(Path.Combine(stage, "result.txt"), "new"); return 1; },
                (stage, _) => True(File.Exists(Path.Combine(stage, "result.txt")), "staging incomplete"),
                afterOldMovedForTest: () => throw new InvalidOperationException("injected")));
            Equal("old", File.ReadAllText(Path.Combine(target, "result.txt")));
            AtomicDirectoryBundle.Execute(target,
                stage => { File.WriteAllText(Path.Combine(stage, "result.txt"), "new"); return 1; },
                (stage, _) => True(File.Exists(Path.Combine(stage, "result.txt")), "staging incomplete"));
            Equal("new", File.ReadAllText(Path.Combine(target, "result.txt")));
            Equal("old", File.ReadAllText(Path.Combine(target + ".previous", "result.txt")));
        });
    }

    private static void TestRouteApplicationWorkflow()
    {
        WithTempRoot(root =>
        {
            var store = new JsonStore();
            var workspace = new WorkspaceService(store);
            WorkspaceRecord project = workspace.Initialize(root, "Route workflow");
            WorkareaRecord workarea = workspace.RegisterWorkarea(root, "Route Area");
            var artifacts = new ArtifactService(store, workspace);
            var processingRuns = new ProcessingRunService(
                store, workspace, artifacts);
            var workflow = new RoutePlanningWorkflow(
                new RoutePlanner(),
                new ConcaveSplitRoutePlanService(),
                new RouteExecutionSegmentPlanner(),
                new RouteProjectService(),
                new RouteExportService(),
                artifacts,
                new RouteManifestWriter(),
                new RouteResultService(store, workspace, artifacts),
                processingRuns);

            string csv = Path.Combine(root, "route-source.csv");
            File.WriteAllText(csv,
                "L1,0,0\nL1,0,100\nL2,10,0\nL2,10,100\n", Encoding.UTF8);
            RoutePlanResult plan = workflow.Generate(new RoutePlanningRequest(
                csv,
                new RoutePlanOptions
                {
                    CoordinateMode = RouteCoordinateMode.Projected,
                    HeaderlessOrder = "Point,X,Y",
                    CentralMeridian = 102,
                    MaxSortieKm = 1
                }));
            Equal(2, plan.OrderedLines.Count);
            Equal("A01", plan.Sorties[0].Name);
            plan.Sorties[0].TaskReferences.Add(new RouteTaskReference
            {
                LineName = "L1",
                SurveyTaskStableId = "task-stable-001",
                SurveyLineStableId = "line-stable-001",
                ExecutionSegmentStableId = "segment-stable-001"
            });

            string projectPath = Path.Combine(root, "route-project.json");
            workflow.SaveProject(projectPath, plan, Path.Combine(root, "route-output"),
                "Point,X,Y", "投影坐标", "西南", "自动");
            Equal("magops-route-project-2", workflow.LoadProject(projectPath).SchemaVersion);

            string output = Path.Combine(root, workarea.Path.Replace('/', Path.DirectorySeparatorChar),
                "04_航线设计与任务文件", "05_设计航线");
            RouteWorkflowExportResult exported = workflow.Export(plan,
                new RouteExportRequest(root, output, "Route Area", ExportKml: true, ExportCsv: true,
                    ArtifactContext: new GeneratedArtifactContext(project.ProjectId, workarea.WorkareaId)));
            True(exported.Bundle.OutputPaths.All(File.Exists),
                "route workflow did not commit CSV/KML outputs");
            Equal(exported.Bundle.OutputPaths.Count, exported.RegisteredArtifacts.Count);
            True(exported.RegisteredArtifacts.All(x =>
                    x.WorkareaRef.Equals(workarea.WorkareaId, StringComparison.OrdinalIgnoreCase)),
                "route workflow artifact registration lost workarea authority");
            foreach (string path in exported.Bundle.OutputPaths)
            {
                ArtifactIdentityInspection routeIdentity = GeneratedArtifactIdentity.Inspect(path);
                Equal("VERIFIED", routeIdentity.Status);
                Equal(2, routeIdentity.Metadata!.SchemaVersion);
                Equal(project.ProjectId, routeIdentity.Metadata.ProjectId);
                Equal(workarea.WorkareaId, routeIdentity.Metadata.WorkareaId);
                Equal("WORKAREA", routeIdentity.Metadata.ContextStatus);
                Equal("route_export", routeIdentity.Metadata.ProcessingStage);
                True(routeIdentity.Metadata.Lineage!.SurveyTaskStableIds.Contains("task-stable-001"),
                    "route sidecar lost SurveyTask stable lineage");
                True(routeIdentity.Metadata.Lineage.SurveyLineStableIds.Contains("line-stable-001"),
                    "route sidecar lost SurveyLine stable lineage");
                True(routeIdentity.Metadata.Lineage.ExecutionSegmentStableIds.Contains("segment-stable-001"),
                    "route sidecar lost ExecutionSegment stable lineage");
                True(File.Exists(path + GeneratedArtifactIdentity.Suffix),
                    "route identity sidecar missing from committed atomic bundle");
            }
            True(!Directory.GetDirectories(Path.GetDirectoryName(output)!,
                    Path.GetFileName(output) + ".staging-*").Any(),
                "route workflow leaked identity staging directory");
            True(exported.RegisteredArtifacts.All(x =>
                    x.MetadataOrigin.Equals("route_export", StringComparison.OrdinalIgnoreCase)),
                "route registry metadata origin must come from formal workflow");
            True(exported.RegisteredArtifacts.All(x =>
                    x.Role.Equals("route", StringComparison.OrdinalIgnoreCase) &&
                    x.ProcessingStage.Equals("route_export", StringComparison.OrdinalIgnoreCase)),
                "route registry must preserve verified sidecar role/stage");

            var firstArtifactIds = exported.RegisteredArtifacts.ToDictionary(
                x => x.Path, x => x.ArtifactId, StringComparer.OrdinalIgnoreCase);
            string stalePath = exported.Bundle.OutputPaths[0];
            GeneratedArtifactIdentity.Write(
                stalePath, "route", "route_export", "stale-sidecar-fixture", [csv],
                artifactId: "artifact_stale_interrupted_export",
                context: new GeneratedArtifactContext(project.ProjectId, workarea.WorkareaId),
                lineage: new GeneratedArtifactLineage(
                    [], [], ["task-stable-001"], ["line-stable-001"], ["segment-stable-001"]));
            RouteWorkflowExportResult reexported = workflow.Export(plan,
                new RouteExportRequest(root, output, "Route Area", ExportKml: true, ExportCsv: true,
                    ArtifactContext: new GeneratedArtifactContext(project.ProjectId, workarea.WorkareaId)));
            foreach (ArtifactRecord artifact in reexported.RegisteredArtifacts)
            {
                True(firstArtifactIds.TryGetValue(artifact.Path, out string? expectedId),
                    "re-export produced an unexpected registered route path");
                Equal(expectedId!, artifact.ArtifactId);
            }
            ArtifactIdentityInspection reconciled = GeneratedArtifactIdentity.Inspect(stalePath);
            Equal("VERIFIED", reconciled.Status);
            Equal(firstArtifactIds[
                    Path.GetRelativePath(root, stalePath).Replace('\\', '/')],
                reconciled.Metadata!.ArtifactId);

            string formalManifestPath = Path.Combine(output, "formal-route-result.json");
            RouteWorkflowFinalizeResult finalized = workflow.FinalizeManagedExport(
                plan, reexported, root, formalManifestPath,
                new GeneratedArtifactContext(project.ProjectId, workarea.WorkareaId));
            ArtifactIdentityInspection formalManifestIdentity =
                GeneratedArtifactIdentity.Inspect(finalized.ManifestPath);
            Equal("VERIFIED", formalManifestIdentity.Status);
            Equal("route_manifest", formalManifestIdentity.Metadata!.ProcessingStage);
            Equal(workarea.WorkareaId, formalManifestIdentity.Metadata.WorkareaId);
            Equal(project.ProjectId, finalized.ImportedResult.ProjectId);
            Equal(workarea.WorkareaId, finalized.ImportedResult.WorkareaId);
            Equal("COMPLETED", finalized.ProcessingRun.Status);
            True(finalized.ProcessingRun.ArtifactIds.Contains(
                    finalized.ManifestArtifact.ArtifactId),
                "completed route ProcessingRun lost manifest Artifact membership");
            True(artifacts.List(root)
                    .Where(x => finalized.ProcessingRun.ArtifactIds.Contains(x.ArtifactId))
                    .All(x => x.ProcessingRunId.Equals(
                        finalized.ProcessingRun.ProcessingRunId,
                        StringComparison.OrdinalIgnoreCase)),
                "route ProcessingRun/Artifact backlinks are inconsistent");
            True(artifacts.List(root).Where(x => x.SourceRef == finalized.ImportedResult.ResultId)
                    .All(x => x.SurveyTaskStableIds.Contains("task-stable-001")),
                "RouteResult import lost verified sidecar stable lineage");

            string stableManifestId = finalized.ManifestArtifact.ArtifactId;
            GeneratedArtifactIdentity.Write(
                finalized.ManifestPath, "manifest", "route_manifest",
                ArtifactService.Sha256(csv), [csv],
                artifactId: "artifact_stale_manifest_sidecar",
                context: new GeneratedArtifactContext(project.ProjectId, workarea.WorkareaId));
            RouteWorkflowExportResult thirdExport = workflow.Export(plan,
                new RouteExportRequest(root, output, "Route Area", ExportKml: true, ExportCsv: true,
                    ArtifactContext: new GeneratedArtifactContext(project.ProjectId, workarea.WorkareaId)));
            RouteWorkflowFinalizeResult refinalized = workflow.FinalizeManagedExport(
                plan, thirdExport, root, formalManifestPath,
                new GeneratedArtifactContext(project.ProjectId, workarea.WorkareaId));
            Equal(stableManifestId, refinalized.ManifestArtifact.ArtifactId);
            Equal(stableManifestId,
                GeneratedArtifactIdentity.Inspect(refinalized.ManifestPath).Metadata!.ArtifactId);

            Throws<InvalidOperationException>(() => workflow.Export(plan,
                new RouteExportRequest(root, Path.Combine(Path.GetTempPath(), "route-outside"),
                    "Route Area", true, true,
                    new GeneratedArtifactContext(project.ProjectId, workarea.WorkareaId))));

            // Managed Route is one transaction through Finalize.  A deterministic
            // manifest-ID conflict after Export must restore the old active bundle,
            // management JSON and finish the run as FAILED.
            byte[] artifactsBeforeFailure = File.ReadAllBytes(
                ProjectPaths.Json(root, "artifacts.json"));
            byte[] routeResultsBeforeFailure = File.Exists(
                ProjectPaths.Json(root, "route_results.json"))
                ? File.ReadAllBytes(ProjectPaths.Json(root, "route_results.json"))
                : [];
            string activeBundle = reexported.RollbackSnapshot.BundleDirectory;
            string sentinel = Path.Combine(activeBundle, "rollback-sentinel.txt");
            File.WriteAllText(sentinel, "old-active", new UTF8Encoding(false));

            RouteWorkflowExportResult failingExport = workflow.Export(plan,
                new RouteExportRequest(root, output, "Route Area", ExportKml: true, ExportCsv: true,
                    ArtifactContext: new GeneratedArtifactContext(project.ProjectId, workarea.WorkareaId)));
            True(!File.Exists(sentinel),
                "test setup did not replace active Route bundle");

            List<ArtifactRecord> conflictRows = artifacts.List(root);
            ArtifactRecord manifestRow = conflictRows.Single(x =>
                x.Path.Equals(
                    Path.GetRelativePath(root, formalManifestPath).Replace('\\','/'),
                    StringComparison.OrdinalIgnoreCase));
            conflictRows.Add(new ArtifactRecord
            {
                ArtifactId = manifestRow.ArtifactId,
                Path = "conflict/duplicate-manifest-id.json",
                Role = "registry",
                SourceKind = "test-conflict"
            });
            store.Write(ProjectPaths.Json(root, "artifacts.json"), conflictRows);

            Throws<InvalidOperationException>(() => workflow.FinalizeManagedExport(
                plan, failingExport, root, formalManifestPath,
                new GeneratedArtifactContext(project.ProjectId, workarea.WorkareaId)));

            True(File.Exists(sentinel),
                "failed managed finalize did not restore previous active Route bundle");
            Equal("old-active", File.ReadAllText(sentinel));
            True(File.ReadAllBytes(ProjectPaths.Json(root, "artifacts.json"))
                    .SequenceEqual(artifactsBeforeFailure),
                "failed managed finalize changed Artifact registry after rollback");
            True(File.ReadAllBytes(ProjectPaths.Json(root, "route_results.json"))
                    .SequenceEqual(routeResultsBeforeFailure),
                "failed managed finalize changed RouteResults after rollback");
            ProcessingRunRecord failedFinalizeRun = processingRuns.List(root).Single(x =>
                x.ProcessingRunId == failingExport.ProcessingRun.ProcessingRunId);
            Equal("FAILED", failedFinalizeRun.Status);

        });
    }

    private static void TestOverlongExecutionSegments()
    {
        static RouteEndpoint Point(string line, int endpoint, double x, double y) => new()
        {
            Line = line, Endpoint = endpoint, X = x, Y = y,
            Label = $"{line}-{endpoint}", Pid = $"{line}-{endpoint}"
        };

        var l1 = new[] { Point("L1", 0, 0, 0), Point("L1", 1, 0, 10) };
        var l2 = new[] { Point("L2", 0, 10, 0), Point("L2", 1, 10, 100) };
        var l3 = new[] { Point("L3", 0, 20, 0), Point("L3", 1, 20, 10) };
        var grouped = new Dictionary<string, RouteEndpoint[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["L1"] = l1, ["L2"] = l2, ["L3"] = l3
        };
        var inference = new RouteLineInference
        {
            Grouped = grouped,
            OrderedLines = ["L1", "L2", "L3"],
            Endpoints = grouped.Values.SelectMany(x => x).ToList(),
            MatchMode = "L"
        };
        var plan = new RoutePlanResult
        {
            Mode = "projected",
            CentralMeridian = 102,
            Inference = inference,
            OrderedLines = ["L1", "L2", "L3"],
            StartPoint = l1[0],
            ActiveStartEndpoint = 0,
            Options = new RoutePlanOptions { MaxSortieKm = 0.06 },
            Sorties = RoutePlanner.SplitSorties(grouped, ["L1", "L2", "L3"], 0, 1_000_000, "projected", 102)
        };

        RouteExecutionSegmentPlan result = new RouteExecutionSegmentPlanner()
            .ApplySingleCut(plan, "L2", 10, 50);
        Equal(2, result.Segments.Count);
        Equal("L2", result.Segments[0].MotherLine);
        Equal("L2.S1", result.Segments[0].SegmentId);
        Equal("L2.S2", result.Segments[1].SegmentId);
        True(result.Plan.Sorties.Any(x => x.SourceLines.SequenceEqual(["L2.S1"])),
            "first protected execution segment was not isolated");
        True(result.Plan.Sorties.Any(x => x.SourceLines.SequenceEqual(["L2.S2"])),
            "second protected execution segment was not isolated");
        True(result.Plan.Sorties.All(x =>
            !x.SourceLines.Contains("L2.S1") || x.SourceLines.Count == 1),
            "protected execution segment mixed with ordinary line");
        True(result.Plan.Sorties.All(x =>
            !x.SourceLines.Contains("L2.S2") || x.SourceLines.Count == 1),
            "protected execution segment mixed with ordinary line");
        Equal("L2.S1", result.Plan.Sorties.Single(x => x.SourceLines.SequenceEqual(["L2.S1"]))
            .TaskReferences.Single().ExecutionSegmentId);
        True(result.Segments.All(x => !string.IsNullOrWhiteSpace(x.SegmentStableId)),
            "execution segments require opaque stable IDs");
        Equal(2, result.Segments.Select(x => x.SegmentStableId).Distinct().Count());
        string unaffectedStableId = result.Plan.ExecutionSegments
            .Single(x => x.SegmentId == "L2.S2").SegmentStableId;

        // Re-cut one existing segment. Display sequence changes, but the unaffected
        // machine identity must survive and become S3 rather than being recreated.
        RoutePlanResult twoSegmentPlan = result.Plan;
        RouteExecutionSegmentPlan recut = new RouteExecutionSegmentPlanner()
            .ApplySingleCut(result.Plan, "L2.S1", 10, 25);
        Equal(3, recut.Plan.ExecutionSegments.Count(x => x.MotherLine == "L2"));
        True(recut.Plan.ExecutionSegments.Select(x => x.SegmentId).OrderBy(x => x)
                .SequenceEqual(new[] { "L2.S1", "L2.S2", "L2.S3" }),
            "repeat cut did not produce three display segments");
        Equal(unaffectedStableId,
            recut.Plan.ExecutionSegments.Single(x => x.SegmentId == "L2.S3").SegmentStableId);
        Equal(unaffectedStableId,
            twoSegmentPlan.ExecutionSegments.Single(x => x.SegmentId == "L2.S2").SegmentStableId);
        Equal(3, recut.Plan.ExecutionSegments.Select(x => x.SegmentStableId).Distinct().Count());
        True(recut.Plan.Sorties.Where(x => x.SourceLines.Any(y => y.StartsWith("L2.S")))
                .All(x => x.SourceLines.Count == 1 &&
                          !string.IsNullOrWhiteSpace(x.TaskReferences.Single().ExecutionSegmentStableId)),
            "repeat-cut protected sorties lost stable segment identity");
        result = recut;
        int[] splitSortieIndexes = result.Plan.Sorties
            .Select((sortie, index) => (sortie, index))
            .Where(x => x.sortie.SourceLines.Any(line => line.StartsWith("L2.S", StringComparison.Ordinal)))
            .Select(x => x.index).ToArray();
        Equal(3, splitSortieIndexes.Length);
        True(result.Plan.Sorties[splitSortieIndexes[0]].SourceLines.SequenceEqual(["L2.S3"]) &&
             result.Plan.Sorties[splitSortieIndexes[1]].SourceLines.SequenceEqual(["L2.S2"]) &&
             result.Plan.Sorties[splitSortieIndexes[2]].SourceLines.SequenceEqual(["L2.S1"]),
            "reverse snake traversal did not order all split segments from the active endpoint");
        RouteEndpoint[] reverseFirst = result.Plan.Sorties[splitSortieIndexes[0]].Points.ToArray();
        RouteEndpoint[] reverseSecond = result.Plan.Sorties[splitSortieIndexes[1]].Points.ToArray();
        RouteEndpoint[] reverseThird = result.Plan.Sorties[splitSortieIndexes[2]].Points.ToArray();
        True(reverseFirst[0].X == l2[1].X && reverseFirst[0].Y == l2[1].Y &&
             reverseFirst[1].X == 10 && reverseFirst[1].Y == 50 &&
             reverseSecond[0].X == 10 && reverseSecond[0].Y == 50 &&
             reverseSecond[1].X == 10 && reverseSecond[1].Y == 25 &&
             reverseThird[0].X == 10 && reverseThird[0].Y == 25 &&
             reverseThird[1].X == l2[0].X && reverseThird[1].Y == l2[0].Y,
            "reverse snake segment routes do not preserve endpoint-to-cut geometry");

        WithTempRoot(root =>
        {
            string routeCsv = Path.Combine(root, "mother-lines.csv");
            File.WriteAllText(routeCsv, "L1,0,0\nL1,10,0\n", Encoding.UTF8);
            RoutePlanResult initialProductionPlan = ClonePlanWithSource(twoSegmentPlan, routeCsv);
            RoutePlanResult productionPlan = ClonePlanWithSource(result.Plan, routeCsv);
            var routeProjects = new RouteProjectService();
            string baseline = Path.Combine(root, "demo_外业测线任务基准.json");

            // Persist two segments first, then save the three-segment revision to the same
            // project. The untouched old S2 becomes display S3 but keeps both machine IDs.
            routeProjects.Save(baseline, initialProductionPlan, root, "Point,Y,X", "投影坐标", "西南", "自动");
            RouteProjectFile initialSaved = routeProjects.Load(baseline);
            RouteTaskReference initialUnaffected = initialSaved.Sorties.SelectMany(x => x.TaskReferences)
                .Single(x => x.ExecutionSegmentId == "L2.S2");
            string initialUnaffectedTaskId = initialUnaffected.SurveyTaskStableId;
            string initialUnaffectedSegmentId = initialUnaffected.ExecutionSegmentStableId;

            routeProjects.Save(baseline, productionPlan, root, "Point,Y,X", "投影坐标", "西南", "自动");
            RouteProjectFile loaded = routeProjects.Load(baseline);
            RouteTaskReference movedUnaffected = loaded.Sorties.SelectMany(x => x.TaskReferences)
                .Single(x => x.ExecutionSegmentId == "L2.S3");
            Equal(initialUnaffectedTaskId, movedUnaffected.SurveyTaskStableId);
            Equal(initialUnaffectedSegmentId, movedUnaffected.ExecutionSegmentStableId);
            Equal(3, movedUnaffected.SegmentIndex);
            True(!loaded.Sorties.SelectMany(x => x.TaskReferences).Any(x =>
                    x.ExecutionSegmentStableId.Equals(initialUnaffectedSegmentId, StringComparison.OrdinalIgnoreCase) &&
                    x.ExecutionSegmentId == "L2.S2"),
                "historical segment display ID leaked back into the re-numbered stable segment");
            Equal(3, loaded.ExecutionSegments.Count);
            True(loaded.ExecutionSegments.All(x => !string.IsNullOrWhiteSpace(x.CutOperationId)),
                "execution segment cut-operation lineage was not persisted");
            True(loaded.ExecutionSegments.All(x => !string.IsNullOrWhiteSpace(x.SegmentStableId)),
                "v2 route project did not persist execution stable IDs");

            // v1 compatibility: persisted display IDs are migration evidence only.
            string legacyPath = Path.Combine(root, "legacy-v1-route.json");
            string legacyJson = File.ReadAllText(baseline)
                .Replace("magops-route-project-2", "magops-route-project-1", StringComparison.Ordinal);
            using (JsonDocument legacyDoc = JsonDocument.Parse(legacyJson))
            {
                JsonNode legacyNode = JsonNode.Parse(legacyJson)!;
                foreach (JsonNode? segment in legacyNode["execution_segments"]!.AsArray())
                    if (segment is JsonObject segmentObject) segmentObject.Remove("SegmentStableId");
                foreach (JsonNode? sortie in legacyNode["sorties"]!.AsArray())
                    foreach (JsonNode? reference in sortie!["TaskReferences"]!.AsArray())
                        if (reference is JsonObject referenceObject) referenceObject.Remove("ExecutionSegmentStableId");
                File.WriteAllText(legacyPath, legacyNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            RouteProjectFile migrated = routeProjects.Load(legacyPath);
            Equal("magops-route-project-2", migrated.SchemaVersion);
            Equal(3, migrated.ExecutionSegments.Select(x => x.SegmentStableId).Distinct().Count());
            True(migrated.Sorties.SelectMany(x => x.TaskReferences)
                    .Where(x => x.ProtectedExecutionSegment)
                    .All(x => !string.IsNullOrWhiteSpace(x.ExecutionSegmentStableId)),
                "v1 route project migration did not restore task-to-segment stable identity");

            PlannedSurveyLineTask[] productionTasks = new FieldOperationsAnalysisService()
                .TryLoadManagedRouteSnapshot(root, Path.Combine(root, "missing-export"))!
                .SurveyLines.Where(x => x.LineName == "L2").ToArray();
            Equal(3, productionTasks.Length);
            Equal(3, productionTasks.Select(x => x.SurveyTaskStableId).Distinct().Count());
            Equal(1, productionTasks.Select(x => x.MotherLineStableId).Distinct().Count());
            True(productionTasks.Select(x => x.ExecutionSegmentId).OrderBy(x => x)
                    .SequenceEqual(new[] { "L2.S1", "L2.S2", "L2.S3" }),
                "production baseline did not expose both execution segment identities");
            RouteExportBundle output = new RouteExportService().ExportAllSeparated(
                productionPlan, Path.Combine(root, "deliverable"), "demo", true, true, false,
                kmlIncludeElevation: false);
            Equal(productionPlan.Sorties.Count * 2, output.OutputPaths.Count);
            True(output.OutputPaths.All(File.Exists), "split route KML/CSV outputs were not produced");
            string manifest = new RouteManifestWriter().Write(
                Path.Combine(root, "deliverable", "demo_route-v25.json"), productionPlan,
                output.OutputPaths);
            using JsonDocument manifestJson = JsonDocument.Parse(File.ReadAllText(manifest));
            JsonElement[] segmentRefs = manifestJson.RootElement.GetProperty("survey_task_references")
                .EnumerateArray().Where(x => (x.GetProperty("execution_segment_id").GetString() ?? "").StartsWith("L2.S", StringComparison.Ordinal)).ToArray();
            Equal(3, segmentRefs.Length);
            True(segmentRefs.All(x =>
                    x.GetProperty("line_name").GetString() == "L2" &&
                    !string.IsNullOrWhiteSpace(x.GetProperty("survey_task_stable_id").GetString()) &&
                    !string.IsNullOrWhiteSpace(x.GetProperty("mother_line_stable_id").GetString()) &&
                    !string.IsNullOrWhiteSpace(x.GetProperty("execution_segment_stable_id").GetString())),
                "manifest did not preserve mother-line/task/segment identity for split outputs");
        });
    }

    private static RoutePlanResult ClonePlanWithSource(RoutePlanResult source, string path) => new()
    {
        SourcePath = path,
        Mode = source.Mode,
        CentralMeridian = source.CentralMeridian,
        Inference = source.Inference,
        OrderedLines = source.OrderedLines,
        StartPoint = source.StartPoint,
        ActiveStartEndpoint = source.ActiveStartEndpoint,
        Sorties = source.Sorties,
        ExecutionSegments = source.ExecutionSegments,
        Options = source.Options,
        Warnings = source.Warnings
    };

    private static void TestWorkspaceTransaction()
    {
        WithTempRoot(root =>
        {
            string path = Path.Combine(root, "count.json");
            Parallel.For(0, 8, _ =>
            {
                for (int i = 0; i < 4; i++)
                    WorkspaceTransaction.Update(root, path, 0, value => value + 1);
            });
            Equal(32, new JsonStore().Read(path, 0));
        });
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"expected {expected}, actual {actual}");
    }

    private static void Equal(double expected, double actual, double tolerance)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"expected {expected:R}, actual {actual:R}, tolerance {tolerance:R}");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"expected exception {typeof(T).Name}");
    }
}
