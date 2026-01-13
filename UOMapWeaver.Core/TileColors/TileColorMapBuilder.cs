using UOMapWeaver.Core.Bmp;
using UOMapWeaver.Core.Map;

namespace UOMapWeaver.Core.TileColors;

public sealed class TileColorMapBuilder
{
    private const byte UnknownIndex = 255;
    private static readonly RgbColor UnknownColor = new(255, 0, 255);

    private readonly TileColorMode _mode;
    private readonly Dictionary<ushort, byte> _tileToIndex;
    private readonly Dictionary<ushort, RgbColor> _tileToColor;
    private readonly HashSet<byte> _usedIndices;
    private readonly HashSet<int> _usedColors;
    private readonly BmpPaletteEntry[]? _palette;
    private int _colorSeed;

    public TileColorMapBuilder(TileColorMode mode, TileColorMap? existing)
    {
        _mode = mode;
        _tileToIndex = existing?.TileToIndex != null
            ? new Dictionary<ushort, byte>(existing.TileToIndex)
            : new Dictionary<ushort, byte>();
        _tileToColor = existing?.TileToColor != null
            ? new Dictionary<ushort, RgbColor>(existing.TileToColor)
            : new Dictionary<ushort, RgbColor>();
        _usedIndices = existing?.TileToIndex != null
            ? new HashSet<byte>(existing.TileToIndex.Values)
            : new HashSet<byte>();
        _usedColors = new HashSet<int>();

        if (existing?.Palette != null)
        {
            _palette = existing.Palette.Select(entry => entry).ToArray();
            foreach (var entry in existing.Palette)
            {
                _usedColors.Add((entry.Red << 16) | (entry.Green << 8) | entry.Blue);
            }
        }
        else if (mode == TileColorMode.Indexed8)
        {
            _palette = Bmp8Codec.CreateGrayscalePalette();
        }

        if (existing?.TileToColor != null)
        {
            foreach (var color in existing.TileToColor.Values)
            {
                _usedColors.Add(color.Key);
            }
        }

        _usedColors.Add(UnknownColor.Key);
        _usedIndices.Add(UnknownIndex);
    }

