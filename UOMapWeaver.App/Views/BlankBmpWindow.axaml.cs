using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using UOMapWeaver.App;
using UOMapWeaver.App.Defaults;
using UOMapWeaver.Core;
using UOMapWeaver.Core.Bmp;
using static UOMapWeaver.App.Views.ViewHelpers;
using FieldState = UOMapWeaver.App.Views.ViewHelpers.FieldState;

namespace UOMapWeaver.App.Views;

public sealed partial class BlankBmpView : UserControl, IAppStateView
{
    private readonly List<MapPreset> _presets = new();
    private MapPreset? _lastPresetSelection;
    private readonly List<FillModeOption> _fillModes = new();
    private readonly List<TerrainFillOption> _terrainOptions = new();
    private readonly List<PaletteOption> _paletteOptions = new();
    private bool _loadingState;

    public BlankBmpView()
    {
        InitializeComponent();
        LoadFillModes();
        LoadTerrainOptions();
        LoadPaletteOptions();
        FillIndexBox.Text = "0";
        FillAltitudeBox.Text = "-5";
        LoadPresets();
        PresetPathText.Text = $"Preset file: {GetPresetPath()}";
        LoadState();
        UpdateStatus();
    }

    private async void OnBrowsePalette(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => PalettePathBox.Text = await PickFileAsync(this, "Select palette BMP", new[] { "bmp" });

    private async void OnBrowseOutput(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => OutputFolderBox.Text = await PickFolderAsync(this, "Select output folder");

    private async void OnOpenPresets(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var path = GetPresetPath();

        if (!File.Exists(path))
        {
            SaveDefaultPresets(path);
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Unable to open presets: {ex.Message}";
            AppStatus.SetWarning(StatusText.Text);
        }
    }

    private async void OnGenerate(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        using var cancelSource = new CancellationTokenSource();
        AppStatus.SetCancelSource(cancelSource);
        SetBusy(true);

        if (!TryParseSize(out var width, out var height))
        {
            StatusText.Text = "Invalid map size.";
            AppStatus.SetError(StatusText.Text);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
            return;
        }

        if (width % MapMul.BlockSize != 0 || height % MapMul.BlockSize != 0)
        {
            StatusText.Text = "Map size must be divisible by 8.";
            AppStatus.SetError(StatusText.Text);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(PalettePathBox.Text) || !File.Exists(PalettePathBox.Text))
        {
            StatusText.Text = "Palette BMP not found.";
            AppStatus.SetError(StatusText.Text);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
            return;
        }

        if (!IsValidPalette(PalettePathBox.Text, out var paletteError))
        {
            StatusText.Text = paletteError;
            AppStatus.SetError(StatusText.Text);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputFolderBox.Text) || !Directory.Exists(OutputFolderBox.Text))
        {
            StatusText.Text = "Output folder not found.";
            AppStatus.SetError(StatusText.Text);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
            return;
        }

        if (!TryGetFillIndex(out var fillIndex, out var fillError))
        {
            StatusText.Text = fillError;
            AppStatus.SetError(StatusText.Text);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
            return;
        }

        if (!TryGetFillAltitude(out var fillAltitude, out var altitudeError))
        {
            StatusText.Text = altitudeError;
            AppStatus.SetError(StatusText.Text);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
            return;
        }

        var baseName = string.IsNullOrWhiteSpace(OutputBaseNameBox.Text)
            ? "BlankMap"
            : OutputBaseNameBox.Text.Trim();

        if (HasInvalidFileNameChars(baseName))
        {
            StatusText.Text = "Output name contains invalid characters.";
            AppStatus.SetError(StatusText.Text);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
            return;
        }

        var outputFolder = OutputFolderBox.Text!;
        var terrainPath = Path.Combine(outputFolder, $"{baseName}_Terrain.bmp");
        var altitudePath = Path.Combine(outputFolder, $"{baseName}_Altitude.bmp");

        if (!await ConfirmOverwriteAsync("Overwrite BMP files?", terrainPath, altitudePath))
        {
            StatusText.Text = "Generation cancelled.";
            AppStatus.SetWarning(StatusText.Text);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
            return;
        }

        try
        {
            AppStatus.SetProgress(0, true);

            AppStatus.AppendLog("Blank BMP generation start.", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Size: {width}x{height}", AppStatusSeverity.Info);
            var palettePath = PalettePathBox.Text!;
            AppStatus.AppendLog($"Palette: {palettePath}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Fill terrain index: {fillIndex}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Fill altitude Z: {fillAltitude}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Output: {terrainPath}, {altitudePath}", AppStatusSeverity.Info);

            var palette = await Task.Run(() => Bmp8Codec.Read(palettePath).Palette, cancelSource.Token);

            IProgress<int> progress = CreateAppProgress();

            await Task.Run(() =>
            {
                using var terrainWriter = new Bmp8StreamWriter(terrainPath, width, height, palette);
                using var altitudeWriter = new Bmp8StreamWriter(altitudePath, width, height, Bmp8Codec.CreateGrayscalePalette());
                var terrainRow = new byte[width];
                var altitudeRow = new byte[width];
                Array.Fill(terrainRow, fillIndex);
                Array.Fill(altitudeRow, EncodeAltitude(fillAltitude));

                var lastProgress = -1;
                for (var y = height - 1; y >= 0; y--)
                {
                    cancelSource.Token.ThrowIfCancellationRequested();
                    terrainWriter.WriteRow(terrainRow);
                    altitudeWriter.WriteRow(altitudeRow);

                    var percent = (int)((height - y) * 100.0 / height);
                    if (percent != lastProgress)
                    {
                        lastProgress = percent;
                        progress.Report(percent);
                    }
                }
            }, cancelSource.Token);

            StatusText.Text = $"Generated {Path.GetFileName(terrainPath)} and {Path.GetFileName(altitudePath)}.";
            AppStatus.SetSuccess(StatusText.Text);
            AppStatus.AppendLog(StatusText.Text, AppStatusSeverity.Success);
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
            SetBusy(false);
        }
    }

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdatePresetMatch();
        UpdateStatus();
        SaveState();
    }

    private void OnPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PresetComboBox.SelectedItem is not MapPreset preset)
        {
            return;
        }

