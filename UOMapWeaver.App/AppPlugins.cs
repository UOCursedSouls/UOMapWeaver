using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UOMapWeaver.Core.Plugins;
using UOMapWeaver.Plugins;

namespace UOMapWeaver.App;

/// <summary>World format plugins found at startup: <c>plugins/</c> next to the executable plus the <c>PluginFolders</c> setting.</summary>
public static class AppPlugins
{
    public static IReadOnlyList<IWorldFormatPlugin> WorldFormats { get; private set; } = Array.Empty<IWorldFormatPlugin>();

    public static void Load()
    {
        try
        {
            var folders = new[] { Path.Combine(AppContext.BaseDirectory, "plugins") }
                .Concat(AppSettings.GetList("PluginFolders"));
            WorldFormats = PluginLoader.Load(folders, m => AppStatus.AppendLog(m, AppStatusSeverity.Warning));
        }
        catch (Exception ex)
        {
            AppStatus.AppendLog($"Plugin loading failed: {ex.Message}", AppStatusSeverity.Warning);
        }
    }
}
