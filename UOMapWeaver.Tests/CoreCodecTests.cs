using System;
using System.IO;
using UOMapWeaver.Core;
using UOMapWeaver.Core.Bmp;
using UOMapWeaver.Core.Map;
using UOMapWeaver.Core.TileReplace;

namespace UOMapWeaver.Tests;

public sealed class CoreCodecTests : IDisposable
{
    private readonly string _tempDir;

    public CoreCodecTests()
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

    // --- BMP Codec round-trip tests ---

    [Fact]
    public void Bmp24Codec_RoundTrip_PreservesPixels()
    {
        var width = 10;
        var height = 8;
        var pixels = new byte[width * height * 3];
        var rng = new Random(42);
        rng.NextBytes(pixels);

        var path = Path.Combine(_tempDir, "roundtrip24.bmp");
        var original = new Bmp24Image(width, height, pixels);
        Bmp24Codec.Write(path, original);

        var loaded = Bmp24Codec.Read(path);

        Assert.Equal(width, loaded.Width);
        Assert.Equal(height, loaded.Height);
        Assert.Equal(pixels.Length, loaded.Pixels.Length);

        for (var i = 0; i < pixels.Length; i++)
        {
            if (pixels[i] != loaded.Pixels[i])
            {
                Assert.Fail($"Pixel mismatch at byte {i}: expected {pixels[i]}, got {loaded.Pixels[i]}");
            }
        }
    }

    [Fact]
    public void Bmp8Codec_RoundTrip_PreservesPixelsAndPalette()
    {
        var width = 12;
        var height = 6;
        var pixels = new byte[width * height];
        var rng = new Random(99);
        rng.NextBytes(pixels);

        var palette = Bmp8Codec.CreateGrayscalePalette();
        var path = Path.Combine(_tempDir, "roundtrip8.bmp");
        var original = new Bmp8Image(width, height, pixels, palette);
        Bmp8Codec.Write(path, original);

        var loaded = Bmp8Codec.Read(path);

        Assert.Equal(width, loaded.Width);
        Assert.Equal(height, loaded.Height);
        Assert.Equal(256, loaded.Palette.Length);

        for (var i = 0; i < pixels.Length; i++)
        {
            if (pixels[i] != loaded.Pixels[i])
            {
                Assert.Fail($"Pixel mismatch at index {i}: expected {pixels[i]}, got {loaded.Pixels[i]}");
            }
        }

        for (var i = 0; i < 256; i++)
        {
            Assert.Equal(palette[i].Red, loaded.Palette[i].Red);
            Assert.Equal(palette[i].Green, loaded.Palette[i].Green);
            Assert.Equal(palette[i].Blue, loaded.Palette[i].Blue);
        }
    }

    // --- StaticMulEntry serialization test ---

    [Fact]
    public void StaticMulCodec_WriteRead_PreservesEntries()
    {
        var width = 16;
        var height = 16;
        var blockWidth = width / MapMul.BlockSize;
        var blockHeight = height / MapMul.BlockSize;
        var blockCount = blockWidth * blockHeight;

        var blocks = new List<StaticMulEntry>[blockCount];
        blocks[0] = new List<StaticMulEntry>
        {
            new(0x1234, 3, 5, -10, 0x0010),
            new(0x0001, 0, 0, 0, 0x0000),
            new(0xFFFF, 7, 7, 127, 0xFFFF)
        };
        blocks[1] = new List<StaticMulEntry>
        {
            new(0x0100, 1, 2, -128, 0x0005)
        };
        // blocks[2] and blocks[3] intentionally left null (empty blocks)

        var staIdxPath = Path.Combine(_tempDir, "staidx_test.mul");
        var staticsPath = Path.Combine(_tempDir, "statics_test.mul");

        StaticMulCodec.WriteStatics(staIdxPath, staticsPath, width, height, blocks);
        var loaded = StaticMulCodec.ReadStatics(staIdxPath, staticsPath, width, height);

        Assert.Equal(blockCount, loaded.Length);

        // Block 0
        Assert.NotNull(loaded[0]);
        Assert.Equal(3, loaded[0].Count);
        AssertStaticEntry(loaded[0][0], 0x1234, 3, 5, -10, 0x0010);
        AssertStaticEntry(loaded[0][1], 0x0001, 0, 0, 0, 0x0000);
        AssertStaticEntry(loaded[0][2], 0xFFFF, 7, 7, 127, 0xFFFF);

        // Block 1
        Assert.NotNull(loaded[1]);
        Assert.Single(loaded[1]);
        AssertStaticEntry(loaded[1][0], 0x0100, 1, 2, -128, 0x0005);

        // Empty blocks
        Assert.Null(loaded[2]);
        Assert.Null(loaded[3]);
    }

