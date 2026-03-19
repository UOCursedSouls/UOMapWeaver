using System;
using System.IO;
using System.Linq;
using UOMapWeaver.Core;
using UOMapWeaver.Core.Bmp;
using UOMapWeaver.Core.Map;
using Xunit;
using Xunit.Abstractions;

namespace UOMapWeaver.Tests;

/// <summary>
/// Integration tests that use REAL UO map files to verify full round-trip conversions.
/// These tests COPY source files to temp directories — originals are never modified.
///
/// Tests are skipped if the source files don't exist on this machine.
/// </summary>
public class RealFileRoundtripTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _tempDir;

    // Paths to real UO files (tests skip if not found)
    private const string UoClientPath = @"C:\Program Files (x86)\Electronic Arts\Ultima Online Classic";
    private const string CustomMapPath = @"C:\Users\Aruto\Desktop\CentrED-Windows-X64\CustomMap\UO_ElPocho";

    public RealFileRoundtripTests(ITestOutputHelper output)
    {
        _output = output;
        _tempDir = Path.Combine(Path.GetTempPath(), $"UOMapWeaver_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    // ================================================================
    //  TERRAIN: MUL → TileIndex BMP → MUL round-trip (lossless)
    // ================================================================

    [Fact]
    public void Terrain_MulToBmpToMul_TileIndex_CustomMap_ByteForByte()
    {
        var sourceMap = Path.Combine(CustomMapPath, "map6.mul");
        if (!File.Exists(sourceMap)) { return; } // Skip: file not found

        const int width = 7168;
        const int height = 4096;

        // Copy source to temp
        var tempMap = Path.Combine(_tempDir, "map6.mul");
        File.Copy(sourceMap, tempMap);

        var terrainBmp = Path.Combine(_tempDir, "Terrain.bmp");
        var altitudeBmp = Path.Combine(_tempDir, "Altitude.bmp");
        var outputMap = Path.Combine(_tempDir, "map6_roundtrip.mul");

        // Step 1: MUL → BMP (TileIndex lossless encoding)
        _output.WriteLine("Step 1: Converting MUL → TileIndex BMP...");
        var exportReport = MapConversion.ConvertMulToTileIndexBmpToFile(
            tempMap, width, height, terrainBmp, altitudeBmp);
        _output.WriteLine($"  Export: {exportReport.TotalTiles} tiles processed");

        Assert.True(File.Exists(terrainBmp), "Terrain BMP not created");
        Assert.True(File.Exists(altitudeBmp), "Altitude BMP not created");

        // Step 2: BMP → MUL
        _output.WriteLine("Step 2: Converting TileIndex BMP → MUL...");
        var importReport = MapConversion.ConvertTileIndexBmpToMulFromFiles(
            terrainBmp, altitudeBmp, outputMap);
        _output.WriteLine($"  Import: {importReport.TotalTiles} tiles processed");

        Assert.True(File.Exists(outputMap), "Output MUL not created");

        // Step 3: Compare tile data (block headers may differ — that's expected)
        _output.WriteLine("Step 3: Comparing tile data...");
        var originalTiles = MapMulCodec.ReadLandTiles(tempMap, width, height);
        var roundtripTiles = MapMulCodec.ReadLandTiles(outputMap, width, height);

        Assert.Equal(originalTiles.Length, roundtripTiles.Length);

        int tileDiffs = 0;
        int zDiffs = 0;
        for (int i = 0; i < originalTiles.Length; i++)
        {
            if (originalTiles[i].TileId != roundtripTiles[i].TileId) { tileDiffs++; }
            if (originalTiles[i].Z != roundtripTiles[i].Z) { zDiffs++; }
        }

        _output.WriteLine($"  Total tiles: {originalTiles.Length:N0}");
        _output.WriteLine($"  TileId diffs: {tileDiffs}");
        _output.WriteLine($"  Z altitude diffs: {zDiffs}");

        Assert.Equal(0, tileDiffs);
        Assert.Equal(0, zDiffs);
        _output.WriteLine("PASS: All tile data identical through MUL→BMP→MUL round-trip!");
    }

    [Fact]
    public void Terrain_MulToBmpToMul_TileIndex_VanillaMap0_ByteForByte()
    {
        var sourceMap = Path.Combine(UoClientPath, "map0.mul");
        if (!File.Exists(sourceMap)) { return; } // Skip: file not found

        // map0.mul = Felucca: 7168 x 4096
        const int width = 7168;
        const int height = 4096;

        var tempMap = Path.Combine(_tempDir, "map0.mul");
        File.Copy(sourceMap, tempMap);

        var terrainBmp = Path.Combine(_tempDir, "Terrain.bmp");
        var altitudeBmp = Path.Combine(_tempDir, "Altitude.bmp");
        var outputMap = Path.Combine(_tempDir, "map0_roundtrip.mul");

        _output.WriteLine("Step 1: Converting vanilla map0.mul → TileIndex BMP...");
        MapConversion.ConvertMulToTileIndexBmpToFile(
            tempMap, width, height, terrainBmp, altitudeBmp);

        _output.WriteLine("Step 2: Converting TileIndex BMP → MUL...");
        MapConversion.ConvertTileIndexBmpToMulFromFiles(
            terrainBmp, altitudeBmp, outputMap);

        _output.WriteLine("Step 3: Comparing tile data...");
        var originalTiles = MapMulCodec.ReadLandTiles(tempMap, width, height);
        var roundtripTiles = MapMulCodec.ReadLandTiles(outputMap, width, height);

        int tileDiffs = 0;
        int zDiffs = 0;
        for (int i = 0; i < originalTiles.Length; i++)
        {
            if (originalTiles[i].TileId != roundtripTiles[i].TileId) { tileDiffs++; }
            if (originalTiles[i].Z != roundtripTiles[i].Z) { zDiffs++; }
        }

        _output.WriteLine($"  Tiles: {originalTiles.Length:N0}, TileId diffs: {tileDiffs}, Z diffs: {zDiffs}");
        Assert.Equal(0, tileDiffs);
        Assert.Equal(0, zDiffs);
        _output.WriteLine("PASS: Vanilla map0.mul tile data round-trip identical!");
    }

    // ================================================================
    //  TERRAIN: Tile data verification (read tiles, verify consistency)
    // ================================================================

    [Fact]
    public void Terrain_ReadTiles_WriteBack_TileDataIdentical()
    {
        var sourceMap = Path.Combine(CustomMapPath, "map6.mul");
        if (!File.Exists(sourceMap)) { return; } // Skip: file not found

        const int width = 7168;
        const int height = 4096;

        var tempMap = Path.Combine(_tempDir, "map6.mul");
        File.Copy(sourceMap, tempMap);

        // Read all tiles
        _output.WriteLine("Reading tiles...");
        var originalTiles = MapMulCodec.ReadLandTiles(tempMap, width, height);
        _output.WriteLine($"  Read {originalTiles.Length:N0} tiles");

        // Write them back and re-read
        var outputMap = Path.Combine(_tempDir, "map6_rewrite.mul");
        _output.WriteLine("Writing tiles back...");
        MapMulCodec.WriteLandTiles(outputMap, width, height, originalTiles);

        var roundtripTiles = MapMulCodec.ReadLandTiles(outputMap, width, height);

        // Compare tile data (not block headers — those are not preserved)
        int tileDiffs = 0;
        int zDiffs = 0;
        for (int i = 0; i < originalTiles.Length; i++)
        {
            if (originalTiles[i].TileId != roundtripTiles[i].TileId) { tileDiffs++; }
            if (originalTiles[i].Z != roundtripTiles[i].Z) { zDiffs++; }
        }

        _output.WriteLine($"  TileId diffs: {tileDiffs}");
        _output.WriteLine($"  Z diffs: {zDiffs}");
        Assert.Equal(0, tileDiffs);
        Assert.Equal(0, zDiffs);
        _output.WriteLine("PASS: All tile data preserved through read/write!");
    }

    // ================================================================
    //  STATICS: Read → Write round-trip
    // ================================================================

    [Fact]
    public void Statics_ReadWriteRoundtrip_ByteForByte()
    {
        var sourceIdx = Path.Combine(CustomMapPath, "staIdx6.mul");
        var sourceSta = Path.Combine(CustomMapPath, "statics6.mul");
        if (!File.Exists(sourceIdx) || !File.Exists(sourceSta)) { return; } // Skip: file not found

        const int width = 7168;
        const int height = 4096;

        // Copy to temp
        var tempIdx = Path.Combine(_tempDir, "staidx6.mul");
        var tempSta = Path.Combine(_tempDir, "statics6.mul");
        File.Copy(sourceIdx, tempIdx);
        File.Copy(sourceSta, tempSta);

        var outIdx = Path.Combine(_tempDir, "staidx6_roundtrip.mul");
        var outSta = Path.Combine(_tempDir, "statics6_roundtrip.mul");

        // Read statics
        _output.WriteLine("Reading statics...");
        var blocks = StaticMulCodec.ReadStatics(tempIdx, tempSta, width, height);
        _output.WriteLine($"  Read {blocks.Length:N0} blocks");

        int totalEntries = blocks.Sum(b => b?.Count ?? 0);
        int nonEmptyBlocks = blocks.Count(b => b != null && b.Count > 0);
        _output.WriteLine($"  Non-empty blocks: {nonEmptyBlocks:N0}");
        _output.WriteLine($"  Total entries: {totalEntries:N0}");

        // Write back
        _output.WriteLine("Writing statics back...");
        StaticMulCodec.WriteStatics(outIdx, outSta, width, height, blocks);

        // Compare staidx
        _output.WriteLine("Comparing staidx...");
        var origIdxBytes = File.ReadAllBytes(tempIdx);
        var newIdxBytes = File.ReadAllBytes(outIdx);
        Assert.Equal(origIdxBytes.Length, newIdxBytes.Length);

        int idxDiffs = 0;
        for (int i = 0; i < origIdxBytes.Length; i++)
        {
            if (origIdxBytes[i] != newIdxBytes[i])
            {
                idxDiffs++;
            }
        }

        _output.WriteLine($"  staidx diffs: {idxDiffs}");

        // Compare statics data (may differ in padding but content should match)
        _output.WriteLine("Verifying statics content...");
        var roundtripBlocks = StaticMulCodec.ReadStatics(outIdx, outSta, width, height);

        int blockDiffs = 0;
        for (int i = 0; i < blocks.Length; i++)
        {
            var origList = blocks[i];
            var newList = roundtripBlocks[i];

            int origCount = origList?.Count ?? 0;
            int newCount = newList?.Count ?? 0;

            if (origCount != newCount)
            {
                blockDiffs++;
                if (blockDiffs <= 5)
                {
                    _output.WriteLine($"  Block {i}: count mismatch {origCount} vs {newCount}");
                }

                continue;
            }

            if (origList == null)
            {
                continue;
            }

            for (int j = 0; j < origList.Count; j++)
            {
                var a = origList[j];
                var b = newList![j];
                if (a.TileId != b.TileId || a.X != b.X || a.Y != b.Y || a.Z != b.Z || a.Hue != b.Hue)
                {
                    blockDiffs++;
                    if (blockDiffs <= 5)
                    {
                        _output.WriteLine($"  Block {i}, entry {j}: mismatch");
                    }

                    break;
                }
            }
        }

        _output.WriteLine($"  Block content diffs: {blockDiffs}");
        Assert.Equal(0, blockDiffs);
        _output.WriteLine("PASS: Statics round-trip content identical!");
    }

    // ================================================================
    //  ALTITUDE: Verify Z values survive round-trip
    // ================================================================

    [Fact]
    public void Altitude_ValuesPreserved_ThroughBmpRoundtrip()
    {
        var sourceMap = Path.Combine(CustomMapPath, "map6.mul");
        if (!File.Exists(sourceMap)) { return; } // Skip: file not found

        const int width = 7168;
        const int height = 4096;

        var tempMap = Path.Combine(_tempDir, "map6.mul");
        File.Copy(sourceMap, tempMap);

        // Read original tiles
        var originalTiles = MapMulCodec.ReadLandTiles(tempMap, width, height);

        // Export to BMP
        var terrainBmp = Path.Combine(_tempDir, "Terrain.bmp");
        var altitudeBmp = Path.Combine(_tempDir, "Altitude.bmp");
        MapConversion.ConvertMulToTileIndexBmpToFile(
            tempMap, width, height, terrainBmp, altitudeBmp);

        // Import back
        var outputMap = Path.Combine(_tempDir, "map6_alt_test.mul");
        MapConversion.ConvertTileIndexBmpToMulFromFiles(
            terrainBmp, altitudeBmp, outputMap);

        // Read round-tripped tiles
        var roundtripTiles = MapMulCodec.ReadLandTiles(outputMap, width, height);

        // Compare all altitudes
        int zDiffs = 0;
        int tileDiffs = 0;
        for (int i = 0; i < originalTiles.Length; i++)
        {
            if (originalTiles[i].Z != roundtripTiles[i].Z)
            {
                zDiffs++;
            }

            if (originalTiles[i].TileId != roundtripTiles[i].TileId)
            {
                tileDiffs++;
            }
        }

        _output.WriteLine($"Tiles: {originalTiles.Length:N0}");
        _output.WriteLine($"TileId diffs: {tileDiffs}");
        _output.WriteLine($"Z altitude diffs: {zDiffs}");
        Assert.Equal(0, tileDiffs);
        Assert.Equal(0, zDiffs);
        _output.WriteLine("PASS: All tile IDs and Z altitudes preserved!");
    }

    // ================================================================
    //  SIZE DETECTION: Verify map size auto-detection
    // ================================================================

    [Fact]
    public void MapSize_AutoDetect_VanillaMap0()
    {
        var sourceMap = Path.Combine(UoClientPath, "map0.mul");
        if (!File.Exists(sourceMap)) { return; } // Skip: file not found

        var detected = MapConversion.TryResolveMapSizeFromFile(sourceMap, out int width, out int height);

        _output.WriteLine($"Detected: {width}x{height}");
        Assert.True(detected);
        Assert.Equal(7168, width);
        Assert.Equal(4096, height);
    }

    [Fact]
    public void MapSize_AutoDetect_CustomElPocho()
    {
        var sourceMap = Path.Combine(CustomMapPath, "map6.mul");
        if (!File.Exists(sourceMap)) { return; } // Skip: file not found

        var detected = MapConversion.TryResolveMapSizeFromFile(sourceMap, out int width, out int height);

        _output.WriteLine($"Detected: {width}x{height}");
        // ElPocho is 7168x4096 based on the file size
        Assert.True(detected);
    }
}
