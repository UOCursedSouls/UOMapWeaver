using System.Text.Json;

namespace UOMapWeaver.Core.MapTrans;

public static class MapTransJsonSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static MapTransProfile Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("MapTrans JSON not found.", path);
        }

        var json = File.ReadAllText(path);
        var dto = JsonSerializer.Deserialize<MapTransProfileDto>(json, JsonOptions)
                  ?? throw new InvalidDataException("Invalid MapTrans JSON.");

        var entries = dto.Entries
            .Select(entry => new MapTransEntry(
                entry.ColorIndex,
                entry.Altitude,
                entry.TileIds ?? Array.Empty<ushort>(),
                entry.Group))
            .ToList();

        var palettePath = ResolvePalettePath(path, dto.PaletteFile);
        return new MapTransProfile(dto.Name ?? Path.GetFileNameWithoutExtension(path), entries, palettePath);
    }

    public static void Save(string path, MapTransProfile profile, string? paletteFile = null)
    {
        var dto = new MapTransProfileDto
        {
            Name = profile.Name,
            PaletteFile = paletteFile,
            Entries = profile.Entries.Select(entry => new MapTransEntryDto
            {
                ColorIndex = entry.ColorIndex,
                Group = entry.Group,
                Altitude = entry.Altitude,
                TileIds = entry.TileIds.ToArray()
            }).ToList()
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var json = JsonSerializer.Serialize(dto, JsonOptions);
        File.WriteAllText(path, json);
    }

    private static string? ResolvePalettePath(string jsonPath, string? paletteFile)
    {
        if (string.IsNullOrWhiteSpace(paletteFile))
        {
            return null;
        }

        if (Path.IsPathRooted(paletteFile))
        {
            return File.Exists(paletteFile) ? paletteFile : null;
        }

        var folder = Path.GetDirectoryName(jsonPath);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return null;
        }

        var candidate = Path.Combine(folder, paletteFile);
        return File.Exists(candidate) ? candidate : null;
    }

    private sealed class MapTransProfileDto
    {
        public string? Name { get; set; }

        public string? PaletteFile { get; set; }

        public List<MapTransEntryDto> Entries { get; set; } = new();
    }

    private sealed class MapTransEntryDto
    {
        public byte ColorIndex { get; set; }

        public byte? Group { get; set; }

        public int Altitude { get; set; }

        public ushort[]? TileIds { get; set; }
    }
}
