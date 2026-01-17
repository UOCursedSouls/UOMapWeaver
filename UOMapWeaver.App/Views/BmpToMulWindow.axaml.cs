using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using UOMapWeaver.App;
using UOMapWeaver.Core;
using UOMapWeaver.Core.Bmp;
using UOMapWeaver.Core.Map;
using UOMapWeaver.Core.MapTrans;
using UOMapWeaver.Core.Statics;
using UOMapWeaver.Core.TileColors;
using static UOMapWeaver.App.Views.ViewHelpers;
using FieldState = UOMapWeaver.App.Views.ViewHelpers.FieldState;

namespace UOMapWeaver.App.Views;

public sealed partial class BmpToMulView : UserControl, IAppStateView
{
    private Bitmap? _previewBitmap;
    private readonly List<MapTransOption> _mapTransOptions = new();
    private readonly List<TerrainEncodingOption> _terrainEncodings = new();
    private string _previewWarning = string.Empty;
    private const long LargeMapPixelThreshold = 20_000_000;
    private double _previewZoom = 1;
    private double _previewMinZoom = 0.05;
    private bool _previewManualZoom;
    private bool _previewPanning;
    private Point _previewPanStart;
    private Vector _previewPanOffset;
    private bool _loadingState;

    public BmpToMulView()
    {
        InitializeComponent();
        LoadTerrainEncodings();
        LoadMapTransOptions();
        LoadState();
        UpdateStatus();
        StopOnErrorCheckBox.IsCheckedChanged += (_, _) => SaveState();
        PreviewScroll.SizeChanged += (_, _) =>
        {
            if (!_previewManualZoom)
            {
                FitPreviewToViewport();
            }
        };
    }

