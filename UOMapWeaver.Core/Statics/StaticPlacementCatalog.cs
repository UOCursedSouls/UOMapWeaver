namespace UOMapWeaver.Core.Statics;

public static class StaticPlacementCatalog
{
    public static Dictionary<string, StaticPlacementDefinition> LoadStaticDefinitions(IEnumerable<string> roots)
    {
        return LoadStaticDefinitions(roots, out _);
    }

    public static Dictionary<string, StaticPlacementDefinition> LoadStaticDefinitions(
        IEnumerable<string> roots,
        out StaticPlacementSourceInfo info)
    {
        var results = new Dictionary<string, StaticPlacementDefinition>(StringComparer.OrdinalIgnoreCase);
        var loadedXml = 0;

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (results.ContainsKey(name))
                {
                    continue;
                }

                var definition = StaticPlacementXmlImporter.LoadStaticDefinitionFromXml(file);
                if (definition != null)
                {
                    results[name] = definition;
                    loadedXml++;
                }
            }
        }

        info = new StaticPlacementSourceInfo(0, loadedXml, null);
        return results;
    }

    public static List<TerrainDefinition> LoadTerrainDefinitions(string terrainPath)
    {
        return LoadTerrainDefinitions(terrainPath, out _);
    }

    public static List<TerrainDefinition> LoadTerrainDefinitions(string terrainPath, out string? sourcePath)
    {
        sourcePath = null;

        var folder = Path.GetDirectoryName(terrainPath) ?? string.Empty;
        var xmlCandidates = new[]
        {
            Path.Combine(folder, "terrain-definitions.xml"),
            Path.Combine(folder, "Terrain.xml")
        };

        foreach (var xmlPath in xmlCandidates)
        {
            if (!File.Exists(xmlPath))
            {
                continue;
            }

            var records = StaticPlacementXmlImporter.LoadTerrainRecordsFromXml(xmlPath);
            sourcePath = xmlPath;
            return records
                .Where(record => !string.IsNullOrWhiteSpace(record.Name))
                .Select(record => new TerrainDefinition(record.Name!.Trim(), record.TileId, record.Random == true))
                .ToList();
        }

        return new List<TerrainDefinition>();
    }

    public static Dictionary<ushort, StaticPlacementDefinition> BuildTileIdLookup(
        IEnumerable<TerrainDefinition> terrains,
        IReadOnlyDictionary<string, StaticPlacementDefinition> placements)
    {
        var results = new Dictionary<ushort, StaticPlacementDefinition>();
        var normalizedLookup = BuildNormalizedLookup(placements);
        foreach (var terrain in terrains)
        {
            if (terrain.Name.Contains("Without Static", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryResolveDefinition(terrain.Name, placements, normalizedLookup, out var definition))
            {
                continue;
            }

            AddTileId(results, terrain.TileId, definition);
            if (terrain.Random)
            {
                for (var i = 1; i <= 3; i++)
                {
                    AddTileId(results, (ushort)(terrain.TileId + i), definition);
                }
            }
        }

        return results;
    }

    internal static Dictionary<string, StaticPlacementDefinition> BuildNormalizedLookupForOverrides(
        IReadOnlyDictionary<string, StaticPlacementDefinition> placements)
    {
        return BuildNormalizedLookup(placements);
    }

    internal static bool TryResolveDefinitionName(
        string name,
        IReadOnlyDictionary<string, StaticPlacementDefinition> placements,
        IReadOnlyDictionary<string, StaticPlacementDefinition> normalizedLookup,
        out StaticPlacementDefinition definition)
    {
        return TryResolveDefinition(name, placements, normalizedLookup, out definition);
    }

    private static void AddTileId(
        Dictionary<ushort, StaticPlacementDefinition> results,
        ushort tileId,
        StaticPlacementDefinition definition)
    {
        if (!results.ContainsKey(tileId))
        {
            results[tileId] = definition;
        }
    }

    private static Dictionary<string, StaticPlacementDefinition> BuildNormalizedLookup(
        IReadOnlyDictionary<string, StaticPlacementDefinition> placements)
    {
        var normalized = new Dictionary<string, StaticPlacementDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in placements)
        {
            var key = NormalizeName(kvp.Key);
            if (!normalized.ContainsKey(key))
            {
                normalized[key] = kvp.Value;
            }
        }

        return normalized;
    }

    private static bool TryResolveDefinition(
        string terrainName,
        IReadOnlyDictionary<string, StaticPlacementDefinition> placements,
        IReadOnlyDictionary<string, StaticPlacementDefinition> normalizedLookup,
        out StaticPlacementDefinition definition)
    {
        definition = default!;
        if (placements.TryGetValue(terrainName, out var direct) && direct is not null)
        {
            definition = direct;
            return true;
        }

        var normalized = NormalizeName(terrainName);
        if (normalizedLookup.TryGetValue(normalized, out var normalizedMatch) && normalizedMatch is not null)
        {
            definition = normalizedMatch;
            return true;
        }

        var fallback = NormalizeByKeyword(normalized);
        if (!string.IsNullOrWhiteSpace(fallback) &&
            normalizedLookup.TryGetValue(fallback, out var fallbackMatch) &&
            fallbackMatch is not null)
        {
            definition = fallbackMatch;
            return true;
        }

        return false;
    }

    internal static bool TryResolveDefinitionForReport(
        string terrainName,
        IReadOnlyDictionary<string, StaticPlacementDefinition> placements)
    {
        if (placements.Count == 0)
        {
            return false;
        }

        var normalizedLookup = BuildNormalizedLookup(placements);
        return TryResolveDefinition(terrainName, placements, normalizedLookup, out _);
    }

    private static string NormalizeName(string name)
    {
        var normalized = name.Trim();
        normalized = normalized.Replace("  ", " ");
        normalized = normalized.Replace(" Without Static", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(" without Static", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(" Embankment", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(" (Dark)", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(" (NS)", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(" (EW)", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("Rough ", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("High ", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("Low ", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("Dark ", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("Light ", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("  ", " ");
        return normalized.Trim();
    }

    private static string NormalizeByKeyword(string normalized)
    {
        var lower = normalized.ToLowerInvariant();
        if (lower.Contains("water"))
        {
            return string.Empty;
        }

        if (lower.Contains("grass"))
        {
            return "Grass";
        }

        if (lower.Contains("forest"))
        {
            return "Forest";
        }

        if (lower.Contains("snow"))
        {
            return "Snow";
        }

        if (lower.Contains("sand"))
        {
            return "Sand";
        }

        if (lower.Contains("beach"))
        {
            return "Beach";
        }

        if (lower.Contains("jungle"))
        {
            return "Jungle";
        }

        if (lower.Contains("swamp"))
        {
            return "Swamp";
        }

        if (lower.Contains("furrow"))
        {
            return "Furrows";
        }

        return string.Empty;
    }
}

public readonly record struct StaticPlacementSourceInfo(int JsonCount, int XmlCount, string? TerrainXmlPath);
