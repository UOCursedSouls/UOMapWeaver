using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using UOMapWeaver.Core.ResourcePack;
using System;
using System.IO;
using System.Threading.Tasks;

namespace UOMapWeaver.App.Views;

public partial class ResourcePackView : UserControl
{
    public ResourcePackView()
    {
        InitializeComponent();
    }

    private async void OnBrowseUoFolder(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select UO Client Folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
        {
            UoFolderBox.Text = folders[0].Path.LocalPath;
            ValidateUoFolder();
        }
    }

    private async void OnBrowseComponentsFolder(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select ModernUO Components Folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
            ComponentsFolderBox.Text = folders[0].Path.LocalPath;
    }

    private async void OnBrowseOutputFolder(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Output Folder",
            AllowMultiple = false
        });

        if (folders.Count > 0)
            OutputFolderBox.Text = folders[0].Path.LocalPath;
    }

    private void ValidateUoFolder()
    {
        var path = UoFolderBox.Text;
        if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
        {
            UoFolderStatus.Text = "❌ Folder not found";
            return;
        }

        var hasArt = File.Exists(Path.Combine(path, "artLegacyMUL.uop")) ||
                     File.Exists(Path.Combine(path, "art.mul"));
        var hasTiledata = File.Exists(Path.Combine(path, "tiledata.mul"));
        var hasRadarcol = File.Exists(Path.Combine(path, "radarcol.mul"));
        var hasGumps = File.Exists(Path.Combine(path, "gumpartLegacyMUL.uop")) ||
                       File.Exists(Path.Combine(path, "gumpart.mul"));
        var hasSounds = File.Exists(Path.Combine(path, "soundLegacyMUL.uop")) ||
                        File.Exists(Path.Combine(path, "sound.mul"));

        var status = $"Art: {(hasArt ? "✅" : "❌")} | TileData: {(hasTiledata ? "✅" : "❌")} | " +
                     $"RadarCol: {(hasRadarcol ? "✅" : "❌")} | Gumps: {(hasGumps ? "✅" : "❌")} | " +
                     $"Sounds: {(hasSounds ? "✅" : "❌")}";
        UoFolderStatus.Text = status;
    }

    private async void OnBuild(object? sender, RoutedEventArgs e)
    {
        var uoPath = UoFolderBox.Text;
        var outputPath = OutputFolderBox.Text;

        if (string.IsNullOrEmpty(uoPath) || !Directory.Exists(uoPath))
        {
            BuildStatus.Text = "❌ Select a valid UO folder first";
            return;
        }
        if (string.IsNullOrEmpty(outputPath))
        {
            BuildStatus.Text = "❌ Select an output folder";
            return;
        }

        BuildButton.IsEnabled = false;
        BuildProgress.IsVisible = true;
        BuildLogList.Items.Clear();

        void Log(string msg)
        {
            Dispatcher.UIThread.Post(() =>
            {
                BuildLogList.Items.Add(msg);
                BuildLogList.ScrollIntoView(msg);
            });
        }

        try
        {
            await Task.Run(() =>
            {
                // Find file paths
                var artIdxPath = Path.Combine(uoPath, "artidx.mul");
                var artMulPath = File.Exists(Path.Combine(uoPath, "artLegacyMUL.uop"))
                    ? Path.Combine(uoPath, "artLegacyMUL.uop")
                    : Path.Combine(uoPath, "art.mul");
                var tileDataPath = Path.Combine(uoPath, "tiledata.mul");
                var radarColPath = Path.Combine(uoPath, "radarcol.mul");
                var componentsDir = string.IsNullOrEmpty(ComponentsFolderBox.Text) ? null : ComponentsFolderBox.Text;
                var musicDir = Path.Combine(uoPath, "Music", "Digital");
                if (!Directory.Exists(musicDir)) musicDir = null;
                var soundPath = File.Exists(Path.Combine(uoPath, "soundLegacyMUL.uop"))
                    ? Path.Combine(uoPath, "soundLegacyMUL.uop")
                    : null;

                Log($"UO Folder: {uoPath}");
                Log($"Output: {outputPath}");
                Log("Building Resource Pack...");

                var builder = new ResourcePackBuilder(
                    artIdxPath, artMulPath, tileDataPath, radarColPath,
                    componentsDir, musicDir, soundPath, uoPath);

                var stats = builder.BuildComplete(outputPath, (phase, current, total) =>
                {
                    Log($"  [{phase}] {current}/{total}");
                    if (total > 0)
                    {
                        Dispatcher.UIThread.Post(() =>
                            BuildProgress.Value = (double)current / total * 100);
                    }
                });

                Log("");
                Log($"=== RESOURCE PACK COMPLETE ===");
                Log($"  Land sprites: {stats.LandSpritesSaved}");
                Log($"  Static sprites: {stats.StaticSpritesSaved}");
                Log($"  Gumps: {stats.GumpsExtracted}");
                Log($"  Music: {stats.MusicFilesCopied}");
                Log($"  Sounds: {stats.SoundFilesExtracted}");
                Log($"  Metadata: {stats.MetadataFiles} files");
                Log($"  Directions: {stats.DirectionMappings}");

                Dispatcher.UIThread.Post(() =>
                {
                    BuildStatus.Text = $"✅ Done! {stats.LandSpritesSaved + stats.StaticSpritesSaved} sprites + {stats.GumpsExtracted} gumps";
                    BuildProgress.Value = 100;
                });
            });
        }
        catch (Exception ex)
        {
            Log($"❌ ERROR: {ex.Message}");
            BuildStatus.Text = $"❌ {ex.Message}";
        }
        finally
        {
            BuildButton.IsEnabled = true;
        }
    }

    private void OnOpenOutput(object? sender, RoutedEventArgs e)
    {
        var path = OutputFolderBox.Text;
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
    }
}
