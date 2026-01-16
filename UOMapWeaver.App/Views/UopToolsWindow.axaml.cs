using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using UOMapWeaver.App;
using UOMapWeaver.Core.Uop;

namespace UOMapWeaver.App.Views;

public sealed partial class UopToolsView : UserControl, IAppStateView
{
    private readonly ObservableCollection<string> _packInputs = new();
    private bool _loadingState;
    private readonly ObservableCollection<UopModeOption> _packModes = new();
    private readonly ObservableCollection<string> _packNamePresets = new();
    private readonly ObservableCollection<ExtractTypeOption> _extractTypes = new();

    public UopToolsView()
    {
        InitializeComponent();
        PackInputList.ItemsSource = _packInputs;
        LoadExtractMappings();
        LoadExtractTypes();
        LoadModes();
        LoadPresets();
        LoadState();
        UpdateExtractState();
        UpdatePackState();
    }

    private async void OnBrowseExtractUop(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ExtractUopPathBox.Text = await PickFileAsync("Select UOP file", new[] { "uop" });

    private async void OnBrowseExtractOutputFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ExtractOutputFolderBox.Text = await PickFolderAsync("Select output folder");

    private async void OnBrowsePackTemplate(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => PackTemplatePathBox.Text = await PickFileAsync("Select template UOP", new[] { "uop" });

    private async void OnBrowsePackOutputFolder(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => PackOutputFolderBox.Text = await PickFolderAsync("Select output folder");

    private async void OnAddPackInputs(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var files = await PickFilesAsync("Select input files", new[] { "mul", "dat", "bin" });
        foreach (var file in files)
        {
            if (!_packInputs.Contains(file))
            {
                _packInputs.Add(file);
            }
        }

        UpdatePackState();
        SaveState();
    }

    private void OnRemovePackInput(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var selected = PackInputList.SelectedItems?.OfType<string>().ToList();
        if (selected == null || selected.Count == 0)
        {
            return;
        }

        foreach (var item in selected)
        {
            _packInputs.Remove(item);
        }

        UpdatePackState();
        SaveState();
    }

    private void OnExtractTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateExtractState();
        SaveState();
    }

    private void OnPackOptionChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdatePackState();
        SaveState();
    }

    private void OnExtractTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateExtractState();
        SaveState();
    }

