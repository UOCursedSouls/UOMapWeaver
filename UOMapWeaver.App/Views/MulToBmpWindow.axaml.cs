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
using UOMapWeaver.Core.TileColors;
using UOMapWeaver.Core.TileReplace;
using static UOMapWeaver.App.Views.ViewHelpers;
using FieldState = UOMapWeaver.App.Views.ViewHelpers.FieldState;

namespace UOMapWeaver.App.Views;

public sealed partial class MulToBmpView : UserControl, IAppStateView
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

    public MulToBmpView()
    {
        InitializeComponent();
        LoadTerrainEncodings();
        LoadMapTransOptions();
        LoadState();
        EnsureTileReplacePath();
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

    private async void OnBrowseMapMul(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        MapMulPathBox.Text = await PickFileAsync(this, "Select map.mul", new[] { "mul", "uop" });
        AutoPopulateFromMapMul();
        UpdateStatus();
    }

    private async void OnBrowseStaIdx(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => StaIdxPathBox.Text = await PickFileAsync(this, "Select staidx.mul", new[] { "mul" });

    private async void OnBrowseStatics(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => StaticsPathBox.Text = await PickFileAsync(this, "Select statics.mul", new[] { "mul" });

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

    private async void OnBrowseTileReplace(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => TileReplacePathBox.Text = await PickFileAsync(this, "Select tile replace JSON", new[] { "json" });

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

        if (!TryResolveMapSize(out var width, out var height))
        {
            StatusText.Text = "Map size is required or could not be detected.";
            StatusTextPreview.Text = StatusText.Text;
            AppStatus.SetError(StatusText.Text);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(MapMulPathBox.Text) || !File.Exists(MapMulPathBox.Text))
        {
            StatusText.Text = "Map.mul not found.";
            StatusTextPreview.Text = StatusText.Text;
            AppStatus.SetError(StatusText.Text);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
            return;
        }

        if (!TryResolveOutputBaseName(out var baseName, out var nameError))
        {
            StatusText.Text = nameError;
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
            var mapPath = MapMulPathBox.Text!;
            var outputFolder = OutputFolderBox.Text!;
            var encodingTag = GetEncodingTag(encoding);
            var useStreaming = UseStreaming(width, height);

            var terrainPath = Path.Combine(outputFolder, baseName is null
                ? $"{encodingTag}_Terrain.bmp"
                : $"{baseName}_{encodingTag}_Terrain.bmp");
            var altitudePath = Path.Combine(outputFolder, baseName is null
                ? $"{encodingTag}_Altitude.bmp"
                : $"{baseName}_{encodingTag}_Altitude.bmp");

            if (!await ConfirmOverwriteAsync("Overwrite BMP files?", terrainPath, altitudePath))
            {
                StatusText.Text = "Generation cancelled.";
                StatusTextPreview.Text = StatusText.Text;
                AppStatus.SetWarning(StatusText.Text);
                AppStatus.AppendLog(StatusText.Text, AppStatusSeverity.Warning);
                AppStatus.SetCancelSource(null);
                return;
            }

            AppStatus.AppendLog("MUL->BMP start.", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Map.mul: {mapPath}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Map.mul size: {FormatFileSize(mapPath)}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Map size: {width}x{height}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Encoding: {encoding}", AppStatusSeverity.Info);
            if (useTileJson)
            {
                AppStatus.AppendLog($"Tile JSON: {TileJsonPathBox.Text}", AppStatusSeverity.Info);
            }
            else if (useMapTrans)
            {
                AppStatus.AppendLog($"MapTrans: {selected}", AppStatusSeverity.Info);
            }
            if (ApplyTileReplaceCheckBox.IsChecked == true)
            {
                AppStatus.AppendLog($"Tile replace JSON: {TileReplacePathBox.Text}", AppStatusSeverity.Info);
            }
            AppStatus.AppendLog($"Output folder: {outputFolder}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Output base name: {(string.IsNullOrWhiteSpace(baseName) ? "(default)" : baseName)}",
                AppStatusSeverity.Info);
            AppStatus.AppendLog($"Include statics: {IncludeStaticsCheckBox.IsChecked == true}", AppStatusSeverity.Info);

            if (IncludeStaticsCheckBox.IsChecked == true)
            {
                if (!string.IsNullOrWhiteSpace(StaIdxPathBox.Text))
                {
                    AppStatus.AppendLog($"StaIdx.mul: {StaIdxPathBox.Text}", AppStatusSeverity.Info);
                    AppStatus.AppendLog($"StaIdx.mul size: {FormatFileSize(StaIdxPathBox.Text)}", AppStatusSeverity.Info);
                }

                if (!string.IsNullOrWhiteSpace(StaticsPathBox.Text))
                {
                    AppStatus.AppendLog($"Statics.mul: {StaticsPathBox.Text}", AppStatusSeverity.Info);
                    AppStatus.AppendLog($"Statics.mul size: {FormatFileSize(StaticsPathBox.Text)}", AppStatusSeverity.Info);
                }
            }

            var progress = CreateAppProgress();

            var options = new MapConversionOptions
            {
                StopOnError = StopOnErrorCheckBox.IsChecked == true,
                CancellationToken = cancelSource.Token
            };

            if (ApplyTileReplaceCheckBox.IsChecked == true)
            {
                if (string.IsNullOrWhiteSpace(TileReplacePathBox.Text) || !File.Exists(TileReplacePathBox.Text))
                {
                    StatusText.Text = "Tile replace JSON not found.";
                    StatusTextPreview.Text = StatusText.Text;
                    AppStatus.SetError(StatusText.Text);
                    return;
                }

                var replaceMap = await Task.Run(() => TileReplacementMapSerializer.Load(TileReplacePathBox.Text));
                options.TileReplacementMap = replaceMap;
                AppStatus.AppendLog($"Tile replace: terrain {replaceMap.Terrain.Count:N0}, statics {replaceMap.Statics.Count:N0}.",
                    AppStatusSeverity.Info);
            }

            if (useTileIndex)
            {
                MapConversionReport report;
                if (useStreaming)
                {
                    report = await Task.Run(() => MapConversion.ConvertMulToTileIndexBmpToFile(
                        mapPath,
                        width,
                        height,
                        terrainPath,
                        altitudePath,
                        progress,
                        entry => AppStatus.AppendLog(entry.Message, MapLogToStatus(entry.Level)),
                        options
                    ), cancelSource.Token);
                }
                else
                {
                    var tileResult = await Task.Run(() => MapConversion.ConvertMulToTileIndexBmp(
                        mapPath,
                        width,
                        height,
                        progress,
                        entry => AppStatus.AppendLog(entry.Message, MapLogToStatus(entry.Level)),
                        options
                    ), cancelSource.Token);

                    await Task.Run(() =>
                    {
                        Bmp24Codec.Write(terrainPath, tileResult.terrain);
                        Bmp8Codec.Write(altitudePath, tileResult.altitude);
                    }, cancelSource.Token);
                    report = tileResult.report;
                }

                StatusText.Text = $"Generated {Path.GetFileName(terrainPath)} and {Path.GetFileName(altitudePath)}.";
                StatusTextPreview.Text = StatusText.Text;
                AppStatus.SetSuccess(StatusText.Text);
                AppStatus.AppendLog($"MUL->BMP completed: {terrainPath}, {altitudePath}. {report}", AppStatusSeverity.Success);
                return;
            }

            if (useTileJson)
            {
                if (string.IsNullOrWhiteSpace(TileJsonPathBox.Text) || !File.Exists(TileJsonPathBox.Text))
                {
                    StatusText.Text = "Tile Color JSON not found.";
                    StatusTextPreview.Text = StatusText.Text;
                    AppStatus.SetError(StatusText.Text);
                    return;
                }

                if (new FileInfo(TileJsonPathBox.Text).Length == 0)
                {
                    StatusText.Text = "Tile Color JSON is empty.";
                    StatusTextPreview.Text = StatusText.Text;
                    AppStatus.SetError(StatusText.Text);
                    return;
                }

                var tileMap = await Task.Run(() => TileColorMapSerializer.Load(TileJsonPathBox.Text));
                options.TileColorMap = tileMap;
                TileJsonModeText.Text = $"Mode: {tileMap.Mode}";

                if (tileMap.Mode == TileColorMode.Rgb24)
                {
                    MapConversionReport report;
                    if (useStreaming)
                    {
                        report = await Task.Run(() => MapConversion.ConvertMulToBmpRgb24ToFile(
                            mapPath,
                            width,
                            height,
                            tileMap,
                            terrainPath,
                            altitudePath,
                            progress,
                            entry => AppStatus.AppendLog(entry.Message, MapLogToStatus(entry.Level)),
                            options
                        ), cancelSource.Token);
                    }
                    else
                    {
                        var rgbResult = await Task.Run(() => MapConversion.ConvertMulToBmpRgb24(
                            mapPath,
                            width,
                            height,
                            tileMap,
                            progress,
                            entry => AppStatus.AppendLog(entry.Message, MapLogToStatus(entry.Level)),
                            options
                        ), cancelSource.Token);

                        await Task.Run(() =>
                        {
                            Bmp24Codec.Write(terrainPath, rgbResult.terrain);
                            Bmp8Codec.Write(altitudePath, rgbResult.altitude);
                        }, cancelSource.Token);
                        report = rgbResult.report;
                    }

                    var rgbReportSummary = report.MissingTerrainColors == 0
                        ? "No missing colors."
                        : $"Missing colors: {report.MissingTerrainColors:N0} (unknown color).";

                    StatusText.Text = $"Generated {Path.GetFileName(terrainPath)} and {Path.GetFileName(altitudePath)}. {rgbReportSummary}";
                    StatusTextPreview.Text = StatusText.Text;
                    AppStatus.SetSuccess(StatusText.Text);
                    AppStatus.AppendLog($"MUL->BMP completed: {terrainPath}, {altitudePath}. {report}", AppStatusSeverity.Success);
                    AppStatus.AppendLog(report.FormatTopMissingColors(),
                        report.MissingTerrainColors > 0 ? AppStatusSeverity.Warning : AppStatusSeverity.Info);
                    return;
                }
            }

            var profile = useTileJson
                ? new MapTransProfile("TileJson", Array.Empty<MapTransEntry>(), null)
                : MapTransParser.LoadFromFile(selected!);

            MapConversionReport finalReport;
            if (useStreaming)
            {
                finalReport = await Task.Run(() => MapConversion.ConvertMulToBmpToFile(
                    mapPath,
                    width,
                    height,
                    profile,
                    terrainPath,
                    altitudePath,
                    progress,
                    entry => AppStatus.AppendLog(entry.Message, MapLogToStatus(entry.Level)),
                    options
                ), cancelSource.Token);
            }
            else
            {
                var result = await Task.Run(() => MapConversion.ConvertMulToBmp(
                    mapPath,
                    width,
                    height,
                    profile,
                    progress,
                    entry => AppStatus.AppendLog(entry.Message, MapLogToStatus(entry.Level)),
                    options
                ), cancelSource.Token);

                await Task.Run(() =>
                {
                    Bmp8Codec.Write(terrainPath, result.terrain);
                    Bmp8Codec.Write(altitudePath, result.altitude);
                }, cancelSource.Token);
                finalReport = result.report;
            }

            var reportSummary = finalReport.MissingTerrainColors == 0
                ? "No missing colors."
                : $"Missing colors: {finalReport.MissingTerrainColors:N0} (transparent).";

            StatusText.Text = $"Generated {Path.GetFileName(terrainPath)} and {Path.GetFileName(altitudePath)}. {reportSummary}";
            StatusTextPreview.Text = StatusText.Text;
            AppStatus.SetSuccess(StatusText.Text);
            AppStatus.AppendLog($"MUL->BMP completed: {terrainPath}, {altitudePath}. {finalReport}", AppStatusSeverity.Success);
            AppStatus.AppendLog(finalReport.FormatTopMissingColors(),
                finalReport.MissingTerrainColors > 0 ? AppStatusSeverity.Warning : AppStatusSeverity.Info);
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
            AppStatus.AppendLog($"MUL->BMP error: {ex.Message}", AppStatusSeverity.Error);
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
        UpdateFileSizeForTextBox(sender);
        UpdateStatus();
        SaveState();
    }

    private void OnMapMulTextChanged(object? sender, TextChangedEventArgs e)
    {
        AutoPopulateFromMapMul();
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

    private bool TryResolveOutputBaseName(out string? baseName, out string errorMessage)
    {
        errorMessage = string.Empty;
        baseName = null;

        if (UseMapNameCheckBox.IsChecked == true)
        {
            if (!string.IsNullOrWhiteSpace(MapMulPathBox.Text))
            {
                baseName = Path.GetFileNameWithoutExtension(MapMulPathBox.Text);
                return true;
            }
        }

        var input = OutputBaseNameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            return true;
        }

        if (HasInvalidFileNameChars(input))
        {
            errorMessage = "Output name contains invalid characters.";
            return false;
        }

        baseName = input;
        return true;
    }

    private static bool HasInvalidFileNameChars(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return name.IndexOfAny(invalidChars) >= 0;
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
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right
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

        MapMulPathBox.Text = AppSettings.GetString("MulToBmp.MapMulPath", MapMulPathBox.Text ?? string.Empty);
        StaIdxPathBox.Text = AppSettings.GetString("MulToBmp.StaIdxPath", StaIdxPathBox.Text ?? string.Empty);
        StaticsPathBox.Text = AppSettings.GetString("MulToBmp.StaticsPath", StaticsPathBox.Text ?? string.Empty);
        OutputFolderBox.Text = AppSettings.GetString("MulToBmp.OutputFolder", OutputFolderBox.Text ?? string.Empty);
        MapWidthBox.Text = AppSettings.GetString("MulToBmp.MapWidth", MapWidthBox.Text ?? string.Empty);
        MapHeightBox.Text = AppSettings.GetString("MulToBmp.MapHeight", MapHeightBox.Text ?? string.Empty);
        TileJsonPathBox.Text = AppSettings.GetString("MulToBmp.TileJsonPath", TileJsonPathBox.Text ?? string.Empty);
        TileReplacePathBox.Text = AppSettings.GetString("MulToBmp.TileReplacePath", TileReplacePathBox.Text ?? string.Empty);
        OutputBaseNameBox.Text = AppSettings.GetString("MulToBmp.OutputBaseName", OutputBaseNameBox.Text ?? string.Empty);

        IncludeStaticsCheckBox.IsChecked = AppSettings.GetBool("MulToBmp.IncludeStatics", IncludeStaticsCheckBox.IsChecked == true);
        UseMapNameCheckBox.IsChecked = AppSettings.GetBool("MulToBmp.UseMapName", UseMapNameCheckBox.IsChecked == true);
        StopOnErrorCheckBox.IsChecked = AppSettings.GetBool("MulToBmp.StopOnError", StopOnErrorCheckBox.IsChecked == true);
        ApplyTileReplaceCheckBox.IsChecked = AppSettings.GetBool("MulToBmp.ApplyTileReplace", ApplyTileReplaceCheckBox.IsChecked == true);

        var encodingText = AppSettings.GetString("MulToBmp.TerrainEncoding", string.Empty);
        if (Enum.TryParse<TerrainEncoding>(encodingText, out var encoding))
        {
            var match = _terrainEncodings.FirstOrDefault(option => option.Encoding == encoding);
            if (match != null)
            {
                TerrainEncodingComboBox.SelectedItem = match;
            }
        }

        var mapTransPath = AppSettings.GetString("MulToBmp.MapTransPath", string.Empty);
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

        AppSettings.SetString("MulToBmp.MapMulPath", MapMulPathBox.Text ?? string.Empty);
        AppSettings.SetString("MulToBmp.StaIdxPath", StaIdxPathBox.Text ?? string.Empty);
        AppSettings.SetString("MulToBmp.StaticsPath", StaticsPathBox.Text ?? string.Empty);
        AppSettings.SetString("MulToBmp.OutputFolder", OutputFolderBox.Text ?? string.Empty);
        AppSettings.SetString("MulToBmp.MapWidth", MapWidthBox.Text ?? string.Empty);
        AppSettings.SetString("MulToBmp.MapHeight", MapHeightBox.Text ?? string.Empty);
        AppSettings.SetString("MulToBmp.TileJsonPath", TileJsonPathBox.Text ?? string.Empty);
        AppSettings.SetString("MulToBmp.TileReplacePath", TileReplacePathBox.Text ?? string.Empty);
        AppSettings.SetString("MulToBmp.OutputBaseName", OutputBaseNameBox.Text ?? string.Empty);
        AppSettings.SetBool("MulToBmp.IncludeStatics", IncludeStaticsCheckBox.IsChecked == true);
        AppSettings.SetBool("MulToBmp.UseMapName", UseMapNameCheckBox.IsChecked == true);
        AppSettings.SetBool("MulToBmp.StopOnError", StopOnErrorCheckBox.IsChecked == true);
        AppSettings.SetBool("MulToBmp.ApplyTileReplace", ApplyTileReplaceCheckBox.IsChecked == true);

        var encoding = GetTerrainEncoding();
        AppSettings.SetString("MulToBmp.TerrainEncoding", encoding.ToString());

        var mapTransPath = GetSelectedMapTransPath() ?? string.Empty;
        AppSettings.SetString("MulToBmp.MapTransPath", mapTransPath);
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

        var mapMulExists = !string.IsNullOrWhiteSpace(MapMulPathBox.Text) && File.Exists(MapMulPathBox.Text);
        if (string.IsNullOrWhiteSpace(MapMulPathBox.Text))
        {
            warnings.Add("Map.mul is required.");
            MapMulSizeText.Text = string.Empty;
        }
        else if (!File.Exists(MapMulPathBox.Text))
        {
            warnings.Add("Map.mul not found.");
            MapMulSizeText.Text = "Not found.";
        }
        else
        {
            TryPopulateMapFileSize(MapMulPathBox.Text, MapMulSizeText);
        }

        SetFieldState(MapMulPathBox, mapMulExists ? FieldState.Valid : FieldState.Error);

        if (IncludeStaticsCheckBox.IsChecked == true)
        {
            var staIdxExists = !string.IsNullOrWhiteSpace(StaIdxPathBox.Text) && File.Exists(StaIdxPathBox.Text);
            if (string.IsNullOrWhiteSpace(StaIdxPathBox.Text))
            {
                warnings.Add("StaIdx.mul is required when statics are enabled.");
                StaIdxSizeText.Text = string.Empty;
            }
            else if (!File.Exists(StaIdxPathBox.Text))
            {
                warnings.Add("StaIdx.mul not found.");
                StaIdxSizeText.Text = "Not found.";
            }
            else
            {
                TryPopulateMapFileSize(StaIdxPathBox.Text, StaIdxSizeText);
            }

            var staticsExists = !string.IsNullOrWhiteSpace(StaticsPathBox.Text) && File.Exists(StaticsPathBox.Text);
            if (string.IsNullOrWhiteSpace(StaticsPathBox.Text))
            {
                warnings.Add("Statics.mul is required when statics are enabled.");
                StaticsSizeText.Text = string.Empty;
            }
            else if (!File.Exists(StaticsPathBox.Text))
            {
                warnings.Add("Statics.mul not found.");
                StaticsSizeText.Text = "Not found.";
            }
            else
            {
                TryPopulateMapFileSize(StaticsPathBox.Text, StaticsSizeText);
            }

            SetFieldState(StaIdxPathBox, staIdxExists ? FieldState.Valid : FieldState.Error);
            SetFieldState(StaticsPathBox, staticsExists ? FieldState.Valid : FieldState.Error);
        }
        else
        {
            StaIdxSizeText.Text = string.Empty;
            StaticsSizeText.Text = string.Empty;
            SetFieldState(StaIdxPathBox, FieldState.Neutral, isOptional: true);
            SetFieldState(StaticsPathBox, FieldState.Neutral, isOptional: true);
        }

        if (string.IsNullOrWhiteSpace(OutputFolderBox.Text))
        {
            warnings.Add("Output folder is required.");
        }
        else if (!Directory.Exists(OutputFolderBox.Text))
        {
            warnings.Add("Output folder not found.");
        }

        SetFieldState(OutputFolderBox, Directory.Exists(OutputFolderBox.Text ?? string.Empty) ? FieldState.Valid : FieldState.Error);

        var encoding = GetTerrainEncoding();
        var useTileJson = encoding == TerrainEncoding.TileJson;
        var useMapTrans = encoding == TerrainEncoding.MapTrans;
        var mapTransSelected = GetSelectedMapTransPath() is not null;
        if (useMapTrans && !mapTransSelected)
        {
            warnings.Add("Select a MapTrans profile.");
        }

        if (useMapTrans)
        {
            SetFieldState(MapTransComboBox, mapTransSelected ? FieldState.Valid : FieldState.Warning);
        }
        else
        {
            SetFieldState(MapTransComboBox, FieldState.Neutral);
        }

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
                SetFieldState(TileJsonPathBox, FieldState.Warning, isOptional: true);
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
            TileJsonModeText.Text = encoding == TerrainEncoding.TileIndexRgb
                ? "TileIndex RGB (24-bit)"
                : string.Empty;
            SetFieldState(TileJsonPathBox, FieldState.Neutral, isOptional: true);
        }

        TileJsonPathBox.IsEnabled = useTileJson;

        var applyReplace = ApplyTileReplaceCheckBox.IsChecked == true;
        if (applyReplace)
        {
            if (string.IsNullOrWhiteSpace(TileReplacePathBox.Text))
            {
                warnings.Add("Tile replace JSON is required.");
                SetFieldState(TileReplacePathBox, FieldState.Warning, isOptional: true);
            }
            else if (!File.Exists(TileReplacePathBox.Text))
            {
                warnings.Add("Tile replace JSON not found.");
                SetFieldState(TileReplacePathBox, FieldState.Error);
            }
            else
            {
                SetFieldState(TileReplacePathBox, FieldState.Valid);
            }
        }
        else
        {
            SetFieldState(TileReplacePathBox, FieldState.Neutral, isOptional: true);
        }

        var outputNameState = FieldState.Neutral;
        if (UseMapNameCheckBox.IsChecked == true)
        {
            outputNameState = string.IsNullOrWhiteSpace(MapMulPathBox.Text) ? FieldState.Warning : FieldState.Valid;
        }
        else if (!string.IsNullOrWhiteSpace(OutputBaseNameBox.Text))
        {
            outputNameState = HasInvalidFileNameChars(OutputBaseNameBox.Text.Trim()) ? FieldState.Error : FieldState.Valid;
        }

        if (outputNameState == FieldState.Error)
        {
            warnings.Add("Output name contains invalid characters.");
        }

        SetFieldState(OutputBaseNameBox, outputNameState, isOptional: true);

        MapSizeErrorText.Text = string.Empty;
        MapSizeDetailsText.Text = string.Empty;
        var sizeResolved = TryResolveMapSize(out var width, out var height);
        if (!sizeResolved)
        {
            warnings.Add("Map size is required or could not be detected.");
            MapSizeErrorText.Text = "Size not detected.";
        }
        else
        {
            if (width % MapMul.BlockSize != 0 || height % MapMul.BlockSize != 0)
            {
                warnings.Add("Map size must be divisible by 8.");
                MapSizeErrorText.Text = "Not /8.";
            }
            else
            {
                MapSizeDetailsText.Text = $"Blocks: {width / MapMul.BlockSize}x{height / MapMul.BlockSize} | Max: {width - 1},{height - 1}";
            }
        }

        var sizeMatchesFile = true;
        if (mapMulExists && sizeResolved)
        {
            sizeMatchesFile = MapConversion.DoesSizeMatchFile(MapMulPathBox.Text!, width, height);
            if (!sizeMatchesFile)
            {
                var details = MapConversion.GetSizeMismatchDetails(MapMulPathBox.Text!, width, height);
                warnings.Add(details);
                MapSizeErrorText.Text = "Mismatch.";
            }
        }

        var mapSizeState = FieldState.Valid;
        if (!sizeResolved)
        {
            mapSizeState = FieldState.Warning;
        }
        else if (!sizeMatchesFile || MapSizeErrorText.Text is "Mismatch." or "Not /8.")
        {
            mapSizeState = FieldState.Error;
        }

        SetFieldState(MapWidthBox, mapSizeState);
        SetFieldState(MapHeightBox, mapSizeState);

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
        else if (MapSizeErrorText.Text is "Mismatch." or "Not /8." || warnings.Exists(w => w.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            AppStatus.SetError(message);
        }
        else
        {
            AppStatus.SetWarning(message);
        }
    }

    private bool TryResolveMapSize(out int width, out int height)
    {
        width = 0;
        height = 0;

        if (int.TryParse(MapWidthBox.Text, out var parsedWidth) &&
            int.TryParse(MapHeightBox.Text, out var parsedHeight))
        {
            width = parsedWidth;
            height = parsedHeight;
            return width > 0 && height > 0;
        }

        if (!string.IsNullOrWhiteSpace(MapMulPathBox.Text))
        {
            return MapConversion.TryResolveMapSizeFromFile(MapMulPathBox.Text, out width, out height);
        }

        return false;
    }

    private void EnsureTileReplacePath()
    {
        if (string.IsNullOrWhiteSpace(TileReplacePathBox.Text))
        {
            TileReplacePathBox.Text = Path.Combine(UOMapWeaverDataPaths.TileReplaceRoot, "UOMapWeaver_TileReplace.json");
        }
    }

    private void AutoPopulateFromMapMul()
    {
        if (string.IsNullOrWhiteSpace(MapMulPathBox.Text))
        {
            MapMulSizeText.Text = string.Empty;
            return;
        }

        var mapPath = MapMulPathBox.Text.Trim();
        if (!File.Exists(mapPath))
        {
            MapMulSizeText.Text = "Not found.";
            return;
        }

        TryPopulateStatics(mapPath);
        TryPopulateMapSize(mapPath);
        TryPopulateMapFileSize(mapPath, MapMulSizeText);
    }

    private void TryPopulateMapSize(string mapPath)
    {
        if (MapConversion.TryResolveMapSizeFromFile(mapPath, out var width, out var height))
        {
            MapWidthBox.Text = width.ToString();
            MapHeightBox.Text = height.ToString();
            MapSizeErrorText.Text = string.Empty;
        }
        else
        {
            MapSizeErrorText.Text = "Size not detected.";
        }
    }

    private void TryPopulateMapFileSize(string mapPath, TextBlock target)
    {
        try
        {
            var fileInfo = new FileInfo(mapPath);
            var bytes = fileInfo.Length;
            var blockCount = bytes / MapMul.LandBlockBytes;
            target.Text = $"{bytes:N0} bytes ({blockCount:N0} blocks)";
        }
        catch
        {
            target.Text = "Size unavailable.";
        }
    }

    private void TryPopulateStatics(string mapPath)
    {
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
            StaticsPathBox.Text = staticsPath;
            TryPopulateMapFileSize(staticsPath, StaticsSizeText);
        }

        if (File.Exists(staidxPath))
        {
            StaIdxPathBox.Text = staidxPath;
            TryPopulateMapFileSize(staidxPath, StaIdxSizeText);
        }
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

    private void UpdateFileSizeForTextBox(object? sender)
    {
        if (sender == MapMulPathBox)
        {
            if (!string.IsNullOrWhiteSpace(MapMulPathBox.Text) && File.Exists(MapMulPathBox.Text))
            {
                TryPopulateMapFileSize(MapMulPathBox.Text, MapMulSizeText);
            }
            else
            {
                MapMulSizeText.Text = string.Empty;
            }
            return;
        }

        if (sender == StaIdxPathBox)
        {
            if (!string.IsNullOrWhiteSpace(StaIdxPathBox.Text) && File.Exists(StaIdxPathBox.Text))
            {
                TryPopulateMapFileSize(StaIdxPathBox.Text, StaIdxSizeText);
            }
            else
            {
                StaIdxSizeText.Text = string.Empty;
            }
            return;
        }

        if (sender == StaticsPathBox)
        {
            if (!string.IsNullOrWhiteSpace(StaticsPathBox.Text) && File.Exists(StaticsPathBox.Text))
            {
                TryPopulateMapFileSize(StaticsPathBox.Text, StaticsSizeText);
            }
            else
            {
                StaticsSizeText.Text = string.Empty;
            }
        }
    }

    private static string? GetMapSuffix(string baseName) => baseName.Length == 0 ? null : baseName;

    private static string GetEncodingTag(TerrainEncoding encoding)
    {
        return encoding switch
        {
            TerrainEncoding.TileJson => "TileJson",
            TerrainEncoding.TileIndexRgb => "TileIndexRgb",
            _ => "MapTrans"
        };
    }

    private static bool UseStreaming(int width, int height)
        => (long)width * height >= LargeMapPixelThreshold;
}

