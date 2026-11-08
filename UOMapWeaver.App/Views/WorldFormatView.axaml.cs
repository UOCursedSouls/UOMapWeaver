using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using UOMapWeaver.Core.WorldFormats;
using UOMapWeaver.Plugins;
using static UOMapWeaver.App.Views.ViewHelpers;

namespace UOMapWeaver.App.Views;

public sealed partial class WorldFormatView : UserControl
{
    public WorldFormatView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => RefreshPlugins();
        RefreshPlugins();
    }

    private IWorldFormatPlugin? Plugin => PluginComboBox.SelectedItem as IWorldFormatPlugin;

    private void RefreshPlugins()
    {
        var plugins = AppPlugins.WorldFormats;
        PluginComboBox.ItemsSource = plugins.Select(p => p).ToList();
        PluginComboBox.DisplayMemberBinding = new Avalonia.Data.Binding(nameof(IWorldFormatPlugin.DisplayName));
        PluginComboBox.SelectedIndex = plugins.Count > 0 ? 0 : -1;
        var any = plugins.Count > 0;
        ImportButton.IsEnabled = any;
        ExportButton.IsEnabled = any;
        PluginStatusText.Text = any ? string.Empty : WorldFormatConversion.NoPluginMessage;
    }

    private async void OnBrowseImportMap(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ImportMapBox.Text = await PickFileAsync(this, "Select map", ["mul", "uop"]) ?? ImportMapBox.Text;

    private async void OnBrowseImportStaIdx(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ImportStaIdxBox.Text = await PickFileAsync(this, "Select staidx.mul", ["mul"]) ?? ImportStaIdxBox.Text;

    private async void OnBrowseImportStatics(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ImportStaticsBox.Text = await PickFileAsync(this, "Select statics.mul", ["mul"]) ?? ImportStaticsBox.Text;

    private async void OnBrowseImportWorld(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ImportWorldBox.Text = await PickFolderAsync(this, "Select world folder") ?? ImportWorldBox.Text;

    private async void OnBrowseExportWorld(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ExportWorldBox.Text = await PickFolderAsync(this, "Select world folder") ?? ExportWorldBox.Text;

    private async void OnBrowseExportOutput(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ExportOutputBox.Text = await PickFolderAsync(this, "Select output folder") ?? ExportOutputBox.Text;

    private static int Num(TextBox box) => int.Parse(box.Text?.Trim() ?? string.Empty);

    private async void OnImport(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var plugin = Plugin;
        var map = ImportMapBox.Text?.Trim();
        var world = ImportWorldBox.Text?.Trim();
        if (plugin is null || string.IsNullOrEmpty(map) || !File.Exists(map) || string.IsNullOrEmpty(world))
        {
            Report(ImportStatusText, "Choose a plugin, an existing map file and a world folder.", AppStatusSeverity.Error);
            return;
        }

        string? staIdx = ImportStaIdxBox.Text?.Trim(), statics = ImportStaticsBox.Text?.Trim();
        int mw, mh, ax, ay, aw, ah, uop;
        try
        {
            (mw, mh, ax, ay, aw, ah, uop) = (Num(MapWidthBox), Num(MapHeightBox), Num(AreaXBox), Num(AreaYBox), Num(AreaWBox), Num(AreaHBox), Num(UopIndexBox));
        }
        catch (FormatException)
        {
            Report(ImportStatusText, "Size, area and UOP index must be whole numbers.", AppStatusSeverity.Error);
            return;
        }

        await RunAsync(ImportButton, ImportStatusText, "Import", progress =>
        {
            var blocks = WorldFormatConversion.ImportArea(plugin, world, Path.GetFileName(world.TrimEnd('/', '\\')), map,
                staIdx, statics, mw, mh, ax, ay, aw, ah, uop, progress);
            return $"Imported {blocks} blocks.";
        });
    }

    private async void OnExport(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var plugin = Plugin;
        var world = ExportWorldBox.Text?.Trim();
        var output = ExportOutputBox.Text?.Trim();
        if (plugin is null || string.IsNullOrEmpty(world) || !Directory.Exists(world) || string.IsNullOrEmpty(output))
        {
            Report(ExportStatusText, "Choose a plugin, an existing world folder and an output folder.", AppStatusSeverity.Error);
            return;
        }

        var mapOut = Path.Combine(output, "map0.mul");
        var idxOut = Path.Combine(output, "staidx0.mul");
        var staticsOut = Path.Combine(output, "statics0.mul");
        (int, int, int, int)? area;
        try
        {
            area = string.IsNullOrWhiteSpace(BlockWBox.Text)
                ? null
                : (Num(BlockXBox), Num(BlockYBox), Num(BlockWBox), Num(BlockHBox));
        }
        catch (FormatException)
        {
            Report(ExportStatusText, "The block area must be whole numbers.", AppStatusSeverity.Error);
            return;
        }

        var existing = new[] { mapOut, idxOut, staticsOut }.Where(File.Exists).ToArray();
        if (existing.Length > 0 && !await ConfirmOverwriteAsync(this, "Overwrite existing files?", existing))
        {
            Report(ExportStatusText, "Export cancelled.", AppStatusSeverity.Warning);
            return;
        }

        await RunAsync(ExportButton, ExportStatusText, "Export", progress =>
        {
            Directory.CreateDirectory(output);
            var (w, h) = WorldFormatConversion.ExportWorld(plugin, world, mapOut, idxOut, staticsOut, area, progress, overwrite: true);
            return $"Exported {w} x {h} tiles.";
        });
    }

    private async Task RunAsync(Button button, TextBlock status, string name, Func<IProgress<int>, string> work)
    {
        button.IsEnabled = false;
        try
        {
            AppStatus.SetProgress(0, true);
            var message = await Task.Run(() => work(CreateAppProgress()));
            Report(status, message, AppStatusSeverity.Success);
        }
        catch (Exception ex)
        {
            Report(status, $"{name} error: {ex.Message}", AppStatusSeverity.Error);
        }
        finally
        {
            AppStatus.SetProgress(0, false);
            button.IsEnabled = Plugin is not null;
        }
    }

    private static void Report(TextBlock status, string message, AppStatusSeverity severity)
    {
        status.Text = AppStatus.Stamp(message);
        AppStatus.AppendLog(message, severity);
    }
}
