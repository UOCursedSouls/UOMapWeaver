using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UOMapWeaver.Core;
using UOMapWeaver.Core.Bmp;
using UOMapWeaver.Core.Map;
using UOMapWeaver.Core.MapTrans;
using UOMapWeaver.Core.Statics;
using Xunit.Abstractions;

namespace UOMapWeaver.Tests;

public sealed class MulBmpRoundtripTests
{
    private readonly ITestOutputHelper _output;

    public MulBmpRoundtripTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void MapMulToBmpAndBack_MatchesOriginalTiles()
    {
        var dataRoot = FindTestDataRoot();
        var mapPath = FindMapMulPath();
        if (mapPath is null)
        {
            _output.WriteLine("Skipping: map data not found. Set UOMAPWEAVER_TEST_MAP to a map.mul path.");
            return;
        }
        Assert.True(MapConversion.TryResolveMapSizeFromFile(mapPath, out var width, out var height),
            "Map size not detected for map0.mul.");

        var originalTiles = MapMulCodec.ReadLandTiles(mapPath, width, height);
        var result = MapConversion.ConvertMulToTileIndexBmp(mapPath, width, height);

        var tempDir = Path.Combine(Path.GetTempPath(), "UOMapWeaver.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var terrainPath = Path.Combine(tempDir, "Terrain_TileIndexRgb.bmp");
            var altitudePath = Path.Combine(tempDir, "Altitude.bmp");

            Bmp24Codec.Write(terrainPath, result.terrain);
            Bmp8Codec.Write(altitudePath, result.altitude);

            var terrain = Bmp24Codec.Read(terrainPath);
            var altitude = Bmp8Codec.Read(altitudePath);
            var roundtrip = MapConversion.ConvertTileIndexBmpToMul(terrain, altitude);

            Assert.Equal(originalTiles.Length, roundtrip.tiles.Length);

            for (var i = 0; i < originalTiles.Length; i++)
            {
                if (originalTiles[i].TileId != roundtrip.tiles[i].TileId ||
                    originalTiles[i].Z != roundtrip.tiles[i].Z)
                {
                    Assert.Fail($"Tile mismatch at {i}: " +
                                $"orig=0x{originalTiles[i].TileId:X4}/{originalTiles[i].Z} " +
                                $"new=0x{roundtrip.tiles[i].TileId:X4}/{roundtrip.tiles[i].Z}");
                }
            }

            var roundtripPath = Path.Combine(tempDir, "map0.mul");
            MapMulCodec.WriteLandTiles(roundtripPath, width, height, roundtrip.tiles);
            Assert.Equal(new FileInfo(mapPath).Length, new FileInfo(roundtripPath).Length);

            var roundtripTiles = MapMulCodec.ReadLandTiles(roundtripPath, width, height);
            Assert.Equal(originalTiles.Length, roundtripTiles.Length);
            for (var i = 0; i < originalTiles.Length; i++)
            {
                if (originalTiles[i].TileId != roundtripTiles[i].TileId ||
                    originalTiles[i].Z != roundtripTiles[i].Z)
                {
                    Assert.Fail($"File round-trip mismatch at {i}: " +
                                $"orig=0x{originalTiles[i].TileId:X4}/{originalTiles[i].Z} " +
                                $"new=0x{roundtripTiles[i].TileId:X4}/{roundtripTiles[i].Z}");
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void MapTransConversion_ReportsMissingColors()
    {
        var dataRoot = FindTestDataRoot();
        var mapPath = FindMapMulPath();
        if (mapPath is null)
        {
            _output.WriteLine("Skipping: map data not found. Set UOMAPWEAVER_TEST_MAP to a map.mul path.");
            return;
        }
        Assert.True(MapConversion.TryResolveMapSizeFromFile(mapPath, out var width, out var height),
            "Map size not detected for map0.mul.");

        var mapTransPath = FindMapTransProfile(dataRoot);
        if (mapTransPath is null)
        {
            _output.WriteLine("Skipping: MapTrans profile not found. Set UOMAPWEAVER_TEST_MAPTRANS.");
            return;
        }

        var profile = MapTransParser.LoadFromFile(mapTransPath!);
        var result = MapConversion.ConvertMulToBmp(mapPath, width, height, profile);

        Assert.True(result.report.MissingTerrainColors > 0, "Expected missing colors from MapTrans profile.");

        var missingPercent = (double)result.report.MissingTerrainColors / result.report.TotalTiles;
        _output.WriteLine($"Missing colors: {result.report.MissingTerrainColors:N0} / {result.report.TotalTiles:N0} ({missingPercent:P2})");
        Assert.InRange(missingPercent, 0.05, 0.20);

        var expectedTop = new[] { 0x0064, 0x01AE, 0x3FF8, 0x0227, 0x00CB };
        var actualTop = result.report.MissingColorsByTileId
            .OrderByDescending(pair => pair.Value)
            .Take(5)
            .Select(pair => (int)pair.Key)
            .ToArray();

        Assert.Equal(expectedTop, actualTop);
    }

    [Fact]
    public void TerrainXmlBmpToMul_MatchesElPochoMap()
    {
        var elPochoDir = FindElPochoDir();
        if (elPochoDir is null)
        {
            _output.WriteLine("Skipping: UO_ElPocho folder not found. Set UOMAPWEAVER_TEST_ELPOCHO_DIR.");
            return;
        }

        var dataRoot = FindTestDataRoot();
        if (dataRoot is null)
        {
            _output.WriteLine("Skipping: UOMapWeaverData not found. Set UOMAPWEAVER_TEST_DATA.");
            return;
        }

        if (!EnsureDataRoot(dataRoot, out var dataRootTarget))
        {
            _output.WriteLine("Skipping: unable to prepare UOMapWeaverData for tests.");
            return;
        }

        var terrainBmp = FindTerrainBmp(elPochoDir);
        var altitudeBmp = Path.Combine(elPochoDir, "Altitude.bmp");
        var mapMul = FindElPochoMapMul(elPochoDir);

        if (terrainBmp is null || !File.Exists(altitudeBmp) || mapMul is null)
        {
            _output.WriteLine("Skipping: missing Terrain/Altitude/map0.mul in UO_ElPocho.");
            return;
        }

        if (!BmpCodec.TryReadInfo(terrainBmp, out var width, out var height, out _))
        {
            Assert.Fail($"Terrain.bmp not readable: {terrainBmp}");
        }

        var outputDir = Path.Combine(Path.GetTempPath(), "UOMapWeaver.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDir);

        try
        {
            var outputMap = Path.Combine(outputDir, "map.mul");
            var terrainXml = Path.Combine(dataRootTarget, "Terrain.xml");
            var transitionsRoot = Path.Combine(dataRootTarget, "Transitions");

            MapConversion.ConvertTerrainXmlBmpToMulFromFiles(
                terrainBmp,
                altitudeBmp,
                outputMap,
                terrainXml,
                transitionsRoot);

            var expectedTiles = MapMulCodec.ReadLandTiles(mapMul, width, height);
            var actualTiles = MapMulCodec.ReadLandTiles(outputMap, width, height);
            Assert.Equal(expectedTiles.Length, actualTiles.Length);

            for (var i = 0; i < expectedTiles.Length; i++)
            {
                if (expectedTiles[i].TileId != actualTiles[i].TileId || expectedTiles[i].Z != actualTiles[i].Z)
                {
                    Assert.Fail($"Tile mismatch at {i}: expected 0x{expectedTiles[i].TileId:X4}/{expectedTiles[i].Z} " +
                                $"actual 0x{actualTiles[i].TileId:X4}/{actualTiles[i].Z}");
                }
            }
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
        }
    }

    [Fact]
    public void TerrainXmlBmpToMul_WithGrayscaleAltitude_ProducesDifferentTiles()
    {
        var elPochoDir = FindElPochoDir();
        if (elPochoDir is null)
        {
            _output.WriteLine("Skipping: UO_ElPocho folder not found. Set UOMAPWEAVER_TEST_ELPOCHO_DIR.");
            return;
        }

        var dataRoot = FindTestDataRoot();
        if (dataRoot is null)
        {
            _output.WriteLine("Skipping: UOMapWeaverData not found. Set UOMAPWEAVER_TEST_DATA.");
            return;
        }

        if (!EnsureDataRoot(dataRoot, out var dataRootTarget))
        {
            _output.WriteLine("Skipping: unable to prepare UOMapWeaverData for tests.");
            return;
        }

        var terrainBmp = FindTerrainBmp(elPochoDir);
        var altitudeBmp = FindGrayscaleAltitude();
        var mapMul = FindElPochoMapMul(elPochoDir);

        if (terrainBmp is null || altitudeBmp is null || mapMul is null)
        {
            _output.WriteLine("Skipping: missing Terrain.bmp, grayscale Altitude, or map0.mul.");
            return;
        }

        if (!BmpCodec.TryReadInfo(terrainBmp, out var width, out var height, out _))
        {
            Assert.Fail($"Terrain.bmp not readable: {terrainBmp}");
        }

        var outputDir = Path.Combine(Path.GetTempPath(), "UOMapWeaver.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDir);

        try
        {
            var outputMap = Path.Combine(outputDir, "map.mul");
            var terrainXml = Path.Combine(dataRootTarget, "Terrain.xml");
            var transitionsRoot = Path.Combine(dataRootTarget, "Transitions");

            MapConversion.ConvertTerrainXmlBmpToMulFromFiles(
                terrainBmp,
                altitudeBmp,
                outputMap,
                terrainXml,
                transitionsRoot);

            var expectedTiles = MapMulCodec.ReadLandTiles(mapMul, width, height);
            var actualTiles = MapMulCodec.ReadLandTiles(outputMap, width, height);
            Assert.Equal(expectedTiles.Length, actualTiles.Length);

            var mismatch = 0;
            for (var i = 0; i < expectedTiles.Length; i++)
            {
                if (expectedTiles[i].TileId != actualTiles[i].TileId || expectedTiles[i].Z != actualTiles[i].Z)
                {
                    mismatch++;
                    if (mismatch >= 1)
                    {
                        break;
                    }
                }
            }

            Assert.True(mismatch > 0, "Expected grayscale altitude to differ from ElPocho reference.");
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
        }
    }

    [Fact]
    public void ElPochoStatics_AreWithinTerrainDefinitions()
    {
        var elPochoDir = FindElPochoDir();
        if (elPochoDir is null)
        {
            _output.WriteLine("Skipping: UO_ElPocho folder not found. Set UOMAPWEAVER_TEST_ELPOCHO_DIR.");
            return;
        }

        var dataRoot = FindTestDataRoot();
        if (dataRoot is null)
        {
            _output.WriteLine("Skipping: UOMapWeaverData not found. Set UOMAPWEAVER_TEST_DATA.");
            return;
        }

        if (!EnsureDataRoot(dataRoot, out var dataRootTarget))
        {
            _output.WriteLine("Skipping: unable to prepare UOMapWeaverData for tests.");
            return;
        }

        var mapMul = FindElPochoMapMul(elPochoDir);
        var staIdx = FindElPochoStaIdx(elPochoDir);
        var staticsMul = FindElPochoStaticsMul(elPochoDir);
        if (mapMul is null || staIdx is null || staticsMul is null)
        {
            _output.WriteLine("Skipping: uo_elpocho map/statics files not found.");
            return;
        }

        if (!MapConversion.TryResolveMapSizeFromFile(mapMul, out var width, out var height))
        {
            Assert.Fail("Map size not detected for uo_elpocho_map0.mul.");
        }

        var mapTiles = MapMulCodec.ReadLandTiles(mapMul, width, height);
        var rules = BuildStaticRules(dataRootTarget);
        var transitionStatics = LoadTransitionStaticTileIds(dataRootTarget);
        if (rules.Count == 0)
        {
            _output.WriteLine("Skipping: static placement rules not found.");
            return;
        }

        var blocks = StaticMulCodec.ReadStatics(staIdx, staticsMul, width, height);
        var (invalid, total, samples) = ValidateStatics(blocks, width, height, mapTiles, rules, transitionStatics, StaticsLayout.ColumnMajor);
        _output.WriteLine($"ElPocho statics checked: {total:N0}, invalid: {invalid:N0}.");
        if (samples.Count > 0)
        {
            _output.WriteLine("Invalid samples: " + string.Join(" | ", samples));
        }

        var invalidRatio = total == 0 ? 0 : (double)invalid / total;
        Assert.InRange(invalidRatio, 0, 0.01);
    }

    [Fact]
    public void GeneratedStatics_AreWithinTerrainDefinitions()
    {
        var elPochoDir = FindElPochoDir();
        if (elPochoDir is null)
        {
            _output.WriteLine("Skipping: UO_ElPocho folder not found. Set UOMAPWEAVER_TEST_ELPOCHO_DIR.");
            return;
        }

        var dataRoot = FindTestDataRoot();
        if (dataRoot is null)
        {
            _output.WriteLine("Skipping: UOMapWeaverData not found. Set UOMAPWEAVER_TEST_DATA.");
            return;
        }

        if (!EnsureDataRoot(dataRoot, out var dataRootTarget))
        {
            _output.WriteLine("Skipping: unable to prepare UOMapWeaverData for tests.");
            return;
        }

        var terrainBmp = FindTerrainBmp(elPochoDir);
        var altitudeBmp = Path.Combine(elPochoDir, "Altitude.bmp");
        if (terrainBmp is null || !File.Exists(altitudeBmp))
        {
            _output.WriteLine("Skipping: Terrain/Altitude BMP not found in UO_ElPocho.");
            return;
        }

        if (!BmpCodec.TryReadInfo(terrainBmp, out var width, out var height, out _))
        {
            Assert.Fail($"Terrain.bmp not readable: {terrainBmp}");
        }

        var outputDir = Path.Combine(Path.GetTempPath(), "UOMapWeaver.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outputDir);

        try
        {
            var outputMap = Path.Combine(outputDir, "map.mul");
            var terrainXml = Path.Combine(dataRootTarget, "Terrain.xml");
            var transitionsRoot = Path.Combine(dataRootTarget, "Transitions");

            MapConversion.ConvertTerrainXmlBmpToMulFromFiles(
                terrainBmp,
                altitudeBmp,
                outputMap,
                terrainXml,
                transitionsRoot);

            var mapTiles = MapMulCodec.ReadLandTiles(outputMap, width, height);
            var rules = BuildStaticRules(dataRootTarget);
            var transitionStatics = LoadTransitionStaticTileIds(dataRootTarget);
            if (rules.Count == 0)
            {
                _output.WriteLine("Skipping: static placement rules not found.");
                return;
            }

            var placements = StaticPlacementGenerator.Generate(
                mapTiles,
                width,
                height,
                new StaticPlacementOptions { Layout = StaticsLayout.ColumnMajor });

            var (invalid, total, samples) = ValidateStatics(placements, width, height, mapTiles, rules, transitionStatics, StaticsLayout.ColumnMajor);
            _output.WriteLine($"Generated statics checked: {total:N0}, invalid: {invalid:N0}.");
            if (samples.Count > 0)
            {
                _output.WriteLine("Invalid samples: " + string.Join(" | ", samples));
            }

            Assert.Equal(0, invalid);
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
        }
    }

    [Fact]
    public void GeneratedStatics_MatchElPochoReference()
    {
        var elPochoDir = FindElPochoDir();
        if (elPochoDir is null)
        {
            _output.WriteLine("Skipping: UO_ElPocho folder not found. Set UOMAPWEAVER_TEST_ELPOCHO_DIR.");
            return;
        }

        var dataRoot = FindTestDataRoot();
        if (dataRoot is null)
        {
            _output.WriteLine("Skipping: UOMapWeaverData not found. Set UOMAPWEAVER_TEST_DATA.");
            return;
        }

        if (!EnsureDataRoot(dataRoot, out var dataRootTarget))
        {
            _output.WriteLine("Skipping: unable to prepare UOMapWeaverData for tests.");
            return;
        }

        var mapMul = FindElPochoMapMul(elPochoDir);
        var staIdx = FindElPochoStaIdx(elPochoDir);
        var staticsMul = FindElPochoStaticsMul(elPochoDir);
        var terrainBmp = FindTerrainBmp(elPochoDir);
        var altitudeBmp = Path.Combine(elPochoDir, "Altitude.bmp");
        if (mapMul is null || staIdx is null || staticsMul is null)
        {
            _output.WriteLine("Skipping: uo_elpocho map/statics files not found.");
            return;
        }

        if (!MapConversion.TryResolveMapSizeFromFile(mapMul, out var width, out var height))
        {
            Assert.Fail("Map size not detected for uo_elpocho_map0.mul.");
        }

        var mapTiles = MapMulCodec.ReadLandTiles(mapMul, width, height);
        var generated = StaticPlacementGenerator.Generate(
            mapTiles,
            width,
            height,
            new StaticPlacementOptions { Layout = StaticsLayout.ColumnMajor });

        if (terrainBmp is not null && File.Exists(altitudeBmp))
        {
            var terrainXml = Path.Combine(dataRootTarget, "Terrain.xml");
            var transitionsRoot = Path.Combine(dataRootTarget, "Transitions");
            var transitionBlocks = MapConversion.GenerateTransitionStaticsFromTerrainXml(
                terrainBmp,
                altitudeBmp,
                terrainXml,
                transitionsRoot,
                StaticsLayout.ColumnMajor,
                out _,
                null,
                null,
                null);
            MergeBlocks(generated, transitionBlocks);
        }

        var referenceBlocks = StaticMulCodec.ReadStatics(staIdx, staticsMul, width, height);
        var referenceColumn = BuildStaticSet(referenceBlocks, width, height, StaticsLayout.ColumnMajor);
        var referenceRow = BuildStaticSet(referenceBlocks, width, height, StaticsLayout.RowMajor);
        var generatedSet = BuildStaticSet(generated, width, height, StaticsLayout.ColumnMajor);

        var columnMatch = CompareStaticSets(referenceColumn, generatedSet, "ColumnMajor");
        var rowMatch = CompareStaticSets(referenceRow, generatedSet, "RowMajor");

        _output.WriteLine($"Reference layout match: ColumnMajor {columnMatch.matchRatio:P2}, RowMajor {rowMatch.matchRatio:P2}.");
        _output.WriteLine($"ColumnMajor missing: {columnMatch.missing:N0}, extra: {columnMatch.extra:N0}, total ref: {columnMatch.reference:N0}.");
        if (columnMatch.missingSamples.Count > 0)
        {
            _output.WriteLine("ColumnMajor missing samples: " + string.Join(" | ", columnMatch.missingSamples));
        }
        if (columnMatch.extraSamples.Count > 0)
        {
            _output.WriteLine("ColumnMajor extra samples: " + string.Join(" | ", columnMatch.extraSamples));
        }

        var bestMatch = Math.Max(columnMatch.matchRatio, rowMatch.matchRatio);
        Assert.True(bestMatch > 0, "No overlap between generated statics and ElPocho reference.");
    }

    private static string? FindTestDataRoot()
    {
        var overrideRoot = Environment.GetEnvironmentVariable("UOMAPWEAVER_TEST_DATA");
        if (!string.IsNullOrWhiteSpace(overrideRoot) && Directory.Exists(overrideRoot))
        {
            return overrideRoot;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "TMP_UtitlityFiles");
            if (Directory.Exists(candidate))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static string? FindMapMulPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("UOMAPWEAVER_TEST_MAP");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }
        return null;
    }

    private static string? FindMapTransProfile(string? dataRoot)
    {
        var dataRootPath = NormalizeDataRoot(dataRoot);
        var candidateRoots = new[]
        {
            Environment.GetEnvironmentVariable("UOMAPWEAVER_TEST_MAPTRANS") ?? string.Empty,
            string.IsNullOrWhiteSpace(dataRootPath) ? string.Empty : Path.Combine(dataRootPath, "System", "MapTrans"),
            string.IsNullOrWhiteSpace(dataRootPath) ? string.Empty : Path.Combine(dataRootPath, "MapTrans")
        };

        foreach (var root in candidateRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            var file = Directory.EnumerateFiles(root, "Mod*.txt", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(file))
            {
                return file;
            }
        }

        return null;
    }

    private static string? FindElPochoDir()
    {
        var overridePath = Environment.GetEnvironmentVariable("UOMAPWEAVER_TEST_ELPOCHO_DIR");
        if (!string.IsNullOrWhiteSpace(overridePath) && Directory.Exists(overridePath))
        {
            return overridePath;
        }

        var defaultPath = @"C:\Users\Aruto\Desktop\CentrED-Windows-X64\CustomMap\UO_ElPocho";
        return Directory.Exists(defaultPath) ? defaultPath : null;
    }

    private static string? FindTerrainBmp(string elPochoDir)
    {
        var candidate = Path.Combine(elPochoDir, "Terrain_24bit.bmp");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        candidate = Path.Combine(elPochoDir, "Terrain.bmp");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? FindElPochoMapMul(string elPochoDir)
    {
        var mapPath = Directory.EnumerateFiles(elPochoDir, "*map0*.mul", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(mapPath) ? null : mapPath;
    }

    private static string? FindElPochoStaticsMul(string elPochoDir)
    {
        var path = Directory.EnumerateFiles(elPochoDir, "*statics0*.mul", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static string? FindElPochoStaIdx(string elPochoDir)
    {
        var path = Directory.EnumerateFiles(elPochoDir, "*staidx0*.mul", SearchOption.TopDirectoryOnly)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static string? FindGrayscaleAltitude()
    {
        var overridePath = Environment.GetEnvironmentVariable("UOMAPWEAVER_TEST_ALTITUDE_GRAY");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        var defaultPath = @"C:\Users\Aruto\Desktop\UOLegacyMaps\map6_MapTrans_Altitude.bmp";
        return File.Exists(defaultPath) ? defaultPath : null;
    }

    private static bool EnsureDataRoot(string dataRoot, out string targetRoot)
    {
        targetRoot = Path.Combine(AppContext.BaseDirectory, "UOMapWeaverData");
        try
        {
            Directory.CreateDirectory(targetRoot);

            CopyIfMissing(Path.Combine(dataRoot, "Terrain.xml"), Path.Combine(targetRoot, "Terrain.xml"));
            CopyIfMissing(Path.Combine(dataRoot, "Altitude.xml"), Path.Combine(targetRoot, "Altitude.xml"));
            CopyDirIfMissing(Path.Combine(dataRoot, "Transitions"), Path.Combine(targetRoot, "Transitions"));
            CopyDirIfMissing(Path.Combine(dataRoot, "TerrainTypes"), Path.Combine(targetRoot, "TerrainTypes"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyIfMissing(string source, string dest)
    {
        if (!File.Exists(source) || File.Exists(dest))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? string.Empty);
        File.Copy(source, dest, overwrite: true);
    }

    private static void CopyDirIfMissing(string source, string dest)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        if (Directory.Exists(dest) && Directory.EnumerateFiles(dest, "*.xml", SearchOption.AllDirectories).Any())
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, dir);
            Directory.CreateDirectory(Path.Combine(dest, rel));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? dest);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static Dictionary<ushort, List<StaticRule>> BuildStaticRules(string dataRoot)
    {
        var terrainXml = Path.Combine(dataRoot, "Terrain.xml");
        var terrainDefs = StaticPlacementCatalog.LoadTerrainDefinitions(terrainXml);
        var placements = StaticPlacementCatalog.LoadStaticDefinitions(new[]
        {
            Path.Combine(dataRoot, "TerrainTypes"),
            Path.Combine(dataRoot, "Statics")
        }, out _);

        var terrainByName = terrainDefs.ToDictionary(def => def.Name, def => def.TileId, StringComparer.OrdinalIgnoreCase);
        var rules = new Dictionary<ushort, List<StaticRule>>();

        foreach (var pair in placements)
        {
            if (!terrainByName.TryGetValue(pair.Key, out var terrainTileId))
            {
                continue;
            }

            foreach (var group in pair.Value.Groups)
            {
                foreach (var item in group.Items)
                {
                    if (!rules.TryGetValue(item.TileId, out var list))
                    {
                        list = new List<StaticRule>();
                        rules[item.TileId] = list;
                    }

                    list.Add(new StaticRule(item.X, item.Y, terrainTileId));
                }
            }
        }

        return rules;
    }

    private static HashSet<ushort> LoadTransitionStaticTileIds(string dataRoot)
    {
        var transitionsRoot = Path.Combine(dataRoot, "Transitions");
        var results = new HashSet<ushort>();
        if (!Directory.Exists(transitionsRoot))
        {
            return results;
        }

        foreach (var path in Directory.EnumerateFiles(transitionsRoot, "*.xml", SearchOption.AllDirectories))
        {
            try
            {
                var doc = System.Xml.Linq.XDocument.Load(path);
                foreach (var node in doc.Descendants("StaticTile"))
                {
                    if (ushort.TryParse(node.Attribute("TileID")?.Value, out var tileId))
                    {
                        results.Add(tileId);
                    }
                }
            }
            catch
            {
                continue;
            }
        }

        return results;
    }

    private static (int invalid, int total, List<string> samples) ValidateStatics(
        IReadOnlyList<List<StaticMulEntry>> blocks,
        int width,
        int height,
        LandTile[] mapTiles,
        IReadOnlyDictionary<ushort, List<StaticRule>> rules,
        IReadOnlySet<ushort> transitionStaticTiles,
        StaticsLayout layout)
    {
        var blockWidth = width / MapMul.BlockSize;
        var blockHeight = height / MapMul.BlockSize;
        var invalid = 0;
        var total = 0;
        var samples = new List<string>();

        for (var blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
        {
            var list = blocks[blockIndex];
            if (list is null || list.Count == 0)
            {
                continue;
            }

            var (blockX, blockY) = GetBlockCoords(blockIndex, blockWidth, blockHeight, layout);
            foreach (var entry in list)
            {
                total++;
                var worldX = blockX * MapMul.BlockSize + entry.X;
                var worldY = blockY * MapMul.BlockSize + entry.Y;

                if (!rules.TryGetValue(entry.TileId, out var ruleList) || ruleList.Count == 0)
                {
                    if (!transitionStaticTiles.Contains(entry.TileId))
                    {
                        invalid++;
                        if (samples.Count < 10)
                        {
                            samples.Add($"0x{entry.TileId:X4}@{worldX},{worldY} no rules");
                        }
                        continue;
                    }

                    continue;
                }

                var valid = false;
                foreach (var rule in ruleList)
                {
                    var baseX = worldX - rule.OffsetX;
                    var baseY = worldY - rule.OffsetY;
                    if (baseX < 0 || baseY < 0 || baseX >= width || baseY >= height)
                    {
                        continue;
                    }

                    var terrainTileId = mapTiles[baseY * width + baseX].TileId;
                    if (terrainTileId == rule.TerrainTileId)
                    {
                        valid = true;
                        break;
                    }
                }

                if (!valid)
                {
                    invalid++;
                    if (samples.Count < 10)
                    {
                        samples.Add($"0x{entry.TileId:X4}@{worldX},{worldY} no terrain match");
                    }
                }
            }
        }

        return (invalid, total, samples);
    }

    private static HashSet<StaticKey> BuildStaticSet(
        IReadOnlyList<List<StaticMulEntry>> blocks,
        int width,
        int height,
        StaticsLayout layout)
    {
        var blockWidth = width / MapMul.BlockSize;
        var blockHeight = height / MapMul.BlockSize;
        var result = new HashSet<StaticKey>();

        for (var blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
        {
            var list = blocks[blockIndex];
            if (list is null || list.Count == 0)
            {
                continue;
            }

            var (blockX, blockY) = GetBlockCoords(blockIndex, blockWidth, blockHeight, layout);
            foreach (var entry in list)
            {
                var worldX = blockX * MapMul.BlockSize + entry.X;
                var worldY = blockY * MapMul.BlockSize + entry.Y;
                if (worldX < 0 || worldX >= width || worldY < 0 || worldY >= height)
                {
                    continue;
                }

                result.Add(new StaticKey(worldX, worldY, entry.TileId, entry.Z));
            }
        }

        return result;
    }

    private static void MergeBlocks(List<StaticMulEntry>[] target, List<StaticMulEntry>[] source)
    {
        for (var i = 0; i < source.Length; i++)
        {
            var list = source[i];
            if (list is null || list.Count == 0)
            {
                continue;
            }

            var targetList = target[i] ??= new List<StaticMulEntry>(list.Count);
            targetList.AddRange(list);
        }
    }

    private static (int reference, int missing, int extra, double matchRatio, List<string> missingSamples, List<string> extraSamples)
        CompareStaticSets(HashSet<StaticKey> reference, HashSet<StaticKey> generated, string label)
    {
        var missing = 0;
        var extra = 0;
        var missingSamples = new List<string>();
        var extraSamples = new List<string>();

        foreach (var entry in reference)
        {
            if (!generated.Contains(entry))
            {
                missing++;
                if (missingSamples.Count < 10)
                {
                    missingSamples.Add(entry.ToString());
                }
            }
        }

        foreach (var entry in generated)
        {
            if (!reference.Contains(entry))
            {
                extra++;
                if (extraSamples.Count < 10)
                {
                    extraSamples.Add(entry.ToString());
                }
            }
        }

        var match = reference.Count - missing;
        var ratio = reference.Count == 0 ? 0 : (double)match / reference.Count;
        return (reference.Count, missing, extra, ratio, missingSamples, extraSamples);
    }

    private static (int x, int y) GetBlockCoords(int index, int blockWidth, int blockHeight, StaticsLayout layout)
    {
        return layout == StaticsLayout.ColumnMajor
            ? (index / blockHeight, index % blockHeight)
            : (index % blockWidth, index / blockWidth);
    }

    private readonly record struct StaticRule(int OffsetX, int OffsetY, ushort TerrainTileId);
    private readonly record struct StaticKey(int X, int Y, ushort TileId, sbyte Z);

    private static string? NormalizeDataRoot(string? dataRoot)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            return null;
        }

        var name = new DirectoryInfo(dataRoot).Name;
        if (name.Equals("UOMapWeaverData", StringComparison.OrdinalIgnoreCase))
        {
            return dataRoot;
        }

        var candidate = Path.Combine(dataRoot, "UOMapWeaverData");
        return Directory.Exists(candidate) ? candidate : dataRoot;
    }
}
