using System;
using System.IO;
using System.Diagnostics;

namespace UOMapWeaver.Core;

public static class UOMapWeaverDataPaths
{
    public const string DataFolderName = "UOMapWeaverData";

    public static string DataRoot => Path.Combine(GetExecutableDirectory(), DataFolderName);

    private static string GetExecutableDirectory()
    {
        var baseDirectory = AppContext.BaseDirectory;

        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                if (IsDotnetHost(processPath))
                {
                    return baseDirectory;
                }

                var directory = Path.GetDirectoryName(processPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    if (IsTempShadowCopy(baseDirectory))
                    {
                        return directory;
                    }

                    return directory;
                }
            }

            using var process = Process.GetCurrentProcess();
            var mainModulePath = process.MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(mainModulePath))
            {
                if (IsDotnetHost(mainModulePath))
                {
                    return baseDirectory;
                }

                var directory = Path.GetDirectoryName(mainModulePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    if (IsTempShadowCopy(baseDirectory))
                    {
                        return directory;
                    }

                    return directory;
                }
            }
        }
        catch
        {
            // Ignore process path failures and fall back.
        }

        return baseDirectory;
    }

    private static bool IsDotnetHost(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTempShadowCopy(string baseDirectory)
    {
        return baseDirectory.Contains($"{Path.DirectorySeparatorChar}.net{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
               baseDirectory.Contains($"{Path.DirectorySeparatorChar}Temp{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    public static string SystemRoot => Path.Combine(DataRoot, "System");

    public static string MapTransRoot => Path.Combine(DataRoot, "MapTrans");

    public static string TransitionsRoot => Path.Combine(DataRoot, "Transitions");

    public static string TemplatesRoot => Path.Combine(DataRoot, "Templates");

    public static string RoughEdgeRoot => Path.Combine(DataRoot, "RoughEdge");

    public static string TerrainTypesRoot => Path.Combine(DataRoot, "TerrainTypes");

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

    public static string MapPresetsPath => Path.Combine(PresetsRoot, "map-presets.json");

    public static string MapDefinitionsPath => Path.Combine(DefinitionsRoot, "map-definitions.json");

    public static string TerrainDefinitionsPath => Path.Combine(DataRoot, "Terrain.xml");

    public static string UiStatePath => Path.Combine(DataRoot, "ui-state.json");
}
