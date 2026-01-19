using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using UOMapWeaver.App;
using UOMapWeaver.App.Defaults;
using UOMapWeaver.Core;
using UOMapWeaver.Core.Bmp;
using UOMapWeaver.Core.TileColors;
using static UOMapWeaver.App.Views.ViewHelpers;
using FieldState = UOMapWeaver.App.Views.ViewHelpers.FieldState;

namespace UOMapWeaver.App.Views;

public sealed partial class BlankBmpView : UserControl, IAppStateView
{
    private readonly List<MapPreset> _presets = new();
    private MapPreset? _lastPresetSelection;
    private readonly List<FillModeOption> _fillModes = new();
    private readonly List<TerrainOutputOption> _terrainOutputModes = new();
    private readonly List<AltitudeOutputOption> _altitudeOutputModes = new();
    private readonly List<TerrainFillOption> _terrainOptions = new();
    private readonly List<PaletteOption> _paletteOptions = new();
    private bool _loadingState;

    public BlankBmpView()
    {
        InitializeComponent();
        LoadTerrainOutputModes();
        LoadAltitudeOutputModes();
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
            AppStatus.AppendLog($"Preset file not found at {path}.", AppStatusSeverity.Warning);
            StatusText.Text = "Preset file not found.";
            AppStatus.SetWarning(StatusText.Text);
            return;
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

        var terrainOutput = GetTerrainOutputMode();
        var altitudeOutput = GetAltitudeOutputMode();

        if (string.IsNullOrWhiteSpace(OutputFolderBox.Text) || !Directory.Exists(OutputFolderBox.Text))
        {
            StatusText.Text = "Output folder not found.";
            AppStatus.SetError(StatusText.Text);
            AppStatus.SetCancelSource(null);
            SetBusy(false);
            return;
        }

        if (!TryGetFillTerrain(out var fillIndex, out var fillColor, out var fillError))
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
            AppStatus.AppendLog($"Terrain output: {terrainOutput}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Altitude output: {altitudeOutput}", AppStatusSeverity.Info);
            var palettePath = PalettePathBox.Text ?? string.Empty;
            if (RequiresPalette(terrainOutput, GetFillMode()))
            {
                AppStatus.AppendLog($"Palette: {palettePath}", AppStatusSeverity.Info);
            }
            AppStatus.AppendLog($"Fill terrain: index {fillIndex}, rgb({fillColor.R},{fillColor.G},{fillColor.B})",
                AppStatusSeverity.Info);
            var altitudeColorLog = altitudeOutput == AltitudeOutputMode.AltitudeXml24
                ? " (Altitude.xml)"
                : " (grayscale)";
            AppStatus.AppendLog($"Fill altitude Z: {fillAltitude}{altitudeColorLog}", AppStatusSeverity.Info);
            AppStatus.AppendLog($"Output: {terrainPath}, {altitudePath}", AppStatusSeverity.Info);

            if (GetFillMode() == FillMode.TerrainXml && !File.Exists(UOMapWeaverDataPaths.TerrainDefinitionsPath))
            {
                AppStatus.AppendLog($"Terrain definitions not found: {UOMapWeaverDataPaths.TerrainDefinitionsPath}",
                    AppStatusSeverity.Warning);
            }

            var palette = Array.Empty<BmpPaletteEntry>();
            if (RequiresPalette(terrainOutput, GetFillMode()))
            {
                if (!TryResolvePalette(out palette, out var paletteError))
                {
                    StatusText.Text = paletteError;
                    AppStatus.SetError(StatusText.Text);
                    AppStatus.SetCancelSource(null);
                    SetBusy(false);
                    return;
                }
            }

            Dictionary<sbyte, RgbColor> altitudeColors = new();
            if (altitudeOutput == AltitudeOutputMode.AltitudeXml24)
            {
                var altitudePathXml = Path.Combine(UOMapWeaverDataPaths.DataRoot, "Altitude.xml");
                altitudeColors = LoadAltitudeColors(altitudePathXml);
                if (altitudeColors.Count == 0)
                {
                    AppStatus.AppendLog($"Altitude.xml not found or empty: {altitudePathXml}. Using grayscale.",
                        AppStatusSeverity.Warning);
                }
            }

            IProgress<int> progress = CreateAppProgress();

            await Task.Run(() =>
            {
                var altitudeGrayscale = EncodeAltitude(fillAltitude);
                Bmp8StreamWriter? terrain8 = null;
                Bmp24StreamWriter? terrain24 = null;
                Bmp8StreamWriter? altitude8 = null;
                Bmp24StreamWriter? altitude24 = null;

                if (terrainOutput == TerrainOutputMode.Palette8)
                {
                    terrain8 = new Bmp8StreamWriter(terrainPath, width, height, palette);
                }
                else
                {
                    terrain24 = new Bmp24StreamWriter(terrainPath, width, height);
                }

                if (altitudeOutput == AltitudeOutputMode.Grayscale8)
                {
                    altitude8 = new Bmp8StreamWriter(altitudePath, width, height, Bmp8Codec.CreateGrayscalePalette());
                }
                else
                {
                    altitude24 = new Bmp24StreamWriter(altitudePath, width, height);
                }

                var terrainRow8 = terrain8 != null ? new byte[width] : Array.Empty<byte>();
                var terrainRow24 = terrain24 != null ? new byte[width * 3] : Array.Empty<byte>();
                var altitudeRow8 = altitude8 != null ? new byte[width] : Array.Empty<byte>();
                var altitudeRow24 = altitude24 != null ? new byte[width * 3] : Array.Empty<byte>();

                if (terrain8 != null)
                {
                    Array.Fill(terrainRow8, fillIndex);
                }
                else
                {
                    for (var x = 0; x < width; x++)
                    {
                        var offset = x * 3;
                        terrainRow24[offset] = fillColor.R;
                        terrainRow24[offset + 1] = fillColor.G;
                        terrainRow24[offset + 2] = fillColor.B;
                    }
                }

                if (altitude8 != null)
                {
                    Array.Fill(altitudeRow8, altitudeGrayscale);
                }
                else
                {
                    var color = altitudeColors.TryGetValue(fillAltitude, out var altitudeColor)
                        ? altitudeColor
                        : new RgbColor(altitudeGrayscale, altitudeGrayscale, altitudeGrayscale);
                    for (var x = 0; x < width; x++)
                    {
                        var offset = x * 3;
                        altitudeRow24[offset] = color.R;
                        altitudeRow24[offset + 1] = color.G;
                        altitudeRow24[offset + 2] = color.B;
                    }
                }

                var lastProgress = -1;
                for (var y = height - 1; y >= 0; y--)
                {
                    cancelSource.Token.ThrowIfCancellationRequested();
                    terrain8?.WriteRow(terrainRow8);
                    terrain24?.WriteRow(terrainRow24);
                    altitude8?.WriteRow(altitudeRow8);
                    altitude24?.WriteRow(altitudeRow24);

                    var percent = (int)((height - y) * 100.0 / height);
                    if (percent != lastProgress)
                    {
                        lastProgress = percent;
                        progress.Report(percent);
                    }
                }

                terrain8?.Dispose();
                terrain24?.Dispose();
                altitude8?.Dispose();
                altitude24?.Dispose();
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

    private void OnOutputModeChanged(object? sender, SelectionChangedEventArgs e)
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
            if (terrain.Altitude.HasValue)
            {
                FillAltitudeBox.Text = terrain.Altitude.Value.ToString();
            }
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
        UpdatePalettePreview();
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

        var terrainOutput = GetTerrainOutputMode();
        var altitudeOutput = GetAltitudeOutputMode();
        var needsPalette = RequiresPalette(terrainOutput, GetFillMode());
        EnsureCompatibleFillMode(terrainOutput);

        if (!string.IsNullOrWhiteSpace(PalettePathBox.Text))
        {
            if (!File.Exists(PalettePathBox.Text))
            {
                PaletteInfoText.Text = "Palette file not found.";
                SetFieldState(PalettePathBox, needsPalette ? FieldState.Error : FieldState.Warning);
            }
            else if (!IsValidPalette(PalettePathBox.Text, out var paletteError))
            {
                PaletteInfoText.Text = paletteError;
                SetFieldState(PalettePathBox, needsPalette ? FieldState.Error : FieldState.Warning);
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
            SetFieldState(PalettePathBox, needsPalette ? FieldState.Error : FieldState.Neutral);
        }

        PalettePathBox.IsEnabled = needsPalette;
        PaletteQuickComboBox.IsEnabled = needsPalette;
        UpdatePalettePreview();

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

        if (TryGetFillTerrain(out var fillIndex, out var fillColor, out _))
        {
            if (GetFillMode() == FillMode.TerrainXml &&
                FillTerrainComboBox.SelectedItem is TerrainFillOption terrain)
            {
                FillResolvedText.Text = needsPalette
                    ? $"{terrain.Name} -> Index {fillIndex}"
                    : $"{terrain.Name} -> RGB({fillColor.R},{fillColor.G},{fillColor.B})";
            }
            else
            {
                FillResolvedText.Text = needsPalette
                    ? $"Index: {fillIndex}"
                    : $"RGB({fillColor.R},{fillColor.G},{fillColor.B})";
            }

            FillColorSwatch.Background = new SolidColorBrush(Color.FromRgb(fillColor.R, fillColor.G, fillColor.B));
        }
        else
        {
            FillResolvedText.Text = string.Empty;
            FillColorSwatch.Background = null;
        }

        if (TryGetFillAltitude(out var fillAltitude, out _))
        {
            if (altitudeOutput == AltitudeOutputMode.AltitudeXml24)
            {
                var altitudePath = Path.Combine(UOMapWeaverDataPaths.DataRoot, "Altitude.xml");
                var altitudeColors = LoadAltitudeColors(altitudePath);
                if (altitudeColors.TryGetValue(fillAltitude, out var altitudeColor))
                {
                    FillAltitudeSwatch.Background = new SolidColorBrush(Color.FromRgb(
                        altitudeColor.R, altitudeColor.G, altitudeColor.B));
                }
                else
                {
                    var grayscale = EncodeAltitude(fillAltitude);
                    FillAltitudeSwatch.Background = new SolidColorBrush(Color.FromRgb(grayscale, grayscale, grayscale));
                }
            }
            else
            {
                var grayscale = EncodeAltitude(fillAltitude);
                FillAltitudeSwatch.Background = new SolidColorBrush(Color.FromRgb(grayscale, grayscale, grayscale));
            }
        }
        else
        {
            FillAltitudeSwatch.Background = null;
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
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? UOMapWeaverDataPaths.PresetsRoot);
            SaveDefaultPresets(path);
            AppStatus.AppendLog($"Preset file not found at {path}.", AppStatusSeverity.Warning);
            _presets.Clear();
            PresetComboBox.ItemsSource = _presets;
            return;
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

    private void LoadTerrainOutputModes()
    {
        _terrainOutputModes.Clear();
        _terrainOutputModes.Add(new TerrainOutputOption(TerrainOutputMode.Palette8, "Palette (8-bit)"));
        _terrainOutputModes.Add(new TerrainOutputOption(TerrainOutputMode.Rgb24, "RGB (24-bit)"));
        TerrainOutputComboBox.ItemsSource = _terrainOutputModes;
        TerrainOutputComboBox.SelectedIndex = 0;
    }

    private void LoadAltitudeOutputModes()
    {
        _altitudeOutputModes.Clear();
        _altitudeOutputModes.Add(new AltitudeOutputOption(AltitudeOutputMode.Grayscale8, "Grayscale (8-bit)"));
        _altitudeOutputModes.Add(new AltitudeOutputOption(AltitudeOutputMode.AltitudeXml24, "Altitude.xml (24-bit)"));
        AltitudeOutputComboBox.ItemsSource = _altitudeOutputModes;
        AltitudeOutputComboBox.SelectedIndex = 0;
    }

    private TerrainOutputMode GetTerrainOutputMode()
        => TerrainOutputComboBox.SelectedItem is TerrainOutputOption option
            ? option.Mode
            : TerrainOutputMode.Palette8;

    private AltitudeOutputMode GetAltitudeOutputMode()
        => AltitudeOutputComboBox.SelectedItem is AltitudeOutputOption option
            ? option.Mode
            : AltitudeOutputMode.Grayscale8;

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

    private bool TryGetFillTerrain(out byte fillIndex, out RgbColor fillColor, out string error)
    {
        error = string.Empty;
        fillIndex = 0;
        fillColor = default;

        var mode = GetFillMode();
        if (mode == FillMode.PaletteIndex)
        {
            if (!int.TryParse(FillIndexBox.Text, out var value) || value < 0 || value > 255)
            {
                error = "Fill index must be between 0 and 255.";
                return false;
            }

            fillIndex = (byte)value;
            if (TryResolvePalette(out var palette, out error))
            {
                var entry = palette[fillIndex];
                fillColor = new RgbColor(entry.Red, entry.Green, entry.Blue);
            }
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

        fillColor = new RgbColor(r, g, b);
        if (RequiresPalette(GetTerrainOutputMode(), mode))
        {
            if (!TryResolvePalette(out var palette, out error))
            {
                return false;
            }

            fillIndex = PaletteUtils.FindNearestIndex(palette, r, g, b);
        }
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

    private bool TryResolvePalette(out BmpPaletteEntry[] palette, out string error)
    {
        palette = Array.Empty<BmpPaletteEntry>();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(PalettePathBox.Text) || !File.Exists(PalettePathBox.Text))
        {
            error = "Palette BMP required.";
            return false;
        }

        if (!IsValidPalette(PalettePathBox.Text, out error))
        {
            return false;
        }

        palette = Bmp8Codec.Read(PalettePathBox.Text).Palette;
        return true;
    }

    private static bool RequiresPalette(TerrainOutputMode outputMode, FillMode fillMode)
        => outputMode == TerrainOutputMode.Palette8 || fillMode == FillMode.PaletteIndex;

    private void EnsureCompatibleFillMode(TerrainOutputMode outputMode)
    {
        if (_loadingState)
        {
            return;
        }

        if (outputMode == TerrainOutputMode.Rgb24 && GetFillMode() == FillMode.PaletteIndex)
        {
            var rgbOption = _fillModes.FirstOrDefault(option => option.Mode == FillMode.Rgb);
            if (rgbOption != null)
            {
                FillModeComboBox.SelectedItem = rgbOption;
            }
        }
    }

    private void UpdatePalettePreview()
    {
        if (PalettePreviewImage == null)
        {
            return;
        }

        var path = PalettePathBox.Text;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            PalettePreviewImage.Source = null;
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            PalettePreviewImage.Source = new Bitmap(stream);
        }
        catch
        {
            PalettePreviewImage.Source = null;
        }
    }

    private static Dictionary<sbyte, RgbColor> LoadAltitudeColors(string path)
    {
        var result = new Dictionary<sbyte, RgbColor>();
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            var doc = XDocument.Load(path);
            foreach (var node in doc.Descendants("Altitude"))
            {
                if (!TryParseByte(node.Attribute("R")?.Value, out var r) ||
                    !TryParseByte(node.Attribute("G")?.Value, out var g) ||
                    !TryParseByte(node.Attribute("B")?.Value, out var b) ||
                    !int.TryParse(node.Attribute("Altitude")?.Value, out var altitude))
                {
                    continue;
                }

                var clamped = (sbyte)Math.Clamp(altitude, sbyte.MinValue, sbyte.MaxValue);
                if (!result.ContainsKey(clamped))
                {
                    result[clamped] = new RgbColor(r, g, b);
                }
            }
        }
        catch
        {
            result.Clear();
        }

        return result;
    }

    private void LoadTerrainOptions()
    {
        _terrainOptions.Clear();
        var terrainPath = UOMapWeaverDataPaths.TerrainDefinitionsPath;
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
                var altitudeText = element.Attribute("Altitude")?.Value
                                   ?? element.Attribute("Z")?.Value
                                   ?? element.Attribute("Alt")?.Value;
                var altitude = TryParseSByte(altitudeText, out var parsedAltitude) ? parsedAltitude : (sbyte?)null;
                if (string.IsNullOrWhiteSpace(name) ||
                    string.IsNullOrWhiteSpace(tileText) ||
                    !TryParseUShort(tileText, out var tileId) ||
                    !TryParseByte(rText, out var r) ||
                    !TryParseByte(gText, out var g) ||
                    !TryParseByte(bText, out var b))
                {
                    continue;
                }

                _terrainOptions.Add(new TerrainFillOption(name, tileId, r, g, b, altitude));
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
        var presets = GetDefaultPresets();
        if (presets.Count == 0)
        {
            return;
        }

        var json = JsonSerializer.Serialize(presets, new JsonSerializerOptions
        {
            WriteIndented = true
        });
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

    private static bool TryParseSByte(string? text, out sbyte value)
    {
        value = 0;
        return sbyte.TryParse(text, out value);
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
        var terrainOutputText = AppSettings.GetString("BlankBmp.TerrainOutput", string.Empty);
        if (Enum.TryParse<TerrainOutputMode>(terrainOutputText, out var terrainOutput))
        {
            var match = _terrainOutputModes.FirstOrDefault(option => option.Mode == terrainOutput);
            if (match != null)
            {
                TerrainOutputComboBox.SelectedItem = match;
            }
        }

        var altitudeOutputText = AppSettings.GetString("BlankBmp.AltitudeOutput", string.Empty);
        if (Enum.TryParse<AltitudeOutputMode>(altitudeOutputText, out var altitudeOutput))
        {
            var match = _altitudeOutputModes.FirstOrDefault(option => option.Mode == altitudeOutput);
            if (match != null)
            {
                AltitudeOutputComboBox.SelectedItem = match;
            }
        }

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
        UpdatePalettePreview();
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
        AppSettings.SetString("BlankBmp.TerrainOutput", GetTerrainOutputMode().ToString());
        AppSettings.SetString("BlankBmp.AltitudeOutput", GetAltitudeOutputMode().ToString());

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

    private sealed class TerrainOutputOption
    {
        public TerrainOutputOption(TerrainOutputMode mode, string name)
        {
            Mode = mode;
            Name = name;
        }

        public TerrainOutputMode Mode { get; }

        public string Name { get; }

        public override string ToString() => Name;
    }

    private enum TerrainOutputMode
    {
        Palette8,
        Rgb24
    }

    private sealed class AltitudeOutputOption
    {
        public AltitudeOutputOption(AltitudeOutputMode mode, string name)
        {
            Mode = mode;
            Name = name;
        }

        public AltitudeOutputMode Mode { get; }

        public string Name { get; }

        public override string ToString() => Name;
    }

    private enum AltitudeOutputMode
    {
        Grayscale8,
        AltitudeXml24
    }

    private sealed class TerrainFillOption
    {
        public TerrainFillOption(string name, ushort tileId, byte r, byte g, byte b, sbyte? altitude)
        {
            Name = name;
            TileId = tileId;
            R = r;
            G = g;
            B = b;
            Altitude = altitude;
        }

        public string Name { get; }

        public ushort TileId { get; }

        public byte R { get; }

        public byte G { get; }

        public byte B { get; }

        public sbyte? Altitude { get; }

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
