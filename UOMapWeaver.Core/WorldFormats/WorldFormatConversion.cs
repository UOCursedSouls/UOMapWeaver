using UOMapWeaver.Core.Map;
using UOMapWeaver.Core.Uop;
using UOMapWeaver.Plugins;

namespace UOMapWeaver.Core.WorldFormats;

/// <summary>Converts a block-aligned area of a UO map to a world through a plugin and back (ADR-0003 section 6).</summary>
public static class WorldFormatConversion
{
    public const ushort MissingLandTileId = 0x00A8;
    public const sbyte MissingLandZ = -15;
    public const string NoPluginMessage = "no world format plugin installed";

    /// <summary>
    /// Imports the tile rectangle (all four values multiples of 8) of a map into <paramref name="worldFolder"/>.
    /// <paramref name="mapPath"/> is a map MUL or a legacy map UOP (<paramref name="uopMapIndex"/> picks its map). Statics are optional.
    /// </summary>
    public static int ImportArea(
        IWorldFormatPlugin plugin,
        string worldFolder,
        string worldName,
        string mapPath,
        string? staIdxPath,
        string? staticsPath,
        int mapWidth,
        int mapHeight,
        int x,
        int y,
        int width,
        int height,
        int uopMapIndex = 0,
        IProgress<int>? progress = null)
    {
        RequireCapability(plugin, WorldFormatCapabilities.Import);
        const int s = WorldBlock.Size;
        if (x % s != 0 || y % s != 0 || width % s != 0 || height % s != 0)
        {
            throw new ArgumentException($"The area must be aligned to the {s}-tile block grid (x, y, width and height multiples of {s}).");
        }

        if (mapWidth % s != 0 || mapHeight % s != 0 || x < 0 || y < 0 || width <= 0 || height <= 0 || x + width > mapWidth || y + height > mapHeight)
        {
            throw new ArgumentException("The area is outside the map.");
        }

        string? tempFolder = null;
        try
        {
            if (string.Equals(Path.GetExtension(mapPath), ".uop", StringComparison.OrdinalIgnoreCase))
            {
                tempFolder = Path.Combine(Path.GetTempPath(), "uomw-uop-" + Guid.NewGuid().ToString("N"));
                UopCodec.ExtractLegacyUopToMul(mapPath, tempFolder, "", LegacyUopType.Map, uopMapIndex);
                mapPath = Path.Combine(tempFolder, $"map{uopMapIndex}.mul");
            }

            var land = MapMulCodec.ReadLandTilesRegion(mapPath, mapWidth, mapHeight, x, y, width, height);
            // shortcut: reads the statics of the whole map, fine up to the largest UO maps, switch to per-block reads if memory hurts.
            var statics = !string.IsNullOrWhiteSpace(staIdxPath) && !string.IsNullOrWhiteSpace(staticsPath)
                ? StaticMulCodec.ReadStatics(staIdxPath, staticsPath, mapWidth, mapHeight)
                : null;

            int bw = width / s, bh = height / s, firstBx = x / s, firstBy = y / s, mapBlockH = mapHeight / s;
            Directory.CreateDirectory(worldFolder);
            using var writer = plugin.CreateWorld(worldFolder, new WorldCreateOptions(worldName, bw, bh));
            for (var bx = 0; bx < bw; bx++)
            {
                for (var by = 0; by < bh; by++)
                {
                    var tiles = new WorldLandTile[s * s];
                    for (var i = 0; i < tiles.Length; i++)
                    {
                        var t = land[(by * s + (i >> 3)) * width + bx * s + (i & 7)];
                        tiles[i] = new WorldLandTile(t.TileId, t.Z);
                    }

                    var list = statics?[(firstBx + bx) * mapBlockH + firstBy + by];
                    var blockStatics = list is null
                        ? Array.Empty<WorldStaticTile>()
                        : list.Select(e => new WorldStaticTile(e.TileId, e.X, e.Y, e.Z, e.Hue)).ToArray();
                    writer.WriteBlock(new WorldBlock(bx, by, tiles, blockStatics));
                }

                progress?.Report((bx + 1) * 100 / bw);
            }

            writer.Complete();
            return bw * bh;
        }
        finally
        {
            if (tempFolder is not null && Directory.Exists(tempFolder))
            {
                Directory.Delete(tempFolder, true);
            }
        }
    }

    /// <summary>
    /// Exports a world to map, staidx and statics MUL. A bounded world exports whole; for an unbounded one
    /// (0 x 0) <paramref name="areaBlocks"/> gives the rectangle (block x, y, width, height). Missing blocks become water.
    /// Returns the size written, in tiles.
    /// </summary>
    public static (int Width, int Height) ExportWorld(
        IWorldFormatPlugin plugin,
        string worldFolder,
        string mapPath,
        string staIdxPath,
        string staticsPath,
        (int X, int Y, int Width, int Height)? areaBlocks = null,
        IProgress<int>? progress = null)
    {
        RequireCapability(plugin, WorldFormatCapabilities.Export);
        using var reader = plugin.OpenWorld(worldFolder);
        var info = reader.Info;
        var (ox, oy, bw, bh) = areaBlocks ?? (0, 0, info.WidthInBlocks, info.HeightInBlocks);
        if (bw <= 0 || bh <= 0 || ox < 0 || oy < 0)
        {
            throw new ArgumentException(info.WidthInBlocks == 0
                ? "The world is unbounded: give the block rectangle to export."
                : "Invalid export area.");
        }

        const int s = WorldBlock.Size;
        int width = bw * s, height = bh * s;
        var land = new LandTile[width * height];
        var statics = new List<StaticMulEntry>[bw * bh];
        for (var bx = 0; bx < bw; bx++)
        {
            for (var by = 0; by < bh; by++)
            {
                var found = reader.TryReadBlock(ox + bx, oy + by, out var block) && block is not null;
                for (var i = 0; i < s * s; i++)
                {
                    land[(by * s + (i >> 3)) * width + bx * s + (i & 7)] = found
                        ? new LandTile(block!.Land[i].TileId, block.Land[i].Z)
                        : new LandTile(MissingLandTileId, MissingLandZ);
                }

                if (found && block!.Statics.Count > 0)
                {
                    statics[bx * bh + by] = block.Statics.Select(t => new StaticMulEntry(t.TileId, t.X, t.Y, t.Z, t.Hue)).ToList();
                }
            }

            progress?.Report((bx + 1) * 100 / bw);
        }

        MapMulCodec.WriteLandTiles(mapPath, width, height, land);
        StaticMulCodec.WriteStatics(staIdxPath, staticsPath, width, height, statics);
        return (width, height);
    }

    private static void RequireCapability(IWorldFormatPlugin plugin, WorldFormatCapabilities needed)
    {
        if ((plugin.Capabilities & needed) != needed)
        {
            throw new InvalidOperationException($"Plugin '{plugin.DisplayName}' cannot {needed.ToString().ToLowerInvariant()}.");
        }
    }
}
