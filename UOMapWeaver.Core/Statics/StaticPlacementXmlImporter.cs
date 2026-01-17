using System.Globalization;
using System.Xml.Linq;

namespace UOMapWeaver.Core.Statics;

public static class StaticPlacementXmlImporter
{
    public static List<TerrainDefinitionRecord> LoadTerrainRecordsFromXml(string terrainPath)
    {
        var results = new List<TerrainDefinitionRecord>();
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

            results.Add(new TerrainDefinitionRecord(
                name,
                tileId,
                ParseOptionalInt(element.Attribute("ID")?.Value),
                ParseOptionalByte(element.Attribute("R")?.Value),
                ParseOptionalByte(element.Attribute("G")?.Value),
                ParseOptionalByte(element.Attribute("B")?.Value),
                ParseOptionalInt(element.Attribute("Base")?.Value),
                ParseOptionalBool(element.Attribute("Random")?.Value)
            ));
        }

        return results;
    }

    public static StaticPlacementDefinition? LoadStaticDefinitionFromXml(string path)
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

    private static int? ParseOptionalInt(string? value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        return null;
    }

    private static byte? ParseOptionalByte(string? value)
    {
        if (byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result))
        {
            return result;
        }

        return null;
    }

    private static bool? ParseOptionalBool(string? value)
    {
        if (bool.TryParse(value, out var result))
        {
            return result;
        }

        return null;
    }
}
