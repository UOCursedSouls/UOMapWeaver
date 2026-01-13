using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace UOMapWeaver.Core.TileReplace;

public static class TileReplacementMapSerializer
{
    public static TileReplacementMap Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Tile replacement JSON not found.", path);
        }

        var json = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new TileReplacementMap();
        }

        var dto = JsonSerializer.Deserialize<TileReplacementMapDto>(json);
        if (dto is null)
        {
            return new TileReplacementMap();
        }

        var terrain = ParseDictionary(dto.Terrain);
        var statics = ParseDictionary(dto.Statics);
        var map = new TileReplacementMap(terrain, statics)
        {
            SourceClientPath = dto.SourceClientPath,
            DestClientPath = dto.DestClientPath
        };

        return map;
    }

    public static void Save(string path, TileReplacementMap map)
    {
        var dto = new TileReplacementMapDto
        {
            SourceClientPath = map.SourceClientPath,
            DestClientPath = map.DestClientPath,
            Terrain = map.Terrain.ToDictionary(pair => $"0x{pair.Key:X4}", pair => $"0x{pair.Value:X4}"),
            Statics = map.Statics.ToDictionary(pair => $"0x{pair.Key:X4}", pair => $"0x{pair.Value:X4}")
        };

        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, json);
    }

    public static bool TryRead(string path, out TileReplacementMap map)
    {
        try
        {
            map = Load(path);
            return true;
        }
        catch
        {
            map = new TileReplacementMap();
            return false;
        }
    }

    private static Dictionary<ushort, ushort> ParseDictionary(Dictionary<string, string>? values)
    {
        var result = new Dictionary<ushort, ushort>();
        if (values is null)
        {
            return result;
        }

        foreach (var (key, value) in values)
        {
            if (!TryParseTileId(key, out var from))
            {
                continue;
            }

            if (!TryParseTileId(value, out var to))
            {
                continue;
            }

            result[from] = to;
        }

        return result;
    }

    private static bool TryParseTileId(string text, out ushort tileId)
    {
        tileId = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ushort.TryParse(trimmed[2..], System.Globalization.NumberStyles.HexNumber, null, out tileId);
        }

        return ushort.TryParse(trimmed, out tileId);
    }

    private sealed class TileReplacementMapDto
    {
        public string? SourceClientPath { get; set; }

        public string? DestClientPath { get; set; }

        public Dictionary<string, string>? Terrain { get; set; }

        public Dictionary<string, string>? Statics { get; set; }
    }
}