        if (preset.IsSeparator)
        {
            PresetComboBox.SelectedItem = _lastPresetSelection;
            return;
        }

        _lastPresetSelection = preset;
        MapWidthBox.Text = preset.Width.ToString();
        MapHeightBox.Text = preset.Height.ToString();
        UpdatePresetMatch();
        UpdateStatus();
        SaveState();
    }

    private void OnFillModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        UpdateStatus();
        SaveState();
    }

    private void OnTerrainChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FillTerrainComboBox.SelectedItem is TerrainFillOption terrain)
        {
            FillRBox.Text = terrain.R.ToString();
            FillGBox.Text = terrain.G.ToString();
            FillBBox.Text = terrain.B.ToString();
        }

        UpdateStatus();
        SaveState();
    }

    private void OnPaletteQuickChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PaletteQuickComboBox.SelectedItem is PaletteOption option)
        {
            PalettePathBox.Text = option.Path;
        }
        SaveState();
    }

    private void UpdateStatus()
    {
        MapSizeErrorText.Text = string.Empty;
        MapSizeDetailsText.Text = string.Empty;
        var widthOk = int.TryParse(MapWidthBox.Text, out var width) && width > 0;
        var heightOk = int.TryParse(MapHeightBox.Text, out var height) && height > 0;
        var sizeValid = widthOk && heightOk;

        if (sizeValid)
        {
            if (width % MapMul.BlockSize != 0 || height % MapMul.BlockSize != 0)
            {
                MapSizeErrorText.Text = "Not /8.";
            }
            else
            {
                MapSizeDetailsText.Text = $"Blocks: {width / MapMul.BlockSize}x{height / MapMul.BlockSize} | Max: {width - 1},{height - 1}";
            }
        }
        else
        {
            MapSizeErrorText.Text = "Invalid.";
        }

        var sizeState = sizeValid && width % MapMul.BlockSize == 0 && height % MapMul.BlockSize == 0
            ? FieldState.Valid
            : FieldState.Error;
        SetFieldState(MapWidthBox, widthOk ? sizeState : FieldState.Error);
        SetFieldState(MapHeightBox, heightOk ? sizeState : FieldState.Error);

        if (!string.IsNullOrWhiteSpace(PalettePathBox.Text))
        {
            if (!File.Exists(PalettePathBox.Text))
            {
                PaletteInfoText.Text = "Palette file not found.";
                SetFieldState(PalettePathBox, FieldState.Error);
            }
            else if (!IsValidPalette(PalettePathBox.Text, out var paletteError))
            {
                PaletteInfoText.Text = paletteError;
                SetFieldState(PalettePathBox, FieldState.Error);
            }
            else
            {
                PaletteInfoText.Text = $"Palette file: {PalettePathBox.Text}";
                SetFieldState(PalettePathBox, FieldState.Valid);
            }
        }
        else
        {
            PaletteInfoText.Text = string.Empty;
            SetFieldState(PalettePathBox, FieldState.Error);
        }

        if (string.IsNullOrWhiteSpace(OutputFolderBox.Text) || !Directory.Exists(OutputFolderBox.Text))
        {
            SetFieldState(OutputFolderBox, FieldState.Error);
        }
        else
        {
            SetFieldState(OutputFolderBox, FieldState.Valid);
        }

        UpdateFillVisibility();
        var mode = GetFillMode();
        if (mode == FillMode.PaletteIndex)
        {
            var indexOk = TryParseByte(FillIndexBox.Text, out var indexValue);
            SetFieldState(FillIndexBox, indexOk ? FieldState.Valid : FieldState.Error);
            SetFieldState(FillRBox, FieldState.Neutral);
            SetFieldState(FillGBox, FieldState.Neutral);
            SetFieldState(FillBBox, FieldState.Neutral);
            SetFieldState(FillTerrainComboBox, FieldState.Neutral);
        }
        else if (mode == FillMode.Rgb)
        {
            var rOk = TryParseByte(FillRBox.Text, out _);
            var gOk = TryParseByte(FillGBox.Text, out _);
            var bOk = TryParseByte(FillBBox.Text, out _);
            SetFieldState(FillRBox, rOk ? FieldState.Valid : FieldState.Error);
            SetFieldState(FillGBox, gOk ? FieldState.Valid : FieldState.Error);
            SetFieldState(FillBBox, bOk ? FieldState.Valid : FieldState.Error);
            SetFieldState(FillIndexBox, FieldState.Neutral);
            SetFieldState(FillTerrainComboBox, FieldState.Neutral);
        }
        else
        {
            var terrainOk = FillTerrainComboBox.SelectedItem is TerrainFillOption;
            SetFieldState(FillTerrainComboBox, terrainOk ? FieldState.Valid : FieldState.Error);
            SetFieldState(FillIndexBox, FieldState.Neutral);
            SetFieldState(FillRBox, FieldState.Neutral);
            SetFieldState(FillGBox, FieldState.Neutral);
            SetFieldState(FillBBox, FieldState.Neutral);
        }

        if (TryGetFillIndex(out var fillIndex, out _))
        {
            if (GetFillMode() == FillMode.TerrainXml &&
                FillTerrainComboBox.SelectedItem is TerrainFillOption terrain)
            {
                FillResolvedText.Text = $"{terrain.Name} -> Index {fillIndex}";
            }
            else
            {
                FillResolvedText.Text = $"Index: {fillIndex}";
            }
        }
        else
        {
            FillResolvedText.Text = string.Empty;
        }

        SetFieldState(FillAltitudeBox, TryGetFillAltitude(out _, out _) ? FieldState.Valid : FieldState.Error);

        var outputName = OutputBaseNameBox.Text;
        if (string.IsNullOrWhiteSpace(outputName))
        {
            SetFieldState(OutputBaseNameBox, FieldState.Neutral, isOptional: true);
        }
        else
        {
            SetFieldState(OutputBaseNameBox,
                HasInvalidFileNameChars(outputName) ? FieldState.Error : FieldState.Valid,
                isOptional: true);
        }
    }

    private void UpdatePresetMatch()
    {
        PresetMatchText.Text = string.Empty;
        if (!TryParseSize(out var width, out var height))
        {
            return;
        }

        var match = _presets.FirstOrDefault(p => !p.IsSeparator && p.Width == width && p.Height == height);
        if (match != null)
        {
            PresetMatchText.Text = $"Preset: {match.Name}";
        }
    }

    private void LoadPresets()
    {
        var path = GetPresetPath();
        if (!File.Exists(path))
        {
            SaveDefaultPresets(path);
        }

        try
        {
            var json = File.ReadAllText(path);
            var presets = JsonSerializer.Deserialize<List<MapPreset>>(json) ?? new List<MapPreset>();
            _presets.Clear();
            _presets.AddRange(presets);
        }
        catch
        {
            _presets.Clear();
            _presets.AddRange(GetDefaultPresets());
        }

        PresetComboBox.ItemsSource = _presets;
    }

    private void LoadFillModes()
    {
        _fillModes.Clear();
        _fillModes.Add(new FillModeOption(FillMode.PaletteIndex, "Palette Index"));
        _fillModes.Add(new FillModeOption(FillMode.Rgb, "RGB"));
        _fillModes.Add(new FillModeOption(FillMode.TerrainXml, "Terrain.xml"));
        FillModeComboBox.ItemsSource = _fillModes;
        FillModeComboBox.SelectedIndex = 0;
    }

    private FillMode GetFillMode()
    {
        return FillModeComboBox.SelectedItem is FillModeOption option ? option.Mode : FillMode.PaletteIndex;
    }

    private void UpdateFillVisibility()
    {
        var mode = GetFillMode();
        var useIndex = mode == FillMode.PaletteIndex;
        FillIndexBox.IsVisible = useIndex;
        var useRgb = mode == FillMode.Rgb;
        FillRBox.IsVisible = useRgb;
        FillGBox.IsVisible = useRgb;
        FillBBox.IsVisible = useRgb;
        FillTerrainComboBox.IsVisible = mode == FillMode.TerrainXml;
    }

    private bool TryGetFillIndex(out byte fillIndex, out string error)
    {
        error = string.Empty;
        fillIndex = 0;

        var mode = GetFillMode();
        if (mode == FillMode.PaletteIndex)
        {
            if (!int.TryParse(FillIndexBox.Text, out var value) || value < 0 || value > 255)
            {
                error = "Fill index must be between 0 and 255.";
                return false;
            }

            fillIndex = (byte)value;
            return true;
        }

        byte r;
        byte g;
        byte b;

        if (mode == FillMode.Rgb)
        {
            if (!TryParseByte(FillRBox.Text, out r) ||
                !TryParseByte(FillGBox.Text, out g) ||
                !TryParseByte(FillBBox.Text, out b))
            {
                error = "Fill RGB must be between 0 and 255.";
                return false;
            }
        }
        else
        {
            if (FillTerrainComboBox.SelectedItem is not TerrainFillOption terrain)
            {
                error = "Select a terrain from Terrain.xml.";
                return false;
            }

            r = terrain.R;
            g = terrain.G;
            b = terrain.B;
        }

        if (string.IsNullOrWhiteSpace(PalettePathBox.Text) || !File.Exists(PalettePathBox.Text))
        {
            error = "Palette BMP required to resolve RGB.";
            return false;
        }

        if (!IsValidPalette(PalettePathBox.Text, out error))
        {
            return false;
        }

        var palette = Bmp8Codec.Read(PalettePathBox.Text).Palette;
        fillIndex = PaletteUtils.FindNearestIndex(palette, r, g, b);
        return true;
    }

    private bool TryGetFillAltitude(out sbyte altitude, out string error)
    {
        error = string.Empty;
        altitude = 0;

        if (!int.TryParse(FillAltitudeBox.Text, out var value) || value < sbyte.MinValue || value > sbyte.MaxValue)
        {
            error = "Fill altitude must be between -128 and 127.";
            return false;
        }

        altitude = (sbyte)value;
        return true;
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

    private void LoadTerrainOptions()
    {
        _terrainOptions.Clear();
        var terrainPath = Path.Combine(UOMapWeaverDataPaths.SystemRoot, "Terrain.xml");
        if (!File.Exists(terrainPath))
        {
            FillTerrainComboBox.ItemsSource = _terrainOptions;
            return;
        }

        try
        {
            var doc = XDocument.Load(terrainPath);
            var root = doc.Root;
            if (root is null)
            {
                FillTerrainComboBox.ItemsSource = _terrainOptions;
                return;
            }

            foreach (var element in root.Elements("Terrain"))
            {
                var name = element.Attribute("Name")?.Value?.Trim();
                var tileText = element.Attribute("TileID")?.Value;
                var rText = element.Attribute("R")?.Value;
                var gText = element.Attribute("G")?.Value;
                var bText = element.Attribute("B")?.Value;
                if (string.IsNullOrWhiteSpace(name) ||
                    string.IsNullOrWhiteSpace(tileText) ||
                    !TryParseUShort(tileText, out var tileId) ||
                    !TryParseByte(rText, out var r) ||
                    !TryParseByte(gText, out var g) ||
                    !TryParseByte(bText, out var b))
                {
                    continue;
                }

                _terrainOptions.Add(new TerrainFillOption(name, tileId, r, g, b));
            }
        }
        catch
        {
            _terrainOptions.Clear();
        }

        FillTerrainComboBox.ItemsSource = _terrainOptions;
        if (_terrainOptions.Count > 0)
        {
            FillTerrainComboBox.SelectedIndex = 0;
        }
    }

    private void LoadPaletteOptions()
    {
        _paletteOptions.Clear();

        var candidates = new[]
        {
            UOMapWeaverDataPaths.PalettesRoot,
            UOMapWeaverDataPaths.SystemRoot,
            UOMapWeaverDataPaths.PhotoshopRoot
        };

        foreach (var root in candidates)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.bmp", SearchOption.AllDirectories))
            {
                if (!file.EndsWith("ColorPalette.bmp", StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith("Palette.bmp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!IsValidPalette(file, out _))
                {
                    continue;
                }

                var name = $"{Path.GetFileName(file)} ({Path.GetDirectoryName(file)})";
                _paletteOptions.Add(new PaletteOption(name, file));
            }
        }

        _paletteOptions.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        PaletteQuickComboBox.ItemsSource = _paletteOptions;
        if (_paletteOptions.Count > 0 && string.IsNullOrWhiteSpace(PalettePathBox.Text))
        {
            PaletteQuickComboBox.SelectedIndex = 0;
        }
    }

    private static string GetPresetPath()
        => UOMapWeaverDataPaths.MapPresetsPath;

    private static void SaveDefaultPresets(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? string.Empty);
        var json = JsonSerializer.Serialize(GetDefaultPresets(), new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private static List<MapPreset> GetDefaultPresets()
    {
        return MapDefaults.Presets
            .Select(preset => new MapPreset(preset.Name, preset.Width, preset.Height, preset.IsSeparator))
            .ToList();
    }

    private bool TryParseSize(out int width, out int height)
    {
        width = 0;
        height = 0;
        return int.TryParse(MapWidthBox.Text, out width) &&
               int.TryParse(MapHeightBox.Text, out height) &&
               width > 0 &&
               height > 0;
    }

    private void SetBusy(bool isBusy)
    {
        if (GenerateButton != null)
        {
            GenerateButton.IsEnabled = !isBusy;
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

    private static bool HasInvalidFileNameChars(string name)
        => name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0;

    private static bool TryParseByte(string? text, out byte value)
    {
        value = 0;
        return byte.TryParse(text, out value);
    }

    private static bool IsValidPalette(string path, out string error)
    {
        error = string.Empty;
        if (!BmpCodec.TryReadInfo(path, out _, out _, out var bits) || bits != 8)
        {
            error = "Palette BMP must be 8-bit.";
            return false;
        }

        return true;
    }

    private static bool TryParseUShort(string? text, out ushort value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
            return ushort.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out value);
        }

        return ushort.TryParse(trimmed, out value);
    }

    private void LoadState()
    {
        _loadingState = true;

        MapWidthBox.Text = AppSettings.GetString("BlankBmp.MapWidth", MapWidthBox.Text ?? string.Empty);
        MapHeightBox.Text = AppSettings.GetString("BlankBmp.MapHeight", MapHeightBox.Text ?? string.Empty);
        PalettePathBox.Text = AppSettings.GetString("BlankBmp.PalettePath", PalettePathBox.Text ?? string.Empty);
        OutputFolderBox.Text = AppSettings.GetString("BlankBmp.OutputFolder", OutputFolderBox.Text ?? string.Empty);
        OutputBaseNameBox.Text = AppSettings.GetString("BlankBmp.OutputName", OutputBaseNameBox.Text ?? string.Empty);
        FillIndexBox.Text = AppSettings.GetString("BlankBmp.FillIndex", FillIndexBox.Text ?? string.Empty);
        FillAltitudeBox.Text = AppSettings.GetString("BlankBmp.FillAltitude", FillAltitudeBox.Text ?? string.Empty);
        FillRBox.Text = AppSettings.GetString("BlankBmp.FillR", FillRBox.Text ?? string.Empty);
        FillGBox.Text = AppSettings.GetString("BlankBmp.FillG", FillGBox.Text ?? string.Empty);
        FillBBox.Text = AppSettings.GetString("BlankBmp.FillB", FillBBox.Text ?? string.Empty);

        var fillModeText = AppSettings.GetString("BlankBmp.FillMode", string.Empty);
        if (Enum.TryParse<FillMode>(fillModeText, out var fillMode))
        {
            var match = _fillModes.FirstOrDefault(option => option.Mode == fillMode);
            if (match != null)
            {
                FillModeComboBox.SelectedItem = match;
            }
        }

        var terrainTileId = AppSettings.GetInt("BlankBmp.FillTerrainTile", -1);
        if (terrainTileId >= 0)
        {
            var terrain = _terrainOptions.FirstOrDefault(option => option.TileId == terrainTileId);
            if (terrain != null)
            {
                FillTerrainComboBox.SelectedItem = terrain;
            }
        }

        var presetIndex = AppSettings.GetInt("BlankBmp.PresetIndex", -1);
        if (presetIndex >= 0 && presetIndex < PresetComboBox.ItemCount)
        {
            PresetComboBox.SelectedIndex = presetIndex;
        }

        _loadingState = false;
    }

    private void SaveState()
    {
        if (_loadingState || !AppSettings.SaveEnabled)
        {
            return;
        }

        AppSettings.SetString("BlankBmp.MapWidth", MapWidthBox.Text ?? string.Empty);
        AppSettings.SetString("BlankBmp.MapHeight", MapHeightBox.Text ?? string.Empty);
        AppSettings.SetString("BlankBmp.PalettePath", PalettePathBox.Text ?? string.Empty);
        AppSettings.SetString("BlankBmp.OutputFolder", OutputFolderBox.Text ?? string.Empty);
        AppSettings.SetString("BlankBmp.OutputName", OutputBaseNameBox.Text ?? string.Empty);
        AppSettings.SetString("BlankBmp.FillIndex", FillIndexBox.Text ?? string.Empty);
        AppSettings.SetString("BlankBmp.FillAltitude", FillAltitudeBox.Text ?? string.Empty);
        AppSettings.SetString("BlankBmp.FillR", FillRBox.Text ?? string.Empty);
        AppSettings.SetString("BlankBmp.FillG", FillGBox.Text ?? string.Empty);
        AppSettings.SetString("BlankBmp.FillB", FillBBox.Text ?? string.Empty);
        AppSettings.SetInt("BlankBmp.PresetIndex", PresetComboBox.SelectedIndex);

        var fillMode = GetFillMode();
        AppSettings.SetString("BlankBmp.FillMode", fillMode.ToString());
        if (FillTerrainComboBox.SelectedItem is TerrainFillOption terrain)
        {
            AppSettings.SetInt("BlankBmp.FillTerrainTile", terrain.TileId);
        }
    }

    public void PersistState()
    {
        SaveState();
    }

    private sealed class MapPreset
    {
        public MapPreset(string name, int width, int height, bool isSeparator = false)
        {
            Name = name;
            Width = width;
            Height = height;
            IsSeparator = isSeparator;
        }

        public string Name { get; }

        public int Width { get; }

        public int Height { get; }

        public bool IsSeparator { get; }

        public override string ToString() => Name;

        public static MapPreset Separator()
            => new("--------------------------------------------------------", 0, 0, true);
    }

    private sealed class FillModeOption
    {
        public FillModeOption(FillMode mode, string name)
        {
            Mode = mode;
            Name = name;
        }

        public FillMode Mode { get; }

        public string Name { get; }

        public override string ToString() => Name;
    }

    private enum FillMode
    {
        PaletteIndex,
        Rgb,
        TerrainXml
    }

    private sealed class TerrainFillOption
    {
        public TerrainFillOption(string name, ushort tileId, byte r, byte g, byte b)
        {
            Name = name;
            TileId = tileId;
            R = r;
            G = g;
            B = b;
        }

        public string Name { get; }

        public ushort TileId { get; }

        public byte R { get; }

        public byte G { get; }

        public byte B { get; }

        public override string ToString() => $"{Name} (0x{TileId:X4}) RGB({R},{G},{B})";
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

}
