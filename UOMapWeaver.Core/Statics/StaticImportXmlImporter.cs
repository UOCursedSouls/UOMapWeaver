using System.Globalization;
using System.Xml.Linq;

namespace UOMapWeaver.Core.Statics;

public static class StaticImportXmlImporter
{
    public static List<StaticImportEntry> LoadStaticTilesFromXml(string path)
    {
        var results = new List<StaticImportEntry>();
        XDocument doc;
        try
        {
            doc = XDocument.Load(path);
        }
        catch
        {
            return results;
        }

        var root = doc.Root;
        if (root is null || !root.Name.LocalName.Equals("Static_Tiles", StringComparison.OrdinalIgnoreCase))
        {
            return results;
        }

        foreach (var element in root.Elements("Tile"))
        {
            var tileText = element.Attribute("TileID")?.Value ?? element.Attribute("ID")?.Value;
            if (string.IsNullOrWhiteSpace(tileText) || !TryParseUShort(tileText, out var tileId))
            {
                continue;
            }

            var x = ParseInt(element.Attribute("X")?.Value);
            var y = ParseInt(element.Attribute("Y")?.Value);
            var zValue = ParseInt(element.Attribute("Z")?.Value);
            var hueValue = ParseInt(element.Attribute("Hue")?.Value);

            var z = zValue < sbyte.MinValue ? sbyte.MinValue : zValue > sbyte.MaxValue ? sbyte.MaxValue : (sbyte)zValue;
            var hue = hueValue < 0
                ? (ushort)0
                : hueValue > ushort.MaxValue
                    ? ushort.MaxValue
                    : (ushort)hueValue;

            results.Add(new StaticImportEntry(tileId, x, y, z, hue));
        }

        return results;
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
