using System;
using System.Collections.Generic;
using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UOMapWeaver.Core;
using UOMapWeaver.Core.Bmp;
using UOMapWeaver.Core.Map;
using UOMapWeaver.Core.Statics;
using static UOMapWeaver.App.Views.ViewHelpers;
using FieldState = UOMapWeaver.App.Views.ViewHelpers.FieldState;

namespace UOMapWeaver.App.Views;

public sealed partial class GenStaticsView : UserControl, IAppStateView
{
    private readonly List<StaticsLayoutOption> _layoutOptions = new();
    private readonly ObservableCollection<BiomeOverrideRow> _biomeOverrides = new();
    private bool _loadingState;

    public GenStaticsView()
    {
        InitializeComponent();
        DataContext = this;
        LoadLayouts();
        LoadBiomeOverrides();
        LoadState();
        UpdateStatus();
        StopOnErrorCheckBox.IsCheckedChanged += (_, _) => SaveState();
    }

    private async void OnBrowseMap(object? sender, RoutedEventArgs e)
        => MapPathBox.Text = await PickFileAsync(this, "Select map.mul", new[] { "mul" });

    private async void OnBrowseTerrain(object? sender, RoutedEventArgs e)
        => TerrainBmpBox.Text = await PickFileAsync(this, "Select Terrain.bmp", new[] { "bmp" });

    private async void OnBrowseAltitude(object? sender, RoutedEventArgs e)
        => AltitudeBmpBox.Text = await PickFileAsync(this, "Select Altitude.bmp", new[] { "bmp" });

    private async void OnBrowseOutput(object? sender, RoutedEventArgs e)
        => OutputFolderBox.Text = await PickFolderAsync(this, "Select output folder");

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

    private void OnCheckChanged(object? sender, RoutedEventArgs e)
    {
        UpdateStatus();
        SaveState();
    }

