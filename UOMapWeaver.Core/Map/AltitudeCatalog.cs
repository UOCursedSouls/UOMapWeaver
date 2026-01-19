using System.Xml.Linq;

namespace UOMapWeaver.Core.Map;

internal sealed class AltitudeCatalog
{
    public AltitudeCatalog(Dictionary<int, sbyte> altitudeByColor)
    {
        AltitudeByColor = altitudeByColor;
    }

    public Dictionary<int, sbyte> AltitudeByColor { get; }

    public static AltitudeCatalog Load(string altitudeXmlPath, Action<MapConversionLogEntry>? log)
    {
        var map = new Dictionary<int, sbyte>();
        if (!File.Exists(altitudeXmlPath))
        {
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Warning,
                $"Altitude.xml not found: {altitudeXmlPath}"));
            return new AltitudeCatalog(map);
        }

        try
        {
            var doc = XDocument.Load(altitudeXmlPath);
            foreach (var node in doc.Descendants("Altitude"))
            {
                var r = ReadInt(node, "R");
                var g = ReadInt(node, "G");
                var b = ReadInt(node, "B");
                var altitude = ReadInt(node, "Altitude");
                var colorKey = (r << 16) | (g << 8) | b;
                map[colorKey] = (sbyte)Math.Clamp(altitude, sbyte.MinValue, sbyte.MaxValue);
            }

            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Info,
                $"Altitude definitions loaded: {map.Count:N0}."));
        }
        catch (Exception ex)
        {
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Warning,
                $"Altitude.xml parse failed: {ex.Message}"));
        }

        return new AltitudeCatalog(map);
    }

    private static int ReadInt(XElement node, string name)
        => int.TryParse(node.Attribute(name)?.Value, out var value) ? value : 0;
}
