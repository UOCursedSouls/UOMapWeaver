using UOMapWeaver.Core.Map;

namespace UOMapWeaver.Core.Statics;

public static class StaticPlacementGenerator
{
    public static List<StaticMulEntry>[] Generate(
        LandTile[] tiles,
        int width,
        int height,
        StaticPlacementOptions options,
        IProgress<int>? progress = null,
        Action<MapConversionLogEntry>? log = null,
        CancellationToken? cancellationToken = null)
    {
        var terrainPath = Path.Combine(UOMapWeaverDataPaths.SystemRoot, "Terrain.xml");
        var terrainDefs = StaticPlacementCatalog.LoadTerrainDefinitions(terrainPath);
        if (terrainDefs.Count == 0)
        {
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Warning,
                $"Terrain definitions not found at {terrainPath}."));
            return CreateEmptyBlocks(width, height);
        }

        var placementDefs = StaticPlacementCatalog.LoadStaticDefinitions(new[]
        {
            UOMapWeaverDataPaths.StaticsRoot,
            Path.Combine(UOMapWeaverDataPaths.SystemRoot, "TerrainTypes")
        });

        if (placementDefs.Count == 0)
        {
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Warning,
                "Static placement definitions not found."));
            return CreateEmptyBlocks(width, height);
        }

        var tileLookup = StaticPlacementCatalog.BuildTileIdLookup(terrainDefs, placementDefs);
        if (tileLookup.Count == 0)
        {
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Warning,
                "No terrain tiles matched static definitions."));
            return CreateEmptyBlocks(width, height);
        }

        var blockWidth = width / MapMul.BlockSize;
        var blockHeight = height / MapMul.BlockSize;
        var blocks = new List<StaticMulEntry>[blockWidth * blockHeight];

        var lastProgress = -1;
        var placedCount = 0;
        var skippedChance = 0;
        var skippedMissing = 0;

        for (var y = 0; y < height; y++)
        {
            cancellationToken?.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var tile = tiles[y * width + x];
                if (!tileLookup.TryGetValue(tile.TileId, out var definition))
                {
                    skippedMissing++;
                    continue;
                }

                if (!TryShouldPlace(definition, x, y, tile.TileId, options.Seed, out var selector))
                {
                    skippedChance++;
                    continue;
                }

                var group = SelectGroup(definition.Groups, selector);
                if (group is null)
                {
                    continue;
                }

                foreach (var item in group.Items)
                {
                    var worldX = x + item.X;
                    var worldY = y + item.Y;
                    if (worldX < 0 || worldX >= width || worldY < 0 || worldY >= height)
                    {
                        continue;
                    }

                    var blockX = worldX / MapMul.BlockSize;
                    var blockY = worldY / MapMul.BlockSize;
                    var blockIndex = blockY * blockWidth + blockX;
                    var list = blocks[blockIndex] ??= new List<StaticMulEntry>();

                    var localX = (byte)(worldX % MapMul.BlockSize);
                    var localY = (byte)(worldY % MapMul.BlockSize);
                    var z = ClampZ(tile.Z + item.Z);

                    list.Add(new StaticMulEntry(item.TileId, localX, localY, z, item.Hue));
                    placedCount++;
                }
            }

            if (progress != null)
            {
                var percent = (int)((y + 1) * 100.0 / height);
                if (percent != lastProgress)
                {
                    lastProgress = percent;
                    progress.Report(percent);
                }
            }
        }

        log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Info,
            $"Statics placed: {placedCount:N0}, skipped by chance: {skippedChance:N0}, missing definitions: {skippedMissing:N0}."));

        return blocks;
    }

    private static List<StaticMulEntry>[] CreateEmptyBlocks(int width, int height)
    {
        var blockWidth = width / MapMul.BlockSize;
        var blockHeight = height / MapMul.BlockSize;
        return new List<StaticMulEntry>[blockWidth * blockHeight];
    }

    private static bool TryShouldPlace(
        StaticPlacementDefinition definition,
        int x,
        int y,
        ushort tileId,
        int seed,
        out uint selector)
    {
        selector = Hash(x, y, tileId, seed);
        var chance = definition.Chance;
        if (chance <= 0)
        {
            return false;
        }

        if (chance > 100)
        {
            chance = 100;
        }

        return selector % 100 < chance;
    }

    private static StaticPlacementGroup? SelectGroup(IReadOnlyList<StaticPlacementGroup> groups, uint selector)
    {
        if (groups.Count == 0)
        {
            return null;
        }

        var totalWeight = 0;
        foreach (var group in groups)
        {
            totalWeight += group.Weight;
        }

        if (totalWeight <= 0)
        {
            return null;
        }

        var roll = (int)((selector >> 8) % (uint)totalWeight);
        foreach (var group in groups)
        {
            if (roll < group.Weight)
            {
                return group;
            }

            roll -= group.Weight;
        }

        return groups[0];
    }

    private static sbyte ClampZ(int z)
    {
        if (z < sbyte.MinValue)
        {
            return sbyte.MinValue;
        }

        return z > sbyte.MaxValue ? sbyte.MaxValue : (sbyte)z;
    }

    private static uint Hash(int x, int y, ushort tileId, int seed)
    {
        unchecked
        {
            var hash = (uint)(x * 73856093 ^ y * 19349663 ^ tileId * 83492791 ^ seed);
            hash ^= hash >> 13;
            hash *= 0x5bd1e995;
            hash ^= hash >> 15;
            return hash;
        }
    }
}

public sealed class StaticPlacementOptions
{
    public int Seed { get; set; }
}
