using System;
using System.IO;
using UOMapWeaver.Core;
using UOMapWeaver.Core.Map;

namespace UOMapWeaver.Tests;

public sealed class VerdataMulTests : IDisposable
{
    private readonly string _tempDir;

    public VerdataMulTests()
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
    public void Load_SyntheticVerdata_ParsesMapPatches()
    {
        // Build a synthetic verdata.mul with one map block patch.
        // Verdata format: 4 bytes entry count, then entries of (fileId, blockId, offset, length, extra)
        // Then actual data blocks.
        var path = Path.Combine(_tempDir, "verdata.mul");
        var blockCount = 4; // 16x16 map = 2x2 blocks

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            var entryCount = 1;
            writer.Write(entryCount); // entry count

            // Entry: fileId=0, blockId=0, offset=<after headers>, length=LandBlockBytes, extra=0
            var dataOffset = 4 + entryCount * 20; // 4 (entry count) + 1 * 5 ints
            writer.Write(0);          // fileId
            writer.Write(0);          // blockId
            writer.Write(dataOffset); // offset
            writer.Write(MapMul.LandBlockBytes); // length
            writer.Write(0);          // extra

            // Write a land block: 4 header bytes + 64 tiles (3 bytes each)
            writer.Write(0); // header (4 bytes)
            for (var i = 0; i < MapMul.LandTilesPerBlock; i++)
            {
                writer.Write((ushort)(i + 1)); // tileId
                writer.Write((byte)10);        // Z as byte (sbyte 10)
            }
        }

        var verdata = VerdataMul.Load(path, blockCount, mapFileIdOverride: 0);

        Assert.Equal(0, verdata.MapFileId);
        Assert.Equal(1, verdata.MapPatchCount);
        Assert.True(verdata.TryGetMapBlock(0, out var block));
        Assert.Equal(MapMul.LandTilesPerBlock, block.Length);
        Assert.Equal((ushort)1, block[0].TileId);
        Assert.Equal((sbyte)10, block[0].Z);
        Assert.Equal((ushort)64, block[63].TileId);
    }

    [Fact]
    public void Load_EmptyVerdata_ReturnsNoPatches()
    {
        var path = Path.Combine(_tempDir, "empty_verdata.mul");

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(0); // 0 entries
        }

        var verdata = VerdataMul.Load(path, blockCount: 4);

        Assert.Equal(0, verdata.MapPatchCount);
        Assert.Equal(0, verdata.StaticsPatchCount);
        Assert.False(verdata.TryGetMapBlock(0, out _));
    }

    [Fact]
    public void ApplyToLandTiles_OverwritesTargetTiles()
    {
        var path = Path.Combine(_tempDir, "apply_verdata.mul");
        var width = 16;
        var height = 16;
        var blockCount = (width / MapMul.BlockSize) * (height / MapMul.BlockSize);

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(1); // 1 entry

            var dataOffset = 4 + 20;
            writer.Write(5);          // fileId
            writer.Write(0);          // blockId=0
            writer.Write(dataOffset); // offset
            writer.Write(MapMul.LandBlockBytes);
            writer.Write(0);

            writer.Write(0); // header
            for (var i = 0; i < MapMul.LandTilesPerBlock; i++)
            {
                writer.Write((ushort)999);
                writer.Write((byte)50);
            }
        }

        var verdata = VerdataMul.Load(path, blockCount, mapFileIdOverride: 5);

        // Create original tiles (all zeros)
        var tiles = new LandTile[width * height];
        for (var i = 0; i < tiles.Length; i++)
        {
            tiles[i] = new LandTile(0, 0);
        }

        verdata.ApplyToLandTiles(tiles, width, height);

        // Block 0 is at column 0, row 0 -> first 8x8 tile area
        Assert.Equal((ushort)999, tiles[0].TileId);
        Assert.Equal((sbyte)50, tiles[0].Z);
    }
}
