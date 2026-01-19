using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using UOMapWeaver.App;
using UOMapWeaver.Core;
using UOMapWeaver.Core.Map;
using UOMapWeaver.Core.TileColors;
using UOMapWeaver.Core.Statics;
using UOMapWeaver.Core.TileReplace;
using UOMapWeaver.Core.Xml;
using static UOMapWeaver.App.Views.ViewHelpers;
using FieldState = UOMapWeaver.App.Views.ViewHelpers.FieldState;

namespace UOMapWeaver.App.Views;

public sealed partial class TileColorMapView : UserControl, IAppStateView
{
    private readonly ObservableCollection<string> _mapPaths = new();
    private TileColorMode? _lockedMode;
    private bool _loadingState;

    public TileColorMapView()
    {
        InitializeComponent();
        MapListBox.ItemsSource = _mapPaths;
        ModeComboBox.ItemsSource = new[]
        {
            "8-bit Indexed",
            "24-bit RGB"
        };
        ModeComboBox.SelectedIndex = 0;
        JsonPathBox.Text = Path.Combine(UOMapWeaverDataPaths.TileColorsRoot, "UOMapWeaver_TileColors.json");
        ImportOutputBox.Text = UOMapWeaverDataPaths.DataRoot;
        FolderOutputBox.Text = UOMapWeaverDataPaths.DataRoot;
        LoadState();
        UpdateStatus();
        UpdateImportStatus();
        UpdateFolderStatus();
        StopOnErrorCheckBox.IsCheckedChanged += (_, _) => SaveState();
        ImportOverwriteCheckBox.IsCheckedChanged += (_, _) => SaveState();
        FolderOverwriteCheckBox.IsCheckedChanged += (_, _) => SaveState();
        FolderCompactCheckBox.IsCheckedChanged += (_, _) => SaveState();
    }

