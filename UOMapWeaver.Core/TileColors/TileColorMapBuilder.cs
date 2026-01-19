using System.Diagnostics;
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
        IProgress<TileColorProgress>? progress = null,
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
        var lastReport = Stopwatch.StartNew();
        var lastReportedTiles = 0L;
        var lastHeartbeat = Stopwatch.StartNew();
        var lastSave = Stopwatch.StartNew();
        var lastSaveTiles = 0L;

        if (totalTiles > 0)
        {
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Info,
                $"Total tiles to scan: {totalTiles:N0}."));
        }

        if (partialSave != null)
        {
            partialSave(BuildMap());
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Info,
                "Initial JSON created."));
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

            var blockWidth = width / MapMul.BlockSize;
            var blockHeight = height / MapMul.BlockSize;
            var buffer = new byte[MapMul.LandBlockBytes];

            var totalBlocks = blockWidth * blockHeight;
            using var stream = new FileStream(
                mapPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                MapMul.LandBlockBytes * 8,
                FileOptions.SequentialScan);
            for (var blockIndex = 0; blockIndex < totalBlocks; blockIndex++)
            {
                var bx = blockIndex / blockHeight;
                var by = blockIndex % blockHeight;
                if (log != null && blockIndex % 2000 == 0)
                {
                    log(new MapConversionLogEntry(
                        MapConversionLogLevel.Info,
                        $"Reading block {blockIndex:N0}/{totalBlocks:N0} (bx={bx}, by={by})."));
                }

                cancellationToken?.ThrowIfCancellationRequested();
                stream.ReadExactly(buffer, 0, buffer.Length);

                if (log != null && blockIndex % 2000 == 0)
                {
                    log(new MapConversionLogEntry(
                        MapConversionLogLevel.Info,
                        $"Read block {blockIndex:N0}/{totalBlocks:N0} (bx={bx}, by={by})."));
                }

                var span = buffer.AsSpan(MapMul.LandHeaderBytes);
                for (var i = 0; i < MapMul.LandTilesPerBlock; i++)
                {
                    if ((i & 0x0F) == 0)
                    {
                        cancellationToken?.ThrowIfCancellationRequested();
                    }

                    var tileId = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
                        span.Slice(i * MapMul.LandTileBytes, 2));

                    var addedTile = false;
                    if (_mode == TileColorMode.Indexed8)
                    {
                        if (_tileToIndex.ContainsKey(tileId))
                        {
                            processedTiles++;
                            ReportProgress(progress, totalTiles, ref lastReport, ref lastReportedTiles, processedTiles);
                            ReportHeartbeat(log, totalTiles, ref lastHeartbeat, processedTiles, bx, by, blockWidth, blockHeight);

                            cancellationToken?.ThrowIfCancellationRequested();
                            continue;
                        }

                        if (!TryAssignIndexed(tileId, out var error))
                        {
                            LogError(log, error, stopOnError);
                            if (stopOnError)
                            {
                                return BuildMap();
                            }
                        }
                        addedTile = true;
                    }
                    else
                    {
                        if (_tileToColor.ContainsKey(tileId))
                        {
                            processedTiles++;
                            ReportProgress(progress, totalTiles, ref lastReport, ref lastReportedTiles, processedTiles);
                            ReportHeartbeat(log, totalTiles, ref lastHeartbeat, processedTiles, bx, by, blockWidth, blockHeight);

                            cancellationToken?.ThrowIfCancellationRequested();
                            continue;
                        }

                        if (!TryAssignRgb(tileId, out var error))
                        {
                            LogError(log, error, stopOnError);
                            if (stopOnError)
                            {
                                return BuildMap();
                            }
                        }
                        addedTile = true;
                    }

                    processedTiles++;
                    ReportProgress(progress, totalTiles, ref lastReport, ref lastReportedTiles, processedTiles);
                    ReportHeartbeat(log, totalTiles, ref lastHeartbeat, processedTiles, bx, by, blockWidth, blockHeight);

                    cancellationToken?.ThrowIfCancellationRequested();

                    if (addedTile && partialSave != null)
                    {
                        if (processedTiles - lastSaveTiles >= 250_000 || lastSave.ElapsedMilliseconds >= 15000)
                        {
                            partialSave(BuildMap());
                            lastSaveTiles = processedTiles;
                            lastSave.Restart();
                        }
                    }
                }
            }

            cancellationToken?.ThrowIfCancellationRequested();
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

    private static void ReportProgress(
        IProgress<TileColorProgress>? progress,
        long totalTiles,
        ref Stopwatch lastReport,
        ref long lastReportedTiles,
        long processedTiles)
    {
        if (progress == null || totalTiles <= 0)
        {
            return;
        }

        if (processedTiles - lastReportedTiles < 50000 && lastReport.ElapsedMilliseconds < 250)
        {
            return;
        }

        lastReportedTiles = processedTiles;
        lastReport.Restart();
        progress.Report(new TileColorProgress(
            processedTiles * 100.0 / totalTiles,
            processedTiles,
            totalTiles));
    }

    private static void ReportHeartbeat(
        Action<MapConversionLogEntry>? log,
        long totalTiles,
        ref Stopwatch lastHeartbeat,
        long processedTiles,
        int blockX,
        int blockY,
        int blockWidth,
        int blockHeight)
    {
        if (log == null || totalTiles <= 0)
        {
            return;
        }

        if (lastHeartbeat.ElapsedMilliseconds < 5000)
        {
            return;
        }

        var percent = processedTiles * 100.0 / totalTiles;
        var blockIndex = blockX * blockHeight + blockY;
        var totalBlocks = blockWidth * blockHeight;
        log(new MapConversionLogEntry(
            MapConversionLogLevel.Info,
            $"Tile JSON progress: {percent:0.00}% ({processedTiles:N0}/{totalTiles:N0}) block {blockIndex:N0}/{totalBlocks:N0} (bx={blockX}, by={blockY})."));
        lastHeartbeat.Restart();
    }


}
