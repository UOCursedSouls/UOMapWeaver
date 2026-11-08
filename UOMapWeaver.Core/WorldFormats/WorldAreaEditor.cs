// SPDX-License-Identifier: GPL-3.0-only
using UOMapWeaver.Core.Plugins;
using UOMapWeaver.Plugins;

namespace UOMapWeaver.Core.WorldFormats;

/// <summary>Edits the areas of a world through a plugin's <see cref="IWorldAreaStore"/>; nothing reaches the store until <see cref="Save"/>.</summary>
public sealed class WorldAreaEditor : IDisposable
{
    private readonly IWorldAreaStore _store;
    private readonly List<WorldArea> _areas;

    private WorldAreaEditor(IWorldAreaStore store, int worldWidth, int worldHeight)
    {
        _store = store;
        _areas = store.ReadAll().ToList();
        WorldWidth = worldWidth;
        WorldHeight = worldHeight;
    }

    /// <summary>The editor, or null when the plugin has no Areas capability (the UI then hides its area tools).</summary>
    public static WorldAreaEditor? Open(IWorldFormatPlugin plugin, string worldFolder)
    {
        var areaPlugin = PluginLoader.GetAreaPlugin(plugin);
        if (areaPlugin is null)
        {
            return null;
        }

        int w = 0, h = 0;
        try
        {
            using var reader = plugin.OpenWorld(worldFolder);
            (w, h) = (reader.Info.WidthInBlocks * WorldBlock.Size, reader.Info.HeightInBlocks * WorldBlock.Size);
        }
        catch (Exception)
        {
            // Size is only a drawing hint; an unreadable block store must not stop area editing.
        }

        return new WorldAreaEditor(areaPlugin.OpenAreas(worldFolder), w, h);
    }

    public IReadOnlyList<string> Kinds => _store.Kinds;

    public IReadOnlyList<WorldArea> Areas => _areas;

    /// <summary>World size in tiles; 0 x 0 when unbounded or unknown.</summary>
    public int WorldWidth { get; }

    public int WorldHeight { get; }

    public bool Dirty { get; private set; }

    public int Create(string kind, string name, WorldAreaRect rect)
    {
        if (!Kinds.Contains(kind))
        {
            throw new ArgumentException($"Unknown area kind '{kind}'.", nameof(kind));
        }

        Check(rect);
        _areas.Add(new WorldArea(null, name, kind, null, [rect]));
        Dirty = true;
        return _areas.Count - 1;
    }

    /// <summary>Replaces one rectangle: used to move and to resize.</summary>
    public void SetRect(int areaIndex, int rectIndex, WorldAreaRect rect)
    {
        Check(rect);
        var a = _areas[areaIndex];
        var rects = a.Rects.ToList();
        rects[rectIndex] = rect;
        _areas[areaIndex] = a with { Rects = rects };
        Dirty = true;
    }

    public void Rename(int areaIndex, string name)
    {
        _areas[areaIndex] = _areas[areaIndex] with { Name = name };
        Dirty = true;
    }

    /// <summary>Deletes an area; its children move up to its parent.</summary>
    public void Delete(int areaIndex)
    {
        var gone = _areas[areaIndex];
        _areas.RemoveAt(areaIndex);
        if (gone.Id is not null)
        {
            for (var i = 0; i < _areas.Count; i++)
            {
                if (_areas[i].ParentId == gone.Id)
                {
                    _areas[i] = _areas[i] with { ParentId = gone.ParentId };
                }
            }
        }

        Dirty = true;
    }

    public void Save()
    {
        _store.WriteAll(_areas.ToList());
        // Ids of new areas are assigned by the plugin: reload so a second save does not create duplicates.
        _areas.Clear();
        _areas.AddRange(_store.ReadAll());
        Dirty = false;
    }

    public void Dispose() => _store.Dispose();

    private static void Check(WorldAreaRect r)
    {
        if (r.Width <= 0 || r.Height <= 0)
        {
            throw new ArgumentException("An area rectangle needs a width and a height above 0.");
        }
    }
}
