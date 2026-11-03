using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UOMapWeaver.Core.Map;
using UOMapWeaver.Core.TileColors;

namespace UOMapWeaver.App.Views;

internal static class ViewHelpers
{
    internal enum FieldState
    {
        Neutral,
        Valid,
        Warning,
        Error
    }

    internal static void SetFieldState(TextBox box, FieldState state, bool isOptional = false)
    {
        if (isOptional && string.IsNullOrWhiteSpace(box.Text))
        {
            box.ClearValue(TemplatedControl.BorderBrushProperty);
            box.ClearValue(TemplatedControl.ForegroundProperty);
            return;
        }

        ApplyFieldState(box, state);
    }

    internal static void SetFieldState(ComboBox box, FieldState state)
    {
        ApplyFieldState(box, state);
    }

    internal static void SetFieldState(TemplatedControl control, FieldState state)
    {
        ApplyFieldState(control, state);
    }

    internal static string FormatFileSize(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{info.Length:N0} bytes";
        }
        catch
        {
            return "unknown";
        }
    }

    internal static IProgress<int> CreateAppProgress(Action<int>? onProgress = null)
    {
        return new Progress<int>(percent =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                AppStatus.SetProgress(percent, true);
                onProgress?.Invoke(percent);
            });
        });
    }

    internal static IProgress<double> CreateAppProgressDouble(Action<double>? onProgress = null)
    {
        return new Progress<double>(percent =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                AppStatus.SetProgress(percent, true);
                onProgress?.Invoke(percent);
            });
        });
    }

    internal static IProgress<TileColorProgress> CreateTileColorProgress(Action<TileColorProgress>? onProgress = null)
    {
        return new Progress<TileColorProgress>(progress =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                AppStatus.SetProgress(progress.Percent, true, progress.ProcessedTiles, progress.TotalTiles);
                onProgress?.Invoke(progress);
            });
        });
    }

    internal static async Task<string?> PickFileAsync(Visual visual, string title, IEnumerable<string> extensions)
    {
        var provider = GetStorageProvider(visual);
        if (provider is null)
        {
            return null;
        }

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = title,
            FileTypeFilter = new[]
            {
                CreateFileTypeFilter(string.Join(", ", extensions), extensions)
            }
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    internal static async Task<IReadOnlyList<string>> PickFilesAsync(Visual visual, string title, IEnumerable<string> extensions)
    {
        var provider = GetStorageProvider(visual);
        if (provider is null)
        {
            return Array.Empty<string>();
        }

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = true,
            Title = title,
            FileTypeFilter = new[]
            {
                CreateFileTypeFilter(string.Join(", ", extensions), extensions)
            }
        });

        return files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToArray();
    }

    internal static async Task<string?> PickFolderAsync(Visual visual, string title)
    {
        var provider = GetStorageProvider(visual);
        if (provider is null)
        {
            return null;
        }

        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    internal static async Task<string?> PickSaveFileAsync(Visual visual, string title, string defaultExtension)
    {
        var provider = GetStorageProvider(visual);
        if (provider is null)
        {
            return null;
        }

        var file = await provider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            DefaultExtension = defaultExtension,
            FileTypeChoices = new[]
            {
                CreateFileTypeFilter(defaultExtension, new[] { defaultExtension })
            }
        });

        return file?.TryGetLocalPath();
    }

    internal static AppStatusSeverity MapLogToStatus(MapConversionLogLevel level)
    {
        return level switch
        {
            MapConversionLogLevel.Error => AppStatusSeverity.Error,
            MapConversionLogLevel.Warning => AppStatusSeverity.Warning,
            MapConversionLogLevel.Success => AppStatusSeverity.Success,
            _ => AppStatusSeverity.Info
        };
    }

    private static void ApplyFieldState(TemplatedControl control, FieldState state)
    {
        if (state == FieldState.Neutral)
        {
            control.ClearValue(TemplatedControl.BorderBrushProperty);
            control.ClearValue(TemplatedControl.ForegroundProperty);
            return;
        }

        var brush = state switch
        {
            FieldState.Warning => Brushes.Goldenrod,
            FieldState.Error => Brushes.IndianRed,
            _ => Brushes.ForestGreen
        };

        control.BorderBrush = brush;
        control.Foreground = brush;
    }

    private static IStorageProvider? GetStorageProvider(Visual visual)
        => TopLevel.GetTopLevel(visual)?.StorageProvider;

    /// <summary>
    /// Gets the host Window for a UserControl by walking the visual tree.
    /// </summary>
    internal static Window? GetHostWindow(Visual visual) => visual.GetVisualRoot() as Window;

    /// <summary>
    /// Gets the owner Window, throwing if not available.
    /// </summary>
    internal static Window GetOwnerWindow(Visual visual)
        => GetHostWindow(visual) ?? throw new InvalidOperationException("Host window not available.");

    /// <summary>
    /// Builds a confirmation dialog with Overwrite/Cancel buttons.
    /// </summary>
    internal static Window BuildConfirmDialog(string title, string message)
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

    /// <summary>
    /// Shows a confirmation dialog if any of the specified paths already exist.
    /// Returns true if no files exist or if the user confirms overwrite.
    /// </summary>
    internal static async Task<bool> ConfirmOverwriteAsync(Visual visual, string title, params string[] paths)
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
        return await dialog.ShowDialog<bool>(GetOwnerWindow(visual));
    }

    private static FilePickerFileType CreateFileTypeFilter(string name, IEnumerable<string> extensions)
    {
        var patterns = extensions
            .Select(ext => ext.StartsWith('.') ? $"*{ext}" : $"*.{ext}")
            .ToList();

        return new FilePickerFileType(name)
        {
            Patterns = patterns
        };
    }
}
