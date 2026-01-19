using System.Globalization;
using System.Xml.Linq;
using UOMapWeaver.Core.Bmp;
using UOMapWeaver.Core.Statics;

namespace UOMapWeaver.Core.MapTrans;

public static class MapTransXmlParser
{
    public static MapTransProfile LoadFromFiles(string mapTransXmlPath, string terrainXmlPath)
    {
        var terrainRecords = StaticPlacementXmlImporter.LoadTerrainRecordsFromXml(terrainXmlPath);
        var terrainLookup = new Dictionary<int, ushort>();
        foreach (var record in terrainRecords)
        {
            if (record.Id is null)
            {
                continue;
            }

            terrainLookup.TryAdd(record.Id.Value, record.TileId);
        }

        var entries = new List<MapTransEntry>();

        XDocument doc;
        try
        {
            doc = XDocument.Load(mapTransXmlPath);
        }
        catch
        {
            return new MapTransProfile(Path.GetFileNameWithoutExtension(mapTransXmlPath), entries, null);
        }

        var root = doc.Root;
        if (root is null)
        {
            return new MapTransProfile(Path.GetFileNameWithoutExtension(mapTransXmlPath), entries, null);
        }

        foreach (var element in root.Elements("HexCode"))
        {
            var groupText = element.Attribute("GroupID")?.Value;
            var altText = element.Attribute("Alt")?.Value;
            var terrainText = element.Attribute("Terrain")?.Value;
            if (string.IsNullOrWhiteSpace(groupText) || string.IsNullOrWhiteSpace(terrainText))
            {
                continue;
            }

            if (!TryParseHexByte(groupText, out var groupId))
            {
                continue;
            }

            if (!int.TryParse(terrainText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var terrainId))
            {
                continue;
            }

            if (!terrainLookup.TryGetValue(terrainId, out var tileId))
            {
                continue;
            }

            var altitude = 0;
            if (!string.IsNullOrWhiteSpace(altText))
            {
                int.TryParse(altText, NumberStyles.Integer, CultureInfo.InvariantCulture, out altitude);
            }

            entries.Add(new MapTransEntry(groupId, altitude, new[] { tileId }, groupId));
        }

        return new MapTransProfile(Path.GetFileNameWithoutExtension(mapTransXmlPath), entries, null);
    }

    public static MapTransProfile MergeMissingTiles(MapTransProfile baseProfile, MapTransProfile xmlProfile,
        out int addedTiles, out int newEntries)
    {
        addedTiles = 0;
        newEntries = 0;

        var baseTileIds = new HashSet<ushort>();
        foreach (var entry in baseProfile.Entries)
        {
            foreach (var tileId in entry.TileIds)
            {
                baseTileIds.Add(tileId);
            }
        }

        var buckets = new Dictionary<(byte colorIndex, int altitude, byte? group), List<ushort>>();
        foreach (var entry in baseProfile.Entries)
        {
            var key = (entry.ColorIndex, entry.Altitude, entry.Group);
            if (!buckets.TryGetValue(key, out var list))
            {
                list = new List<ushort>();
                buckets[key] = list;
            }
            list.AddRange(entry.TileIds);
        }

        foreach (var entry in xmlProfile.Entries)
        {
            foreach (var tileId in entry.TileIds)
            {
                if (baseTileIds.Contains(tileId))
                {
                    continue;
                }

                var key = (entry.ColorIndex, entry.Altitude, entry.Group);
                if (!buckets.TryGetValue(key, out var list))
                {
                    list = new List<ushort>();
                    buckets[key] = list;
                    newEntries++;
                }

                list.Add(tileId);
                baseTileIds.Add(tileId);
                addedTiles++;
            }
        }

        var mergedEntries = new List<MapTransEntry>(buckets.Count);
        foreach (var pair in buckets)
        {
            mergedEntries.Add(new MapTransEntry(pair.Key.colorIndex, pair.Key.altitude, pair.Value, pair.Key.group));
        }

        return new MapTransProfile(baseProfile.Name, mergedEntries, baseProfile.PalettePath);
    }

    public static MapTransProfile MergeMissingTilesFromTerrainColors(
        MapTransProfile baseProfile,
        string terrainXmlPath,
        out int addedTiles
    )
    {
        addedTiles = 0;

        if (string.IsNullOrWhiteSpace(baseProfile.PalettePath) || !File.Exists(baseProfile.PalettePath))
        {
            return baseProfile;
        }

        var terrainRecords = StaticPlacementXmlImporter.LoadTerrainRecordsFromXml(terrainXmlPath);
        if (terrainRecords.Count == 0)
        {
            return baseProfile;
        }

        var palette = Bmp8Codec.Read(baseProfile.PalettePath).Palette;
        var existingTiles = new HashSet<ushort>();
        foreach (var entry in baseProfile.Entries)
        {
            foreach (var tileId in entry.TileIds)
            {
                existingTiles.Add(tileId);
            }
        }

        var entries = new List<MapTransEntry>(baseProfile.Entries);
        foreach (var record in terrainRecords)
        {
            if (record.R is null || record.G is null || record.B is null)
            {
                continue;
            }

            var tileId = record.TileId;
            if (existingTiles.Contains(tileId))
            {
                continue;
            }

            var index = FindNearestPaletteIndex(palette, record.R.Value, record.G.Value, record.B.Value);
            entries.Add(new MapTransEntry(index, 0, new[] { tileId }));
            existingTiles.Add(tileId);
            addedTiles++;
        }

        return new MapTransProfile(baseProfile.Name, entries, baseProfile.PalettePath);
    }

    private static byte FindNearestPaletteIndex(BmpPaletteEntry[] palette, byte r, byte g, byte b)
    {
        var bestIndex = 0;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < palette.Length; i++)
        {
            var entry = palette[i];
            var dr = entry.Red - r;
            var dg = entry.Green - g;
            var db = entry.Blue - b;
            var distance = dr * dr + dg * dg + db * db;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        return (byte)bestIndex;
    }

    private static bool TryParseHexByte(string token, out byte value)
    {
        value = 0;
        token = token.Trim();
        if (!byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }
}