    private async void OnBrowseAltitude(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => AltitudeBmpBox.Text = await PickFileAsync(this, "Select Altitude.bmp", new[] { "bmp" });

    private async void OnBrowseTerrain(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => TerrainBmpBox.Text = await PickFileAsync(this, "Select Terrain.bmp", new[] { "bmp" });

    private async void OnBrowseOutput(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => OutputFolderBox.Text = await PickFolderAsync(this, "Select output folder");

    private async void OnBrowseMapTrans(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = await PickFileAsync(this, "Select MapTrans profile", new[] { "txt" });
        if (!string.IsNullOrWhiteSpace(path))
        {
            AddMapTransOption(path);
        }
    }

    private async void OnBrowseTileJson(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => TileJsonPathBox.Text = await PickFileAsync(this, "Select Tile Color JSON", new[] { "json" });

    private async void OnLoadPreview(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = await PickFileAsync(this, "Select BMP", new[] { "bmp" });
        if (!string.IsNullOrWhiteSpace(path))
        {
            await LoadBmpPreviewAsync(path);
        }
    }

    private async void OnGenerate(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SetBusy(true);

        using var cancelSource = new CancellationTokenSource();
        AppStatus.SetCancelSource(cancelSource);

        var encoding = GetTerrainEncoding();
        var useTileJson = encoding == TerrainEncoding.TileJson;
        var useMapTrans = encoding == TerrainEncoding.MapTrans;
        var useTileIndex = encoding == TerrainEncoding.TileIndexRgb;
        var selected = GetSelectedMapTransPath();
        if (useMapTrans && string.IsNullOrWhiteSpace(selected))
        {
            StatusText.Text = "Select a MapTrans profile.";
            AppStatus.SetError(StatusText.Text);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(AltitudeBmpBox.Text) || string.IsNullOrWhiteSpace(TerrainBmpBox.Text))
        {
            StatusText.Text = "Altitude.bmp and Terrain.bmp are required.";
            StatusTextPreview.Text = StatusText.Text;
            AppStatus.SetError(StatusText.Text);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputFolderBox.Text))
        {
            StatusText.Text = "Output folder is required.";
            StatusTextPreview.Text = StatusText.Text;
            AppStatus.SetError(StatusText.Text);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
            return;
        }

        try
        {
            var altitudeBmpPath = AltitudeBmpBox.Text!.Trim();
            var terrainBmpPath = TerrainBmpBox.Text!.Trim();
            var outputFolder = OutputFolderBox.Text!.Trim();
            var mapPath = Path.Combine(outputFolder, "map.mul");
            var staIdxPath = Path.Combine(outputFolder, "staidx.mul");
            var staticsPath = Path.Combine(outputFolder, "statics.mul");

            if (GenerateStaticsCheckBox.IsChecked == true)
            {
                if (!await ConfirmOverwriteAsync("Overwrite MUL files?", mapPath, staIdxPath, staticsPath))
                {
                    StatusText.Text = "Conversion cancelled.";
                    StatusTextPreview.Text = StatusText.Text;
                    AppStatus.SetWarning(StatusText.Text);
                    AppStatus.AppendLog(StatusText.Text, AppStatusSeverity.Warning);
                    AppStatus.SetCancelSource(null);
                    return;
                }
            }
            else
            {
                if (!await ConfirmOverwriteAsync("Overwrite MUL files?", mapPath))
                {
                    StatusText.Text = "Conversion cancelled.";
                    StatusTextPreview.Text = StatusText.Text;
                    AppStatus.SetWarning(StatusText.Text);
                    AppStatus.AppendLog(StatusText.Text, AppStatusSeverity.Warning);
                    AppStatus.SetCancelSource(null);
                    return;
                }
            }

            TileColorMap? tileMap = null;
            MapTransProfile? profile = null;
            Bmp8Image altitude;
            Bmp8Image? terrain = null;
            Bmp24Image? terrain24 = null;
            LandTile[]? tiles = null;
            MapConversionReport? report = null;

            if (useTileJson)
            {
                var tileJsonPath = TileJsonPathBox.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(tileJsonPath) || !File.Exists(tileJsonPath))
                {
                    StatusText.Text = "Tile Color JSON not found.";
                    StatusTextPreview.Text = StatusText.Text;
                    AppStatus.SetError(StatusText.Text);
                    AppStatus.SetCancelSource(null);
                    return;
                }

                if (new FileInfo(tileJsonPath).Length == 0)
                {
                    StatusText.Text = "Tile Color JSON is empty.";
                    StatusTextPreview.Text = StatusText.Text;
                    AppStatus.SetError(StatusText.Text);
                    AppStatus.SetCancelSource(null);
                    return;
                }

                tileMap = await Task.Run(() => TileColorMapSerializer.Load(tileJsonPath));
                TileJsonModeText.Text = $"Mode: {tileMap.Mode}";
                profile = new MapTransProfile("TileJson", Array.Empty<MapTransEntry>(), null);
            }
            else if (useMapTrans)
            {
                profile = await Task.Run(() => MapTransParser.LoadFromFile(selected!));
            }

            if (!BmpCodec.TryReadInfo(altitudeBmpPath, out var altitudeWidth, out var altitudeHeight, out var altitudeBits) ||
                altitudeBits != 8)
            {
                StatusText.Text = "Altitude.bmp must be 8-bit.";
                StatusTextPreview.Text = StatusText.Text;
                AppStatus.SetError(StatusText.Text);
                AppStatus.SetCancelSource(null);
                return;
            }

            if (!BmpCodec.TryReadInfo(terrainBmpPath, out var terrainWidth, out var terrainHeight, out var terrainBits))
            {
                StatusText.Text = "Terrain.bmp not detected.";
                StatusTextPreview.Text = StatusText.Text;
                AppStatus.SetError(StatusText.Text);
                AppStatus.SetCancelSource(null);
                return;
            }

            if (useTileIndex && terrainBits != 24)
            {
                StatusText.Text = "Terrain.bmp must be 24-bit for TileIndex RGB encoding.";
                StatusTextPreview.Text = StatusText.Text;
                AppStatus.SetError(StatusText.Text);
                AppStatus.SetCancelSource(null);
                return;
            }

            if (useMapTrans && terrainBits != 8)
            {
                StatusText.Text = "Terrain.bmp must be 8-bit for MapTrans encoding.";
                StatusTextPreview.Text = StatusText.Text;
                AppStatus.SetError(StatusText.Text);
                AppStatus.SetCancelSource(null);
                return;
            }

            if (useTileJson && tileMap != null)
            {
                if (tileMap.Mode == TileColorMode.Rgb24 && terrainBits != 24)
                {
                    StatusText.Text = "Terrain.bmp must be 24-bit for RGB JSON mode.";
                    StatusTextPreview.Text = StatusText.Text;
                    AppStatus.SetError(StatusText.Text);
                    AppStatus.SetCancelSource(null);
                    return;
                }

                if (tileMap.Mode == TileColorMode.Indexed8 && terrainBits != 8)
                {
                    StatusText.Text = "Terrain.bmp must be 8-bit for Indexed JSON mode.";
                    StatusTextPreview.Text = StatusText.Text;
                    AppStatus.SetError(StatusText.Text);
                    AppStatus.SetCancelSource(null);
                    return;
                }
            }

            if (terrainWidth != altitudeWidth || terrainHeight != altitudeHeight)
            {
                StatusText.Text = "Altitude.bmp and Terrain.bmp must match in size.";
                StatusTextPreview.Text = StatusText.Text;
                AppStatus.SetError(StatusText.Text);
                AppStatus.AppendLog(StatusText.Text, AppStatusSeverity.Error);
                AppStatus.SetCancelSource(null);
                return;
            }

            if (terrainWidth % MapMul.BlockSize != 0 || terrainHeight % MapMul.BlockSize != 0)
            {
                StatusText.Text = "Map size must be divisible by 8.";
                StatusTextPreview.Text = StatusText.Text;
                AppStatus.SetError(StatusText.Text);
                AppStatus.AppendLog(StatusText.Text, AppStatusSeverity.Error);
                AppStatus.SetCancelSource(null);
                return;
            }

            var useStreaming = UseStreaming(terrainWidth, terrainHeight);

            if (useStreaming)
            {
                AppStatus.AppendLog("BMP->MUL start.", AppStatusSeverity.Info);
                AppStatus.AppendLog($"Terrain.bmp: {TerrainBmpBox.Text}", AppStatusSeverity.Info);
                AppStatus.AppendLog($"Altitude.bmp: {AltitudeBmpBox.Text}", AppStatusSeverity.Info);
                AppStatus.AppendLog($"Terrain.bmp size: {FormatFileSize(TerrainBmpBox.Text)}", AppStatusSeverity.Info);
                AppStatus.AppendLog($"Altitude.bmp size: {FormatFileSize(AltitudeBmpBox.Text)}", AppStatusSeverity.Info);
                AppStatus.AppendLog($"Terrain size: {terrainWidth}x{terrainHeight}", AppStatusSeverity.Info);
                AppStatus.AppendLog($"Encoding: {encoding}", AppStatusSeverity.Info);
                if (useTileJson)
                {
                    AppStatus.AppendLog($"Tile JSON: {TileJsonPathBox.Text}", AppStatusSeverity.Info);
                }
                else if (useMapTrans)
                {
                    AppStatus.AppendLog($"MapTrans: {selected}", AppStatusSeverity.Info);
                }
                AppStatus.AppendLog($"Output folder: {outputFolder}", AppStatusSeverity.Info);
                AppStatus.AppendLog($"Generate statics: {GenerateStaticsCheckBox.IsChecked == true}", AppStatusSeverity.Info);

                var streamProgress = CreateAppProgress();

                var streamOptions = new MapConversionOptions
                {
                    StopOnError = StopOnErrorCheckBox.IsChecked == true,
                    TileColorMap = tileMap,
                    CancellationToken = cancelSource.Token
                };

                MapConversionReport streamReport;
                if (useTileIndex)
                {
                    streamReport = await Task.Run(() => MapConversion.ConvertTileIndexBmpToMulFromFiles(
                        terrainBmpPath,
                        altitudeBmpPath,
                        mapPath,
                        streamProgress,
                        entry => AppStatus.AppendLog(entry.Message, MapLogToStatus(entry.Level)),
                        streamOptions
                    ), cancelSource.Token);
                }
                else if (tileMap?.Mode == TileColorMode.Rgb24)
                {
                    streamReport = await Task.Run(() => MapConversion.ConvertBmp24ToMulFromFiles(
                        terrainBmpPath,
                        altitudeBmpPath,
                        tileMap,
                        mapPath,
                        streamProgress,
                        entry => AppStatus.AppendLog(entry.Message, MapLogToStatus(entry.Level)),
                        streamOptions
                    ), cancelSource.Token);
                }
                else
                {
                    streamReport = await Task.Run(() => MapConversion.ConvertBmpToMulFromFiles(
                        terrainBmpPath,
                        altitudeBmpPath,
                        profile!,
                        mapPath,
                        streamProgress,
                        entry => AppStatus.AppendLog(entry.Message, MapLogToStatus(entry.Level)),
                        streamOptions
                    ), cancelSource.Token);
                }

                if (GenerateStaticsCheckBox.IsChecked == true)
                {
                    if (EmptyStaticsCheckBox.IsChecked == true)
                    {
                        await Task.Run(() => StaticMulCodec.WriteEmptyStatics(staIdxPath, staticsPath, terrainWidth, terrainHeight), cancelSource.Token);
                        AppStatus.AppendLog("Generated empty statics.", AppStatusSeverity.Info);
                    }
                    else
                    {
                        await Task.Run(() => StaticMulCodec.WriteEmptyStatics(staIdxPath, staticsPath, terrainWidth, terrainHeight), cancelSource.Token);
                        AppStatus.AppendLog("Large map: generated empty statics (populated statics require full tile data).",
                            AppStatusSeverity.Warning);
                    }
                }

                var streamReportSummary = streamReport.MissingTerrainTiles == 0
                    ? "No missing tiles."
                    : $"Missing tiles: {streamReport.MissingTerrainTiles:N0}.";

                StatusText.Text = $"Generated {Path.GetFileName(mapPath)}. {streamReportSummary}";
                StatusTextPreview.Text = StatusText.Text;
                AppStatus.SetSuccess(StatusText.Text);
                AppStatus.AppendLog($"BMP->MUL completed: {mapPath}. {streamReport}", AppStatusSeverity.Success);
                AppStatus.AppendLog(streamReport.FormatTopMissingTiles(),
                    streamReport.MissingTerrainTiles > 0 ? AppStatusSeverity.Warning : AppStatusSeverity.Info);
                return;
            }

            altitude = await Task.Run(() => Bmp8Codec.Read(altitudeBmpPath));

            if (useTileIndex)
            {
                terrain24 = await Task.Run(() => Bmp24Codec.Read(terrainBmpPath));
            }
            else if (tileMap?.Mode == TileColorMode.Rgb24)
            {
                terrain24 = await Task.Run(() => Bmp24Codec.Read(terrainBmpPath));
            }
            else
            {
                terrain = await Task.Run(() => Bmp8Codec.Read(terrainBmpPath));
            }

            var terrainWidthValue = terrain?.Width ?? terrain24?.Width ?? 0;
            var terrainHeightValue = terrain?.Height ?? terrain24?.Height ?? 0;
            if (terrainWidthValue != altitude.Width || terrainHeightValue != altitude.Height)
            {
                StatusText.Text = "Altitude.bmp and Terrain.bmp must match in size.";
                StatusTextPreview.Text = StatusText.Text;
                AppStatus.SetError(StatusText.Text);
                AppStatus.AppendLog(StatusText.Text, AppStatusSeverity.Error);
                AppStatus.SetCancelSource(null);
                return;
            }

            if (terrainWidthValue % MapMul.BlockSize != 0 || terrainHeightValue % MapMul.BlockSize != 0)
            {
                StatusText.Text = "Map size must be divisible by 8.";
                StatusTextPreview.Text = StatusText.Text;
                AppStatus.SetError(StatusText.Text);
                AppStatus.AppendLog(StatusText.Text, AppStatusSeverity.Error);
                AppStatus.SetCancelSource(null);
                return;
            }

            AppStatus.AppendLog("BMP->MUL start.", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Terrain.bmp: {terrainBmpPath}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Altitude.bmp: {altitudeBmpPath}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Terrain.bmp size: {FormatFileSize(terrainBmpPath)}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Altitude.bmp size: {FormatFileSize(altitudeBmpPath)}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Terrain size: {terrainWidthValue}x{terrainHeightValue}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Encoding: {encoding}", AppStatusSeverity.Info);
            if (useTileJson)
            {
                AppStatus.AppendLog($"Tile JSON: {TileJsonPathBox.Text}", AppStatusSeverity.Info);
            }
            else if (useMapTrans)
            {
                AppStatus.AppendLog($"MapTrans: {selected}", AppStatusSeverity.Info);
            }
            AppStatus.AppendLog($"Output folder: {outputFolder}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Generate statics: {GenerateStaticsCheckBox.IsChecked == true}", AppStatusSeverity.Info);

            var progress = CreateAppProgress();

            var options = new MapConversionOptions
            {
                StopOnError = StopOnErrorCheckBox.IsChecked == true,
                TileColorMap = tileMap,
                CancellationToken = cancelSource.Token
            };

            if (useTileIndex)
            {
                var tileResult = await Task.Run(() => MapConversion.ConvertTileIndexBmpToMul(
                    terrain24!,
                    altitude,
                    progress,
                    entry => AppStatus.AppendLog(entry.Message, MapLogToStatus(entry.Level)),
                    options
                ), cancelSource.Token);

                tiles = tileResult.tiles;
                report = tileResult.report;
            }

            if (tileMap?.Mode == TileColorMode.Rgb24)
            {
                var rgbResult = await Task.Run(() => MapConversion.ConvertBmp24ToMul(
                    terrain24!,
                    altitude,
                    tileMap,
                    progress,
                    entry => AppStatus.AppendLog(entry.Message, MapLogToStatus(entry.Level)),
                    options
                ), cancelSource.Token);

                tiles = rgbResult.tiles;
                report = rgbResult.report;
            }

            if (tiles is null)
            {
                var result = await Task.Run(() => MapConversion.ConvertBmpToMul(
                    terrain!,
                    altitude,
                    profile!,
                    progress,
                    entry => AppStatus.AppendLog(entry.Message, MapLogToStatus(entry.Level)),
                    options
                ), cancelSource.Token);

                tiles = result.tiles;
                report = result.report;
            }

            await Task.Run(() => MapMulCodec.WriteLandTiles(mapPath, terrainWidthValue, terrainHeightValue, tiles!), cancelSource.Token);

            if (!await TryWriteStaticsAsync(staIdxPath, staticsPath, terrainWidthValue, terrainHeightValue, tiles!, cancelSource.Token))
            {
                return;
            }

            var reportSummary = report!.MissingTerrainTiles == 0
                ? "No missing tiles."
                : $"Missing tiles: {report!.MissingTerrainTiles:N0}.";

            StatusText.Text = $"Generated {Path.GetFileName(mapPath)}. {reportSummary}";
            StatusTextPreview.Text = StatusText.Text;
            AppStatus.SetSuccess(StatusText.Text);
            AppStatus.AppendLog($"BMP->MUL completed: {mapPath}. {report}", AppStatusSeverity.Success);
            AppStatus.AppendLog(report.FormatTopMissingTiles(),
                report.MissingTerrainTiles > 0 ? AppStatusSeverity.Warning : AppStatusSeverity.Info);
        }
        catch (MapConversionAbortException ex)
        {
            StatusText.Text = $"Stopped: {ex.Message}";
            StatusTextPreview.Text = StatusText.Text;
            AppStatus.SetError(StatusText.Text);
            AppStatus.AppendLog(StatusText.Text, AppStatusSeverity.Error);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Operation cancelled.";
            StatusTextPreview.Text = StatusText.Text;
            AppStatus.SetWarning(StatusText.Text);
            AppStatus.AppendLog(StatusText.Text, AppStatusSeverity.Warning);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            StatusTextPreview.Text = StatusText.Text;
            AppStatus.SetError(StatusText.Text);
            AppStatus.AppendLog($"BMP->MUL error: {ex.Message}", AppStatusSeverity.Error);
        }
        finally
        {
            AppStatus.SetProgress(0, false);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
        }
    }

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateStatus();
        SaveState();
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateStatus();
        SaveState();
    }

    private void OnCheckChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        UpdateStatus();
        SaveState();
    }

    private void SetBusy(bool isBusy)
    {
        if (GenerateButton != null)
        {
            GenerateButton.IsEnabled = !isBusy;
        }

        if (LoadPreviewButton != null)
        {
            LoadPreviewButton.IsEnabled = !isBusy;
        }
    }

    private void ShowLogOverlay()
    {
        if (VisualRoot is MainWindow main)
        {
            main.ShowLogOverlay();
        }
    }

    private async Task<bool> TryWriteStaticsAsync(
        string staIdxPath,
        string staticsPath,
        int width,
        int height,
        LandTile[] tiles,
        CancellationToken token)
    {
        if (GenerateStaticsCheckBox.IsChecked != true)
        {
            return true;
        }

        if (EmptyStaticsCheckBox.IsChecked == true)
        {
            await Task.Run(() => StaticMulCodec.WriteEmptyStatics(staIdxPath, staticsPath, width, height), token);
            AppStatus.AppendLog("Generated empty statics.", AppStatusSeverity.Info);
            return true;
        }

        AppStatus.AppendLog("Generating statics from data definitions...", AppStatusSeverity.Info);

        var progress = CreateAppProgress();

        try
        {
            var placements = await Task.Run(() => StaticPlacementGenerator.Generate(
                tiles,
                width,
                height,
                new StaticPlacementOptions(),
                progress,
                entry => AppStatus.AppendLog(entry.Message, MapLogToStatus(entry.Level)),
                token), token);

            await Task.Run(() => StaticMulCodec.WriteStatics(staIdxPath, staticsPath, width, height, placements), token);
            AppStatus.AppendLog("Generated populated statics.", AppStatusSeverity.Success);
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Static generation error: {ex.Message}";
            StatusTextPreview.Text = StatusText.Text;
            AppStatus.SetError(StatusText.Text);
            AppStatus.AppendLog(StatusText.Text, AppStatusSeverity.Error);
            return false;
        }
    }

    private async Task<bool> ConfirmOverwriteAsync(string title, params string[] paths)
    {
        var existing = new List<string>();
        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                existing.Add(path);
            }
        }

