using System.Xml.Linq;

namespace UOMapWeaver.Core.Map;

internal sealed class TerrainTransitionCatalog
{
    public TerrainTransitionCatalog(
        Dictionary<int, TerrainDefinition> terrainById,
        Dictionary<int, int> terrainIdByColor,
        Dictionary<string, TransitionSet> transitions,
        Dictionary<int, List<TransitionTile>> baseTiles)
    {
        TerrainById = terrainById;
        TerrainIdByColor = terrainIdByColor;
        Transitions = transitions;
        BaseTiles = baseTiles;
    }

    public Dictionary<int, TerrainDefinition> TerrainById { get; }

    public Dictionary<int, int> TerrainIdByColor { get; }

    public Dictionary<string, TransitionSet> Transitions { get; }

    public Dictionary<int, List<TransitionTile>> BaseTiles { get; }

    public static TerrainTransitionCatalog Load(string terrainXmlPath, string transitionsRoot, Action<MapConversionLogEntry>? log)
    {
        var terrainById = LoadTerrainDefinitions(terrainXmlPath, log, out var terrainIdByColor);
        var transitions = LoadTransitions(transitionsRoot, log);
        var baseTiles = new Dictionary<int, List<TransitionTile>>();

        foreach (var entry in transitions)
        {
            if (entry.Key.Length != 18)
            {
                continue;
            }

            if (TryParseSolidHash(entry.Key, out var terrainId))
            {
                baseTiles[terrainId] = entry.Value.MapTiles;
            }
        }

        return new TerrainTransitionCatalog(terrainById, terrainIdByColor, transitions, baseTiles);
    }

    private static Dictionary<int, TerrainDefinition> LoadTerrainDefinitions(
        string terrainXmlPath,
        Action<MapConversionLogEntry>? log,
        out Dictionary<int, int> terrainIdByColor)
    {
        terrainIdByColor = new Dictionary<int, int>();
        var result = new Dictionary<int, TerrainDefinition>();

        if (!File.Exists(terrainXmlPath))
        {
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Warning,
                $"Terrain.xml not found: {terrainXmlPath}"));
            return result;
        }

        var doc = XDocument.Load(terrainXmlPath);
        foreach (var node in doc.Descendants("Terrain"))
        {
            var id = ReadInt(node, "ID");
            var tileId = ReadUShort(node, "TileID");
            var r = ReadInt(node, "R");
            var g = ReadInt(node, "G");
            var b = ReadInt(node, "B");
            var random = ReadBool(node, "Random");

            var colorKey = (r << 16) | (g << 8) | b;
            if (!terrainIdByColor.ContainsKey(colorKey))
            {
                terrainIdByColor[colorKey] = id;
            }

            result[id] = new TerrainDefinition(id, tileId, colorKey, random);
        }

        log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Info,
            $"Terrain definitions loaded: {result.Count:N0}."));

        return result;
    }

    private static Dictionary<string, TransitionSet> LoadTransitions(string transitionsRoot, Action<MapConversionLogEntry>? log)
    {
        var result = new Dictionary<string, TransitionSet>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(transitionsRoot))
        {
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Warning,
                $"Transitions folder not found: {transitionsRoot}"));
            return result;
        }

        var files = Directory.EnumerateFiles(transitionsRoot, "*.xml", SearchOption.AllDirectories).ToList();
        if (files.Count == 0)
        {
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Warning,
                $"No transition XML files found in {transitionsRoot}."));
        }
        else
        {
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Info,
                $"Transition XML files found: {files.Count:N0}."));

            if (files.Count <= 1)
            {
                log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Warning,
                    "Transition set looks incomplete. Copy the full Transitions folder from MapCreator to improve corners/edges."));
            }
        }
        foreach (var path in files)
        {
            try
            {
                var doc = XDocument.Load(path);
                foreach (var info in doc.Descendants("TransInfo"))
                {
                    var hash = info.Attribute("HashKey")?.Value?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(hash))
                    {
                        continue;
                    }

                    if (!result.TryGetValue(hash, out var set))
                    {
                        set = new TransitionSet(new List<TransitionTile>(), new List<TransitionStatic>());
                        result[hash] = set;
                    }

                    foreach (var mapTile in info.Descendants("MapTile"))
                    {
                        var tileId = ReadUShort(mapTile, "TileID");
                        var altMod = ReadSByte(mapTile, "AltIDMod");
                        set.MapTiles.Add(new TransitionTile(tileId, altMod));
                    }

                    foreach (var staticTile in info.Descendants("StaticTile"))
                    {
                        var tileId = ReadUShort(staticTile, "TileID");
                        var altMod = ReadSByte(staticTile, "AltIDMod");
                        set.StaticTiles.Add(new TransitionStatic(tileId, altMod));
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Warning,
                    $"Transition load failed: {Path.GetFileName(path)} ({ex.Message})"));
            }
        }

        log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Info,
            $"Transition entries loaded: {result.Count:N0}."));

        return result;
    }

    private static bool TryParseSolidHash(string hash, out int terrainId)
    {
        terrainId = 0;
        if (hash.Length != 18)
        {
            return false;
        }

        var first = hash.Substring(0, 2);
        if (!int.TryParse(first, System.Globalization.NumberStyles.HexNumber, null, out var id))
        {
            return false;
        }

        for (var i = 2; i < hash.Length; i += 2)
        {
            if (!hash.Substring(i, 2).Equals(first, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        terrainId = id;
        return true;
    }

    private static int ReadInt(XElement node, string name)
        => int.TryParse(node.Attribute(name)?.Value, out var value) ? value : 0;

    private static ushort ReadUShort(XElement node, string name)
        => ushort.TryParse(node.Attribute(name)?.Value, out var value) ? value : (ushort)0;

    private static sbyte ReadSByte(XElement node, string name)
        => sbyte.TryParse(node.Attribute(name)?.Value, out var value) ? value : (sbyte)0;

    private static bool ReadBool(XElement node, string name)
        => bool.TryParse(node.Attribute(name)?.Value, out var value) && value;
}

internal sealed record TerrainDefinition(int Id, ushort TileId, int ColorKey, bool Random);

internal sealed record TransitionTile(ushort TileId, sbyte AltitudeMod);

internal sealed record TransitionStatic(ushort TileId, sbyte AltitudeMod);

internal sealed record TransitionSet(List<TransitionTile> MapTiles, List<TransitionStatic> StaticTiles);
