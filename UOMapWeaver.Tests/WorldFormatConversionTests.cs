using UOMapWeaver.Core.Map;
using UOMapWeaver.Core.Uop;
using UOMapWeaver.Core.WorldFormats;
using UOMapWeaver.Plugins;

namespace UOMapWeaver.Tests;

public sealed class WorldFormatConversionTests : IDisposable
{
    private const int Size = 128;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uomw-wfc-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryPlugin _plugin = new();

    public WorldFormatConversionTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private string P(string name) => Path.Combine(_root, name);

    private (LandTile[] Land, List<StaticMulEntry>[] Statics) WriteFixture()
    {
        var land = new LandTile[Size * Size];
        for (var i = 0; i < land.Length; i++)
        {
            land[i] = new LandTile((ushort)(i * 7 % 0x3FFF), (sbyte)(i % 100 - 50));
        }

        var statics = new List<StaticMulEntry>[(Size / 8) * (Size / 8)];
        for (var b = 0; b < statics.Length; b++)
        {
            if (b % 3 == 0)
            {
                continue;
            }

            statics[b] = [new((ushort)(b + 1), (byte)(b % 8), 3, (sbyte)(b - 100), (ushort)b), new(0x1234, 7, 7, 5, 0)];
        }

        MapMulCodec.WriteLandTiles(P("map.mul"), Size, Size, land);
        StaticMulCodec.WriteStatics(P("staidx.mul"), P("statics.mul"), Size, Size, statics);
        return (land, statics);
    }

    private void CheckRoundTrip(string mapPath)
    {
        var (land, statics) = WriteFixture();
        const int x = 16, y = 8, w = 64, h = 48;

        var blocks = WorldFormatConversion.ImportArea(_plugin, P("world"), "w", mapPath, P("staidx.mul"), P("statics.mul"), Size, Size, x, y, w, h);
        Assert.Equal(8 * 6, blocks);
        WorldFormatConversion.ExportWorld(_plugin, P("world"), P("out-map.mul"), P("out-staidx.mul"), P("out-statics.mul"));

        var cut = new LandTile[w * h];
        for (var ty = 0; ty < h; ty++)
        {
            for (var tx = 0; tx < w; tx++)
            {
                cut[ty * w + tx] = land[(y + ty) * Size + x + tx];
            }
        }

        var cutStatics = new List<StaticMulEntry>[(w / 8) * (h / 8)];
        for (var bx = 0; bx < w / 8; bx++)
        {
            for (var by = 0; by < h / 8; by++)
            {
                cutStatics[bx * (h / 8) + by] = statics[(x / 8 + bx) * (Size / 8) + y / 8 + by];
            }
        }

        MapMulCodec.WriteLandTiles(P("exp-map.mul"), w, h, cut);
        StaticMulCodec.WriteStatics(P("exp-staidx.mul"), P("exp-statics.mul"), w, h, cutStatics);

        Assert.Equal(File.ReadAllBytes(P("exp-map.mul")), File.ReadAllBytes(P("out-map.mul")));
        Assert.Equal(File.ReadAllBytes(P("exp-staidx.mul")), File.ReadAllBytes(P("out-staidx.mul")));
        Assert.Equal(File.ReadAllBytes(P("exp-statics.mul")), File.ReadAllBytes(P("out-statics.mul")));
    }

    [Fact]
    public void WorldFormatConversion_MulAreaRoundTrip_ByteIdentical()
    {
        CheckRoundTrip(P("map.mul"));
    }

    [Fact]
    public void WorldFormatConversion_UopAreaRoundTrip_ByteIdentical()
    {
        WriteFixture();
        UopCodec.PackLegacyUopFromMul(P("map.mul"), 0, P("map0LegacyMUL.uop"));
        File.Delete(P("map.mul"));
        CheckRoundTrip(P("map0LegacyMUL.uop"));
    }

    [Fact]
    public void WorldFormatConversion_UnalignedRectangle_Refused()
    {
        WriteFixture();
        Assert.Throws<ArgumentException>(() =>
            WorldFormatConversion.ImportArea(_plugin, P("world"), "w", P("map.mul"), null, null, Size, Size, 4, 8, 64, 48));
        Assert.Throws<ArgumentException>(() =>
            WorldFormatConversion.ImportArea(_plugin, P("world"), "w", P("map.mul"), null, null, Size, Size, 16, 8, 60, 48));
    }

    [Fact]
    public void WorldFormatConversion_MissingBlock_ExportsWater()
    {
        using (var w = _plugin.CreateWorld(P("world"), new WorldCreateOptions("w", 2, 1)))
        {
            w.WriteBlock(new WorldBlock(0, 0, Enumerable.Repeat(new WorldLandTile(5, 3), 64).ToArray(), []));
            w.Complete();
        }

        WorldFormatConversion.ExportWorld(_plugin, P("world"), P("m.mul"), P("i.mul"), P("s.mul"));

        var tiles = MapMulCodec.ReadLandTiles(P("m.mul"), 16, 8);
        Assert.Equal(5, tiles[0].TileId);
        Assert.Equal(0x00A8, tiles[8].TileId);
        Assert.Equal(-15, tiles[8].Z);
        Assert.Equal(0x00A8, tiles[15 + 7 * 16].TileId);
    }

    [Fact]
    public void WorldFormatConversion_PluginWithoutCapability_Refused()
    {
        _plugin.Caps = WorldFormatCapabilities.Export;
        WriteFixture();
        Assert.Throws<InvalidOperationException>(() =>
            WorldFormatConversion.ImportArea(_plugin, P("world"), "w", P("map.mul"), null, null, Size, Size, 0, 0, 8, 8));
    }

    /// <summary>In-memory plugin: worlds are kept in a dictionary keyed by folder.</summary>
    private sealed class MemoryPlugin : IWorldFormatPlugin
    {
        private readonly Dictionary<string, (WorldInfo Info, Dictionary<(int, int), WorldBlock> Blocks)> _worlds = new();

        public WorldFormatCapabilities Caps { get; set; } = WorldFormatCapabilities.Import | WorldFormatCapabilities.Export;
        public int ContractVersion => WorldFormatContract.Version;
        public string Id => "memory";
        public string DisplayName => "Memory";
        public WorldFormatCapabilities Capabilities => Caps;

        public IWorldWriter CreateWorld(string worldFolder, WorldCreateOptions options)
        {
            var blocks = new Dictionary<(int, int), WorldBlock>();
            _worlds[worldFolder] = (new WorldInfo(options.WorldName, options.WidthInBlocks, options.HeightInBlocks), blocks);
            return new Writer(blocks);
        }

        public IWorldReader OpenWorld(string worldFolder) => new Reader(_worlds[worldFolder]);

        private sealed class Writer(Dictionary<(int, int), WorldBlock> blocks) : IWorldWriter
        {
            public void WriteBlock(WorldBlock block) => blocks.Add((block.BlockX, block.BlockY), block);
            public void Complete() { }
            public void Dispose() { }
        }

        private sealed class Reader((WorldInfo Info, Dictionary<(int, int), WorldBlock> Blocks) world) : IWorldReader
        {
            public WorldInfo Info => world.Info;
            public bool TryReadBlock(int blockX, int blockY, out WorldBlock? block) => world.Blocks.TryGetValue((blockX, blockY), out block);
            public void Dispose() { }
        }
    }
}