        if (existing.Count == 0)
        {
            return true;
        }

        var message = "The following files already exist:\n" +
                      string.Join('\n', existing.Select(Path.GetFileName)) +
                      "\n\nOverwrite them?";

        var dialog = BuildConfirmDialog(title, message);
        return await dialog.ShowDialog<bool>(GetOwnerWindow());
    }

    private static Window BuildConfirmDialog(string title, string message)
    {
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
            TextWrapping = TextWrapping.Wrap
        };

        var overwriteButton = new Button { Content = "Overwrite", MinWidth = 90 };
        var cancelButton = new Button { Content = "Cancel", MinWidth = 90 };

        overwriteButton.Click += (_, _) => dialog.Close(true);
        cancelButton.Click += (_, _) => dialog.Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(overwriteButton);
        buttons.Children.Add(cancelButton);

        var layout = new StackPanel { Spacing = 12, Margin = new Thickness(16) };
        layout.Children.Add(text);
        layout.Children.Add(buttons);

        dialog.Content = layout;
        return dialog;
    }

    private async Task LoadBmpPreviewAsync(string path)
    {
        if (!File.Exists(path))
        {
            StatusText.Text = "File not found.";
            return;
        }

        _previewBitmap?.Dispose();
        await using var stream = File.OpenRead(path);
        _previewBitmap = new Bitmap(stream);
        PreviewImage.Source = _previewBitmap;
        ResetPreviewZoom();

        var size = _previewBitmap.PixelSize;
        PreviewInfoText.Text = $"{Path.GetFileName(path)} - {size.Width}x{size.Height}";

        if (size.Width % 8 != 0 || size.Height % 8 != 0)
        {
            _previewWarning = "Warning: image size is not divisible by 8.";
        }
        else
        {
            _previewWarning = string.Empty;
        }

        UpdateStatus();
    }

    private void LoadState()
    {
        _loadingState = true;

        AltitudeBmpBox.Text = AppSettings.GetString("BmpToMul.AltitudeBmp", AltitudeBmpBox.Text ?? string.Empty);
        TerrainBmpBox.Text = AppSettings.GetString("BmpToMul.TerrainBmp", TerrainBmpBox.Text ?? string.Empty);
        OutputFolderBox.Text = AppSettings.GetString("BmpToMul.OutputFolder", OutputFolderBox.Text ?? string.Empty);
        TileJsonPathBox.Text = AppSettings.GetString("BmpToMul.TileJsonPath", TileJsonPathBox.Text ?? string.Empty);

        GenerateStaticsCheckBox.IsChecked = AppSettings.GetBool("BmpToMul.GenerateStatics", GenerateStaticsCheckBox.IsChecked == true);
        EmptyStaticsCheckBox.IsChecked = AppSettings.GetBool("BmpToMul.EmptyStatics", EmptyStaticsCheckBox.IsChecked == true);
        StopOnErrorCheckBox.IsChecked = AppSettings.GetBool("BmpToMul.StopOnError", StopOnErrorCheckBox.IsChecked == true);

        var encodingText = AppSettings.GetString("BmpToMul.TerrainEncoding", string.Empty);
        if (Enum.TryParse<TerrainEncoding>(encodingText, out var encoding))
        {
            var match = _terrainEncodings.FirstOrDefault(option => option.Encoding == encoding);
            if (match != null)
            {
                TerrainEncodingComboBox.SelectedItem = match;
            }
        }

        var mapTransPath = AppSettings.GetString("BmpToMul.MapTransPath", string.Empty);
        if (!string.IsNullOrWhiteSpace(mapTransPath))
        {
            AddMapTransOption(mapTransPath);
        }

        _loadingState = false;
    }

    private void SaveState()
    {
        if (_loadingState || !AppSettings.SaveEnabled)
        {
            return;
        }

        AppSettings.SetString("BmpToMul.AltitudeBmp", AltitudeBmpBox.Text ?? string.Empty);
        AppSettings.SetString("BmpToMul.TerrainBmp", TerrainBmpBox.Text ?? string.Empty);
        AppSettings.SetString("BmpToMul.OutputFolder", OutputFolderBox.Text ?? string.Empty);
        AppSettings.SetString("BmpToMul.TileJsonPath", TileJsonPathBox.Text ?? string.Empty);
        AppSettings.SetBool("BmpToMul.GenerateStatics", GenerateStaticsCheckBox.IsChecked == true);
        AppSettings.SetBool("BmpToMul.EmptyStatics", EmptyStaticsCheckBox.IsChecked == true);
        AppSettings.SetBool("BmpToMul.StopOnError", StopOnErrorCheckBox.IsChecked == true);

        var encoding = GetTerrainEncoding();
        AppSettings.SetString("BmpToMul.TerrainEncoding", encoding.ToString());

        var mapTransPath = GetSelectedMapTransPath() ?? string.Empty;
        AppSettings.SetString("BmpToMul.MapTransPath", mapTransPath);
    }

    public void PersistState()
    {
        SaveState();
    }

    private void OnPreviewWheel(object? sender, PointerWheelEventArgs e)
    {
        if (_previewBitmap is null)
        {
            return;
        }

        var delta = e.Delta.Y;
        if (Math.Abs(delta) < 0.01)
        {
            return;
        }

        _previewManualZoom = true;
        var factor = delta > 0 ? 1.1 : 0.9;
        _previewZoom = Math.Clamp(_previewZoom * factor, _previewMinZoom, 20);
        ApplyPreviewZoom();
        e.Handled = true;
    }

    private void OnPreviewPanPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_previewBitmap is null)
        {
            return;
        }

        var point = e.GetCurrentPoint(PreviewScroll);
        if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsRightButtonPressed)
        {
            return;
        }

        _previewPanning = true;
        _previewPanStart = e.GetPosition(PreviewScroll);
        _previewPanOffset = PreviewScroll.Offset;
        e.Pointer.Capture(PreviewScroll);
        e.Handled = true;
    }

    private void OnPreviewPanMoved(object? sender, PointerEventArgs e)
    {
        if (!_previewPanning)
        {
            return;
        }

        var current = e.GetPosition(PreviewScroll);
        var delta = current - _previewPanStart;
        PreviewScroll.Offset = _previewPanOffset - new Vector(delta.X, delta.Y);
        e.Handled = true;
    }

    private void OnPreviewPanReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_previewPanning)
        {
            return;
        }

        _previewPanning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void ResetPreviewZoom()
    {
        _previewManualZoom = false;
        FitPreviewToViewport();
    }

    private void FitPreviewToViewport()
    {
        if (_previewBitmap is null)
        {
            return;
        }

        var viewport = PreviewScroll.Bounds;
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            return;
        }

        var bmpWidth = _previewBitmap.PixelSize.Width;
        var bmpHeight = _previewBitmap.PixelSize.Height;
        if (bmpWidth <= 0 || bmpHeight <= 0)
        {
            return;
        }

        var scale = Math.Min(viewport.Width / bmpWidth, viewport.Height / bmpHeight);
        if (double.IsInfinity(scale) || scale <= 0)
        {
            scale = 1;
        }

        _previewMinZoom = Math.Clamp(scale, 0.05, 20);
        _previewZoom = _previewManualZoom ? Math.Max(_previewZoom, _previewMinZoom) : _previewMinZoom;
        ApplyPreviewZoom();
    }

    private void ApplyPreviewZoom()
    {
        if (_previewBitmap is null)
        {
            return;
        }

        var bmpWidth = _previewBitmap.PixelSize.Width;
        var bmpHeight = _previewBitmap.PixelSize.Height;
        PreviewImage.Width = bmpWidth * _previewZoom;
        PreviewImage.Height = bmpHeight * _previewZoom;
    }

    private Window? GetHostWindow() => VisualRoot as Window;

    private Window GetOwnerWindow()
        => GetHostWindow() ?? throw new InvalidOperationException("Host window not available.");

    private void LoadTerrainEncodings()
    {
        _terrainEncodings.Clear();
        _terrainEncodings.Add(new TerrainEncodingOption(TerrainEncoding.MapTrans, "MapTrans (8-bit palette)"));
        _terrainEncodings.Add(new TerrainEncodingOption(TerrainEncoding.TileJson, "Tile JSON (8/24-bit)"));
        _terrainEncodings.Add(new TerrainEncodingOption(TerrainEncoding.TileIndexRgb, "TileIndex RGB (24-bit)"));

        if (TerrainEncodingComboBox != null)
        {
            TerrainEncodingComboBox.ItemsSource = _terrainEncodings;
            TerrainEncodingComboBox.SelectedIndex = 0;
        }
    }

    private TerrainEncoding GetTerrainEncoding()
    {
        return TerrainEncodingComboBox?.SelectedItem is TerrainEncodingOption option
            ? option.Encoding
            : TerrainEncoding.MapTrans;
    }

    private void LoadMapTransOptions()
    {
        _mapTransOptions.Clear();
        var roots = FindMapTransRoots().ToList();
        MapTransRootsText.Text = roots.Count == 0
            ? "No MapTrans folders found."
            : string.Join(Environment.NewLine, roots);

        foreach (var file in MapTransCatalog.FindMapTransFiles(roots))
        {
            AddMapTransOption(file, select: false);
        }

        if (MapTransComboBox != null)
        {
            MapTransComboBox.ItemsSource = _mapTransOptions;
            if (_mapTransOptions.Count > 0)
            {
                MapTransComboBox.SelectedIndex = 0;
            }
        }
    }

    private void AddMapTransOption(string path, bool select = true)
    {
        if (_mapTransOptions.Exists(option => option.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            if (select)
            {
                MapTransComboBox.SelectedItem = _mapTransOptions.Find(option =>
                    option.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            }
            return;
        }

        var parentName = new DirectoryInfo(Path.GetDirectoryName(path) ?? string.Empty).Name;
        var name = string.IsNullOrWhiteSpace(parentName)
            ? Path.GetFileName(path)
            : $"{parentName}/{Path.GetFileName(path)}";
        _mapTransOptions.Add(new MapTransOption(name, path));

        MapTransComboBox.ItemsSource = _mapTransOptions;
        if (select)
        {
            MapTransComboBox.SelectedIndex = _mapTransOptions.Count - 1;
        }
    }

    private string? GetSelectedMapTransPath()
    {
        return MapTransComboBox.SelectedItem is MapTransOption option ? option.Path : null;
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

    private enum TerrainEncoding
    {
        MapTrans,
        TileJson,
        TileIndexRgb
    }

    private sealed class TerrainEncodingOption
    {
        public TerrainEncodingOption(TerrainEncoding encoding, string name)
        {
            Encoding = encoding;
            Name = name;
        }

        public TerrainEncoding Encoding { get; }

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

    private void UpdateStatus()
    {
        var warnings = new List<string>();
        BmpSizeDetailsText.Text = string.Empty;

        var altitudeExists = !string.IsNullOrWhiteSpace(AltitudeBmpBox.Text) && File.Exists(AltitudeBmpBox.Text);
        if (string.IsNullOrWhiteSpace(AltitudeBmpBox.Text))
        {
            warnings.Add("Altitude.bmp is required.");
        }
        else if (!File.Exists(AltitudeBmpBox.Text))
        {
            warnings.Add("Altitude.bmp not found.");
        }

        var terrainExists = !string.IsNullOrWhiteSpace(TerrainBmpBox.Text) && File.Exists(TerrainBmpBox.Text);
        if (string.IsNullOrWhiteSpace(TerrainBmpBox.Text))
        {
            warnings.Add("Terrain.bmp is required.");
        }
        else if (!File.Exists(TerrainBmpBox.Text))
        {
            warnings.Add("Terrain.bmp not found.");
        }

        SetFieldState(AltitudeBmpBox, altitudeExists ? FieldState.Valid : FieldState.Error);
        SetFieldState(TerrainBmpBox, terrainExists ? FieldState.Valid : FieldState.Error);

        if (string.IsNullOrWhiteSpace(OutputFolderBox.Text))
        {
            warnings.Add("Output folder is required.");
        }
        else if (!Directory.Exists(OutputFolderBox.Text))
        {
            warnings.Add("Output folder not found.");
        }

        SetFieldState(OutputFolderBox, Directory.Exists(OutputFolderBox.Text ?? string.Empty) ? FieldState.Valid : FieldState.Error);

        var generateStatics = GenerateStaticsCheckBox.IsChecked == true;
        EmptyStaticsCheckBox.IsVisible = generateStatics;
        EmptyStaticsCheckBox.IsEnabled = generateStatics;

        if (generateStatics && EmptyStaticsCheckBox.IsChecked != true)
        {
            var terrainPath = Path.Combine(UOMapWeaverDataPaths.SystemRoot, "Terrain.xml");
            var staticsAvailable = Directory.Exists(UOMapWeaverDataPaths.StaticsRoot) &&
                                   Directory.EnumerateFiles(UOMapWeaverDataPaths.StaticsRoot, "*.xml", SearchOption.TopDirectoryOnly).Any();
            if (!File.Exists(terrainPath) || !staticsAvailable)
            {
                warnings.Add("Static data missing; will generate empty statics.");
            }
        }

        var encoding = GetTerrainEncoding();
        var useTileJson = encoding == TerrainEncoding.TileJson;
        var useMapTrans = encoding == TerrainEncoding.MapTrans;
        var useTileIndex = encoding == TerrainEncoding.TileIndexRgb;
        var mapTransSelected = GetSelectedMapTransPath() is not null;
        if (useMapTrans && !mapTransSelected)
        {
            warnings.Add("Select a MapTrans profile.");
        }

        SetFieldState(MapTransComboBox, useMapTrans
            ? mapTransSelected ? FieldState.Valid : FieldState.Warning
            : FieldState.Neutral);

        MapTransComboBox.IsEnabled = useMapTrans;
        if (MapTransBrowseButton != null)
        {
            MapTransBrowseButton.IsEnabled = useMapTrans;
        }

        if (useTileJson)
        {
            if (string.IsNullOrWhiteSpace(TileJsonPathBox.Text))
            {
                warnings.Add("Tile Color JSON is required.");
                TileJsonModeText.Text = string.Empty;
                SetFieldState(TileJsonPathBox, FieldState.Warning);
            }
            else if (!File.Exists(TileJsonPathBox.Text))
            {
                warnings.Add("Tile Color JSON not found.");
                TileJsonModeText.Text = "Not found.";
                SetFieldState(TileJsonPathBox, FieldState.Error);
            }
            else
            {
                if (TileColorMapSerializer.TryReadMode(TileJsonPathBox.Text, out var mode))
                {
                    TileJsonModeText.Text = $"Mode: {mode} | {TileJsonPathBox.Text}";
                }
                else
                {
                    TileJsonModeText.Text = $"Invalid JSON | {TileJsonPathBox.Text}";
                }

                SetFieldState(TileJsonPathBox, FieldState.Valid);
            }
        }
        else
        {
            TileJsonModeText.Text = useTileIndex ? "TileIndex RGB (24-bit)" : string.Empty;
            SetFieldState(TileJsonPathBox, FieldState.Neutral, isOptional: true);
        }

        TileJsonPathBox.IsEnabled = useTileJson;

        var sizeWarning = false;
        if (!string.IsNullOrWhiteSpace(AltitudeBmpBox.Text) &&
            !string.IsNullOrWhiteSpace(TerrainBmpBox.Text) &&
            File.Exists(AltitudeBmpBox.Text) &&
            File.Exists(TerrainBmpBox.Text))
        {
            var sizeMatch = TryGetBmpSize(AltitudeBmpBox.Text, out var altitudeWidth, out var altitudeHeight) &&
                            TryGetBmpSize(TerrainBmpBox.Text, out var terrainWidth, out var terrainHeight) &&
                            altitudeWidth == terrainWidth &&
                            altitudeHeight == terrainHeight;

            if (!sizeMatch)
            {
                warnings.Add("Altitude.bmp and Terrain.bmp must match in size.");
                sizeWarning = true;
            }
            else if (altitudeWidth % MapMul.BlockSize != 0 || altitudeHeight % MapMul.BlockSize != 0)
            {
                warnings.Add("Map size must be divisible by 8.");
                sizeWarning = true;
            }
            else
            {
                BmpSizeDetailsText.Text = $"Size: {altitudeWidth}x{altitudeHeight} | Blocks: {altitudeWidth / MapMul.BlockSize}x{altitudeHeight / MapMul.BlockSize} | Max: {altitudeWidth - 1},{altitudeHeight - 1}";
            }

            if (BmpCodec.TryReadInfo(TerrainBmpBox.Text, out _, out _, out var bits))
            {
                if (useTileIndex && bits != 24)
                {
                    warnings.Add("Terrain.bmp must be 24-bit for TileIndex RGB encoding.");
                    sizeWarning = true;
                }
                else if (useMapTrans && bits != 8)
                {
                    warnings.Add("Terrain.bmp must be 8-bit for MapTrans encoding.");
                    sizeWarning = true;
                }
                else if (useTileJson && !string.IsNullOrWhiteSpace(TileJsonPathBox.Text) &&
                         File.Exists(TileJsonPathBox.Text) &&
                         TileColorMapSerializer.TryReadMode(TileJsonPathBox.Text, out var mode))
                {
                    if (mode == TileColorMode.Rgb24 && bits != 24)
                    {
                        warnings.Add("Terrain.bmp must be 24-bit for RGB JSON mode.");
                        sizeWarning = true;
                    }
                    else if (mode == TileColorMode.Indexed8 && bits != 8)
                    {
                        warnings.Add("Terrain.bmp must be 8-bit for Indexed JSON mode.");
                        sizeWarning = true;
                    }
                }
            }
        }

        if (sizeWarning)
        {
            SetFieldState(AltitudeBmpBox, FieldState.Warning);
            SetFieldState(TerrainBmpBox, FieldState.Warning);
        }

        if (!string.IsNullOrWhiteSpace(_previewWarning))
        {
            warnings.Add(_previewWarning);
        }

        var message = warnings.Count == 0
            ? "Ready."
            : string.Join(" ", warnings);

        StatusText.Text = AppStatus.Stamp(message);
        StatusTextPreview.Text = AppStatus.Stamp(message);

        if (warnings.Count == 0)
        {
            AppStatus.SetInfo(message);
        }
        else if (warnings.Exists(w => w.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            AppStatus.SetError(message);
        }
        else
        {
            AppStatus.SetWarning(message);
        }
    }

    private static bool TryGetBmpSize(string path, out int width, out int height)
    {
        width = 0;
        height = 0;

        try
        {
            if (BmpCodec.TryReadInfo(path, out var bmpWidth, out var bmpHeight, out _))
            {
                width = bmpWidth;
                height = bmpHeight;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool UseStreaming(int width, int height)
        => (long)width * height >= LargeMapPixelThreshold;
}

