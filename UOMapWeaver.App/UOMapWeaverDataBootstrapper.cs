using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using UOMapWeaver.App.Defaults;
using UOMapWeaver.Core;
using UOMapWeaver.Core.Bmp;

namespace UOMapWeaver.App;

internal static class UOMapWeaverDataBootstrapper
{
    public static void EnsureDataFolders()
    {
        var root = UOMapWeaverDataPaths.DataRoot;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(UOMapWeaverDataPaths.SystemRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.MapTransRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.TransitionsRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.StaticsRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.PhotoshopRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.ImportRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.LoggerRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.ExportRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.DeveloperRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.TileColorsRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.TileReplaceRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.PalettesRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.PresetsRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.DefinitionsRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.ExamplesRoot);

        EnsureMapPresets();
        EnsureMapDefinitions();
        EnsureDefaultPalette();
        EnsureDataReadme();
        EnsurePlaceholderReadmes();
        CopyDefaultsFromOutput();
        CopyDefaultsFromTmpUtility();
        EnsureFallbackMapTransProfile();
    }

    private static void EnsureMapPresets()
    {
        var path = UOMapWeaverDataPaths.MapPresetsPath;
        var presets = MapDefaults.Presets
            .Select(preset => new MapPresetRecord(preset.Name, preset.Width, preset.Height, preset.IsSeparator))
            .ToList();

        var existing = LoadJson<MapPresetRecord>(path);
        if (existing.Count == 0 && File.Exists(path))
        {
            return;
        }

        var merged = MergePresets(existing, presets, out var updated);
        if (!File.Exists(path) || updated)
        {
            var json = JsonSerializer.Serialize(merged, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }

    private static void EnsureMapDefinitions()
    {
        var path = UOMapWeaverDataPaths.MapDefinitionsPath;
        var definitions = MapDefaults.Presets
            .Where(preset => !preset.IsSeparator && preset.Width > 0 && preset.Height > 0)
            .Select(preset => new MapDefinitionRecord(preset.Name, preset.Width, preset.Height))
            .ToList();

        var existing = LoadJson<MapDefinitionRecord>(path);
        if (existing.Count == 0 && File.Exists(path))
        {
            return;
        }

        var merged = MergeDefinitions(existing, definitions, out var updated);
        if (!File.Exists(path) || updated)
        {
            var json = JsonSerializer.Serialize(merged, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            File.WriteAllText(path, json);
        }
    }

    private static List<MapPresetRecord> MergePresets(
        List<MapPresetRecord> existing,
        List<MapPresetRecord> defaults,
        out bool updated)
    {
        updated = false;
        if (existing.Count == 0)
        {
            updated = true;
            return defaults;
        }

        var merged = new List<MapPresetRecord>(existing);
        var known = new HashSet<(int width, int height)>(
            existing.Where(p => !p.IsSeparator).Select(p => (p.Width, p.Height)));

        foreach (var preset in defaults)
        {
            if (preset.IsSeparator)
            {
                continue;
            }

            if (known.Add((preset.Width, preset.Height)))
            {
                merged.Add(preset);
                updated = true;
            }
        }

        return merged;
    }

    private static List<MapDefinitionRecord> MergeDefinitions(
        List<MapDefinitionRecord> existing,
        List<MapDefinitionRecord> defaults,
        out bool updated)
    {
        updated = false;
        if (existing.Count == 0)
        {
            updated = true;
            return defaults;
        }

        var merged = new List<MapDefinitionRecord>(existing);
        var known = new HashSet<(int width, int height)>(existing.Select(p => (p.Width, p.Height)));

        foreach (var def in defaults)
        {
            if (known.Add((def.Width, def.Height)))
            {
                merged.Add(def);
                updated = true;
            }
        }

        return merged;
    }

    private static List<T> LoadJson<T>(string path)
    {
        if (!File.Exists(path))
        {
            return new List<T>();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<T>>(json) ?? new List<T>();
        }
        catch
        {
            return new List<T>();
        }
    }

    private static void EnsureDefaultPalette()
    {
        var palettePath = Path.Combine(UOMapWeaverDataPaths.PalettesRoot, "GrayscalePalette.bmp");
        if (File.Exists(palettePath))
        {
            return;
        }

        var palette = Bmp8Codec.CreateGrayscalePalette();
        var image = new Bmp8Image(1, 1, new byte[] { 0 }, palette);
        Bmp8Codec.Write(palettePath, image);
    }

    private static void EnsureDataReadme()
    {
        var readmePath = Path.Combine(UOMapWeaverDataPaths.DataRoot, "README.txt");
        if (File.Exists(readmePath))
        {
            return;
        }

        var lines = new[]
        {
            "UOMapWeaverData",
            "----------------",
            "This folder holds default data for UOMapWeaver.",
            "",
            "System/",
            "  Legacy system data from UOLandscaper/MapCreator.",
            "System/MapTrans/",
            "  MapTrans profiles (.txt) and optional ColorPalette.bmp per profile folder.",
            "Transitions/",
            "  Terrain transition rules and templates.",
            "Statics/",
            "  Static placement definitions.",
            "Photoshop/",
            "  Photoshop swatches/palettes.",
            "Import Files/",
            "  Import templates and metadata.",
            "Logger/",
            "  Legacy logger templates and outputs.",
            "ExportUOL/",
            "  Export output templates.",
            "Developer/",
            "  Developer reference files.",
            "TileColors/",
            "  Tile color JSON files generated by the Tile Colors tab.",
            "JsonTileReplace/",
            "  Tile replacement JSON files generated by the Tile Remap tab.",
            "Palettes/",
            "  Palette BMPs used for Blank BMP generation (includes GrayscalePalette.bmp).",
            "Presets/",
            "  map-presets.json (editable list of map sizes for Blank BMP).",
            "Definitions/",
            "  map-definitions.json (used for map size detection).",
            "Examples/",
            "  Sample inputs/outputs copied from TMP_UtitlityFiles if available."
        };

        File.WriteAllLines(readmePath, lines);
    }

    private static void EnsurePlaceholderReadmes()
    {
        var placeholders = new Dictionary<string, string>
        {
            { UOMapWeaverDataPaths.SystemRoot, "System templates and MapTrans profiles live here." },
            { UOMapWeaverDataPaths.MapTransRoot, "MapTrans profiles (.txt) and palette BMPs." },
            { UOMapWeaverDataPaths.TransitionsRoot, "Terrain transition rules and templates." },
            { UOMapWeaverDataPaths.StaticsRoot, "Static placement definitions." },
            { UOMapWeaverDataPaths.PhotoshopRoot, "Photoshop swatches/palettes." },
            { UOMapWeaverDataPaths.ImportRoot, "Import templates and metadata." },
            { UOMapWeaverDataPaths.LoggerRoot, "Legacy logger templates and outputs." },
            { UOMapWeaverDataPaths.ExportRoot, "Export output templates." },
            { UOMapWeaverDataPaths.DeveloperRoot, "Developer reference files." },
            { UOMapWeaverDataPaths.ExamplesRoot, "Examples and sample inputs." },
            { UOMapWeaverDataPaths.TileReplaceRoot, "Tile replacement JSON files." }
        };

        foreach (var (folder, description) in placeholders)
        {
            var path = Path.Combine(folder, "README.txt");
            if (!File.Exists(path))
            {
                File.WriteAllLines(path, new[]
                {
                    description,
                    "Place required files here if defaults are missing."
                });
            }
        }
    }

    private static void EnsureFallbackMapTransProfile()
    {
        if (Directory.EnumerateFiles(UOMapWeaverDataPaths.MapTransRoot, "Mod*.txt", SearchOption.AllDirectories).Any())
        {
            return;
        }

        var fallbackPath = Path.Combine(UOMapWeaverDataPaths.MapTransRoot, "ModDefault.txt");
        if (File.Exists(fallbackPath))
        {
            return;
        }

        var lines = new[]
        {
            "// Fallback MapTrans profile created by UOMapWeaver.",
            "// Format: <colorIndexHex> <altitude> <tileIdHex> [tileIdHex ...]",
            "// Example below maps color 00 at altitude 0 to tile 0000.",
            "00 0 0000"
        };

        File.WriteAllLines(fallbackPath, lines);
    }

    private static void CopyDefaultsFromOutput()
    {
        var baseDir = AppContext.BaseDirectory;
        var defaultsRoot = Path.Combine(baseDir, "Defaults");
        if (!Directory.Exists(defaultsRoot))
        {
            return;
        }

        CopyDirectoryIfPresent(Path.Combine(defaultsRoot, "Data"), UOMapWeaverDataPaths.DataRoot);
        CopyDirectoryIfPresent(Path.Combine(defaultsRoot, "UOLandscaper", "Data"), UOMapWeaverDataPaths.DataRoot);

        var mapCreatorEngine = Path.Combine(defaultsRoot, "MapCreator", "Engine");
        CopyDirectoryIfPresent(Path.Combine(mapCreatorEngine, "MapTrans"), UOMapWeaverDataPaths.MapTransRoot);
        CopyDirectoryIfPresent(Path.Combine(mapCreatorEngine, "RoughEdge"), Path.Combine(UOMapWeaverDataPaths.SystemRoot, "RoughEdge"));
        CopyDirectoryIfPresent(Path.Combine(mapCreatorEngine, "Templates"), Path.Combine(UOMapWeaverDataPaths.SystemRoot, "Templates"));
        CopyDirectoryIfPresent(Path.Combine(mapCreatorEngine, "TerrainTypes"), Path.Combine(UOMapWeaverDataPaths.SystemRoot, "TerrainTypes"));
        CopyDirectoryIfPresent(Path.Combine(mapCreatorEngine, "Transitions"), UOMapWeaverDataPaths.TransitionsRoot);
        CopyFilesIfPresent(mapCreatorEngine, UOMapWeaverDataPaths.SystemRoot, "*.xml");

        CopyDirectoryIfPresent(Path.Combine(defaultsRoot, "Examples"), UOMapWeaverDataPaths.ExamplesRoot);
    }

    private static void CopyDefaultsFromTmpUtility()
    {
        var tmpRoot = FindTmpUtilityRoot();
        if (string.IsNullOrWhiteSpace(tmpRoot))
        {
            return;
        }

        CopyDirectoryIfPresent(Path.Combine(tmpRoot, "Data"), UOMapWeaverDataPaths.DataRoot);
        CopyDirectoryIfPresent(Path.Combine(tmpRoot, "Example"), UOMapWeaverDataPaths.ExamplesRoot);

        var uolData = Path.Combine(tmpRoot, "UOLandscaper 1.5", "Data");
        CopyDirectoryIfPresent(uolData, UOMapWeaverDataPaths.DataRoot);

        var mapCreatorEngine = Path.Combine(tmpRoot, "MapCreator_golfin", "MapCompiler", "Engine");
        CopyDirectoryIfPresent(Path.Combine(mapCreatorEngine, "MapTrans"), UOMapWeaverDataPaths.MapTransRoot);
        CopyDirectoryIfPresent(Path.Combine(mapCreatorEngine, "RoughEdge"), Path.Combine(UOMapWeaverDataPaths.SystemRoot, "RoughEdge"));
        CopyDirectoryIfPresent(Path.Combine(mapCreatorEngine, "Templates"), Path.Combine(UOMapWeaverDataPaths.SystemRoot, "Templates"));
        CopyDirectoryIfPresent(Path.Combine(mapCreatorEngine, "TerrainTypes"), Path.Combine(UOMapWeaverDataPaths.SystemRoot, "TerrainTypes"));
        CopyDirectoryIfPresent(Path.Combine(mapCreatorEngine, "Transitions"), UOMapWeaverDataPaths.TransitionsRoot);
        CopyFilesIfPresent(mapCreatorEngine, UOMapWeaverDataPaths.SystemRoot, "*.xml");
    }

    private static string? FindTmpUtilityRoot()
    {
        foreach (var baseDir in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            var candidate = Path.Combine(baseDir, "TMP_UtitlityFiles");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static void CopyDirectoryIfPresent(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        CopyDirectory(source, destination, overwrite: false);
    }

    private static void CopyDirectory(string source, string destination, bool overwrite)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destination);
            if (!overwrite && File.Exists(target))
            {
                continue;
            }

            File.Copy(file, target, overwrite);
        }
    }

    private static void CopyFilesIfPresent(string source, string destination, string searchPattern)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source, searchPattern, SearchOption.TopDirectoryOnly))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            if (File.Exists(target))
            {
                continue;
            }

            File.Copy(file, target, overwrite: false);
        }
    }

    private sealed record MapPresetRecord(string Name, int Width, int Height, bool IsSeparator);

    private sealed record MapDefinitionRecord(string Name, int Width, int Height);
}
