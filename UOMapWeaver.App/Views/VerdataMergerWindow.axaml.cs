using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using UOMapWeaver.Core.Map;
using static UOMapWeaver.App.Views.ViewHelpers;
using FieldState = UOMapWeaver.App.Views.ViewHelpers.FieldState;

namespace UOMapWeaver.App.Views;

public sealed partial class VerdataMergerView : UserControl, IAppStateView
{
    private readonly ObservableCollection<string> _breakdown = new();
    private readonly ObservableCollection<string> _warnings = new();
    private bool _loadingState;
    private bool _busy;

    public VerdataMergerView()
    {
        InitializeComponent();
        PatchBreakdownList.ItemsSource = _breakdown;
        WarningsList.ItemsSource = _warnings;
        LoadState();
        UpdateFieldStates();
    }

    // ------------------------------------------------------------------ //
    //  Browse handlers
    // ------------------------------------------------------------------ //

    private async void OnBrowseVerdata(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync(this, "Select verdata.mul", new[] { "mul" });
        if (!string.IsNullOrWhiteSpace(path))
        {
            VerdataPathBox.Text = path;
            UpdateFieldStates();
            SaveState();
        }
    }

    private async void OnBrowseMulFolder(object? sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync(this, "Select MUL folder");
        if (!string.IsNullOrWhiteSpace(path))
        {
            MulFolderBox.Text = path;
            UpdateFieldStates();
            SaveState();
        }
    }

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_loadingState) return;
        UpdateFieldStates();
        SaveState();
    }

    // ------------------------------------------------------------------ //
    //  Analyze — read-only scan of verdata contents
    // ------------------------------------------------------------------ //

    private async void OnAnalyze(object? sender, RoutedEventArgs e)
    {
        if (!ValidateInputs()) return;
        SetBusy(true);
        _breakdown.Clear();
        _warnings.Clear();

        var verdataPath = VerdataPathBox.Text!.Trim();

        try
        {
            AppStatus.SetInfo("Reading verdata.mul...");
            AppStatus.AppendLog($"Analyzing: {verdataPath}", AppStatusSeverity.Info);

            List<VerdataMulMerger.VerdataPatch>? patches = null;
            await Task.Run(() => patches = VerdataMulMerger.ReadPatches(verdataPath));

            if (patches is null || patches.Count == 0)
            {
                AppStatus.SetWarning("No patches found in verdata.mul.");
                _breakdown.Add("No patches found.");
                return;
            }

            _breakdown.Add($"Total patches: {patches.Count}");
            _breakdown.Add("");

            var grouped = patches.GroupBy(p => p.FileId)
                .OrderByDescending(g => g.Count());

            foreach (var group in grouped)
            {
                var name = VerdataMulMerger.FileNameForId(group.Key) ?? $"unknown (0x{group.Key:X2})";
                var sizes = group.Select(p => p.Length);
                _breakdown.Add($"  {name,-20} {group.Count(),5} patches   (size range: {sizes.Min()}-{sizes.Max()} bytes)");
                AppStatus.AppendLog($"  {name}: {group.Count()} patches", AppStatusSeverity.Info);
            }

            var totalBytes = patches.Sum(p => (long)p.Length);
            _breakdown.Add("");
            _breakdown.Add($"Total patch data: {totalBytes:N0} bytes");

            AppStatus.SetSuccess($"Analysis complete: {patches.Count} patches across {grouped.Count()} files.");
        }
        catch (Exception ex)
        {
            AppStatus.SetError($"Analysis failed: {ex.Message}");
            _warnings.Add(ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ------------------------------------------------------------------ //
    //  Merge — apply verdata patches into base MUL files
    // ------------------------------------------------------------------ //

    private async void OnMerge(object? sender, RoutedEventArgs e)
    {
        if (!ValidateInputs()) return;
        SetBusy(true);
        _breakdown.Clear();
        _warnings.Clear();

        var verdataPath = VerdataPathBox.Text!.Trim();
        var mulFolder = MulFolderBox.Text!.Trim();
        var doBackup = BackupCheckBox.IsChecked == true;
        var deleteVerdata = DeleteVerdataCheckBox.IsChecked == true;

        using var cts = new CancellationTokenSource();
        AppStatus.SetCancelSource(cts);

        try
        {
            AppStatus.SetInfo("Reading verdata.mul patches...");
            AppStatus.AppendLog($"Merge started: {verdataPath} -> {mulFolder}", AppStatusSeverity.Info);

            List<VerdataMulMerger.VerdataPatch>? patches = null;
            await Task.Run(() => patches = VerdataMulMerger.ReadPatches(verdataPath), cts.Token);

            if (patches is null || patches.Count == 0)
            {
                AppStatus.SetWarning("No patches found in verdata.mul.");
                return;
            }

            _breakdown.Add($"Read {patches.Count} patches from verdata.mul");

            // Backup target files before patching.
            if (doBackup)
            {
                AppStatus.AppendLog("Creating backups...", AppStatusSeverity.Info);
                var targetFiles = patches.Select(p => VerdataMulMerger.FileNameForId(p.FileId))
                    .Where(n => n is not null)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(n => Path.Combine(mulFolder, n!))
                    .Where(File.Exists)
                    .ToArray();

                BackupManager.BackupFiles(targetFiles);
                _breakdown.Add($"Backed up {targetFiles.Length} files.");
                AppStatus.AppendLog($"Backed up {targetFiles.Length} files.", AppStatusSeverity.Info);
            }

            // Apply patches.
            AppStatus.SetInfo("Merging patches into MUL files...");
            var progress = CreateAppProgress();

            VerdataMulMerger.MergeResult? result = null;
            await Task.Run(() => result = VerdataMulMerger.Merge(patches, mulFolder, progress, cts.Token), cts.Token);

            if (result is null) return;

            // Display results.
            _breakdown.Add("");
            _breakdown.Add("Merge results:");
            foreach (var (file, count) in result.PatchesPerFile.OrderByDescending(kv => kv.Value))
                _breakdown.Add($"  {file,-20} {count,5} patches applied");

            _breakdown.Add("");
            _breakdown.Add($"Applied: {result.Applied} / {result.TotalPatches}");
            if (result.Skipped > 0)
                _breakdown.Add($"Skipped: {result.Skipped}");

            foreach (var warning in result.Warnings)
            {
                _warnings.Add(warning);
                AppStatus.AppendLog(warning, AppStatusSeverity.Warning);
            }

            // Optionally delete verdata.mul.
            if (deleteVerdata && result.Applied > 0)
            {
                var verdataInMul = Path.Combine(mulFolder, "verdata.mul");
                if (File.Exists(verdataInMul))
                {
                    File.Delete(verdataInMul);
                    _breakdown.Add("Deleted verdata.mul from MUL folder.");
                    AppStatus.AppendLog("Deleted verdata.mul from MUL folder.", AppStatusSeverity.Info);
                }
            }

            AppStatus.SetSuccess($"Merge complete: {result.Applied} patches applied to {result.PatchesPerFile.Count} files.");
            AppStatus.AppendLog($"Merge complete: {result.Applied} applied, {result.Skipped} skipped.", AppStatusSeverity.Info);
        }
        catch (OperationCanceledException)
        {
            AppStatus.SetWarning("Merge cancelled.");
        }
        catch (Exception ex)
        {
            AppStatus.SetError($"Merge failed: {ex.Message}");
            _warnings.Add(ex.Message);
            AppStatus.AppendLog($"Error: {ex.Message}", AppStatusSeverity.Error);
        }
        finally
        {
            SetBusy(false);
            AppStatus.SetProgress(0, false);
        }
    }

    // ------------------------------------------------------------------ //
    //  Validation and state
    // ------------------------------------------------------------------ //

    private bool ValidateInputs()
    {
        var verdataPath = VerdataPathBox.Text?.Trim();
        var mulFolder = MulFolderBox.Text?.Trim();

        if (string.IsNullOrWhiteSpace(verdataPath) || !File.Exists(verdataPath))
        {
            AppStatus.SetError("Please select a valid verdata.mul file.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(mulFolder) || !Directory.Exists(mulFolder))
        {
            AppStatus.SetError("Please select a valid MUL folder.");
            return false;
        }

        return true;
    }

    private void UpdateFieldStates()
    {
        if (_loadingState) return;

        var verdataPath = VerdataPathBox.Text?.Trim() ?? "";
        var mulFolder = MulFolderBox.Text?.Trim() ?? "";

        var verdataOk = File.Exists(verdataPath);
        SetFieldState(VerdataPathBox, verdataOk ? FieldState.Valid : string.IsNullOrEmpty(verdataPath) ? FieldState.Neutral : FieldState.Error);

        if (verdataOk)
            VerdataInfoText.Text = FormatFileSize(verdataPath);
        else
            VerdataInfoText.Text = "";

        var mulOk = Directory.Exists(mulFolder);
        SetFieldState(MulFolderBox, mulOk ? FieldState.Valid : string.IsNullOrEmpty(mulFolder) ? FieldState.Neutral : FieldState.Error);

        if (mulOk)
        {
            var mulCount = Directory.EnumerateFiles(mulFolder, "*.mul").Count();
            MulFolderInfoText.Text = $"{mulCount} .mul files";
        }
        else
        {
            MulFolderInfoText.Text = "";
        }

        var canRun = verdataOk && mulOk && !_busy;
        AnalyzeButton.IsEnabled = canRun;
        MergeButton.IsEnabled = canRun;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        AnalyzeButton.IsEnabled = !busy;
        MergeButton.IsEnabled = !busy;
    }

    // ------------------------------------------------------------------ //
    //  State persistence
    // ------------------------------------------------------------------ //

    private void LoadState()
    {
        _loadingState = true;
        try
        {
            VerdataPathBox.Text = AppSettings.GetString("VerdataMerger.VerdataPath");
            MulFolderBox.Text = AppSettings.GetString("VerdataMerger.MulFolder");
            BackupCheckBox.IsChecked = AppSettings.GetBool("VerdataMerger.Backup", true);
            DeleteVerdataCheckBox.IsChecked = AppSettings.GetBool("VerdataMerger.DeleteVerdata");
        }
        finally
        {
            _loadingState = false;
        }
    }

    public void PersistState() => SaveState();

    private void SaveState()
    {
        if (_loadingState) return;
        AppSettings.SetString("VerdataMerger.VerdataPath", VerdataPathBox.Text ?? "");
        AppSettings.SetString("VerdataMerger.MulFolder", MulFolderBox.Text ?? "");
        AppSettings.SetBool("VerdataMerger.Backup", BackupCheckBox.IsChecked == true);
        AppSettings.SetBool("VerdataMerger.DeleteVerdata", DeleteVerdataCheckBox.IsChecked == true);
    }
}
