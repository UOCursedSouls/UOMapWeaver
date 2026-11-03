using System;
using System.IO;
using System.Linq;
using UOMapWeaver.Core;

namespace UOMapWeaver.App;

/// <summary>
/// Persistent file logger that writes all AppStatus log entries to a timestamped log file.
/// Log files are stored in a logs/ directory next to the executable.
/// Maintains a maximum of 10 log files, deleting the oldest when exceeded.
/// </summary>
public sealed class FileLogger : IDisposable
{
    private const int MaxLogFiles = 10;

    private readonly StreamWriter? _writer;
    private readonly string _logDirectory;
    private readonly object _writeLock = new();
    private bool _disposed;

    public string? LogFilePath { get; }

    public FileLogger()
    {
        _logDirectory = Path.Combine(UOMapWeaverDataPaths.DataRoot, "logs");

        try
        {
            Directory.CreateDirectory(_logDirectory);
            RotateLogFiles();

            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
            var fileName = $"UOMapWeaver_{timestamp}.log";
            LogFilePath = Path.Combine(_logDirectory, fileName);

            _writer = new StreamWriter(LogFilePath, append: false)
            {
                AutoFlush = true
            };

            _writer.WriteLine($"UOMapWeaver Log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _writer.WriteLine(new string('-', 60));
        }
        catch
        {
            // File logging is best-effort; do not block the app if it fails.
            _writer = null;
            LogFilePath = null;
        }
    }

    /// <summary>
    /// Writes a formatted log entry to the log file.
    /// </summary>
    public void Write(AppLogEntry entry)
    {
        if (_disposed || _writer is null)
        {
            return;
        }

        lock (_writeLock)
        {
            try
            {
                _writer.WriteLine(entry.ToFormattedString());
            }
            catch
            {
                // Ignore write errors.
            }
        }
    }

    /// <summary>
    /// Writes a raw message line to the log file.
    /// </summary>
    public void WriteLine(string message)
    {
        if (_disposed || _writer is null)
        {
            return;
        }

        lock (_writeLock)
        {
            try
            {
                _writer.WriteLine(message);
            }
            catch
            {
                // Ignore write errors.
            }
        }
    }

    /// <summary>
    /// Deletes the oldest log files when the count exceeds the maximum.
    /// </summary>
    private void RotateLogFiles()
    {
        try
        {
            var logFiles = Directory.GetFiles(_logDirectory, "UOMapWeaver_*.log")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            // Keep MaxLogFiles - 1 to make room for the new file about to be created.
            while (logFiles.Count >= MaxLogFiles)
            {
                var oldest = logFiles[^1];
                oldest.Delete();
                logFiles.RemoveAt(logFiles.Count - 1);
            }
        }
        catch
        {
            // Rotation is best-effort.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_writeLock)
        {
            try
            {
                _writer?.WriteLine(new string('-', 60));
                _writer?.WriteLine($"Log closed — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
                // Ignore disposal errors.
            }
        }
    }
}
