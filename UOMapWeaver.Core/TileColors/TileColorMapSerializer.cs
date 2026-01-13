using System.Text.Json;
using UOMapWeaver.Core.Bmp;

namespace UOMapWeaver.Core.TileColors;

public static class TileColorMapSerializer
{
    private const int CurrentVersion = 1;
    private static readonly RgbColor DefaultUnknown = new(255, 0, 255);

    public static bool TryReadMode(string path, out TileColorMode mode)
    {
        mode = TileColorMode.Indexed8;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            if (new FileInfo(path).Length == 0)
            {
                return false;
            }

            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<TileColorMapDto>(json);
            if (dto == null)
            {
                return false;
            }

            mode = ParseMode(dto.Mode);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static TileColorMap Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Tile color JSON not found.", path);
        }

        var json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize<TileColorMapDto>(json)
                  ?? throw new InvalidDataException("Invalid tile color JSON.");

        var mode = ParseMode(dto.Mode);
        var unknown = ParseColor(dto.UnknownColor) ?? DefaultUnknown;

        var tileToIndex = new Dictionary<ushort, byte>();
        var tileToColor = new Dictionary<ushort, RgbColor>();
        var indexToTile = new Dictionary<byte, ushort>();
        var colorToTile = new Dictionary<int, ushort>();

        if (dto.Tiles != null)
        {
            foreach (var pair in dto.Tiles)
            {
                if (!TryParseTileId(pair.Key, out var tileId))
                {
                    continue;
                }

                if (mode == TileColorMode.Indexed8)
                {
                    if (!byte.TryParse(pair.Value, out var index))
                    {
                        continue;
                    }

                    tileToIndex[tileId] = index;
                    if (!indexToTile.ContainsKey(index))
                    {
                        indexToTile[index] = tileId;
                    }
                }
                else
                {
                    if (!RgbColor.TryParse(pair.Value, out var color))
                    {
                        continue;
                    }

                    tileToColor[tileId] = color;
                    if (!colorToTile.ContainsKey(color.Key))
                    {
                        colorToTile[color.Key] = tileId;
                    }
                }
            }
        }

        BmpPaletteEntry[]? palette = null;
        if (mode == TileColorMode.Indexed8)
        {
            palette = BuildPalette(dto.Palette, unknown);
        }

        return new TileColorMap(mode, tileToIndex, tileToColor, indexToTile, colorToTile, palette, unknown);
    }

    public static void Save(string path, TileColorMap map)
    {
        var dto = new TileColorMapDto
        {
            Version = CurrentVersion,
            Mode = map.Mode.ToString(),
            UnknownColor = map.UnknownColor.ToHex(),
            Tiles = new Dictionary<string, string>()
        };

        if (map.Mode == TileColorMode.Indexed8)
        {
            foreach (var pair in map.TileToIndex.OrderBy(pair => pair.Key))
            {
                dto.Tiles[$"0x{pair.Key:X4}"] = pair.Value.ToString();
            }

            dto.Palette = SerializePalette(map.Palette);
        }
        else
        {
            foreach (var pair in map.TileToColor.OrderBy(pair => pair.Key))
            {
                dto.Tiles[$"0x{pair.Key:X4}"] = pair.Value.ToHex();
            }
        }

        var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(path, json);
    }

    private static TileColorMode ParseMode(string? value)
    {
        if (string.Equals(value, "Rgb24", StringComparison.OrdinalIgnoreCase))
        {
            return TileColorMode.Rgb24;
        }

        return TileColorMode.Indexed8;
    }

    private static bool TryParseTileId(string value, out ushort tileId)
    {
        tileId = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ushort.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out tileId);
        }

        return ushort.TryParse(text, out tileId);
    }

    private static RgbColor? ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return RgbColor.TryParse(value, out var color) ? color : null;
    }

    private static BmpPaletteEntry[] BuildPalette(string[]? palette, RgbColor unknown)
    {
        var entries = Bmp8Codec.CreateGrayscalePalette();
        if (palette == null)
        {
            ApplyUnknown(entries, unknown);
            return entries;
        }

        var count = Math.Min(entries.Length, palette.Length);
        for (var i = 0; i < count; i++)
        {
            if (RgbColor.TryParse(palette[i], out var color))
            {
                entries[i] = new BmpPaletteEntry(color.B, color.G, color.R, 0);
            }
        }

        ApplyUnknown(entries, unknown);
        return entries;
    }

    private static void ApplyUnknown(BmpPaletteEntry[] palette, RgbColor unknown)
    {
        if (palette.Length <= 255)
        {
            return;
        }

        palette[255] = new BmpPaletteEntry(unknown.B, unknown.G, unknown.R, 0);
    }

    private static string[]? SerializePalette(BmpPaletteEntry[]? palette)
    {
        if (palette == null || palette.Length == 0)
        {
            return null;
        }

        var result = new string[palette.Length];
        for (var i = 0; i < palette.Length; i++)
        {
            var entry = palette[i];
            result[i] = $"#{entry.Red:X2}{entry.Green:X2}{entry.Blue:X2}";
        }

        return result;
    }

    private sealed class TileColorMapDto
    {
        public int Version { get; set; } = CurrentVersion;

        public string? Mode { get; set; }

        public string? UnknownColor { get; set; }

        public string[]? Palette { get; set; }

        public Dictionary<string, string>? Tiles { get; set; }
    }
}
