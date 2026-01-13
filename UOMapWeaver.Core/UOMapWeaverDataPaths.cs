using System;
using System.IO;

namespace UOMapWeaver.Core;

public static class UOMapWeaverDataPaths
{
    public const string DataFolderName = "UOMapWeaverData";

    public static string DataRoot => Path.Combine(AppContext.BaseDirectory, DataFolderName);

    public static string SystemRoot => Path.Combine(DataRoot, "System");

    public static string MapTransRoot => Path.Combine(SystemRoot, "MapTrans");

    public static string TransitionsRoot => Path.Combine(DataRoot, "Transitions");

    public static string StaticsRoot => Path.Combine(DataRoot, "Statics");

    public static string PhotoshopRoot => Path.Combine(DataRoot, "Photoshop");

    public static string ImportRoot => Path.Combine(DataRoot, "Import Files");

    public static string LoggerRoot => Path.Combine(DataRoot, "Logger");

    public static string ExportRoot => Path.Combine(DataRoot, "ExportUOL");

    public static string DeveloperRoot => Path.Combine(DataRoot, "Developer");

    public static string TileColorsRoot => Path.Combine(DataRoot, "TileColors");

    public static string TileReplaceRoot => Path.Combine(DataRoot, "JsonTileReplace");

    public static string PalettesRoot => Path.Combine(DataRoot, "Palettes");

    public static string PresetsRoot => Path.Combine(DataRoot, "Presets");

    public static string DefinitionsRoot => Path.Combine(DataRoot, "Definitions");

    public static string ExamplesRoot => Path.Combine(DataRoot, "Examples");

    public static string MapPresetsPath => Path.Combine(PresetsRoot, "map-presets.json");

    public static string MapDefinitionsPath => Path.Combine(DefinitionsRoot, "map-definitions.json");

    public static string UiStatePath => Path.Combine(DataRoot, "ui-state.json");
}
