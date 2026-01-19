using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using UOMapWeaver.Core;
using UOMapWeaver.App.Defaults;

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
        Directory.CreateDirectory(Path.Combine(UOMapWeaverDataPaths.TransitionsRoot, "Citified Terrains"));
        Directory.CreateDirectory(Path.Combine(UOMapWeaverDataPaths.TransitionsRoot, "Citified Terrains", "3way"));
        Directory.CreateDirectory(Path.Combine(UOMapWeaverDataPaths.TransitionsRoot, "Natural Terrains"));
        Directory.CreateDirectory(Path.Combine(UOMapWeaverDataPaths.TransitionsRoot, "Natural Terrains", "3way"));
        Directory.CreateDirectory(UOMapWeaverDataPaths.TemplatesRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.RoughEdgeRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.TerrainTypesRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.StaticsRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.PhotoshopRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.ImportRoot);
        Directory.CreateDirectory(Path.Combine(UOMapWeaverDataPaths.ImportRoot, "Example"));
        Directory.CreateDirectory(Path.Combine(UOMapWeaverDataPaths.ImportRoot, "Example", "Import"));
        Directory.CreateDirectory(Path.Combine(UOMapWeaverDataPaths.DataRoot, "ColorTables", "ACT"));
        Directory.CreateDirectory(UOMapWeaverDataPaths.LoggerRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.ExportRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.DeveloperRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.TileColorsRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.TileReplaceRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.PalettesRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.PresetsRoot);
        Directory.CreateDirectory(UOMapWeaverDataPaths.DefinitionsRoot);

        EnsureFolderReadmes();
        EnsureMapPresets(force: false);
    }

    public static void RegenerateDefaults()
    {
        EnsureDataFolders();
        EnsureFolderReadmes(force: true);
        EnsureMapPresets(force: true);
    }

    private static void EnsureMapPresets(bool force)
    {
        var path = UOMapWeaverDataPaths.MapPresetsPath;
        if (!force && File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? UOMapWeaverDataPaths.PresetsRoot);
        var presets = MapDefaults.Presets
            .Select(preset => new MapPresetDto(preset.Name, preset.Width, preset.Height, preset.IsSeparator))
            .ToList();

        var json = JsonSerializer.Serialize(presets, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(path, json);
    }

    private static void EnsureFolderReadmes(bool force = false)
    {
        var entries = new Dictionary<string, string[]>
        {
            {
                UOMapWeaverDataPaths.DataRoot,
                new[]
                {
                    "UOMapWeaverData",
                    "----------------",
                    "User-supplied data files for UOMapWeaver.",
                    "No default files are shipped. Place your own files in the folders below.",
                    "",
                    "Root files (place directly in this folder):",
                    "- Terrain.xml (terrain definitions + colors used by MapTrans and fills)",
                    "- Altitude.xml (altitude rules, optional)",
                    "- MapInfo.xml (map metadata, optional)",
                    "- Template.xml",
                    "- 2Way_Template.xml",
                    "- 3Way_Template.xml",
                    "- customSwatchNames.xml (optional)",
                    "",
                    "Folders:",
                    "- MapTrans/ (MapTrans profiles .txt/.xml)",
                    "- Palettes/ (8-bit palette BMPs, 256 colors)",
                    "- ColorTables/ACT/ (Terrain.act, Altitude.act)",
                    "- Transitions/ (terrain transition XML rules)",
                    "- TerrainTypes/ (terrain type XMLs + RandomStatics)",
                    "- Templates/ (template XMLs)",
                    "- RoughEdge/ (rough edge XMLs)",
                    "- Statics/ (optional custom RandomStatics XMLs)",
                    "- Import Files/ (static import XMLs)",
                    "- Definitions/ (map-definitions.json)",
                    "- Presets/ (map-presets.json)",
                    "- TileColors/ (tile color JSON outputs)",
                    "- JsonTileReplace/ (tile replacement JSONs)",
                    "- Logger/, ExportUOL/, Developer/, Photoshop/, System/ (optional legacy data)"
                }
            },
            {
                UOMapWeaverDataPaths.MapTransRoot,
                new[]
                {
                    "MapTrans",
                    "--------",
                    "MapTrans profiles used for MUL <-> BMP conversion.",
                    "Expected files:",
                    "- Mod11.txt (or Mod9.txt / Mod10.txt)",
                    "- Mod11.xml (optional XML profile)",
                    "- Mod11b.txt (optional variant)",
                    "- TransInfo.xml (optional profile list)",
                    "",
                    "Notes:",
                    "- If you use Mod11.xml, keep Mod11.txt with the same base name.",
                    "- You can organize profiles in subfolders (e.g., DragonMod11)."
                }
            },
            {
                UOMapWeaverDataPaths.PalettesRoot,
                new[]
                {
                    "Palettes",
                    "--------",
                    "8-bit BMP palettes (256 colors). Used by MUL->BMP, Blank BMP, and MapTrans.",
                    "Expected files:",
                    "- TerrainPalette.bmp (recommended)",
                    "- AltitudePalette.bmp (optional)",
                    "- Any *.bmp palette with 256 entries."
                }
            },
            {
                Path.Combine(UOMapWeaverDataPaths.DataRoot, "ColorTables", "ACT"),
                new[]
                {
                    "ColorTables/ACT",
                    "---------------",
                    "Photoshop ACT palettes (optional). Can be imported into Palettes.",
                    "Expected files:",
                    "- Terrain.act",
                    "- Altitude.act"
                }
            },
            {
                Path.Combine(UOMapWeaverDataPaths.DataRoot, "ColorTables"),
                new[]
                {
                    "ColorTables",
                    "-----------",
                    "Palette source files (ACT) used for building BMP palettes.",
                    "Expected folder:",
                    "- ACT/"
                }
            },
            {
                UOMapWeaverDataPaths.TransitionsRoot,
                new[]
                {
                    "Transitions",
                    "-----------",
                    "Terrain transition XML rules used by generators.",
                    "Copy the full Transitions folder from MapCreator/UOLandscaper for correct corners.",
                    "Expected folders:",
                    "- Natural Terrains/",
                    "- Citified Terrains/",
                    "- 3way/ (inside each group)",
                    "",
                    "Example file name:",
                    "- Grass <-> Sand.xml"
                }
            },
            {
                Path.Combine(UOMapWeaverDataPaths.TransitionsRoot, "Natural Terrains"),
                new[]
                {
                    "Transitions/Natural Terrains",
                    "----------------------------",
                    "Natural terrain transitions.",
                    "Example file name:",
                    "- Grass <-> ShallowWater.xml"
                }
            },
            {
                Path.Combine(UOMapWeaverDataPaths.TransitionsRoot, "Natural Terrains", "3way"),
                new[]
                {
                    "Transitions/Natural Terrains/3way",
                    "---------------------------------",
                    "3-way transition templates.",
                    "Example file name:",
                    "- Grass <-> Sand <-> ShallowWater.xml"
                }
            },
            {
                Path.Combine(UOMapWeaverDataPaths.TransitionsRoot, "Citified Terrains"),
                new[]
                {
                    "Transitions/Citified Terrains",
                    "-----------------------------",
                    "City/structure terrain transitions.",
                    "Example file name:",
                    "- Cobblestones <-> Grass.xml"
                }
            },
            {
                Path.Combine(UOMapWeaverDataPaths.TransitionsRoot, "Citified Terrains", "3way"),
                new[]
                {
                    "Transitions/Citified Terrains/3way",
                    "----------------------------------",
                    "3-way city transition templates.",
                    "Example file name:",
                    "- Cobblestones <-> DarkWoodenFloor <-> Grass.xml"
                }
            },
            {
                UOMapWeaverDataPaths.TerrainTypesRoot,
                new[]
                {
                    "TerrainTypes",
                    "------------",
                    "Terrain type XMLs used by generators. Each file can include RandomStatics.",
                    "Expected files:",
                    "- Grass.xml, Sand.xml, Forest.xml, Snow.xml, etc.",
                    "",
                    "Example snippet:",
                    "<RandomStatics Chance=\"15\">",
                    "  <Statics Freq=\"100\">",
                    "    <Static TileID=\"0x0A1B\" X=\"0\" Y=\"0\" Z=\"0\" Hue=\"0\" />",
                    "  </Statics>",
                    "</RandomStatics>"
                }
            },
            {
                UOMapWeaverDataPaths.TemplatesRoot,
                new[]
                {
                    "Templates",
                    "---------",
                    "Template XMLs used by generators.",
                    "Expected files:",
                    "- 2Way_Template.xml",
                    "- 3Way_Template.xml",
                    "- Internal 3Way_Template.xml",
                    "- External 3Way_Template.xml"
                }
            },
            {
                UOMapWeaverDataPaths.RoughEdgeRoot,
                new[]
                {
                    "RoughEdge",
                    "---------",
                    "Rough edge definition XMLs.",
                    "Expected files:",
                    "- Left.xml",
                    "- Top.xml",
                    "- Corner.xml"
                }
            },
            {
                UOMapWeaverDataPaths.StaticsRoot,
                new[]
                {
                    "Statics",
                    "-------",
                    "Optional custom RandomStatics XMLs. Use when you want extra rules for a terrain.",
                    "Expected files:",
                    "- Grass.xml, Sand.xml, Forest.xml, etc.",
                    "",
                    "Notes:",
                    "- File name must match Terrain.xml <Terrain Name=\"...\">",
                    "- Same format as TerrainTypes RandomStatics.",
                    "",
                    "Example snippet:",
                    "<RandomStatics Chance=\"10\">",
                    "  <Statics Freq=\"100\">",
                    "    <Static TileID=\"0x0A1B\" X=\"0\" Y=\"0\" Z=\"0\" Hue=\"0\" />",
                    "  </Statics>",
                    "</RandomStatics>"
                }
            },
            {
                UOMapWeaverDataPaths.ImportRoot,
                new[]
                {
                    "Import Files",
                    "------------",
                    "Static import XMLs used when 'Import statics' is enabled.",
                    "Expected files:",
                    "- Format.xml (optional)",
                    "- *.xml import files (e.g., woodenbridge.xml)"
                }
            },
            {
                Path.Combine(UOMapWeaverDataPaths.ImportRoot, "Example"),
                new[]
                {
                    "Import Files/Example",
                    "--------------------",
                    "Optional example BMPs and XMLs.",
                    "Expected files:",
                    "- Terrain.bmp",
                    "- Altitude.bmp"
                }
            },
            {
                Path.Combine(UOMapWeaverDataPaths.ImportRoot, "Example", "Import"),
                new[]
                {
                    "Import Files/Example/Import",
                    "---------------------------",
                    "Optional example import XML files."
                }
            },
            {
                UOMapWeaverDataPaths.DefinitionsRoot,
                new[]
                {
                    "Definitions",
                    "-----------",
                    "Definition JSON files used by the app.",
                    "Expected files:",
                    "- map-definitions.json"
                }
            },
            {
                UOMapWeaverDataPaths.PresetsRoot,
                new[]
                {
                    "Presets",
                    "-------",
                    "Map preset JSON used by Blank BMP.",
                    "Expected files:",
                    "- map-presets.json"
                }
            },
            {
                UOMapWeaverDataPaths.TileColorsRoot,
                new[]
                {
                    "TileColors",
                    "----------",
                    "Tile color JSON outputs generated by the Tile Colors tab."
                }
            },
            {
                UOMapWeaverDataPaths.TileReplaceRoot,
                new[]
                {
                    "JsonTileReplace",
                    "---------------",
                    "Tile replacement JSON files.",
                    "Expected files:",
                    "- UOMapWeaver_TileReplace.json"
                }
            },
            {
                UOMapWeaverDataPaths.SystemRoot,
                new[]
                {
                    "System",
                    "------",
                    "Optional legacy data folder. Used only if you place files here."
                }
            },
            {
                UOMapWeaverDataPaths.LoggerRoot,
                new[]
                {
                    "Logger",
                    "------",
                    "Optional legacy logger templates and outputs."
                }
            },
            {
                UOMapWeaverDataPaths.ExportRoot,
                new[]
                {
                    "ExportUOL",
                    "---------",
                    "Optional export templates."
                }
            },
            {
                UOMapWeaverDataPaths.DeveloperRoot,
                new[]
                {
                    "Developer",
                    "---------",
                    "Optional developer reference files."
                }
            },
            {
                UOMapWeaverDataPaths.PhotoshopRoot,
                new[]
                {
                    "Photoshop",
                    "---------",
                    "Optional Photoshop swatches/palettes."
                }
            }
        };

        foreach (var (folder, lines) in entries)
        {
            var path = Path.Combine(folder, "README.txt");
            if (!force && File.Exists(path))
            {
                continue;
            }

            Directory.CreateDirectory(folder);
            File.WriteAllLines(path, lines);
        }
    }

    private sealed record MapPresetDto(string Name, int Width, int Height, bool IsSeparator);
}
