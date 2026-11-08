// SPDX-License-Identifier: GPL-3.0-only
using System.Reflection;
using System.Runtime.Loader;
using UOMapWeaver.Plugins;

namespace UOMapWeaver.Core.Plugins;

/// <summary>Finds and loads world format plugins (ADR-0003 sections 3-4).</summary>
public static class PluginLoader
{
    /// <summary>
    /// Scans each folder for subfolders <c>&lt;Name&gt;/&lt;Name&gt;.dll</c> and loads the plugins in them.
    /// Failures are passed to <paramref name="log"/> and skipped; the first plugin with a given Id wins.
    /// </summary>
    public static IReadOnlyList<IWorldFormatPlugin> Load(IEnumerable<string> pluginFolders, Action<string>? log = null)
    {
        log ??= static _ => { };
        var result = new List<IWorldFormatPlugin>();

        foreach (var folder in pluginFolders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var dir in Directory.GetDirectories(folder).OrderBy(d => d, StringComparer.Ordinal))
            {
                var dll = Path.Combine(dir, Path.GetFileName(dir) + ".dll");
                if (!File.Exists(dll))
                {
                    continue;
                }

                try
                {
                    foreach (var plugin in LoadAssembly(dll))
                    {
                        Accept(plugin, dll, result, log);
                    }
                }
                catch (Exception ex)
                {
                    log($"Plugin '{dll}' skipped: {ex.Message}");
                }
            }
        }

        return result;
    }

    private static void Accept(IWorldFormatPlugin plugin, string dll, List<IWorldFormatPlugin> result, Action<string> log)
    {
        if (plugin.ContractVersion != WorldFormatContract.Version)
        {
            log($"Plugin '{dll}' incompatible (contract v{plugin.ContractVersion}, needs v{WorldFormatContract.Version}).");
            return;
        }

        if (result.Any(p => p.Id == plugin.Id))
        {
            log($"Plugin '{dll}' skipped: Id '{plugin.Id}' is already loaded.");
            return;
        }

        result.Add(plugin);
    }

    private static List<IWorldFormatPlugin> LoadAssembly(string dllPath)
    {
        var assembly = new PluginLoadContext(dllPath).LoadFromAssemblyPath(dllPath);
        var plugins = new List<IWorldFormatPlugin>();
        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || !typeof(IWorldFormatPlugin).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }

            plugins.Add((IWorldFormatPlugin)Activator.CreateInstance(type)!);
        }

        return plugins;
    }

    private sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

        protected override Assembly? Load(AssemblyName name)
        {
            // The contract comes from the host so IWorldFormatPlugin is the same type on both sides.
            if (name.Name == typeof(IWorldFormatPlugin).Assembly.GetName().Name)
            {
                return null;
            }

            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}