    public TileColorMap Build(
        IEnumerable<string> mapMulPaths,
        Action<MapConversionLogEntry>? log = null,
        bool stopOnError = false,
        IProgress<int>? progress = null,
        CancellationToken? cancellationToken = null,
        Action<TileColorMap>? partialSave = null
    )
    {
        var sizeByPath = new Dictionary<string, (int width, int height)?>();
        long totalTiles = 0;

        foreach (var mapPath in mapMulPaths)
        {
            if (!File.Exists(mapPath))
            {
                sizeByPath[mapPath] = null;
                continue;
            }

            if (MapConversion.TryResolveMapSizeFromFile(mapPath, out var width, out var height))
            {
                sizeByPath[mapPath] = (width, height);
                totalTiles += (long)width * height;
            }
            else
            {
                sizeByPath[mapPath] = null;
            }
        }

        long processedTiles = 0;
        var lastPercent = -1;

        if (totalTiles > 0)
        {
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Info,
                $"Total tiles to scan: {totalTiles:N0}."));
        }

        foreach (var mapPath in mapMulPaths)
        {
            cancellationToken?.ThrowIfCancellationRequested();
            if (!File.Exists(mapPath))
            {
                LogError(log, $"Map.mul not found: {mapPath}", stopOnError);
                continue;
            }

            if (!sizeByPath.TryGetValue(mapPath, out var size) || size == null)
            {
                LogError(log, $"Map size not detected for {mapPath}.", stopOnError);
                continue;
            }

            var width = size.Value.width;
            var height = size.Value.height;
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Info,
                $"Scanning {Path.GetFileName(mapPath)} ({width}x{height})."));

            using var reader = new MapMulRowReader(mapPath, width, height);
            var row = new LandTile[width];
            for (var y = 0; y < height; y++)
            {
                cancellationToken?.ThrowIfCancellationRequested();
                reader.ReadRow(y, row);

                for (var x = 0; x < width; x++)
                {
                    var tile = row[x];
                    if (_mode == TileColorMode.Indexed8)
                    {
                        if (_tileToIndex.ContainsKey(tile.TileId))
                        {
                            continue;
                        }

                        if (!TryAssignIndexed(tile.TileId, out var error))
                        {
                            LogError(log, error, stopOnError);
                            if (stopOnError)
                            {
                                return BuildMap();
                            }
                        }
                    }
                    else
                    {
                        if (_tileToColor.ContainsKey(tile.TileId))
                        {
                            continue;
                        }

                        if (!TryAssignRgb(tile.TileId, out var error))
                        {
                            LogError(log, error, stopOnError);
                            if (stopOnError)
                            {
                                return BuildMap();
                            }
                        }
                    }
                }

                if (progress != null && totalTiles > 0)
                {
                    processedTiles += width;
                    var percent = (int)(processedTiles * 100 / totalTiles);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress.Report(percent);
                    }
                }
            }

            if (partialSave != null)
            {
                partialSave(BuildMap());
                log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Info,
                    $"Partial JSON saved after {Path.GetFileName(mapPath)}."));
            }
        }

        return BuildMap();
    }

    private TileColorMap BuildMap()
    {
        var indexToTile = new Dictionary<byte, ushort>();
        var colorToTile = new Dictionary<int, ushort>();

        foreach (var pair in _tileToIndex)
        {
            if (!indexToTile.ContainsKey(pair.Value))
            {
                indexToTile[pair.Value] = pair.Key;
            }
        }

        foreach (var pair in _tileToColor)
        {
            if (!colorToTile.ContainsKey(pair.Value.Key))
            {
                colorToTile[pair.Value.Key] = pair.Key;
            }
        }

        if (_palette != null)
        {
            _palette[UnknownIndex] = new BmpPaletteEntry(UnknownColor.B, UnknownColor.G, UnknownColor.R, 0);
        }

        return new TileColorMap(_mode, _tileToIndex, _tileToColor, indexToTile, colorToTile, _palette, UnknownColor);
    }

    private bool TryAssignIndexed(ushort tileId, out string error)
    {
        error = string.Empty;
        var index = NextFreeIndex();
        if (!index.HasValue)
        {
            error = $"No palette slots left (max 255). Tile 0x{tileId:X4} skipped.";
            return false;
        }

        var color = NextColor();
        _tileToIndex[tileId] = index.Value;
        _usedIndices.Add(index.Value);

        if (_palette != null)
        {
            _palette[index.Value] = new BmpPaletteEntry(color.B, color.G, color.R, 0);
        }

        return true;
    }

    private bool TryAssignRgb(ushort tileId, out string error)
    {
        error = string.Empty;
        var color = NextColor();
        _tileToColor[tileId] = color;
        _usedColors.Add(color.Key);
        return true;
    }

    private byte? NextFreeIndex()
    {
        for (byte i = 0; i < 255; i++)
        {
            if (!_usedIndices.Contains(i))
            {
                return i;
            }
        }

        return null;
    }

    private RgbColor NextColor()
    {
        while (true)
        {
            var r = (byte)((_colorSeed * 47) % 256);
            var g = (byte)((_colorSeed * 97) % 256);
            var b = (byte)((_colorSeed * 151) % 256);
            _colorSeed++;
            var color = new RgbColor(r, g, b);
            if (!_usedColors.Contains(color.Key) && color.Key != UnknownColor.Key)
            {
                _usedColors.Add(color.Key);
                return color;
            }
        }
    }

    private static void LogError(Action<MapConversionLogEntry>? log, string message, bool stopOnError)
    {
        log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Error, message));
        if (stopOnError)
        {
            throw new MapConversionAbortException(message);
        }
    }
}