    private async void OnGenerate(object? sender, RoutedEventArgs e)
    {
        SetBusy(true);

        using var cancelSource = new CancellationTokenSource();
        AppStatus.SetCancelSource(cancelSource);

        try
        {
            if (string.IsNullOrWhiteSpace(MapPathBox.Text) || !File.Exists(MapPathBox.Text))
            {
                StatusText.Text = "Map.mul is required.";
                StatusTextPreview.Text = StatusText.Text;
                AppStatus.SetError(StatusText.Text);
                return;
            }

            if (string.IsNullOrWhiteSpace(OutputFolderBox.Text))
            {
                StatusText.Text = "Output folder is required.";
                StatusTextPreview.Text = StatusText.Text;
                AppStatus.SetError(StatusText.Text);
                return;
            }

            var mapPath = MapPathBox.Text.Trim();
            var outputFolder = OutputFolderBox.Text.Trim();
            var useTransitionStatics = TransitionStaticsCheckBox.IsChecked == true;
            var useRandomStatics = RandomStaticsCheckBox.IsChecked == true;
            var useImportStatics = ImportStaticsCheckBox.IsChecked == true;
            var useEmptyStatics = EmptyStaticsCheckBox.IsChecked == true;
            var stopOnError = StopOnErrorCheckBox.IsChecked == true;

            if (!Directory.Exists(outputFolder))
            {
                Directory.CreateDirectory(outputFolder);
            }

            if (!MapConversion.TryResolveMapSizeFromFile(mapPath, out var width, out var height))
            {
                StatusText.Text = "Map size not detected.";
                StatusTextPreview.Text = StatusText.Text;
                AppStatus.SetError(StatusText.Text);
                return;
            }

            var mapName = Path.GetFileNameWithoutExtension(mapPath);
            if (!TryResolveStaticsNames(mapName, out var staticsName, out var staidxName))
            {
                staticsName = "statics.mul";
                staidxName = "staidx.mul";
            }

            var staticsPath = Path.Combine(outputFolder, staticsName);
            var staidxPath = Path.Combine(outputFolder, staidxName);
            var layout = GetSelectedLayout();

            if (!await ConfirmOverwriteAsync("Overwrite statics files?", staidxPath, staticsPath))
            {
                StatusText.Text = "Generation cancelled.";
                StatusTextPreview.Text = StatusText.Text;
                AppStatus.SetWarning(StatusText.Text);
                AppStatus.AppendLog(StatusText.Text, AppStatusSeverity.Warning);
                return;
            }

            AppStatus.AppendLog("GenStatics start.", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Map.mul: {mapPath}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Map size: {width}x{height}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Output staidx.mul: {staidxPath}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Output statics.mul: {staticsPath}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Random statics: {useRandomStatics}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Import statics: {useImportStatics}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Empty statics: {useEmptyStatics}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Transition statics: {useTransitionStatics}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Statics layout: {layout}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Biome overrides: {OverrideEnabledCheckBox.IsChecked == true}", AppStatusSeverity.Info);
            LogMissingStaticsData(useRandomStatics, useImportStatics);

            if (EmptyStaticsCheckBox.IsChecked == true)
            {
                await Task.Run(() => StaticMulCodec.WriteEmptyStatics(staidxPath, staticsPath, width, height), cancelSource.Token);
                AppStatus.AppendLog("Generated empty statics.", AppStatusSeverity.Info);
                StatusText.Text = "Statics generated (empty).";
                StatusTextPreview.Text = StatusText.Text;
                AppStatus.SetSuccess(StatusText.Text);
                return;
            }

            var blocks = CreateEmptyBlocks(width, height);

            if (useTransitionStatics)
            {
                var terrainPath = TerrainBmpBox.Text?.Trim();
                var altitudePath = AltitudeBmpBox.Text?.Trim();
                if (string.IsNullOrWhiteSpace(terrainPath) || string.IsNullOrWhiteSpace(altitudePath) ||
                    !File.Exists(terrainPath) || !File.Exists(altitudePath))
                {
                    StatusText.Text = "Terrain.bmp and Altitude.bmp are required for transition statics.";
                    StatusTextPreview.Text = StatusText.Text;
                    AppStatus.SetError(StatusText.Text);
                    return;
                }

                if (!BmpCodec.TryReadInfo(terrainPath, out var terrainWidth, out var terrainHeight, out _) ||
                    terrainWidth != width || terrainHeight != height)
                {
                    StatusText.Text = "Terrain.bmp size must match map size.";
                    StatusTextPreview.Text = StatusText.Text;
                    AppStatus.SetError(StatusText.Text);
                    return;
                }

                if (!BmpCodec.TryReadInfo(altitudePath, out var altitudeWidth, out var altitudeHeight, out _) ||
                    altitudeWidth != width || altitudeHeight != height)
                {
                    StatusText.Text = "Altitude.bmp size must match map size.";
                    StatusTextPreview.Text = StatusText.Text;
                    AppStatus.SetError(StatusText.Text);
                    return;
                }

                var progress = CreateAppProgress();
                var placedStatics = 0;
                var terrainXmlPath = UOMapWeaverDataPaths.TerrainDefinitionsPath;
                var transitionsRoot = UOMapWeaverDataPaths.TransitionsRoot;
                var transitionBlocks = await Task.Run(() => MapConversion.GenerateTransitionStaticsFromTerrainXml(
                    terrainPath,
                    altitudePath,
                    terrainXmlPath,
                    transitionsRoot,
                    layout,
                    out placedStatics,
                    progress,
                    entry => AppStatus.AppendLog(entry.Message, MapLogToStatus(entry.Level)),
                    new MapConversionOptions
                    {
                        StopOnError = stopOnError,
                        CancellationToken = cancelSource.Token
                    }), cancelSource.Token);

                MergeBlocks(blocks, transitionBlocks);
                AppStatus.AppendLog($"Transition statics placed: {placedStatics:N0}.", AppStatusSeverity.Info);
            }

            LandTile[]? tiles = null;
            if (useRandomStatics)
            {
                tiles = await Task.Run(() => MapMulCodec.ReadLandTiles(mapPath, width, height), cancelSource.Token);
                var progress = CreateAppProgress();
                var missingReportPath = Path.Combine(outputFolder,
                    $"statics_missing_terrain_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                var overrides = BuildOverrideState();
                var randomOptions = new StaticPlacementOptions
                {
                    Layout = layout,
                    WriteMissingTerrainReport = true,
                    MissingTerrainReportPath = missingReportPath,
                    OverrideEnabled = overrides.Enabled
                };
                foreach (var kvp in overrides.DefinitionOverrides)
                {
                    randomOptions.OverrideDefinitions[kvp.Key] = kvp.Value;
                }

                foreach (var kvp in overrides.ChanceOverrides)
                {
                    randomOptions.OverrideChances[kvp.Key] = kvp.Value;
                }

                var randomBlocks = await Task.Run(() => StaticPlacementGenerator.Generate(
                    tiles,
                    width,
                    height,
                    randomOptions,
                    progress,
                    entry => AppStatus.AppendLog(entry.Message, MapLogToStatus(entry.Level)),
                    cancelSource.Token), cancelSource.Token);

                MergeBlocks(blocks, randomBlocks);
            }

            if (useImportStatics)
            {
                var importEntries = StaticImportCatalog.LoadStaticTiles(
                    new[] { UOMapWeaverDataPaths.ImportRoot },
                    out var importInfo);
                if (importEntries.Count == 0)
                {
                    AppStatus.AppendLog("Import statics enabled but no import XML found.", AppStatusSeverity.Warning);
                }
                else
                {
                    StaticImportCatalog.AddImportedStatics(blocks, importEntries, width, height, layout,
                        out var added, out var skipped);
                    AppStatus.AppendLog($"Imported statics: {importInfo.FileCount:N0} file(s), {importInfo.TileCount:N0} tiles.",
                        AppStatusSeverity.Info);
                    AppStatus.AppendLog($"Imported statics added: {added:N0}, skipped out of bounds: {skipped:N0}.",
                        AppStatusSeverity.Info);
                }
            }

            await Task.Run(() => StaticMulCodec.WriteStatics(staidxPath, staticsPath, width, height, blocks), cancelSource.Token);
            AppStatus.AppendLog("Generated populated statics.", AppStatusSeverity.Success);
            StatusText.Text = $"Statics generated: {Path.GetFileName(staticsPath)}";
            StatusTextPreview.Text = StatusText.Text;
            AppStatus.SetSuccess(StatusText.Text);
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
            AppStatus.AppendLog($"GenStatics error: {ex.Message}", AppStatusSeverity.Error);
        }
        finally
        {
            AppStatus.SetProgress(0, false);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
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

    private void LoadLayouts()
    {
        _layoutOptions.Clear();
        _layoutOptions.Add(new StaticsLayoutOption(StaticsLayout.ColumnMajor, "Column-major blocks (alt)"));
        _layoutOptions.Add(new StaticsLayoutOption(StaticsLayout.RowMajor, "Row-major blocks"));
        StaticsLayoutComboBox.ItemsSource = _layoutOptions;
        StaticsLayoutComboBox.SelectedIndex = 0;
    }

    private void UpdateStatus()
    {
        var warnings = new List<string>();
        MapSizeText.Text = string.Empty;
        UpdateOverrideSummary(warnings);

        var mapExists = !string.IsNullOrWhiteSpace(MapPathBox.Text) && File.Exists(MapPathBox.Text);
        SetFieldState(MapPathBox, mapExists ? FieldState.Valid : FieldState.Error);
        if (!mapExists)
        {
            warnings.Add("Map.mul is required.");
        }

        if (!string.IsNullOrWhiteSpace(MapPathBox.Text) &&
            File.Exists(MapPathBox.Text) &&
            MapConversion.TryResolveMapSizeFromFile(MapPathBox.Text, out var width, out var height))
        {
            MapSizeText.Text = $"Size: {width}x{height} | Blocks: {width / MapMul.BlockSize}x{height / MapMul.BlockSize} | Max: {width - 1},{height - 1}";
        }

        var outputExists = !string.IsNullOrWhiteSpace(OutputFolderBox.Text) && Directory.Exists(OutputFolderBox.Text);
        SetFieldState(OutputFolderBox, outputExists ? FieldState.Valid : FieldState.Error);
        if (!outputExists)
        {
            warnings.Add("Output folder is required.");
        }

        var transitionEnabled = TransitionStaticsCheckBox.IsChecked == true;
        var terrainBmpExists = !string.IsNullOrWhiteSpace(TerrainBmpBox.Text) && File.Exists(TerrainBmpBox.Text);
        var altitudeBmpExists = !string.IsNullOrWhiteSpace(AltitudeBmpBox.Text) && File.Exists(AltitudeBmpBox.Text);

        if (transitionEnabled)
        {
            SetFieldState(TerrainBmpBox, terrainBmpExists ? FieldState.Valid : FieldState.Error);
            SetFieldState(AltitudeBmpBox, altitudeBmpExists ? FieldState.Valid : FieldState.Error);
        }
        else
        {
            SetFieldState(TerrainBmpBox, FieldState.Neutral, isOptional: true);
            SetFieldState(AltitudeBmpBox, FieldState.Neutral, isOptional: true);
        }

        if (!string.IsNullOrWhiteSpace(MapPathBox.Text))
        {
            var mapName = Path.GetFileNameWithoutExtension(MapPathBox.Text);
            if (!TryResolveStaticsNames(mapName, out var staticsName, out var staidxName))
            {
                staticsName = "statics.mul";
                staidxName = "staidx.mul";
            }

            var outputFolder = !string.IsNullOrWhiteSpace(OutputFolderBox.Text)
                ? OutputFolderBox.Text
                : Path.GetDirectoryName(MapPathBox.Text) ?? string.Empty;

            StaIdxPathText.Text = Path.Combine(outputFolder, staidxName);
            StaticsPathText.Text = Path.Combine(outputFolder, staticsName);
        }

        var emptyEnabled = EmptyStaticsCheckBox.IsChecked == true;
        if (RandomStaticsCheckBox.IsChecked == false && ImportStaticsCheckBox.IsChecked == false && EmptyStaticsCheckBox.IsChecked == false)
        {
            warnings.Add("Enable Random, Import, or Empty statics.");
        }

        if (emptyEnabled)
        {
            RandomStaticsCheckBox.IsEnabled = false;
            ImportStaticsCheckBox.IsEnabled = false;
            TransitionStaticsCheckBox.IsEnabled = false;
        }
        else
        {
            RandomStaticsCheckBox.IsEnabled = true;
            ImportStaticsCheckBox.IsEnabled = true;
            TransitionStaticsCheckBox.IsEnabled = true;
        }

        if (RandomStaticsCheckBox.IsChecked == true)
        {
            var terrainPath = UOMapWeaverDataPaths.TerrainDefinitionsPath;
            var terrainTypesAvailable = Directory.Exists(UOMapWeaverDataPaths.TerrainTypesRoot) &&
                                        Directory.EnumerateFiles(UOMapWeaverDataPaths.TerrainTypesRoot, "*.xml", SearchOption.AllDirectories).Any();
            if (!File.Exists(terrainPath) || !terrainTypesAvailable)
            {
                warnings.Add("Static data missing; check Terrain.xml and TerrainTypes.");
            }
        }

        if (transitionEnabled)
        {
            if (!terrainBmpExists)
            {
                warnings.Add("Terrain.bmp is required for transition statics.");
            }

            if (!altitudeBmpExists)
            {
                warnings.Add("Altitude.bmp is required for transition statics.");
            }

            var mapWidth = 0;
            var mapHeight = 0;
            if (terrainBmpExists && altitudeBmpExists && mapExists &&
                MapConversion.TryResolveMapSizeFromFile(MapPathBox.Text!, out mapWidth, out mapHeight) &&
                BmpCodec.TryReadInfo(TerrainBmpBox.Text!, out var terrainWidth, out var terrainHeight, out _) &&
                BmpCodec.TryReadInfo(AltitudeBmpBox.Text!, out var altitudeWidth, out var altitudeHeight, out _))
            {
                if (terrainWidth != mapWidth || terrainHeight != mapHeight ||
                    altitudeWidth != mapWidth || altitudeHeight != mapHeight)
                {
                    warnings.Add("Terrain.bmp and Altitude.bmp must match map size.");
                }
            }
        }

        if (ImportStaticsCheckBox.IsChecked == true)
        {
            var importAvailable = Directory.Exists(UOMapWeaverDataPaths.ImportRoot) &&
                                  Directory.EnumerateFiles(UOMapWeaverDataPaths.ImportRoot, "*.xml", SearchOption.AllDirectories).Any();
            if (!importAvailable)
            {
                warnings.Add("Import statics enabled but no XML found.");
            }
        }

        if (TransitionStaticsCheckBox.IsChecked == true)
        {
            var terrainPath = TerrainBmpBox.Text;
            var altitudePath = AltitudeBmpBox.Text;
            if (string.IsNullOrWhiteSpace(terrainPath) || !File.Exists(terrainPath))
            {
                warnings.Add("Terrain.bmp is required for transition statics.");
            }

            if (string.IsNullOrWhiteSpace(altitudePath) || !File.Exists(altitudePath))
            {
                warnings.Add("Altitude.bmp is required for transition statics.");
            }

            var terrainXmlPath = UOMapWeaverDataPaths.TerrainDefinitionsPath;
            if (!File.Exists(terrainXmlPath))
            {
                warnings.Add($"Terrain.xml not found: {terrainXmlPath}");
            }

            if (!Directory.Exists(UOMapWeaverDataPaths.TransitionsRoot))
            {
                warnings.Add($"Transitions folder not found: {UOMapWeaverDataPaths.TransitionsRoot}");
            }
        }

        var message = warnings.Count == 0 ? "Ready." : string.Join(" ", warnings);
        StatusText.Text = AppStatus.Stamp(message);
        StatusTextPreview.Text = AppStatus.Stamp(message);

        if (warnings.Count == 0)
        {
            AppStatus.SetInfo(message);
        }
        else if (warnings.Any(w => w.Contains("required", StringComparison.OrdinalIgnoreCase) ||
                                   w.Contains("missing", StringComparison.OrdinalIgnoreCase)))
        {
            AppStatus.SetError(message);
        }
        else
        {
            AppStatus.SetWarning(message);
        }
    }

    private void UpdateOverrideSummary(List<string> warnings)
    {
        if (_biomeOverrides.Count == 0)
        {
            OverrideSummaryText.Text = "No terrain definitions loaded.";
            warnings.Add("No terrain definitions loaded.");
            return;
        }

        var missing = _biomeOverrides.Count(row => row.DefaultDefinition == "(missing)");
        var invalidChance = _biomeOverrides.Count(row => !string.IsNullOrWhiteSpace(row.ChanceOverride) &&
                                                        !int.TryParse(row.ChanceOverride, out _));
        var invalidOverride = _biomeOverrides.Count(row => !string.IsNullOrWhiteSpace(row.SelectedDefinition) &&
                                                          !row.AvailableDefinitions.Contains(row.SelectedDefinition));
        var enabled = OverrideEnabledCheckBox.IsChecked == true;

        OverrideSummaryText.Text = enabled
            ? $"Overrides enabled. Missing XML: {missing}. Invalid overrides: {invalidOverride}. Invalid chance: {invalidChance}."
            : $"Overrides disabled. Missing XML: {missing}.";

        if (missing > 0)
        {
            warnings.Add("Some biomes do not have a matching XML.");
        }

        if (invalidChance > 0)
        {
            warnings.Add("Chance override must be numeric.");
        }

        if (invalidOverride > 0)
        {
            warnings.Add("Override XML not found.");
        }
    }

    private void OnCheckData(object? sender, RoutedEventArgs e)
    {
        var randomEnabled = RandomStaticsCheckBox.IsChecked == true;
        var importEnabled = ImportStaticsCheckBox.IsChecked == true;
        LogMissingStaticsData(randomEnabled, importEnabled);

        var message = randomEnabled || importEnabled
            ? "Static data check complete."
            : "Enable Random or Import statics to check data.";
        StatusText.Text = AppStatus.Stamp(message);
        StatusTextPreview.Text = StatusText.Text;
        AppStatus.SetInfo(message);
    }

    private void SetBusy(bool isBusy)
    {
        GenerateButton.IsEnabled = !isBusy;
        CheckDataButton.IsEnabled = !isBusy;
    }

    private void LogMissingStaticsData(bool randomEnabled, bool importEnabled)
    {
        if (randomEnabled)
        {
            var terrainPath = UOMapWeaverDataPaths.TerrainDefinitionsPath;
            if (!File.Exists(terrainPath))
            {
                AppStatus.AppendLog($"Terrain.xml not found: {terrainPath}", AppStatusSeverity.Warning);
            }

            var terrainTypesAvailable = Directory.Exists(UOMapWeaverDataPaths.TerrainTypesRoot) &&
                                        Directory.EnumerateFiles(UOMapWeaverDataPaths.TerrainTypesRoot, "*.xml", SearchOption.AllDirectories).Any();
            if (!terrainTypesAvailable)
            {
                AppStatus.AppendLog($"TerrainTypes XML not found in {UOMapWeaverDataPaths.TerrainTypesRoot}.", AppStatusSeverity.Warning);
            }
        }

        if (importEnabled)
        {
            var importRoot = UOMapWeaverDataPaths.ImportRoot;
            if (!Directory.Exists(importRoot) ||
                !Directory.EnumerateFiles(importRoot, "*.xml", SearchOption.AllDirectories).Any())
            {
                AppStatus.AppendLog($"Import XML not found in {importRoot}.", AppStatusSeverity.Warning);
            }
        }

        if (TransitionStaticsCheckBox.IsChecked == true)
        {
            var terrainPath = TerrainBmpBox.Text;
            if (string.IsNullOrWhiteSpace(terrainPath) || !File.Exists(terrainPath))
            {
                AppStatus.AppendLog("Terrain.bmp not found for transition statics.", AppStatusSeverity.Warning);
            }

            var altitudePath = AltitudeBmpBox.Text;
            if (string.IsNullOrWhiteSpace(altitudePath) || !File.Exists(altitudePath))
            {
                AppStatus.AppendLog("Altitude.bmp not found for transition statics.", AppStatusSeverity.Warning);
            }

            var transitionsRoot = UOMapWeaverDataPaths.TransitionsRoot;
            if (!Directory.Exists(transitionsRoot))
            {
                AppStatus.AppendLog($"Transitions folder not found: {transitionsRoot}", AppStatusSeverity.Warning);
            }
        }
    }

    private Window? GetHostWindow() => VisualRoot as Window;

    private Window GetOwnerWindow()
        => GetHostWindow() ?? throw new InvalidOperationException("Host window not available.");

    public void PersistState()
    {
        SaveState();
    }

    private void LoadState()
    {
        _loadingState = true;
        MapPathBox.Text = AppSettings.GetString("GenStatics.MapPath", MapPathBox.Text ?? string.Empty);
        TerrainBmpBox.Text = AppSettings.GetString("GenStatics.TerrainBmp", TerrainBmpBox.Text ?? string.Empty);
        AltitudeBmpBox.Text = AppSettings.GetString("GenStatics.AltitudeBmp", AltitudeBmpBox.Text ?? string.Empty);
        OutputFolderBox.Text = AppSettings.GetString("GenStatics.OutputFolder", OutputFolderBox.Text ?? string.Empty);
        RandomStaticsCheckBox.IsChecked = AppSettings.GetBool("GenStatics.RandomStatics", RandomStaticsCheckBox.IsChecked == true);
        ImportStaticsCheckBox.IsChecked = AppSettings.GetBool("GenStatics.ImportStatics", ImportStaticsCheckBox.IsChecked == true);
        EmptyStaticsCheckBox.IsChecked = AppSettings.GetBool("GenStatics.EmptyStatics", EmptyStaticsCheckBox.IsChecked == true);
        StopOnErrorCheckBox.IsChecked = AppSettings.GetBool("GenStatics.StopOnError", StopOnErrorCheckBox.IsChecked == true);
        TransitionStaticsCheckBox.IsChecked = AppSettings.GetBool("GenStatics.TransitionStatics", TransitionStaticsCheckBox.IsChecked == true);
        OverrideEnabledCheckBox.IsChecked = AppSettings.GetBool("GenStatics.OverrideEnabled", OverrideEnabledCheckBox.IsChecked == true);

        var overrideList = AppSettings.GetList("GenStatics.OverrideList");
        if (overrideList.Length > 0)
        {
            ApplyOverrideList(overrideList);
        }

        var layoutText = AppSettings.GetString("GenStatics.Layout", string.Empty);
        if (Enum.TryParse<StaticsLayout>(layoutText, out var layout))
        {
            var match = _layoutOptions.FirstOrDefault(option => option.Layout == layout);
            if (match != null)
            {
                StaticsLayoutComboBox.SelectedItem = match;
            }
        }

        _loadingState = false;
    }

    private void SaveState()
    {
        if (_loadingState || !AppSettings.SaveEnabled)
        {
            return;
        }

        AppSettings.SetString("GenStatics.MapPath", MapPathBox.Text ?? string.Empty);
        AppSettings.SetString("GenStatics.TerrainBmp", TerrainBmpBox.Text ?? string.Empty);
        AppSettings.SetString("GenStatics.AltitudeBmp", AltitudeBmpBox.Text ?? string.Empty);
        AppSettings.SetString("GenStatics.OutputFolder", OutputFolderBox.Text ?? string.Empty);
        AppSettings.SetBool("GenStatics.RandomStatics", RandomStaticsCheckBox.IsChecked == true);
        AppSettings.SetBool("GenStatics.ImportStatics", ImportStaticsCheckBox.IsChecked == true);
        AppSettings.SetBool("GenStatics.EmptyStatics", EmptyStaticsCheckBox.IsChecked == true);
        AppSettings.SetBool("GenStatics.StopOnError", StopOnErrorCheckBox.IsChecked == true);
        AppSettings.SetBool("GenStatics.TransitionStatics", TransitionStaticsCheckBox.IsChecked == true);
        AppSettings.SetBool("GenStatics.OverrideEnabled", OverrideEnabledCheckBox.IsChecked == true);
        AppSettings.SetString("GenStatics.Layout", GetSelectedLayout().ToString());
        AppSettings.SetList("GenStatics.OverrideList", SerializeOverrideList());
    }

    private static List<StaticMulEntry>[] CreateEmptyBlocks(int width, int height)
    {
        var blockWidth = width / MapMul.BlockSize;
        var blockHeight = height / MapMul.BlockSize;
        return new List<StaticMulEntry>[blockWidth * blockHeight];
    }

    private static void MergeBlocks(List<StaticMulEntry>[] target, List<StaticMulEntry>[] source)
    {
        for (var i = 0; i < source.Length; i++)
        {
            var list = source[i];
            if (list is null || list.Count == 0)
            {
                continue;
            }

            var targetList = target[i] ??= new List<StaticMulEntry>(list.Count);
            targetList.AddRange(list);
        }
    }

    private StaticsLayout GetSelectedLayout()
        => StaticsLayoutComboBox.SelectedItem is StaticsLayoutOption option
            ? option.Layout
            : StaticsLayout.ColumnMajor;

    private static bool TryResolveStaticsNames(string mapName, out string staticsName, out string staidxName)
    {
        staticsName = string.Empty;
        staidxName = string.Empty;

        if (string.IsNullOrWhiteSpace(mapName))
        {
            return false;
        }

        var lower = mapName.ToLowerInvariant();
        var suffix = string.Empty;
        if (lower.StartsWith("map"))
        {
            suffix = mapName[3..];
        }
        else if (lower.EndsWith("_map"))
        {
            var prefix = mapName[..^4];
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                staticsName = $"{prefix}_statics.mul";
                staidxName = $"{prefix}_staidx.mul";
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(suffix))
        {
            staticsName = $"statics{suffix}.mul";
            staidxName = $"staidx{suffix}.mul";
            return true;
        }

        return false;
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

    public ObservableCollection<BiomeOverrideRow> BiomeOverrides => _biomeOverrides;

    private void LoadBiomeOverrides()
    {
        _biomeOverrides.Clear();
        var placements = StaticPlacementCatalog.LoadStaticDefinitions(new[]
        {
            UOMapWeaverDataPaths.TerrainTypesRoot,
            UOMapWeaverDataPaths.StaticsRoot
        }, out _);

        var available = placements.Keys.OrderBy(name => name).ToList();

        var terrainDefs = StaticPlacementCatalog.LoadTerrainDefinitions(UOMapWeaverDataPaths.TerrainDefinitionsPath, out _);
        foreach (var terrain in terrainDefs.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var defaultName = ResolveDefinitionName(terrain.Name, placements);
            var row = new BiomeOverrideRow(terrain.Name, defaultName, available);
            row.PropertyChanged += OnOverrideChanged;
            _biomeOverrides.Add(row);
        }
    }

    private void OnOverrideChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loadingState)
        {
            return;
        }

        SaveState();
        UpdateStatus();
    }

    private string ResolveDefinitionName(string terrainName, IReadOnlyDictionary<string, StaticPlacementDefinition> placements)
    {
        if (placements.TryGetValue(terrainName, out var def))
        {
            return def.Name;
        }

        var normalized = NormalizeName(terrainName);
        foreach (var name in placements.Keys)
        {
            if (NormalizeName(name).Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        var fallback = NormalizeByKeyword(normalized);
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            foreach (var name in placements.Keys)
            {
                if (NormalizeName(name).Equals(fallback, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }
        }

        return string.Empty;
    }

    private static string NormalizeName(string name)
    {
        var normalized = name.Trim();
        normalized = normalized.Replace("  ", " ");
        normalized = normalized.Replace(" Without Static", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(" without Static", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(" Embankment", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(" (Dark)", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(" (NS)", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(" (EW)", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("Rough ", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("High ", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("Low ", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("Dark ", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("Light ", "", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace("  ", " ");
        return normalized.Trim();
    }

    private static string NormalizeByKeyword(string normalized)
    {
        var lower = normalized.ToLowerInvariant();
        if (lower.Contains("water"))
        {
            return string.Empty;
        }

        if (lower.Contains("grass"))
        {
            return "Grass";
        }

        if (lower.Contains("forest"))
        {
            return "Forest";
        }

        if (lower.Contains("snow"))
        {
            return "Snow";
        }

        if (lower.Contains("sand"))
        {
            return "Sand";
        }

        if (lower.Contains("beach"))
        {
            return "Beach";
        }

        if (lower.Contains("jungle"))
        {
            return "Jungle";
        }

        if (lower.Contains("swamp"))
        {
            return "Swamp";
        }

        if (lower.Contains("furrow"))
        {
            return "Furrows";
        }

        return string.Empty;
    }

    private (bool Enabled, Dictionary<string, string> DefinitionOverrides, Dictionary<string, int> ChanceOverrides) BuildOverrideState()
    {
        var enabled = OverrideEnabledCheckBox.IsChecked == true;
        var definitionOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var chanceOverrides = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in _biomeOverrides)
        {
            if (!string.IsNullOrWhiteSpace(row.SelectedDefinition) &&
                row.AvailableDefinitions.Contains(row.SelectedDefinition))
            {
                definitionOverrides[row.TerrainName] = row.SelectedDefinition!;
            }

            if (int.TryParse(row.ChanceOverride, out var chance))
            {
                chanceOverrides[row.TerrainName] = chance;
            }
        }

        return (enabled, definitionOverrides, chanceOverrides);
    }

    private IEnumerable<string> SerializeOverrideList()
    {
        foreach (var row in _biomeOverrides)
        {
            yield return $"{row.TerrainName}|{row.SelectedDefinition}|{row.ChanceOverride}";
        }
    }

    private void ApplyOverrideList(IEnumerable<string> items)
    {
        var map = _biomeOverrides.ToDictionary(row => row.TerrainName, StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var parts = item.Split('|');
            if (parts.Length < 3)
            {
                continue;
            }

            if (!map.TryGetValue(parts[0], out var row))
            {
                continue;
            }

            row.SelectedDefinition = parts[1];
            row.ChanceOverride = parts[2];
        }
    }

}

public sealed class BiomeOverrideRow : INotifyPropertyChanged
{
    private string _selectedDefinition;
    private string _chanceOverride;

    public BiomeOverrideRow(string terrainName, string defaultDefinition, IReadOnlyList<string> availableDefinitions)
    {
        TerrainName = terrainName;
        DefaultDefinition = string.IsNullOrWhiteSpace(defaultDefinition) ? "(missing)" : defaultDefinition;
        AvailableDefinitions = availableDefinitions;
        _selectedDefinition = string.Empty;
        _chanceOverride = string.Empty;
    }

    public string TerrainName { get; }

    public string DefaultDefinition { get; }

    public IReadOnlyList<string> AvailableDefinitions { get; }

    public string SelectedDefinition
    {
        get => _selectedDefinition;
        set
        {
            if (_selectedDefinition == value)
            {
                return;
            }

            _selectedDefinition = value;
            OnPropertyChanged(nameof(SelectedDefinition));
            OnPropertyChanged(nameof(Status));
        }
    }

    public string ChanceOverride
    {
        get => _chanceOverride;
        set
        {
            if (_chanceOverride == value)
            {
                return;
            }

            _chanceOverride = value;
            OnPropertyChanged(nameof(ChanceOverride));
            OnPropertyChanged(nameof(Status));
        }
    }

    public string Status
    {
        get
        {
            if (DefaultDefinition == "(missing)" && string.IsNullOrWhiteSpace(SelectedDefinition))
            {
                return "Missing XML";
            }

            if (!string.IsNullOrWhiteSpace(SelectedDefinition) &&
                !AvailableDefinitions.Contains(SelectedDefinition))
            {
                return "Override XML not found";
            }

            if (!string.IsNullOrWhiteSpace(ChanceOverride) && !int.TryParse(ChanceOverride, out _))
            {
                return "Chance not numeric";
            }

            return string.Empty;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
