using UOMapWeaver.Core;
using UOMapWeaver.Core.Bmp;
using UOMapWeaver.Core.MapTrans;
using UOMapWeaver.Core.Statics;
using UOMapWeaver.Core.TileColors;
using UOMapWeaver.Core.TileReplace;

namespace UOMapWeaver.Core.Map;

public static class MapConversion
{
    private const byte UnknownTerrainIndex = 255;

    public static (Bmp8Image terrain, Bmp8Image altitude, MapConversionReport report) ConvertMulToBmp(
        string mapMulPath,
        int width,
        int height,
        MapTransProfile profile,
        IProgress<int>? progress = null,
        Action<MapConversionLogEntry>? log = null,
        MapConversionOptions? options = null
    )
    {
        options ??= new MapConversionOptions();
        var tileColorMap = options.TileColorMap;
        var tiles = MapMulCodec.ReadLandTiles(mapMulPath, width, height);
        var terrainPixels = new byte[width * height];
        var altitudePixels = new byte[width * height];
        var report = new MapConversionReport(width * height);

        var terrainPalette = tileColorMap?.Mode == TileColorMode.Indexed8
            ? tileColorMap.Palette ?? Bmp8Codec.CreateGrayscalePalette()
            : LoadTerrainPalette(profile);
        var altitudePalette = Bmp8Codec.CreateGrayscalePalette();

        var lookup = tileColorMap?.Mode == TileColorMode.Indexed8 ? null : BuildTileLookup(profile);
        var sortedTileIds = lookup is null
            ? Array.Empty<ushort>()
            : lookup.Keys.OrderBy(key => key).ToArray();
        var nearestCache = lookup is null ? null : new Dictionary<ushort, ushort>();

        var lastProgress = -1;
        var loggedErrors = 0;
        var suppressedErrors = 0;
        var suppressionLogged = false;
        for (var y = 0; y < height; y++)
        {
            options.CancellationToken?.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var tile = tiles[y * width + x];
                var tileId = ApplyTerrainReplacement(tile.TileId, options.TileReplacementMap);
                var resolved = tileColorMap?.Mode == TileColorMode.Indexed8
                    ? tileColorMap.TryGetColorIndex(tileId, out var colorIndex)
                    : TryResolveTerrainColor(lookup!, tileId, tile.Z, out colorIndex, sortedTileIds, nearestCache!, report);

                if (resolved)
                {
                    terrainPixels[y * width + x] = colorIndex;
                }
                else
                {
                    terrainPixels[y * width + x] = UnknownTerrainIndex;
                    report.MissingTerrainColors++;
                    report.AddMissingColor(tileId);
                    var message = $"Missing terrain color at ({x},{y}) tile=0x{tileId:X4} z={tile.Z}; using transparent index {UnknownTerrainIndex}.";
                    if (options.StopOnError)
                    {
                        log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Error, message));
                        throw new MapConversionAbortException(message);
                    }

                    if (log != null && options.MaxErrorLogs > 0)
                    {
                        if (loggedErrors < options.MaxErrorLogs)
                        {
                            log.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Error, message));
                            loggedErrors++;
                        }
                        else
                        {
                            suppressedErrors++;
                            if (!suppressionLogged)
                            {
                                suppressionLogged = true;
                                log.Invoke(new MapConversionLogEntry(
                                    MapConversionLogLevel.Warning,
                                    $"Too many missing color errors. Suppressing further error logs (limit {options.MaxErrorLogs})."
                                ));
                            }
                        }
                    }
                }
                altitudePixels[y * width + x] = EncodeAltitude(tile.Z);
            }

            ReportProgress(progress, y + 1, height, ref lastProgress);
        }

        ApplyUnknownColor(terrainPalette);
        var terrain = new Bmp8Image(width, height, terrainPixels, terrainPalette);
        var altitude = new Bmp8Image(width, height, altitudePixels, altitudePalette);

        if (suppressedErrors > 0)
        {
            log?.Invoke(new MapConversionLogEntry(
                MapConversionLogLevel.Warning,
                $"Suppressed {suppressedErrors:N0} additional missing color errors."
            ));
        }

        return (terrain, altitude, report);
    }

    public static MapConversionReport ConvertMulToBmpToFile(
        string mapMulPath,
        int width,
        int height,
        MapTransProfile profile,
        string terrainPath,
        string altitudePath,
        IProgress<int>? progress = null,
        Action<MapConversionLogEntry>? log = null,
        MapConversionOptions? options = null
    )
    {
        options ??= new MapConversionOptions();
        var tileColorMap = options.TileColorMap;
        var report = new MapConversionReport(width * height);

        var terrainPalette = tileColorMap?.Mode == TileColorMode.Indexed8
            ? tileColorMap.Palette ?? Bmp8Codec.CreateGrayscalePalette()
            : LoadTerrainPalette(profile);
        var altitudePalette = Bmp8Codec.CreateGrayscalePalette();

        ApplyUnknownColor(terrainPalette);
        var lookup = tileColorMap?.Mode == TileColorMode.Indexed8 ? null : BuildTileLookup(profile);
        var sortedTileIds = lookup is null
            ? Array.Empty<ushort>()
            : lookup.Keys.OrderBy(key => key).ToArray();
        var nearestCache = lookup is null ? null : new Dictionary<ushort, ushort>();

        var loggedErrors = 0;
        var suppressedErrors = 0;
        var suppressionLogged = false;
        var lastProgress = -1;

        using var reader = new MapMulRowReader(mapMulPath, width, height);
        using var terrainWriter = new Bmp8StreamWriter(terrainPath, width, height, terrainPalette);
        using var altitudeWriter = new Bmp8StreamWriter(altitudePath, width, height, altitudePalette);

        var tileRow = new LandTile[width];
        var terrainRow = new byte[width];
        var altitudeRow = new byte[width];

        for (var y = height - 1; y >= 0; y--)
        {
            options.CancellationToken?.ThrowIfCancellationRequested();
            reader.ReadRow(y, tileRow);

            for (var x = 0; x < width; x++)
            {
                var tile = tileRow[x];
                var tileId = ApplyTerrainReplacement(tile.TileId, options.TileReplacementMap);
                var resolved = tileColorMap?.Mode == TileColorMode.Indexed8
                    ? tileColorMap.TryGetColorIndex(tileId, out var colorIndex)
                    : TryResolveTerrainColor(lookup!, tileId, tile.Z, out colorIndex, sortedTileIds, nearestCache!, report);

                if (resolved)
                {
                    terrainRow[x] = colorIndex;
                }
                else
                {
                    terrainRow[x] = UnknownTerrainIndex;
                    report.MissingTerrainColors++;
                    report.AddMissingColor(tileId);
                    var message = $"Missing terrain color at ({x},{y}) tile=0x{tileId:X4} z={tile.Z}; using transparent index {UnknownTerrainIndex}.";
                    if (options.StopOnError)
                    {
                        log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Error, message));
                        throw new MapConversionAbortException(message);
                    }

                    if (log != null && options.MaxErrorLogs > 0)
                    {
                        if (loggedErrors < options.MaxErrorLogs)
                        {
                            log.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Error, message));
                            loggedErrors++;
                        }
                        else
                        {
                            suppressedErrors++;
                            if (!suppressionLogged)
                            {
                                suppressionLogged = true;
                                log.Invoke(new MapConversionLogEntry(
                                    MapConversionLogLevel.Warning,
                                    $"Too many missing color errors. Suppressing further error logs (limit {options.MaxErrorLogs})."
                                ));
                            }
                        }
                    }
                }

                altitudeRow[x] = EncodeAltitude(tile.Z);
            }

            terrainWriter.WriteRow(terrainRow);
            altitudeWriter.WriteRow(altitudeRow);

            ReportProgress(progress, height - y, height, ref lastProgress);
        }

        if (suppressedErrors > 0)
        {
            log?.Invoke(new MapConversionLogEntry(
                MapConversionLogLevel.Warning,
                $"Suppressed {suppressedErrors:N0} additional missing color errors."
            ));
        }

        return report;
    }

    public static (LandTile[] tiles, MapConversionReport report) ConvertBmpToMul(
        Bmp8Image terrain,
        Bmp8Image altitude,
        MapTransProfile profile,
        IProgress<int>? progress = null,
        Action<MapConversionLogEntry>? log = null,
        MapConversionOptions? options = null
    )
    {
        options ??= new MapConversionOptions();
        var tileColorMap = options.TileColorMap;
        if (terrain.Width != altitude.Width || terrain.Height != altitude.Height)
        {
            throw new InvalidOperationException("Terrain and altitude images must match in size.");
        }

        var width = terrain.Width;
        var height = terrain.Height;
        var tiles = new LandTile[width * height];
        var report = new MapConversionReport(width * height);

        var lastProgress = -1;
        var loggedErrors = 0;
        var suppressedErrors = 0;
        var suppressionLogged = false;
        for (var y = 0; y < height; y++)
        {
            options.CancellationToken?.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                var terrainIndex = terrain.Pixels[index];
                var altitudeIndex = altitude.Pixels[index];
                var z = DecodeAltitude(altitudeIndex);

                var resolved = tileColorMap?.Mode == TileColorMode.Indexed8
                    ? tileColorMap.TryGetTileId(terrainIndex, out var tileId)
                    : TryResolveTerrainTile(profile, terrainIndex, z, x, y, out tileId);

                if (!resolved)
                {
                    report.MissingTerrainTiles++;
                    report.AddMissingTile(terrainIndex);
                    var message = $"Missing terrain tile at ({x},{y}) color=0x{terrainIndex:X2} z={z}; using tileId 0x{tileId:X4}.";
                    if (options.StopOnError)
                    {
                        log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Error, message));
                        throw new MapConversionAbortException(message);
                    }

                    if (log != null && options.MaxErrorLogs > 0)
                    {
                        if (loggedErrors < options.MaxErrorLogs)
                        {
                            log.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Error, message));
                            loggedErrors++;
                        }
                        else
                        {
                            suppressedErrors++;
                            if (!suppressionLogged)
                            {
                                suppressionLogged = true;
                                log.Invoke(new MapConversionLogEntry(
                                    MapConversionLogLevel.Warning,
                                    $"Too many missing tile errors. Suppressing further error logs (limit {options.MaxErrorLogs})."
                                ));
                            }
                        }
                    }
                }

                tiles[index] = new LandTile(tileId, z);
            }

            ReportProgress(progress, y + 1, height, ref lastProgress);
        }

        if (suppressedErrors > 0)
        {
            log?.Invoke(new MapConversionLogEntry(
                MapConversionLogLevel.Warning,
                $"Suppressed {suppressedErrors:N0} additional missing tile errors."
            ));
        }

        return (tiles, report);
    }

    public static MapConversionReport ConvertBmpToMulFromFiles(
        string terrainPath,
        string altitudePath,
        MapTransProfile profile,
        string mapMulPath,
        IProgress<int>? progress = null,
        Action<MapConversionLogEntry>? log = null,
        MapConversionOptions? options = null
    )
    {
        options ??= new MapConversionOptions();
        var tileColorMap = options.TileColorMap;

        using var terrainReader = new Bmp8StreamReader(terrainPath);
        using var altitudeReader = new Bmp8StreamReader(altitudePath);

        if (terrainReader.Width != altitudeReader.Width || terrainReader.Height != altitudeReader.Height)
        {
            throw new InvalidOperationException("Terrain and altitude images must match in size.");
        }

        var width = terrainReader.Width;
        var height = terrainReader.Height;
        var report = new MapConversionReport(width * height);

        var loggedErrors = 0;
        var suppressedErrors = 0;
        var suppressionLogged = false;
        var lastProgress = -1;
        var rowsProcessed = 0;

        var terrainRow = new byte[width];
        var altitudeRow = new byte[width];

        MapMulCodec.WriteLandTilesFromRows(mapMulPath, width, height, (y, rowSpan) =>
        {
            options.CancellationToken?.ThrowIfCancellationRequested();
            terrainReader.ReadRow(y, terrainRow);
            altitudeReader.ReadRow(y, altitudeRow);

            for (var x = 0; x < width; x++)
            {
                var terrainIndex = terrainRow[x];
                var z = DecodeAltitude(altitudeRow[x]);

                var resolved = tileColorMap?.Mode == TileColorMode.Indexed8
                    ? tileColorMap.TryGetTileId(terrainIndex, out var tileId)
                    : TryResolveTerrainTile(profile, terrainIndex, z, x, y, out tileId);

                if (!resolved)
                {
                    report.MissingTerrainTiles++;
                    report.AddMissingTile(terrainIndex);
                    var message = $"Missing terrain tile at ({x},{y}) color=0x{terrainIndex:X2} z={z}; using tileId 0x{tileId:X4}.";
                    if (options.StopOnError)
                    {
                        log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Error, message));
                        throw new MapConversionAbortException(message);
                    }

                    if (log != null && options.MaxErrorLogs > 0)
                    {
                        if (loggedErrors < options.MaxErrorLogs)
                        {
                            log.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Error, message));
                            loggedErrors++;
                        }
                        else
                        {
                            suppressedErrors++;
                            if (!suppressionLogged)
                            {
                                suppressionLogged = true;
                                log.Invoke(new MapConversionLogEntry(
                                    MapConversionLogLevel.Warning,
                                    $"Too many missing tile errors. Suppressing further error logs (limit {options.MaxErrorLogs})."
                                ));
                            }
                        }
                    }
                }

                rowSpan[x] = new LandTile(tileId, z);
            }

            rowsProcessed++;
            ReportProgress(progress, rowsProcessed, height, ref lastProgress);
        });

        if (suppressedErrors > 0)
        {
            log?.Invoke(new MapConversionLogEntry(
                MapConversionLogLevel.Warning,
                $"Suppressed {suppressedErrors:N0} additional missing tile errors."
            ));
        }

        return report;
    }

    public static MapConversionReport ConvertTerrainXmlBmpToMulFromFiles(
        string terrainPath,
        string altitudePath,
        string mapMulPath,
        string terrainXmlPath,
        string transitionsRoot,
        IProgress<int>? progress = null,
        Action<MapConversionLogEntry>? log = null,
        MapConversionOptions? options = null
    )
    {
        options ??= new MapConversionOptions();
        using var terrainReader = new Bmp24StreamReader(terrainPath);
        using var altitudeReader = new Bmp8StreamReader(altitudePath);

        if (terrainReader.Width != altitudeReader.Width || terrainReader.Height != altitudeReader.Height)
        {
            throw new InvalidOperationException("Terrain and altitude images must match in size.");
        }

        var width = terrainReader.Width;
        var height = terrainReader.Height;
        var report = new MapConversionReport(width * height);
        var catalog = TerrainTransitionCatalog.Load(terrainXmlPath, transitionsRoot, log);
        var altitudeCatalog = AltitudeCatalog.Load(Path.Combine(UOMapWeaverDataPaths.DataRoot, "Altitude.xml"), log);

        var loggedErrors = 0;
        var suppressedErrors = 0;
        var suppressionLogged = false;
        var lastProgress = -1;
        var rowsProcessed = 0;

        var terrainRow = new byte[width * 3];
        var altitudeRow = new byte[width];
        var prevTerrainIds = new byte[width];
        var currTerrainIds = new byte[width];
        var nextTerrainIds = new byte[width];
        var altitudeByIndex = BuildAltitudeByIndex(altitudeReader.Palette, altitudeCatalog, log, options, report,
            ref loggedErrors, ref suppressedErrors, ref suppressionLogged);

        var hasAnyTerrain = catalog.TerrainById.Count > 0;
        if (!hasAnyTerrain)
        {
            log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Warning,
                "No Terrain definitions loaded. Output will be filled with tileId 0."));
        }

        if (height > 0)
        {
            ReadTerrainRowIds(terrainReader, catalog, 0, currTerrainIds, terrainRow, report, log,
                ref loggedErrors, ref suppressedErrors, ref suppressionLogged, options);

            if (height > 1)
            {
                ReadTerrainRowIds(terrainReader, catalog, 1, nextTerrainIds, terrainRow, report, log,
                    ref loggedErrors, ref suppressedErrors, ref suppressionLogged, options);
            }
            else
            {
                Array.Copy(currTerrainIds, nextTerrainIds, currTerrainIds.Length);
            }
        }
        Array.Copy(currTerrainIds, prevTerrainIds, currTerrainIds.Length);

        MapMulCodec.WriteLandTilesFromRows(mapMulPath, width, height, (y, rowSpan) =>
        {
            options.CancellationToken?.ThrowIfCancellationRequested();
            altitudeReader.ReadRow(y, altitudeRow);

            if (y > 0)
            {
                Array.Copy(currTerrainIds, prevTerrainIds, currTerrainIds.Length);
                Array.Copy(nextTerrainIds, currTerrainIds, nextTerrainIds.Length);

                if (y + 1 < height)
                {
                    ReadTerrainRowIds(terrainReader, catalog, y + 1, nextTerrainIds, terrainRow, report, log,
                        ref loggedErrors, ref suppressedErrors, ref suppressionLogged, options);
                }
                else
                {
                    Array.Copy(currTerrainIds, nextTerrainIds, currTerrainIds.Length);
                }
            }

            for (var x = 0; x < width; x++)
            {
                var z = altitudeByIndex[altitudeRow[x]];
                var selection = ResolveTerrainTransitionSelection(catalog, prevTerrainIds, currTerrainIds, nextTerrainIds, x, y);
                var adjustedZ = (sbyte)Math.Clamp(z + selection.MapTile.AltitudeMod, sbyte.MinValue, sbyte.MaxValue);
                var tileId = ApplyTerrainReplacement(selection.MapTile.TileId, options.TileReplacementMap);

                if (tileId == 0 && hasAnyTerrain)
                {
                    report.MissingTerrainTiles++;
                }

                rowSpan[x] = new LandTile(tileId, adjustedZ);
            }

            rowsProcessed++;
            ReportProgress(progress, rowsProcessed, height, ref lastProgress);
        });

        if (suppressedErrors > 0)
        {
            log?.Invoke(new MapConversionLogEntry(
                MapConversionLogLevel.Warning,
                $"Suppressed {suppressedErrors:N0} additional missing terrain color errors."
            ));
        }

        return report;
    }

    public static MapConversionReport ConvertTerrainXmlBmpToMulFromFilesWithStatics(
        string terrainPath,
        string altitudePath,
        string mapMulPath,
        string staidxPath,
        string staticsPath,
        string terrainXmlPath,
        string transitionsRoot,
        StaticsLayout layout,
        IProgress<int>? progress = null,
        Action<MapConversionLogEntry>? log = null,
        MapConversionOptions? options = null)
    {
        options ??= new MapConversionOptions();
        if (!BmpCodec.TryReadInfo(terrainPath, out var width, out var height, out _))
        {
            throw new InvalidOperationException($"Terrain.bmp not readable: {terrainPath}");
        }

        var report = ConvertTerrainXmlBmpToMulFromFiles(
            terrainPath,
            altitudePath,
            mapMulPath,
            terrainXmlPath,
            transitionsRoot,
            progress,
            log,
            options);

        var blocks = GenerateTransitionStaticsFromTerrainXml(
            terrainPath,
            altitudePath,
            terrainXmlPath,
            transitionsRoot,
            layout,
            out var placedStatics,
            progress,
            log,
            options);

        StaticMulCodec.WriteStatics(staidxPath, staticsPath, width, height, blocks);
        log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Info,
            $"Transition statics placed: {placedStatics:N0}."));

        return report;
    }

    public static List<StaticMulEntry>[] GenerateTransitionStaticsFromTerrainXml(
        string terrainPath,
        string altitudePath,
        string terrainXmlPath,
        string transitionsRoot,
        StaticsLayout layout,
        out int placedStatics,
        IProgress<int>? progress = null,
        Action<MapConversionLogEntry>? log = null,
        MapConversionOptions? options = null)
    {
        options ??= new MapConversionOptions();
        placedStatics = 0;

        using var terrainReader = new Bmp24StreamReader(terrainPath);
        using var altitudeReader = new Bmp8StreamReader(altitudePath);

        if (terrainReader.Width != altitudeReader.Width || terrainReader.Height != altitudeReader.Height)
        {
            throw new InvalidOperationException("Terrain and altitude images must match in size.");
        }

        var width = terrainReader.Width;
        var height = terrainReader.Height;
        var catalog = TerrainTransitionCatalog.Load(terrainXmlPath, transitionsRoot, log);
        var altitudeCatalog = AltitudeCatalog.Load(Path.Combine(UOMapWeaverDataPaths.DataRoot, "Altitude.xml"), log);

        var loggedErrors = 0;
        var suppressedErrors = 0;
        var suppressionLogged = false;
        var lastProgress = -1;
        var rowsProcessed = 0;

        var terrainRow = new byte[width * 3];
        var altitudeRow = new byte[width];
        var prevTerrainIds = new byte[width];
        var currTerrainIds = new byte[width];
        var nextTerrainIds = new byte[width];
        var report = new MapConversionReport(width * height);
        var altitudeByIndex = BuildAltitudeByIndex(altitudeReader.Palette, altitudeCatalog, log, options, report,
            ref loggedErrors, ref suppressedErrors, ref suppressionLogged);

        if (height > 0)
        {
            ReadTerrainRowIds(terrainReader, catalog, 0, currTerrainIds, terrainRow, report, log,
                ref loggedErrors, ref suppressedErrors, ref suppressionLogged, options);

            if (height > 1)
            {
                ReadTerrainRowIds(terrainReader, catalog, 1, nextTerrainIds, terrainRow, report, log,
                    ref loggedErrors, ref suppressedErrors, ref suppressionLogged, options);
            }
            else
            {
                Array.Copy(currTerrainIds, nextTerrainIds, currTerrainIds.Length);
            }
        }
        Array.Copy(currTerrainIds, prevTerrainIds, currTerrainIds.Length);

        var blockWidth = width / MapMul.BlockSize;
        var blockHeight = height / MapMul.BlockSize;
        var blocks = new List<StaticMulEntry>[blockWidth * blockHeight];

        for (var y = 0; y < height; y++)
        {
            options.CancellationToken?.ThrowIfCancellationRequested();
            altitudeReader.ReadRow(y, altitudeRow);

            if (y > 0)
            {
                Array.Copy(currTerrainIds, prevTerrainIds, currTerrainIds.Length);
                Array.Copy(nextTerrainIds, currTerrainIds, nextTerrainIds.Length);

                if (y + 1 < height)
                {
                    ReadTerrainRowIds(terrainReader, catalog, y + 1, nextTerrainIds, terrainRow, report, log,
                        ref loggedErrors, ref suppressedErrors, ref suppressionLogged, options);
                }
                else
                {
                    Array.Copy(currTerrainIds, nextTerrainIds, currTerrainIds.Length);
                }
            }

            for (var x = 0; x < width; x++)
            {
                var selection = ResolveTerrainTransitionSelection(catalog, prevTerrainIds, currTerrainIds, nextTerrainIds, x, y);
                if (selection.StaticTile is null)
                {
                    continue;
                }

                var tileId = ApplyStaticReplacement(selection.StaticTile.TileId, options.TileReplacementMap);
                if (tileId == 0)
                {
                    continue;
                }

                var z = altitudeByIndex[altitudeRow[x]];
                var adjustedZ = (sbyte)Math.Clamp(z + selection.StaticTile.AltitudeMod, sbyte.MinValue, sbyte.MaxValue);

                var blockX = x / MapMul.BlockSize;
                var blockY = y / MapMul.BlockSize;
                var localX = (byte)(x - (blockX * MapMul.BlockSize));
                var localY = (byte)(y - (blockY * MapMul.BlockSize));
                var blockIndex = StaticsLayoutHelper.GetBlockIndex(blockX, blockY, blockWidth, blockHeight, layout);
                var list = blocks[blockIndex] ??= new List<StaticMulEntry>();
                list.Add(new StaticMulEntry(tileId, localX, localY, adjustedZ, 0));
                placedStatics++;
            }

            rowsProcessed++;
            ReportProgress(progress, rowsProcessed, height, ref lastProgress);
        }

        if (suppressedErrors > 0)
        {
            log?.Invoke(new MapConversionLogEntry(
                MapConversionLogLevel.Warning,
                $"Suppressed {suppressedErrors:N0} additional missing terrain color errors."
            ));
        }

        return blocks;
    }

    public static (Bmp24Image terrain, Bmp8Image altitude, MapConversionReport report) ConvertMulToBmpRgb24(
        string mapMulPath,
        int width,
        int height,
        TileColorMap map,
        IProgress<int>? progress = null,
        Action<MapConversionLogEntry>? log = null,
        MapConversionOptions? options = null
    )
    {
        options ??= new MapConversionOptions();
        var tiles = MapMulCodec.ReadLandTiles(mapMulPath, width, height);
        var terrainPixels = new byte[width * height * 3];
        var altitudePixels = new byte[width * height];
        var report = new MapConversionReport(width * height);

        var altitudePalette = Bmp8Codec.CreateGrayscalePalette();
        var unknown = map.UnknownColor;

        var lastProgress = -1;
        var loggedErrors = 0;
        var suppressedErrors = 0;
        var suppressionLogged = false;

        for (var y = 0; y < height; y++)
        {
            options.CancellationToken?.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var tile = tiles[y * width + x];
                var tileId = ApplyTerrainReplacement(tile.TileId, options.TileReplacementMap);
                if (!map.TryGetColor(tileId, out var color))
                {
                    report.MissingTerrainColors++;
                    report.AddMissingColor(tileId);
                    var message = $"Missing terrain color at ({x},{y}) tile=0x{tileId:X4} z={tile.Z}; using {unknown.ToHex()}.";
                    if (options.StopOnError)
                    {
                        log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Error, message));
                        throw new MapConversionAbortException(message);
                    }

                    if (log != null && options.MaxErrorLogs > 0)
                    {
                        if (loggedErrors < options.MaxErrorLogs)
                        {
                            log.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Error, message));
                            loggedErrors++;
                        }
                        else
                        {
                            suppressedErrors++;
                            if (!suppressionLogged)
                            {
                                suppressionLogged = true;
                                log.Invoke(new MapConversionLogEntry(
                                    MapConversionLogLevel.Warning,
                                    $"Too many missing color errors. Suppressing further error logs (limit {options.MaxErrorLogs})."
                                ));
                            }
                        }
                    }

                    color = unknown;
                }

                var index = (y * width + x) * 3;
                terrainPixels[index] = color.R;
                terrainPixels[index + 1] = color.G;
                terrainPixels[index + 2] = color.B;
                altitudePixels[y * width + x] = EncodeAltitude(tile.Z);
            }

            ReportProgress(progress, y + 1, height, ref lastProgress);
        }

        if (suppressedErrors > 0)
        {
            log?.Invoke(new MapConversionLogEntry(
                MapConversionLogLevel.Warning,
                $"Suppressed {suppressedErrors:N0} additional missing color errors."
            ));
        }

        var terrain = new Bmp24Image(width, height, terrainPixels);
        var altitude = new Bmp8Image(width, height, altitudePixels, altitudePalette);
        return (terrain, altitude, report);
    }

    public static (Bmp24Image terrain, Bmp8Image altitude, MapConversionReport report) ConvertMulToTerrainXmlBmp(
        string mapMulPath,
        int width,
        int height,
        string terrainXmlPath,
        string transitionsRoot,
        IProgress<int>? progress = null,
        Action<MapConversionLogEntry>? log = null,
        MapConversionOptions? options = null
    )
    {
        var map = BuildTerrainXmlTileColorMap(terrainXmlPath, transitionsRoot, log);
        return ConvertMulToBmpRgb24(mapMulPath, width, height, map, progress, log, options);
    }

    public static MapConversionReport ConvertMulToBmpRgb24ToFile(
        string mapMulPath,
        int width,
        int height,
        TileColorMap map,
        string terrainPath,
        string altitudePath,
        IProgress<int>? progress = null,
        Action<MapConversionLogEntry>? log = null,
        MapConversionOptions? options = null
    )
    {
        options ??= new MapConversionOptions();
        var report = new MapConversionReport(width * height);
        var altitudePalette = Bmp8Codec.CreateGrayscalePalette();
        var unknown = map.UnknownColor;

        var loggedErrors = 0;
        var suppressedErrors = 0;
        var suppressionLogged = false;
        var lastProgress = -1;

        using var reader = new MapMulRowReader(mapMulPath, width, height);
        using var terrainWriter = new Bmp24StreamWriter(terrainPath, width, height);
        using var altitudeWriter = new Bmp8StreamWriter(altitudePath, width, height, altitudePalette);

        var tileRow = new LandTile[width];
        var terrainRow = new byte[width * 3];
        var altitudeRow = new byte[width];

        for (var y = height - 1; y >= 0; y--)
        {
            options.CancellationToken?.ThrowIfCancellationRequested();
            reader.ReadRow(y, tileRow);

            for (var x = 0; x < width; x++)
            {
                var tile = tileRow[x];
                var tileId = ApplyTerrainReplacement(tile.TileId, options.TileReplacementMap);
                if (!map.TryGetColor(tileId, out var color))
                {
                    report.MissingTerrainColors++;
                    report.AddMissingColor(tileId);
                    var message = $"Missing terrain color at ({x},{y}) tile=0x{tileId:X4} z={tile.Z}; using {unknown.ToHex()}.";
                    if (options.StopOnError)
                    {
                        log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Error, message));
                        throw new MapConversionAbortException(message);
                    }

                    if (log != null && options.MaxErrorLogs > 0)
                    {
                        if (loggedErrors < options.MaxErrorLogs)
                        {
                            log.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Error, message));
                            loggedErrors++;
                        }
                        else
                        {
                            suppressedErrors++;
                            if (!suppressionLogged)
                            {
                                suppressionLogged = true;
                                log.Invoke(new MapConversionLogEntry(
                                    MapConversionLogLevel.Warning,
                                    $"Too many missing color errors. Suppressing further error logs (limit {options.MaxErrorLogs})."
                                ));
                            }
                        }
                    }

                    color = unknown;
                }

                var index = x * 3;
                terrainRow[index] = color.R;
                terrainRow[index + 1] = color.G;
                terrainRow[index + 2] = color.B;
                altitudeRow[x] = EncodeAltitude(tile.Z);
            }

            terrainWriter.WriteRow(terrainRow);
            altitudeWriter.WriteRow(altitudeRow);

            ReportProgress(progress, height - y, height, ref lastProgress);
        }

        if (suppressedErrors > 0)
        {
            log?.Invoke(new MapConversionLogEntry(
                MapConversionLogLevel.Warning,
                $"Suppressed {suppressedErrors:N0} additional missing color errors."
            ));
        }

        return report;
    }

    public static MapConversionReport ConvertMulToTerrainXmlBmpToFile(
        string mapMulPath,
        int width,
        int height,
        string terrainXmlPath,
        string transitionsRoot,
        string terrainPath,
        string altitudePath,
        IProgress<int>? progress = null,
        Action<MapConversionLogEntry>? log = null,
        MapConversionOptions? options = null
    )
    {
        options ??= new MapConversionOptions();
        if (options.UseAltitudeXmlColors)
        {
            return ConvertMulToTerrainXmlBmpToFileWithAltitudeColors(
                mapMulPath,
                width,
                height,
                terrainXmlPath,
                transitionsRoot,
                terrainPath,
                altitudePath,
                progress,
                log,
                options
            );
        }

        var map = BuildTerrainXmlTileColorMap(terrainXmlPath, transitionsRoot, log);
        return ConvertMulToBmpRgb24ToFile(
            mapMulPath,
            width,
            height,
            map,
            terrainPath,
            altitudePath,
            progress,
            log,
            options
        );
    }

    public static TileColorMap BuildTerrainXmlTileColorMap(
        string terrainXmlPath,
        string transitionsRoot,
        Action<MapConversionLogEntry>? log = null
    )
    {
        var catalog = TerrainTransitionCatalog.Load(terrainXmlPath, transitionsRoot, log);
        var tileToColor = new Dictionary<ushort, RgbColor>();
        var colorToTile = new Dictionary<int, ushort>();
        var terrainColorById = new Dictionary<int, RgbColor>();
        var unknown = new RgbColor(255, 0, 255);

        foreach (var terrain in catalog.TerrainById.Values)
        {
            var color = new RgbColor(
                (byte)((terrain.ColorKey >> 16) & 0xFF),
                (byte)((terrain.ColorKey >> 8) & 0xFF),
                (byte)(terrain.ColorKey & 0xFF)
            );
            terrainColorById[terrain.Id] = color;

            AddTerrainXmlTileColor(tileToColor, colorToTile, terrain.TileId, color);
            if (terrain.Random)
            {
                for (var i = 1; i <= 3; i++)
                {
                    AddTerrainXmlTileColor(tileToColor, colorToTile, (ushort)(terrain.TileId + i), color);
                }
            }
        }

        var transitionTiles = 0;
        var transitionConflicts = 0;
        foreach (var entry in catalog.Transitions)
        {
            if (!TryParseTerrainHashCenter(entry.Key, out var terrainId))
            {
                continue;
            }

            if (!terrainColorById.TryGetValue(terrainId, out var color))
            {
                continue;
            }

            foreach (var tile in entry.Value.MapTiles)
            {
                if (tileToColor.TryGetValue(tile.TileId, out var existing))
                {
                    if (existing.Key != color.Key)
                    {
                        transitionConflicts++;
                    }
                    continue;
                }

                AddTerrainXmlTileColor(tileToColor, colorToTile, tile.TileId, color);
                transitionTiles++;
            }
        }

        log?.Invoke(new MapConversionLogEntry(
            MapConversionLogLevel.Info,
            $"Terrain XML tile colors mapped: {tileToColor.Count:N0} tiles ({transitionTiles:N0} transitions, {transitionConflicts:N0} conflicts)."
        ));

        return new TileColorMap(
            TileColorMode.Rgb24,
            new Dictionary<ushort, byte>(),
            tileToColor,
            new Dictionary<byte, ushort>(),
            colorToTile,
            null,
            unknown
        );
    }

    private static MapConversionReport ConvertMulToTerrainXmlBmpToFileWithAltitudeColors(
        string mapMulPath,
        int width,
        int height,
        string terrainXmlPath,
        string transitionsRoot,
        string terrainPath,
        string altitudePath,
        IProgress<int>? progress,
        Action<MapConversionLogEntry>? log,
        MapConversionOptions options
    )
    {
        var map = BuildTerrainXmlTileColorMap(terrainXmlPath, transitionsRoot, log);
        var report = new MapConversionReport(width * height);
        var altitudeCatalog = AltitudeCatalog.Load(Path.Combine(UOMapWeaverDataPaths.DataRoot, "Altitude.xml"), log);
        var altitudeColorMap = BuildAltitudeColorMap(altitudeCatalog);
        var unknown = map.UnknownColor;

        var loggedErrors = 0;
        var suppressedErrors = 0;
        var suppressionLogged = false;
        var lastProgress = -1;

        using var reader = new MapMulRowReader(mapMulPath, width, height);
        using var terrainWriter = new Bmp24StreamWriter(terrainPath, width, height);
        using var altitudeWriter = new Bmp24StreamWriter(altitudePath, width, height);

        var tileRow = new LandTile[width];
        var terrainRow = new byte[width * 3];
        var altitudeRow = new byte[width * 3];

        for (var y = height - 1; y >= 0; y--)
        {
            options.CancellationToken?.ThrowIfCancellationRequested();
            reader.ReadRow(y, tileRow);

            for (var x = 0; x < width; x++)
            {
                var tile = tileRow[x];
                var tileId = ApplyTerrainReplacement(tile.TileId, options.TileReplacementMap);
                if (!map.TryGetColor(tileId, out var color))
                {
                    report.MissingTerrainColors++;
                    report.AddMissingColor(tileId);
                    var message = $"Missing terrain color at ({x},{y}) tile=0x{tileId:X4} z={tile.Z}; using {unknown.ToHex()}.";
                    if (options.StopOnError)
                    {
                        log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Error, message));
                        throw new MapConversionAbortException(message);
                    }

                    if (log != null && options.MaxErrorLogs > 0)
                    {
                        if (loggedErrors < options.MaxErrorLogs)
                        {
                            log.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Error, message));
                            loggedErrors++;
                        }
                        else
                        {
                            suppressedErrors++;
                            if (!suppressionLogged)
                            {
                                suppressionLogged = true;
                                log.Invoke(new MapConversionLogEntry(
                                    MapConversionLogLevel.Warning,
                                    $"Too many missing color errors. Suppressing further error logs (limit {options.MaxErrorLogs})."
                                ));
                            }
                        }
                    }

                    color = unknown;
                }

                var index = x * 3;
                terrainRow[index] = color.R;
                terrainRow[index + 1] = color.G;
                terrainRow[index + 2] = color.B;

                if (altitudeColorMap.TryGetValue(tile.Z, out var altitudeColor))
                {
                    altitudeRow[index] = altitudeColor.R;
                    altitudeRow[index + 1] = altitudeColor.G;
                    altitudeRow[index + 2] = altitudeColor.B;
                }
                else
                {
                    var grayscale = EncodeAltitude(tile.Z);
                    altitudeRow[index] = grayscale;
                    altitudeRow[index + 1] = grayscale;
                    altitudeRow[index + 2] = grayscale;
                }
            }

            terrainWriter.WriteRow(terrainRow);
            altitudeWriter.WriteRow(altitudeRow);

            ReportProgress(progress, height - y, height, ref lastProgress);
        }

        if (suppressedErrors > 0)
        {
            log?.Invoke(new MapConversionLogEntry(
                MapConversionLogLevel.Warning,
                $"Suppressed {suppressedErrors:N0} additional missing color errors."
            ));
        }

        return report;
    }

    public static (Bmp24Image terrain, Bmp8Image altitude, MapConversionReport report) ConvertMulToTileIndexBmp(
        string mapMulPath,
        int width,
        int height,
        IProgress<int>? progress = null,
        Action<MapConversionLogEntry>? log = null,
        MapConversionOptions? options = null
    )
    {
        options ??= new MapConversionOptions();
        var tiles = MapMulCodec.ReadLandTiles(mapMulPath, width, height);
        var terrainPixels = new byte[width * height * 3];
        var altitudePixels = new byte[width * height];
        var report = new MapConversionReport(width * height);
        var altitudePalette = Bmp8Codec.CreateGrayscalePalette();

        var lastProgress = -1;
        for (var y = 0; y < height; y++)
        {
            options.CancellationToken?.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var tile = tiles[y * width + x];
                var tileId = ApplyTerrainReplacement(tile.TileId, options.TileReplacementMap);
                var index = (y * width + x) * 3;
                terrainPixels[index] = (byte)(tileId >> 8);
                terrainPixels[index + 1] = (byte)(tileId & 0xFF);
                terrainPixels[index + 2] = 0;
                altitudePixels[y * width + x] = EncodeAltitude(tile.Z);
            }

            ReportProgress(progress, y + 1, height, ref lastProgress);
        }

        var terrain = new Bmp24Image(width, height, terrainPixels);
        var altitude = new Bmp8Image(width, height, altitudePixels, altitudePalette);
        return (terrain, altitude, report);
    }

    public static MapConversionReport ConvertMulToTileIndexBmpToFile(
        string mapMulPath,
        int width,
        int height,
        string terrainPath,
        string altitudePath,
        IProgress<int>? progress = null,
        Action<MapConversionLogEntry>? log = null,
        MapConversionOptions? options = null
    )
    {
        options ??= new MapConversionOptions();
        var report = new MapConversionReport(width * height);
        var altitudePalette = Bmp8Codec.CreateGrayscalePalette();
        var lastProgress = -1;

        using var reader = new MapMulRowReader(mapMulPath, width, height);
        using var terrainWriter = new Bmp24StreamWriter(terrainPath, width, height);
        using var altitudeWriter = new Bmp8StreamWriter(altitudePath, width, height, altitudePalette);

        var tileRow = new LandTile[width];
        var terrainRow = new byte[width * 3];
        var altitudeRow = new byte[width];

        for (var y = height - 1; y >= 0; y--)
        {
            options.CancellationToken?.ThrowIfCancellationRequested();
            reader.ReadRow(y, tileRow);

            for (var x = 0; x < width; x++)
            {
                var tile = tileRow[x];
                var tileId = ApplyTerrainReplacement(tile.TileId, options.TileReplacementMap);
                var index = x * 3;
                terrainRow[index] = (byte)(tileId >> 8);
                terrainRow[index + 1] = (byte)(tileId & 0xFF);
                terrainRow[index + 2] = 0;
                altitudeRow[x] = EncodeAltitude(tile.Z);
            }

            terrainWriter.WriteRow(terrainRow);
            altitudeWriter.WriteRow(altitudeRow);

            ReportProgress(progress, height - y, height, ref lastProgress);
        }

        return report;
    }

    public static (LandTile[] tiles, MapConversionReport report) ConvertBmp24ToMul(
        Bmp24Image terrain,
        Bmp8Image altitude,
        TileColorMap map,
        IProgress<int>? progress = null,
        Action<MapConversionLogEntry>? log = null,
        MapConversionOptions? options = null
    )
    {
        options ??= new MapConversionOptions();
        if (terrain.Width != altitude.Width || terrain.Height != altitude.Height)
        {
            throw new InvalidOperationException("Terrain and altitude images must match in size.");
        }

        var width = terrain.Width;
        var height = terrain.Height;
        var tiles = new LandTile[width * height];
        var report = new MapConversionReport(width * height);

        var lastProgress = -1;
        var loggedErrors = 0;
        var suppressedErrors = 0;
        var suppressionLogged = false;
        var unknownTileId = (ushort)0;

        for (var y = 0; y < height; y++)
        {
            options.CancellationToken?.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                var pixelIndex = index * 3;
                var color = new RgbColor(
                    terrain.Pixels[pixelIndex],
                    terrain.Pixels[pixelIndex + 1],
                    terrain.Pixels[pixelIndex + 2]);
                var z = DecodeAltitude(altitude.Pixels[index]);

                if (!map.TryGetTileId(color, out var tileId))
                {
                    report.MissingTerrainTiles++;
                    report.AddMissingTile(0);
                    var message = $"Missing terrain tile at ({x},{y}) color={color.ToHex()} z={z}; using tileId 0x{unknownTileId:X4}.";
                    if (options.StopOnError)
                    {
                        log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Error, message));
                        throw new MapConversionAbortException(message);
                    }

                    if (log != null && options.MaxErrorLogs > 0)
                    {
                        if (loggedErrors < options.MaxErrorLogs)
                        {
                            log.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Error, message));
                            loggedErrors++;
                        }
                        else
                        {
                            suppressedErrors++;
                            if (!suppressionLogged)
                            {
                                suppressionLogged = true;
                                log.Invoke(new MapConversionLogEntry(
                                    MapConversionLogLevel.Warning,
                                    $"Too many missing tile errors. Suppressing further error logs (limit {options.MaxErrorLogs})."
                                ));
                            }
                        }
                    }

                    tileId = unknownTileId;
                }

                tiles[index] = new LandTile(tileId, z);
            }

            ReportProgress(progress, y + 1, height, ref lastProgress);
        }

        if (suppressedErrors > 0)
        {
            log?.Invoke(new MapConversionLogEntry(
                MapConversionLogLevel.Warning,
                $"Suppressed {suppressedErrors:N0} additional missing tile errors."
            ));
        }

        return (tiles, report);
    }

    public static MapConversionReport ConvertBmp24ToMulFromFiles(
        string terrainPath,
        string altitudePath,
        TileColorMap map,
        string mapMulPath,
        IProgress<int>? progress = null,
        Action<MapConversionLogEntry>? log = null,
        MapConversionOptions? options = null
    )
    {
        options ??= new MapConversionOptions();

        using var terrainReader = new Bmp24StreamReader(terrainPath);
        using var altitudeReader = new Bmp8StreamReader(altitudePath);

        if (terrainReader.Width != altitudeReader.Width || terrainReader.Height != altitudeReader.Height)
        {
            throw new InvalidOperationException("Terrain and altitude images must match in size.");
        }

        var width = terrainReader.Width;
        var height = terrainReader.Height;
        var report = new MapConversionReport(width * height);

        var loggedErrors = 0;
        var suppressedErrors = 0;
        var suppressionLogged = false;
        var lastProgress = -1;
        var rowsProcessed = 0;
        var unknownTileId = (ushort)0;

        var terrainRow = new byte[width * 3];
        var altitudeRow = new byte[width];

        MapMulCodec.WriteLandTilesFromRows(mapMulPath, width, height, (y, rowSpan) =>
        {
            options.CancellationToken?.ThrowIfCancellationRequested();
            terrainReader.ReadRow(y, terrainRow);
            altitudeReader.ReadRow(y, altitudeRow);

            for (var x = 0; x < width; x++)
            {
                var index = x * 3;
                var color = new RgbColor(terrainRow[index], terrainRow[index + 1], terrainRow[index + 2]);
                var z = DecodeAltitude(altitudeRow[x]);

                if (!map.TryGetTileId(color, out var tileId))
                {
                    report.MissingTerrainTiles++;
                    report.AddMissingTile(0);
                    var message = $"Missing terrain tile at ({x},{y}) color={color.ToHex()} z={z}; using tileId 0x{unknownTileId:X4}.";
                    if (options.StopOnError)
                    {
                        log?.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Error, message));
                        throw new MapConversionAbortException(message);
                    }

                    if (log != null && options.MaxErrorLogs > 0)
                    {
                        if (loggedErrors < options.MaxErrorLogs)
                        {
                            log.Invoke(new MapConversionLogEntry(MapConversionLogLevel.Error, message));
                            loggedErrors++;
                        }
                        else
                        {
                            suppressedErrors++;
                            if (!suppressionLogged)
                            {
                                suppressionLogged = true;
                                log.Invoke(new MapConversionLogEntry(
                                    MapConversionLogLevel.Warning,
                                    $"Too many missing tile errors. Suppressing further error logs (limit {options.MaxErrorLogs})."
                                ));
                            }
                        }
                    }

                    tileId = unknownTileId;
                }

                rowSpan[x] = new LandTile(tileId, z);
            }

            rowsProcessed++;
            ReportProgress(progress, rowsProcessed, height, ref lastProgress);
        });

        if (suppressedErrors > 0)
        {
            log?.Invoke(new MapConversionLogEntry(
                MapConversionLogLevel.Warning,
                $"Suppressed {suppressedErrors:N0} additional missing tile errors."
            ));
        }

        return report;
    }

    public static (LandTile[] tiles, MapConversionReport report) ConvertTileIndexBmpToMul(
        Bmp24Image terrain,
        Bmp8Image altitude,
        IProgress<int>? progress = null,
        Action<MapConversionLogEntry>? log = null,
        MapConversionOptions? options = null
    )
    {
        options ??= new MapConversionOptions();
        if (terrain.Width != altitude.Width || terrain.Height != altitude.Height)
        {
            throw new InvalidOperationException("Terrain and altitude images must match in size.");
        }

        var width = terrain.Width;
        var height = terrain.Height;
        var tiles = new LandTile[width * height];
        var report = new MapConversionReport(width * height);

        var lastProgress = -1;
        for (var y = 0; y < height; y++)
        {
            options.CancellationToken?.ThrowIfCancellationRequested();
            for (var x = 0; x < width; x++)
            {
                var index = y * width + x;
                var pixelIndex = index * 3;
                var tileId = (ushort)((terrain.Pixels[pixelIndex] << 8) | terrain.Pixels[pixelIndex + 1]);
                var z = DecodeAltitude(altitude.Pixels[index]);
                tiles[index] = new LandTile(tileId, z);
            }

            ReportProgress(progress, y + 1, height, ref lastProgress);
        }

        return (tiles, report);
    }

    public static MapConversionReport ConvertTileIndexBmpToMulFromFiles(
        string terrainPath,
        string altitudePath,
        string mapMulPath,
        IProgress<int>? progress = null,
        Action<MapConversionLogEntry>? log = null,
        MapConversionOptions? options = null
    )
    {
        options ??= new MapConversionOptions();

        using var terrainReader = new Bmp24StreamReader(terrainPath);
        using var altitudeReader = new Bmp8StreamReader(altitudePath);

        if (terrainReader.Width != altitudeReader.Width || terrainReader.Height != altitudeReader.Height)
        {
            throw new InvalidOperationException("Terrain and altitude images must match in size.");
        }

        var width = terrainReader.Width;
        var height = terrainReader.Height;
        var report = new MapConversionReport(width * height);

        var lastProgress = -1;
        var rowsProcessed = 0;

        var terrainRow = new byte[width * 3];
        var altitudeRow = new byte[width];

        MapMulCodec.WriteLandTilesFromRows(mapMulPath, width, height, (y, rowSpan) =>
        {
            options.CancellationToken?.ThrowIfCancellationRequested();
            terrainReader.ReadRow(y, terrainRow);
            altitudeReader.ReadRow(y, altitudeRow);

            for (var x = 0; x < width; x++)
            {
                var index = x * 3;
                var tileId = (ushort)((terrainRow[index] << 8) | terrainRow[index + 1]);
                var z = DecodeAltitude(altitudeRow[x]);
                rowSpan[x] = new LandTile(tileId, z);
            }

            rowsProcessed++;
            ReportProgress(progress, rowsProcessed, height, ref lastProgress);
        });

        return report;
    }

    private static void ReportProgress(IProgress<int>? progress, int completed, int total, ref int lastProgress)
    {
        if (progress is null || total <= 0)
        {
            return;
        }

        var percent = (int)(completed * 100.0 / total);
        if (percent == lastProgress)
        {
            return;
        }

        lastProgress = percent;
        progress.Report(percent);
    }

    public static bool TryResolveMapSizeFromFile(string mapMulPath, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (!File.Exists(mapMulPath))
        {
            return false;
        }

        var length = new FileInfo(mapMulPath).Length;
        if (!TryGetBlockCount(length, out var blockCount) &&
            !TryGetBlockCountFromStaIdx(mapMulPath, out blockCount))
        {
            return false;
        }
        if (blockCount == 0)
        {
            return false;
        }

        if (TryResolveFromFileName(mapMulPath, out width, out height))
        {
            var blocks = (width / MapMul.BlockSize) * (height / MapMul.BlockSize);
            if (blocks == blockCount || blocks == blockCount - 1)
            {
                return true;
            }
        }

        var mapDefs = TryLoadMapDefinitions();
        foreach (var def in mapDefs)
        {
            var blocks = (def.width / MapMul.BlockSize) * (def.height / MapMul.BlockSize);
            if (blocks == blockCount || blocks == blockCount - 1)
            {
                width = def.width;
                height = def.height;
                return true;
            }
        }

        if (TryResolveFromBlockCount(blockCount, out width, out height))
        {
            return true;
        }

        if (blockCount > 1 && TryResolveFromBlockCount(blockCount - 1, out width, out height))
        {
            return true;
        }

        var guess = (int)Math.Sqrt(blockCount);
        if (guess > 0 && blockCount % guess == 0)
        {
            width = guess * MapMul.BlockSize;
            height = (blockCount / guess) * MapMul.BlockSize;
            return true;
        }

        return false;
    }

    public static bool DoesSizeMatchFile(string mapMulPath, int width, int height)
    {
        if (!File.Exists(mapMulPath))
        {
            return false;
        }

        var length = new FileInfo(mapMulPath).Length;
        if (!TryGetBlockCount(length, out var blockCount) &&
            !TryGetBlockCountFromStaIdx(mapMulPath, out blockCount))
        {
            return false;
        }
        var expectedBlocks = (width / MapMul.BlockSize) * (height / MapMul.BlockSize);
        return expectedBlocks == blockCount || expectedBlocks == blockCount - 1;
    }

    public static string GetSizeMismatchDetails(string mapMulPath, int width, int height)
    {
        if (!File.Exists(mapMulPath))
        {
            return "Map.mul not found.";
        }

        var length = new FileInfo(mapMulPath).Length;
        if (!TryGetBlockCount(length, out var blockCount) &&
            !TryGetBlockCountFromStaIdx(mapMulPath, out blockCount))
        {
            return "Map.mul size is not aligned to land block bytes.";
        }
        var expectedBlocks = (width / MapMul.BlockSize) * (height / MapMul.BlockSize);

        if (expectedBlocks == blockCount - 1)
        {
            return $"Map size matches with extra block: expected {expectedBlocks:N0} blocks for {width}x{height}, file has {blockCount:N0} blocks.";
        }

        return $"Map size mismatch: expected {expectedBlocks:N0} blocks for {width}x{height}, file has {blockCount:N0} blocks.";
    }

    private static bool TryResolveTerrainColor(
        Dictionary<ushort, List<MapTransEntry>> lookup,
        ushort tileId,
        sbyte z,
        out byte colorIndex,
        ushort[] sortedTileIds,
        Dictionary<ushort, ushort> nearestCache,
        MapConversionReport report
    )
    {
        if (!lookup.TryGetValue(tileId, out var entries) || entries.Count == 0)
        {
            if (!TryResolveNearestTileId(tileId, sortedTileIds, nearestCache, out var nearestId) ||
                !lookup.TryGetValue(nearestId, out entries) || entries.Count == 0)
            {
                colorIndex = 0;
                return false;
            }

            report.FallbackTerrainColors++;
        }

        var best = entries[0];
        var bestDiff = Math.Abs(best.Altitude - z);

        for (var i = 1; i < entries.Count; i++)
        {
            var entry = entries[i];
            var diff = Math.Abs(entry.Altitude - z);
            if (diff < bestDiff)
            {
                best = entry;
                bestDiff = diff;
            }
        }

        colorIndex = best.ColorIndex;
        return true;
    }

    private static bool TryResolveNearestTileId(
        ushort tileId,
        ushort[] sortedTileIds,
        Dictionary<ushort, ushort> nearestCache,
        out ushort nearestId
    )
    {
        if (sortedTileIds.Length == 0)
        {
            nearestId = 0;
            return false;
        }

        if (nearestCache.TryGetValue(tileId, out nearestId))
        {
            return true;
        }

        var index = Array.BinarySearch(sortedTileIds, tileId);
        if (index >= 0)
        {
            nearestId = sortedTileIds[index];
            nearestCache[tileId] = nearestId;
            return true;
        }

        var insertIndex = ~index;
        var leftIndex = Math.Clamp(insertIndex - 1, 0, sortedTileIds.Length - 1);
        var rightIndex = Math.Clamp(insertIndex, 0, sortedTileIds.Length - 1);

        var left = sortedTileIds[leftIndex];
        var right = sortedTileIds[rightIndex];
        nearestId = (ushort)(Math.Abs(left - tileId) <= Math.Abs(right - tileId) ? left : right);
        nearestCache[tileId] = nearestId;
        return true;
    }

    private static bool TryResolveTerrainTile(MapTransProfile profile, byte terrainIndex, sbyte z, int x, int y, out ushort tileId)
    {
        if (!profile.EntriesByColor.TryGetValue(terrainIndex, out var entries) || entries.Count == 0)
        {
            tileId = 0;
            return false;
        }

        var entry = entries[0];
        var bestDiff = Math.Abs(entry.Altitude - z);

        for (var i = 1; i < entries.Count; i++)
        {
            var candidate = entries[i];
            var diff = Math.Abs(candidate.Altitude - z);
            if (diff < bestDiff)
            {
                entry = candidate;
                bestDiff = diff;
            }
        }

        if (entry.TileIds.Count == 0)
        {
            tileId = 0;
            return false;
        }

        var tileIndex = (x + y) % entry.TileIds.Count;
        tileId = entry.TileIds[tileIndex];
        return true;
    }

    private static Dictionary<ushort, List<MapTransEntry>> BuildTileLookup(MapTransProfile profile)
    {
        var lookup = new Dictionary<ushort, List<MapTransEntry>>();
        foreach (var entry in profile.Entries)
        {
            foreach (var tileId in entry.TileIds)
            {
                if (!lookup.TryGetValue(tileId, out var list))
                {
                    list = new List<MapTransEntry>();
                    lookup[tileId] = list;
                }

                list.Add(entry);
            }
        }

        return lookup;
    }

    private static void ReadTerrainRowIds(
        Bmp24StreamReader terrainReader,
        TerrainTransitionCatalog catalog,
        int y,
        byte[] terrainIds,
        byte[] terrainRow,
        MapConversionReport report,
        Action<MapConversionLogEntry>? log,
        ref int loggedErrors,
        ref int suppressedErrors,
        ref bool suppressionLogged,
        MapConversionOptions options)
    {
        terrainReader.ReadRow(y, terrainRow);
        for (var x = 0; x < terrainIds.Length; x++)
        {
            var offset = x * 3;
            var b = terrainRow[offset];
            var g = terrainRow[offset + 1];
            var r = terrainRow[offset + 2];
            var colorKey = (r << 16) | (g << 8) | b;

            if (!catalog.TerrainIdByColor.TryGetValue(colorKey, out var terrainId))
            {
                report.MissingTerrainColors++;
                if (log != null && options.MaxErrorLogs > 0)
                {
                    if (loggedErrors < options.MaxErrorLogs)
                    {
                        log.Invoke(new MapConversionLogEntry(
                            MapConversionLogLevel.Error,
                            $"Missing terrain color at ({x},{y}) rgb=0x{colorKey:X6}; using 0."
                        ));
                        loggedErrors++;
                    }
                    else
                    {
                        suppressedErrors++;
                        if (!suppressionLogged)
                        {
                            suppressionLogged = true;
                            log.Invoke(new MapConversionLogEntry(
                                MapConversionLogLevel.Warning,
                                $"Too many missing color errors. Suppressing further error logs (limit {options.MaxErrorLogs})."
                            ));
                        }
                    }
                }

                terrainId = 0;
            }

            terrainIds[x] = (byte)(terrainId & 0xFF);
        }
    }

    private static TransitionSelection ResolveTerrainTransitionSelection(
        TerrainTransitionCatalog catalog,
        byte[] prevRow,
        byte[] currRow,
        byte[] nextRow,
        int x,
        int y)
    {
        var width = currRow.Length;
        var left = Math.Max(x - 1, 0);
        var right = Math.Min(x + 1, width - 1);

        var hash = BuildTerrainTransitionHash(
            prevRow[left], prevRow[x], prevRow[right],
            currRow[left], currRow[x], currRow[right],
            nextRow[left], nextRow[x], nextRow[right]);

        List<TransitionTile>? mapTiles = null;
        List<TransitionStatic>? staticTiles = null;
        if (catalog.Transitions.TryGetValue(hash, out var set))
        {
            mapTiles = set.MapTiles;
            staticTiles = set.StaticTiles;
        }

        if (mapTiles is null || mapTiles.Count == 0)
        {
            if (catalog.BaseTiles.TryGetValue(currRow[x], out var baseTiles) && baseTiles.Count > 0)
            {
                mapTiles = baseTiles;
            }
        }

        var seed = x + y;
        TransitionTile mapTile;
        if (mapTiles is { Count: > 0 })
        {
            mapTile = mapTiles[seed % mapTiles.Count];
        }
        else if (catalog.TerrainById.TryGetValue(currRow[x], out var terrain))
        {
            mapTile = new TransitionTile(terrain.TileId, 0);
        }
        else
        {
            mapTile = new TransitionTile(0, 0);
        }

        TransitionStatic? staticTile = null;
        if (staticTiles is { Count: > 0 })
        {
            staticTile = staticTiles[seed % staticTiles.Count];
        }

        return new TransitionSelection(mapTile, staticTile);
    }

    private readonly record struct TransitionSelection(TransitionTile MapTile, TransitionStatic? StaticTile);

    private static string BuildTerrainTransitionHash(
        byte tl, byte t, byte tr,
        byte ml, byte m, byte mr,
        byte bl, byte b, byte br)
    {
        Span<char> chars = stackalloc char[18];
        WriteHexByte(tl, chars, 0);
        WriteHexByte(t, chars, 2);
        WriteHexByte(tr, chars, 4);
        WriteHexByte(ml, chars, 6);
        WriteHexByte(m, chars, 8);
        WriteHexByte(mr, chars, 10);
        WriteHexByte(bl, chars, 12);
        WriteHexByte(b, chars, 14);
        WriteHexByte(br, chars, 16);
        return new string(chars);
    }

    private static bool TryParseTerrainHashCenter(string hash, out int terrainId)
    {
        terrainId = 0;
        if (hash.Length != 18)
        {
            return false;
        }

        var center = hash.Substring(8, 2);
        return int.TryParse(center, System.Globalization.NumberStyles.HexNumber, null, out terrainId);
    }

    private static Dictionary<sbyte, RgbColor> BuildAltitudeColorMap(AltitudeCatalog catalog)
    {
        var map = new Dictionary<sbyte, RgbColor>();
        foreach (var entry in catalog.AltitudeByColor)
        {
            var colorKey = entry.Key;
            var altitude = entry.Value;
            if (map.ContainsKey(altitude))
            {
                continue;
            }

            var color = new RgbColor(
                (byte)((colorKey >> 16) & 0xFF),
                (byte)((colorKey >> 8) & 0xFF),
                (byte)(colorKey & 0xFF)
            );
            map[altitude] = color;
        }

        return map;
    }

    private static void AddTerrainXmlTileColor(
        Dictionary<ushort, RgbColor> tileToColor,
        Dictionary<int, ushort> colorToTile,
        ushort tileId,
        RgbColor color)
    {
        if (!tileToColor.ContainsKey(tileId))
        {
            tileToColor[tileId] = color;
        }

        if (!colorToTile.ContainsKey(color.Key))
        {
            colorToTile[color.Key] = tileId;
        }
    }

    private static void WriteHexByte(byte value, Span<char> chars, int offset)
    {
        const string hex = "0123456789ABCDEF";
        chars[offset] = hex[value >> 4];
        chars[offset + 1] = hex[value & 0xF];
    }

    private static sbyte[] BuildAltitudeByIndex(
        BmpPaletteEntry[] palette,
        AltitudeCatalog catalog,
        Action<MapConversionLogEntry>? log,
        MapConversionOptions options,
        MapConversionReport report,
        ref int loggedErrors,
        ref int suppressedErrors,
        ref bool suppressionLogged)
    {
        var altitudeByIndex = new sbyte[256];
        for (var i = 0; i < altitudeByIndex.Length; i++)
        {
            var entry = palette[i];
            var colorKey = (entry.Red << 16) | (entry.Green << 8) | entry.Blue;
            if (catalog.AltitudeByColor.TryGetValue(colorKey, out var altitude))
            {
                altitudeByIndex[i] = altitude;
                continue;
            }

            altitudeByIndex[i] = DecodeAltitude((byte)i);
            if (log != null && options.MaxErrorLogs > 0)
            {
                if (loggedErrors < options.MaxErrorLogs)
                {
                    log.Invoke(new MapConversionLogEntry(
                        MapConversionLogLevel.Warning,
                        $"Missing altitude color rgb=0x{colorKey:X6} at index {i}; using grayscale decode."
                    ));
                    loggedErrors++;
                }
                else
                {
                    suppressedErrors++;
                    if (!suppressionLogged)
                    {
                        suppressionLogged = true;
                        log.Invoke(new MapConversionLogEntry(
                            MapConversionLogLevel.Warning,
                            $"Too many missing altitude color errors. Suppressing further error logs (limit {options.MaxErrorLogs})."
                        ));
                    }
                }
            }
        }

        return altitudeByIndex;
    }

    private static byte EncodeAltitude(sbyte z)
    {
        var value = z + 128;
        if (value < 0)
        {
            return 0;
        }

        return value > 255 ? (byte)255 : (byte)value;
    }

    private static sbyte DecodeAltitude(byte value)
    {
        var z = value - 128;
        if (z < sbyte.MinValue)
        {
            return sbyte.MinValue;
        }

        return z > sbyte.MaxValue ? sbyte.MaxValue : (sbyte)z;
    }

    private static BmpPaletteEntry[] LoadTerrainPalette(MapTransProfile profile)
    {
        var actPalette = TryLoadActPalette(Path.Combine(UOMapWeaverDataPaths.DataRoot, "ColorTables", "ACT", "Terrain.act"));
        var palettePath = profile.PalettePath;

        if (!string.IsNullOrWhiteSpace(palettePath) &&
            !IsDefaultPalettePath(palettePath) &&
            File.Exists(palettePath))
        {
            var paletteImage = Bmp8Codec.Read(palettePath);
            return paletteImage.Palette;
        }

        if (actPalette != null)
        {
            return actPalette;
        }

        if (!string.IsNullOrWhiteSpace(palettePath) && File.Exists(palettePath))
        {
            var paletteImage = Bmp8Codec.Read(palettePath);
            return paletteImage.Palette;
        }

        return Bmp8Codec.CreateGrayscalePalette();
    }

    private static bool IsDefaultPalettePath(string path)
    {
        if (!Path.GetFileName(path).Equals("TerrainPalette.bmp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(path);
        return !string.IsNullOrWhiteSpace(directory) &&
               Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)
                   .Equals(Path.GetFullPath(UOMapWeaverDataPaths.MapTransRoot).TrimEnd(Path.DirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase);
    }

    private static BmpPaletteEntry[]? TryLoadActPalette(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 768)
        {
            return null;
        }

        var palette = new BmpPaletteEntry[256];
        for (var i = 0; i < 256; i++)
        {
            var offset = i * 3;
            var red = bytes[offset];
            var green = bytes[offset + 1];
            var blue = bytes[offset + 2];
            palette[i] = new BmpPaletteEntry(blue, green, red, 0);
        }

        return palette;
    }

    private static void ApplyUnknownColor(BmpPaletteEntry[] palette)
    {
        if (palette.Length <= UnknownTerrainIndex)
        {
            return;
        }

        palette[UnknownTerrainIndex] = new BmpPaletteEntry(255, 0, 255, 0);
    }

    private static ushort ApplyTerrainReplacement(ushort tileId, TileReplacementMap? replacements)
    {
        if (replacements is not null && replacements.TryGetTerrainReplacement(tileId, out var replacement))
        {
            return replacement;
        }

        return tileId;
    }

    private static ushort ApplyStaticReplacement(ushort tileId, TileReplacementMap? replacements)
    {
        if (replacements is not null && replacements.TryGetStaticReplacement(tileId, out var replacement))
        {
            return replacement;
        }

        return tileId;
    }

    private static IEnumerable<(int width, int height)> TryLoadMapDefinitions()
    {
        var results = new List<(int width, int height)>();
        var candidates = new[]
        {
            UOMapWeaverDataPaths.MapDefinitionsPath,
            UOMapWeaverDataPaths.MapPresetsPath,
            Path.Combine(Directory.GetCurrentDirectory(), "Data", "map-definitions.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "Data", "map-presets.json"),
            Path.Combine(AppContext.BaseDirectory, "Data", "map-definitions.json"),
            Path.Combine(AppContext.BaseDirectory, "Data", "map-presets.json")
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(path);
                var doc = System.Text.Json.JsonDocument.Parse(json);
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (!element.TryGetProperty("width", out var widthProp) ||
                        !element.TryGetProperty("height", out var heightProp))
                    {
                        continue;
                    }

                    var width = widthProp.GetInt32();
                    var height = heightProp.GetInt32();
                    if (width > 0 && height > 0)
                    {
                        results.Add((width, height));
                    }
                }
            }
            catch
            {
                return results;
            }
        }

        return results;
    }

    private static bool TryResolveFromFileName(string mapMulPath, out int width, out int height)
    {
        width = 0;
        height = 0;

        var name = Path.GetFileNameWithoutExtension(mapMulPath);
        if (!name.StartsWith("map", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = name[3..];
        if (!int.TryParse(suffix, out var index))
        {
            return false;
        }

        return TryResolveFromMapIndex(index, out width, out height);
    }

    private static bool TryResolveFromMapIndex(int index, out int width, out int height)
    {
        width = 0;
        height = 0;

        switch (index)
        {
            case 0:
            case 1:
                width = 6144;
                height = 4096;
                return true;
            case 2:
                width = 2304;
                height = 1600;
                return true;
            case 3:
                width = 2560;
                height = 2048;
                return true;
            case 4:
                width = 1448;
                height = 1448;
                return true;
            case 5:
                width = 1280;
                height = 4096;
                return true;
            default:
                return false;
        }
    }

    public static bool TryResolveFromBlockCount(int blockCount, out int width, out int height)
    {
        width = 0;
        height = 0;

        var knownSizes = new[]
        {
            24000, 20000, 8192, 7168, 6400, 6144, 5120, 4096, 2560, 2304, 2048, 1600, 1536, 1448, 1280, 1024, 768, 512
        };

        if (TryResolveFromKnownSizes(blockCount, knownSizes, requireKnownHeight: true, out width, out height))
        {
            return true;
        }

        return TryResolveFromKnownSizes(blockCount, knownSizes, requireKnownHeight: false, out width, out height);
    }

    private static bool TryResolveFromKnownSizes(int blockCount, int[] knownSizes, bool requireKnownHeight, out int width, out int height)
    {
        width = 0;
        height = 0;
        var knownSet = new HashSet<int>(knownSizes);

        foreach (var candidateWidth in knownSizes)
        {
            var blockWidth = candidateWidth / MapMul.BlockSize;
            if (blockWidth <= 0 || blockCount % blockWidth != 0)
            {
                continue;
            }

            var blockHeight = blockCount / blockWidth;
            var candidateHeight = blockHeight * MapMul.BlockSize;

            if (requireKnownHeight && !knownSet.Contains(candidateHeight))
            {
                continue;
            }

            width = candidateWidth;
            height = candidateHeight;
            return true;
        }

        return false;
    }

    private static bool TryGetBlockCountFromStaIdx(string mapMulPath, out int blockCount)
    {
        blockCount = 0;
        if (!TryFindStaIdxPath(mapMulPath, out var staIdxPath))
        {
            return false;
        }

        try
        {
            var length = new FileInfo(staIdxPath).Length;
            if (length <= 0 || length % MapMul.StaticIndexRecordBytes != 0)
            {
                return false;
            }

            blockCount = (int)(length / MapMul.StaticIndexRecordBytes);
            return blockCount > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryFindStaIdxPath(string mapMulPath, out string staIdxPath)
    {
        staIdxPath = string.Empty;
        var directory = Path.GetDirectoryName(mapMulPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(mapMulPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (TryResolveStaticsNames(name, out var staticsName, out var staidxName))
        {
            var candidate = Path.Combine(directory, staidxName);
            if (File.Exists(candidate))
            {
                staIdxPath = candidate;
                return true;
            }
        }

        var fallback = Path.Combine(directory, "staidx.mul");
        if (File.Exists(fallback))
        {
            staIdxPath = fallback;
            return true;
        }

        return false;
    }

    private static bool TryResolveStaticsNames(string mapName, out string staticsName, out string staidxName)
    {
        staticsName = string.Empty;
        staidxName = string.Empty;

        if (mapName.EndsWith("_map", StringComparison.OrdinalIgnoreCase))
        {
            var prefix = mapName[..^4];
            if (string.IsNullOrWhiteSpace(prefix))
            {
                return false;
            }

            staticsName = $"{prefix}_statics.mul";
            staidxName = $"{prefix}_staidx.mul";
            return true;
        }

        var mapIndexPos = mapName.LastIndexOf("_map", StringComparison.OrdinalIgnoreCase);
        if (mapIndexPos >= 0 && mapIndexPos + 4 < mapName.Length)
        {
            var prefix = mapName[..mapIndexPos];
            var suffix = mapName[(mapIndexPos + 4)..];
            if (suffix.All(char.IsDigit))
            {
                if (string.IsNullOrWhiteSpace(prefix))
                {
                    staticsName = $"statics{suffix}.mul";
                    staidxName = $"staidx{suffix}.mul";
                    return true;
                }

                staticsName = $"{prefix}_statics{suffix}.mul";
                staidxName = $"{prefix}_staidx{suffix}.mul";
                return true;
            }
        }

        if (mapName.StartsWith("map", StringComparison.OrdinalIgnoreCase))
        {
            var suffix = mapName[3..];
            if (suffix.Length == 0 || suffix.All(char.IsDigit))
            {
                staticsName = string.IsNullOrEmpty(suffix) ? "statics.mul" : $"statics{suffix}.mul";
                staidxName = string.IsNullOrEmpty(suffix) ? "staidx.mul" : $"staidx{suffix}.mul";
                return true;
            }
        }

        return false;
    }

    private static bool TryGetBlockCount(long length, out int blockCount)
    {
        blockCount = 0;
        if (length <= 0)
        {
            return false;
        }

        if (length % MapMul.LandBlockBytes == 0)
        {
            blockCount = (int)(length / MapMul.LandBlockBytes);
            return blockCount > 0;
        }

        if ((length - 4) > 0 && (length - 4) % MapMul.LandBlockBytes == 0)
        {
            blockCount = (int)((length - 4) / MapMul.LandBlockBytes);
            return blockCount > 0;
        }

        return false;
    }
}

public enum MapConversionLogLevel
{
    Info,
    Warning,
    Error,
    Success
}

public readonly struct MapConversionLogEntry
{
    public MapConversionLogEntry(MapConversionLogLevel level, string message)
    {
        Level = level;
        Message = message;
    }

    public MapConversionLogLevel Level { get; }

    public string Message { get; }
}

public sealed class MapConversionOptions
{
    public bool StopOnError { get; set; }

    public int MaxErrorLogs { get; set; } = 100;

    public TileColorMap? TileColorMap { get; set; }

    public TileReplacementMap? TileReplacementMap { get; set; }

    public bool UseAltitudeXmlColors { get; set; }

    public CancellationToken? CancellationToken { get; set; }
}

public sealed class MapConversionAbortException : Exception
{
    public MapConversionAbortException(string message) : base(message)
    {
    }
}

public sealed class MapConversionReport
{
    public MapConversionReport(int totalTiles)
    {
        TotalTiles = totalTiles;
    }

    public int TotalTiles { get; }

    public int MissingTerrainColors { get; set; }

    public int MissingTerrainTiles { get; set; }

    public int FallbackTerrainColors { get; set; }

    public Dictionary<ushort, int> MissingColorsByTileId { get; } = new();

    public Dictionary<byte, int> MissingTilesByColorIndex { get; } = new();

    public void AddMissingColor(ushort tileId)
    {
        MissingColorsByTileId[tileId] = MissingColorsByTileId.TryGetValue(tileId, out var count) ? count + 1 : 1;
    }

    public void AddMissingTile(byte colorIndex)
    {
        MissingTilesByColorIndex[colorIndex] = MissingTilesByColorIndex.TryGetValue(colorIndex, out var count) ? count + 1 : 1;
    }

    public override string ToString()
    {
        if (MissingTerrainColors == 0 && MissingTerrainTiles == 0 && FallbackTerrainColors == 0)
        {
            return "No conversion gaps detected.";
        }

        return $"Missing colors: {MissingTerrainColors:N0}, missing tiles: {MissingTerrainTiles:N0}, fallback colors: {FallbackTerrainColors:N0}.";
    }

    public string FormatTopMissingColors(int limit = 5)
    {
        if (MissingColorsByTileId.Count == 0)
        {
            return "No missing color tiles.";
        }

        var entries = MissingColorsByTileId
            .OrderByDescending(pair => pair.Value)
            .Take(limit)
            .Select(pair => $"0x{pair.Key:X4}:{pair.Value}");

        return $"Missing colors by tileId: {string.Join(", ", entries)}";
    }

    public string FormatTopMissingTiles(int limit = 5)
    {
        if (MissingTilesByColorIndex.Count == 0)
        {
            return "No missing tile colors.";
        }

        var entries = MissingTilesByColorIndex
            .OrderByDescending(pair => pair.Value)
            .Take(limit)
            .Select(pair => $"0x{pair.Key:X2}:{pair.Value}");

        return $"Missing tiles by color: {string.Join(", ", entries)}";
    }
}

