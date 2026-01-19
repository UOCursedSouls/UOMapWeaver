using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using UOMapWeaver.App;
using UOMapWeaver.Core.Bmp;
using UOMapWeaver.Core;
using UOMapWeaver.Core.Map;
using UOMapWeaver.Core.MapTrans;
using UOMapWeaver.Core.ClientData;
using UOMapWeaver.Core.Statics;
using UOMapWeaver.Core.TileColors;
using UOMapWeaver.Core.TileReplace;
using static UOMapWeaver.App.Views.ViewHelpers;
using FieldState = UOMapWeaver.App.Views.ViewHelpers.FieldState;

namespace UOMapWeaver.App.Views;

public sealed partial class MapCopyView : UserControl, IAppStateView
{
    private (int width, int height)? _sourceSize;
    private (int width, int height)? _destSize;
    private Bitmap? _sourcePreviewBitmap;
    private Bitmap? _destPreviewBitmap;
    private RectInt? _sourceSelectionPixels;
    private bool _syncingRegionFields;
    private bool _isSelectingSource;
    private bool _isDraggingOverlay;
    private Point _sourceDragStart;
    private Point _overlayDragStart;
    private Point _overlayStartOffset;
    private Point _overlayDisplayOffset;
    private Size _overlayDisplaySize;
    private readonly List<PreviewEncodingOption> _previewEncodings = new();
    private readonly List<MapTransOption> _previewMapTransOptions = new();
    private readonly ZoomPanState _sourceZoom = new();
    private readonly ZoomPanState _destZoom = new();
    private readonly ZoomPanState _combinedZoom = new();
    private int _sourcePreviewOriginX;
    private int _sourcePreviewOriginY;
    private bool _loadingState;
    private readonly List<StaticsCopyModeOption> _staticsCopyModes = new();
    private readonly List<StaticsLayoutOption> _staticsLayoutOptions = new();
    private readonly List<StaticsZOption> _staticsZOptions = new();
    private readonly ObservableCollection<string> _missingTerrainEntries = new();
    private readonly ObservableCollection<string> _missingStaticsEntries = new();
    private readonly ObservableCollection<TileRemapItem> _terrainRemapItems = new();
    private readonly ObservableCollection<TileRemapItem> _staticsRemapItems = new();
    private TileValidationResult? _lastValidation;

    public MapCopyView()
    {
        InitializeComponent();
        MissingTerrainList.ItemsSource = _missingTerrainEntries;
        MissingStaticsList.ItemsSource = _missingStaticsEntries;
        TerrainRemapList.ItemsSource = _terrainRemapItems;
        StaticsRemapList.ItemsSource = _staticsRemapItems;
        SourcePreviewImage.SizeChanged += (_, _) => UpdateSourceSelectionVisual();
        DestPreviewImage.SizeChanged += (_, _) => UpdateOverlayFromSelection();
        CombinedPreviewImage.SizeChanged += (_, _) => UpdateCombinedOverlayFromSelection();
        LoadPreviewEncodings();
        LoadPreviewMapTransOptions();
        UpdatePreviewEncodingVisibility();
        LoadStaticsCopyModes();
        LoadStaticsLayouts();
        LoadStaticsZOptions();
        LoadState();
        EnsureTileReplacePath();
        UpdateClientInfo();
        UpdatePreviewEncodingVisibility();
        UpdateStatus();
        UpdatePreviewLabels();
        UpdatePreviewActionVisibility();
        if (string.IsNullOrWhiteSpace(OutputFolderBox.Text))
        {
            OutputFolderBox.Text = AppContext.BaseDirectory;
        }
        ConfigureZoomState(_sourceZoom, SourcePreviewScroll, SourcePreviewCanvas, SourcePreviewImage,
            () =>
            {
                UpdateSourceSelectionVisual();
                UpdateOverlayFromSelection();
            });
        ConfigureZoomState(_destZoom, DestPreviewScroll, DestPreviewCanvas, DestPreviewImage, UpdateOverlayFromSelection);
        ConfigureZoomState(_combinedZoom, CombinedPreviewScroll, CombinedPreviewCanvas, CombinedPreviewImage,
            UpdateCombinedOverlayFromSelection);
    }

    private async void OnBrowseSourceMap(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => SourceMapPathBox.Text = await PickFileAsync(this, "Select source map.mul", new[] { "mul", "uop" });

    private async void OnBrowseSourceStaIdx(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => SourceStaIdxPathBox.Text = await PickFileAsync(this, "Select source staidx.mul", new[] { "mul" });

    private async void OnBrowseSourceStatics(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => SourceStaticsPathBox.Text = await PickFileAsync(this, "Select source statics.mul", new[] { "mul" });

    private async void OnBrowseVerdata(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => VerdataPathBox.Text = await PickFileAsync(this, "Select verdata.mul", new[] { "mul" });

    private async void OnBrowseSourceClient(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => SourceClientFolderBox.Text = await PickFolderAsync(this, "Select source client folder");

    private async void OnBrowseDestClient(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => DestClientFolderBox.Text = await PickFolderAsync(this, "Select destination client folder");

    private async void OnBrowseDestMap(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => DestMapPathBox.Text = await PickFileAsync(this, "Select destination map.mul", new[] { "mul", "uop" });

    private async void OnBrowseDestStaIdx(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => DestStaIdxPathBox.Text = await PickFileAsync(this, "Select destination staidx.mul", new[] { "mul" });

    private async void OnBrowseDestStatics(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => DestStaticsPathBox.Text = await PickFileAsync(this, "Select destination statics.mul", new[] { "mul" });

    private async void OnBrowseOutputFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => OutputFolderBox.Text = await PickFolderAsync(this, "Select output folder");

    private async void OnBrowseSourceBmp(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => SourceBmpPathBox.Text = await PickFileAsync(this, "Select source BMP", new[] { "bmp" });

    private async void OnBrowseDestBmp(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => DestBmpPathBox.Text = await PickFileAsync(this, "Select destination BMP", new[] { "bmp" });

    private void OnSourceTextChanged(object? sender, TextChangedEventArgs e)
    {
        AutoPopulateStatics(SourceMapPathBox.Text, SourceStaIdxPathBox, SourceStaticsPathBox);
        AutoPopulateVerdata(SourceMapPathBox.Text, VerdataPathBox);
        UpdateStatus();
        UpdatePreviewActionVisibility();
        SaveState();
    }

    private void OnDestTextChanged(object? sender, TextChangedEventArgs e)
    {
        AutoPopulateStatics(DestMapPathBox.Text, DestStaIdxPathBox, DestStaticsPathBox);
        UpdateStatus();
        UpdatePreviewActionVisibility();
        SaveState();
    }

    private void OnClientPathChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateClientInfo();
        SaveState();
    }

    private void OnVerdataTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateStatus();
        SaveState();
    }

    private void OnOutputFolderChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateStatus();
        SaveState();
    }

    private void OnSourceRegionChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncingRegionFields)
        {
            return;
        }

        if (TryGetSourceRectFromFields(out var rect))
        {
            _sourceSelectionPixels = rect;
            UpdateSourceSelectionVisual();
            UpdateOverlayFromSelection();
            UpdatePreviewLabels();
        }

        SaveState();
    }

    private void OnDestStartChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncingRegionFields)
        {
            return;
        }

        if (TryParseInt(DestStartXBox.Text, out var destX) && TryParseInt(DestStartYBox.Text, out var destY))
        {
            UpdateOverlayPositionFromDestStart(destX, destY);
            UpdateCombinedOverlayFromSelection();
            UpdatePreviewLabels();
        }

        SaveState();
    }

    private void OnOptionChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ApplyCopyOptionState();
        if (UseVerdataCheckBox.IsChecked == true)
        {
            AutoPopulateVerdata(SourceMapPathBox.Text, VerdataPathBox);
        }
        UpdateStatus();
        SaveState();
    }

    private void OnFixedZChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyCopyOptionState();
        UpdateStatus();
        SaveState();
    }

    private void OnOptionChanged(object? sender, SelectionChangedEventArgs e)
    {
        ApplyCopyOptionState();
        UpdateStatus();
        SaveState();
    }

    private void OnPreviewEncodingChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdatePreviewEncodingVisibility();
        UpdateStatus();
        SaveState();
    }

    private async void OnBrowsePreviewMapTrans(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = await PickFileAsync(this, "Select MapTrans profile", new[] { "txt", "json" });
        if (!string.IsNullOrWhiteSpace(path))
        {
            AddPreviewMapTransOption(path);
            UpdatePreviewEncodingVisibility();
        }
    }

    private async void OnBrowsePreviewTileJson(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => PreviewTileJsonPathBox.Text = await PickFileAsync(this, "Select Tile Color JSON", new[] { "json" });

    private void OnPreviewTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdatePreviewEncodingVisibility();
        UpdateStatus();
        SaveState();
    }

    private void OnReplacePathChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateStatus();
        SaveState();
    }

    private async void OnGeneratePreview(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var sourceMapPath = SourceMapPathBox.Text;
        var destMapPath = DestMapPathBox.Text;

        if (string.IsNullOrWhiteSpace(sourceMapPath) || !File.Exists(sourceMapPath) ||
            string.IsNullOrWhiteSpace(destMapPath) || !File.Exists(destMapPath))
        {
            AppStatus.SetError("Map paths are required to generate preview BMPs.");
            return;
        }

        if (!MapConversion.TryResolveMapSizeFromFile(sourceMapPath, out var sourceWidth, out var sourceHeight) ||
            !MapConversion.TryResolveMapSizeFromFile(destMapPath, out var destWidth, out var destHeight))
        {
            AppStatus.SetError("Map sizes not detected.");
            return;
        }

        if (AreSourceRegionFieldsEmpty())
        {
            var fullRect = new RectInt(0, 0, sourceWidth, sourceHeight);
            _sourceSelectionPixels = fullRect;
            UpdateRegionFieldsFromSelection(fullRect);
            UpdateSourceSelectionVisual();
            UpdateOverlayFromSelection();
            SaveState();
        }

        if (!TryGetSourceRectFromFields(out var cropRect))
        {
            if (TryGetFullMapRectFromFields(sourceWidth, sourceHeight, out cropRect))
            {
                _sourceSelectionPixels = cropRect;
                UpdateRegionFieldsFromSelection(cropRect);
            }
            else
            {
                AppStatus.SetError("Set a source area before generating preview BMPs.");
                return;
            }
        }

        if (!ValidateBounds(cropRect, sourceWidth, sourceHeight, (0, 0), sourceWidth, sourceHeight, out var sourceError))
        {
            AppStatus.SetError(sourceError);
            return;
        }

        PreviewGenerateButton.IsEnabled = false;
        SetBusy(true);

        using var cancelSource = new CancellationTokenSource();
        AppStatus.SetCancelSource(cancelSource);

        try
        {
            AppStatus.SetProgress(0, true);
            var previewRoot = Path.Combine(UOMapWeaverDataPaths.DataRoot, "Preview");
            Directory.CreateDirectory(previewRoot);

            var sourceName = Path.GetFileNameWithoutExtension(sourceMapPath);
            var destName = Path.GetFileNameWithoutExtension(destMapPath);
            var sourcePreviewPath = Path.Combine(previewRoot,
                $"{sourceName}_preview_{cropRect.X}_{cropRect.Y}_{cropRect.Width}x{cropRect.Height}.bmp");
            var destPreviewPath = Path.Combine(previewRoot,
                $"{destName}_preview_{destWidth}x{destHeight}.bmp");

            AppStatus.AppendLog("Map copy preview generation start.", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Source map: {sourceMapPath}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Dest map: {destMapPath}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Source crop: {cropRect}", AppStatusSeverity.Info);

            var useVerdata = UseVerdataCheckBox.IsChecked == true;
            var applyVerdataTerrain = VerdataTerrainCheckBox.IsChecked == true;
            VerdataMul? verdata = null;
            if (useVerdata)
            {
                var verdataPath = VerdataPathBox.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(verdataPath) || !File.Exists(verdataPath))
                {
                    AppStatus.SetError("Verdata.mul not found.");
                    return;
                }

                var blockCount = (sourceWidth / MapMul.BlockSize) * (sourceHeight / MapMul.BlockSize);
                var mapFileId = ParseOptionalInt(VerdataMapFileIdBox.Text);
                var staticsFileId = ParseOptionalInt(VerdataStaticsFileIdBox.Text);
                verdata = await Task.Run(() => VerdataMul.Load(verdataPath, blockCount, mapFileId, staticsFileId), cancelSource.Token);
                AppStatus.AppendLog($"Verdata: {verdataPath}", AppStatusSeverity.Info);
                AppStatus.AppendLog($"Verdata map patches: {verdata.MapPatchCount} (fileId {verdata.MapFileId?.ToString() ?? "n/a"})",
                    AppStatusSeverity.Info);
                AppStatus.AppendLog($"Verdata apply terrain: {applyVerdataTerrain}", AppStatusSeverity.Info);
            }

            await Task.Run(() =>
            {
                var totalRows = (long)cropRect.Height + destHeight;
                var processedRows = 0L;
                var lastProgress = -1;

                var encoding = GetPreviewEncoding();
                var useTileIndex = encoding == PreviewEncoding.TileIndexRgb;
                var useMapTrans = encoding == PreviewEncoding.MapTrans;
                var useTileJson = encoding == PreviewEncoding.TileJson;

                TileColorMap? tileMap = null;
                MapTransProfile? profile = null;
                Dictionary<ushort, List<MapTransEntry>>? lookup = null;
                BmpPaletteEntry[]? palette = null;
                var output24 = useTileIndex;

                if (useMapTrans)
                {
                    var selected = GetSelectedPreviewMapTransPath();
                    if (string.IsNullOrWhiteSpace(selected))
                    {
                        throw new InvalidOperationException("Select a MapTrans profile for preview.");
                    }

                    profile = MapTransParser.LoadFromFile(selected);
                    if (selected.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    {
                        AppStatus.AppendLog($"MapTrans loaded from TXT: {Path.GetFileName(selected)}", AppStatusSeverity.Warning);
                    }
                    palette = LoadPreviewPalette(profile.PalettePath);
                    ApplyUnknownPaletteColor(palette);
                    lookup = BuildTileLookup(profile);
                    output24 = false;
                }
                else if (useTileJson)
                {
                    if (string.IsNullOrWhiteSpace(PreviewTileJsonPathBox.Text) || !File.Exists(PreviewTileJsonPathBox.Text))
                    {
                        throw new InvalidOperationException("Tile Color JSON not found for preview.");
                    }

                    tileMap = TileColorMapSerializer.Load(PreviewTileJsonPathBox.Text);
                    if (tileMap.Mode == TileColorMode.Rgb24)
                    {
                        output24 = true;
                    }
                    else
                    {
                        palette = tileMap.Palette ?? Bmp8Codec.CreateGrayscalePalette();
                        ApplyUnknownPaletteColor(palette);
                        output24 = false;
                    }
                }

                using var sourceReader = new MapMulRowReader(sourceMapPath, sourceWidth, sourceHeight);
                using var destReader = new MapMulRowReader(destMapPath, destWidth, destHeight);

                using var sourceWriter24 = output24 ? new Bmp24StreamWriter(sourcePreviewPath, cropRect.Width, cropRect.Height) : null;
                using var destWriter24 = output24 ? new Bmp24StreamWriter(destPreviewPath, destWidth, destHeight) : null;
                using var sourceWriter8 = output24 ? null : new Bmp8StreamWriter(sourcePreviewPath, cropRect.Width, cropRect.Height, palette!);
                using var destWriter8 = output24 ? null : new Bmp8StreamWriter(destPreviewPath, destWidth, destHeight, palette!);

                var sourceRow = new LandTile[sourceWidth];
                var destRow = new LandTile[destWidth];
                var sourcePixels24 = output24 ? new byte[cropRect.Width * 3] : Array.Empty<byte>();
                var destPixels24 = output24 ? new byte[destWidth * 3] : Array.Empty<byte>();
                var sourcePixels8 = output24 ? Array.Empty<byte>() : new byte[cropRect.Width];
                var destPixels8 = output24 ? Array.Empty<byte>() : new byte[destWidth];
                var missingColors = 0;

                for (var y = cropRect.Height - 1; y >= 0; y--)
                {
                    cancelSource.Token.ThrowIfCancellationRequested();
                    sourceReader.ReadRow(cropRect.Y + y, sourceRow);
                    if (verdata is not null && applyVerdataTerrain && verdata.MapPatchCount > 0)
                    {
                        ApplyVerdataRow(verdata, cropRect.Y + y, sourceRow, sourceWidth, sourceHeight);
                    }

                    for (var x = 0; x < cropRect.Width; x++)
                    {
                        var tile = sourceRow[cropRect.X + x];
                        if (output24)
                        {
                            var index = x * 3;
                            if (useTileJson && tileMap?.Mode == TileColorMode.Rgb24)
                            {
                                if (!tileMap.TryGetColor(tile.TileId, out var color))
                                {
                                    color = tileMap.UnknownColor;
                                    missingColors++;
                                }

                                sourcePixels24[index] = color.R;
                                sourcePixels24[index + 1] = color.G;
                                sourcePixels24[index + 2] = color.B;
                            }
                            else
                            {
                                sourcePixels24[index] = (byte)(tile.TileId >> 8);
                                sourcePixels24[index + 1] = (byte)(tile.TileId & 0xFF);
                                sourcePixels24[index + 2] = 0;
                            }
                        }
                        else
                        {
                            if (useTileJson)
                            {
                                if (!tileMap!.TryGetColorIndex(tile.TileId, out var colorIndex))
                                {
                                    colorIndex = 255;
                                    missingColors++;
                                }

                                sourcePixels8[x] = colorIndex;
                            }
                            else
                            {
                                if (!TryResolveTerrainColor(lookup!, tile.TileId, tile.Z, out var colorIndex))
                                {
                                    colorIndex = 255;
                                    missingColors++;
                                }

                                sourcePixels8[x] = colorIndex;
                            }
                        }
                    }

                    if (output24)
                    {
                        sourceWriter24!.WriteRow(sourcePixels24);
                    }
                    else
                    {
                        sourceWriter8!.WriteRow(sourcePixels8);
                    }
                    processedRows++;
                    ReportProgress(processedRows, totalRows, ref lastProgress);
                }

                for (var y = destHeight - 1; y >= 0; y--)
                {
                    cancelSource.Token.ThrowIfCancellationRequested();
                    destReader.ReadRow(y, destRow);

                    for (var x = 0; x < destWidth; x++)
                    {
                        var tile = destRow[x];
                        if (output24)
                        {
                            var index = x * 3;
                            if (useTileJson && tileMap?.Mode == TileColorMode.Rgb24)
                            {
                                if (!tileMap.TryGetColor(tile.TileId, out var color))
                                {
                                    color = tileMap.UnknownColor;
                                    missingColors++;
                                }

                                destPixels24[index] = color.R;
                                destPixels24[index + 1] = color.G;
                                destPixels24[index + 2] = color.B;
                            }
                            else
                            {
                                destPixels24[index] = (byte)(tile.TileId >> 8);
                                destPixels24[index + 1] = (byte)(tile.TileId & 0xFF);
                                destPixels24[index + 2] = 0;
                            }
                        }
                        else
                        {
                            if (useTileJson)
                            {
                                if (!tileMap!.TryGetColorIndex(tile.TileId, out var colorIndex))
                                {
                                    colorIndex = 255;
                                    missingColors++;
                                }

                                destPixels8[x] = colorIndex;
                            }
                            else
                            {
                                if (!TryResolveTerrainColor(lookup!, tile.TileId, tile.Z, out var colorIndex))
                                {
                                    colorIndex = 255;
                                    missingColors++;
                                }

                                destPixels8[x] = colorIndex;
                            }
                        }
                    }

                    if (output24)
                    {
                        destWriter24!.WriteRow(destPixels24);
                    }
                    else
                    {
                        destWriter8!.WriteRow(destPixels8);
                    }
                    processedRows++;
                    ReportProgress(processedRows, totalRows, ref lastProgress);
                }

                if (missingColors > 0)
                {
                    AppStatus.AppendLog($"Preview generation: {missingColors:N0} tiles missing color mapping.", AppStatusSeverity.Warning);
                }
            }, cancelSource.Token);

            SourceBmpPathBox.Text = sourcePreviewPath;
            DestBmpPathBox.Text = destPreviewPath;
            LoadPreviewBitmap(sourcePreviewPath, isSource: true);
            LoadPreviewBitmap(destPreviewPath, isSource: false);

            AppStatus.SetSuccess("Preview BMPs generated.");
            AppStatus.AppendLog($"Preview BMPs saved to {previewRoot}", AppStatusSeverity.Success);
        }
        catch (Exception ex)
        {
            AppStatus.SetError($"Preview generation failed: {ex.Message}");
            AppStatus.AppendLog($"Preview generation failed: {ex.Message}", AppStatusSeverity.Error);
        }
        finally
        {
            AppStatus.SetProgress(0, false);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
            UpdatePreviewActionVisibility();
            PreviewGenerateButton.IsEnabled = true;
        }
    }

    private async void OnScanValidation(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var sourceMapPath = SourceMapPathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(sourceMapPath) || !File.Exists(sourceMapPath))
        {
            AppStatus.SetError("Source map.mul is required for validation.");
            return;
        }

        if (!MapConversion.TryResolveMapSizeFromFile(sourceMapPath, out var sourceWidth, out var sourceHeight))
        {
            AppStatus.SetError("Source map size not detected.");
            return;
        }

        if (!TryGetSourceRectFromFields(out var rect))
        {
            if (!TryGetFullMapRectFromFields(sourceWidth, sourceHeight, out rect))
            {
                AppStatus.SetError("Set a source area before validation.");
                return;
            }
        }

        var useVerdata = UseVerdataCheckBox.IsChecked == true;
        var applyVerdataTerrain = VerdataTerrainCheckBox.IsChecked == true;
        var applyVerdataStatics = VerdataStaticsCheckBox.IsChecked == true;
        var verdataPath = VerdataPathBox.Text?.Trim() ?? string.Empty;

        var sourceClientFolder = SourceClientFolderBox.Text?.Trim() ?? string.Empty;
        var destClientFolder = DestClientFolderBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(destClientFolder) || !Directory.Exists(destClientFolder))
        {
            AppStatus.SetError("Destination client folder is required for validation.");
            return;
        }

