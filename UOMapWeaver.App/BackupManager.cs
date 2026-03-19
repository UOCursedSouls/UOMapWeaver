using System;
using System.IO;
using System.Linq;
using UOMapWeaver.Core;

namespace UOMapWeaver.App;

/// <summary>
/// Creates backups of destination files before destructive operations.
/// Backups are stored in a backups/ directory under UOMapWeaverData.
/// Maintains a maximum of 5 backups per original file name, rotating the oldest.
/// </summary>
public static class BackupManager
{
    private const int MaxBackupsPerFile = 5;

    public static string BackupDirectory => Path.Combine(UOMapWeaverDataPaths.DataRoot, "backups");

    /// <summary>
    /// Creates a backup of the specified file if it exists.
    /// Returns the backup file path, or null if the file does not exist or backup failed.
    /// </summary>
    public static string? BackupFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            Directory.CreateDirectory(BackupDirectory);

            var originalName = Path.GetFileName(filePath);
            var nameWithoutExt = Path.GetFileNameWithoutExtension(filePath);
            var ext = Path.GetExtension(filePath);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var backupName = $"{nameWithoutExt}_{timestamp}.bak{ext}";
            var backupPath = Path.Combine(BackupDirectory, backupName);

            File.Copy(filePath, backupPath, overwrite: true);

            RotateBackups(originalName, nameWithoutExt, ext);

            AppStatus.AppendLog($"Backup created: {backupPath}", AppStatusSeverity.Info);
            return backupPath;
        }
        catch (Exception ex)
        {
            AppStatus.AppendLog($"Backup failed for {filePath}: {ex.Message}", AppStatusSeverity.Warning);
            return null;
        }
    }

    /// <summary>
    /// Creates backups of multiple files. Returns the count of successful backups.
    /// </summary>
    public static int BackupFiles(params string[] filePaths)
    {
        var count = 0;
        foreach (var path in filePaths)
        {
            if (BackupFile(path) is not null)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Returns the most recent backup for a given original file name, or null if none exists.
    /// </summary>
    public static string? GetLatestBackup(string originalFileName)
    {
        if (!Directory.Exists(BackupDirectory))
        {
            return null;
        }

        var nameWithoutExt = Path.GetFileNameWithoutExtension(originalFileName);
        var ext = Path.GetExtension(originalFileName);
        var pattern = $"{nameWithoutExt}_*.bak{ext}";

        return Directory.GetFiles(BackupDirectory, pattern)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTimeUtc)
            .FirstOrDefault()
            ?.FullName;
    }

    /// <summary>
    /// Restores the most recent backup for the given original file, copying it to the specified destination.
    /// Returns true if restoration succeeded.
    /// </summary>
    public static bool RestoreLatestBackup(string originalFileName, string destinationPath)
    {
        var backupPath = GetLatestBackup(originalFileName);
        if (backupPath is null || !File.Exists(backupPath))
        {
            AppStatus.AppendLog($"No backup found for {originalFileName}.", AppStatusSeverity.Warning);
            return false;
        }

        try
        {
            var destDir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            File.Copy(backupPath, destinationPath, overwrite: true);
            AppStatus.AppendLog($"Restored backup: {backupPath} -> {destinationPath}", AppStatusSeverity.Success);
            return true;
        }
        catch (Exception ex)
        {
            AppStatus.AppendLog($"Restore failed: {ex.Message}", AppStatusSeverity.Error);
            return false;
        }
    }

    /// <summary>
    /// Lists all backups for a given original file name, newest first.
    /// </summary>
    public static string[] ListBackups(string originalFileName)
    {
        if (!Directory.Exists(BackupDirectory))
        {
            return Array.Empty<string>();
        }

        var nameWithoutExt = Path.GetFileNameWithoutExtension(originalFileName);
        var ext = Path.GetExtension(originalFileName);
        var pattern = $"{nameWithoutExt}_*.bak{ext}";

        return Directory.GetFiles(BackupDirectory, pattern)
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.CreationTimeUtc)
            .Select(f => f.FullName)
            .ToArray();
    }

    /// <summary>
    /// Keeps only the newest MaxBackupsPerFile backups for the given file, deleting the rest.
    /// </summary>
    private static void RotateBackups(string originalName, string nameWithoutExt, string ext)
    {
        try
        {
            var pattern = $"{nameWithoutExt}_*.bak{ext}";
            var backups = Directory.GetFiles(BackupDirectory, pattern)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTimeUtc)
                .ToList();

            while (backups.Count > MaxBackupsPerFile)
            {
                var oldest = backups[^1];
                oldest.Delete();
                backups.RemoveAt(backups.Count - 1);
            }
        }
        catch
        {
            // Rotation is best-effort.
        }
    }
}
