using System;

namespace UOMapWeaver.App;

public static class AppStatus
{
    public static event EventHandler<AppStatusMessage>? MessageChanged;
    public static event EventHandler<AppLogEntry>? LogAppended;
    public static event EventHandler? LogCleared;
    public static event EventHandler<bool>? CancelAvailableChanged;
    public static event EventHandler<AppProgressState>? ProgressChanged;

    private static CancellationTokenSource? _cancelSource;

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
        LogAppended?.Invoke(null, new AppLogEntry(message, severity));
    }

    public static void ClearLog()
    {
        LogCleared?.Invoke(null, EventArgs.Empty);
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
    {
        Message = message;
        Severity = severity;
    }

    public string Message { get; }

    public AppStatusSeverity Severity { get; }
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