        ValidationScanButton.IsEnabled = false;
        SetBusy(true);
        using var cancelSource = new CancellationTokenSource();
        AppStatus.SetCancelSource(cancelSource);
        AppStatus.SetProgress(0, true);

        try
        {
            var sourceStaIdxPath = SourceStaIdxPathBox.Text?.Trim() ?? string.Empty;
            var sourceStaticsPath = SourceStaticsPathBox.Text?.Trim() ?? string.Empty;

            var clientInfo = ResolveClientInfo(sourceClientFolder, destClientFolder);
            var blockCount = (sourceWidth / MapMul.BlockSize) * (sourceHeight / MapMul.BlockSize);
            var mapFileId = ParseOptionalInt(VerdataMapFileIdBox.Text);
            var staticsFileId = ParseOptionalInt(VerdataStaticsFileIdBox.Text);

            var result = await Task.Run(() =>
            {
                VerdataMul? verdata = null;
                if (useVerdata)
                {
                    if (string.IsNullOrWhiteSpace(verdataPath) || !File.Exists(verdataPath))
                    {
                        throw new InvalidOperationException("Verdata.mul not found.");
                    }

                    verdata = VerdataMul.Load(verdataPath, blockCount, mapFileId, staticsFileId);
                }

                var terrainCounts = new Dictionary<ushort, int>();
                using (var reader = new MapMulRowReader(sourceMapPath, sourceWidth, sourceHeight))
                {
                    var row = new LandTile[sourceWidth];
                    for (var y = rect.Y; y < rect.Bottom; y++)
                    {
                        cancelSource.Token.ThrowIfCancellationRequested();
                        reader.ReadRow(y, row);
                        if (verdata is not null && applyVerdataTerrain && verdata.MapPatchCount > 0)
                        {
                            ApplyVerdataRow(verdata, y, row, sourceWidth, sourceHeight);
                        }

                        for (var x = rect.X; x < rect.Right; x++)
                        {
                            var tileId = row[x].TileId;
                            terrainCounts[tileId] = terrainCounts.TryGetValue(tileId, out var count) ? count + 1 : 1;
                        }
                    }
                }

                var staticCounts = new Dictionary<ushort, int>();
                if (File.Exists(sourceStaIdxPath) && File.Exists(sourceStaticsPath))
                {
                    var staticsBlocks = StaticMulCodec.ReadStatics(sourceStaIdxPath, sourceStaticsPath, sourceWidth, sourceHeight);
                    if (verdata is not null && applyVerdataStatics && verdata.StaticsPatchCount > 0)
                    {
                        verdata.ApplyToStatics(staticsBlocks);
                    }

                    CountStaticsTileIdsInRect(staticsBlocks, sourceWidth, sourceHeight, rect, GetStaticsLayout(), staticCounts);
                }

                using var sourceArt = clientInfo.SourceArtIndexPath is not null && clientInfo.SourceArtPath is not null
                    ? ArtIndex.Load(clientInfo.SourceArtIndexPath, clientInfo.SourceArtPath)
                    : null;
                using var destArt = clientInfo.DestArtIndexPath is not null && clientInfo.DestArtPath is not null
                    ? ArtIndex.Load(clientInfo.DestArtIndexPath, clientInfo.DestArtPath)
                    : null;

                var missingTerrain = new Dictionary<ushort, int>();
                var missingStatics = new Dictionary<ushort, int>();
                var diffTerrain = new Dictionary<ushort, int>();
                var diffStatics = new Dictionary<ushort, int>();

                foreach (var (tileId, count) in terrainCounts)
                {
                    var artIndex = tileId;
                    if (destArt is null || !destArt.HasEntry(artIndex))
                    {
                        missingTerrain[tileId] = count;
                        continue;
                    }

                    if (sourceArt is not null && sourceArt.HasEntry(artIndex))
                    {
                        var sourceHash = sourceArt.GetEntryHashHex(artIndex);
                        var destHash = destArt.GetEntryHashHex(artIndex);
                        if (!string.Equals(sourceHash, destHash, StringComparison.OrdinalIgnoreCase))
                        {
                            diffTerrain[tileId] = count;
                        }
                    }
                }

                foreach (var (tileId, count) in staticCounts)
                {
                    var artIndex = 0x4000 + tileId;
                    if (destArt is null || !destArt.HasEntry(artIndex))
                    {
                        missingStatics[tileId] = count;
                        continue;
                    }

                    if (sourceArt is not null && sourceArt.HasEntry(artIndex))
                    {
                        var sourceHash = sourceArt.GetEntryHashHex(artIndex);
                        var destHash = destArt.GetEntryHashHex(artIndex);
                        if (!string.Equals(sourceHash, destHash, StringComparison.OrdinalIgnoreCase))
                        {
                            diffStatics[tileId] = count;
                        }
                    }
                }

                return new TileValidationResult(
                    missingTerrain,
                    missingStatics,
                    diffTerrain,
                    diffStatics,
                    terrainCounts.Count,
                    staticCounts.Count,
                    destArt is not null,
                    sourceArt is not null);
            }, cancelSource.Token);

            ApplyValidationResult(result, clientInfo);
        }
        catch (OperationCanceledException)
        {
            AppStatus.SetWarning("Validation cancelled.");
        }
        catch (Exception ex)
        {
            AppStatus.SetError($"Validation error: {ex.Message}");
        }
        finally
        {
            ValidationScanButton.IsEnabled = true;
            AppStatus.SetProgress(0, false);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
        }
    }

    private void OnClearValidation(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _missingTerrainEntries.Clear();
        _missingStaticsEntries.Clear();
        _lastValidation = null;
        ValidationSummaryText.Text = string.Empty;
        ValidationMissingTerrainText.Text = string.Empty;
        ValidationMissingStaticsText.Text = string.Empty;
        AppStatus.AppendLog("Validation results cleared.", AppStatusSeverity.Info);
    }

    private async void OnBrowseTileReplace(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => TileReplacePathBox.Text = await PickFileAsync(this, "Select tile replace JSON", new[] { "json" });

    private void OnLoadTileReplace(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = TileReplacePathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            AppStatus.SetError("Tile replace JSON not found.");
            return;
        }

        try
        {
            var map = TileReplacementMapSerializer.Load(path);
            LoadRemapFromMap(map);
            TileReplaceStatusText.Text = $"Loaded {map.Terrain.Count:N0} terrain, {map.Statics.Count:N0} statics.";
            AppStatus.AppendLog(TileReplaceStatusText.Text, AppStatusSeverity.Info);
        }
        catch (Exception ex)
        {
            AppStatus.SetError($"Tile replace load error: {ex.Message}");
        }
    }

    private void OnSaveTileReplace(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var map = BuildReplacementMapFromUi();
        if (map.Terrain.Count == 0 && map.Statics.Count == 0)
        {
            AppStatus.SetWarning("No replacement entries to save.");
            return;
        }

        var path = TileReplacePathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(UOMapWeaverDataPaths.TileReplaceRoot, "UOMapWeaver_TileReplace.json");
            TileReplacePathBox.Text = path;
        }

        try
        {
            map.SourceClientPath = SourceClientFolderBox.Text?.Trim();
            map.DestClientPath = DestClientFolderBox.Text?.Trim();
            TileReplacementMapSerializer.Save(path, map);
            TileReplaceStatusText.Text = $"Saved {map.Terrain.Count:N0} terrain, {map.Statics.Count:N0} statics.";
            AppStatus.AppendLog(TileReplaceStatusText.Text, AppStatusSeverity.Success);
        }
        catch (Exception ex)
        {
            AppStatus.SetError($"Tile replace save error: {ex.Message}");
        }
    }

    private void OnLoadMissingForReplace(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_lastValidation is null)
        {
            AppStatus.SetWarning("Run validation before loading missing tiles.");
            return;
        }

        LoadRemapFromMissing(_lastValidation.MissingTerrain, _lastValidation.MissingStatics);
        TileReplaceStatusText.Text = $"Loaded {_lastValidation.MissingTerrain.Count:N0} terrain, {_lastValidation.MissingStatics.Count:N0} statics.";
        AppStatus.AppendLog(TileReplaceStatusText.Text, AppStatusSeverity.Info);
    }

    private void OnClearRemap(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _terrainRemapItems.Clear();
        _staticsRemapItems.Clear();
        TileReplaceStatusText.Text = "Remap list cleared.";
        AppStatus.AppendLog(TileReplaceStatusText.Text, AppStatusSeverity.Info);
    }

    private async void OnCopy(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SetBusy(true);

        using var cancelSource = new CancellationTokenSource();
        AppStatus.SetCancelSource(cancelSource);

        try
        {
            if (!TryResolveSizes(out var sourceSize, out var destSize))
            {
                StatusText.Text = "Map sizes not detected.";
                AppStatus.SetError(StatusText.Text);
                return;
            }

            VerdataMul? verdata = null;
            var applyVerdataTerrain = VerdataTerrainCheckBox.IsChecked == true;
            var applyVerdataStatics = VerdataStaticsCheckBox.IsChecked == true;
            if (UseVerdataCheckBox.IsChecked == true)
            {
                if (!TryLoadVerdata(sourceSize.width, sourceSize.height, out verdata, reportToStatus: true))
                {
                    return;
                }
            }

            if (!TryGetRegion(out var sourceRect, out var destStart))
            {
                StatusText.Text = "Invalid coordinates.";
                AppStatus.SetError(StatusText.Text);
                return;
            }

            if (!ValidateBounds(sourceRect, sourceSize.width, sourceSize.height, destStart, destSize.width, destSize.height,
                    out var boundError))
            {
                StatusText.Text = boundError;
                AppStatus.SetError(StatusText.Text);
                return;
            }

            if (!File.Exists(SourceMapPathBox.Text ?? string.Empty) ||
                !File.Exists(DestMapPathBox.Text ?? string.Empty))
            {
                StatusText.Text = "Map.mul paths are required.";
                AppStatus.SetError(StatusText.Text);
                return;
            }

            var sourceMapPath = SourceMapPathBox.Text!;
            var sourceStaIdxPath = SourceStaIdxPathBox.Text ?? string.Empty;
            var sourceStaticsPath = SourceStaticsPathBox.Text ?? string.Empty;
            var originalDestMapPath = DestMapPathBox.Text!;
            var originalDestStaIdxPath = DestStaIdxPathBox.Text ?? string.Empty;
            var originalDestStaticsPath = DestStaticsPathBox.Text ?? string.Empty;
            var destMapPath = originalDestMapPath;
            var destStaIdxPath = originalDestStaIdxPath;
            var destStaticsPath = originalDestStaticsPath;

            var copyStatics = CopyStaticsCheckBox.IsChecked == true;
            var copyMap = CopyMapCheckBox.IsChecked == true;
            var saveAsNew = SaveAsNewMapCheckBox.IsChecked == true;
            var overwriteTerrain = OverwriteTerrainCheckBox.IsChecked == true;
            var overwriteStatics = OverwriteStaticsCheckBox.IsChecked == true;
            var sourceHeight = sourceSize.height;
            var outputFolder = OutputFolderBox.Text ?? string.Empty;
            var staticsMode = GetStaticsCopyMode();
            var staticsLayout = GetStaticsLayout();
            var staticsZMode = GetStaticsZMode();
            var staticsFixedZ = (sbyte)0;

            if (copyStatics && staticsZMode == StaticsZMode.Fixed)
            {
                if (!TryParseInt(StaticsFixedZBox.Text, out var fixedZ) ||
                    fixedZ < sbyte.MinValue || fixedZ > sbyte.MaxValue)
                {
                    StatusText.Text = "Static Z must be between -128 and 127.";
                    AppStatus.SetError(StatusText.Text);
                    return;
                }

                staticsFixedZ = (sbyte)fixedZ;
            }

            if (copyStatics)
            {
                ResolveStaticsPaths(sourceMapPath, sourceStaIdxPath, sourceStaticsPath,
                    out sourceStaIdxPath, out sourceStaticsPath);
                ResolveStaticsPaths(originalDestMapPath, originalDestStaIdxPath, originalDestStaticsPath,
                    out originalDestStaIdxPath, out originalDestStaticsPath);

                destStaIdxPath = originalDestStaIdxPath;
                destStaticsPath = originalDestStaticsPath;

                if (string.IsNullOrWhiteSpace(sourceStaIdxPath) || string.IsNullOrWhiteSpace(sourceStaticsPath))
                {
                    StatusText.Text = "Source statics paths not resolved.";
                    AppStatus.SetError(StatusText.Text);
                    AppStatus.AppendLog(StatusText.Text, AppStatusSeverity.Error);
                    return;
                }

                if (string.IsNullOrWhiteSpace(destStaIdxPath) || string.IsNullOrWhiteSpace(destStaticsPath))
                {
                    StatusText.Text = "Destination statics paths not resolved.";
                    AppStatus.SetError(StatusText.Text);
                    AppStatus.AppendLog(StatusText.Text, AppStatusSeverity.Error);
                    return;
                }
            }

            if (saveAsNew)
            {
                if (string.IsNullOrWhiteSpace(outputFolder) || !Directory.Exists(outputFolder))
                {
                    StatusText.Text = "Output folder not found.";
                    AppStatus.SetError(StatusText.Text);
                    return;
                }

                destMapPath = BuildCopyPath(destMapPath, outputFolder);
                if (!string.IsNullOrWhiteSpace(destStaIdxPath))
                {
                    destStaIdxPath = BuildCopyPath(destStaIdxPath, outputFolder);
                }

                if (!string.IsNullOrWhiteSpace(destStaticsPath))
                {
                    destStaticsPath = BuildCopyPath(destStaticsPath, outputFolder);
                }
            }

            var filesToOverwrite = new List<string>();
            if (copyMap || saveAsNew)
            {
                filesToOverwrite.Add(destMapPath);
            }
            if (copyStatics)
            {
                if (!string.IsNullOrWhiteSpace(destStaIdxPath))
                {
                    filesToOverwrite.Add(destStaIdxPath);
                }

                if (!string.IsNullOrWhiteSpace(destStaticsPath))
                {
                    filesToOverwrite.Add(destStaticsPath);
                }
            }

            var overwriteTargets = filesToOverwrite.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
            if (overwriteTargets.Length > 0 &&
                !await ConfirmOverwriteAsync("Overwrite destination files?", overwriteTargets))
            {
                StatusText.Text = "Copy cancelled.";
                AppStatus.SetWarning(StatusText.Text);
                return;
            }

            var effectiveSourceRect = sourceRect;
            var effectiveDestStart = destStart;
            if (copyStatics && staticsMode == StaticsCopyMode.BlockReplaceAligned)
            {
                var snappedRect = SnapRectToBlocks(sourceRect, sourceSize.width, sourceSize.height);
                var snappedDest = SnapPointToBlock(destStart, destSize.width, destSize.height);
                if (!snappedRect.Equals(sourceRect) || snappedDest != destStart)
                {
                    AppStatus.AppendLog(
                        $"Copy uses /8 alignment: rect {sourceRect} -> {snappedRect}, dest {destStart.x},{destStart.y} -> {snappedDest.x},{snappedDest.y}",
                        AppStatusSeverity.Warning);
                }

                effectiveSourceRect = snappedRect;
                effectiveDestStart = snappedDest;
            }
            if (!effectiveSourceRect.Equals(sourceRect) || effectiveDestStart != destStart)
            {
                if (!ValidateBounds(effectiveSourceRect, sourceSize.width, sourceSize.height, effectiveDestStart, destSize.width, destSize.height,
                        out var alignedError))
                {
                    StatusText.Text = alignedError;
                    AppStatus.SetError(StatusText.Text);
                    return;
                }
            }

            AppStatus.AppendLog("Map copy start.", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Source map: {SourceMapPathBox.Text}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Dest map: {destMapPath}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Source rect: {sourceRect}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Dest start: {destStart.x},{destStart.y}", AppStatusSeverity.Info);
            if (!effectiveSourceRect.Equals(sourceRect) || effectiveDestStart != destStart)
            {
                AppStatus.AppendLog($"Effective rect: {effectiveSourceRect}", AppStatusSeverity.Info);
                AppStatus.AppendLog($"Effective dest: {effectiveDestStart.x},{effectiveDestStart.y}", AppStatusSeverity.Info);
            }
            AppStatus.AppendLog($"Source size: {sourceSize.width}x{sourceSize.height} (blocks {sourceSize.width / MapMul.BlockSize}x{sourceSize.height / MapMul.BlockSize})",
                AppStatusSeverity.Info);
            AppStatus.AppendLog($"Dest size: {destSize.width}x{destSize.height} (blocks {destSize.width / MapMul.BlockSize}x{destSize.height / MapMul.BlockSize})",
                AppStatusSeverity.Info);
            AppStatus.AppendLog($"Copy statics: {copyStatics}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Copy map: {copyMap}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Save as new map: {saveAsNew}", AppStatusSeverity.Info);
            if (copyStatics)
            {
                AppStatus.AppendLog($"Source statics: {sourceStaIdxPath}, {sourceStaticsPath}", AppStatusSeverity.Info);
                AppStatus.AppendLog($"Dest statics: {destStaIdxPath}, {destStaticsPath}", AppStatusSeverity.Info);
                AppStatus.AppendLog($"Overwrite statics: {overwriteStatics}", AppStatusSeverity.Info);
                AppStatus.AppendLog($"Statics copy mode: {staticsMode}", AppStatusSeverity.Info);
                AppStatus.AppendLog($"Statics layout: {staticsLayout}", AppStatusSeverity.Info);
                AppStatus.AppendLog($"Statics Z mode: {staticsZMode}", AppStatusSeverity.Info);
                if (staticsZMode == StaticsZMode.Fixed)
                {
                    AppStatus.AppendLog($"Statics fixed Z: {staticsFixedZ}", AppStatusSeverity.Info);
                }
            }
            if (verdata is not null)
            {
                AppStatus.AppendLog($"Verdata: {VerdataPathBox.Text}", AppStatusSeverity.Info);
                AppStatus.AppendLog($"Verdata map patches: {verdata.MapPatchCount} (fileId {verdata.MapFileId?.ToString() ?? "n/a"})",
                    AppStatusSeverity.Info);
                AppStatus.AppendLog($"Verdata static patches: {verdata.StaticsPatchCount} (fileId {verdata.StaticsFileId?.ToString() ?? "n/a"})",
                    AppStatusSeverity.Info);
                AppStatus.AppendLog($"Verdata apply: terrain={applyVerdataTerrain}, statics={applyVerdataStatics}", AppStatusSeverity.Info);
            }

            var tileReplacements = BuildReplacementMap();
            if (ApplyTileReplaceCheckBox.IsChecked == true && tileReplacements is null)
            {
                AppStatus.AppendLog("Tile replace enabled but no replacements found. Skipping.", AppStatusSeverity.Warning);
            }
            else if (tileReplacements is not null)
            {
                AppStatus.AppendLog(
                    $"Tile replace: terrain {tileReplacements.Terrain.Count:N0}, statics {tileReplacements.Statics.Count:N0}.",
                    AppStatusSeverity.Info);
            }

            LogFileInfo("Source map", sourceMapPath);
            LogFileInfo("Dest map", destMapPath);
            if (copyStatics)
            {
                LogFileInfo("Source staidx", sourceStaIdxPath);
                LogFileInfo("Source statics", sourceStaticsPath);
                LogFileInfo("Dest staidx", destStaIdxPath);
                LogFileInfo("Dest statics", destStaticsPath);
            }

            AppStatus.SetProgress(0, true);
            var progress = new Progress<int>(percent => AppStatus.SetProgress(percent, true));

            await Task.Run(() =>
            {
                var zMode = staticsZMode;
                LandTile[]? sourceTiles = null;
                LandTile[]? destTiles = null;

                if (saveAsNew)
                {
                    File.Copy(originalDestMapPath, destMapPath, overwrite: true);
                    if (copyStatics)
                    {
                        if (!string.IsNullOrWhiteSpace(originalDestStaIdxPath) && File.Exists(originalDestStaIdxPath))
                        {
                            File.Copy(originalDestStaIdxPath, destStaIdxPath, overwrite: true);
                        }

                        if (!string.IsNullOrWhiteSpace(originalDestStaticsPath) && File.Exists(originalDestStaticsPath))
                        {
                            File.Copy(originalDestStaticsPath, destStaticsPath, overwrite: true);
                        }
                    }
                }

                if (copyMap || (copyStatics && zMode == StaticsZMode.OffsetByTerrain))
                {
                    sourceTiles = MapMulCodec.ReadLandTiles(sourceMapPath, sourceSize.width, sourceSize.height);
                    destTiles = MapMulCodec.ReadLandTiles(destMapPath, destSize.width, destSize.height);
                    if (verdata is not null && applyVerdataTerrain && verdata.MapPatchCount > 0)
                    {
                        verdata.ApplyToLandTiles(sourceTiles, sourceSize.width, sourceSize.height);
                    }
                }

                if (copyStatics && zMode == StaticsZMode.OffsetByTerrain && (sourceTiles is null || destTiles is null))
                {
                    AppStatus.AppendLog("Statics Z offset requested but terrain tiles were not loaded. Using Keep Z.", AppStatusSeverity.Warning);
                    zMode = StaticsZMode.Keep;
                }

                if (copyMap)
                {
                    if (sourceTiles is null || destTiles is null)
                    {
                        throw new InvalidOperationException("Terrain tiles not loaded for copy.");
                    }

                    CopyTerrain(sourceTiles, destTiles, sourceSize.width, effectiveSourceRect, effectiveDestStart, destSize.width,
                        tileReplacements,
                        overwrite: overwriteTerrain, progress: progress, progressStart: 0, progressRange: copyStatics ? 50 : 100);

                    MapMulCodec.WriteLandTiles(destMapPath, destSize.width, destSize.height, destTiles);
                }

                if (copyStatics)
                {
                    EnsureStaticsFiles(destStaIdxPath, destStaticsPath, destSize.width, destSize.height);
                    CopyStatics(effectiveSourceRect, effectiveDestStart, sourceSize.width, sourceHeight, destSize.width, destSize.height,
                        sourceStaIdxPath, sourceStaticsPath, destStaIdxPath, destStaticsPath, overwriteStatics,
                        staticsMode, staticsLayout, zMode, staticsFixedZ, sourceTiles, destTiles, tileReplacements,
                        verdata, applyVerdataStatics, progress,
                        copyMap ? 50 : 0, copyMap ? 50 : 100);
            }
            }, cancelSource.Token);

            StatusText.Text = "Copy completed.";
            AppStatus.SetSuccess(StatusText.Text);
            AppStatus.AppendLog(StatusText.Text, AppStatusSeverity.Success);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            AppStatus.SetError(StatusText.Text);
            AppStatus.AppendLog(StatusText.Text, AppStatusSeverity.Error);
        }
        finally
        {
            AppStatus.SetProgress(0, false);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
        }
    }

    private void CopyTerrain(
        LandTile[] sourceTiles,
        LandTile[] destTiles,
        int sourceWidth,
        RectInt sourceRect,
        (int x, int y) destStart,
        int destWidth,
        TileReplacementMap? replacements,
        bool overwrite,
        IProgress<int> progress,
        int progressStart,
        int progressRange)
    {
        if (!overwrite)
        {
            progress.Report(progressStart + progressRange);
            return;
        }

        var total = sourceRect.Height;
        var lastPercent = -1;
        for (var y = 0; y < sourceRect.Height; y++)
        {
            for (var x = 0; x < sourceRect.Width; x++)
            {
                var sourceIndex = (sourceRect.Y + y) * sourceWidth + (sourceRect.X + x);
                var destIndex = (destStart.y + y) * destWidth + (destStart.x + x);
                var sourceTile = ApplyTerrainReplacement(sourceTiles[sourceIndex], replacements);
                destTiles[destIndex] = sourceTile;
            }

            if (total > 0)
            {
                var percent = progressStart + (int)((y + 1) * progressRange / (double)total);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    progress.Report(percent);
                }
            }
        }
    }

    private void CopyStatics(
        RectInt sourceRect,
        (int x, int y) destStart,
        int sourceWidth,
        int sourceHeight,
        int destWidth,
        int destHeight,
        string sourceStaIdxPath,
        string sourceStaticsPath,
        string destStaIdxPath,
        string destStaticsPath,
        bool overwrite,
        StaticsCopyMode mode,
        StaticsLayout layout,
        StaticsZMode zMode,
        sbyte fixedZ,
        LandTile[]? sourceTiles,
        LandTile[]? destTiles,
        TileReplacementMap? replacements,
        VerdataMul? verdata,
        bool applyVerdataStatics,
        IProgress<int> progress,
        int progressStart,
        int progressRange)
    {
        try
        {
            if (!File.Exists(sourceStaIdxPath) || !File.Exists(sourceStaticsPath))
            {
                AppStatus.AppendLog("Source statics not found. Skipping statics copy.", AppStatusSeverity.Warning);
                progress.Report(progressStart + progressRange);
                return;
            }

            var sourceStaticsBlocks = StaticMulCodec.ReadStatics(sourceStaIdxPath, sourceStaticsPath, sourceWidth, sourceHeight);
            var destStaticsBlocks = StaticMulCodec.ReadStatics(destStaIdxPath, destStaticsPath, destWidth, destHeight);
            var blockWidth = destWidth / MapMul.BlockSize;
            var blockHeight = destHeight / MapMul.BlockSize;
            var sourceBlockWidth = sourceWidth / MapMul.BlockSize;
            var sourceBlockHeight = sourceHeight / MapMul.BlockSize;
            LogStaticsBlockDiagnostics("Source", sourceStaIdxPath, sourceStaticsPath, sourceWidth, sourceHeight, sourceStaticsBlocks);
            LogStaticsBlockDiagnostics("Dest", destStaIdxPath, destStaticsPath, destWidth, destHeight, destStaticsBlocks);
            if (verdata is not null && applyVerdataStatics && verdata.StaticsPatchCount > 0)
            {
                verdata.ApplyToStatics(sourceStaticsBlocks);
                AppStatus.AppendLog($"Verdata statics patches applied: {verdata.StaticsPatchCount:N0}.", AppStatusSeverity.Info);
            }

            if (zMode == StaticsZMode.OffsetByTerrain && (sourceTiles is null || destTiles is null))
            {
                AppStatus.AppendLog("Statics Z offset requires terrain tiles. Using Keep Z.", AppStatusSeverity.Warning);
                zMode = StaticsZMode.Keep;
            }

            var rect = sourceRect;
            var dest = destStart;
            if (mode == StaticsCopyMode.BlockReplaceAligned)
            {
                var snappedRect = SnapRectToBlocks(sourceRect, sourceWidth, sourceHeight);
                var snappedDest = SnapPointToBlock(destStart, destWidth, destHeight);
                if (!snappedRect.Equals(sourceRect) || snappedDest != destStart)
                {
                    AppStatus.AppendLog(
                        $"Statics block snap: {sourceRect} -> {snappedRect}, dest {destStart.x},{destStart.y} -> {snappedDest.x},{snappedDest.y}",
                        AppStatusSeverity.Info);
                }

                rect = snappedRect;
                dest = snappedDest;
            }

            AppStatus.AppendLog($"Statics effective rect: {rect}, dest {dest.x},{dest.y}", AppStatusSeverity.Info);
            var sourceEntriesBefore = CountStaticsEntriesInRect(sourceStaticsBlocks, sourceWidth, rect, layout);
            var destEntriesBefore = CountStaticsEntriesInRect(destStaticsBlocks, destWidth,
                new RectInt(dest.x, dest.y, rect.Width, rect.Height), layout);
            AppStatus.AppendLog($"Statics entries in rect: source {sourceEntriesBefore:N0}, dest {destEntriesBefore:N0}.",
                AppStatusSeverity.Info);

            var copiedEntries = 0;
            var overwrittenEntries = 0;
            var touchedBlocks = new HashSet<int>();
            var samples = new List<string>();
            var sampleChecks = new List<StaticSampleCheck>();
            var total = rect.Height;
            var lastPercent = -1;
            var clearedEntries = 0;
            var skipPerCellRemoval = overwrite && mode != StaticsCopyMode.BlockReplaceAligned;

            if (overwrite && mode != StaticsCopyMode.BlockReplaceAligned)
            {
                clearedEntries = ClearDestStaticsCells(destStaticsBlocks, destWidth, blockHeight, rect, dest, layout);
                if (clearedEntries > 0)
                {
                    AppStatus.AppendLog($"Statics overwrite cleared: {clearedEntries:N0} entries.", AppStatusSeverity.Info);
                }
            }

            if (mode == StaticsCopyMode.BlockReplaceAligned)
            {
                if (!IsBlockAligned(rect, dest))
                {
                    AppStatus.AppendLog("Statics copy error: block replace requires /8 aligned rect and destination.",
                        AppStatusSeverity.Error);
                    progress.Report(progressStart + progressRange);
                    return;
                }

                var srcBlockStartX = rect.X / MapMul.BlockSize;
                var srcBlockEndX = (rect.Right - 1) / MapMul.BlockSize;
                var srcBlockStartY = rect.Y / MapMul.BlockSize;
                var srcBlockEndY = (rect.Bottom - 1) / MapMul.BlockSize;
                var destBlockOffsetX = dest.x / MapMul.BlockSize - srcBlockStartX;
                var destBlockOffsetY = dest.y / MapMul.BlockSize - srcBlockStartY;
                AppStatus.AppendLog(
                    $"Statics block range src [{srcBlockStartX},{srcBlockStartY}] -> [{srcBlockEndX},{srcBlockEndY}], offset {destBlockOffsetX},{destBlockOffsetY}",
                    AppStatusSeverity.Info);

                for (var blockY = srcBlockStartY; blockY <= srcBlockEndY; blockY++)
                {
                    for (var blockX = srcBlockStartX; blockX <= srcBlockEndX; blockX++)
                    {
                        var srcBlockIndex = GetBlockIndex(blockX, blockY, sourceBlockWidth, sourceBlockHeight, layout);
                        var srcList = sourceStaticsBlocks[srcBlockIndex];
                        if (srcList is null || srcList.Count == 0)
                        {
                            if (overwrite)
                            {
                                var emptyDstBlockX = blockX + destBlockOffsetX;
                                var emptyDstBlockY = blockY + destBlockOffsetY;
                                var emptyDstBlockIndex = GetBlockIndex(emptyDstBlockX, emptyDstBlockY, blockWidth, blockHeight, layout);
                                if (emptyDstBlockIndex >= 0 && emptyDstBlockIndex < destStaticsBlocks.Length)
                                {
                                    overwrittenEntries += destStaticsBlocks[emptyDstBlockIndex]?.Count ?? 0;
                                    destStaticsBlocks[emptyDstBlockIndex] = new List<StaticMulEntry>();
                                    touchedBlocks.Add(emptyDstBlockIndex);
                                }
                            }
                            continue;
                        }

                        var dstBlockX = blockX + destBlockOffsetX;
                        var dstBlockY = blockY + destBlockOffsetY;
                        var dstBlockIndex = GetBlockIndex(dstBlockX, dstBlockY, blockWidth, blockHeight, layout);

                        if (dstBlockIndex < 0 || dstBlockIndex >= destStaticsBlocks.Length)
                        {
                            continue;
                        }

                        var adjustedList = new List<StaticMulEntry>(srcList.Count);
                        foreach (var entry in srcList)
                        {
                            var adjustedEntry = ApplyStaticReplacement(entry, replacements);
                            var srcGlobalX = blockX * MapMul.BlockSize + entry.X;
                            var srcGlobalY = blockY * MapMul.BlockSize + entry.Y;
                            var dstGlobalX = dstBlockX * MapMul.BlockSize + entry.X;
                            var dstGlobalY = dstBlockY * MapMul.BlockSize + entry.Y;
                            var adjustedZ = AdjustStaticZ(adjustedEntry, srcGlobalX, srcGlobalY, dstGlobalX, dstGlobalY,
                                zMode, fixedZ, sourceTiles, sourceWidth, destTiles, destWidth);
                            adjustedList.Add(adjustedEntry with { Z = adjustedZ });
                        }

                        if (overwrite)
                        {
                            overwrittenEntries += destStaticsBlocks[dstBlockIndex]?.Count ?? 0;
                            destStaticsBlocks[dstBlockIndex] = adjustedList;
                        }
                        else
                        {
                            var dstList = destStaticsBlocks[dstBlockIndex] ??= new List<StaticMulEntry>();
                            dstList.AddRange(adjustedList);
                        }

                        copiedEntries += srcList.Count;
                        touchedBlocks.Add(dstBlockIndex);
                        if (samples.Count < 10 && srcList.Count > 0)
                        {
                            var sample = ApplyStaticReplacement(srcList[0], replacements);
                            var srcGlobalX = blockX * MapMul.BlockSize + sample.X;
                            var srcGlobalY = blockY * MapMul.BlockSize + sample.Y;
                            var dstGlobalX = dstBlockX * MapMul.BlockSize + sample.X;
                            var dstGlobalY = dstBlockY * MapMul.BlockSize + sample.Y;
                            samples.Add($"src {srcGlobalX},{srcGlobalY} -> dst {dstGlobalX},{dstGlobalY}");
                            sampleChecks.Add(new StaticSampleCheck(
                                sample.TileId,
                                dstBlockIndex,
                                (byte)(dstGlobalX % MapMul.BlockSize),
                                (byte)(dstGlobalY % MapMul.BlockSize),
                                dstGlobalX,
                                dstGlobalY));
                        }
                    }

                    if (total > 0)
                    {
                        var rowsDone = Math.Min(rect.Height, (blockY - srcBlockStartY + 1) * MapMul.BlockSize);
                        var percent = progressStart + (int)(rowsDone * progressRange / (double)total);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress.Report(percent);
                        }
                    }
                }
            }
            else if (mode == StaticsCopyMode.EntryTranslate)
            {
                var srcBlockStartX = rect.X / MapMul.BlockSize;
                var srcBlockEndX = (rect.Right - 1) / MapMul.BlockSize;
                var srcBlockStartY = rect.Y / MapMul.BlockSize;
                var srcBlockEndY = (rect.Bottom - 1) / MapMul.BlockSize;

                for (var blockY = srcBlockStartY; blockY <= srcBlockEndY; blockY++)
                {
                    for (var blockX = srcBlockStartX; blockX <= srcBlockEndX; blockX++)
                    {
                        var srcBlockIndex = GetBlockIndex(blockX, blockY, sourceBlockWidth, sourceBlockHeight, layout);
                        var srcList = sourceStaticsBlocks[srcBlockIndex];
                        if (srcList is null || srcList.Count == 0)
                        {
                            continue;
                        }

                        foreach (var entry in srcList)
                        {
                            var srcGlobalX = blockX * MapMul.BlockSize + entry.X;
                            var srcGlobalY = blockY * MapMul.BlockSize + entry.Y;

                            if (srcGlobalX < rect.X || srcGlobalX >= rect.Right ||
                                srcGlobalY < rect.Y || srcGlobalY >= rect.Bottom)
                            {
                                continue;
                            }

                            var dstGlobalX = dest.x + (srcGlobalX - rect.X);
                            var dstGlobalY = dest.y + (srcGlobalY - rect.Y);

                            var dstBlockIndex = GetBlockIndex(dstGlobalX / MapMul.BlockSize, dstGlobalY / MapMul.BlockSize,
                                blockWidth, blockHeight, layout);
                            var dstList = destStaticsBlocks[dstBlockIndex] ??= new List<StaticMulEntry>();

                            if (overwrite && !skipPerCellRemoval)
                            {
                                var beforeCount = dstList.Count;
                                dstList.RemoveAll(target =>
                                    MatchesCell(target, dstGlobalX % MapMul.BlockSize, dstGlobalY % MapMul.BlockSize));
                                overwrittenEntries += beforeCount - dstList.Count;
                            }

                            var adjustedEntry = ApplyStaticReplacement(entry, replacements);
                            var adjustedZ = AdjustStaticZ(adjustedEntry, srcGlobalX, srcGlobalY, dstGlobalX, dstGlobalY,
                                zMode, fixedZ, sourceTiles, sourceWidth, destTiles, destWidth);
                            dstList.Add(adjustedEntry with
                            {
                                X = (byte)(dstGlobalX % MapMul.BlockSize),
                                Y = (byte)(dstGlobalY % MapMul.BlockSize),
                                Z = adjustedZ
                            });
                            copiedEntries++;
                            touchedBlocks.Add(dstBlockIndex);
                            if (samples.Count < 10)
                            {
                                samples.Add($"src {srcGlobalX},{srcGlobalY} -> dst {dstGlobalX},{dstGlobalY}");
                                sampleChecks.Add(new StaticSampleCheck(
                                    adjustedEntry.TileId,
                                    dstBlockIndex,
                                    (byte)(dstGlobalX % MapMul.BlockSize),
                                    (byte)(dstGlobalY % MapMul.BlockSize),
                                    dstGlobalX,
                                    dstGlobalY));
                            }
                        }
                    }
                    if (total > 0)
                    {
                        var rowsDone = Math.Min(sourceRect.Height, (blockY - srcBlockStartY + 1) * MapMul.BlockSize);
                        var percent = progressStart + (int)(rowsDone * progressRange / (double)total);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress.Report(percent);
                        }
                    }
                }
            }
            else
            {
                for (var y = 0; y < rect.Height; y++)
                {
                    for (var x = 0; x < rect.Width; x++)
                    {
                        var srcX = rect.X + x;
                        var srcY = rect.Y + y;
                        var dstX = dest.x + x;
                        var dstY = dest.y + y;

                        var srcBlockIndex = GetBlockIndex(srcX / MapMul.BlockSize, srcY / MapMul.BlockSize,
                            sourceBlockWidth, sourceBlockHeight, layout);
                        var dstBlockIndex = GetBlockIndex(dstX / MapMul.BlockSize, dstY / MapMul.BlockSize,
                            blockWidth, blockHeight, layout);

                        var srcList = sourceStaticsBlocks[srcBlockIndex];
                        if (srcList is null || srcList.Count == 0)
                        {
                            continue;
                        }

                        var dstList = destStaticsBlocks[dstBlockIndex] ??= new List<StaticMulEntry>();
                        if (overwrite && !skipPerCellRemoval)
                        {
                            var beforeCount = dstList.Count;
                            dstList.RemoveAll(entry =>
                                MatchesCell(entry, dstX % MapMul.BlockSize, dstY % MapMul.BlockSize));
                            overwrittenEntries += beforeCount - dstList.Count;
                        }

                        var localX = srcX % MapMul.BlockSize;
                        var localY = srcY % MapMul.BlockSize;
                        var dstLocalX = dstX % MapMul.BlockSize;
                        var dstLocalY = dstY % MapMul.BlockSize;
                        foreach (var entry in srcList)
                        {
                            if (entry.X != localX || entry.Y != localY)
                            {
                                continue;
                            }

                            var adjustedEntry = ApplyStaticReplacement(entry, replacements);
                            var adjustedZ = AdjustStaticZ(adjustedEntry, srcX, srcY, dstX, dstY,
                                zMode, fixedZ, sourceTiles, sourceWidth, destTiles, destWidth);
                            dstList.Add(adjustedEntry with { X = (byte)dstLocalX, Y = (byte)dstLocalY, Z = adjustedZ });
                            copiedEntries++;
                            if (samples.Count < 10)
                            {
                                samples.Add($"src {srcX},{srcY} -> dst {dstX},{dstY}");
                                sampleChecks.Add(new StaticSampleCheck(
                                    adjustedEntry.TileId,
                                    dstBlockIndex,
                                    (byte)dstLocalX,
                                    (byte)dstLocalY,
                                    dstX,
                                    dstY));
                            }
                        }

                        touchedBlocks.Add(dstBlockIndex);
                    }

                    if (total > 0)
                    {
                        var percent = progressStart + (int)((y + 1) * progressRange / (double)total);
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progress.Report(percent);
                        }
                    }
                }
            }

            if (destStaticsBlocks.Length != blockWidth * blockHeight)
            {
                Array.Resize(ref destStaticsBlocks, blockWidth * blockHeight);
            }

            StaticMulCodec.WriteStatics(destStaIdxPath, destStaticsPath, destWidth, destHeight, destStaticsBlocks);

            AppStatus.AppendLog(
                $"Statics copy done. Entries copied: {copiedEntries:N0}, removed: {overwrittenEntries:N0}, blocks touched: {touchedBlocks.Count:N0}.",
                AppStatusSeverity.Info);
            if (samples.Count > 0)
            {
                AppStatus.AppendLog($"Statics sample: {string.Join(" | ", samples)}", AppStatusSeverity.Info);
            }
            if (sampleChecks.Count > 0)
            {
                var checks = sampleChecks.Select(sample =>
                {
                    var list = sample.BlockIndex >= 0 && sample.BlockIndex < destStaticsBlocks.Length
                        ? destStaticsBlocks[sample.BlockIndex]
                        : null;
                    var match = list?.Any(entry => entry.TileId == sample.TileId && entry.X == sample.CellX && entry.Y == sample.CellY) == true;
                    return $"tile 0x{sample.TileId:X4} @ {sample.DestX},{sample.DestY} -> {(match ? "ok" : "missing")}";
                });
                AppStatus.AppendLog($"Statics verify: {string.Join(" | ", checks)}", AppStatusSeverity.Info);
            }

            var reloadedBlocks = StaticMulCodec.ReadStatics(destStaIdxPath, destStaticsPath, destWidth, destHeight);
            var recheck = sampleChecks.Select(sample =>
            {
                var list = sample.BlockIndex >= 0 && sample.BlockIndex < reloadedBlocks.Length
                    ? reloadedBlocks[sample.BlockIndex]
                    : null;
                var match = list?.Any(entry => entry.TileId == sample.TileId && entry.X == sample.CellX && entry.Y == sample.CellY) == true;
                return $"tile 0x{sample.TileId:X4} @ {sample.DestX},{sample.DestY} -> {(match ? "ok" : "missing")}";
            });
            AppStatus.AppendLog($"Statics reread verify: {string.Join(" | ", recheck)}", AppStatusSeverity.Info);

            var destRect = new RectInt(dest.x, dest.y, rect.Width, rect.Height);
            var deltaX = destRect.X - rect.X;
            var deltaY = destRect.Y - rect.Y;
            AppStatus.AppendLog($"Statics delta: dx={deltaX}, dy={deltaY}", AppStatusSeverity.Info);
            CompareStaticsRegionTranslated(sourceStaticsBlocks, sourceWidth, rect, deltaX, deltaY, reloadedBlocks, destWidth,
                destRect, layout);
        }
        catch (Exception ex)
        {
            AppStatus.AppendLog($"Statics copy error: {ex.Message}", AppStatusSeverity.Error);
            throw;
        }
    }

    private static bool MatchesCell(StaticMulEntry entry, int x, int y)
        => entry.X == x && entry.Y == y;

    private void EnsureStaticsFiles(string destStaIdxPath, string destStaticsPath, int width, int height)
    {
        var destStaIdx = destStaIdxPath;
        var destStatics = destStaticsPath;

        if (string.IsNullOrWhiteSpace(destStaIdx) || string.IsNullOrWhiteSpace(destStatics))
        {
            return;
        }

        if (!File.Exists(destStaIdx) || !File.Exists(destStatics))
        {
            StaticMulCodec.WriteEmptyStatics(destStaIdx, destStatics, width, height);
            AppStatus.AppendLog("Destination statics were missing. Created empty statics.", AppStatusSeverity.Warning);
        }
    }

    private bool TryResolveSizes(out (int width, int height) sourceSize, out (int width, int height) destSize)
    {
        sourceSize = default;
        destSize = default;

        _sourceSize = null;
        _destSize = null;

        if (!string.IsNullOrWhiteSpace(SourceMapPathBox.Text) &&
            MapConversion.TryResolveMapSizeFromFile(SourceMapPathBox.Text, out var sw, out var sh))
        {
            sourceSize = (sw, sh);
            _sourceSize = sourceSize;
        }

        if (!string.IsNullOrWhiteSpace(DestMapPathBox.Text) &&
            MapConversion.TryResolveMapSizeFromFile(DestMapPathBox.Text, out var dw, out var dh))
        {
            destSize = (dw, dh);
            _destSize = destSize;
        }

        return _sourceSize.HasValue && _destSize.HasValue;
    }

    private bool TryGetRegion(out RectInt sourceRect, out (int x, int y) destStart)
    {
        sourceRect = default;
        destStart = default;

        if (!TryParseInt(SourceFromXBox.Text, out var fromX) ||
            !TryParseInt(SourceFromYBox.Text, out var fromY) ||
            !TryParseInt(SourceToXBox.Text, out var toX) ||
            !TryParseInt(SourceToYBox.Text, out var toY) ||
            !TryParseInt(DestStartXBox.Text, out var destX) ||
            !TryParseInt(DestStartYBox.Text, out var destY))
        {
            return false;
        }

        var minX = Math.Min(fromX, toX);
        var minY = Math.Min(fromY, toY);
        var maxX = Math.Max(fromX, toX);
        var maxY = Math.Max(fromY, toY);

        sourceRect = new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
        destStart = (destX, destY);
        return true;
    }

    private static bool ValidateBounds(
        RectInt sourceRect,
        int sourceWidth,
        int sourceHeight,
        (int x, int y) destStart,
        int destWidth,
        int destHeight,
        out string error)
    {
        error = string.Empty;

        if (sourceRect.X < 0 || sourceRect.Y < 0 ||
            sourceRect.Right > sourceWidth || sourceRect.Bottom > sourceHeight)
        {
            error = "Source area outside source map.";
            return false;
        }

        if (destStart.x < 0 || destStart.y < 0 ||
            destStart.x + sourceRect.Width > destWidth ||
            destStart.y + sourceRect.Height > destHeight)
        {
            error = "Destination area outside destination map.";
            return false;
        }

        return true;
    }

    private void UpdateStatus()
    {
        _sourceSize = null;
        _destSize = null;

        var sourceMapPath = SourceMapPathBox.Text ?? string.Empty;
        var destMapPath = DestMapPathBox.Text ?? string.Empty;
        var useVerdata = UseVerdataCheckBox.IsChecked == true;
        var verdataPath = VerdataPathBox.Text ?? string.Empty;

        var sourceExists = !string.IsNullOrWhiteSpace(sourceMapPath) && File.Exists(sourceMapPath);
        var destExists = !string.IsNullOrWhiteSpace(destMapPath) && File.Exists(destMapPath);

        SetFieldState(SourceMapPathBox, sourceExists ? FieldState.Valid : FieldState.Error);
        SetFieldState(DestMapPathBox, destExists ? FieldState.Valid : FieldState.Error);

        if (sourceExists && MapConversion.TryResolveMapSizeFromFile(sourceMapPath, out var sourceWidth, out var sourceHeight))
        {
            _sourceSize = (sourceWidth, sourceHeight);
            SourceMapSizeText.Text = $"Size: {sourceWidth}x{sourceHeight} | Blocks: {sourceWidth / MapMul.BlockSize}x{sourceHeight / MapMul.BlockSize} | Max: {sourceWidth - 1},{sourceHeight - 1}";
        }
        else
        {
            SourceMapSizeText.Text = string.Empty;
        }

        if (destExists && MapConversion.TryResolveMapSizeFromFile(destMapPath, out var destWidth, out var destHeight))
        {
            _destSize = (destWidth, destHeight);
            DestMapSizeText.Text = $"Size: {destWidth}x{destHeight} | Blocks: {destWidth / MapMul.BlockSize}x{destHeight / MapMul.BlockSize} | Max: {destWidth - 1},{destHeight - 1}";
        }
        else
        {
            DestMapSizeText.Text = string.Empty;
        }

        var copyStatics = CopyStaticsCheckBox.IsChecked == true;
        var copyMap = CopyMapCheckBox.IsChecked == true;
        var sourceStaIdxOk = !string.IsNullOrWhiteSpace(SourceStaIdxPathBox.Text) && File.Exists(SourceStaIdxPathBox.Text);
        var sourceStaticsOk = !string.IsNullOrWhiteSpace(SourceStaticsPathBox.Text) && File.Exists(SourceStaticsPathBox.Text);
        var destStaIdxOk = !string.IsNullOrWhiteSpace(DestStaIdxPathBox.Text) && File.Exists(DestStaIdxPathBox.Text);
        var destStaticsOk = !string.IsNullOrWhiteSpace(DestStaticsPathBox.Text) && File.Exists(DestStaticsPathBox.Text);
        var saveAsNew = SaveAsNewMapCheckBox.IsChecked == true;
        var outputOk = !string.IsNullOrWhiteSpace(OutputFolderBox.Text) && Directory.Exists(OutputFolderBox.Text);

        SetFieldState(SourceStaIdxPathBox, sourceStaIdxOk ? FieldState.Valid : FieldState.Error, isOptional: !copyStatics);
        SetFieldState(SourceStaticsPathBox, sourceStaticsOk ? FieldState.Valid : FieldState.Error, isOptional: !copyStatics);
        SetFieldState(DestStaIdxPathBox, destStaIdxOk ? FieldState.Valid : FieldState.Error, isOptional: !copyStatics);
        SetFieldState(DestStaticsPathBox, destStaticsOk ? FieldState.Valid : FieldState.Error, isOptional: !copyStatics);
        SetFieldState(OutputFolderBox, outputOk ? FieldState.Valid : FieldState.Error, isOptional: !saveAsNew);

        var verdataOk = !string.IsNullOrWhiteSpace(verdataPath) && File.Exists(verdataPath);
        SetFieldState(VerdataPathBox, verdataOk ? FieldState.Valid : FieldState.Error, isOptional: !useVerdata);
        VerdataSizeText.Text = verdataOk ? FormatFileSize(verdataPath) : string.Empty;
        if (useVerdata)
        {
            SetFieldState(VerdataMapFileIdBox, TryParseOptionalInt(VerdataMapFileIdBox.Text) ? FieldState.Valid : FieldState.Error);
            SetFieldState(VerdataStaticsFileIdBox, TryParseOptionalInt(VerdataStaticsFileIdBox.Text) ? FieldState.Valid : FieldState.Error);
        }
        else
        {
            SetFieldState(VerdataMapFileIdBox, FieldState.Neutral, isOptional: true);
            SetFieldState(VerdataStaticsFileIdBox, FieldState.Neutral, isOptional: true);
        }

        var sourceFromText = SourceFromXBox.Text;
        var sourceFromYText = SourceFromYBox.Text;
        var sourceToText = SourceToXBox.Text;
        var sourceToYText = SourceToYBox.Text;
        var destStartXText = DestStartXBox.Text;
        var destStartYText = DestStartYBox.Text;

        var sourceCoordsEmpty = string.IsNullOrWhiteSpace(sourceFromText) &&
                                string.IsNullOrWhiteSpace(sourceFromYText) &&
                                string.IsNullOrWhiteSpace(sourceToText) &&
                                string.IsNullOrWhiteSpace(sourceToYText);
        if (sourceCoordsEmpty)
        {
            SetFieldState(SourceFromXBox, FieldState.Neutral, isOptional: true);
            SetFieldState(SourceFromYBox, FieldState.Neutral, isOptional: true);
            SetFieldState(SourceToXBox, FieldState.Neutral, isOptional: true);
            SetFieldState(SourceToYBox, FieldState.Neutral, isOptional: true);
        }
        else
        {
            var sourceFromOk = TryParseInt(sourceFromText, out _);
            var sourceToOk = TryParseInt(sourceToText, out _);
            var sourceYFromOk = TryParseInt(sourceFromYText, out _);
            var sourceYToOk = TryParseInt(sourceToYText, out _);
            SetFieldState(SourceFromXBox, sourceFromOk ? FieldState.Valid : FieldState.Error);
            SetFieldState(SourceFromYBox, sourceYFromOk ? FieldState.Valid : FieldState.Error);
            SetFieldState(SourceToXBox, sourceToOk ? FieldState.Valid : FieldState.Error);
            SetFieldState(SourceToYBox, sourceYToOk ? FieldState.Valid : FieldState.Error);
        }

        var destCoordsEmpty = string.IsNullOrWhiteSpace(destStartXText) &&
                              string.IsNullOrWhiteSpace(destStartYText);
        if (destCoordsEmpty)
        {
            SetFieldState(DestStartXBox, FieldState.Neutral, isOptional: true);
            SetFieldState(DestStartYBox, FieldState.Neutral, isOptional: true);
        }
        else
        {
            var destStartOk = TryParseInt(destStartXText, out _) && TryParseInt(destStartYText, out _);
            SetFieldState(DestStartXBox, destStartOk ? FieldState.Valid : FieldState.Error);
            SetFieldState(DestStartYBox, destStartOk ? FieldState.Valid : FieldState.Error);
        }

        if (copyStatics && GetStaticsZMode() == StaticsZMode.Fixed)
        {
            var fixedZOk = TryParseInt(StaticsFixedZBox.Text, out var fixedZ) &&
                           fixedZ >= sbyte.MinValue && fixedZ <= sbyte.MaxValue;
            SetFieldState(StaticsFixedZBox, fixedZOk ? FieldState.Valid : FieldState.Error);
        }
        else
        {
            SetFieldState(StaticsFixedZBox, FieldState.Neutral, isOptional: true);
        }

        var encoding = GetPreviewEncoding();
        if (encoding == PreviewEncoding.MapTrans)
        {
            SetFieldState(PreviewMapTransComboBox, GetSelectedPreviewMapTransPath() is not null ? FieldState.Valid : FieldState.Warning);
            SetFieldState(PreviewTileJsonPathBox, FieldState.Neutral, isOptional: true);
        }
        else if (encoding == PreviewEncoding.TileJson)
        {
            var tileJsonOk = !string.IsNullOrWhiteSpace(PreviewTileJsonPathBox.Text) && File.Exists(PreviewTileJsonPathBox.Text);
            SetFieldState(PreviewTileJsonPathBox, tileJsonOk ? FieldState.Valid : FieldState.Error);
            SetFieldState(PreviewMapTransComboBox, FieldState.Neutral);
        }
        else
        {
            SetFieldState(PreviewMapTransComboBox, FieldState.Neutral);
            SetFieldState(PreviewTileJsonPathBox, FieldState.Neutral, isOptional: true);
        }

        var applyReplace = ApplyTileReplaceCheckBox.IsChecked == true;
        var replacePath = TileReplacePathBox.Text ?? string.Empty;
        var replaceOk = !string.IsNullOrWhiteSpace(replacePath) && File.Exists(replacePath);
        SetFieldState(TileReplacePathBox, replaceOk ? FieldState.Valid : FieldState.Warning, isOptional: !applyReplace);

        CopyButton.IsEnabled = copyMap || copyStatics;
        UpdateAlignmentHint(copyStatics);
        UpdatePreviewLabels();
        UpdatePreviewActionVisibility();
    }

    private void EnsureTileReplacePath()
    {
        if (string.IsNullOrWhiteSpace(TileReplacePathBox.Text))
        {
            TileReplacePathBox.Text = Path.Combine(UOMapWeaverDataPaths.TileReplaceRoot, "UOMapWeaver_TileReplace.json");
        }
    }

    private void UpdateClientInfo()
    {
        UpdateClientInfo(SourceClientFolderBox, SourceClientInfoText);
        UpdateClientInfo(DestClientFolderBox, DestClientInfoText);
    }

    private void UpdateClientInfo(TextBox box, TextBlock infoText)
    {
        var folder = box.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(folder))
        {
            infoText.Text = string.Empty;
            SetFieldState(box, FieldState.Neutral, isOptional: true);
            return;
        }

        if (!Directory.Exists(folder))
        {
            infoText.Text = "Folder not found.";
            SetFieldState(box, FieldState.Error);
            return;
        }

        var artIdx = Path.Combine(folder, "artidx.mul");
        var artMul = Path.Combine(folder, "art.mul");
        var tiledata = Path.Combine(folder, "tiledata.mul");

        var artIdxOk = File.Exists(artIdx);
        var artOk = File.Exists(artMul);
        var tiledataOk = File.Exists(tiledata);

        infoText.Text = $"artidx.mul: {(artIdxOk ? "ok" : "missing")} | art.mul: {(artOk ? "ok" : "missing")} | tiledata.mul: {(tiledataOk ? "ok" : "missing")}";

        if (artIdxOk && artOk)
        {
            SetFieldState(box, FieldState.Valid);
        }
        else
        {
            SetFieldState(box, FieldState.Warning);
        }
    }

    private void SetBusy(bool isBusy)
    {
        if (CopyButton != null)
        {
            CopyButton.IsEnabled = !isBusy;
        }
    }

    private void ApplyCopyOptionState()
    {
        var copyMap = CopyMapCheckBox.IsChecked == true;
        var copyStatics = CopyStaticsCheckBox.IsChecked == true;
        var staticsMode = GetStaticsCopyMode();
        var useVerdata = UseVerdataCheckBox.IsChecked == true;

        if (!copyMap)
        {
            OverwriteTerrainCheckBox.IsChecked = false;
        }

        OverwriteTerrainCheckBox.IsEnabled = copyMap;

        if (!copyStatics)
        {
            OverwriteStaticsCheckBox.IsChecked = false;
        }

        OverwriteStaticsCheckBox.IsEnabled = copyStatics;

        if (CopyButton != null)
        {
            CopyButton.IsEnabled = copyMap || copyStatics;
        }

        StaticsCopyModeComboBox.IsEnabled = copyStatics;
        StaticsLayoutComboBox.IsEnabled = copyStatics;
        StaticsZModeComboBox.IsEnabled = copyStatics;
        StaticsFixedZBox.IsEnabled = copyStatics && GetStaticsZMode() == StaticsZMode.Fixed;
        VerdataPathBox.IsEnabled = useVerdata;
        VerdataTerrainCheckBox.IsEnabled = useVerdata;
        VerdataStaticsCheckBox.IsEnabled = useVerdata;
        VerdataMapFileIdBox.IsEnabled = useVerdata;
        VerdataStaticsFileIdBox.IsEnabled = useVerdata;

        if (copyStatics && staticsMode == StaticsCopyMode.BlockReplaceAligned)
        {
            SnapSelectionToBlocks();
        }
    }

    private void LoadStaticsCopyModes()
    {
        _staticsCopyModes.Clear();
        _staticsCopyModes.Add(new StaticsCopyModeOption(StaticsCopyMode.CellMatch, "Cell match (default)"));
        _staticsCopyModes.Add(new StaticsCopyModeOption(StaticsCopyMode.EntryTranslate, "Entry translate (alt)"));
        _staticsCopyModes.Add(new StaticsCopyModeOption(StaticsCopyMode.BlockReplaceAligned, "Block replace (aligned)"));

        StaticsCopyModeComboBox.ItemsSource = _staticsCopyModes;
        StaticsCopyModeComboBox.SelectedIndex = 0;
    }

    private void LoadStaticsLayouts()
    {
        _staticsLayoutOptions.Clear();
        _staticsLayoutOptions.Add(new StaticsLayoutOption(StaticsLayout.RowMajor, "Row-major blocks (default)"));
        _staticsLayoutOptions.Add(new StaticsLayoutOption(StaticsLayout.ColumnMajor, "Column-major blocks (alt)"));

        StaticsLayoutComboBox.ItemsSource = _staticsLayoutOptions;
        StaticsLayoutComboBox.SelectedIndex = 0;
    }

    private void LoadStaticsZOptions()
    {
        _staticsZOptions.Clear();
        _staticsZOptions.Add(new StaticsZOption(StaticsZMode.Keep, "Keep Z"));
        _staticsZOptions.Add(new StaticsZOption(StaticsZMode.OffsetByTerrain, "Offset by terrain"));
        _staticsZOptions.Add(new StaticsZOption(StaticsZMode.Fixed, "Set fixed Z"));

        StaticsZModeComboBox.ItemsSource = _staticsZOptions;
        StaticsZModeComboBox.SelectedIndex = 0;
    }

    private StaticsCopyMode GetStaticsCopyMode()
    {
        return StaticsCopyModeComboBox.SelectedItem is StaticsCopyModeOption option
            ? option.Mode
            : StaticsCopyMode.CellMatch;
    }

    private StaticsLayout GetStaticsLayout()
    {
        return StaticsLayoutComboBox.SelectedItem is StaticsLayoutOption option
            ? option.Layout
            : StaticsLayout.RowMajor;
    }

    private StaticsZMode GetStaticsZMode()
    {
        return StaticsZModeComboBox.SelectedItem is StaticsZOption option
            ? option.Mode
            : StaticsZMode.Keep;
    }

    private void LogFileInfo(string label, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            AppStatus.AppendLog($"{label}: <empty>", AppStatusSeverity.Warning);
            return;
        }

        try
        {
            if (!File.Exists(path))
            {
                AppStatus.AppendLog($"{label}: missing ({path})", AppStatusSeverity.Warning);
                return;
            }

            var info = new FileInfo(path);
            AppStatus.AppendLog($"{label}: {path} ({info.Length:N0} bytes, {info.LastWriteTime:yyyy-MM-dd HH:mm:ss})",
                AppStatusSeverity.Info);
        }
        catch (Exception ex)
        {
            AppStatus.AppendLog($"{label}: {path} ({ex.Message})", AppStatusSeverity.Warning);
        }
    }

    private static bool TryParseInt(string? text, out int value)
        => int.TryParse(text, out value);

    private static Bmp24Image BuildTileIndexPreview(LandTile[] tiles, int width, int height)
    {
        var pixels = new byte[width * height * 3];
        for (var i = 0; i < tiles.Length; i++)
        {
            var tileId = tiles[i].TileId;
            var dest = i * 3;
            pixels[dest] = (byte)(tileId >> 8);
            pixels[dest + 1] = (byte)(tileId & 0xFF);
            pixels[dest + 2] = 0;
        }

        return new Bmp24Image(width, height, pixels);
    }

    private void OnSourceBmpPathChanged(object? sender, TextChangedEventArgs e)
    {
        LoadPreviewBitmap(SourceBmpPathBox.Text, isSource: true);
        SaveState();
    }

    private void OnDestBmpPathChanged(object? sender, TextChangedEventArgs e)
    {
        LoadPreviewBitmap(DestBmpPathBox.Text, isSource: false);
        SaveState();
    }

    private void LoadPreviewBitmap(string? path, bool isSource)
    {
        if (isSource)
        {
            _sourcePreviewBitmap?.Dispose();
            _sourcePreviewBitmap = null;
            SourcePreviewImage.Source = null;
            SourceBmpInfoText.Text = string.Empty;
            _sourcePreviewOriginX = 0;
            _sourcePreviewOriginY = 0;
            UpdateZoomState(_sourceZoom, null);
        }
        else
        {
            _destPreviewBitmap?.Dispose();
            _destPreviewBitmap = null;
            DestPreviewImage.Source = null;
            DestBmpInfoText.Text = string.Empty;
            OverlayBorder.IsVisible = false;
            OverlayImage.Source = null;
            CombinedPreviewImage.Source = null;
            CombinedOverlayBorder.IsVisible = false;
            CombinedOverlayImage.Source = null;
            UpdateZoomState(_destZoom, null);
            UpdateZoomState(_combinedZoom, null);
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            if (isSource)
            {
                SourceSelectionRect.IsVisible = false;
                _sourceSelectionPixels = null;
            }

            UpdatePreviewLabels();
            UpdatePreviewActionVisibility();
            return;
        }

        try
        {
            var bitmap = new Bitmap(path);
            if (isSource)
            {
                _sourcePreviewBitmap = bitmap;
                SourcePreviewImage.Source = bitmap;
                SourceBmpInfoText.Text = $"Size: {bitmap.PixelSize.Width}x{bitmap.PixelSize.Height}";
                if (TryParsePreviewCrop(path, out var cropX, out var cropY, out _, out _))
                {
                    _sourcePreviewOriginX = cropX;
                    _sourcePreviewOriginY = cropY;
                }
                else
                {
                    _sourcePreviewOriginX = 0;
                    _sourcePreviewOriginY = 0;
                }
                UpdateZoomState(_sourceZoom, bitmap);
                if (!TryGetSourceRectFromFields(out var selection))
                {
                    selection = _sourceSelectionPixels ?? default;
                }

                _sourceSelectionPixels = selection.Width > 0 && selection.Height > 0
                    ? selection
                    : _sourceSelectionPixels;
            }
            else
            {
                _destPreviewBitmap = bitmap;
                DestPreviewImage.Source = bitmap;
                CombinedPreviewImage.Source = bitmap;
                DestBmpInfoText.Text = $"Size: {bitmap.PixelSize.Width}x{bitmap.PixelSize.Height}";
                UpdateZoomState(_destZoom, bitmap);
                UpdateZoomState(_combinedZoom, bitmap);
            }
        }
        catch (Exception ex)
        {
            if (isSource)
            {
                SourceBmpInfoText.Text = $"Error: {ex.Message}";
            }
            else
            {
                DestBmpInfoText.Text = $"Error: {ex.Message}";
            }
        }

        UpdateSourceSelectionVisual();
        UpdateOverlayFromSelection();
        UpdateCombinedOverlayFromSelection();
        UpdatePreviewLabels();
        UpdatePreviewActionVisibility();
    }

    private void OnSourcePreviewWheel(object? sender, PointerWheelEventArgs e)
        => HandleZoomWheel(_sourceZoom, e);

    private void OnDestPreviewWheel(object? sender, PointerWheelEventArgs e)
        => HandleZoomWheel(_destZoom, e);

    private void OnCombinedPreviewWheel(object? sender, PointerWheelEventArgs e)
        => HandleZoomWheel(_combinedZoom, e);

    private void OnSourcePreviewPanPressed(object? sender, PointerPressedEventArgs e)
        => HandlePanPressed(_sourceZoom, e);

    private void OnSourcePreviewPanMoved(object? sender, PointerEventArgs e)
        => HandlePanMoved(_sourceZoom, e);

    private void OnSourcePreviewPanReleased(object? sender, PointerReleasedEventArgs e)
        => HandlePanReleased(_sourceZoom, e);

    private void OnDestPreviewPanPressed(object? sender, PointerPressedEventArgs e)
        => HandlePanPressed(_destZoom, e);

    private void OnDestPreviewPanMoved(object? sender, PointerEventArgs e)
        => HandlePanMoved(_destZoom, e);

    private void OnDestPreviewPanReleased(object? sender, PointerReleasedEventArgs e)
        => HandlePanReleased(_destZoom, e);

    private void OnCombinedPreviewPanPressed(object? sender, PointerPressedEventArgs e)
        => HandlePanPressed(_combinedZoom, e);

    private void OnCombinedPreviewPanMoved(object? sender, PointerEventArgs e)
        => HandlePanMoved(_combinedZoom, e);

    private void OnCombinedPreviewPanReleased(object? sender, PointerReleasedEventArgs e)
        => HandlePanReleased(_combinedZoom, e);

    private void OnSourcePreviewPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(SourcePreviewCanvas).Properties.IsRightButtonPressed)
        {
            return;
        }

        if (_sourcePreviewBitmap is null)
        {
            return;
        }

        var point = e.GetPosition(SourcePreviewImage);
        if (!TryGetPixelFromPosition(SourcePreviewImage, _sourcePreviewBitmap, point, out var pixel))
        {
            return;
        }

        var mapPoint = new Point(pixel.X + _sourcePreviewOriginX, pixel.Y + _sourcePreviewOriginY);
        _isSelectingSource = true;
        _sourceDragStart = mapPoint;
        e.Pointer.Capture(SourcePreviewCanvas);
        SetSourceSelectionFromPoints(_sourceDragStart, _sourceDragStart);
    }

    private void OnSourcePreviewMoved(object? sender, PointerEventArgs e)
    {
        if (!_isSelectingSource || _sourcePreviewBitmap is null)
        {
            return;
        }

        var point = e.GetPosition(SourcePreviewImage);
        if (!TryGetPixelFromPosition(SourcePreviewImage, _sourcePreviewBitmap, point, out var pixel))
        {
            return;
        }

        var mapPoint = new Point(pixel.X + _sourcePreviewOriginX, pixel.Y + _sourcePreviewOriginY);
        SetSourceSelectionFromPoints(_sourceDragStart, mapPoint);
    }

    private void OnSourcePreviewReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isSelectingSource)
        {
            return;
        }

        _isSelectingSource = false;
        if (e.Pointer.Captured == SourcePreviewCanvas)
        {
            e.Pointer.Capture(null);
        }
    }

    private void OnOverlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(DestPreviewCanvas).Properties.IsRightButtonPressed)
        {
            return;
        }

        if (_destPreviewBitmap is null || _sourceSelectionPixels is null || !OverlayBorder.IsVisible)
        {
            return;
        }

        _isDraggingOverlay = true;
        _overlayDragStart = e.GetPosition(DestPreviewCanvas);
        _overlayStartOffset = _overlayDisplayOffset;
        e.Pointer.Capture(OverlayBorder);
    }

    private void OnOverlayMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDraggingOverlay || _destPreviewBitmap is null || _sourceSelectionPixels is null)
        {
            return;
        }

        if (!TryGetDisplayRect(DestPreviewImage, _destPreviewBitmap, out var displayRect))
        {
            return;
        }

        var current = e.GetPosition(DestPreviewCanvas);
        var delta = current - _overlayDragStart;
        var next = new Point(_overlayStartOffset.X + delta.X, _overlayStartOffset.Y + delta.Y);

        _overlayDisplayOffset = ClampOverlayToDisplayRect(next, displayRect);
        UpdateOverlayVisual();
        UpdateDestStartFromOverlay(displayRect);
    }

    private void OnOverlayReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isDraggingOverlay)
        {
            return;
        }

        _isDraggingOverlay = false;
        if (e.Pointer.Captured == OverlayBorder)
        {
            e.Pointer.Capture(null);
        }
    }

    private void SetSourceSelectionFromPoints(Point start, Point end)
    {
        var startX = (int)Math.Round(start.X);
        var startY = (int)Math.Round(start.Y);
        var endX = (int)Math.Round(end.X);
        var endY = (int)Math.Round(end.Y);

        var minX = Math.Min(startX, endX);
        var minY = Math.Min(startY, endY);
        var maxX = Math.Max(startX, endX);
        var maxY = Math.Max(startY, endY);

        var rect = new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
        _sourceSelectionPixels = rect;
        UpdateSourceSelectionVisual();
        UpdateRegionFieldsFromSelection(rect);
        UpdateOverlayFromSelection();
    }

    private void UpdateRegionFieldsFromSelection(RectInt rect)
    {
        _syncingRegionFields = true;
        SourceFromXBox.Text = rect.X.ToString();
        SourceFromYBox.Text = rect.Y.ToString();
        SourceToXBox.Text = (rect.Right - 1).ToString();
        SourceToYBox.Text = (rect.Bottom - 1).ToString();
        _syncingRegionFields = false;
        UpdatePreviewLabels();
    }

    private void UpdateDestStartFields(int x, int y)
    {
        _syncingRegionFields = true;
        DestStartXBox.Text = x.ToString();
        DestStartYBox.Text = y.ToString();
        _syncingRegionFields = false;
        UpdatePreviewLabels();
    }

    private void UpdateSourceSelectionVisual()
    {
        if (_sourcePreviewBitmap is null || _sourceSelectionPixels is null)
        {
            SourceSelectionRect.IsVisible = false;
            return;
        }

        if (!TryGetDisplayRect(SourcePreviewImage, _sourcePreviewBitmap, out var displayRect))
        {
            SourceSelectionRect.IsVisible = false;
            return;
        }

        if (!TryGetSelectionRectForSourcePreview(out var rect))
        {
            SourceSelectionRect.IsVisible = false;
            return;
        }
        var bmpWidth = _sourcePreviewBitmap.PixelSize.Width;
        var bmpHeight = _sourcePreviewBitmap.PixelSize.Height;
        if (bmpWidth == 0 || bmpHeight == 0)
        {
            SourceSelectionRect.IsVisible = false;
            return;
        }

        var left = displayRect.X + rect.X / (double)bmpWidth * displayRect.Width;
        var top = displayRect.Y + rect.Y / (double)bmpHeight * displayRect.Height;
        var width = rect.Width / (double)bmpWidth * displayRect.Width;
        var height = rect.Height / (double)bmpHeight * displayRect.Height;

        if (width < 1 || height < 1)
        {
            SourceSelectionRect.IsVisible = false;
            return;
        }

        SourceSelectionRect.IsVisible = true;
        SourceSelectionRect.Width = width;
        SourceSelectionRect.Height = height;
        Canvas.SetLeft(SourceSelectionRect, left);
        Canvas.SetTop(SourceSelectionRect, top);
    }

    private void UpdateOverlayFromSelection()
    {
        if (_destPreviewBitmap is null || _sourcePreviewBitmap is null || _sourceSelectionPixels is null)
        {
            OverlayBorder.IsVisible = false;
            OverlayImage.Source = null;
            return;
        }

        if (!TryGetDisplayRect(DestPreviewImage, _destPreviewBitmap, out var displayRect))
        {
            OverlayBorder.IsVisible = false;
            return;
        }

        if (!TryGetSelectionRectForSourcePreview(out var rect))
        {
            OverlayBorder.IsVisible = false;
            OverlayImage.Source = null;
            return;
        }
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            OverlayBorder.IsVisible = false;
            return;
        }

        OverlayImage.Source = new CroppedBitmap(_sourcePreviewBitmap, new PixelRect(rect.X, rect.Y, rect.Width, rect.Height));

        _overlayDisplaySize = new Size(
            rect.Width / (double)_destPreviewBitmap.PixelSize.Width * displayRect.Width,
            rect.Height / (double)_destPreviewBitmap.PixelSize.Height * displayRect.Height);

        if (_overlayDisplaySize.Width <= 0 || _overlayDisplaySize.Height <= 0)
        {
            OverlayBorder.IsVisible = false;
            return;
        }
        _overlayDisplaySize = new Size(
            Math.Max(1, _overlayDisplaySize.Width),
            Math.Max(1, _overlayDisplaySize.Height));

        if (TryParseInt(DestStartXBox.Text, out var destX) && TryParseInt(DestStartYBox.Text, out var destY))
        {
            UpdateOverlayPositionFromDestStart(destX, destY);
        }
        else
        {
            _overlayDisplayOffset = new Point(displayRect.X, displayRect.Y);
            UpdateOverlayVisual();
        }

        OverlayBorder.IsVisible = true;
        UpdateCombinedOverlayFromSelection();
    }

    private void UpdateCombinedOverlayFromSelection()
    {
        if (_destPreviewBitmap is null || _sourcePreviewBitmap is null || _sourceSelectionPixels is null)
        {
            CombinedOverlayBorder.IsVisible = false;
            CombinedOverlayImage.Source = null;
            CombinedAnchorMarker.IsVisible = false;
            return;
        }

        if (!TryGetDisplayRect(CombinedPreviewImage, _destPreviewBitmap, out var displayRect))
        {
            CombinedOverlayBorder.IsVisible = false;
            CombinedAnchorMarker.IsVisible = false;
            return;
        }

        if (!TryGetSelectionRectForSourcePreview(out var rect))
        {
            CombinedOverlayBorder.IsVisible = false;
            CombinedOverlayImage.Source = null;
            CombinedAnchorMarker.IsVisible = false;
            return;
        }
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            CombinedOverlayBorder.IsVisible = false;
            CombinedAnchorMarker.IsVisible = false;
            return;
        }

        CombinedOverlayImage.Source = new CroppedBitmap(_sourcePreviewBitmap, new PixelRect(rect.X, rect.Y, rect.Width, rect.Height));

        var overlaySize = new Size(
            rect.Width / (double)_destPreviewBitmap.PixelSize.Width * displayRect.Width,
            rect.Height / (double)_destPreviewBitmap.PixelSize.Height * displayRect.Height);

        if (overlaySize.Width <= 0 || overlaySize.Height <= 0)
        {
            CombinedOverlayBorder.IsVisible = false;
            CombinedAnchorMarker.IsVisible = false;
            return;
        }

        overlaySize = new Size(Math.Max(1, overlaySize.Width), Math.Max(1, overlaySize.Height));

        var offset = TryGetCombinedOverlayOffset(displayRect, out var destStart)
            ? destStart
            : new Point(displayRect.X, displayRect.Y);

        CombinedOverlayBorder.IsVisible = true;
        CombinedOverlayBorder.Width = overlaySize.Width;
        CombinedOverlayBorder.Height = overlaySize.Height;
        Canvas.SetLeft(CombinedOverlayBorder, offset.X);
        Canvas.SetTop(CombinedOverlayBorder, offset.Y);
        UpdateCombinedAnchorMarker(displayRect);
    }

    private void UpdateOverlayPositionFromDestStart(int destX, int destY)
    {
        if (_destPreviewBitmap is null || _sourceSelectionPixels is null)
        {
            return;
        }

        if (!TryGetDisplayRect(DestPreviewImage, _destPreviewBitmap, out var displayRect))
        {
            return;
        }

        var bmpWidth = _destPreviewBitmap.PixelSize.Width;
        var bmpHeight = _destPreviewBitmap.PixelSize.Height;
        if (bmpWidth == 0 || bmpHeight == 0)
        {
            return;
        }

        _overlayDisplayOffset = new Point(
            displayRect.X + destX / (double)bmpWidth * displayRect.Width,
            displayRect.Y + destY / (double)bmpHeight * displayRect.Height);

        _overlayDisplayOffset = ClampOverlayToDisplayRect(_overlayDisplayOffset, displayRect);
        UpdateOverlayVisual();
        UpdateCombinedOverlayFromSelection();
    }

    private void UpdateOverlayVisual()
    {
        OverlayBorder.IsVisible = true;
        OverlayBorder.Width = _overlayDisplaySize.Width;
        OverlayBorder.Height = _overlayDisplaySize.Height;
        Canvas.SetLeft(OverlayBorder, _overlayDisplayOffset.X);
        Canvas.SetTop(OverlayBorder, _overlayDisplayOffset.Y);
        UpdateCombinedOverlayVisual();
    }

    private void UpdateCombinedOverlayVisual()
    {
        if (_destPreviewBitmap is null || _sourceSelectionPixels is null)
        {
            return;
        }

        if (!TryGetDisplayRect(CombinedPreviewImage, _destPreviewBitmap, out var displayRect))
        {
            return;
        }

        var rect = _sourceSelectionPixels.Value;
        CombinedOverlayBorder.IsVisible = true;
        CombinedOverlayBorder.Width = rect.Width / (double)_destPreviewBitmap.PixelSize.Width * displayRect.Width;
        CombinedOverlayBorder.Height = rect.Height / (double)_destPreviewBitmap.PixelSize.Height * displayRect.Height;
        var offset = TryGetCombinedOverlayOffset(displayRect, out var destStart)
            ? destStart
            : new Point(displayRect.X, displayRect.Y);
        Canvas.SetLeft(CombinedOverlayBorder, offset.X);
        Canvas.SetTop(CombinedOverlayBorder, offset.Y);
        UpdateCombinedAnchorMarker(displayRect);
    }

    private Point ClampOverlayToDisplayRect(Point offset, Rect displayRect)
    {
        var maxX = displayRect.Right - _overlayDisplaySize.Width;
        var maxY = displayRect.Bottom - _overlayDisplaySize.Height;

        if (maxX < displayRect.X)
        {
            maxX = displayRect.X;
        }

        if (maxY < displayRect.Y)
        {
            maxY = displayRect.Y;
        }

        var clampedX = Math.Clamp(offset.X, displayRect.X, maxX);
        var clampedY = Math.Clamp(offset.Y, displayRect.Y, maxY);
        return new Point(clampedX, clampedY);
    }

    private void UpdateDestStartFromOverlay(Rect displayRect)
    {
        if (_destPreviewBitmap is null || _sourceSelectionPixels is null)
        {
            return;
        }

        var bmpWidth = _destPreviewBitmap.PixelSize.Width;
        var bmpHeight = _destPreviewBitmap.PixelSize.Height;
        if (bmpWidth == 0 || bmpHeight == 0)
        {
            return;
        }

        var rawX = (_overlayDisplayOffset.X - displayRect.X) / displayRect.Width * bmpWidth;
        var rawY = (_overlayDisplayOffset.Y - displayRect.Y) / displayRect.Height * bmpHeight;

        var maxX = Math.Max(0, bmpWidth - _sourceSelectionPixels.Value.Width);
        var maxY = Math.Max(0, bmpHeight - _sourceSelectionPixels.Value.Height);

        var destX = Math.Clamp((int)Math.Round(rawX), 0, maxX);
        var destY = Math.Clamp((int)Math.Round(rawY), 0, maxY);

        UpdateDestStartFields(destX, destY);
        UpdateCombinedOverlayFromSelection();
    }

    private static bool TryGetDisplayRect(Image image, Bitmap bitmap, out Rect displayRect)
    {
        var bounds = image.Bounds;
        displayRect = default;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        var bmpWidth = bitmap.PixelSize.Width;
        var bmpHeight = bitmap.PixelSize.Height;
        if (bmpWidth <= 0 || bmpHeight <= 0)
        {
            return false;
        }

        displayRect = new Rect(0, 0, bounds.Width, bounds.Height);
        return true;
    }

    private static bool TryGetPixelFromPosition(Image image, Bitmap bitmap, Point position, out PixelPoint pixel)
    {
        pixel = default;
        if (!TryGetDisplayRect(image, bitmap, out var displayRect))
        {
            return false;
        }

        if (!displayRect.Contains(position))
        {
            return false;
        }

        var bmpWidth = bitmap.PixelSize.Width;
        var bmpHeight = bitmap.PixelSize.Height;
        if (bmpWidth == 0 || bmpHeight == 0)
        {
            return false;
        }

        var x = (position.X - displayRect.X) / displayRect.Width * bmpWidth;
        var y = (position.Y - displayRect.Y) / displayRect.Height * bmpHeight;

        var ix = Math.Clamp((int)Math.Round(x), 0, bmpWidth - 1);
        var iy = Math.Clamp((int)Math.Round(y), 0, bmpHeight - 1);
        pixel = new PixelPoint(ix, iy);
        return true;
    }

    private bool TryGetSourceRectFromFields(out RectInt rect)
    {
        rect = default;
        if (!TryParseInt(SourceFromXBox.Text, out var fromX) ||
            !TryParseInt(SourceFromYBox.Text, out var fromY) ||
            !TryParseInt(SourceToXBox.Text, out var toX) ||
            !TryParseInt(SourceToYBox.Text, out var toY))
        {
            return false;
        }

        var minX = Math.Min(fromX, toX);
        var minY = Math.Min(fromY, toY);
        var maxX = Math.Max(fromX, toX);
        var maxY = Math.Max(fromY, toY);
        rect = new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
        return true;
    }

    private void UpdatePreviewLabels()
    {
        var sourceText = string.Empty;
        if (TryParseInt(SourceFromXBox.Text, out var fromX) &&
            TryParseInt(SourceFromYBox.Text, out var fromY) &&
            TryParseInt(SourceToXBox.Text, out var toX) &&
            TryParseInt(SourceToYBox.Text, out var toY))
        {
            sourceText = $"Source: {fromX},{fromY} -> {toX},{toY}";
        }

        var destText = string.Empty;
        if (TryParseInt(DestStartXBox.Text, out var destX) &&
            TryParseInt(DestStartYBox.Text, out var destY))
        {
            destText = $"Paste: {destX},{destY}";
        }

        PreviewSourceRangeText.Text = sourceText;
        PreviewDestStartText.Text = destText;
    }

    private void UpdateAlignmentHint(bool copyStatics)
    {
        if (!copyStatics)
        {
            AlignmentHintText.Text = string.Empty;
            return;
        }

        var mode = GetStaticsCopyMode();
        if (mode != StaticsCopyMode.BlockReplaceAligned)
        {
            AlignmentHintText.Text = string.Empty;
            return;
        }

        if (!TryGetRegion(out var sourceRect, out var destStart))
        {
            AlignmentHintText.Text = "Block replace needs /8 alignment.";
            AlignmentHintText.Foreground = Brushes.Gold;
            return;
        }

        if (IsBlockAligned(sourceRect, destStart))
        {
            AlignmentHintText.Text = "Aligned to /8 blocks.";
            AlignmentHintText.Foreground = Brushes.LightGreen;
            return;
        }

        AlignmentHintText.Text = "Auto-snapped to /8 blocks.";
        AlignmentHintText.Foreground = Brushes.Gold;
    }

    private void SnapSelectionToBlocks()
    {
        if (!TryGetRegion(out var sourceRect, out var destStart))
        {
            return;
        }

        if (IsBlockAligned(sourceRect, destStart))
        {
            return;
        }

        var sourceSizeOk = TryResolveSizes(out var sourceSize, out var destSize);
        var sourceWidth = sourceSizeOk ? sourceSize.width : int.MaxValue;
        var sourceHeight = sourceSizeOk ? sourceSize.height : int.MaxValue;
        var destWidth = sourceSizeOk ? destSize.width : int.MaxValue;
        var destHeight = sourceSizeOk ? destSize.height : int.MaxValue;

        var snapped = SnapRectToBlocks(sourceRect, sourceWidth, sourceHeight);
        var snappedDest = SnapPointToBlock(destStart, destWidth, destHeight);

        _syncingRegionFields = true;
        SourceFromXBox.Text = snapped.X.ToString();
        SourceFromYBox.Text = snapped.Y.ToString();
        SourceToXBox.Text = (snapped.Right - 1).ToString();
        SourceToYBox.Text = (snapped.Bottom - 1).ToString();
        DestStartXBox.Text = snappedDest.x.ToString();
        DestStartYBox.Text = snappedDest.y.ToString();
        _syncingRegionFields = false;

        _sourceSelectionPixels = snapped;
        UpdateSourceSelectionVisual();
        UpdateOverlayFromSelection();
        UpdateCombinedOverlayFromSelection();
    }

    private static RectInt SnapRectToBlocks(RectInt rect, int maxWidth, int maxHeight)
    {
        var startBlock = (int)Math.Floor(rect.X / (double)MapMul.BlockSize);
        var endBlock = (int)Math.Floor((rect.Right - 1) / (double)MapMul.BlockSize);
        var startYBlock = (int)Math.Floor(rect.Y / (double)MapMul.BlockSize);
        var endYBlock = (int)Math.Floor((rect.Bottom - 1) / (double)MapMul.BlockSize);

        var startX = Math.Max(0, startBlock * MapMul.BlockSize);
        var startY = Math.Max(0, startYBlock * MapMul.BlockSize);
        var endX = endBlock * MapMul.BlockSize + (MapMul.BlockSize - 1);
        var endY = endYBlock * MapMul.BlockSize + (MapMul.BlockSize - 1);

        if (maxWidth != int.MaxValue)
        {
            endX = Math.Min(endX, maxWidth - 1);
        }

        if (maxHeight != int.MaxValue)
        {
            endY = Math.Min(endY, maxHeight - 1);
        }

        var width = Math.Max(1, endX - startX + 1);
        var height = Math.Max(1, endY - startY + 1);
        return new RectInt(startX, startY, width, height);
    }

    private static (int x, int y) SnapPointToBlock((int x, int y) point, int maxWidth, int maxHeight)
    {
        var blockX = (int)Math.Floor(point.x / (double)MapMul.BlockSize) * MapMul.BlockSize;
        var blockY = (int)Math.Floor(point.y / (double)MapMul.BlockSize) * MapMul.BlockSize;

        if (maxWidth != int.MaxValue)
        {
            blockX = Math.Clamp(blockX, 0, Math.Max(0, maxWidth - MapMul.BlockSize));
        }

        if (maxHeight != int.MaxValue)
        {
            blockY = Math.Clamp(blockY, 0, Math.Max(0, maxHeight - MapMul.BlockSize));
        }

        return (blockX, blockY);
    }

    private void LoadState()
    {
        _loadingState = true;

        SourceMapPathBox.Text = AppSettings.GetString("MapCopy.SourceMapPath", SourceMapPathBox.Text ?? string.Empty);
        SourceStaIdxPathBox.Text = AppSettings.GetString("MapCopy.SourceStaIdxPath", SourceStaIdxPathBox.Text ?? string.Empty);
        SourceStaticsPathBox.Text = AppSettings.GetString("MapCopy.SourceStaticsPath", SourceStaticsPathBox.Text ?? string.Empty);
        DestMapPathBox.Text = AppSettings.GetString("MapCopy.DestMapPath", DestMapPathBox.Text ?? string.Empty);
        DestStaIdxPathBox.Text = AppSettings.GetString("MapCopy.DestStaIdxPath", DestStaIdxPathBox.Text ?? string.Empty);
        DestStaticsPathBox.Text = AppSettings.GetString("MapCopy.DestStaticsPath", DestStaticsPathBox.Text ?? string.Empty);
        SourceClientFolderBox.Text = AppSettings.GetString("MapCopy.SourceClientFolder", SourceClientFolderBox.Text ?? string.Empty);
        DestClientFolderBox.Text = AppSettings.GetString("MapCopy.DestClientFolder", DestClientFolderBox.Text ?? string.Empty);
        SourceFromXBox.Text = AppSettings.GetString("MapCopy.SourceFromX", SourceFromXBox.Text ?? string.Empty);
        SourceFromYBox.Text = AppSettings.GetString("MapCopy.SourceFromY", SourceFromYBox.Text ?? string.Empty);
        SourceToXBox.Text = AppSettings.GetString("MapCopy.SourceToX", SourceToXBox.Text ?? string.Empty);
        SourceToYBox.Text = AppSettings.GetString("MapCopy.SourceToY", SourceToYBox.Text ?? string.Empty);
        DestStartXBox.Text = AppSettings.GetString("MapCopy.DestStartX", DestStartXBox.Text ?? string.Empty);
        DestStartYBox.Text = AppSettings.GetString("MapCopy.DestStartY", DestStartYBox.Text ?? string.Empty);
        OutputFolderBox.Text = AppSettings.GetString("MapCopy.OutputFolder", OutputFolderBox.Text ?? string.Empty);
        VerdataPathBox.Text = AppSettings.GetString("MapCopy.VerdataPath", VerdataPathBox.Text ?? string.Empty);
        VerdataPathBox.Text = AppSettings.GetString("MapCopy.VerdataPath", VerdataPathBox.Text ?? string.Empty);

        CopyStaticsCheckBox.IsChecked = AppSettings.GetBool("MapCopy.CopyStatics", CopyStaticsCheckBox.IsChecked == true);
        CopyMapCheckBox.IsChecked = AppSettings.GetBool("MapCopy.CopyMap", CopyMapCheckBox.IsChecked == true);
        OverwriteTerrainCheckBox.IsChecked = AppSettings.GetBool("MapCopy.OverwriteTerrain", OverwriteTerrainCheckBox.IsChecked == true);
        OverwriteStaticsCheckBox.IsChecked = AppSettings.GetBool("MapCopy.OverwriteStatics", OverwriteStaticsCheckBox.IsChecked == true);
        SaveAsNewMapCheckBox.IsChecked = AppSettings.GetBool("MapCopy.SaveAsNew", SaveAsNewMapCheckBox.IsChecked == true);
        UseVerdataCheckBox.IsChecked = AppSettings.GetBool("MapCopy.UseVerdata", UseVerdataCheckBox.IsChecked == true);
        VerdataTerrainCheckBox.IsChecked = AppSettings.GetBool("MapCopy.VerdataTerrain", VerdataTerrainCheckBox.IsChecked == true);
        VerdataStaticsCheckBox.IsChecked = AppSettings.GetBool("MapCopy.VerdataStatics", VerdataStaticsCheckBox.IsChecked == true);
        VerdataMapFileIdBox.Text = AppSettings.GetString("MapCopy.VerdataMapFileId", VerdataMapFileIdBox.Text ?? string.Empty);
        VerdataStaticsFileIdBox.Text = AppSettings.GetString("MapCopy.VerdataStaticsFileId", VerdataStaticsFileIdBox.Text ?? string.Empty);
        UseVerdataCheckBox.IsChecked = AppSettings.GetBool("MapCopy.UseVerdata", UseVerdataCheckBox.IsChecked == true);

        SourceBmpPathBox.Text = AppSettings.GetString("MapCopy.SourceBmpPath", SourceBmpPathBox.Text ?? string.Empty);
        DestBmpPathBox.Text = AppSettings.GetString("MapCopy.DestBmpPath", DestBmpPathBox.Text ?? string.Empty);

        var encodingText = AppSettings.GetString("MapCopy.PreviewEncoding", string.Empty);
        if (Enum.TryParse<PreviewEncoding>(encodingText, out var encoding))
        {
            var match = _previewEncodings.FirstOrDefault(option => option.Encoding == encoding);
            if (match != null)
            {
                PreviewEncodingComboBox.SelectedItem = match;
            }
        }

        var staticsModeText = AppSettings.GetString("MapCopy.StaticsCopyMode", string.Empty);
        if (Enum.TryParse<StaticsCopyMode>(staticsModeText, out var staticsMode))
        {
            var match = _staticsCopyModes.FirstOrDefault(option => option.Mode == staticsMode);
            if (match != null)
            {
                StaticsCopyModeComboBox.SelectedItem = match;
            }
        }

        var staticsLayoutText = AppSettings.GetString("MapCopy.StaticsLayout", string.Empty);
        if (Enum.TryParse<StaticsLayout>(staticsLayoutText, out var staticsLayout))
        {
            var match = _staticsLayoutOptions.FirstOrDefault(option => option.Layout == staticsLayout);
            if (match != null)
            {
                StaticsLayoutComboBox.SelectedItem = match;
            }
        }

        var staticsZText = AppSettings.GetString("MapCopy.StaticsZMode", string.Empty);
        if (Enum.TryParse<StaticsZMode>(staticsZText, out var staticsZMode))
        {
            var match = _staticsZOptions.FirstOrDefault(option => option.Mode == staticsZMode);
            if (match != null)
            {
                StaticsZModeComboBox.SelectedItem = match;
            }
        }

        StaticsFixedZBox.Text = AppSettings.GetString("MapCopy.StaticsFixedZ", StaticsFixedZBox.Text ?? string.Empty);

        var previewMapTransPath = AppSettings.GetString("MapCopy.PreviewMapTransPath", string.Empty);
        if (!string.IsNullOrWhiteSpace(previewMapTransPath))
        {
            AddPreviewMapTransOption(previewMapTransPath);
        }

        PreviewTileJsonPathBox.Text = AppSettings.GetString("MapCopy.PreviewTileJsonPath", PreviewTileJsonPathBox.Text ?? string.Empty);
        TileReplacePathBox.Text = AppSettings.GetString("MapCopy.TileReplacePath", TileReplacePathBox.Text ?? string.Empty);
        ApplyTileReplaceCheckBox.IsChecked = AppSettings.GetBool("MapCopy.ApplyTileReplace", ApplyTileReplaceCheckBox.IsChecked == true);

        _loadingState = false;
    }

    private void SaveState()
    {
        if (_loadingState || !AppSettings.SaveEnabled)
        {
            return;
        }

        AppSettings.SetString("MapCopy.SourceMapPath", SourceMapPathBox.Text ?? string.Empty);
        AppSettings.SetString("MapCopy.SourceStaIdxPath", SourceStaIdxPathBox.Text ?? string.Empty);
        AppSettings.SetString("MapCopy.SourceStaticsPath", SourceStaticsPathBox.Text ?? string.Empty);
        AppSettings.SetString("MapCopy.DestMapPath", DestMapPathBox.Text ?? string.Empty);
        AppSettings.SetString("MapCopy.DestStaIdxPath", DestStaIdxPathBox.Text ?? string.Empty);
        AppSettings.SetString("MapCopy.DestStaticsPath", DestStaticsPathBox.Text ?? string.Empty);
        AppSettings.SetString("MapCopy.SourceClientFolder", SourceClientFolderBox.Text ?? string.Empty);
        AppSettings.SetString("MapCopy.DestClientFolder", DestClientFolderBox.Text ?? string.Empty);
        AppSettings.SetString("MapCopy.SourceFromX", SourceFromXBox.Text ?? string.Empty);
        AppSettings.SetString("MapCopy.SourceFromY", SourceFromYBox.Text ?? string.Empty);
        AppSettings.SetString("MapCopy.SourceToX", SourceToXBox.Text ?? string.Empty);
        AppSettings.SetString("MapCopy.SourceToY", SourceToYBox.Text ?? string.Empty);
        AppSettings.SetString("MapCopy.DestStartX", DestStartXBox.Text ?? string.Empty);
        AppSettings.SetString("MapCopy.DestStartY", DestStartYBox.Text ?? string.Empty);
        AppSettings.SetString("MapCopy.OutputFolder", OutputFolderBox.Text ?? string.Empty);
        AppSettings.SetString("MapCopy.SourceBmpPath", SourceBmpPathBox.Text ?? string.Empty);
        AppSettings.SetString("MapCopy.DestBmpPath", DestBmpPathBox.Text ?? string.Empty);
        AppSettings.SetString("MapCopy.VerdataPath", VerdataPathBox.Text ?? string.Empty);
        AppSettings.SetString("MapCopy.VerdataPath", VerdataPathBox.Text ?? string.Empty);

        AppSettings.SetBool("MapCopy.CopyStatics", CopyStaticsCheckBox.IsChecked == true);
        AppSettings.SetBool("MapCopy.CopyMap", CopyMapCheckBox.IsChecked == true);
        AppSettings.SetBool("MapCopy.OverwriteTerrain", OverwriteTerrainCheckBox.IsChecked == true);
        AppSettings.SetBool("MapCopy.OverwriteStatics", OverwriteStaticsCheckBox.IsChecked == true);
        AppSettings.SetBool("MapCopy.SaveAsNew", SaveAsNewMapCheckBox.IsChecked == true);
        AppSettings.SetBool("MapCopy.UseVerdata", UseVerdataCheckBox.IsChecked == true);
        AppSettings.SetBool("MapCopy.VerdataTerrain", VerdataTerrainCheckBox.IsChecked == true);
        AppSettings.SetBool("MapCopy.VerdataStatics", VerdataStaticsCheckBox.IsChecked == true);
        AppSettings.SetString("MapCopy.VerdataMapFileId", VerdataMapFileIdBox.Text ?? string.Empty);
        AppSettings.SetString("MapCopy.VerdataStaticsFileId", VerdataStaticsFileIdBox.Text ?? string.Empty);
        AppSettings.SetBool("MapCopy.UseVerdata", UseVerdataCheckBox.IsChecked == true);

        var encoding = GetPreviewEncoding();
        AppSettings.SetString("MapCopy.PreviewEncoding", encoding.ToString());
        AppSettings.SetString("MapCopy.PreviewMapTransPath", GetSelectedPreviewMapTransPath() ?? string.Empty);
        AppSettings.SetString("MapCopy.PreviewTileJsonPath", PreviewTileJsonPathBox.Text ?? string.Empty);
        AppSettings.SetString("MapCopy.StaticsCopyMode", GetStaticsCopyMode().ToString());
        AppSettings.SetString("MapCopy.StaticsLayout", GetStaticsLayout().ToString());
        AppSettings.SetString("MapCopy.StaticsZMode", GetStaticsZMode().ToString());
        AppSettings.SetString("MapCopy.StaticsFixedZ", StaticsFixedZBox.Text ?? string.Empty);
        AppSettings.SetString("MapCopy.TileReplacePath", TileReplacePathBox.Text ?? string.Empty);
        AppSettings.SetBool("MapCopy.ApplyTileReplace", ApplyTileReplaceCheckBox.IsChecked == true);
    }

    public void PersistState()
    {
        SaveState();
    }

    private bool TryGetSelectionRectForSourcePreview(out RectInt rect)
    {
        rect = default;
        if (_sourceSelectionPixels is null)
        {
            return false;
        }

        var selection = _sourceSelectionPixels.Value;
        var previewRect = new RectInt(
            selection.X - _sourcePreviewOriginX,
            selection.Y - _sourcePreviewOriginY,
            selection.Width,
            selection.Height);

        if (_sourcePreviewBitmap is null)
        {
            return false;
        }

        var bmpWidth = _sourcePreviewBitmap.PixelSize.Width;
        var bmpHeight = _sourcePreviewBitmap.PixelSize.Height;
        if (bmpWidth <= 0 || bmpHeight <= 0)
        {
            return false;
        }

        if (previewRect.X < 0 || previewRect.Y < 0 ||
            previewRect.Right > bmpWidth || previewRect.Bottom > bmpHeight)
        {
            return false;
        }

        rect = previewRect;
        return true;
    }

    private bool TryGetFullMapRectFromFields(int width, int height, out RectInt rect)
    {
        rect = default;

        var fromXText = SourceFromXBox.Text;
        var fromYText = SourceFromYBox.Text;
        var toXText = SourceToXBox.Text;
        var toYText = SourceToYBox.Text;
        var fromXEmpty = string.IsNullOrWhiteSpace(fromXText);
        var fromYEmpty = string.IsNullOrWhiteSpace(fromYText);
        var toXEmpty = string.IsNullOrWhiteSpace(toXText);
        var toYEmpty = string.IsNullOrWhiteSpace(toYText);

        if (fromXEmpty && fromYEmpty && toXEmpty && toYEmpty)
        {
            rect = new RectInt(0, 0, width, height);
            return true;
        }

        if (!TryParseInt(fromXText, out var fromX) ||
            !TryParseInt(fromYText, out var fromY))
        {
            return false;
        }

        if (fromX != 0 || fromY != 0)
        {
            return false;
        }

        var toXHas = TryParseInt(toXText, out var toX);
        var toYHas = TryParseInt(toYText, out var toY);

        if ((toXHas && toX != 0) || (toYHas && toY != 0))
        {
            return false;
        }

        rect = new RectInt(0, 0, width, height);
        return true;
    }

    private bool AreSourceRegionFieldsEmpty()
    {
        return string.IsNullOrWhiteSpace(SourceFromXBox.Text) &&
               string.IsNullOrWhiteSpace(SourceFromYBox.Text) &&
               string.IsNullOrWhiteSpace(SourceToXBox.Text) &&
               string.IsNullOrWhiteSpace(SourceToYBox.Text);
    }

    private static bool TryParsePreviewCrop(string path, out int x, out int y, out int width, out int height)
    {
        x = 0;
        y = 0;
        width = 0;
        height = 0;

        var name = Path.GetFileNameWithoutExtension(path);
        var marker = "_preview_";
        var markerIndex = name.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var tail = name[(markerIndex + marker.Length)..];
        var parts = tail.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return false;
        }

        if (!int.TryParse(parts[0], out x) || !int.TryParse(parts[1], out y))
        {
            return false;
        }

        var sizeParts = parts[2].Split('x', 'X');
        if (sizeParts.Length != 2)
        {
            return false;
        }

        return int.TryParse(sizeParts[0], out width) && int.TryParse(sizeParts[1], out height);
    }

    private void UpdatePreviewActionVisibility()
    {
        PreviewGenerateButton.IsVisible = true;
        var hasSourceMap = !string.IsNullOrWhiteSpace(SourceMapPathBox.Text) && File.Exists(SourceMapPathBox.Text);
        var hasDestMap = !string.IsNullOrWhiteSpace(DestMapPathBox.Text) && File.Exists(DestMapPathBox.Text);
        PreviewGenerateButton.IsEnabled = hasSourceMap && hasDestMap;
    }

    private void UpdatePreviewEncodingVisibility()
    {
        var encoding = GetPreviewEncoding();
        var useMapTrans = encoding == PreviewEncoding.MapTrans;
        var useTileJson = encoding == PreviewEncoding.TileJson;

        PreviewMapTransLabel.IsVisible = useMapTrans;
        PreviewMapTransComboBox.IsVisible = useMapTrans;
        PreviewMapTransBrowseButton.IsVisible = useMapTrans;
        PreviewTileJsonLabel.IsVisible = useTileJson;
        PreviewTileJsonPathBox.IsVisible = useTileJson;
        PreviewTileJsonPathBox.IsEnabled = useTileJson;
        PreviewTileJsonBrowseButton.IsVisible = useTileJson;
    }

    private void LoadPreviewEncodings()
    {
        _previewEncodings.Clear();
        _previewEncodings.Add(new PreviewEncodingOption(PreviewEncoding.MapTrans, "MapTrans (8-bit palette)"));
        _previewEncodings.Add(new PreviewEncodingOption(PreviewEncoding.TileJson, "Tile JSON (8/24-bit)"));
        _previewEncodings.Add(new PreviewEncodingOption(PreviewEncoding.TileIndexRgb, "TileIndex RGB (24-bit)"));
        PreviewEncodingComboBox.ItemsSource = _previewEncodings;
        PreviewEncodingComboBox.SelectedIndex = 2;
    }

    private PreviewEncoding GetPreviewEncoding()
    {
        return PreviewEncodingComboBox.SelectedItem is PreviewEncodingOption option
            ? option.Encoding
            : PreviewEncoding.TileIndexRgb;
    }

    private void LoadPreviewMapTransOptions()
    {
        var selectedPath = GetSelectedPreviewMapTransPath();
        _previewMapTransOptions.Clear();
        var roots = FindMapTransRoots().ToList();
        foreach (var file in MapTransCatalog.FindMapTransFiles(roots))
        {
            AddPreviewMapTransOption(file, select: false);
        }

        RefreshPreviewMapTransOptions(selectedPath);
    }

    private void AddPreviewMapTransOption(string path, bool select = true)
    {
        if (_previewMapTransOptions.Exists(option => option.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            if (select)
            {
                PreviewMapTransComboBox.SelectedItem = _previewMapTransOptions.Find(option =>
                    option.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            }
            return;
        }

        var parentName = new DirectoryInfo(Path.GetDirectoryName(path) ?? string.Empty).Name;
        var name = string.IsNullOrWhiteSpace(parentName)
            ? Path.GetFileName(path)
            : $"{parentName}/{Path.GetFileName(path)}";
        _previewMapTransOptions.Add(new MapTransOption(name, path));
        RefreshPreviewMapTransOptions(select ? path : null);
    }

    private string? GetSelectedPreviewMapTransPath()
        => PreviewMapTransComboBox.SelectedItem is MapTransOption option ? option.Path : null;

    private void RefreshPreviewMapTransOptions(string? selectPath)
    {
        _previewMapTransOptions.Sort(CompareMapTransOption);
        PreviewMapTransComboBox.ItemsSource = null;
        PreviewMapTransComboBox.ItemsSource = _previewMapTransOptions;

        if (!string.IsNullOrWhiteSpace(selectPath))
        {
            PreviewMapTransComboBox.SelectedItem = _previewMapTransOptions.Find(option =>
                option.Path.Equals(selectPath, StringComparison.OrdinalIgnoreCase));
        }
        else if (PreviewMapTransComboBox.SelectedItem is null && _previewMapTransOptions.Count > 0)
        {
            PreviewMapTransComboBox.SelectedIndex = 0;
        }
    }

    private static IEnumerable<string> FindMapTransRoots()
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var baseDir in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(baseDir);
            for (var i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                AddRoot(dir.FullName, "MapCreator_golfin\\MapCompiler\\Engine\\MapTrans");
                AddRoot(dir.FullName, "TMP_UtitlityFiles\\MapCreator_golfin\\MapCompiler\\Engine\\MapTrans");
                AddRoot(Path.Combine(dir.FullName, "UOMapWeaver"), "MapCreator_golfin\\MapCompiler\\Engine\\MapTrans");
            }

            AddRoot(baseDir, "MapTrans");
            AddRoot(baseDir, UOMapWeaverDataPaths.MapTransRoot);
        }

        return roots;

        void AddRoot(string root, string relative)
        {
            var candidate = Path.Combine(root, relative);
            if (Directory.Exists(candidate) && seen.Add(candidate))
            {
                roots.Add(candidate);
            }
        }
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

    private static bool TryResolveTerrainColor(
        Dictionary<ushort, List<MapTransEntry>> lookup,
        ushort tileId,
        sbyte z,
        out byte colorIndex)
    {
        if (!lookup.TryGetValue(tileId, out var entries) || entries.Count == 0)
        {
            colorIndex = 0;
            return false;
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

    private static BmpPaletteEntry[] LoadPreviewPalette(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            return Bmp8Codec.Read(path).Palette;
        }

        return Bmp8Codec.CreateGrayscalePalette();
    }

    private static void ApplyUnknownPaletteColor(BmpPaletteEntry[] palette)
    {
        if (palette.Length <= 255)
        {
            return;
        }

        palette[255] = new BmpPaletteEntry(255, 0, 255, 0);
    }

    private bool TryGetCombinedOverlayOffset(Rect displayRect, out Point offset)
    {
        offset = default;
        if (_destPreviewBitmap is null)
        {
            return false;
        }

        if (!TryParseInt(DestStartXBox.Text, out var destX) ||
            !TryParseInt(DestStartYBox.Text, out var destY))
        {
            return false;
        }

        var bmpWidth = _destPreviewBitmap.PixelSize.Width;
        var bmpHeight = _destPreviewBitmap.PixelSize.Height;
        if (bmpWidth == 0 || bmpHeight == 0)
        {
            return false;
        }

        offset = new Point(
            displayRect.X + destX / (double)bmpWidth * displayRect.Width,
            displayRect.Y + destY / (double)bmpHeight * displayRect.Height);
        return true;
    }

    private void UpdateCombinedAnchorMarker(Rect displayRect)
    {
        if (_destPreviewBitmap is null)
        {
            CombinedAnchorMarker.IsVisible = false;
            return;
        }

        if (!TryParseInt(DestStartXBox.Text, out var destX) ||
            !TryParseInt(DestStartYBox.Text, out var destY))
        {
            CombinedAnchorMarker.IsVisible = false;
            return;
        }

        var bmpWidth = _destPreviewBitmap.PixelSize.Width;
        var bmpHeight = _destPreviewBitmap.PixelSize.Height;
        if (bmpWidth == 0 || bmpHeight == 0)
        {
            CombinedAnchorMarker.IsVisible = false;
            return;
        }

        var markerX = displayRect.X + destX / (double)bmpWidth * displayRect.Width;
        var markerY = displayRect.Y + destY / (double)bmpHeight * displayRect.Height;
        CombinedAnchorMarker.IsVisible = true;
        Canvas.SetLeft(CombinedAnchorMarker, markerX - CombinedAnchorMarker.Width / 2);
        Canvas.SetTop(CombinedAnchorMarker, markerY - CombinedAnchorMarker.Height / 2);
    }

    private static void ReportProgress(long processedRows, long totalRows, ref int lastProgress)
    {
        if (totalRows <= 0)
        {
            return;
        }

        var percent = (int)(processedRows * 100.0 / totalRows);
        if (percent == lastProgress)
        {
            return;
        }

        lastProgress = percent;
        Dispatcher.UIThread.Post(() => AppStatus.SetProgress(percent, true));
    }

    private bool TryLoadVerdata(int width, int height, out VerdataMul? verdata, bool reportToStatus)
    {
        verdata = null;
        if (UseVerdataCheckBox.IsChecked != true)
        {
            return true;
        }

        var path = VerdataPathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            if (reportToStatus)
            {
                StatusText.Text = "Verdata.mul not found.";
                AppStatus.SetError(StatusText.Text);
            }
            else
            {
                AppStatus.SetError("Verdata.mul not found.");
            }
            return false;
        }

        var blockCount = (width / MapMul.BlockSize) * (height / MapMul.BlockSize);
        var mapFileId = ParseOptionalInt(VerdataMapFileIdBox.Text);
        var staticsFileId = ParseOptionalInt(VerdataStaticsFileIdBox.Text);
        verdata = VerdataMul.Load(path, blockCount, mapFileId, staticsFileId);
        return true;
    }

    private static void ApplyVerdataRow(VerdataMul verdata, int y, LandTile[] row, int width, int height)
    {
        if (verdata.MapPatchCount == 0)
        {
            return;
        }

        if (y < 0 || y >= height)
        {
            return;
        }

        var blockWidth = width / MapMul.BlockSize;
        var blockHeight = height / MapMul.BlockSize;
        var blockY = y / MapMul.BlockSize;
        var localY = y % MapMul.BlockSize;

        for (var blockX = 0; blockX < blockWidth; blockX++)
        {
            var blockId = blockX * blockHeight + blockY;
            if (!verdata.TryGetMapBlock(blockId, out var blockTiles))
            {
                continue;
            }

            var rowStart = blockX * MapMul.BlockSize;
            var offset = localY * MapMul.BlockSize;
            for (var localX = 0; localX < MapMul.BlockSize; localX++)
            {
                row[rowStart + localX] = blockTiles[offset + localX];
            }
        }
    }

    private static void AutoPopulateStatics(string? mapPath, TextBox staIdxBox, TextBox staticsBox)
    {
        if (string.IsNullOrWhiteSpace(mapPath) || !File.Exists(mapPath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(mapPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(mapPath);

        string staticsName;
        string staidxName;

        if (!TryResolveStaticsNames(name, out staticsName, out staidxName))
        {
            staticsName = "statics.mul";
            staidxName = "staidx.mul";
        }

        var staticsPath = Path.Combine(directory, staticsName);
        var staidxPath = Path.Combine(directory, staidxName);

        if (File.Exists(staticsPath))
        {
            staticsBox.Text = staticsPath;
        }

        if (File.Exists(staidxPath))
        {
            staIdxBox.Text = staidxPath;
        }
    }

    private static void AutoPopulateVerdata(string? mapPath, TextBox verdataBox)
    {
        if (string.IsNullOrWhiteSpace(mapPath) || !File.Exists(mapPath))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(verdataBox.Text))
        {
            return;
        }

        var directory = Path.GetDirectoryName(mapPath) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var candidates = Directory.EnumerateFiles(directory, "verdata*.mul", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 1)
        {
            verdataBox.Text = candidates[0];
        }
    }

    private static bool TryParseOptionalInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        return int.TryParse(text.Trim(), out var value) && value >= 0;
    }

    private static int? ParseOptionalInt(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return int.TryParse(text.Trim(), out var value) && value >= 0 ? value : null;
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

    private static void ResolveStaticsPaths(
        string mapPath,
        string? staIdxPath,
        string? staticsPath,
        out string resolvedStaIdx,
        out string resolvedStatics)
    {
        resolvedStaIdx = staIdxPath ?? string.Empty;
        resolvedStatics = staticsPath ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(resolvedStaIdx) && !string.IsNullOrWhiteSpace(resolvedStatics))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(mapPath))
        {
            return;
        }

        var name = Path.GetFileNameWithoutExtension(mapPath);
        if (!TryResolveStaticsNames(name, out var staticsName, out var staidxName))
        {
            return;
        }

        var directory = Path.GetDirectoryName(mapPath) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(resolvedStaIdx))
        {
            resolvedStaIdx = Path.Combine(directory, staidxName);
        }

        if (string.IsNullOrWhiteSpace(resolvedStatics))
        {
            resolvedStatics = Path.Combine(directory, staticsName);
        }
    }

    private async Task<bool> ConfirmOverwriteAsync(string title, params string[] paths)
    {
        var existing = paths.Where(File.Exists).Select(Path.GetFileName).ToList();
        if (existing.Count == 0)
        {
            return true;
        }

        var message = "The following files already exist:\n" +
                      string.Join('\n', existing) +
                      "\n\nOverwrite them?";

        var dialog = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };

        var overwriteButton = new Button { Content = "Overwrite", MinWidth = 90 };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 90 };

        overwriteButton.Click += (_, _) => dialog.Close(true);
        cancelButton.Click += (_, _) => dialog.Close(false);

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
        };
        buttons.Children.Add(overwriteButton);
        buttons.Children.Add(cancelButton);

        var layout = new StackPanel { Spacing = 12, Margin = new Avalonia.Thickness(16) };
        layout.Children.Add(text);
        layout.Children.Add(buttons);

        dialog.Content = layout;
        return await dialog.ShowDialog<bool>(GetOwnerWindow());
    }

    private Window? GetHostWindow() => VisualRoot as Window;

    private Window GetOwnerWindow()
        => GetHostWindow() ?? throw new InvalidOperationException("Host window not available.");

    private readonly record struct RectInt(int X, int Y, int Width, int Height)
    {
        public int Right => X + Width;

        public int Bottom => Y + Height;

        public override string ToString() => $"{X},{Y} -> {Right - 1},{Bottom - 1}";
    }

    private void ConfigureZoomState(ZoomPanState state, ScrollViewer scroll, Canvas canvas, Image image, Action? onZoomApplied)
    {
        state.Scroll = scroll;
        state.Canvas = canvas;
        state.Image = image;
        state.OnZoomApplied = onZoomApplied;

        scroll.SizeChanged += (_, _) =>
        {
            if (!state.ManualZoom)
            {
                FitZoomToViewport(state);
            }
        };
    }

    private void UpdateZoomState(ZoomPanState state, Bitmap? bitmap)
    {
        if (bitmap is null)
        {
            state.BmpWidth = 0;
            state.BmpHeight = 0;
            state.Zoom = 1;
            state.ManualZoom = false;
            state.Canvas.Width = 0;
            state.Canvas.Height = 0;
            state.Image.Width = 0;
            state.Image.Height = 0;
            return;
        }

        state.BmpWidth = bitmap.PixelSize.Width;
        state.BmpHeight = bitmap.PixelSize.Height;
        state.ManualZoom = false;
        FitZoomToViewport(state);
    }

    private static void HandleZoomWheel(ZoomPanState state, PointerWheelEventArgs e)
    {
        if (state.BmpWidth <= 0 || state.BmpHeight <= 0)
        {
            return;
        }

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        var delta = e.Delta.Y;
        if (Math.Abs(delta) < 0.01)
        {
            return;
        }

        state.ManualZoom = true;
        var factor = delta > 0 ? 1.1 : 0.9;
        state.Zoom = Math.Clamp(state.Zoom * factor, state.MinZoom, 20);
        ApplyZoom(state);
        e.Handled = true;
    }

    private static void HandlePanPressed(ZoomPanState state, PointerPressedEventArgs e)
    {
        if (state.BmpWidth <= 0 || state.BmpHeight <= 0)
        {
            return;
        }

        var point = e.GetCurrentPoint(state.Scroll);
        if (!point.Properties.IsRightButtonPressed)
        {
            return;
        }

        state.IsPanning = true;
        state.PanStart = e.GetPosition(state.Scroll);
        state.PanOffset = state.Scroll.Offset;
        e.Pointer.Capture(state.Scroll);
        e.Handled = true;
    }

    private static void HandlePanMoved(ZoomPanState state, PointerEventArgs e)
    {
        if (!state.IsPanning)
        {
            return;
        }

        var current = e.GetPosition(state.Scroll);
        var delta = current - state.PanStart;
        state.Scroll.Offset = state.PanOffset - new Vector(delta.X, delta.Y);
        e.Handled = true;
    }

    private static void HandlePanReleased(ZoomPanState state, PointerReleasedEventArgs e)
    {
        if (!state.IsPanning)
        {
            return;
        }

        state.IsPanning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private static void FitZoomToViewport(ZoomPanState state)
    {
        if (state.BmpWidth <= 0 || state.BmpHeight <= 0)
        {
            return;
        }

        var viewport = state.Scroll.Bounds;
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            return;
        }

        var scale = Math.Min(viewport.Width / state.BmpWidth, viewport.Height / state.BmpHeight);
        if (double.IsInfinity(scale) || scale <= 0)
        {
            scale = 1;
        }

        state.MinZoom = Math.Clamp(scale, 0.05, 20);
        state.Zoom = state.ManualZoom ? Math.Max(state.Zoom, state.MinZoom) : state.MinZoom;
        ApplyZoom(state);
    }

    private static void ApplyZoom(ZoomPanState state)
    {
        if (state.BmpWidth <= 0 || state.BmpHeight <= 0)
        {
            return;
        }

        state.Canvas.Width = state.BmpWidth * state.Zoom;
        state.Canvas.Height = state.BmpHeight * state.Zoom;
        state.Image.Width = state.Canvas.Width;
        state.Image.Height = state.Canvas.Height;
        state.OnZoomApplied?.Invoke();
    }

    private enum PreviewEncoding
    {
        MapTrans,
        TileJson,
        TileIndexRgb
    }

    private enum StaticsCopyMode
    {
        CellMatch,
        EntryTranslate,
        BlockReplaceAligned
    }

    private sealed class StaticsCopyModeOption
    {
        public StaticsCopyModeOption(StaticsCopyMode mode, string name)
        {
            Mode = mode;
            Name = name;
        }

        public StaticsCopyMode Mode { get; }

        public string Name { get; }

        public override string ToString() => Name;
    }

    private enum StaticsLayout
    {
        RowMajor,
        ColumnMajor
    }

    private sealed class StaticsLayoutOption
    {
        public StaticsLayoutOption(StaticsLayout layout, string name)
        {
            Layout = layout;
            Name = name;
        }

        public StaticsLayout Layout { get; }

        public string Name { get; }

        public override string ToString() => Name;
    }

    private enum StaticsZMode
    {
        Keep,
        OffsetByTerrain,
        Fixed
    }

    private sealed class StaticsZOption
    {
        public StaticsZOption(StaticsZMode mode, string name)
        {
            Mode = mode;
            Name = name;
        }

        public StaticsZMode Mode { get; }

        public string Name { get; }

        public override string ToString() => Name;
    }

    private static bool IsBlockAligned(RectInt rect, (int x, int y) destStart)
    {
        return rect.X % MapMul.BlockSize == 0 &&
               rect.Y % MapMul.BlockSize == 0 &&
               rect.Width % MapMul.BlockSize == 0 &&
               rect.Height % MapMul.BlockSize == 0 &&
               destStart.x % MapMul.BlockSize == 0 &&
               destStart.y % MapMul.BlockSize == 0;
    }

    private static int GetBlockIndex(int blockX, int blockY, int blockWidth, int blockHeight, StaticsLayout layout)
    {
        return layout == StaticsLayout.ColumnMajor
            ? blockX * blockHeight + blockY
            : blockY * blockWidth + blockX;
    }

    private static sbyte AdjustStaticZ(
        StaticMulEntry entry,
        int srcX,
        int srcY,
        int destX,
        int destY,
        StaticsZMode mode,
        sbyte fixedZ,
        LandTile[]? sourceTiles,
        int sourceWidth,
        LandTile[]? destTiles,
        int destWidth)
    {
        return mode switch
        {
            StaticsZMode.Fixed => fixedZ,
            StaticsZMode.OffsetByTerrain when sourceTiles is not null && destTiles is not null
                => ClampZ(entry.Z + (destTiles[destY * destWidth + destX].Z - sourceTiles[srcY * sourceWidth + srcX].Z)),
            _ => entry.Z
        };
    }

    private static LandTile ApplyTerrainReplacement(LandTile tile, TileReplacementMap? replacements)
    {
        if (replacements is not null && replacements.TryGetTerrainReplacement(tile.TileId, out var replacement))
        {
            return new LandTile(replacement, tile.Z);
        }

        return tile;
    }

    private static StaticMulEntry ApplyStaticReplacement(StaticMulEntry entry, TileReplacementMap? replacements)
    {
        if (replacements is not null && replacements.TryGetStaticReplacement(entry.TileId, out var replacement))
        {
            return entry with { TileId = replacement };
        }

        return entry;
    }

    private static sbyte ClampZ(int value)
        => (sbyte)Math.Clamp(value, sbyte.MinValue, sbyte.MaxValue);

    private static int ClearDestStaticsCells(
        List<StaticMulEntry>[] destBlocks,
        int destWidth,
        int destBlockHeight,
        RectInt sourceRect,
        (int x, int y) destStart,
        StaticsLayout layout)
    {
        var blockWidth = destWidth / MapMul.BlockSize;
        var removed = 0;

        for (var y = 0; y < sourceRect.Height; y++)
        {
            for (var x = 0; x < sourceRect.Width; x++)
            {
                var dstX = destStart.x + x;
                var dstY = destStart.y + y;
                var dstBlockIndex = GetBlockIndex(dstX / MapMul.BlockSize, dstY / MapMul.BlockSize, blockWidth,
                    destBlockHeight, layout);
                if (dstBlockIndex < 0 || dstBlockIndex >= destBlocks.Length)
                {
                    continue;
                }

                var dstList = destBlocks[dstBlockIndex];
                if (dstList is null || dstList.Count == 0)
                {
                    continue;
                }

                var cellX = dstX % MapMul.BlockSize;
                var cellY = dstY % MapMul.BlockSize;
                var beforeCount = dstList.Count;
                dstList.RemoveAll(entry => entry.X == cellX && entry.Y == cellY);
                removed += beforeCount - dstList.Count;
            }
        }

        return removed;
    }

    private readonly record struct StaticSampleCheck(
        ushort TileId,
        int BlockIndex,
        byte CellX,
        byte CellY,
        int DestX,
        int DestY);

    private static void CompareStaticsRegionTranslated(
        List<StaticMulEntry>[] sourceBlocks,
        int sourceWidth,
        RectInt sourceRect,
        int deltaX,
        int deltaY,
        List<StaticMulEntry>[] destBlocks,
        int destWidth,
        RectInt destRect,
        StaticsLayout layout)
    {
        try
        {
            var sourceMap = BuildStaticsMultiset(sourceBlocks, sourceWidth, sourceRect, deltaX, deltaY, layout);
            var destMap = BuildStaticsMultiset(destBlocks, destWidth, destRect, 0, 0, layout);

            var missing = 0;
            var extra = 0;
            var missingSamples = new List<string>();
            var extraSamples = new List<string>();

            foreach (var (key, count) in sourceMap)
            {
                destMap.TryGetValue(key, out var destCount);
                if (destCount < count)
                {
                    missing += count - destCount;
                    if (missingSamples.Count < 10)
                    {
                        missingSamples.Add(FormatStaticKey(key, count - destCount));
                    }
                }
            }

            foreach (var (key, count) in destMap)
            {
                sourceMap.TryGetValue(key, out var sourceCount);
                if (count > sourceCount)
                {
                    extra += count - sourceCount;
                    if (extraSamples.Count < 10)
                    {
                        extraSamples.Add(FormatStaticKey(key, count - sourceCount));
                    }
                }
            }

            AppStatus.AppendLog(
                $"Statics compare: source {sourceMap.Values.Sum():N0} entries, dest {destMap.Values.Sum():N0} entries. Missing {missing:N0}, extra {extra:N0}.",
                AppStatusSeverity.Info);

            if (missingSamples.Count > 0)
            {
                AppStatus.AppendLog($"Statics missing sample: {string.Join(" | ", missingSamples)}", AppStatusSeverity.Warning);
            }

            if (extraSamples.Count > 0)
            {
                AppStatus.AppendLog($"Statics extra sample: {string.Join(" | ", extraSamples)}", AppStatusSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            AppStatus.AppendLog($"Statics compare error: {ex.Message}", AppStatusSeverity.Warning);
        }
    }

    private static Dictionary<StaticKey, int> BuildStaticsMultiset(
        List<StaticMulEntry>[] blocks,
        int mapWidth,
        RectInt rect,
        int offsetX,
        int offsetY,
        StaticsLayout layout)
    {
        var result = new Dictionary<StaticKey, int>();
        var blockWidth = mapWidth / MapMul.BlockSize;
        var blockHeight = blocks.Length / Math.Max(1, blockWidth);

        var startBlockX = rect.X / MapMul.BlockSize;
        var endBlockX = (rect.Right - 1) / MapMul.BlockSize;
        var startBlockY = rect.Y / MapMul.BlockSize;
        var endBlockY = (rect.Bottom - 1) / MapMul.BlockSize;

        for (var blockY = startBlockY; blockY <= endBlockY; blockY++)
        {
            for (var blockX = startBlockX; blockX <= endBlockX; blockX++)
            {
                var blockIndex = GetBlockIndex(blockX, blockY, blockWidth, blockHeight, layout);
                if (blockIndex < 0 || blockIndex >= blocks.Length)
                {
                    continue;
                }

                var list = blocks[blockIndex];
                if (list is null || list.Count == 0)
                {
                    continue;
                }

                foreach (var entry in list)
                {
                    var globalX = blockX * MapMul.BlockSize + entry.X;
                    var globalY = blockY * MapMul.BlockSize + entry.Y;
                    if (globalX < rect.X || globalX >= rect.Right ||
                        globalY < rect.Y || globalY >= rect.Bottom)
                    {
                        continue;
                    }

                    var key = new StaticKey(entry.TileId, globalX + offsetX, globalY + offsetY, entry.Z, entry.Hue);
                    if (result.TryGetValue(key, out var count))
                    {
                        result[key] = count + 1;
                    }
                    else
                    {
                        result[key] = 1;
                    }
                }
            }
        }

        return result;
    }

    private static int CountStaticsEntriesInRect(
        List<StaticMulEntry>[] blocks,
        int mapWidth,
        RectInt rect,
        StaticsLayout layout)
    {
        var count = 0;
        var blockWidth = mapWidth / MapMul.BlockSize;
        var blockHeight = blocks.Length / Math.Max(1, blockWidth);

        var startBlockX = rect.X / MapMul.BlockSize;
        var endBlockX = (rect.Right - 1) / MapMul.BlockSize;
        var startBlockY = rect.Y / MapMul.BlockSize;
        var endBlockY = (rect.Bottom - 1) / MapMul.BlockSize;

        for (var blockY = startBlockY; blockY <= endBlockY; blockY++)
        {
            for (var blockX = startBlockX; blockX <= endBlockX; blockX++)
            {
                var blockIndex = GetBlockIndex(blockX, blockY, blockWidth, blockHeight, layout);
                if (blockIndex < 0 || blockIndex >= blocks.Length)
                {
                    continue;
                }

                var list = blocks[blockIndex];
                if (list is null || list.Count == 0)
                {
                    continue;
                }

                foreach (var entry in list)
                {
                    var globalX = blockX * MapMul.BlockSize + entry.X;
                    var globalY = blockY * MapMul.BlockSize + entry.Y;
                    if (globalX < rect.X || globalX >= rect.Right ||
                        globalY < rect.Y || globalY >= rect.Bottom)
                    {
                        continue;
                    }

                    count++;
                }
            }
        }

        return count;
    }

    private static void LogStaticsBlockDiagnostics(
        string label,
        string staIdxPath,
        string staticsPath,
        int width,
        int height,
        IReadOnlyList<List<StaticMulEntry>> blocks)
    {
        var expectedBlocks = (width / MapMul.BlockSize) * (height / MapMul.BlockSize);
        var idxBlocks = -1L;
        if (!string.IsNullOrWhiteSpace(staIdxPath) && File.Exists(staIdxPath))
        {
            idxBlocks = new FileInfo(staIdxPath).Length / MapMul.StaticIndexRecordBytes;
        }

        AppStatus.AppendLog(
            $"{label} statics blocks: expected {expectedBlocks:N0}, staidx {idxBlocks:N0}, loaded {blocks.Count:N0}.",
            AppStatusSeverity.Info);

        if (idxBlocks >= 0 && idxBlocks != expectedBlocks)
        {
            AppStatus.AppendLog(
                $"{label} statics size mismatch: map expects {expectedBlocks:N0} blocks, staidx has {idxBlocks:N0}.",
                AppStatusSeverity.Warning);
        }

        if (!string.IsNullOrWhiteSpace(staticsPath) && File.Exists(staticsPath))
        {
            AppStatus.AppendLog($"{label} statics file bytes: {new FileInfo(staticsPath).Length:N0}.", AppStatusSeverity.Info);
        }
    }

    private static string FormatStaticKey(StaticKey key, int count)
        => $"0x{key.TileId:X4} @ {key.X},{key.Y} z{key.Z} h{key.Hue} x{count}";

    private readonly record struct StaticKey(ushort TileId, int X, int Y, sbyte Z, ushort Hue);

    private sealed class ZoomPanState
    {
        public ScrollViewer Scroll { get; set; } = null!;
        public Canvas Canvas { get; set; } = null!;
        public Image Image { get; set; } = null!;
        public Action? OnZoomApplied { get; set; }
        public int BmpWidth { get; set; }
        public int BmpHeight { get; set; }
        public double Zoom { get; set; } = 1;
        public double MinZoom { get; set; } = 0.05;
        public bool ManualZoom { get; set; }
        public bool IsPanning { get; set; }
        public Point PanStart { get; set; }
        public Vector PanOffset { get; set; }
    }

    private sealed class PreviewEncodingOption
    {
        public PreviewEncodingOption(PreviewEncoding encoding, string name)
        {
            Encoding = encoding;
            Name = name;
        }

        public PreviewEncoding Encoding { get; }

        public string Name { get; }

        public override string ToString() => Name;
    }

    private sealed class MapTransOption
    {
        public MapTransOption(string name, string path)
        {
            Name = name;
            Path = path;
        }

        public string Name { get; }

        public string Path { get; }

        public override string ToString() => Name;
    }

    private static int CompareMapTransOption(MapTransOption left, MapTransOption right)
        => CompareNatural(left.Name, right.Name);

    private static int CompareNatural(string left, string right)
    {
        var leftIndex = 0;
        var rightIndex = 0;

        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            var leftChar = left[leftIndex];
            var rightChar = right[rightIndex];

            if (char.IsDigit(leftChar) && char.IsDigit(rightChar))
            {
                var leftNumber = ReadNumber(left, ref leftIndex);
                var rightNumber = ReadNumber(right, ref rightIndex);
                var numberCompare = leftNumber.CompareTo(rightNumber);
                if (numberCompare != 0)
                {
                    return numberCompare;
                }
                continue;
            }

            var charCompare = char.ToUpperInvariant(leftChar).CompareTo(char.ToUpperInvariant(rightChar));
            if (charCompare != 0)
            {
                return charCompare;
            }

            leftIndex++;
            rightIndex++;
        }

        return left.Length.CompareTo(right.Length);
    }

    private static int ReadNumber(string value, ref int index)
    {
        var start = index;
        while (index < value.Length && char.IsDigit(value[index]))
        {
            index++;
        }

        if (int.TryParse(value[start..index], out var number))
        {
            return number;
        }

        return 0;
    }

    private static void CountStaticsTileIdsInRect(
        List<StaticMulEntry>[] blocks,
        int mapWidth,
        int mapHeight,
        RectInt rect,
        StaticsLayout layout,
        Dictionary<ushort, int> counts)
    {
        var blockWidth = mapWidth / MapMul.BlockSize;
        var blockHeight = mapHeight / MapMul.BlockSize;

        var startBlockX = rect.X / MapMul.BlockSize;
        var endBlockX = (rect.Right - 1) / MapMul.BlockSize;
        var startBlockY = rect.Y / MapMul.BlockSize;
        var endBlockY = (rect.Bottom - 1) / MapMul.BlockSize;

        for (var blockY = startBlockY; blockY <= endBlockY; blockY++)
        {
            for (var blockX = startBlockX; blockX <= endBlockX; blockX++)
            {
                var blockIndex = GetBlockIndex(blockX, blockY, blockWidth, blockHeight, layout);
                if (blockIndex < 0 || blockIndex >= blocks.Length)
                {
                    continue;
                }

                var list = blocks[blockIndex];
                if (list is null || list.Count == 0)
                {
                    continue;
                }

                foreach (var entry in list)
                {
                    var globalX = blockX * MapMul.BlockSize + entry.X;
                    var globalY = blockY * MapMul.BlockSize + entry.Y;
                    if (globalX < rect.X || globalX >= rect.Right ||
                        globalY < rect.Y || globalY >= rect.Bottom)
                    {
                        continue;
                    }

                    counts[entry.TileId] = counts.TryGetValue(entry.TileId, out var current) ? current + 1 : 1;
                }
            }
        }
    }

    private TileReplacementMap? BuildReplacementMap()
    {
        if (ApplyTileReplaceCheckBox.IsChecked != true)
        {
            return null;
        }

        var map = BuildReplacementMapFromUi();
        if (map.Terrain.Count == 0 && map.Statics.Count == 0)
        {
            var path = TileReplacePathBox.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                map = TileReplacementMapSerializer.Load(path);
            }
        }

        return map.Terrain.Count == 0 && map.Statics.Count == 0 ? null : map;
    }

    private TileReplacementMap BuildReplacementMapFromUi()
    {
        var terrain = new Dictionary<ushort, ushort>();
        var statics = new Dictionary<ushort, ushort>();

        foreach (var item in _terrainRemapItems)
        {
            if (TryParseTileId(item.ReplacementText, out var replacement))
            {
                terrain[item.SourceId] = replacement;
            }
        }

        foreach (var item in _staticsRemapItems)
        {
            if (TryParseTileId(item.ReplacementText, out var replacement))
            {
                statics[item.SourceId] = replacement;
            }
        }

        return new TileReplacementMap(terrain, statics);
    }

    private void LoadRemapFromMap(TileReplacementMap map)
    {
        _terrainRemapItems.Clear();
        _staticsRemapItems.Clear();

        foreach (var pair in map.Terrain.OrderBy(pair => pair.Key))
        {
            _terrainRemapItems.Add(new TileRemapItem(pair.Key, 0) { ReplacementText = $"0x{pair.Value:X4}" });
        }

        foreach (var pair in map.Statics.OrderBy(pair => pair.Key))
        {
            _staticsRemapItems.Add(new TileRemapItem(pair.Key, 0) { ReplacementText = $"0x{pair.Value:X4}" });
        }
    }

    private void LoadRemapFromMissing(Dictionary<ushort, int> terrain, Dictionary<ushort, int> statics)
    {
        _terrainRemapItems.Clear();
        _staticsRemapItems.Clear();

        foreach (var pair in terrain.OrderByDescending(pair => pair.Value))
        {
            _terrainRemapItems.Add(new TileRemapItem(pair.Key, pair.Value));
        }

        foreach (var pair in statics.OrderByDescending(pair => pair.Value))
        {
            _staticsRemapItems.Add(new TileRemapItem(pair.Key, pair.Value));
        }
    }

    private static bool TryParseTileId(string? text, out ushort tileId)
    {
        tileId = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ushort.TryParse(trimmed[2..], System.Globalization.NumberStyles.HexNumber, null, out tileId);
        }

        return ushort.TryParse(trimmed, out tileId);
    }

    private static ClientInfo ResolveClientInfo(string sourceFolder, string destFolder)
    {
        var sourceArtIdx = string.Empty;
        var sourceArt = string.Empty;
        var destArtIdx = string.Empty;
        var destArt = string.Empty;

        if (!string.IsNullOrWhiteSpace(sourceFolder) && Directory.Exists(sourceFolder))
        {
            sourceArtIdx = Path.Combine(sourceFolder, "artidx.mul");
            sourceArt = Path.Combine(sourceFolder, "art.mul");
        }

        if (!string.IsNullOrWhiteSpace(destFolder) && Directory.Exists(destFolder))
        {
            destArtIdx = Path.Combine(destFolder, "artidx.mul");
            destArt = Path.Combine(destFolder, "art.mul");
        }

        return new ClientInfo(
            !string.IsNullOrWhiteSpace(sourceArtIdx) && File.Exists(sourceArtIdx) ? sourceArtIdx : null,
            !string.IsNullOrWhiteSpace(sourceArt) && File.Exists(sourceArt) ? sourceArt : null,
            !string.IsNullOrWhiteSpace(destArtIdx) && File.Exists(destArtIdx) ? destArtIdx : null,
            !string.IsNullOrWhiteSpace(destArt) && File.Exists(destArt) ? destArt : null);
    }

    private void ApplyValidationResult(TileValidationResult result, ClientInfo clientInfo)
    {
        _lastValidation = result;
        _missingTerrainEntries.Clear();
        _missingStaticsEntries.Clear();

        foreach (var pair in result.MissingTerrain.OrderByDescending(pair => pair.Value))
        {
            _missingTerrainEntries.Add($"0x{pair.Key:X4} ({pair.Value:N0}) missing");
        }

        foreach (var pair in result.DiffTerrain.OrderByDescending(pair => pair.Value))
        {
            _missingTerrainEntries.Add($"0x{pair.Key:X4} ({pair.Value:N0}) diff");
        }

        foreach (var pair in result.MissingStatics.OrderByDescending(pair => pair.Value))
        {
            _missingStaticsEntries.Add($"0x{pair.Key:X4} ({pair.Value:N0}) missing");
        }

        foreach (var pair in result.DiffStatics.OrderByDescending(pair => pair.Value))
        {
            _missingStaticsEntries.Add($"0x{pair.Key:X4} ({pair.Value:N0}) diff");
        }

        ValidationMissingTerrainText.Text =
            $"Unique: {result.TerrainUniqueCount:N0} | Missing: {result.MissingTerrain.Count:N0} | Diff: {result.DiffTerrain.Count:N0}";
        ValidationMissingStaticsText.Text =
            $"Unique: {result.StaticUniqueCount:N0} | Missing: {result.MissingStatics.Count:N0} | Diff: {result.DiffStatics.Count:N0}";

        var artNotice = result.HasDestArt
            ? (result.HasSourceArt ? "Art compare enabled." : "Art compare limited (source art missing).")
            : "Art compare disabled (destination art missing).";

        ValidationSummaryText.Text =
            $"Terrain IDs: {result.TerrainUniqueCount:N0}, Static IDs: {result.StaticUniqueCount:N0}. {artNotice}";

        AppStatus.AppendLog("Tile validation completed.", AppStatusSeverity.Success);
        AppStatus.AppendLog(
            $"Missing terrain {result.MissingTerrain.Count:N0}, missing statics {result.MissingStatics.Count:N0}, diff terrain {result.DiffTerrain.Count:N0}, diff statics {result.DiffStatics.Count:N0}.",
            AppStatusSeverity.Info);
    }

    private sealed record ClientInfo(
        string? SourceArtIndexPath,
        string? SourceArtPath,
        string? DestArtIndexPath,
        string? DestArtPath);

    private sealed class TileValidationResult
    {
        public TileValidationResult(
            Dictionary<ushort, int> missingTerrain,
            Dictionary<ushort, int> missingStatics,
            Dictionary<ushort, int> diffTerrain,
            Dictionary<ushort, int> diffStatics,
            int terrainUniqueCount,
            int staticUniqueCount,
            bool hasDestArt,
            bool hasSourceArt)
        {
            MissingTerrain = missingTerrain;
            MissingStatics = missingStatics;
            DiffTerrain = diffTerrain;
            DiffStatics = diffStatics;
            TerrainUniqueCount = terrainUniqueCount;
            StaticUniqueCount = staticUniqueCount;
            HasDestArt = hasDestArt;
            HasSourceArt = hasSourceArt;
        }

        public Dictionary<ushort, int> MissingTerrain { get; }

        public Dictionary<ushort, int> MissingStatics { get; }

        public Dictionary<ushort, int> DiffTerrain { get; }

        public Dictionary<ushort, int> DiffStatics { get; }

        public int TerrainUniqueCount { get; }

        public int StaticUniqueCount { get; }

        public bool HasDestArt { get; }

        public bool HasSourceArt { get; }
    }

    private sealed class TileRemapItem : INotifyPropertyChanged
    {
        private string _replacementText = string.Empty;

        public TileRemapItem(ushort sourceId, int count)
        {
            SourceId = sourceId;
            Count = count;
        }

        public ushort SourceId { get; }

        public int Count { get; }

        public string DisplayId => $"0x{SourceId:X4}";

        public string CountText => Count > 0 ? Count.ToString("N0") : string.Empty;

        public string ReplacementText
        {
            get => _replacementText;
            set
            {
                if (value == _replacementText)
                {
                    return;
                }

                _replacementText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ReplacementText)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static string BuildCopyPath(string path, string? outputFolder = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var directory = !string.IsNullOrWhiteSpace(outputFolder)
            ? outputFolder
            : Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        return Path.Combine(directory, $"{name}_copy{extension}");
    }
}
