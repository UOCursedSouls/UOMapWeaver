namespace UOMapWeaver.Core.Statics;

public static class StaticPlacementCatalog
{
    public static Dictionary<string, StaticPlacementDefinition> LoadStaticDefinitions(IEnumerable<string> roots)
    {
        var results = new Dictionary<string, StaticPlacementDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (results.ContainsKey(name))
                {
                    continue;
                }

                var definition = StaticPlacementJson.LoadStaticDefinition(file);
                if (definition != null)
                {
                    results[name] = definition;
                }
            }
        }

        return results;
    }

    public static List<TerrainDefinition> LoadTerrainDefinitions(string terrainPath)
    {
        return StaticPlacementJson.LoadTerrainDefinitions(terrainPath);
    }

    public static Dictionary<ushort, StaticPlacementDefinition> BuildTileIdLookup(
        IEnumerable<TerrainDefinition> terrains,
        IReadOnlyDictionary<string, StaticPlacementDefinition> placements)
    {
        var results = new Dictionary<ushort, StaticPlacementDefinition>();
        foreach (var terrain in terrains)
        {
            if (terrain.Name.Contains("Without Static", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!placements.TryGetValue(terrain.Name, out var definition))
            {
                continue;
            }

            results.TryAdd(terrain.TileId, definition);
        }

        return results;
    }

}
