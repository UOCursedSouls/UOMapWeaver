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
        var terrainPath = UOMapWeaverDataPaths.TerrainDefinitionsPath;
        var terrainDefs = StaticPlacementCatalog.LoadTerrainDefinitions(terrainPath, out var terrainSource);
        if (terrainDefs.Count == 0)
        {
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Warning,
                $"Terrain definitions not found at {terrainPath}."));
            return CreateEmptyBlocks(width, height);
        }

        if (!string.IsNullOrWhiteSpace(terrainSource))
        {
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Warning,
                $"Terrain definitions loaded from XML: {Path.GetFileName(terrainSource)}."));
        }

        var placementDefs = StaticPlacementCatalog.LoadStaticDefinitions(new[]
        {
            UOMapWeaverDataPaths.TerrainTypesRoot,
            UOMapWeaverDataPaths.StaticsRoot
        }, out var sourceInfo);

        if (placementDefs.Count == 0)
        {
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Warning,
                "Static placement definitions not found."));
            return CreateEmptyBlocks(width, height);
        }

        if (sourceInfo.XmlCount > 0)
        {
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Warning,
                $"Statics loaded from XML: {sourceInfo.XmlCount:N0} file(s)."));
        }
        else if (sourceInfo.JsonCount > 0)
        {
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Info,
                $"Statics loaded from JSON: {sourceInfo.JsonCount:N0} file(s)."));
        }

        var normalizedLookup = StaticPlacementCatalog.BuildNormalizedLookupForOverrides(placementDefs);

        var disabledDefs = placementDefs
            .Where(kvp => kvp.Value.Chance <= 0 || kvp.Value.Groups.Count == 0)
            .Select(kvp => kvp.Key)
            .OrderBy(name => name)
            .ToList();

        if (disabledDefs.Count > 0)
        {
            var sample = disabledDefs.Take(10).ToList();
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Warning,
                $"Static definitions disabled (Chance=0 or empty): {string.Join(", ", sample)}{(disabledDefs.Count > sample.Count ? ", ..." : string.Empty)}"));
        }

        var tileLookup = StaticPlacementCatalog.BuildTileIdLookup(terrainDefs, placementDefs);
        if (tileLookup.Count == 0)
        {
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Warning,
                "No terrain tiles matched static definitions."));
            return CreateEmptyBlocks(width, height);
        }
        else
        {
            var totalTerrains = terrainDefs.Count;
            var matched = tileLookup.Count;
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Info,
                $"Static terrain matches: {matched:N0}/{totalTerrains:N0}."));

            if (matched < totalTerrains)
            {
                var missing = new List<string>();
                foreach (var terrain in terrainDefs)
                {
                    if (!placementDefs.ContainsKey(terrain.Name))
                    {
                        missing.Add(terrain.Name);
                        if (missing.Count >= 10)
                        {
                            break;
                        }
                    }
                }

                if (missing.Count > 0)
                {
                    log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Warning,
                        $"Missing static definitions for: {string.Join(", ", missing)}{(matched + missing.Count < totalTerrains ? ", ..." : string.Empty)}"));
                }
            }
        }

        var blockWidth = width / MapMul.BlockSize;
        var blockHeight = height / MapMul.BlockSize;
        var blocks = new List<StaticMulEntry>[blockWidth * blockHeight];
        var layout = options.Layout;

        var lastProgress = -1;
        var placedCount = 0;
        var skippedChance = 0;
        var skippedMissing = 0;
        MissingTerrainReport? missingReport = null;
        Dictionary<ushort, string>? terrainNameByTile = null;
        HashSet<string>? missingDefinitionNames = null;

        if (options.OverrideEnabled)
        {
            terrainNameByTile = new Dictionary<ushort, string>();
            foreach (var terrain in terrainDefs)
            {
                if (!terrainNameByTile.ContainsKey(terrain.TileId))
                {
                    terrainNameByTile[terrain.TileId] = terrain.Name;
                }
            }
        }

        if (options.WriteMissingTerrainReport && !string.IsNullOrWhiteSpace(options.MissingTerrainReportPath))
        {
            missingReport = new MissingTerrainReport(options.MissingTerrainReportTop);
            if (terrainNameByTile is null)
            {
                terrainNameByTile = new Dictionary<ushort, string>();
                foreach (var terrain in terrainDefs)
                {
                    if (!terrainNameByTile.ContainsKey(terrain.TileId))
                    {
                        terrainNameByTile[terrain.TileId] = terrain.Name;
                    }
                }
            }

            missingDefinitionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var terrain in terrainDefs)
            {
                if (options.OverrideEnabled &&
                    options.OverrideDefinitions.TryGetValue(terrain.Name, out var overrideName) &&
                    !string.IsNullOrWhiteSpace(overrideName) &&
                    StaticPlacementCatalog.TryResolveDefinitionName(overrideName, placementDefs, normalizedLookup, out _))
                {
                    continue;
                }

                if (!StaticPlacementCatalog.TryResolveDefinitionForReport(terrain.Name, placementDefs) &&
                    !terrain.Name.Contains("Without Static", StringComparison.OrdinalIgnoreCase))
                {
                    missingDefinitionNames.Add(terrain.Name);
                }
            }
        }

        for (var y = 0; y < height; y++)
        {
            cancellationToken?.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var tile = tiles[y * width + x];
                if (missingReport != null && terrainNameByTile != null && missingDefinitionNames != null)
                {
                    missingReport.TotalTiles++;
                    if (!terrainNameByTile.TryGetValue(tile.TileId, out var terrainName) ||
                        string.IsNullOrWhiteSpace(terrainName))
                    {
                        missingReport.AddUnknown(tile.TileId);
                    }
                    else if (missingDefinitionNames.Contains(terrainName))
                    {
                        missingReport.AddMissing(terrainName, tile.TileId);
                    }
                }

                if (!TryResolveDefinition(tileLookup, placementDefs, normalizedLookup, options, tile.TileId, terrainNameByTile, out var definition))
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
                    var blockIndex = StaticsLayoutHelper.GetBlockIndex(blockX, blockY, blockWidth, blockHeight, layout);
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

        if (missingReport != null && !string.IsNullOrWhiteSpace(options.MissingTerrainReportPath))
        {
            missingReport.Write(options.MissingTerrainReportPath);
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Warning,
                $"Missing terrain statics report saved: {options.MissingTerrainReportPath}"));
        }

        return blocks;
    }

    private static List<StaticMulEntry>[] CreateEmptyBlocks(int width, int height)
    {
        var blockWidth = width / MapMul.BlockSize;
        var blockHeight = height / MapMul.BlockSize;
        return new List<StaticMulEntry>[blockWidth * blockHeight];
    }

    private static bool TryResolveDefinition(
        Dictionary<ushort, StaticPlacementDefinition> baseLookup,
        IReadOnlyDictionary<string, StaticPlacementDefinition> placementDefs,
        IReadOnlyDictionary<string, StaticPlacementDefinition> normalizedLookup,
        StaticPlacementOptions options,
        ushort tileId,
        Dictionary<ushort, string>? terrainNameByTile,
        out StaticPlacementDefinition definition)
    {
        definition = default!;
        if (!options.OverrideEnabled ||
            options.OverrideDefinitions.Count == 0 ||
            terrainNameByTile is null ||
            !terrainNameByTile.TryGetValue(tileId, out var terrainName))
        {
            if (!baseLookup.TryGetValue(tileId, out var baseDef))
            {
                return false;
            }

            definition = ApplyChanceOverride(baseDef, terrainNameByTile, tileId, options);
            return true;
        }

        if (options.OverrideDefinitions.TryGetValue(terrainName, out var overrideName) &&
            !string.IsNullOrWhiteSpace(overrideName))
        {
            if (!StaticPlacementCatalog.TryResolveDefinitionName(overrideName, placementDefs, normalizedLookup, out var overrideDef))
            {
                return false;
            }

            definition = ApplyChanceOverride(overrideDef, terrainNameByTile, tileId, options);
            return true;
        }

        if (!baseLookup.TryGetValue(tileId, out var fallback))
        {
            return false;
        }

        definition = ApplyChanceOverride(fallback, terrainNameByTile, tileId, options);
        return true;
    }

    private static StaticPlacementDefinition ApplyChanceOverride(
        StaticPlacementDefinition definition,
        Dictionary<ushort, string>? terrainNameByTile,
        ushort tileId,
        StaticPlacementOptions options)
    {
        if (!options.OverrideEnabled || options.OverrideChances.Count == 0 || terrainNameByTile is null)
        {
            return definition;
        }

        if (terrainNameByTile.TryGetValue(tileId, out var terrainName) &&
            options.OverrideChances.TryGetValue(terrainName, out var chance))
        {
            return new StaticPlacementDefinition(definition.Name, chance, definition.Groups);
        }

        return definition;
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
    public StaticsLayout Layout { get; set; } = StaticsLayout.RowMajor;
    public bool WriteMissingTerrainReport { get; set; }
    public string? MissingTerrainReportPath { get; set; }
    public int MissingTerrainReportTop { get; set; } = 20;
    public bool OverrideEnabled { get; set; }
    public Dictionary<string, string> OverrideDefinitions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> OverrideChances { get; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class MissingTerrainReport
{
    private readonly int _topCount;
    private readonly Dictionary<string, Dictionary<ushort, int>> _byTerrain = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ushort, int> _unknown = new();

    public MissingTerrainReport(int topCount)
    {
        _topCount = topCount;
    }

    public long TotalTiles { get; set; }

    public void AddMissing(string terrainName, ushort tileId)
    {
        if (!_byTerrain.TryGetValue(terrainName, out var map))
        {
            map = new Dictionary<ushort, int>();
            _byTerrain[terrainName] = map;
        }

        map[tileId] = map.TryGetValue(tileId, out var count) ? count + 1 : 1;
    }

    public void AddUnknown(ushort tileId)
    {
        _unknown[tileId] = _unknown.TryGetValue(tileId, out var count) ? count + 1 : 1;
    }

    public void Write(string path)
    {
        var lines = new List<string>
        {
            "Missing terrain statics report",
            $"Total tiles scanned: {TotalTiles:N0}",
            string.Empty,
            "Top missing terrain groups:"
        };

        foreach (var terrain in _byTerrain.OrderByDescending(x => x.Value.Values.Sum()).Take(_topCount))
        {
            var total = terrain.Value.Values.Sum();
            lines.Add($"  {terrain.Key}: {total:N0}");
            var topTiles = terrain.Value.OrderByDescending(x => x.Value).Take(_topCount)
                .Select(x => $"0x{x.Key:X4}:{x.Value}");
            lines.Add($"    Tiles: {string.Join(", ", topTiles)}");
        }

        if (_unknown.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Unknown terrain tiles:");
            var topUnknown = _unknown.OrderByDescending(x => x.Value).Take(_topCount)
                .Select(x => $"0x{x.Key:X4}:{x.Value}");
            lines.Add($"  Tiles: {string.Join(", ", topUnknown)}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllLines(path, lines);
    }
}
