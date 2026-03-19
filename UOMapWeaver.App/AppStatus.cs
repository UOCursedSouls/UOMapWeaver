using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace UOMapWeaver.App;

public static class AppStatus
{
    public static event EventHandler<AppStatusMessage>? MessageChanged;
    public static event EventHandler<AppLogEntry>? LogAppended;
    public static event EventHandler? LogCleared;
    public static event EventHandler<bool>? CancelAvailableChanged;
    public static event EventHandler<AppProgressState>? ProgressChanged;

    private static CancellationTokenSource? _cancelSource;
    private static readonly List<AppLogEntry> _logHistory = new();
    private static readonly object _logLock = new();
    private static AppStatusSeverity _minimumSeverity = AppStatusSeverity.Info;
    private static readonly Stopwatch _operationTimer = new();
    private static string? _currentOperation;
    private static FileLogger? _fileLogger;

    public static AppStatusSeverity MinimumSeverity
    {
        get => _minimumSeverity;
        set => _minimumSeverity = value;
    }

    /// <summary>
    /// Initializes persistent file logging. Call once at app startup after data folders are created.
    /// </summary>
    public static void InitFileLogger()
    {
        _fileLogger = new FileLogger();
        if (_fileLogger.LogFilePath is not null)
        {
            AppendLog($"File logging started: {_fileLogger.LogFilePath}", AppStatusSeverity.Info);
        }
    }

    /// <summary>
    /// Flushes and closes the file logger. Call at app shutdown.
    /// </summary>
    public static void ShutdownFileLogger()
    {
        _fileLogger?.Dispose();
        _fileLogger = null;
    }

    public static string Stamp(string message)
    {
        return $"[{GetTimestamp()}] {message}";
    }

    public static string GetTimestamp()
    {
        return DateTime.Now.ToString("HH:mm:ss.fff");
    }

    public static void SetInfo(string message)
    {
        MessageChanged?.Invoke(null, new AppStatusMessage(message, AppStatusSeverity.Info));
    }

    public static void SetSuccess(string message)
    {
        MessageChanged?.Invoke(null, new AppStatusMessage(message, AppStatusSeverity.Success));
    }

    public static void SetError(string message)
    {
        MessageChanged?.Invoke(null, new AppStatusMessage(message, AppStatusSeverity.Error));
    }

    public static void SetWarning(string message)
    {
        MessageChanged?.Invoke(null, new AppStatusMessage(message, AppStatusSeverity.Warning));
    }

    public static void AppendLog(string message, AppStatusSeverity severity = AppStatusSeverity.Info)
    {
        var entry = new AppLogEntry(message, severity, DateTime.Now);

        lock (_logLock)
        {
            _logHistory.Add(entry);
        }

        // Write to persistent file log regardless of severity filter.
        _fileLogger?.Write(entry);

        if (severity >= _minimumSeverity)
        {
            LogAppended?.Invoke(null, entry);
        }
    }

    public static void ClearLog()
    {
        lock (_logLock)
        {
            _logHistory.Clear();
        }

        LogCleared?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Starts tracking elapsed time for a named operation.
    /// </summary>
    public static void BeginOperation(string operationName)
    {
        _currentOperation = operationName;
        _operationTimer.Restart();
        AppendLog($"Operation started: {operationName}", AppStatusSeverity.Info);
    }

    /// <summary>
    /// Ends the current operation and logs the elapsed time.
    /// </summary>
    public static TimeSpan EndOperation()
    {
        _operationTimer.Stop();
        var elapsed = _operationTimer.Elapsed;
        var name = _currentOperation ?? "Unknown";
        AppendLog($"Operation completed: {name} ({FormatElapsed(elapsed)})", AppStatusSeverity.Info);
        _currentOperation = null;
        return elapsed;
    }

    /// <summary>
    /// Returns the elapsed time of the current operation without stopping the timer.
    /// </summary>
    public static TimeSpan GetOperationElapsed() => _operationTimer.Elapsed;

    /// <summary>
    /// Returns whether an operation timer is currently running.
    /// </summary>
    public static bool IsOperationRunning => _operationTimer.IsRunning;

    /// <summary>
    /// Returns a snapshot of all log entries, optionally filtered by minimum severity.
    /// </summary>
    public static List<AppLogEntry> GetLogHistory(AppStatusSeverity? minimumSeverity = null)
    {
        lock (_logLock)
        {
            if (minimumSeverity.HasValue)
            {
                var min = minimumSeverity.Value;
                return _logHistory.Where(e => e.Severity >= min).ToList();
            }

            return new List<AppLogEntry>(_logHistory);
        }
    }

    /// <summary>
    /// Exports the log history to a file. Each line includes timestamp, severity prefix, and message.
    /// </summary>
    public static void ExportLog(string filePath, AppStatusSeverity? minimumSeverity = null)
    {
        var entries = GetLogHistory(minimumSeverity);
        var sb = new StringBuilder();

        foreach (var entry in entries)
        {
            sb.AppendLine(entry.ToFormattedString());
        }

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(filePath, sb.ToString());
    }

    public static void SetProgress(double percent, bool isActive, long? processed = null, long? total = null)
    {
        if (percent < 0)
        {
            percent = 0;
        }

        if (percent > 100)
        {
            percent = 100;
        }

        ProgressChanged?.Invoke(null, new AppProgressState(percent, isActive, processed, total));
    }

    public static void SetCancelSource(CancellationTokenSource? source)
    {
        _cancelSource = source;
        CancelAvailableChanged?.Invoke(null, _cancelSource != null);
    }

    public static void RequestCancel()
    {
        _cancelSource?.Cancel();
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed.TotalMinutes >= 1)
        {
            return $"{elapsed.Minutes}m {elapsed.Seconds}.{elapsed.Milliseconds:D3}s";
        }

        return $"{elapsed.TotalSeconds:F3}s";
    }
}

public readonly struct AppStatusMessage
{
    public AppStatusMessage(string message, AppStatusSeverity severity)
    {
        Message = message;
        Severity = severity;
    }

    public string Message { get; }

    public AppStatusSeverity Severity { get; }
}

public enum AppStatusSeverity
{
    Info,
    Success,
    Warning,
    Error
}

public readonly struct AppLogEntry
{
    public AppLogEntry(string message, AppStatusSeverity severity)
        : this(message, severity, DateTime.Now)
    {
    }

    public AppLogEntry(string message, AppStatusSeverity severity, DateTime timestamp)
    {
        Message = message;
        Severity = severity;
        Timestamp = timestamp;
    }

    public string Message { get; }

    public AppStatusSeverity Severity { get; }

    public DateTime Timestamp { get; }

    public string SeverityPrefix => Severity switch
    {
        AppStatusSeverity.Info => "[INFO]",
        AppStatusSeverity.Success => "[INFO]",
        AppStatusSeverity.Warning => "[WARN]",
        AppStatusSeverity.Error => "[ERROR]",
        _ => "[INFO]"
    };

    /// <summary>
    /// Returns a formatted log line: [HH:mm:ss.fff] [SEVERITY] message
    /// </summary>
    public string ToFormattedString()
    {
        return $"[{Timestamp:HH:mm:ss.fff}] {SeverityPrefix} {Message}";
    }
}

public readonly struct AppProgressState
{
    public AppProgressState(double percent, bool isActive, long? processed, long? total)
    {
        Percent = percent;
        IsActive = isActive;
        Processed = processed;
        Total = total;
    }

    public double Percent { get; }

    public bool IsActive { get; }

    public long? Processed { get; }

    public long? Total { get; }
}
