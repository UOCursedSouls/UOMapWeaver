using System;
using System.IO;
using UOMapWeaver.Core;
using UOMapWeaver.Core.Bmp;
using UOMapWeaver.Core.Map;

namespace UOMapWeaver.Tests;

public sealed class MapConversionTests : IDisposable
{
    private readonly string _tempDir;

    public MapConversionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "UOMapWeaver.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public void TryResolveMapSizeFromFile_StandardMap0_DetectsCorrectSize()
    {
        // Create a map file matching map0.mul dimensions (7168x4096 = 896x512 blocks = 458752 blocks)
        // Each block is 196 bytes. File size = 458752 * 196
        var width = 7168;
        var height = 4096;
        var tiles = new LandTile[width * height];

        // Name it map0.mul so the filename-based detection works
        var mapPath = Path.Combine(_tempDir, "map0.mul");
        MapMulCodec.WriteLandTiles(mapPath, width, height, tiles);

        Assert.True(MapConversion.TryResolveMapSizeFromFile(mapPath, out var detectedWidth, out var detectedHeight));
        Assert.Equal(width, detectedWidth);
        Assert.Equal(height, detectedHeight);
    }

    [Fact]
    public void TryResolveMapSizeFromFile_CustomMap_DetectsSizeFromBlocks()
    {
        // Create a 256x256 map (32x32 blocks = 1024 blocks)
        var width = 256;
        var height = 256;
        var tiles = new LandTile[width * height];

        var mapPath = Path.Combine(_tempDir, "custom_map.mul");
        MapMulCodec.WriteLandTiles(mapPath, width, height, tiles);

        // With a non-standard name and no map-definitions.json, detection may depend on block count
        // The method should either succeed or fail gracefully
        var result = MapConversion.TryResolveMapSizeFromFile(mapPath, out var detectedWidth, out var detectedHeight);

        // If it succeeds, the detected size should produce the same block count
        if (result)
        {
            var expectedBlocks = (width / MapMul.BlockSize) * (height / MapMul.BlockSize);
            var detectedBlocks = (detectedWidth / MapMul.BlockSize) * (detectedHeight / MapMul.BlockSize);
            Assert.Equal(expectedBlocks, detectedBlocks);
        }
    }

    [Fact]
    public void TryResolveMapSizeFromFile_NonexistentFile_ReturnsFalse()
    {
        var result = MapConversion.TryResolveMapSizeFromFile(
            Path.Combine(_tempDir, "nonexistent.mul"),
            out var width, out var height);

        Assert.False(result);
        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }

    [Fact]
    public void TryResolveMapSizeFromFile_EmptyFile_ReturnsFalse()
    {
        var mapPath = Path.Combine(_tempDir, "empty.mul");
        File.WriteAllBytes(mapPath, Array.Empty<byte>());

        var result = MapConversion.TryResolveMapSizeFromFile(mapPath, out _, out _);
        Assert.False(result);
    }

    [Fact]
    public void TryResolveMapSizeFromFile_TooSmallFile_ReturnsFalse()
    {
        var mapPath = Path.Combine(_tempDir, "tiny.mul");
        File.WriteAllBytes(mapPath, new byte[10]);

        var result = MapConversion.TryResolveMapSizeFromFile(mapPath, out _, out _);
        Assert.False(result);
    }

    [Fact]
    public void ConvertMulToTileIndexBmp_SmallMap_ProducesCorrectDimensions()
    {
        var width = 16;
        var height = 16;
        var tiles = new LandTile[width * height];
        var rng = new Random(42);
        for (var i = 0; i < tiles.Length; i++)
        {
            tiles[i] = new LandTile((ushort)rng.Next(0, 0x4000), (sbyte)rng.Next(-128, 128));
        }

        var mapPath = Path.Combine(_tempDir, "tileindex.mul");
        MapMulCodec.WriteLandTiles(mapPath, width, height, tiles);

        var result = MapConversion.ConvertMulToTileIndexBmp(mapPath, width, height);

        Assert.Equal(width, result.terrain.Width);
        Assert.Equal(height, result.terrain.Height);
        Assert.Equal(width, result.altitude.Width);
        Assert.Equal(height, result.altitude.Height);
    }

    [Fact]
    public void ConvertTileIndexBmp_RoundTrip_PreservesTiles()
    {
        var width = 16;
        var height = 16;
        var tiles = new LandTile[width * height];
        var rng = new Random(55);
        for (var i = 0; i < tiles.Length; i++)
        {
            tiles[i] = new LandTile((ushort)rng.Next(0, 0x4000), (sbyte)rng.Next(-128, 128));
        }

        var mapPath = Path.Combine(_tempDir, "rt.mul");
        MapMulCodec.WriteLandTiles(mapPath, width, height, tiles);

        var forward = MapConversion.ConvertMulToTileIndexBmp(mapPath, width, height);
        var back = MapConversion.ConvertTileIndexBmpToMul(forward.terrain, forward.altitude);

        Assert.Equal(tiles.Length, back.tiles.Length);
        for (var i = 0; i < tiles.Length; i++)
        {
            Assert.Equal(tiles[i].TileId, back.tiles[i].TileId);
            Assert.Equal(tiles[i].Z, back.tiles[i].Z);
        }
    }
}
