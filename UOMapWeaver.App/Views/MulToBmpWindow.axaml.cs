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
    private readonly List<PaletteOption> _paletteOptions = new();
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
        LoadPaletteOptions();
        LoadState();
        EnsureTileReplacePath();
        UpdateStatus();
        StopOnErrorCheckBox.IsCheckedChanged += (_, _) => SaveState();
        if (PreviewScroll != null)
        {
            PreviewScroll.SizeChanged += (_, _) =>
            {
                if (!_previewManualZoom)
                {
                    FitPreviewToViewport();
                }
            };
        }
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
        var path = await PickFileAsync(this, "Select MapTrans profile", new[] { "txt", "json", "xml" });
        if (!string.IsNullOrWhiteSpace(path))
        {
            AddMapTransOption(path);
        }
    }

    private async void OnBrowsePalette(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = await PickFileAsync(this, "Select palette BMP", new[] { "bmp" });
        if (!string.IsNullOrWhiteSpace(path))
        {
            AddPaletteOption(path);
        }
    }

    private async void OnImportActPalette(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = await PickFileAsync(this, "Select ACT palette", new[] { "act" });
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (!TryBuildActPalette(path, out var palette))
            {
                StatusText.Text = "ACT palette is invalid.";
                AppStatus.SetError(StatusText.Text);
                return;
            }

            var name = Path.GetFileNameWithoutExtension(path);
            var outputPath = Path.Combine(UOMapWeaverDataPaths.PalettesRoot, $"{name}.bmp");
            var image = new Bmp8Image(16, 16, BuildPalettePixels(), palette);
            Bmp8Codec.Write(outputPath, image);

            AddPaletteOption(outputPath);
            if (MapTransPaletteComboBox != null)
            {
                MapTransPaletteComboBox.SelectedItem = _paletteOptions.Find(option =>
                    option.Path.Equals(outputPath, StringComparison.OrdinalIgnoreCase));
            }
            LoadPalettePreview();
            SaveState();
            AppStatus.AppendLog($"Palette imported: {outputPath}", AppStatusSeverity.Success);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Palette import error: {ex.Message}";
            AppStatus.SetError(StatusText.Text);
        }
    }

    private void OnGeneratePalette(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            var palette = new BmpPaletteEntry[256];
            var random = new Random();
            for (var i = 0; i < palette.Length; i++)
            {
                palette[i] = new BmpPaletteEntry(
                    (byte)random.Next(0, 256),
                    (byte)random.Next(0, 256),
                    (byte)random.Next(0, 256),
                    0);
            }

            var pixels = new byte[16 * 16];
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = (byte)i;
            }

            var image = new Bmp8Image(16, 16, pixels, palette);
            var outputPath = Path.Combine(UOMapWeaverDataPaths.PalettesRoot,
                $"RandomPalette_{DateTime.Now:yyyyMMdd_HHmmss}.bmp");
            Bmp8Codec.Write(outputPath, image);

            AddPaletteOption(outputPath);
            AppStatus.AppendLog($"Palette generated: {outputPath}", AppStatusSeverity.Success);

            var previewDir = Path.Combine(UOMapWeaverDataPaths.PalettesRoot, "24bit");
            Directory.CreateDirectory(previewDir);
            var previewPath = Path.Combine(previewDir,
                $"RandomPalette_{DateTime.Now:yyyyMMdd_HHmmss}_24bit.bmp");
            var previewPixels = new byte[16 * 16 * 3];
            for (var i = 0; i < palette.Length; i++)
            {
                var dst = i * 3;
                previewPixels[dst] = palette[i].Red;
                previewPixels[dst + 1] = palette[i].Green;
                previewPixels[dst + 2] = palette[i].Blue;
            }
            var previewImage = new Bmp24Image(16, 16, previewPixels);
            Bmp24Codec.Write(previewPath, previewImage);
            AppStatus.AppendLog($"24-bit palette preview generated: {previewPath}", AppStatusSeverity.Info);
        }
        catch (Exception ex)
        {
            AppStatus.AppendLog($"Palette generation error: {ex.Message}", AppStatusSeverity.Error);
        }
    }

    private void OnPaletteTextChanged(object? sender, TextChangedEventArgs e)
    {
        LoadPalettePreview();
        UpdateStatus();
        SaveState();
    }

    private void OnConvertMapTrans(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var selected = GetSelectedMapTransPath();
        if (string.IsNullOrWhiteSpace(selected))
        {
            StatusText.Text = "Select a MapTrans profile.";
            AppStatus.SetError(StatusText.Text);
            return;
        }

        if (!selected.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "MapTrans is already JSON.";
            AppStatus.SetInfo(StatusText.Text);
            return;
        }

        try
        {
            var profile = MapTransParser.LoadFromFile(selected);
            if (selected.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                AppStatus.AppendLog($"MapTrans loaded from TXT: {Path.GetFileName(selected)}", AppStatusSeverity.Warning);
            }
            var jsonPath = Path.ChangeExtension(selected, ".json");
            var paletteFile = profile.PalettePath is null ? null : Path.GetFileName(profile.PalettePath);
            MapTransJsonSerializer.Save(jsonPath, profile, paletteFile);
            AddMapTransOption(jsonPath);
            StatusText.Text = $"Saved {Path.GetFileName(jsonPath)}.";
            AppStatus.SetSuccess(StatusText.Text);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"MapTrans convert error: {ex.Message}";
            AppStatus.SetError(StatusText.Text);
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
        var useMapTransXml = encoding == TerrainEncoding.MapTransXml;
        var useTerrainXml = encoding == TerrainEncoding.TerrainXml;
        var useTileIndex = encoding == TerrainEncoding.TileIndexRgb;
        var selected = GetSelectedMapTransPath();
        if ((useMapTrans || useMapTransXml) && string.IsNullOrWhiteSpace(selected))
        {
            StatusText.Text = "Select a MapTrans profile.";
            AppStatus.SetError(StatusText.Text);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
            return;
        }
        if (useMapTrans && !string.IsNullOrWhiteSpace(selected) &&
            selected.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "MapTrans (8-bit) requires a Mod*.txt profile.";
            StatusTextPreview.Text = StatusText.Text;
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
            else if (useMapTrans || useMapTransXml)
            {
                AppStatus.AppendLog($"MapTrans: {selected}", AppStatusSeverity.Info);
                if (useMapTransXml)
                {
                    var terrainXmlPath = Path.Combine(UOMapWeaverDataPaths.DataRoot, "Terrain.xml");
                    AppStatus.AppendLog($"Terrain.xml: {terrainXmlPath}", AppStatusSeverity.Info);
                }
                var paletteOverrideLog = GetSelectedPalettePath();
                if (!string.IsNullOrWhiteSpace(paletteOverrideLog))
                {
                    AppStatus.AppendLog($"Palette override: {paletteOverrideLog}", AppStatusSeverity.Info);
                    if (File.Exists(paletteOverrideLog))
                    {
                        try
                        {
                            var paletteImage = Bmp8Codec.Read(paletteOverrideLog);
                            var unique = paletteImage.Palette
                                .Select(entry => (entry.Red << 16) | (entry.Green << 8) | entry.Blue)
                                .Distinct()
                                .Count();
                            AppStatus.AppendLog(
                                $"Palette info: {paletteImage.Width}x{paletteImage.Height}, entries {paletteImage.Palette.Length}, unique {unique}.",
                                AppStatusSeverity.Info);
                            if (unique < 200)
                            {
                                AppStatus.AppendLog(
                                    $"Palette warning: only {unique} unique colors; MapTrans 8-bit may show color drift.",
                                    AppStatusSeverity.Warning);
                            }
                        }
                        catch (Exception ex)
                        {
                            AppStatus.AppendLog($"Palette info error: {ex.Message}", AppStatusSeverity.Warning);
                        }
                    }
                }
                else
                {
                    var actPath = Path.Combine(UOMapWeaverDataPaths.DataRoot, "ColorTables", "ACT", "Terrain.act");
                    if (File.Exists(actPath))
                    {
                        AppStatus.AppendLog($"Terrain palette source: {actPath}", AppStatusSeverity.Info);
                    }
                }
            }
            else if (useTerrainXml)
            {
                var terrainXmlPath = Path.Combine(UOMapWeaverDataPaths.DataRoot, "Terrain.xml");
                var transitionsRoot = Path.Combine(UOMapWeaverDataPaths.DataRoot, "Transitions");
                AppStatus.AppendLog($"Terrain.xml: {terrainXmlPath}", AppStatusSeverity.Info);
                AppStatus.AppendLog($"Transitions: {transitionsRoot}", AppStatusSeverity.Info);
            }
            if (ApplyTileReplaceCheckBox.IsChecked == true)
            {
                AppStatus.AppendLog($"Tile replace JSON: {TileReplacePathBox.Text}", AppStatusSeverity.Info);
            }
            AppStatus.AppendLog($"Output folder: {outputFolder}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Output base name: {(string.IsNullOrWhiteSpace(baseName) ? "(default)" : baseName)}",
                AppStatusSeverity.Info);
            AppStatus.AppendLog($"Include statics: {IncludeStaticsCheckBox.IsChecked == true}", AppStatusSeverity.Info);

            LogMissingMulToBmpData(encoding);

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
                UseAltitudeXmlColors = useTerrainXml && AltitudeColorCheckBox.IsChecked == true,
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

            if (useTerrainXml)
            {
                var terrainXmlPath = Path.Combine(UOMapWeaverDataPaths.DataRoot, "Terrain.xml");
                var transitionsRoot = Path.Combine(UOMapWeaverDataPaths.DataRoot, "Transitions");
                if (!File.Exists(terrainXmlPath))
                {
                    StatusText.Text = $"Terrain.xml not found at {terrainXmlPath}.";
                    StatusTextPreview.Text = StatusText.Text;
                    AppStatus.SetError(StatusText.Text);
                    AppStatus.SetCancelSource(null);
                    SetBusy(false);
                    return;
                }

                var report = await Task.Run(() => MapConversion.ConvertMulToTerrainXmlBmpToFile(
                    mapPath,
                    width,
                    height,
                    terrainXmlPath,
                    transitionsRoot,
                    terrainPath,
                    altitudePath,
                    progress,
                    entry => AppStatus.AppendLog(entry.Message, MapLogToStatus(entry.Level)),
                    options
                ), cancelSource.Token);

                var terrainXmlSummary = report.MissingTerrainColors == 0
                    ? "No missing colors."
                    : $"Missing colors: {report.MissingTerrainColors:N0} (unknown color).";

                StatusText.Text = $"Generated {Path.GetFileName(terrainPath)} and {Path.GetFileName(altitudePath)}. {terrainXmlSummary}";
                StatusTextPreview.Text = StatusText.Text;
                AppStatus.SetSuccess(StatusText.Text);
                AppStatus.AppendLog($"MUL->BMP completed: {terrainPath}, {altitudePath}. {report}", AppStatusSeverity.Success);
                AppStatus.AppendLog(report.FormatTopMissingColors(),
                    report.MissingTerrainColors > 0 ? AppStatusSeverity.Warning : AppStatusSeverity.Info);
                return;
            }

            MapTransProfile profile;
            var mapTransPathUsed = selected;
            if (useTileJson)
            {
                profile = new MapTransProfile("TileJson", Array.Empty<MapTransEntry>(), null);
            }
            else if (useMapTransXml)
            {
                var txtFallback = Path.ChangeExtension(selected, ".txt");
                if (!string.IsNullOrWhiteSpace(txtFallback) && File.Exists(txtFallback))
                {
                    mapTransPathUsed = txtFallback;
                    AppStatus.AppendLog("MapTrans XML selected for MUL->BMP. Using TXT profile fallback.",
                        AppStatusSeverity.Warning);
                    var baseProfile = MapTransParser.LoadFromFile(txtFallback);
                    var terrainXmlPath = Path.Combine(UOMapWeaverDataPaths.DataRoot, "Terrain.xml");
                    var xmlProfile = File.Exists(terrainXmlPath)
                        ? MapTransXmlParser.LoadFromFiles(selected!, terrainXmlPath)
                        : new MapTransProfile("MapTransXml", Array.Empty<MapTransEntry>(), null);
                    profile = MapTransXmlParser.MergeMissingTiles(baseProfile, xmlProfile, out var addedTiles, out var newEntries);
                    if (addedTiles > 0)
                    {
                        AppStatus.AppendLog($"MapTrans XML merged {addedTiles:N0} tiles into {newEntries:N0} entries.",
                            AppStatusSeverity.Info);
                    }
                }
                else
                {
                if (!selected!.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                {
                    StatusText.Text = "MapTrans XML encoding requires a Mod*.xml profile.";
                    StatusTextPreview.Text = StatusText.Text;
                    AppStatus.SetError(StatusText.Text);
                    AppStatus.SetCancelSource(null);
                    SetBusy(false);
                    return;
                }

                var terrainXmlPath = Path.Combine(UOMapWeaverDataPaths.DataRoot, "Terrain.xml");
                if (!File.Exists(terrainXmlPath))
                {
                    StatusText.Text = $"Terrain.xml not found at {terrainXmlPath}.";
                    StatusTextPreview.Text = StatusText.Text;
                    AppStatus.SetError(StatusText.Text);
                    AppStatus.SetCancelSource(null);
                    SetBusy(false);
                    return;
                }

                profile = MapTransXmlParser.LoadFromFiles(selected, terrainXmlPath);
                }
            }
            else
            {
                profile = MapTransParser.LoadFromFile(selected!);
            }
            var paletteOverride = GetSelectedPalettePath();
            if ((useMapTrans || useMapTransXml) && !string.IsNullOrWhiteSpace(paletteOverride) && File.Exists(paletteOverride))
            {
                profile = new MapTransProfile(profile.Name, profile.Entries, paletteOverride);
            }
            if (useMapTrans || useMapTransXml)
            {
                var terrainXmlPath = Path.Combine(UOMapWeaverDataPaths.DataRoot, "Terrain.xml");
                if (File.Exists(terrainXmlPath))
                {
                    profile = MapTransXmlParser.MergeMissingTilesFromTerrainColors(profile, terrainXmlPath, out var addedTiles);
                    if (addedTiles > 0)
                    {
                        AppStatus.AppendLog($"MapTrans terrain color merge: {addedTiles:N0} tiles.",
                            AppStatusSeverity.Info);
                    }
                }
            }
            if (useMapTrans && mapTransPathUsed!.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            {
                AppStatus.AppendLog($"MapTrans loaded from TXT: {Path.GetFileName(mapTransPathUsed)}", AppStatusSeverity.Warning);
            }
            if (useMapTrans || useMapTransXml)
            {
                AppStatus.AppendLog(
                    $"MapTrans palette: {(string.IsNullOrWhiteSpace(profile.PalettePath) ? "(none)" : profile.PalettePath)}",
                    AppStatusSeverity.Info);
            }

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
        if (sender == MapTransPaletteComboBox)
        {
            LoadPalettePreview();
        }

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
        var palettePath = AppSettings.GetString("MulToBmp.MapTransPalette", string.Empty);
        if (!string.IsNullOrWhiteSpace(palettePath))
        {
            AddPaletteOption(palettePath);
        }

        IncludeStaticsCheckBox.IsChecked = AppSettings.GetBool("MulToBmp.IncludeStatics", IncludeStaticsCheckBox.IsChecked == true);
        UseMapNameCheckBox.IsChecked = AppSettings.GetBool("MulToBmp.UseMapName", UseMapNameCheckBox.IsChecked == true);
        StopOnErrorCheckBox.IsChecked = AppSettings.GetBool("MulToBmp.StopOnError", StopOnErrorCheckBox.IsChecked == true);
        ApplyTileReplaceCheckBox.IsChecked = AppSettings.GetBool("MulToBmp.ApplyTileReplace", ApplyTileReplaceCheckBox.IsChecked == true);
        AltitudeColorCheckBox.IsChecked = AppSettings.GetBool("MulToBmp.AltitudeXmlColors", AltitudeColorCheckBox.IsChecked == true);

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

        LoadPalettePreview();

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
        AppSettings.SetString("MulToBmp.MapTransPalette", GetSelectedPalettePath() ?? string.Empty);
        AppSettings.SetBool("MulToBmp.IncludeStatics", IncludeStaticsCheckBox.IsChecked == true);
        AppSettings.SetBool("MulToBmp.UseMapName", UseMapNameCheckBox.IsChecked == true);
        AppSettings.SetBool("MulToBmp.StopOnError", StopOnErrorCheckBox.IsChecked == true);
        AppSettings.SetBool("MulToBmp.ApplyTileReplace", ApplyTileReplaceCheckBox.IsChecked == true);
        AppSettings.SetBool("MulToBmp.AltitudeXmlColors", AltitudeColorCheckBox.IsChecked == true);

        var encoding = GetTerrainEncoding();
        AppSettings.SetString("MulToBmp.TerrainEncoding", encoding.ToString());

        var mapTransPath = GetSelectedMapTransPath() ?? string.Empty;
        AppSettings.SetString("MulToBmp.MapTransPath", mapTransPath);
    }

    private void LoadPalettePreview()
    {
        if (MapTransPaletteImage == null)
        {
            return;
        }

        var path = GetSelectedPalettePath();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            MapTransPaletteImage.Source = null;
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            MapTransPaletteImage.Source = new Bitmap(stream);
        }
        catch
        {
            MapTransPaletteImage.Source = null;
        }
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
        _terrainEncodings.Add(new TerrainEncodingOption(TerrainEncoding.TerrainXml, "Terrain XML (Transitions, 24-bit)"));
        _terrainEncodings.Add(new TerrainEncodingOption(TerrainEncoding.MapTrans, "MapTrans (8-bit palette)"));
        _terrainEncodings.Add(new TerrainEncodingOption(TerrainEncoding.MapTransXml, "MapTrans XML (Terrain.xml)"));
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
            : TerrainEncoding.TerrainXml;
    }

    private void LoadMapTransOptions()
    {
        var selectedPath = GetSelectedMapTransPath();
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
            RefreshMapTransOptions(selectedPath);
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
        RefreshMapTransOptions(select ? path : null);
    }

    private string? GetSelectedMapTransPath()
    {
        return MapTransComboBox.SelectedItem is MapTransOption option ? option.Path : null;
    }

    private void RefreshMapTransOptions(string? selectPath)
    {
        _mapTransOptions.Sort(CompareMapTransOption);
        MapTransComboBox.ItemsSource = null;
        MapTransComboBox.ItemsSource = _mapTransOptions;

        if (!string.IsNullOrWhiteSpace(selectPath))
        {
            MapTransComboBox.SelectedItem = _mapTransOptions.Find(option =>
                option.Path.Equals(selectPath, StringComparison.OrdinalIgnoreCase));
        }
        else if (MapTransComboBox.SelectedItem is null && _mapTransOptions.Count > 0)
        {
            MapTransComboBox.SelectedIndex = 0;
        }
    }

    private void LoadPaletteOptions()
    {
        var selectedPath = GetSelectedPalettePath();
        _paletteOptions.Clear();
        foreach (var path in FindPalettePaths())
        {
            AddPaletteOption(path, select: false);
        }

        RefreshPaletteOptions(selectedPath);
        if (MapTransPaletteComboBox.SelectedItem is null)
        {
            var terrainPalette = _paletteOptions.Find(option =>
                option.Path.EndsWith("TerrainPalette.bmp", StringComparison.OrdinalIgnoreCase));
            if (terrainPalette != null)
            {
                MapTransPaletteComboBox.SelectedItem = terrainPalette;
            }
        }
    }

    private void AddPaletteOption(string path, bool select = true)
    {
        if (_paletteOptions.Exists(option => option.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
        {
            if (select)
            {
                MapTransPaletteComboBox.SelectedItem = _paletteOptions.Find(option =>
                    option.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            }
            return;
        }

        var parentName = new DirectoryInfo(Path.GetDirectoryName(path) ?? string.Empty).Name;
        var name = string.IsNullOrWhiteSpace(parentName)
            ? Path.GetFileName(path)
            : $"{parentName}/{Path.GetFileName(path)}";
        _paletteOptions.Add(new PaletteOption(name, path));
        RefreshPaletteOptions(select ? path : null);
    }

    private string? GetSelectedPalettePath()
        => MapTransPaletteComboBox.SelectedItem is PaletteOption option ? option.Path : null;

    private void RefreshPaletteOptions(string? selectPath)
    {
        _paletteOptions.Sort(ComparePaletteOption);
        MapTransPaletteComboBox.ItemsSource = null;
        MapTransPaletteComboBox.ItemsSource = _paletteOptions;

        if (!string.IsNullOrWhiteSpace(selectPath))
        {
            MapTransPaletteComboBox.SelectedItem = _paletteOptions.Find(option =>
                option.Path.Equals(selectPath, StringComparison.OrdinalIgnoreCase));
        }
        else if (MapTransPaletteComboBox.SelectedItem is null && _paletteOptions.Count > 0)
        {
            MapTransPaletteComboBox.SelectedIndex = 0;
        }
    }

    private static IEnumerable<string> FindPalettePaths()
    {
        if (!Directory.Exists(UOMapWeaverDataPaths.PalettesRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(UOMapWeaverDataPaths.PalettesRoot, "*.bmp", SearchOption.AllDirectories)
            .Where(path =>
            {
                var dir = Path.GetDirectoryName(path) ?? string.Empty;
                if (dir.EndsWith(Path.Combine("Palettes", "24bit"), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return !Path.GetFileName(path).Contains("_24bit", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryBuildActPalette(string actPath, out BmpPaletteEntry[] palette)
    {
        palette = Array.Empty<BmpPaletteEntry>();
        if (!File.Exists(actPath))
        {
            return false;
        }

        var bytes = File.ReadAllBytes(actPath);
        if (bytes.Length < 768)
        {
            return false;
        }

        var result = new BmpPaletteEntry[256];
        for (var i = 0; i < 256; i++)
        {
            var offset = i * 3;
            var red = bytes[offset];
            var green = bytes[offset + 1];
            var blue = bytes[offset + 2];
            result[i] = new BmpPaletteEntry(blue, green, red, 0);
        }

        palette = result;
        return true;
    }

    private static byte[] BuildPalettePixels()
    {
        var pixels = new byte[16 * 16];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)i;
        }

        return pixels;
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
        TerrainXml,
        MapTrans,
        MapTransXml,
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

    private sealed class PaletteOption
    {
        public PaletteOption(string name, string path)
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

    private static int ComparePaletteOption(PaletteOption left, PaletteOption right)
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
        var useMapTransXml = encoding == TerrainEncoding.MapTransXml;
        var useTerrainXml = encoding == TerrainEncoding.TerrainXml;
        var mapTransSelected = GetSelectedMapTransPath() is not null;
        if ((useMapTrans || useMapTransXml) && !mapTransSelected)
        {
            warnings.Add("Select a MapTrans profile.");
        }
        else if (useMapTrans)
        {
            var selected = GetSelectedMapTransPath();
            if (!string.IsNullOrWhiteSpace(selected) &&
                selected.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("MapTrans (8-bit) requires a Mod*.txt profile.");
            }
        }
        else if (useMapTransXml)
        {
            var selected = GetSelectedMapTransPath();
            if (!string.IsNullOrWhiteSpace(selected) &&
                !selected.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("MapTrans XML requires a Mod*.xml profile.");
            }
            else if (!string.IsNullOrWhiteSpace(selected))
            {
                var txtFallback = Path.ChangeExtension(selected, ".txt");
                if (string.IsNullOrWhiteSpace(txtFallback) || !File.Exists(txtFallback))
                {
                    warnings.Add("MapTrans XML needs a sibling Mod*.txt for MUL->BMP.");
                }
            }
        }

        if (useMapTrans || useMapTransXml)
        {
            var selected = GetSelectedMapTransPath();
            var wrongProfile = useMapTrans &&
                               !string.IsNullOrWhiteSpace(selected) &&
                               selected.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
            SetFieldState(MapTransComboBox, mapTransSelected && !wrongProfile ? FieldState.Valid : FieldState.Warning);
            var palettePath = GetSelectedPalettePath();
            if (!string.IsNullOrWhiteSpace(palettePath) && !File.Exists(palettePath))
            {
                warnings.Add("Palette BMP not found.");
                SetFieldState(MapTransPaletteComboBox, FieldState.Error);
            }
            else if (!string.IsNullOrWhiteSpace(palettePath))
            {
                SetFieldState(MapTransPaletteComboBox, FieldState.Valid);
            }
            else
            {
                SetFieldState(MapTransPaletteComboBox, FieldState.Neutral);
            }
        }
        else
        {
            SetFieldState(MapTransComboBox, FieldState.Neutral);
            SetFieldState(MapTransPaletteComboBox, FieldState.Neutral);
        }

        MapTransComboBox.IsEnabled = useMapTrans || useMapTransXml;
        MapTransPaletteComboBox.IsEnabled = useMapTrans || useMapTransXml;
        if (MapTransBrowseButton != null)
        {
            MapTransBrowseButton.IsEnabled = useMapTrans || useMapTransXml;
        }
        if (MapTransConvertButton != null)
        {
            var selected = GetSelectedMapTransPath();
            MapTransConvertButton.IsEnabled = useMapTrans && !string.IsNullOrWhiteSpace(selected) &&
                                             selected.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
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
            TileJsonModeText.Text = encoding switch
            {
                TerrainEncoding.TileIndexRgb => "TileIndex RGB (24-bit)",
                TerrainEncoding.TerrainXml => "Terrain XML (Transitions, 24-bit)",
                _ => string.Empty
            };
            SetFieldState(TileJsonPathBox, FieldState.Neutral, isOptional: true);
        }

        TileJsonPathBox.IsEnabled = useTileJson;

        if (useTerrainXml)
        {
            AltitudeColorCheckBox.IsEnabled = true;
            var terrainXmlPath = Path.Combine(UOMapWeaverDataPaths.DataRoot, "Terrain.xml");
            if (!File.Exists(terrainXmlPath))
            {
                warnings.Add("Terrain.xml not found.");
            }

            var transitionsRoot = Path.Combine(UOMapWeaverDataPaths.DataRoot, "Transitions");
            if (!Directory.Exists(transitionsRoot))
            {
                warnings.Add("Transitions folder not found.");
            }

            if (AltitudeColorCheckBox.IsChecked == true)
            {
                var altitudeXmlPath = Path.Combine(UOMapWeaverDataPaths.DataRoot, "Altitude.xml");
                if (!File.Exists(altitudeXmlPath))
                {
                    warnings.Add("Altitude.xml not found.");
                }
            }
        }
        else
        {
            AltitudeColorCheckBox.IsEnabled = false;
        }

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
            TerrainEncoding.TerrainXml => "TerrainXml",
            TerrainEncoding.TileJson => "TileJson",
            TerrainEncoding.TileIndexRgb => "TileIndexRgb",
            _ => "MapTrans"
        };
    }

    private void LogMissingMulToBmpData(TerrainEncoding encoding)
    {
        var usesMapTrans = encoding == TerrainEncoding.MapTrans || encoding == TerrainEncoding.MapTransXml;
        if (usesMapTrans)
        {
            var mapTransRoot = UOMapWeaverDataPaths.MapTransRoot;
            if (!Directory.Exists(mapTransRoot))
            {
                AppStatus.AppendLog($"MapTrans folder missing: {mapTransRoot}", AppStatusSeverity.Warning);
            }
            else
            {
                var hasProfiles = Directory.EnumerateFiles(mapTransRoot, "Mod*.txt", SearchOption.AllDirectories).Any() ||
                                  Directory.EnumerateFiles(mapTransRoot, "Mod*.xml", SearchOption.AllDirectories).Any();
                if (!hasProfiles)
                {
                    AppStatus.AppendLog($"MapTrans profiles not found in {mapTransRoot}.", AppStatusSeverity.Warning);
                }
            }

            var terrainXmlPath = Path.Combine(UOMapWeaverDataPaths.DataRoot, "Terrain.xml");
            if (!File.Exists(terrainXmlPath))
            {
                AppStatus.AppendLog($"Terrain.xml not found at {terrainXmlPath}.", AppStatusSeverity.Warning);
            }
        }
        else if (encoding == TerrainEncoding.TerrainXml)
        {
            var terrainXmlPath = Path.Combine(UOMapWeaverDataPaths.DataRoot, "Terrain.xml");
            if (!File.Exists(terrainXmlPath))
            {
                AppStatus.AppendLog($"Terrain.xml not found at {terrainXmlPath}.", AppStatusSeverity.Warning);
            }

            var transitionsRoot = Path.Combine(UOMapWeaverDataPaths.DataRoot, "Transitions");
            if (!Directory.Exists(transitionsRoot))
            {
                AppStatus.AppendLog($"Transitions folder not found: {transitionsRoot}", AppStatusSeverity.Warning);
            }
            if (AltitudeColorCheckBox.IsChecked == true)
            {
                var altitudeXmlPath = Path.Combine(UOMapWeaverDataPaths.DataRoot, "Altitude.xml");
                if (!File.Exists(altitudeXmlPath))
                {
                    AppStatus.AppendLog($"Altitude.xml not found at {altitudeXmlPath}.", AppStatusSeverity.Warning);
                }
            }
        }

        var palettePath = GetSelectedPalettePath();
        if (!string.IsNullOrWhiteSpace(palettePath))
        {
            if (!File.Exists(palettePath))
            {
                AppStatus.AppendLog($"Palette not found: {palettePath}", AppStatusSeverity.Warning);
            }
        }
        else
        {
            var paletteRoot = UOMapWeaverDataPaths.PalettesRoot;
            if (!Directory.Exists(paletteRoot) ||
                !Directory.EnumerateFiles(paletteRoot, "*.bmp", SearchOption.AllDirectories).Any())
            {
                AppStatus.AppendLog($"No palette BMPs found in {paletteRoot}.", AppStatusSeverity.Warning);
            }
        }
    }

    private static bool UseStreaming(int width, int height)
        => (long)width * height >= LargeMapPixelThreshold;
}