    [Fact]
    public void StaticMulCodec_WriteEmptyStatics_ProducesAllNullBlocks()
    {
        var width = 16;
        var height = 16;
        var staIdxPath = Path.Combine(_tempDir, "staidx_empty.mul");
        var staticsPath = Path.Combine(_tempDir, "statics_empty.mul");

        StaticMulCodec.WriteEmptyStatics(staIdxPath, staticsPath, width, height);
        var loaded = StaticMulCodec.ReadStatics(staIdxPath, staticsPath, width, height);

        var blockCount = (width / MapMul.BlockSize) * (height / MapMul.BlockSize);
        Assert.Equal(blockCount, loaded.Length);

        for (var i = 0; i < loaded.Length; i++)
        {
            Assert.Null(loaded[i]);
        }
    }

    // --- TileReplacementMap tests ---

    [Fact]
    public void TileReplacementMap_SaveLoad_PreservesEntries()
    {
        var map = new TileReplacementMap(
            terrain: new Dictionary<ushort, ushort> { { 0x0001, 0x0002 }, { 0x00FF, 0x0100 } },
            statics: new Dictionary<ushort, ushort> { { 0x1000, 0x2000 } })
        {
            SourceClientPath = @"C:\Source",
            DestClientPath = @"C:\Dest"
        };

        var path = Path.Combine(_tempDir, "tile_replace.json");
        TileReplacementMapSerializer.Save(path, map);

        Assert.True(File.Exists(path));

        var loaded = TileReplacementMapSerializer.Load(path);

        Assert.Equal(2, loaded.Terrain.Count);
        Assert.True(loaded.TryGetTerrainReplacement(0x0001, out var t1));
        Assert.Equal((ushort)0x0002, t1);
        Assert.True(loaded.TryGetTerrainReplacement(0x00FF, out var t2));
        Assert.Equal((ushort)0x0100, t2);

        Assert.Single(loaded.Statics);
        Assert.True(loaded.TryGetStaticReplacement(0x1000, out var s1));
        Assert.Equal((ushort)0x2000, s1);

        Assert.Equal(@"C:\Source", loaded.SourceClientPath);
        Assert.Equal(@"C:\Dest", loaded.DestClientPath);
    }

    [Fact]
    public void TileReplacementMap_MissingKey_ReturnsFalse()
    {
        var map = new TileReplacementMap();
        Assert.False(map.TryGetTerrainReplacement(0x9999, out _));
        Assert.False(map.TryGetStaticReplacement(0x9999, out _));
    }

    [Fact]
    public void TileReplacementMapSerializer_TryRead_ReturnsFalseForMissingFile()
    {
        var path = Path.Combine(_tempDir, "nonexistent.json");
        var result = TileReplacementMapSerializer.TryRead(path, out var map);
        Assert.False(result);
        Assert.NotNull(map);
        Assert.Empty(map.Terrain);
        Assert.Empty(map.Statics);
    }

    // --- MapMulCodec block reading/writing tests ---