    private async void OnBrowseJson(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => JsonPathBox.Text = await PickSaveFileAsync(this, "Select JSON location", "json");

    private async void OnBrowseTerrainXml(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => TerrainXmlBox.Text = await PickFileAsync(this, "Select Terrain.xml", new[] { "xml" });

    private async void OnBrowseTerrainTypes(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => TerrainTypesBox.Text = await PickFolderAsync(this, "Select TerrainTypes folder");

    private async void OnBrowseImportOutput(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ImportOutputBox.Text = await PickFolderAsync(this, "Select output folder");

    private async void OnBrowseFolderTransitions(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => FolderTransitionsBox.Text = await PickFolderAsync(this, "Select Transitions folder");

    private async void OnBrowseFolderTemplates(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => FolderTemplatesBox.Text = await PickFolderAsync(this, "Select Templates folder");

    private async void OnBrowseFolderRoughEdge(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => FolderRoughEdgeBox.Text = await PickFolderAsync(this, "Select RoughEdge folder");

    private async void OnBrowseFolderOutput(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => FolderOutputBox.Text = await PickFolderAsync(this, "Select output folder");

    private void OnLoadJson(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(JsonPathBox.Text) || !File.Exists(JsonPathBox.Text))
        {
            StatusText.Text = "JSON file not found.";
            AppStatus.SetError(StatusText.Text);
            return;
        }

        if (new FileInfo(JsonPathBox.Text).Length == 0)
        {
            _lockedMode = null;
            ModeComboBox.IsEnabled = true;
            ModeHintText.Text = string.Empty;
            StatusText.Text = "JSON is empty. Choose a mode and build.";
            AppStatus.SetInfo(StatusText.Text);
            return;
        }

        if (TileColorMapSerializer.TryReadMode(JsonPathBox.Text, out var mode))
        {
            LockMode(mode);
            StatusText.Text = $"Loaded JSON mode: {mode}.";
            AppStatus.SetInfo(StatusText.Text);
        }
        else
        {
            StatusText.Text = "Invalid JSON file.";
            AppStatus.SetError(StatusText.Text);
        }
    }

    private async void OnAddMap(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await PickFilesAsync(this, "Select map.mul files", new[] { "mul", "uop" });
        foreach (var path in files)
        {
            if (!_mapPaths.Contains(path))
            {
                _mapPaths.Add(path);
            }
        }

        UpdateStatus();
        SaveState();
    }

    private void OnRemoveMap(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (MapListBox.SelectedItem is string selected)
        {
            _mapPaths.Remove(selected);
        }

        UpdateStatus();
        SaveState();
    }

    private void OnClearMaps(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _mapPaths.Clear();
        UpdateStatus();
        SaveState();
    }

    private async void OnImportXml(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var terrainXmlPath = TerrainXmlBox.Text?.Trim();
        var terrainTypesPath = TerrainTypesBox.Text?.Trim();
        var outputRoot = ImportOutputBox.Text?.Trim();
        var overwrite = ImportOverwriteCheckBox.IsChecked == true;

        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            ImportStatusText.Text = "Output folder is required.";
            AppStatus.SetError(ImportStatusText.Text);
            UpdateImportStatus();
            return;
        }

        if (string.IsNullOrWhiteSpace(terrainXmlPath) &&
            string.IsNullOrWhiteSpace(terrainTypesPath))
        {
            ImportStatusText.Text = "Select Terrain.xml or a TerrainTypes folder.";
            AppStatus.SetError(ImportStatusText.Text);
            UpdateImportStatus();
            return;
        }

        if (!string.IsNullOrWhiteSpace(terrainXmlPath) && !File.Exists(terrainXmlPath))
        {
            ImportStatusText.Text = "Terrain.xml not found.";
            AppStatus.SetError(ImportStatusText.Text);
            UpdateImportStatus();
            return;
        }

        if (!string.IsNullOrWhiteSpace(terrainTypesPath) && !Directory.Exists(terrainTypesPath))
        {
            ImportStatusText.Text = "TerrainTypes folder not found.";
            AppStatus.SetError(ImportStatusText.Text);
            UpdateImportStatus();
            return;
        }

        AppStatus.AppendLog("XML import started.", AppStatusSeverity.Info);
        AppStatus.SetProgress(0, true);

        try
        {
            var result = await Task.Run(() =>
                ImportXmlData(terrainXmlPath, terrainTypesPath, outputRoot, overwrite));

            ImportStatusText.Text = result;
            AppStatus.SetSuccess(result);
            AppStatus.AppendLog(result, AppStatusSeverity.Success);
        }
        catch (Exception ex)
        {
            ImportStatusText.Text = $"Import error: {ex.Message}";
            AppStatus.SetError(ImportStatusText.Text);
            AppStatus.AppendLog(ImportStatusText.Text, AppStatusSeverity.Error);
        }
        finally
        {
            AppStatus.SetProgress(0, false);
        }

        UpdateImportStatus();
    }

    private async void OnConvertXmlFolders(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var transitionsPath = FolderTransitionsBox.Text?.Trim();
        var templatesPath = FolderTemplatesBox.Text?.Trim();
        var roughEdgePath = FolderRoughEdgeBox.Text?.Trim();
        var outputRoot = FolderOutputBox.Text?.Trim();
        var overwrite = FolderOverwriteCheckBox.IsChecked == true;
        var compact = FolderCompactCheckBox.IsChecked != false;

        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            FolderStatusText.Text = "Output folder is required.";
            AppStatus.SetError(FolderStatusText.Text);
            UpdateFolderStatus();
            return;
        }

        if (string.IsNullOrWhiteSpace(transitionsPath) &&
            string.IsNullOrWhiteSpace(templatesPath) &&
            string.IsNullOrWhiteSpace(roughEdgePath))
        {
            FolderStatusText.Text = "Select at least one XML folder.";
            AppStatus.SetError(FolderStatusText.Text);
            UpdateFolderStatus();
            return;
        }

        if (!string.IsNullOrWhiteSpace(transitionsPath) && !Directory.Exists(transitionsPath))
        {
            FolderStatusText.Text = "Transitions folder not found.";
            AppStatus.SetError(FolderStatusText.Text);
            UpdateFolderStatus();
            return;
        }

        if (!string.IsNullOrWhiteSpace(templatesPath) && !Directory.Exists(templatesPath))
        {
            FolderStatusText.Text = "Templates folder not found.";
            AppStatus.SetError(FolderStatusText.Text);
            UpdateFolderStatus();
            return;
        }

        if (!string.IsNullOrWhiteSpace(roughEdgePath) && !Directory.Exists(roughEdgePath))
        {
            FolderStatusText.Text = "RoughEdge folder not found.";
            AppStatus.SetError(FolderStatusText.Text);
            UpdateFolderStatus();
            return;
        }

        AppStatus.AppendLog("XML folder import started.", AppStatusSeverity.Info);
        AppStatus.SetProgress(0, true);

        try
        {
            var result = await Task.Run(() =>
            {
                var transitionsRoot = Path.Combine(outputRoot, "Transitions");
                var templatesRoot = Path.Combine(outputRoot, "Templates");
                var roughEdgeRoot = Path.Combine(outputRoot, "RoughEdge");

                var transitionsResult = ImportXmlFolder(transitionsPath, transitionsRoot, overwrite, compact);
                var templatesResult = ImportXmlFolder(templatesPath, templatesRoot, overwrite, compact);
                var roughEdgeResult = ImportXmlFolder(roughEdgePath, roughEdgeRoot, overwrite, compact);

                return $"Transitions: {transitionsResult}. Templates: {templatesResult}. RoughEdge: {roughEdgeResult}.";
            });

            FolderStatusText.Text = result;
            AppStatus.SetSuccess(result);
            AppStatus.AppendLog(result, AppStatusSeverity.Success);
        }
        catch (Exception ex)
        {
            FolderStatusText.Text = $"Import error: {ex.Message}";
            AppStatus.SetError(FolderStatusText.Text);
            AppStatus.AppendLog(FolderStatusText.Text, AppStatusSeverity.Error);
        }
        finally
        {
            AppStatus.SetProgress(0, false);
        }

        UpdateFolderStatus();
    }

    private async void OnValidateXmlFolders(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var outputRoot = FolderOutputBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            FolderStatusText.Text = "Output folder is required.";
            AppStatus.SetError(FolderStatusText.Text);
            UpdateFolderStatus();
            return;
        }

        AppStatus.AppendLog("JSON validation started.", AppStatusSeverity.Info);
        AppStatus.SetProgress(0, true);

        try
        {
            var result = await Task.Run(() => ValidateAllJson(outputRoot));
            FolderStatusText.Text = result.Message;
            if (result.Errors > 0)
            {
                AppStatus.SetError(result.Message);
                foreach (var line in result.Samples)
                {
                    AppStatus.AppendLog(line, AppStatusSeverity.Error);
                }
            }
            else
            {
                AppStatus.SetSuccess(result.Message);
            }
        }
        catch (Exception ex)
        {
            FolderStatusText.Text = $"Validation error: {ex.Message}";
            AppStatus.SetError(FolderStatusText.Text);
            AppStatus.AppendLog(FolderStatusText.Text, AppStatusSeverity.Error);
        }
        finally
        {
            AppStatus.SetProgress(0, false);
        }

        UpdateFolderStatus();
    }

    private async void OnBuild(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        using var cancelSource = new CancellationTokenSource();
        AppStatus.SetCancelSource(cancelSource);

        AppStatus.SetProgress(0, true);

        if (string.IsNullOrWhiteSpace(JsonPathBox.Text))
        {
            StatusText.Text = "JSON path is required.";
            AppStatus.SetError(StatusText.Text);
            AppStatus.SetCancelSource(null);
            return;
        }

        if (_mapPaths.Count == 0)
        {
            StatusText.Text = "Add at least one map.mul.";
            AppStatus.SetError(StatusText.Text);
            AppStatus.SetCancelSource(null);
            return;
        }

        var mode = GetSelectedMode();
        TileColorMap? existing = null;

        if (File.Exists(JsonPathBox.Text))
        {
            try
            {
                if (new FileInfo(JsonPathBox.Text).Length > 0)
                {
                    existing = TileColorMapSerializer.Load(JsonPathBox.Text);
                    mode = existing.Mode;
                    LockMode(mode);
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Failed to load JSON: {ex.Message}";
                AppStatus.SetError(StatusText.Text);
                AppStatus.SetCancelSource(null);
                return;
            }
        }

        var jsonPath = JsonPathBox.Text ?? string.Empty;
        var saveLock = new object();
        TileColorMap? lastSaved = null;
        var savePending = false;
        var saveGate = new SemaphoreSlim(1, 1);

        async Task FlushSaveQueueAsync()
        {
            while (true)
            {
                TileColorMap? snapshot;
                lock (saveLock)
                {
                    if (!savePending || lastSaved == null)
                    {
                        savePending = false;
                        return;
                    }

                    snapshot = lastSaved;
                    savePending = false;
                }

                await saveGate.WaitAsync(cancelSource.Token);
                try
                {
                    var folder = Path.GetDirectoryName(jsonPath);
                    if (!string.IsNullOrWhiteSpace(folder))
                    {
                        Directory.CreateDirectory(folder);
                    }

                    await TileColorMapSerializer.SaveAsync(jsonPath, snapshot!, cancelSource.Token);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.Post(() =>
                        AppStatus.AppendLog($"Tile JSON save error: {ex.Message}", AppStatusSeverity.Error));
                }
                finally
                {
                    saveGate.Release();
                }
            }
        }

        try
        {
            var builder = new TileColorMapBuilder(mode, existing);
            var mapPaths = _mapPaths.ToList();
            var stopOnError = StopOnErrorCheckBox.IsChecked == true;
            AppStatus.AppendLog($"Tile JSON build start: {JsonPathBox.Text}", AppStatusSeverity.Info);
            var logAction = new Action<MapConversionLogEntry>(entry =>
                Dispatcher.UIThread.Post(() =>
                    AppStatus.AppendLog(entry.Message, MapLogToStatus(entry.Level))));
            var progress = CreateTileColorProgress();
            var saveAction = new Action<TileColorMap>(map =>
            {
                cancelSource.Token.ThrowIfCancellationRequested();
                lock (saveLock)
                {
                    lastSaved = map;
                    savePending = true;
                }

                _ = Task.Run(FlushSaveQueueAsync, cancelSource.Token);
            });

            var map = await Task.Run(() =>
                builder.Build(mapPaths, logAction, stopOnError, progress, cancelSource.Token, saveAction),
                cancelSource.Token);

            cancelSource.Token.ThrowIfCancellationRequested();
            cancelSource.Token.ThrowIfCancellationRequested();
            var finalFolder = Path.GetDirectoryName(jsonPath);
            if (!string.IsNullOrWhiteSpace(finalFolder))
            {
                Directory.CreateDirectory(finalFolder);
            }
            await TileColorMapSerializer.SaveAsync(jsonPath, map, cancelSource.Token);

            StatusText.Text = $"JSON saved: {Path.GetFileName(jsonPath)} ({map.TileToIndex.Count + map.TileToColor.Count} tiles).";
            AppStatus.SetSuccess(StatusText.Text);
            AppStatus.AppendLog(StatusText.Text, AppStatusSeverity.Success);
        }
        catch (MapConversionAbortException ex)
        {
            StatusText.Text = $"Stopped: {ex.Message}";
            AppStatus.SetError(StatusText.Text);
            AppStatus.AppendLog(StatusText.Text, AppStatusSeverity.Error);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Operation cancelled.";
            AppStatus.SetWarning(StatusText.Text);
            AppStatus.AppendLog(StatusText.Text, AppStatusSeverity.Warning);
            lock (saveLock)
            {
                if (lastSaved != null)
                {
                    TileColorMapSerializer.Save(jsonPath, lastSaved);
                    AppStatus.AppendLog("Partial JSON saved on cancel.", AppStatusSeverity.Warning);
                }
            }
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
        }
    }

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateStatus();
        SaveState();
    }

    private void OnImportTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateImportStatus();
        SaveState();
    }

    private void OnFolderTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateFolderStatus();
        SaveState();
    }

    private async void OnRegenerateDefaults(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        AppStatus.AppendLog("Regenerating defaults...", AppStatusSeverity.Info);
        AppStatus.SetProgress(0, true);

        try
        {
            await Task.Run(() => UOMapWeaverDataBootstrapper.RegenerateDefaults());
            ImportStatusText.Text = "Defaults regenerated.";
            AppStatus.SetSuccess(ImportStatusText.Text);
            AppStatus.AppendLog(ImportStatusText.Text, AppStatusSeverity.Success);
        }
        catch (Exception ex)
        {
            ImportStatusText.Text = $"Regenerate error: {ex.Message}";
            AppStatus.SetError(ImportStatusText.Text);
            AppStatus.AppendLog(ImportStatusText.Text, AppStatusSeverity.Error);
        }
        finally
        {
            AppStatus.SetProgress(0, false);
        }
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateStatus();
        SaveState();
    }

    private void UpdateStatus()
    {
        var jsonPath = JsonPathBox.Text ?? string.Empty;
        var jsonExists = !string.IsNullOrWhiteSpace(jsonPath) && File.Exists(jsonPath);
        var jsonValidMode = false;
        TileColorMode detectedMode = default;

        if (jsonExists)
        {
            var info = new FileInfo(jsonPath);
            if (info.Length > 0 && TileColorMapSerializer.TryReadMode(jsonPath, out detectedMode))
            {
                jsonValidMode = true;
            }
        }

        if (jsonValidMode)
        {
            LockMode(detectedMode);
            JsonInfoText.Text = $"JSON: {jsonPath}";
        }
        else
        {
            _lockedMode = null;
            ModeHintText.Text = string.Empty;
            ModeComboBox.IsEnabled = true;
            JsonInfoText.Text = string.IsNullOrWhiteSpace(jsonPath)
                ? string.Empty
                : $"JSON: {jsonPath}";
        }

        var jsonState = FieldState.Valid;
        if (string.IsNullOrWhiteSpace(jsonPath))
        {
            jsonState = FieldState.Error;
        }
        else if (jsonExists && new FileInfo(jsonPath).Length > 0 && !jsonValidMode)
        {
            jsonState = FieldState.Error;
        }

        SetFieldState(JsonPathBox, jsonState);
        SetFieldState(MapListBox, _mapPaths.Count > 0 ? FieldState.Valid : FieldState.Error);
    }

    private void UpdateImportStatus()
    {
        var outputRoot = ImportOutputBox.Text ?? string.Empty;
        var terrainXmlPath = TerrainXmlBox.Text ?? string.Empty;
        var terrainTypesPath = TerrainTypesBox.Text ?? string.Empty;
        var definitionsPath = string.IsNullOrWhiteSpace(outputRoot)
            ? "Definitions/terrain-definitions.json"
            : Path.Combine(outputRoot, "Definitions", "terrain-definitions.json");

        var staticsPath = string.IsNullOrWhiteSpace(outputRoot)
            ? "Statics/*.json"
            : Path.Combine(outputRoot, "Statics", "*.json");

        ImportInfoText.Text = $"Output: {definitionsPath} | {staticsPath}";

        var outputState = string.IsNullOrWhiteSpace(outputRoot) ? FieldState.Error : FieldState.Valid;
        SetFieldState(ImportOutputBox, outputState);

        var terrainState = FieldState.Neutral;
        if (!string.IsNullOrWhiteSpace(terrainXmlPath))
        {
            terrainState = File.Exists(terrainXmlPath) ? FieldState.Valid : FieldState.Error;
        }

        var typesState = FieldState.Neutral;
        if (!string.IsNullOrWhiteSpace(terrainTypesPath))
        {
            typesState = Directory.Exists(terrainTypesPath) ? FieldState.Valid : FieldState.Error;
        }

        SetFieldState(TerrainXmlBox, terrainState, isOptional: true);
        SetFieldState(TerrainTypesBox, typesState, isOptional: true);
    }

    private void UpdateFolderStatus()
    {
        var outputRoot = FolderOutputBox.Text ?? string.Empty;
        var transitionsPath = FolderTransitionsBox.Text ?? string.Empty;
        var templatesPath = FolderTemplatesBox.Text ?? string.Empty;
        var roughEdgePath = FolderRoughEdgeBox.Text ?? string.Empty;

        var transitionsOutput = string.IsNullOrWhiteSpace(outputRoot)
            ? "Transitions/*.json"
            : Path.Combine(outputRoot, "Transitions", "*.json");

        var templatesOutput = string.IsNullOrWhiteSpace(outputRoot)
            ? "Templates/*.json"
            : Path.Combine(outputRoot, "Templates", "*.json");

        var roughEdgeOutput = string.IsNullOrWhiteSpace(outputRoot)
            ? "RoughEdge/*.json"
            : Path.Combine(outputRoot, "RoughEdge", "*.json");

        FolderInfoText.Text = $"Output: {transitionsOutput} | {templatesOutput} | {roughEdgeOutput}";

        SetFieldState(FolderOutputBox, string.IsNullOrWhiteSpace(outputRoot) ? FieldState.Error : FieldState.Valid);
        SetFieldState(FolderTransitionsBox,
            string.IsNullOrWhiteSpace(transitionsPath)
                ? FieldState.Neutral
                : Directory.Exists(transitionsPath) ? FieldState.Valid : FieldState.Error,
            isOptional: true);
        SetFieldState(FolderTemplatesBox,
            string.IsNullOrWhiteSpace(templatesPath)
                ? FieldState.Neutral
                : Directory.Exists(templatesPath) ? FieldState.Valid : FieldState.Error,
            isOptional: true);
        SetFieldState(FolderRoughEdgeBox,
            string.IsNullOrWhiteSpace(roughEdgePath)
                ? FieldState.Neutral
                : Directory.Exists(roughEdgePath) ? FieldState.Valid : FieldState.Error,
            isOptional: true);
    }

    private void LockMode(TileColorMode mode)
    {
        _lockedMode = mode;
        ModeComboBox.SelectedIndex = mode == TileColorMode.Indexed8 ? 0 : 1;
        ModeComboBox.IsEnabled = false;
        ModeHintText.Text = "Locked by existing JSON.";
    }

    private TileColorMode GetSelectedMode()
    {
        if (_lockedMode.HasValue)
        {
            return _lockedMode.Value;
        }

        return ModeComboBox.SelectedIndex == 1 ? TileColorMode.Rgb24 : TileColorMode.Indexed8;
    }

    private void LoadState()
    {
        _loadingState = true;

        JsonPathBox.Text = AppSettings.GetString("TileColors.JsonPath", JsonPathBox.Text ?? string.Empty);
        StopOnErrorCheckBox.IsChecked = AppSettings.GetBool("TileColors.StopOnError", StopOnErrorCheckBox.IsChecked == true);
        TerrainXmlBox.Text = AppSettings.GetString("TileColors.ImportTerrainXml", TerrainXmlBox.Text ?? string.Empty);
        TerrainTypesBox.Text = AppSettings.GetString("TileColors.ImportTerrainTypes", TerrainTypesBox.Text ?? string.Empty);
        ImportOutputBox.Text = AppSettings.GetString("TileColors.ImportOutput", ImportOutputBox.Text ?? string.Empty);
        ImportOverwriteCheckBox.IsChecked = AppSettings.GetBool("TileColors.ImportOverwrite", ImportOverwriteCheckBox.IsChecked == true);
        FolderTransitionsBox.Text = AppSettings.GetString("TileColors.FolderTransitions", FolderTransitionsBox.Text ?? string.Empty);
        FolderTemplatesBox.Text = AppSettings.GetString("TileColors.FolderTemplates", FolderTemplatesBox.Text ?? string.Empty);
        FolderRoughEdgeBox.Text = AppSettings.GetString("TileColors.FolderRoughEdge", FolderRoughEdgeBox.Text ?? string.Empty);
        FolderOutputBox.Text = AppSettings.GetString("TileColors.FolderOutput", FolderOutputBox.Text ?? string.Empty);
        FolderOverwriteCheckBox.IsChecked = AppSettings.GetBool("TileColors.FolderOverwrite", FolderOverwriteCheckBox.IsChecked == true);
        FolderCompactCheckBox.IsChecked = AppSettings.GetBool("TileColors.FolderCompact", FolderCompactCheckBox.IsChecked != false);

        var modeIndex = AppSettings.GetInt("TileColors.ModeIndex", ModeComboBox.SelectedIndex);
        if (modeIndex >= 0 && modeIndex < ModeComboBox.ItemCount)
        {
            ModeComboBox.SelectedIndex = modeIndex;
        }

        _mapPaths.Clear();
        foreach (var path in AppSettings.GetList("TileColors.MapPaths"))
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                _mapPaths.Add(path);
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

        AppSettings.SetString("TileColors.JsonPath", JsonPathBox.Text ?? string.Empty);
        AppSettings.SetBool("TileColors.StopOnError", StopOnErrorCheckBox.IsChecked == true);
        AppSettings.SetInt("TileColors.ModeIndex", ModeComboBox.SelectedIndex);
        AppSettings.SetList("TileColors.MapPaths", _mapPaths);
        AppSettings.SetString("TileColors.ImportTerrainXml", TerrainXmlBox.Text ?? string.Empty);
        AppSettings.SetString("TileColors.ImportTerrainTypes", TerrainTypesBox.Text ?? string.Empty);
        AppSettings.SetString("TileColors.ImportOutput", ImportOutputBox.Text ?? string.Empty);
        AppSettings.SetBool("TileColors.ImportOverwrite", ImportOverwriteCheckBox.IsChecked == true);
        AppSettings.SetString("TileColors.FolderTransitions", FolderTransitionsBox.Text ?? string.Empty);
        AppSettings.SetString("TileColors.FolderTemplates", FolderTemplatesBox.Text ?? string.Empty);
        AppSettings.SetString("TileColors.FolderRoughEdge", FolderRoughEdgeBox.Text ?? string.Empty);
        AppSettings.SetString("TileColors.FolderOutput", FolderOutputBox.Text ?? string.Empty);
        AppSettings.SetBool("TileColors.FolderOverwrite", FolderOverwriteCheckBox.IsChecked == true);
        AppSettings.SetBool("TileColors.FolderCompact", FolderCompactCheckBox.IsChecked != false);
    }

    public void PersistState()
    {
        SaveState();
    }

    private static string ImportXmlData(
        string? terrainXmlPath,
        string? terrainTypesPath,
        string outputRoot,
        bool overwrite)
    {
        var definitionsRoot = Path.Combine(outputRoot, "Definitions");
        var staticsRoot = Path.Combine(outputRoot, "Statics");
        Directory.CreateDirectory(definitionsRoot);
        Directory.CreateDirectory(staticsRoot);

        var terrainCount = 0;
        var terrainSkipped = 0;

        if (!string.IsNullOrWhiteSpace(terrainXmlPath))
        {
            var terrainRecords = StaticPlacementXmlImporter.LoadTerrainRecordsFromXml(terrainXmlPath);
            var terrainJsonPath = Path.Combine(definitionsRoot, "terrain-definitions.json");
            if (!overwrite && File.Exists(terrainJsonPath))
            {
                terrainSkipped = terrainRecords.Count;
            }
            else
            {
                StaticPlacementJson.SaveTerrainRecords(terrainJsonPath, terrainRecords);
                terrainCount = terrainRecords.Count;
            }
        }

        var staticCount = 0;
        var staticSkipped = 0;
        var staticFailed = 0;

        if (!string.IsNullOrWhiteSpace(terrainTypesPath))
        {
            foreach (var xmlPath in Directory.EnumerateFiles(terrainTypesPath, "*.xml", SearchOption.AllDirectories))
            {
                var definition = StaticPlacementXmlImporter.LoadStaticDefinitionFromXml(xmlPath);
                if (definition is null)
                {
                    staticFailed++;
                    continue;
                }

                var relative = Path.GetRelativePath(terrainTypesPath, xmlPath);
                var jsonPath = Path.Combine(staticsRoot, Path.ChangeExtension(relative, ".json"));
                Directory.CreateDirectory(Path.GetDirectoryName(jsonPath) ?? staticsRoot);

                if (!overwrite && File.Exists(jsonPath))
                {
                    staticSkipped++;
                    continue;
                }

                StaticPlacementJson.SaveStaticDefinition(jsonPath, definition);
                staticCount++;
            }
        }

        return $"Imported terrain: {terrainCount} (skipped {terrainSkipped}). " +
               $"Static definitions: {staticCount} (skipped {staticSkipped}, failed {staticFailed}).";
    }

    private static string ImportXmlFolder(string? sourceRoot, string targetRoot, bool overwrite, bool compact)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
        {
            return "0";
        }

        var imported = 0;
        var skipped = 0;

        foreach (var xmlPath in Directory.EnumerateFiles(sourceRoot, "*.xml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, xmlPath);
            var jsonPath = Path.Combine(targetRoot, Path.ChangeExtension(relative, ".json"));
            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath) ?? targetRoot);

            if (!overwrite && File.Exists(jsonPath))
            {
                skipped++;
                continue;
            }

            var xml = File.ReadAllText(xmlPath);
            var json = compact
                ? XmlJsonCompactConverter.ConvertToJson(xml)
                : XmlJsonConverter.ConvertToJson(xml);
            File.WriteAllText(jsonPath, json);
            imported++;
        }

        return $"{imported} (skipped {skipped})";
    }

