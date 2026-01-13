using System;
using System.IO;
using System.Linq;
using UOMapWeaver.Core.Bmp;
using UOMapWeaver.Core.Map;
using UOMapWeaver.Core.MapTrans;
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
        var repoRoot = FindRepoRoot();
        Assert.True(repoRoot is not null, "Unable to locate repo root.");

        var mapPath = Path.Combine(repoRoot!, "TMP_UtitlityFiles", "map0.mul");
        Assert.True(File.Exists(mapPath), $"map0.mul not found at {mapPath}");
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
        var repoRoot = FindRepoRoot();
        Assert.True(repoRoot is not null, "Unable to locate repo root.");

        var mapPath = Path.Combine(repoRoot!, "TMP_UtitlityFiles", "map0.mul");
        Assert.True(File.Exists(mapPath), $"map0.mul not found at {mapPath}");
        Assert.True(MapConversion.TryResolveMapSizeFromFile(mapPath, out var width, out var height),
            "Map size not detected for map0.mul.");

        var mapTransPath = FindMapTransProfile(repoRoot!);
        Assert.True(mapTransPath is not null, "MapTrans profile not found.");

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

    private static string? FindRepoRoot()
    {
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

    private static string? FindMapTransProfile(string repoRoot)
    {
        var candidateRoots = new[]
        {
            Path.Combine(repoRoot, "UOMapWeaverData", "System", "MapTrans"),
            Path.Combine(repoRoot, "TMP_UtitlityFiles", "MapCreator_golfin", "MapCompiler", "Engine", "MapTrans"),
            Path.Combine(repoRoot, "TMP_UtitlityFiles", "Data", "System", "MapTrans")
        };

        foreach (var root in candidateRoots)
        {
            if (!Directory.Exists(root))
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
}