    [Fact]
    public void MapMulCodec_WriteRead_PreservesLandTiles()
    {
        // Use a minimal 16x16 map (2x2 blocks)
        var width = 16;
        var height = 16;
        var tileCount = width * height;
        var tiles = new LandTile[tileCount];

        var rng = new Random(123);
        for (var i = 0; i < tileCount; i++)
        {
            var tileId = (ushort)rng.Next(0, 0x4000);
            var z = (sbyte)rng.Next(-128, 128);
            tiles[i] = new LandTile(tileId, z);
        }

        var mapPath = Path.Combine(_tempDir, "map_test.mul");
        MapMulCodec.WriteLandTiles(mapPath, width, height, tiles);
        var loaded = MapMulCodec.ReadLandTiles(mapPath, width, height);

        Assert.Equal(tileCount, loaded.Length);
        for (var i = 0; i < tileCount; i++)
        {
            if (tiles[i].TileId != loaded[i].TileId || tiles[i].Z != loaded[i].Z)
            {
                Assert.Fail(
                    $"Tile mismatch at {i}: expected 0x{tiles[i].TileId:X4}/{tiles[i].Z}, got 0x{loaded[i].TileId:X4}/{loaded[i].Z}");
            }
        }
    }

    [Fact]
    public void MapMulCodec_ReadRegion_MatchesFullRead()
    {
        var width = 32;
        var height = 32;
        var tileCount = width * height;
        var tiles = new LandTile[tileCount];

        var rng = new Random(456);
        for (var i = 0; i < tileCount; i++)
        {
            tiles[i] = new LandTile((ushort)rng.Next(0, 0x4000), (sbyte)rng.Next(-128, 128));
        }

        var mapPath = Path.Combine(_tempDir, "map_region.mul");
        MapMulCodec.WriteLandTiles(mapPath, width, height, tiles);

        // Read a sub-region
        var regionX = 8;
        var regionY = 8;
        var regionW = 16;
        var regionH = 16;
        var region = MapMulCodec.ReadLandTilesRegion(mapPath, width, height, regionX, regionY, regionW, regionH);

        Assert.Equal(regionW * regionH, region.Length);

        for (var ry = 0; ry < regionH; ry++)
        {
            for (var rx = 0; rx < regionW; rx++)
            {
                var expected = tiles[(regionY + ry) * width + (regionX + rx)];
                var actual = region[ry * regionW + rx];
                if (expected.TileId != actual.TileId || expected.Z != actual.Z)
                {
                    Assert.Fail(
                        $"Region mismatch at ({rx},{ry}): expected 0x{expected.TileId:X4}/{expected.Z}, got 0x{actual.TileId:X4}/{actual.Z}");
                }
            }
        }
    }

    [Fact]
    public void MapMulCodec_WriteLandTilesFromRows_MatchesDirectWrite()
    {
        var width = 16;
        var height = 16;
        var tileCount = width * height;
        var tiles = new LandTile[tileCount];

        var rng = new Random(789);
        for (var i = 0; i < tileCount; i++)
        {
            tiles[i] = new LandTile((ushort)rng.Next(0, 0x4000), (sbyte)rng.Next(-128, 128));
        }

        var directPath = Path.Combine(_tempDir, "map_direct.mul");
        var rowPath = Path.Combine(_tempDir, "map_rows.mul");

        MapMulCodec.WriteLandTiles(directPath, width, height, tiles);
        MapMulCodec.WriteLandTilesFromRows(rowPath, width, height, (y, row) =>
        {
            for (var x = 0; x < width; x++)
            {
                row[x] = tiles[y * width + x];
            }
        });

        var directTiles = MapMulCodec.ReadLandTiles(directPath, width, height);
        var rowTiles = MapMulCodec.ReadLandTiles(rowPath, width, height);

        Assert.Equal(directTiles.Length, rowTiles.Length);
        for (var i = 0; i < directTiles.Length; i++)
        {
            if (directTiles[i].TileId != rowTiles[i].TileId || directTiles[i].Z != rowTiles[i].Z)
            {
                Assert.Fail(
                    $"Row write mismatch at {i}: expected 0x{directTiles[i].TileId:X4}/{directTiles[i].Z}, got 0x{rowTiles[i].TileId:X4}/{rowTiles[i].Z}");
            }
        }
    }

    private static void AssertStaticEntry(StaticMulEntry entry, ushort tileId, byte x, byte y, sbyte z, ushort hue)
    {
        Assert.Equal(tileId, entry.TileId);
        Assert.Equal(x, entry.X);
        Assert.Equal(y, entry.Y);
        Assert.Equal(z, entry.Z);
        Assert.Equal(hue, entry.Hue);
    }
}