    private static (string Message, int Errors, string[] Samples) ValidateXmlJsonFolders(string outputRoot)
    {
        var roots = new[]
        {
            Path.Combine(outputRoot, "Transitions"),
            Path.Combine(outputRoot, "Templates"),
            Path.Combine(outputRoot, "RoughEdge")
        };

        var files = roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
            .ToList();

        if (files.Count == 0)
        {
            return ("XML folders: no JSON files found.", 0, Array.Empty<string>());
        }

        var errors = 0;
        var samples = new List<string>();

        foreach (var path in files)
        {
            var issue = ValidateXmlJsonFile(path);
            if (issue is null)
            {
                continue;
            }

            errors++;
            if (samples.Count < 5)
            {
                samples.Add($"{Path.GetFileName(path)}: {issue}");
            }
        }

        var message = $"Validated {files.Count} JSON files. Errors: {errors}.";
        return ($"XML folders: {message}", errors, samples.ToArray());
    }

    private static string? ValidateXmlJsonFile(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("root", out var root) || root.ValueKind != JsonValueKind.Object)
            {
                return "Missing root object.";
            }

            if (root.TryGetProperty("kind", out _))
            {
                return ValidateXmlJsonNode(root);
            }

            return ValidateXmlCompactNode(root);
        }
        catch (Exception ex)
        {
            return $"Invalid JSON: {ex.Message}";
        }
    }

    private static string? ValidateXmlJsonNode(JsonElement node)
    {
        var hasKind = node.TryGetProperty("kind", out var kindProp);
        var hasName = node.TryGetProperty("name", out var nameProp);
        if (hasKind)
        {
            if (kindProp.ValueKind != JsonValueKind.String)
            {
                return "kind must be a string.";
            }
        }
        else if (!hasName || nameProp.ValueKind != JsonValueKind.String)
        {
            return "element node must have name.";
        }

        if (node.TryGetProperty("attributes", out var attrs))
        {
            if (attrs.ValueKind != JsonValueKind.Object)
            {
                return "attributes must be an object.";
            }

            foreach (var prop in attrs.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String)
                {
                    return "attribute values must be strings.";
                }
            }
        }

        if (node.TryGetProperty("children", out var children))
        {
            if (children.ValueKind != JsonValueKind.Array)
            {
                return "children must be an array.";
            }

            foreach (var child in children.EnumerateArray())
            {
                if (child.ValueKind != JsonValueKind.Object)
                {
                    return "child node must be an object.";
                }

                var error = ValidateXmlJsonNode(child);
                if (error is not null)
                {
                    return error;
                }
            }
        }

        return null;
    }

    private static string? ValidateXmlCompactNode(JsonElement node)
    {
        var hasName = TryGetProperty(node, "name", out var nameProp);
        var hasText = TryGetProperty(node, "text", out var textProp);
        var hasDocType = TryGetProperty(node, "docType", out var docTypeProp);
        var hasUnknown = TryGetProperty(node, "unknown", out var unknownProp);
        var hasPiTarget = TryGetProperty(node, "processingInstructionTarget", out var piTargetProp);
        var hasPiData = TryGetProperty(node, "processingInstructionData", out var piDataProp);

        if (hasName)
        {
            if (nameProp.ValueKind != JsonValueKind.String)
            {
                return "name must be a string.";
            }
        }
        else if (!hasText && !hasDocType && !hasUnknown && !hasPiTarget && !hasPiData)
        {
            return "node must have name or text.";
        }

        if (hasText && textProp.ValueKind != JsonValueKind.String)
        {
            return "text must be a string.";
        }

        if (hasDocType && docTypeProp.ValueKind != JsonValueKind.String)
        {
            return "docType must be a string.";
        }

        if (hasUnknown && unknownProp.ValueKind != JsonValueKind.String)
        {
            return "unknown must be a string.";
        }

        if (hasPiTarget && piTargetProp.ValueKind != JsonValueKind.String)
        {
            return "processingInstructionTarget must be a string.";
        }

        if (hasPiData && piDataProp.ValueKind != JsonValueKind.String)
        {
            return "processingInstructionData must be a string.";
        }

        if (node.TryGetProperty("attributes", out var attrs))
        {
            if (attrs.ValueKind != JsonValueKind.Object)
            {
                return "attributes must be an object.";
            }

            foreach (var prop in attrs.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.String)
                {
                    return "attribute values must be strings.";
                }
            }
        }

        if (node.TryGetProperty("children", out var children))
        {
            if (children.ValueKind != JsonValueKind.Array)
            {
                return "children must be an array.";
            }

            foreach (var child in children.EnumerateArray())
            {
                if (child.ValueKind != JsonValueKind.Object)
                {
                    return "child node must be an object.";
                }

                var error = ValidateXmlCompactNode(child);
                if (error is not null)
                {
                    return error;
                }
            }
        }

        return null;
    }

    private static (string Message, int Errors, string[] Samples) ValidateOtherJson(string outputRoot)
    {
        var errors = 0;
        var samples = new List<string>();

        var definitionIssues = ValidateJsonFile(
            Path.Combine(outputRoot, "Definitions", "terrain-definitions.json"),
            ValidateTerrainDefinitions,
            new[]
            {
                Path.Combine(outputRoot, "Definitions", "terrain-definitions.xml"),
                Path.Combine(outputRoot, "Definitions", "Terrain.xml")
            });
        AddIssues("terrain-definitions.json", definitionIssues, ref errors, samples);

        var mapDefIssues = ValidateJsonFile(Path.Combine(outputRoot, "Definitions", "map-definitions.json"), ValidateMapDefinitions);
        AddIssues("map-definitions.json", mapDefIssues, ref errors, samples);

        var presetsIssues = ValidateJsonFile(Path.Combine(outputRoot, "Presets", "map-presets.json"), ValidateMapPresets);
        AddIssues("map-presets.json", presetsIssues, ref errors, samples);

        var tileColorsIssues = ValidateJsonFolder(Path.Combine(outputRoot, "TileColors"), ValidateTileColors);
        AddIssues("tile colors", tileColorsIssues, ref errors, samples);

        var tileReplaceIssues = ValidateJsonFolder(Path.Combine(outputRoot, "JsonTileReplace"), ValidateTileReplace);
        AddIssues("tile replace", tileReplaceIssues, ref errors, samples);

        var staticsIssues = ValidateJsonFolder(Path.Combine(outputRoot, "Statics"), ValidateStaticDefinition, "xml");
        AddIssues("statics", staticsIssues, ref errors, samples);

        var mapTransIssues = ValidateJsonFolder(Path.Combine(outputRoot, "System", "MapTrans"), ValidateMapTrans, "txt");
        AddIssues("map trans", mapTransIssues, ref errors, samples);

        var message = $"Other JSON: Errors {errors}.";
        return (message, errors, samples.Take(10).ToArray());
    }

    private static (string Message, int Errors, string[] Samples) ValidateAllJson(string outputRoot)
    {
        var xmlResult = ValidateXmlJsonFolders(outputRoot);
        var otherResult = ValidateOtherJson(outputRoot);

        var errors = xmlResult.Errors + otherResult.Errors;
        var samples = xmlResult.Samples.Concat(otherResult.Samples).Take(10).ToArray();
        var message = $"{xmlResult.Message} {otherResult.Message}";

        return (message, errors, samples);
    }

    private static List<string> ValidateJsonFile(string path, Func<JsonElement, string?> validator, string[]? alternates = null)
    {
        if (!File.Exists(path))
        {
            if (alternates != null && alternates.Any(File.Exists))
            {
                return new List<string>();
            }

            return new List<string> { "File not found." };
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var issue = validator(doc.RootElement);
            return issue is null ? new List<string>() : new List<string> { issue };
        }
        catch (Exception ex)
        {
            return new List<string> { $"Invalid JSON: {ex.Message}" };
        }
    }

    private static List<string> ValidateJsonFolder(string folder, Func<JsonElement, string?> validator, string? alternateExtension = null)
    {
        if (!Directory.Exists(folder))
        {
            return new List<string>();
        }

        var issues = new List<string>();
        var jsonFiles = Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories).ToList();
        if (jsonFiles.Count == 0 && !string.IsNullOrWhiteSpace(alternateExtension))
        {
            var altFiles = Directory.EnumerateFiles(folder, $"*.{alternateExtension}", SearchOption.AllDirectories).ToList();
            if (altFiles.Count > 0)
            {
                return issues;
            }
        }

        foreach (var path in jsonFiles)
        {
            var fileIssues = ValidateJsonFile(path, validator);
            foreach (var issue in fileIssues)
            {
                issues.Add($"{Path.GetFileName(path)}: {issue}");
            }
        }

        return issues;
    }

    private static void AddIssues(string label, List<string> issues, ref int errors, List<string> samples)
    {
        if (issues.Count == 0)
        {
            return;
        }

        errors += issues.Count;
        foreach (var issue in issues.Take(5))
        {
            samples.Add($"{label}: {issue}");
        }
    }

    private static string? ValidateTerrainDefinitions(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return "Expected an array.";
        }

        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                return "Entry must be an object.";
            }

            if (!TryGetProperty(entry, "name", out var name) || name.ValueKind != JsonValueKind.String)
            {
                return "Entry missing name.";
            }

            if (!TryGetProperty(entry, "tileId", out var tileId) || tileId.ValueKind != JsonValueKind.Number)
            {
                return "Entry missing tileId.";
            }
        }

        return null;
    }

    private static string? ValidateMapDefinitions(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return "Expected an array.";
        }

        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                return "Entry must be an object.";
            }

            if (!TryGetProperty(entry, "name", out var name) || name.ValueKind != JsonValueKind.String)
            {
                return "Entry missing name.";
            }

            if (!TryGetProperty(entry, "width", out var width) || width.ValueKind != JsonValueKind.Number)
            {
                return "Entry missing width.";
            }

            if (!TryGetProperty(entry, "height", out var height) || height.ValueKind != JsonValueKind.Number)
            {
                return "Entry missing height.";
            }
        }

        return null;
    }

    private static string? ValidateMapPresets(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            return "Expected an array.";
        }

        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                return "Entry must be an object.";
            }

            if (!TryGetProperty(entry, "name", out var name) || name.ValueKind != JsonValueKind.String)
            {
                return "Entry missing name.";
            }

            if (TryGetProperty(entry, "width", out var width) && width.ValueKind != JsonValueKind.Number)
            {
                return "width must be a number.";
            }

            if (TryGetProperty(entry, "height", out var height) && height.ValueKind != JsonValueKind.Number)
            {
                return "height must be a number.";
            }

            if (TryGetProperty(entry, "isSeparator", out var separator) &&
                separator.ValueKind != JsonValueKind.True && separator.ValueKind != JsonValueKind.False)
            {
                return "isSeparator must be boolean.";
            }
        }

        return null;
    }

    private static string? ValidateStaticDefinition(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return "Expected an object.";
        }

        if (!TryGetProperty(root, "chance", out var chance) || chance.ValueKind != JsonValueKind.Number)
        {
            return "Missing chance.";
        }

        if (!TryGetProperty(root, "groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
        {
            return "Missing groups array.";
        }

        foreach (var group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object)
            {
                return "Group must be an object.";
            }

            if (!TryGetProperty(group, "weight", out var weight) || weight.ValueKind != JsonValueKind.Number)
            {
                return "Group missing weight.";
            }

            if (!TryGetProperty(group, "items", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return "Group missing items.";
            }

            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    return "Item must be an object.";
                }

                if (!TryGetProperty(item, "tileId", out var tileId) || tileId.ValueKind != JsonValueKind.Number)
                {
                    return "Item missing tileId.";
                }

                if (!TryGetProperty(item, "x", out var x) || x.ValueKind != JsonValueKind.Number ||
                    !TryGetProperty(item, "y", out var y) || y.ValueKind != JsonValueKind.Number)
                {
                    return "Item missing x/y.";
                }

                if (!TryGetProperty(item, "z", out var z) || z.ValueKind != JsonValueKind.Number)
                {
                    return "Item missing z.";
                }

                if (!TryGetProperty(item, "hue", out var hue) || hue.ValueKind != JsonValueKind.Number)
                {
                    return "Item missing hue.";
                }
            }
        }

        return null;
    }

    private static string? ValidateTileColors(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return "Expected an object.";
        }

        if (!TryGetProperty(root, "mode", out var mode) || mode.ValueKind != JsonValueKind.String)
        {
            return "Missing mode.";
        }

        if (!TryGetProperty(root, "unknownColor", out var unknown) || unknown.ValueKind != JsonValueKind.String)
        {
            return "Missing unknownColor.";
        }

        if (!RgbColor.TryParse(unknown.GetString() ?? string.Empty, out _))
        {
            return "unknownColor is invalid.";
        }

        if (!TryGetProperty(root, "tiles", out var tiles) || tiles.ValueKind != JsonValueKind.Object)
        {
            return "Missing tiles object.";
        }

        var isRgb = string.Equals(mode.GetString(), "Rgb24", StringComparison.OrdinalIgnoreCase);
        foreach (var prop in tiles.EnumerateObject())
        {
            if (!TryParseTileId(prop.Name, out _))
            {
                return "Invalid tile id key.";
            }

            if (prop.Value.ValueKind != JsonValueKind.String)
            {
                return "Tile value must be a string.";
            }

            if (isRgb)
            {
                if (!RgbColor.TryParse(prop.Value.GetString() ?? string.Empty, out _))
                {
                    return "Tile color value invalid.";
                }
            }
            else if (!byte.TryParse(prop.Value.GetString(), out _))
            {
                return "Tile index value invalid.";
            }
        }

        if (!isRgb && TryGetProperty(root, "palette", out var palette))
        {
            if (palette.ValueKind != JsonValueKind.Array)
            {
                return "palette must be an array.";
            }

            foreach (var entry in palette.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String ||
                    !RgbColor.TryParse(entry.GetString() ?? string.Empty, out _))
                {
                    return "palette entry invalid.";
                }
            }
        }

        return null;
    }

    private static string? ValidateTileReplace(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return "Expected an object.";
        }

        if (TryGetProperty(root, "terrain", out var terrain))
        {
            var issue = ValidateTileReplaceDict(terrain);
            if (issue is not null)
            {
                return issue;
            }
        }

        if (TryGetProperty(root, "statics", out var statics))
        {
            var issue = ValidateTileReplaceDict(statics);
            if (issue is not null)
            {
                return issue;
            }
        }

        return null;
    }

    private static string? ValidateMapTrans(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return "Expected an object.";
        }

        if (TryGetProperty(root, "paletteFile", out var paletteFile) &&
            paletteFile.ValueKind != JsonValueKind.String)
        {
            return "paletteFile must be a string.";
        }

        if (!TryGetProperty(root, "entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return "Missing entries array.";
        }

        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                return "Entry must be an object.";
            }

            if (!TryGetProperty(entry, "colorIndex", out var colorIndex) || colorIndex.ValueKind != JsonValueKind.Number)
            {
                return "Entry missing colorIndex.";
            }

            if (!TryGetProperty(entry, "altitude", out var altitude) || altitude.ValueKind != JsonValueKind.Number)
            {
                return "Entry missing altitude.";
            }

            if (TryGetProperty(entry, "group", out var group) && group.ValueKind != JsonValueKind.Number)
            {
                return "group must be a number.";
            }

            if (!TryGetProperty(entry, "tileIds", out var tileIds) || tileIds.ValueKind != JsonValueKind.Array)
            {
                return "Entry missing tileIds.";
            }

            foreach (var tileId in tileIds.EnumerateArray())
            {
                if (tileId.ValueKind != JsonValueKind.Number)
                {
                    return "tileIds must be numbers.";
                }
            }
        }

        return null;
    }

    private static string? ValidateTileReplaceDict(JsonElement dict)
    {
        if (dict.ValueKind != JsonValueKind.Object)
        {
            return "replacement map must be an object.";
        }

        foreach (var prop in dict.EnumerateObject())
        {
            if (!TryParseTileId(prop.Name, out _))
            {
                return "invalid tile id key.";
            }

            if (prop.Value.ValueKind != JsonValueKind.String ||
                !TryParseTileId(prop.Value.GetString() ?? string.Empty, out _))
            {
                return "invalid tile id value.";
            }
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var prop in element.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryParseTileId(string text, out ushort tileId)
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

}
