using System.Globalization;

namespace UOMapWeaver.Core.MapTrans;

public static class MapTransParser
{
    public static MapTransProfile LoadFromFile(string path)
    {
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return MapTransJsonSerializer.Load(path);
        }

        var lines = File.ReadAllLines(path);
        var entries = new List<MapTransEntry>();

        foreach (var rawLine in lines)
        {
            var line = StripComment(rawLine);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var tokens = SplitTokens(line);
            if (tokens.Count < 3)
            {
                continue;
            }

            if (!TryParseHexByte(tokens[0], out var colorIndex))
            {
                continue;
            }

            if (tokens.Count >= 3 && TryParseHexByte(tokens[1], out var group))
            {
                if (tokens.Count >= 4 &&
                    TryParseSignedInt(tokens[2], out var groupedAltitude) &&
                    TryParseHexUShort(tokens[3], out _))
                {
                    var tileIds = ParseTileIds(tokens, 3);
                    entries.Add(new MapTransEntry(colorIndex, groupedAltitude, tileIds, group));
                    continue;
                }

                if (TryParseHexUShort(tokens[2], out _))
                {
                    var tileIds = ParseTileIds(tokens, 2);
                    entries.Add(new MapTransEntry(colorIndex, 0, tileIds, group));
                    continue;
                }
            }

            if (TryParseSignedInt(tokens[1], out var altitude))
            {
                var tiles = ParseTileIds(tokens, 2);
                entries.Add(new MapTransEntry(colorIndex, altitude, tiles));
            }
        }

        var name = Path.GetFileNameWithoutExtension(path);
        var palettePath = FindPalettePath(path);
        return new MapTransProfile(name, entries, palettePath);
    }

    private static string StripComment(string line)
    {
        var index = line.IndexOf("//", StringComparison.Ordinal);
        return index >= 0 ? line[..index].Trim() : line.Trim();
    }

    private static List<string> SplitTokens(string line)
    {
        var tokens = new List<string>();
        var current = new Span<char>(new char[line.Length]);
        var length = 0;

        foreach (var c in line)
        {
            if (char.IsWhiteSpace(c))
            {
                if (length > 0)
                {
                    tokens.Add(new string(current[..length]));
                    length = 0;
                }
                continue;
            }

            current[length++] = c;
        }

        if (length > 0)
        {
            tokens.Add(new string(current[..length]));
        }

        return tokens;
    }

    private static IReadOnlyList<ushort> ParseTileIds(List<string> tokens, int startIndex)
    {
        var tiles = new List<ushort>();

        for (var i = startIndex; i < tokens.Count; i++)
        {
            if (TryParseHexUShort(tokens[i], out var tileId))
            {
                tiles.Add(tileId);
            }
        }

        return tiles;
    }

    private static bool TryParseHexByte(string token, out byte value)
    {
        value = 0;
        if (!IsHexToken(token))
        {
            return false;
        }

        if (!byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryParseHexUShort(string token, out ushort value)
    {
        value = 0;
        if (!IsHexToken(token))
        {
            return false;
        }

        if (!ushort.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryParseSignedInt(string token, out int value)
        => int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    private static bool IsHexToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        for (var i = 0; i < token.Length; i++)
        {
            if (!Uri.IsHexDigit(token[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static string? FindPalettePath(string mapTransPath)
    {
        var directory = Path.GetDirectoryName(mapTransPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var palettePath = Path.Combine(directory, "TerrainPalette.bmp");
        if (File.Exists(palettePath))
        {
            return palettePath;
        }

        var current = new DirectoryInfo(directory);
        while (current != null)
        {
            palettePath = Path.Combine(current.FullName, "TerrainPalette.bmp");
            if (File.Exists(palettePath))
            {
                return palettePath;
            }

            if (current.FullName.Equals(UOMapWeaverDataPaths.MapTransRoot, StringComparison.OrdinalIgnoreCase) ||
                current.FullName.Equals(UOMapWeaverDataPaths.DataRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = current.Parent;
        }

        var legacyFallback = Path.Combine(UOMapWeaverDataPaths.MapTransRoot, "TerrainPalette.bmp");
        if (File.Exists(legacyFallback))
        {
            return legacyFallback;
        }

        return null;
    }
}