    private void OnPackPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loadingState)
        {
            return;
        }

        if (PackPresetNameComboBox.SelectedItem is string preset && !string.IsNullOrWhiteSpace(preset))
        {
            PackOutputNameBox.Text = preset;
        }

        UpdatePackState();
        SaveState();
    }

    private void OnAutoExtractName(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var guess = GuessBaseNameFromUop(ExtractUopPathBox.Text ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(guess))
        {
            ExtractBaseNameBox.Text = guess;
        }
    }

    private void OnAutoPackName(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var guess = GuessUopNameFromInputs(_packInputs);
        if (!string.IsNullOrWhiteSpace(guess))
        {
            PackOutputNameBox.Text = guess;
        }
    }

    private void OnPackTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdatePackState();
        SaveState();
    }

    private async void OnExtract(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var uopPath = ExtractUopPathBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(uopPath) || !File.Exists(uopPath))
        {
            SetStatus("UOP file not found.", AppStatusSeverity.Error);
            return;
        }

        var outputFolder = ExtractOutputFolderBox.Text?.Trim() ?? string.Empty;
        var prefix = ExtractBaseNameBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(outputFolder))
        {
            SetStatus("Output folder is required.", AppStatusSeverity.Error);
            return;
        }

        SetBusy(true);
        using var cancelSource = new CancellationTokenSource();
        AppStatus.SetCancelSource(cancelSource);

        try
        {
            AppStatus.SetProgress(0, true);
            AppStatus.AppendLog($"UOP extract start: {uopPath}", AppStatusSeverity.Info);

            var selected = GetSelectedExtractType();
            var progress = new Progress<int>(percent => AppStatus.SetProgress(percent, true));
            await Task.Run(() =>
            {
                Directory.CreateDirectory(outputFolder);
                ExtractByUopName(uopPath, outputFolder, prefix, selected, message => AppStatus.AppendLog(message, AppStatusSeverity.Info),
                    progress, cancelSource.Token);
            }, cancelSource.Token);

            SetStatus("UOP extracted.", AppStatusSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Extraction cancelled.", AppStatusSeverity.Warning);
        }
        catch (Exception ex)
        {
            SetStatus($"Extract error: {ex.Message}", AppStatusSeverity.Error);
        }
        finally
        {
            AppStatus.SetProgress(0, false);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
        }
    }

    private async void OnPack(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var templatePath = PackTemplatePathBox.Text?.Trim() ?? string.Empty;
        var outputFolder = PackOutputFolderBox.Text?.Trim() ?? string.Empty;
        var outputName = PackOutputNameBox.Text?.Trim() ?? string.Empty;
        var mode = GetSelectedPackMode();
        if (string.IsNullOrWhiteSpace(outputFolder) || !Directory.Exists(outputFolder))
        {
            SetStatus("Output folder is required.", AppStatusSeverity.Error);
            return;
        }

        if (_packInputs.Count == 0)
        {
            SetStatus("Add at least one input file.", AppStatusSeverity.Error);
            return;
        }

        if (string.IsNullOrWhiteSpace(outputName))
        {
            SetStatus("Output name is required.", AppStatusSeverity.Error);
            return;
        }

        var outputPath = Path.Combine(outputFolder, EnsureUopExtension(outputName));

        var chunkSize = 0;
        if (!string.IsNullOrWhiteSpace(PackChunkSizeBox.Text) &&
            !int.TryParse(PackChunkSizeBox.Text, out chunkSize))
        {
            SetStatus("Chunk size must be a number.", AppStatusSeverity.Error);
            return;
        }

        SetBusy(true);
        using var cancelSource = new CancellationTokenSource();
        AppStatus.SetCancelSource(cancelSource);
        string? tempCombined = null;

        try
        {
            if (!await ConfirmOverwriteAsync("Overwrite UOP file?", outputPath))
            {
                SetStatus("Pack cancelled.", AppStatusSeverity.Warning);
                return;
            }

            AppStatus.SetProgress(0, true);
            var progress = new Progress<int>(percent => AppStatus.SetProgress(percent, true));

            var forcedIndexed = ShouldForceIndexedPack(_packInputs, outputPath);
            if (mode == UopMode.Concatenate && forcedIndexed)
            {
                AppStatus.AppendLog("Pack mode forced to Mul + Idx for detected legacy files.", AppStatusSeverity.Warning);
                mode = UopMode.Indexed;
            }

            AppStatus.AppendLog($"Pack mode: {mode}", AppStatusSeverity.Info);

            if (mode == UopMode.Concatenate)
            {
                var inputs = GetExistingInputs();
                if (inputs.Count == 0)
                {
                    SetStatus("Add at least one input file.", AppStatusSeverity.Error);
                    return;
                }

                var input = inputs[0];
                if (inputs.Count > 1)
                {
                    tempCombined = Path.Combine(Path.GetTempPath(), $"uop_pack_{Guid.NewGuid():N}.bin");
                    using (var combined = new FileStream(tempCombined, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        foreach (var path in inputs)
                        {
                            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                            source.CopyTo(combined);
                        }
                    }
                    input = tempCombined;
                }

                AppStatus.AppendLog($"Packing {string.Join(", ", inputs.Select(Path.GetFileName))} -> {outputPath}", AppStatusSeverity.Info);
                await Task.Run(() =>
                {
                    UopCodec.PackFile(input, outputPath, string.IsNullOrWhiteSpace(templatePath) ? null : templatePath, chunkSize,
                        message => AppStatus.AppendLog(message, AppStatusSeverity.Info),
                        progress, cancelSource.Token);
                }, cancelSource.Token);
            }
            else
            {
                if (!TryResolveMulIdxPair(out var mulPath, out var idxPath, out var error))
                {
                    if (TryResolveMapMul(out var mapMul, out var mapIndex, out var mapError))
                    {
                        AppStatus.AppendLog($"Packing {mapMul} -> {outputPath}", AppStatusSeverity.Info);
                        await Task.Run(() =>
                        {
                            UopCodec.PackLegacyUopFromMul(mapMul, mapIndex, outputPath,
                                message => AppStatus.AppendLog(message, AppStatusSeverity.Info),
                                progress, cancelSource.Token);
                        }, cancelSource.Token);
                        SetStatus("UOP pack completed.", AppStatusSeverity.Success);
                        return;
                    }

                    SetStatus(error ?? mapError ?? "Mul/Idx files are required.", AppStatusSeverity.Error);
                    return;
                }

                if (!TryResolveLegacyPackType(outputPath, mulPath, idxPath, out var legacyType, out var legacyMapIndex))
                {
                    SetStatus("Unable to detect UOP type for mul/idx pack.", AppStatusSeverity.Error);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(templatePath))
                {
                    AppStatus.AppendLog($"Packing {mulPath} + {idxPath} -> {outputPath}", AppStatusSeverity.Info);
                    await Task.Run(() =>
                    {
                        UopCodec.PackIndexedFromFiles(mulPath, idxPath, outputPath, templatePath,
                            message => AppStatus.AppendLog(message, AppStatusSeverity.Info),
                            progress, cancelSource.Token);
                    }, cancelSource.Token);
                }
                else
                {
                    var housingBin = FindHousingBin();
                    AppStatus.AppendLog($"Packing {mulPath} + {idxPath} -> {outputPath}", AppStatusSeverity.Info);
                    await Task.Run(() =>
                    {
                        UopCodec.PackLegacyUopFromMulIdx(mulPath, idxPath, legacyType, legacyMapIndex, outputPath,
                            message => AppStatus.AppendLog(message, AppStatusSeverity.Info),
                            progress, cancelSource.Token,
                            housingBin);
                    }, cancelSource.Token);
                }
            }

            SetStatus("UOP pack completed.", AppStatusSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Pack cancelled.", AppStatusSeverity.Warning);
        }
        catch (Exception ex)
        {
            SetStatus($"Pack error: {ex.Message}", AppStatusSeverity.Error);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempCombined) && File.Exists(tempCombined))
            {
                File.Delete(tempCombined);
            }
            AppStatus.SetProgress(0, false);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
        }
    }

    private void UpdateExtractState()
    {
        ExtractOutputFolderBox.IsEnabled = true;
        ExtractBaseNameBox.IsEnabled = true;
        ExtractButton.IsEnabled = true;
        SetFieldState(ExtractUopPathBox, File.Exists(ExtractUopPathBox.Text ?? string.Empty) ? FieldState.Valid : FieldState.Error);
        SetFieldState(ExtractOutputFolderBox, !string.IsNullOrWhiteSpace(ExtractOutputFolderBox.Text) ? FieldState.Valid : FieldState.Error);
        SetFieldState(ExtractBaseNameBox, string.IsNullOrWhiteSpace(ExtractBaseNameBox.Text) ? FieldState.Neutral : FieldState.Valid, isOptional: true);
    }

    private void UpdatePackState()
    {
        var mode = GetSelectedPackMode();
        var templateExists = File.Exists(PackTemplatePathBox.Text ?? string.Empty);
        SetFieldState(PackTemplatePathBox, templateExists ? FieldState.Valid : FieldState.Warning, isOptional: !templateExists);
        SetFieldState(PackOutputFolderBox, Directory.Exists(PackOutputFolderBox.Text ?? string.Empty) ? FieldState.Valid : FieldState.Error);
        SetFieldState(PackOutputNameBox, !string.IsNullOrWhiteSpace(PackOutputNameBox.Text) ? FieldState.Valid : FieldState.Error);
        SetFieldState(PackChunkSizeBox, string.IsNullOrWhiteSpace(PackChunkSizeBox.Text) || int.TryParse(PackChunkSizeBox.Text, out _) ? FieldState.Neutral : FieldState.Error);
        PackButton.IsEnabled = true;
    }

    private void SetBusy(bool isBusy)
    {
        if (ExtractButton != null)
        {
            ExtractButton.IsEnabled = !isBusy;
        }

        if (PackButton != null)
        {
            PackButton.IsEnabled = !isBusy;
        }
    }

    private void SetStatus(string message, AppStatusSeverity severity)
    {
        var stamped = AppStatus.Stamp(message);
        if (ExtractStatusText != null)
        {
            ExtractStatusText.Text = stamped;
        }

        if (PackStatusText != null)
        {
            PackStatusText.Text = stamped;
        }
        AppStatus.AppendLog(message, severity);
        switch (severity)
        {
            case AppStatusSeverity.Success:
                AppStatus.SetSuccess(message);
                break;
            case AppStatusSeverity.Warning:
                AppStatus.SetWarning(message);
                break;
            case AppStatusSeverity.Error:
                AppStatus.SetError(message);
                break;
            default:
                AppStatus.SetInfo(message);
                break;
        }
    }

    private void LoadState()
    {
        _loadingState = true;

        ExtractUopPathBox.Text = AppSettings.GetString("UopTools.ExtractUopPath", ExtractUopPathBox.Text ?? string.Empty);
        ExtractOutputFolderBox.Text = AppSettings.GetString("UopTools.ExtractOutputFolder", ExtractOutputFolderBox.Text ?? string.Empty);
        ExtractBaseNameBox.Text = AppSettings.GetString("UopTools.ExtractBaseName", ExtractBaseNameBox.Text ?? string.Empty);
        var extractType = AppSettings.GetString("UopTools.ExtractType", string.Empty);
        SelectExtractType(extractType);
        PackTemplatePathBox.Text = AppSettings.GetString("UopTools.PackTemplate", PackTemplatePathBox.Text ?? string.Empty);
        PackOutputFolderBox.Text = AppSettings.GetString("UopTools.PackOutputFolder", PackOutputFolderBox.Text ?? string.Empty);
        PackOutputNameBox.Text = AppSettings.GetString("UopTools.PackOutputName", PackOutputNameBox.Text ?? string.Empty);
        PackChunkSizeBox.Text = AppSettings.GetString("UopTools.PackChunkSize", PackChunkSizeBox.Text ?? string.Empty);
        var packMode = AppSettings.GetString("UopTools.PackMode", string.Empty);
        SelectMode(PackModeComboBox, _packModes, packMode);
        var presetName = AppSettings.GetString("UopTools.PackPresetName", string.Empty);
        if (!string.IsNullOrWhiteSpace(presetName))
        {
            PackPresetNameComboBox.SelectedItem = _packNamePresets.FirstOrDefault(name => name.Equals(presetName, StringComparison.OrdinalIgnoreCase));
        }
        foreach (var entry in AppSettings.GetList("UopTools.PackInputs"))
        {
            if (!_packInputs.Contains(entry))
            {
                _packInputs.Add(entry);
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

        AppSettings.SetString("UopTools.ExtractUopPath", ExtractUopPathBox.Text ?? string.Empty);
        AppSettings.SetString("UopTools.ExtractOutputFolder", ExtractOutputFolderBox.Text ?? string.Empty);
        AppSettings.SetString("UopTools.ExtractBaseName", ExtractBaseNameBox.Text ?? string.Empty);
        AppSettings.SetString("UopTools.ExtractType", GetSelectedExtractType().Value.ToString());
        AppSettings.SetString("UopTools.PackTemplate", PackTemplatePathBox.Text ?? string.Empty);
        AppSettings.SetString("UopTools.PackOutputFolder", PackOutputFolderBox.Text ?? string.Empty);
        AppSettings.SetString("UopTools.PackOutputName", PackOutputNameBox.Text ?? string.Empty);
        AppSettings.SetString("UopTools.PackChunkSize", PackChunkSizeBox.Text ?? string.Empty);
        AppSettings.SetString("UopTools.PackMode", GetSelectedPackMode().ToString());
        AppSettings.SetString("UopTools.PackPresetName", PackPresetNameComboBox.SelectedItem as string ?? string.Empty);
        AppSettings.SetList("UopTools.PackInputs", _packInputs);
    }

    public void PersistState()
    {
        SaveState();
    }

    private static void SetFieldState(TextBox box, FieldState state, bool isOptional = false)
    {
        if (isOptional && string.IsNullOrWhiteSpace(box.Text))
        {
            box.ClearValue(BorderBrushProperty);
            box.ClearValue(ForegroundProperty);
            return;
        }

        ApplyFieldState(box, state);
    }

    private static void ApplyFieldState(TemplatedControl control, FieldState state)
    {
        if (state == FieldState.Neutral)
        {
            control.ClearValue(BorderBrushProperty);
            control.ClearValue(ForegroundProperty);
            return;
        }

        var brush = state switch
        {
            FieldState.Warning => Avalonia.Media.Brushes.Goldenrod,
            FieldState.Error => Avalonia.Media.Brushes.IndianRed,
            _ => Avalonia.Media.Brushes.ForestGreen
        };

        control.BorderBrush = brush;
        control.Foreground = brush;
    }

    private enum FieldState
    {
        Neutral,
        Valid,
        Warning,
        Error
    }

    private static async Task<string?> PickFileAsync(string title, string[] extensions)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.StorageProvider is null)
        {
            return null;
        }

        var provider = desktop.MainWindow.StorageProvider;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(title) { Patterns = extensions.Select(ext => $"*.{ext}").ToArray() }
            }
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private static async Task<string[]> PickFilesAsync(string title, string[] extensions)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.StorageProvider is null)
        {
            return Array.Empty<string>();
        }

        var provider = desktop.MainWindow.StorageProvider;
        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(title) { Patterns = extensions.Select(ext => $"*.{ext}").ToArray() }
            }
        });

        return files.Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToArray()!;
    }

    private static async Task<string?> PickFolderAsync(string title)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.StorageProvider is null)
        {
            return null;
        }

        var provider = desktop.MainWindow.StorageProvider;
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
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

    private Window? GetHostWindow() => VisualRoot as Window;

    private Window GetOwnerWindow()
        => GetHostWindow() ?? throw new InvalidOperationException("Host window not available.");

    private void LoadModes()
    {
        _packModes.Add(new UopModeOption("Single file (concatenate)", UopMode.Concatenate));
        _packModes.Add(new UopModeOption("Mul + Idx", UopMode.Indexed));
        PackModeComboBox.ItemsSource = _packModes;
        PackModeComboBox.SelectedIndex = 0;
    }

    private void LoadPresets()
    {
        var presets = new[]
        {
            "artLegacyMUL.uop",
            "gumpartLegacyMUL.uop",
            "soundLegacyMUL.uop",
            "map0xLegacyMUL.uop",
            "map1LegacyMUL.uop",
            "map1xLegacyMUL.uop",
            "map2LegacyMUL.uop",
            "map2xLegacyMUL.uop",
            "map3LegacyMUL.uop",
            "map4LegacyMUL.uop",
            "map5LegacyMUL.uop",
            "map5xLegacyMUL.uop",
            "tileart.uop",
            "AnimationFrame1.uop",
            "AnimationFrame2.uop",
            "AnimationFrame3.uop",
            "AnimationFrame4.uop",
            "AnimationFrame6.uop",
            "AnimationSequence.uop",
            "MainMisc.uop",
            "MultiCollection.uop",
            "string_dictionary.uop"
        };

        foreach (var preset in presets)
        {
            _packNamePresets.Add(preset);
        }

        PackPresetNameComboBox.ItemsSource = _packNamePresets;
    }

    private void LoadExtractMappings()
    {
        var mappings = new[]
        {
            "mapNLegacyMUL.uop -> mapN.mul",
            "mapNxLegacyMUL.uop -> mapN.mul",
            "gumpartLegacyMUL.uop -> gumpart.mul + gumpidx.mul",
            "artLegacyMUL.uop -> art.mul + artidx.mul",
            "soundLegacyMUL.uop -> sound.mul + soundidx.mul",
            "tileart.uop -> tileart.bin",
            "AnimationFrame#.uop -> animationframe#.bin",
            "AnimationSequence.uop -> animationsequence.bin",
            "MainMisc.uop -> mainmisc.bin",
            "MultiCollection.uop -> multicollection.mul + multicollection.idx (+ housing.bin)",
            "string_dictionary.uop -> string_dictionary.bin"
        };

        ExtractMappingList.ItemsSource = mappings;
    }

    private void LoadExtractTypes()
    {
        _extractTypes.Add(new ExtractTypeOption("Auto (by file name)", ExtractType.Auto));
        _extractTypes.Add(new ExtractTypeOption("Map (map#.mul)", ExtractType.Map));
        _extractTypes.Add(new ExtractTypeOption("Art (art.mul + artidx.mul)", ExtractType.Art));
        _extractTypes.Add(new ExtractTypeOption("Gump (gumpart.mul + gumpidx.mul)", ExtractType.Gump));
        _extractTypes.Add(new ExtractTypeOption("Sound (sound.mul + soundidx.mul)", ExtractType.Sound));
        _extractTypes.Add(new ExtractTypeOption("MultiCollection (mul + idx)", ExtractType.MultiCollection));
        _extractTypes.Add(new ExtractTypeOption("Generic (single file)", ExtractType.Generic));
        ExtractTypeComboBox.ItemsSource = _extractTypes;
        ExtractTypeComboBox.SelectedIndex = 0;
    }

    private ExtractTypeOption GetSelectedExtractType()
        => ExtractTypeComboBox.SelectedItem as ExtractTypeOption ?? _extractTypes[0];

    private void SelectExtractType(string value)
    {
        if (!Enum.TryParse<ExtractType>(value, out var parsed))
        {
            return;
        }

        var match = _extractTypes.FirstOrDefault(option => option.Value == parsed);
        if (match is not null)
        {
            ExtractTypeComboBox.SelectedItem = match;
        }
    }

    private UopMode GetSelectedPackMode()
        => PackModeComboBox.SelectedItem is UopModeOption option ? option.Value : UopMode.Concatenate;

    private static void SelectMode(ComboBox comboBox, IEnumerable<UopModeOption> options, string value)
    {
        if (!Enum.TryParse<UopMode>(value, out var parsed))
        {
            return;
        }

        var match = options.FirstOrDefault(option => option.Value == parsed);
        if (match is not null)
        {
            comboBox.SelectedItem = match;
        }
    }

    private static string? GuessBaseNameFromUop(string uopPath)
    {
        if (string.IsNullOrWhiteSpace(uopPath))
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(uopPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (name.EndsWith("LegacyMUL", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^"LegacyMUL".Length];
        }

        if (name.EndsWith("Legacy", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^"Legacy".Length];
        }

        return name;
    }

    private static string? GuessUopNameFromInputs(IReadOnlyCollection<string> inputs)
    {
        var baseName = inputs.Select(Path.GetFileNameWithoutExtension)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name) && !name.Contains("idx", StringComparison.OrdinalIgnoreCase));
        baseName ??= inputs.Select(Path.GetFileNameWithoutExtension).FirstOrDefault();

        if (string.IsNullOrWhiteSpace(baseName))
        {
            return null;
        }

        var lower = baseName.ToLowerInvariant();
        if (lower.StartsWith("animationframe"))
        {
            var suffix = baseName["animationframe".Length..];
            return $"AnimationFrame{suffix}.uop";
        }

        return lower switch
        {
            "animationsequence" => "AnimationSequence.uop",
            "mainmisc" => "MainMisc.uop",
            "multicollection" => "MultiCollection.uop",
            "string_dictionary" => "string_dictionary.uop",
            "tileart" => "tileart.uop",
            "gumpart" => "gumpartLegacyMUL.uop",
            "art" => "artLegacyMUL.uop",
            "sound" => "soundLegacyMUL.uop",
            _ when lower.StartsWith("map") => $"{baseName}LegacyMUL.uop",
            _ => $"{baseName}LegacyMUL.uop"
        };
    }

    private static string EnsureUopExtension(string name)
        => name.EndsWith(".uop", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.uop";

    private List<string> GetExistingInputs()
        => _packInputs.Where(File.Exists).ToList();

    private static bool ShouldForceIndexedPack(IEnumerable<string> inputs, string outputPath)
    {
        var outputName = Path.GetFileNameWithoutExtension(outputPath).ToLowerInvariant();
        if (outputName.Contains("gumpart") || outputName.Contains("art") || outputName.Contains("sound") || outputName.Contains("multicollection"))
        {
            return true;
        }

        if (outputName.StartsWith("map", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var input in inputs)
        {
            var name = Path.GetFileNameWithoutExtension(input).ToLowerInvariant();
            if (name.Contains("idx"))
            {
                return true;
            }

            if (name.Contains("gump") || name.Contains("art") || name.Contains("sound") || name.Contains("multi"))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryResolveMapMul(out string mulPath, out int mapIndex, out string? error)
    {
        mulPath = string.Empty;
        mapIndex = 0;
        error = null;

        var existing = _packInputs.Where(File.Exists).ToList();
        var mul = existing.FirstOrDefault(path => Path.GetExtension(path).Equals(".mul", StringComparison.OrdinalIgnoreCase));
        if (mul is null)
        {
            error = "Map mul not found.";
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(mul);
        if (!IsMapUop(name, out mapIndex))
        {
            var outputName = Path.GetFileNameWithoutExtension(PackOutputNameBox.Text ?? string.Empty);
            if (!IsMapUop(outputName, out mapIndex))
            {
                error = "Map index not detected.";
                return false;
            }
        }

        mulPath = mul;
        return true;
    }

    private bool TryResolveLegacyPackType(string outputPath, string mulPath, string idxPath, out LegacyUopType type, out int mapIndex)
    {
        type = LegacyUopType.Art;
        mapIndex = 0;

        var outputName = Path.GetFileNameWithoutExtension(outputPath);
        if (IsMapUop(outputName.ToLowerInvariant(), out mapIndex))
        {
            type = LegacyUopType.Map;
            return true;
        }

        var lowerMul = Path.GetFileNameWithoutExtension(mulPath).ToLowerInvariant();
        var lowerOut = outputName.ToLowerInvariant();

        if (lowerMul.Contains("gump") || lowerOut.Contains("gump"))
        {
            type = LegacyUopType.Gump;
            return true;
        }

        if (lowerMul.Contains("sound") || lowerOut.Contains("sound"))
        {
            type = LegacyUopType.Sound;
            return true;
        }

        if (lowerMul.Contains("multi") || lowerOut.Contains("multi"))
        {
            type = LegacyUopType.MultiCollection;
            return true;
        }

        type = LegacyUopType.Art;
        return true;
    }

    private string? FindHousingBin()
    {
        var existing = _packInputs.Where(File.Exists).ToList();
        var housing = existing.FirstOrDefault(path => Path.GetFileName(path).Equals("housing.bin", StringComparison.OrdinalIgnoreCase));
        return housing;
    }

    private bool TryResolveMulIdxPair(out string mulPath, out string idxPath, out string? error)
    {
        mulPath = string.Empty;
        idxPath = string.Empty;
        error = null;

        var existing = _packInputs.Where(File.Exists).ToList();
        if (existing.Count < 2)
        {
            error = "Mul + Idx files are required.";
            return false;
        }

        var idx = existing.FirstOrDefault(path => Path.GetFileName(path).Contains("idx", StringComparison.OrdinalIgnoreCase));
        if (idx is null && existing.Count == 2)
        {
            idx = existing.OrderBy(path => new FileInfo(path).Length).FirstOrDefault();
        }

        if (idx is null)
        {
            error = "Unable to detect idx file. Include a file with 'idx' in the name.";
            return false;
        }

        var mul = existing.FirstOrDefault(path => !string.Equals(path, idx, StringComparison.OrdinalIgnoreCase));
        if (mul is null)
        {
            error = "Unable to detect mul file.";
            return false;
        }

        mulPath = mul;
        idxPath = idx;
        return true;
    }

    private sealed class UopModeOption
    {
        public UopModeOption(string label, UopMode value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }
        public UopMode Value { get; }

        public override string ToString() => Label;
    }

    private enum UopMode
    {
        Concatenate,
        Indexed
    }

    private static void ExtractByUopName(
        string uopPath,
        string outputFolder,
        string prefix,
        ExtractTypeOption selectedType,
        Action<string>? log,
        IProgress<int>? progress,
        CancellationToken token)
    {
        var fileName = Path.GetFileNameWithoutExtension(uopPath);
        var baseName = GuessBaseNameFromUop(uopPath) ?? fileName;
        var lower = baseName.ToLowerInvariant();

        if (selectedType.Value == ExtractType.Map)
        {
            if (!IsMapUop(lower, out var forcedIndex))
            {
                throw new InvalidOperationException("Map extract selected but map index not detected.");
            }

            log?.Invoke($"Extract map UOP as: {prefix}map{forcedIndex}.mul");
            UopCodec.ExtractLegacyUopToMul(uopPath, outputFolder, prefix, LegacyUopType.Map, forcedIndex, log, progress, token);
            return;
        }

        if (selectedType.Value == ExtractType.Art)
        {
            log?.Invoke($"Extract UOP as: {prefix}art.mul + {prefix}artidx.mul");
            UopCodec.ExtractLegacyUopToMul(uopPath, outputFolder, prefix, LegacyUopType.Art, 0, log, progress, token);
            return;
        }

        if (selectedType.Value == ExtractType.Gump)
        {
            log?.Invoke($"Extract UOP as: {prefix}gumpart.mul + {prefix}gumpidx.mul");
            UopCodec.ExtractLegacyUopToMul(uopPath, outputFolder, prefix, LegacyUopType.Gump, 0, log, progress, token);
            return;
        }

        if (selectedType.Value == ExtractType.Sound)
        {
            log?.Invoke($"Extract UOP as: {prefix}sound.mul + {prefix}soundidx.mul");
            UopCodec.ExtractLegacyUopToMul(uopPath, outputFolder, prefix, LegacyUopType.Sound, 0, log, progress, token);
            return;
        }

        if (selectedType.Value == ExtractType.MultiCollection)
        {
            log?.Invoke($"Extract UOP as: {prefix}multicollection.mul + {prefix}multicollection.idx");
            UopCodec.ExtractLegacyUopToMul(uopPath, outputFolder, prefix, LegacyUopType.MultiCollection, 0, log, progress, token);
            return;
        }

        if (selectedType.Value == ExtractType.Generic)
        {
            var genericName = $"{prefix}{baseName}.bin";
            log?.Invoke($"Extract UOP as single file: {genericName}");
            UopCodec.ExtractToFile(uopPath, Path.Combine(outputFolder, genericName), true, log, progress, token);
            return;
        }

        if (IsMapUop(lower, out var mapIndex))
        {
            log?.Invoke($"Extract map UOP as: {prefix}map{mapIndex}.mul");
            UopCodec.ExtractLegacyUopToMul(uopPath, outputFolder, prefix, LegacyUopType.Map, mapIndex, log, progress, token);
            return;
        }

        if (lower is "gumpart" or "art" or "sound")
        {
            var type = lower switch
            {
                "gumpart" => LegacyUopType.Gump,
                "art" => LegacyUopType.Art,
                _ => LegacyUopType.Sound
            };
            log?.Invoke($"Extract UOP as: {prefix}{lower}.mul + {prefix}{lower}idx.mul");
            UopCodec.ExtractLegacyUopToMul(uopPath, outputFolder, prefix, type, 0, log, progress, token);
            return;
        }

        if (lower == "multicollection")
        {
            log?.Invoke($"Extract UOP as: {prefix}multicollection.mul + {prefix}multicollection.idx");
            UopCodec.ExtractLegacyUopToMul(uopPath, outputFolder, prefix, LegacyUopType.MultiCollection, 0, log, progress, token);
            return;
        }

        var fallbackName = $"{prefix}{baseName}.bin";
        log?.Invoke($"Extract UOP as single file: {fallbackName}");
        UopCodec.ExtractToFile(uopPath, Path.Combine(outputFolder, fallbackName), true, log, progress, token);
    }

    private static bool IsMapUop(string baseName, out int mapIndex)
    {
        mapIndex = 0;
        if (!baseName.StartsWith("map", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var digits = new string(baseName.Skip(3).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out mapIndex);
    }

    private sealed class ExtractTypeOption
    {
        public ExtractTypeOption(string label, ExtractType value)
        {
            Label = label;
            Value = value;
        }

        public string Label { get; }
        public ExtractType Value { get; }

        public override string ToString() => Label;
    }

    private enum ExtractType
    {
        Auto,
        Map,
        Art,
        Gump,
        Sound,
        MultiCollection,
        Generic
    }

}
