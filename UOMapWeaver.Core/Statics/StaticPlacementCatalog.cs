using System.Globalization;
using System.Xml.Linq;

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

            foreach (var file in Directory.EnumerateFiles(root, "*.xml", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (results.ContainsKey(name))
                {
                    continue;
                }

                var definition = TryLoadStaticDefinition(file);
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
        var results = new List<TerrainDefinition>();
        if (!File.Exists(terrainPath))
        {
            return results;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Load(terrainPath);
        }
        catch
        {
            return results;
        }

        var root = doc.Root;
        if (root is null)
        {
            return results;
        }

        foreach (var element in root.Elements("Terrain"))
        {
            var name = element.Attribute("Name")?.Value?.Trim();
            var tileIdText = element.Attribute("TileID")?.Value;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(tileIdText))
            {
                continue;
            }

            if (!TryParseUShort(tileIdText, out var tileId))
            {
                continue;
            }

            results.Add(new TerrainDefinition(name, tileId));
        }

        return results;
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

    private static StaticPlacementDefinition? TryLoadStaticDefinition(string path)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(path);
        }
        catch
        {
            return null;
        }

        var root = doc.Root;
        if (root is null || !root.Name.LocalName.Equals("RandomStatics", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var chanceText = root.Attribute("Chance")?.Value;
        var chance = 0;
        if (!string.IsNullOrWhiteSpace(chanceText) && int.TryParse(chanceText, out var parsedChance))
        {
            chance = parsedChance;
        }

        var groups = new List<StaticPlacementGroup>();
        foreach (var element in root.Elements("Statics"))
        {
            var weightText = element.Attribute("Freq")?.Value;
            if (!int.TryParse(weightText, out var weight) || weight <= 0)
            {
                continue;
            }

            var items = new List<StaticPlacementItem>();
            foreach (var itemElement in element.Elements("Static"))
            {
                var tileText = itemElement.Attribute("TileID")?.Value;
                if (string.IsNullOrWhiteSpace(tileText) || !TryParseUShort(tileText, out var tileId))
                {
                    continue;
                }

                var x = ParseInt(itemElement.Attribute("X")?.Value);
                var y = ParseInt(itemElement.Attribute("Y")?.Value);
                var zValue = ParseInt(itemElement.Attribute("Z")?.Value);
                var hueValue = ParseInt(itemElement.Attribute("Hue")?.Value);
                var z = zValue < sbyte.MinValue ? sbyte.MinValue : zValue > sbyte.MaxValue ? sbyte.MaxValue : (sbyte)zValue;
                var hue = hueValue < 0
                    ? (ushort)0
                    : hueValue > ushort.MaxValue
                        ? ushort.MaxValue
                        : (ushort)hueValue;

                items.Add(new StaticPlacementItem(tileId, x, y, z, hue));
            }

            if (items.Count > 0)
            {
                groups.Add(new StaticPlacementGroup(weight, items));
            }
        }

        var name = Path.GetFileNameWithoutExtension(path);
        return new StaticPlacementDefinition(name, chance, groups);
    }

    private static bool TryParseUShort(string value, out ushort result)
    {
        result = 0;
        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        if (text.Any(c => char.IsLetter(c)))
        {
            return ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result);
        }

        return ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    private static int ParseInt(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
    }
}
