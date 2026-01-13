using System.Collections.Generic;

namespace UOMapWeaver.App.Defaults;

internal static class MapDefaults
{
    internal static IReadOnlyList<MapPresetDefinition> Presets { get; } = new List<MapPresetDefinition>
    {
        new("Felucca (896x512) | (7168x4096)", 7168, 4096),
        new("Felucca (768x512) | (6144x4096)", 6144, 4096),
        MapPresetDefinition.Separator(),
        new("Trammel (896x512) | (7168x4096)", 7168, 4096),
        new("Trammel (768x512) | (6144x4096)", 6144, 4096),
        MapPresetDefinition.Separator(),
        new("Ilshenar (288x200) | (2304x1600)", 2304, 1600),
        MapPresetDefinition.Separator(),
        new("Malas (320x256) | (2560x2048)", 2560, 2048),
        MapPresetDefinition.Separator(),
        new("Tokuno (181x181) | (1448x1448)", 1448, 1448),
        MapPresetDefinition.Separator(),
        new("Ter Mur (160x512) | (1280x4096)", 1280, 4096),
        MapPresetDefinition.Separator(),
        new("Custom_1 (896x896) | (7168x7168)", 7168, 7168),
        new("Custom_OLs (1338x836) | (10704x6688)", 10704, 6688),
        new("Custom_2 (1250x895) | (10000x7160)", 10000, 7160),
        new("Custom_3 (1875x1500) | (15000x12000)", 15000, 12000),
        new("UOCS_Anima (2000x1875) | (16000x15000)", 16000, 15000),
        new("Custom_4 (3000x2500) | (24000x20000)", 24000, 20000)
    };

    internal sealed record MapPresetDefinition(string Name, int Width, int Height, bool IsSeparator = false)
    {
        public static MapPresetDefinition Separator()
            => new("--------------------------------------------------------", 0, 0, true);
    }
}
