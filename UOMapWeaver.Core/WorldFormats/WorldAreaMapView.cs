// SPDX-License-Identifier: GPL-3.0-only
using UOMapWeaver.Core.TileColors;
using UOMapWeaver.Plugins;

namespace UOMapWeaver.Core.WorldFormats;

/// <summary>One world block ready to draw: a colour per land tile and their mean.</summary>
public sealed record WorldBlockColors(RgbColor[] Tiles, RgbColor Mean);

/// <summary>
/// View model of the area canvas: the area selection, the zoom/pan transform and a bounded cache of land colours
/// read through the plugin's reader. Reading is explicit (<see cref="LoadVisible"/>, meant for a worker thread);
/// drawing only uses <see cref="TryGetCached"/>.
/// </summary>
public sealed class WorldAreaMapView : IDisposable
{
    private readonly IWorldReader? _reader;
    private readonly Func<ushort, RgbColor> _colorOf;
    private readonly int _cacheLimit;
    private readonly int _loadLimit;
    private readonly object _lock = new();
    // Value null = the plugin has no block there (remembered so it is not asked again).
    private readonly Dictionary<(int, int), LinkedListNode<((int, int) Key, WorldBlockColors? Value)>> _cache = [];
    private readonly LinkedList<((int, int) Key, WorldBlockColors? Value)> _lru = new();

    public WorldAreaMapView(WorldAreaEditor editor, IWorldReader? reader, Func<ushort, RgbColor> colorOf, int cacheLimit = 4096, int loadLimit = 1024)
    {
        Editor = editor;
        _reader = reader;
        _colorOf = colorOf;
        _cacheLimit = cacheLimit;
        _loadLimit = loadLimit;
    }

    public WorldAreaEditor Editor { get; }

    /// <summary>Number of blocks asked of the reader so far.</summary>
    public int ReadCount { get; private set; }

    public int SelectedIndex { get; private set; } = -1;

    /// <summary>Pixels per world tile.</summary>
    public double Zoom { get; private set; } = 1;

    /// <summary>World tile shown at the top-left corner of the view.</summary>
    public double OriginX { get; private set; }

    public double OriginY { get; private set; }

    public void Select(int index) => SelectedIndex = index >= 0 && index < Editor.Areas.Count ? index : -1;

    public void RenameSelected(string name)
    {
        if (SelectedIndex >= 0)
        {
            Editor.Rename(SelectedIndex, name);
        }
    }

    public void DeleteSelected()
    {
        if (SelectedIndex >= 0)
        {
            Editor.Delete(SelectedIndex);
            SelectedIndex = -1;
        }
    }

    public void SetView(double originX, double originY, double zoom)
    {
        (OriginX, OriginY, Zoom) = (originX, originY, Math.Clamp(zoom, 0.01, 64));
    }

    public (double X, double Y) ToWorld(double px, double py) => (OriginX + px / Zoom, OriginY + py / Zoom);

    /// <summary>Zooms by <paramref name="factor"/> keeping the world point under the pixel fixed.</summary>
    public void ZoomAt(double px, double py, double factor)
    {
        var (wx, wy) = ToWorld(px, py);
        Zoom = Math.Clamp(Zoom * factor, 0.01, 64);
        (OriginX, OriginY) = (wx - px / Zoom, wy - py / Zoom);
    }

    public void Pan(double dxPixels, double dyPixels) => (OriginX, OriginY) = (OriginX - dxPixels / Zoom, OriginY - dyPixels / Zoom);

    /// <summary>Block step for the view: 1 when every visible block fits the load limit, else a stride that keeps the load bounded.</summary>
    public int Stride(double viewWidth, double viewHeight)
    {
        var (x0, y0, x1, y1) = BlockRange(viewWidth, viewHeight);
        var count = (long)(x1 - x0 + 1) * (y1 - y0 + 1);
        return count <= _loadLimit ? 1 : (int)Math.Ceiling(Math.Sqrt((double)count / _loadLimit));
    }

    private (int X0, int Y0, int X1, int Y1) BlockRange(double viewWidth, double viewHeight)
    {
        var (wx1, wy1) = ToWorld(viewWidth, viewHeight);
        var limX = Editor.WorldWidth > 0 ? Editor.WorldWidth / WorldBlock.Size - 1 : int.MaxValue;
        var limY = Editor.WorldHeight > 0 ? Editor.WorldHeight / WorldBlock.Size - 1 : int.MaxValue;
        return (
            Math.Max(0, (int)Math.Floor(OriginX / WorldBlock.Size)),
            Math.Max(0, (int)Math.Floor(OriginY / WorldBlock.Size)),
            Math.Min(limX, (int)Math.Floor(wx1 / WorldBlock.Size)),
            Math.Min(limY, (int)Math.Floor(wy1 / WorldBlock.Size)));
    }

    /// <summary>Block coordinates the view shows at the current stride.</summary>
    public IEnumerable<(int X, int Y)> VisibleBlocks(double viewWidth, double viewHeight)
    {
        var step = Stride(viewWidth, viewHeight);
        var (x0, y0, x1, y1) = BlockRange(viewWidth, viewHeight);
        for (var y = y0 - y0 % step; y <= y1; y += step)
        {
            for (var x = x0 - x0 % step; x <= x1; x += step)
            {
                if (x >= 0 && y >= 0)
                {
                    yield return (x, y);
                }
            }
        }
    }

    /// <summary>Reads the visible blocks not cached yet; call off the UI thread. Returns the number read.</summary>
    public int LoadVisible(double viewWidth, double viewHeight)
    {
        if (_reader is null)
        {
            return 0;
        }

        var read = 0;
        foreach (var (x, y) in VisibleBlocks(viewWidth, viewHeight))
        {
            lock (_lock)
            {
                if (_cache.ContainsKey((x, y)))
                {
                    continue;
                }

                WorldBlockColors? colors = null;
                try
                {
                    if (_reader.TryReadBlock(x, y, out var block) && block is not null)
                    {
                        colors = Convert(block);
                    }
                }
                catch (Exception)
                {
                    // An unreadable block is drawn empty, like a missing one.
                }

                ReadCount++;
                read++;
                Add((x, y), colors);
            }
        }

        return read;
    }

    /// <summary>True when the block was read before; <paramref name="colors"/> is null for a missing block.</summary>
    public bool TryGetCached(int blockX, int blockY, out WorldBlockColors? colors)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue((blockX, blockY), out var node))
            {
                colors = node.Value.Value;
                return true;
            }
        }

        colors = null;
        return false;
    }

    private WorldBlockColors Convert(WorldBlock block)
    {
        var tiles = new RgbColor[block.Land.Length];
        int r = 0, g = 0, b = 0;
        for (var i = 0; i < tiles.Length; i++)
        {
            tiles[i] = _colorOf(block.Land[i].TileId);
            (r, g, b) = (r + tiles[i].R, g + tiles[i].G, b + tiles[i].B);
        }

        return new WorldBlockColors(tiles, new RgbColor((byte)(r / tiles.Length), (byte)(g / tiles.Length), (byte)(b / tiles.Length)));
    }

    private void Add((int, int) key, WorldBlockColors? value)
    {
        _cache[key] = _lru.AddFirst((key, value));
        while (_lru.Count > _cacheLimit)
        {
            _cache.Remove(_lru.Last!.Value.Key);
            _lru.RemoveLast();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _reader?.Dispose();
        }

        Editor.Dispose();
    }
}
