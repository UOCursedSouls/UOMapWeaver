using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using UOMapWeaver.App;
using UOMapWeaver.Core;
using UOMapWeaver.Core.Map;
using UOMapWeaver.Core.TileColors;
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
        LoadState();
        UpdateStatus();
        StopOnErrorCheckBox.IsCheckedChanged += (_, _) => SaveState();
    }

    private async void OnBrowseJson(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => JsonPathBox.Text = await PickSaveFileAsync(this, "Select JSON location", "json");

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

        try
        {
            var builder = new TileColorMapBuilder(mode, existing);
            var mapPaths = _mapPaths.ToList();
            var stopOnError = StopOnErrorCheckBox.IsChecked == true;
            AppStatus.AppendLog($"Tile JSON build start: {JsonPathBox.Text}", AppStatusSeverity.Info);
            var logAction = new Action<MapConversionLogEntry>(entry =>
                Dispatcher.UIThread.Post(() =>
                    AppStatus.AppendLog(entry.Message, MapLogToStatus(entry.Level))));
            var progress = CreateAppProgress();

            var saveAction = new Action<TileColorMap>(map =>
            {
                TileColorMapSerializer.Save(JsonPathBox.Text, map);
            });

            var map = await Task.Run(() =>
                builder.Build(mapPaths, logAction, stopOnError, progress, cancelSource.Token, saveAction),
                cancelSource.Token);

            await Task.Run(() => TileColorMapSerializer.Save(JsonPathBox.Text, map), cancelSource.Token);

            StatusText.Text = $"JSON saved: {Path.GetFileName(JsonPathBox.Text)} ({map.TileToIndex.Count + map.TileToColor.Count} tiles).";
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
    }

    public void PersistState()
    {
        SaveState();
    }

}
