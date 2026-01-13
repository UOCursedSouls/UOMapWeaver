using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UOMapWeaver.App;
using UOMapWeaver.Core;

namespace UOMapWeaver.App.Views;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<LogEntryViewModel> _logEntries = new();
    private readonly ConcurrentQueue<AppLogEntry> _pendingLogs = new();
    private readonly DispatcherTimer _logFlushTimer;
    private string _lastBannerMessage = string.Empty;
    private AppStatusSeverity _lastBannerSeverity = AppStatusSeverity.Info;
    private string _lastStatusMessage = string.Empty;
    private AppStatusSeverity _lastStatusSeverity = AppStatusSeverity.Info;
    private DateTime _lastStatusTimestamp = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        LogsToggle.IsCheckedChanged += (_, _) =>
        {
            LogOverlay.IsVisible = LogsToggle.IsChecked == true;
        };
        SaveStateToggle.IsChecked = AppSettings.SaveEnabled;
        SaveStateToggle.IsCheckedChanged += (_, _) =>
        {
            AppSettings.SetSaveEnabled(SaveStateToggle.IsChecked == true);
        };
        ResetStateButton.Click += (_, _) => ResetUiState();
        CancelButton.Click += (_, _) => AppStatus.RequestCancel();
        DataReadmeButton.Click += (_, _) => OpenDataReadme();

        LogList.ItemsSource = _logEntries;
        LogList.KeyDown += OnLogListKeyDown;

        AppStatus.MessageChanged += OnStatusMessageChanged;
        AppStatus.LogAppended += OnLogAppended;
        AppStatus.LogCleared += OnLogCleared;
        AppStatus.CancelAvailableChanged += (_, available) => CancelButton.IsEnabled = available;
        AppStatus.ProgressChanged += OnProgressChanged;

        _logFlushTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _logFlushTimer.Tick += (_, _) => FlushLogQueue();
        _logFlushTimer.Start();
    }

    public void ShowLogOverlay()
    {
        LogsToggle.IsChecked = true;
    }

    private void OnStatusMessageChanged(object? sender, AppStatusMessage e)
    {
        var timestamp = AppStatus.GetTimestamp();
        BannerTimestampRun.Text = $"[{timestamp}]";
        BannerMessageRun.Text = e.Message;
        _lastBannerMessage = e.Message;
        _lastBannerSeverity = e.Severity;
        BannerMessageRun.Foreground = GetSeverityBrush(e.Severity);

        if (!string.IsNullOrWhiteSpace(e.Message))
        {
            AppendLogEntry(e.Message, e.Severity);
            _lastStatusMessage = e.Message;
            _lastStatusSeverity = e.Severity;
            _lastStatusTimestamp = DateTime.UtcNow;
        }
    }

    private void OnLogAppended(object? sender, AppLogEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Message))
        {
            return;
        }

        if (ShouldSkipLog(entry))
        {
            return;
        }

        _pendingLogs.Enqueue(entry);
    }

    private void OnLogCleared(object? sender, EventArgs e)
    {
        _logEntries.Clear();
        _lastBannerMessage = string.Empty;
        _lastBannerSeverity = AppStatusSeverity.Info;
        _lastStatusMessage = string.Empty;
        _lastStatusSeverity = AppStatusSeverity.Info;
        _lastStatusTimestamp = DateTime.MinValue;
        while (_pendingLogs.TryDequeue(out _))
        {
        }
    }

    private void OnProgressChanged(object? sender, AppProgressState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            GlobalProgressBar.IsVisible = state.IsActive;
            GlobalProgressBar.Value = state.Percent;
            GlobalProgressText.Text = state.IsActive ? $"{state.Percent}%" : string.Empty;
        });
    }

    private void AppendLogEntry(string message, AppStatusSeverity severity)
    {
        var entry = CreateLogEntry(message, severity);

        if (Dispatcher.UIThread.CheckAccess())
        {
            _logEntries.Add(entry);
            LogList.ScrollIntoView(entry);
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                _logEntries.Add(entry);
                LogList.ScrollIntoView(entry);
            });
        }
    }

    private void FlushLogQueue()
    {
        if (_pendingLogs.IsEmpty)
        {
            return;
        }

        var count = 0;
        while (count < 250 && _pendingLogs.TryDequeue(out var entry))
        {
            _logEntries.Add(CreateLogEntry(entry.Message, entry.Severity));
            count++;
        }

        if (_logEntries.Count > 0)
        {
            LogList.ScrollIntoView(_logEntries[^1]);
        }
    }

    private static LogEntryViewModel CreateLogEntry(string message, AppStatusSeverity severity)
    {
        var timestamp = $"[{AppStatus.GetTimestamp()}]";
        return new LogEntryViewModel(timestamp, message, Brushes.Gray, GetSeverityBrush(severity));
    }

    private bool ShouldSkipLog(AppLogEntry entry)
    {
        if (entry.Message != _lastStatusMessage || entry.Severity != _lastStatusSeverity)
        {
            return false;
        }

        return (DateTime.UtcNow - _lastStatusTimestamp) < TimeSpan.FromSeconds(2);
    }

    private static IBrush GetSeverityBrush(AppStatusSeverity severity)
    {
        return severity switch
        {
            AppStatusSeverity.Error => Brushes.OrangeRed,
            AppStatusSeverity.Warning => Brushes.Gold,
            AppStatusSeverity.Success => Brushes.LightGreen,
            _ => Brushes.White
        };
    }

    private void OpenDataReadme()
    {
        try
        {
            var path = Path.Combine(UOMapWeaverDataPaths.DataRoot, "README.txt");
            if (!File.Exists(path))
            {
                UOMapWeaverDataBootstrapper.EnsureDataFolders();
            }

            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            else
            {
                AppStatus.SetWarning("Data README not found.");
            }
        }
        catch (Exception ex)
        {
            AppStatus.SetWarning($"Unable to open data README: {ex.Message}");
        }
    }

    private void ResetUiState()
    {
        AppSettings.Reset();
        AppStatus.SetInfo("Saved fields cleared. Restart to reset UI.");
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        foreach (var view in this.GetVisualDescendants().OfType<IAppStateView>())
        {
            view.PersistState();
        }

        AppSettings.Save();
    }

    private void OnLogListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = CopySelectedLogsToClipboardAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SelectAllLogs();
            e.Handled = true;
        }
    }

    private async void OnCopySelectedLogs(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await CopySelectedLogsToClipboardAsync();
    }

    private void OnSelectAllLogs(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        SelectAllLogs();
    }

    private async void OnCopyAllLogs(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await CopyAllLogsToClipboardAsync();
    }

    private async Task CopySelectedLogsToClipboardAsync()
    {
        var selected = LogList.SelectedItems?.OfType<LogEntryViewModel>().ToList();
        if (selected is null || selected.Count == 0)
        {
            return;
        }

        var text = string.Join(Environment.NewLine, selected.Select(item => $"{item.Timestamp} {item.Message}"));
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        await clipboard.SetTextAsync(text);
        AppStatus.SetInfo("Log copied to clipboard.");
    }

    private void SelectAllLogs()
    {
        LogList.SelectAll();
        if (_logEntries.Count > 0)
        {
            LogList.ScrollIntoView(_logEntries[^1]);
        }
    }

    private async Task CopyAllLogsToClipboardAsync()
    {
        if (_logEntries.Count == 0)
        {
            return;
        }

        var text = string.Join(Environment.NewLine, _logEntries.Select(item => $"{item.Timestamp} {item.Message}"));
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        await clipboard.SetTextAsync(text);
        AppStatus.SetInfo("All log entries copied to clipboard.");
    }

    private sealed record LogEntryViewModel(string Timestamp, string Message, IBrush TimestampForeground, IBrush MessageForeground);
}

